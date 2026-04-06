using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoiceBridge.Core.Configuration;
using VoiceBridge.Core.Events;
using VoiceBridge.Core.Interfaces;
using VoiceBridge.Core.Models;

namespace VoiceBridge.Core.Services;

public sealed class DeepgramStreamingService : ISpeechRecognitionService
{
    private readonly DeepgramOptions _options;
    private readonly ILogger<DeepgramStreamingService> _logger;

    private const int MaxReconnectAttempts = 5;
    private static readonly int[] ReconnectDelaysMs = [1000, 2000, 4000, 8000, 16000];

    private StreamContext? _primary;
    private StreamContext? _secondary;
    private CancellationTokenSource? _sessionCts;

    private SessionState _currentState = SessionState.Idle;
    private bool _autoReconnect;
    private bool _dualMode;
    private string _primaryLanguage = "tr";
    private string _secondaryLanguage = "en";

    public event EventHandler<TranscriptReceivedEventArgs>? TranscriptReceived;
    public event EventHandler<SpeechRecognitionErrorEventArgs>? RecognitionError;
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public SessionState CurrentState => _currentState;

    private sealed class StreamContext
    {
        public required string Language { get; init; }
        public ClientWebSocket? Socket { get; set; }
        public Task? ReceiveTask { get; set; }
        public int ReconnectAttempt { get; set; }
    }

    public DeepgramStreamingService(
        IOptions<VoiceBridgeOptions> options,
        ILogger<DeepgramStreamingService> logger)
    {
        _options = options.Value.Deepgram;
        _logger = logger;

        if (_options.Language is "tr-en" or "dual")
        {
            _dualMode = true;
            _primaryLanguage = "tr";
            _secondaryLanguage = "en";
        }
        else if (_options.Language == "en-tr")
        {
            _dualMode = true;
            _primaryLanguage = "en";
            _secondaryLanguage = "tr";
        }
        else
        {
            _dualMode = false;
            _primaryLanguage = _options.Language;
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_currentState == SessionState.Listening)
            return;

        SetState(SessionState.Connecting);
        _autoReconnect = true;
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            _primary = new StreamContext { Language = _primaryLanguage };
            await ConnectStreamAsync(_primary, _sessionCts.Token);

            if (_dualMode)
            {
                _secondary = new StreamContext { Language = _secondaryLanguage };
                try
                {
                    await ConnectStreamAsync(_secondary, _sessionCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Secondary stream ({Lang}) başlatılamadı, primary ile devam", _secondaryLanguage);
                    _secondary = null;
                }
            }

            SetState(SessionState.Listening);
            _logger.LogInformation("Deepgram bağlantısı kuruldu (DualMode: {Dual})", _dualMode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deepgram bağlantı hatası");
            _autoReconnect = false;
            SetState(SessionState.Error);
            RecognitionError?.Invoke(this, new SpeechRecognitionErrorEventArgs
            {
                Message = $"Bağlantı hatası: {ex.Message}",
                Exception = ex
            });
            throw;
        }
    }

    private async Task ConnectStreamAsync(StreamContext ctx, CancellationToken ct)
    {
        ctx.Socket?.Dispose();
        ctx.Socket = new ClientWebSocket();
        ctx.Socket.Options.SetRequestHeader("Authorization", $"Token {_options.ApiKey}");

        var uri = BuildConnectionUri(ctx.Language);
        _logger.LogInformation("Deepgram [{Lang}] bağlanıyor: {Uri}", ctx.Language, uri);

        await ctx.Socket.ConnectAsync(uri, ct);
        ctx.ReconnectAttempt = 0;
        ctx.ReceiveTask = ReceiveLoopAsync(ctx, ct);
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken ct = default)
    {
        var tasks = new List<Task>(2);

        if (_primary?.Socket?.State == WebSocketState.Open)
            tasks.Add(SendToSocketAsync(_primary.Socket, audioData, ct));

        if (_dualMode && _secondary?.Socket?.State == WebSocketState.Open)
            tasks.Add(SendToSocketAsync(_secondary.Socket, audioData, ct));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    private static async Task SendToSocketAsync(ClientWebSocket ws, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            await ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
        }
        catch { /* Hata receive loop'ta yakalanacak */ }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _autoReconnect = false;
        _sessionCts?.Cancel();

        await CloseStreamAsync(_primary, ct);
        await CloseStreamAsync(_secondary, ct);

        var tasks = new List<Task>();
        if (_primary?.ReceiveTask is not null) tasks.Add(SafeAwait(_primary.ReceiveTask));
        if (_secondary?.ReceiveTask is not null) tasks.Add(SafeAwait(_secondary.ReceiveTask));
        if (tasks.Count > 0) await Task.WhenAll(tasks);

        _primary?.Socket?.Dispose();
        _secondary?.Socket?.Dispose();
        _primary = null;
        _secondary = null;

        SetState(SessionState.Disconnected);
    }

    private static async Task CloseStreamAsync(StreamContext? ctx, CancellationToken ct)
    {
        if (ctx?.Socket is null) return;
        try
        {
            if (ctx.Socket.State == WebSocketState.Open)
            {
                var closeMsg = Encoding.UTF8.GetBytes("{\"type\":\"CloseStream\"}");
                await ctx.Socket.SendAsync(closeMsg, WebSocketMessageType.Text, true, ct);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ctx.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", cts.Token);
            }
        }
        catch { /* Zorla kapatılacak */ }
    }

    private static async Task SafeAwait(Task task)
    {
        try { await task; } catch (OperationCanceledException) { } catch { }
    }

    private async Task ReceiveLoopAsync(StreamContext ctx, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream();
        var cleanClose = false;

        try
        {
            while (!ct.IsCancellationRequested && ctx.Socket?.State == WebSocketState.Open)
            {
                var result = await ctx.Socket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Deepgram [{Lang}] bağlantıyı kapattı (Code: {Code})",
                        ctx.Language, result.CloseStatus);
                    cleanClose = result.CloseStatus == WebSocketCloseStatus.NormalClosure;
                    break;
                }

                messageBuffer.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(
                        messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                    messageBuffer.SetLength(0);
                    ProcessMessage(json, ctx.Language);
                }
            }
        }
        catch (OperationCanceledException)
        {
            cleanClose = true;
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket [{Lang}] alım hatası", ctx.Language);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive loop [{Lang}] beklenmeyen hata", ctx.Language);
        }

        // Otomatik reconnect: user Disconnect çağırmadıysa ve session iptal edilmediyse
        if (_autoReconnect && !cleanClose && !ct.IsCancellationRequested)
        {
            _ = Task.Run(() => TryReconnectAsync(ctx, ct), CancellationToken.None);
        }
    }

    private async Task TryReconnectAsync(StreamContext ctx, CancellationToken ct)
    {
        while (_autoReconnect && !ct.IsCancellationRequested && ctx.ReconnectAttempt < MaxReconnectAttempts)
        {
            var delayMs = ReconnectDelaysMs[Math.Min(ctx.ReconnectAttempt, ReconnectDelaysMs.Length - 1)];
            ctx.ReconnectAttempt++;

            _logger.LogWarning("Deepgram [{Lang}] yeniden bağlanılıyor (deneme {Attempt}/{Max}, {Delay}ms sonra)",
                ctx.Language, ctx.ReconnectAttempt, MaxReconnectAttempts, delayMs);

            if (_currentState != SessionState.Reconnecting)
                SetState(SessionState.Reconnecting);

            try
            {
                await Task.Delay(delayMs, ct);
            }
            catch (OperationCanceledException) { return; }

            try
            {
                await ConnectStreamAsync(ctx, ct);
                _logger.LogInformation("Deepgram [{Lang}] yeniden bağlandı", ctx.Language);

                // Diğer stream de sağlamsa Listening durumuna dön
                if (AnyStreamOpen())
                    SetState(SessionState.Listening);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deepgram [{Lang}] reconnect denemesi başarısız", ctx.Language);
            }
        }

        _logger.LogError("Deepgram [{Lang}] maksimum reconnect denemesine ulaşıldı", ctx.Language);

        // Her iki stream de başarısız olduysa Error durumu
        if (!AnyStreamOpen())
        {
            SetState(SessionState.Error);
            RecognitionError?.Invoke(this, new SpeechRecognitionErrorEventArgs
            {
                Message = $"Deepgram bağlantısı yeniden kurulamadı ({MaxReconnectAttempts} deneme)"
            });
        }
    }

    private bool AnyStreamOpen()
    {
        return (_primary?.Socket?.State == WebSocketState.Open) ||
               (_dualMode && _secondary?.Socket?.State == WebSocketState.Open);
    }

    private void ProcessMessage(string json, string language)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            if (type == "Results")
                ProcessTranscriptResult(root, language);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deepgram [{Lang}] mesaj işleme hatası", language);
        }
    }

    private void ProcessTranscriptResult(JsonElement root, string streamLanguage)
    {
        var channel = root.GetProperty("channel");
        var alternatives = channel.GetProperty("alternatives");

        if (alternatives.GetArrayLength() == 0) return;

        var best = alternatives[0];
        var transcript = best.GetProperty("transcript").GetString();

        if (string.IsNullOrWhiteSpace(transcript)) return;

        var isFinal = root.GetProperty("is_final").GetBoolean();
        var confidence = best.GetProperty("confidence").GetDouble();

        // Dual modda düşük confidence = yanlış dildeki stream, filtrele
        if (_dualMode && confidence < 0.3)
            return;

        var detectedLang = streamLanguage switch
        {
            "tr" => DetectedLanguage.Turkish,
            "en" => DetectedLanguage.English,
            _ => DetectedLanguage.Unknown
        };

        var speakerIndex = 0;
        var words = best.GetProperty("words");
        double startTime = 0, endTime = 0;

        if (words.GetArrayLength() > 0)
        {
            var firstWord = words[0];
            var lastWord = words[words.GetArrayLength() - 1];

            startTime = firstWord.GetProperty("start").GetDouble();
            endTime = lastWord.GetProperty("end").GetDouble();

            if (firstWord.TryGetProperty("speaker", out var speakerProp))
                speakerIndex = speakerProp.GetInt32();
        }

        var entry = new TranscriptEntry
        {
            SpeakerIndex = speakerIndex,
            Language = detectedLang,
            OriginalText = transcript,
            IsInterim = !isFinal,
            IsTranslationPending = isFinal,
            Confidence = confidence,
            StartTime = startTime,
            EndTime = endTime
        };

        TranscriptReceived?.Invoke(this, new TranscriptReceivedEventArgs { Entry = entry });
    }

    private Uri BuildConnectionUri(string language)
    {
        var model = _options.Model;
        if (model.Contains(':')) model = "nova-3";
        model = model.Trim();

        var sb = new StringBuilder("wss://api.deepgram.com/v1/listen?");
        sb.Append($"model={Uri.EscapeDataString(model)}");
        sb.Append($"&language={Uri.EscapeDataString(language)}");
        sb.Append($"&encoding={_options.Encoding}");
        sb.Append($"&sample_rate={_options.SampleRate}");
        sb.Append($"&channels={_options.Channels}");

        if (_options.SmartFormat) sb.Append("&smart_format=true");
        if (_options.Punctuate) sb.Append("&punctuate=true");
        if (_options.InterimResults) sb.Append("&interim_results=true");
        if (_options.Diarize) sb.Append("&diarize=true");

        if (_options.UtteranceEnd)
            sb.Append($"&utterance_end_ms={_options.UtteranceEndMs}");

        return new Uri(sb.ToString());
    }

    private void SetState(SessionState newState)
    {
        var old = _currentState;
        if (old == newState) return;
        _currentState = newState;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs
        {
            OldState = old,
            NewState = newState
        });
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
