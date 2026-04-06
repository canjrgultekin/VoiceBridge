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

    // Dual stream: TR ve EN için ayrı WebSocket bağlantıları
    private ClientWebSocket? _wsPrimary;
    private ClientWebSocket? _wsSecondary;
    private CancellationTokenSource? _receiveCts;
    private Task? _receivePrimaryTask;
    private Task? _receiveSecondaryTask;
    private SessionState _currentState = SessionState.Idle;

    private string _primaryLanguage = "tr";
    private string _secondaryLanguage = "en";
    private bool _dualMode;

    public event EventHandler<TranscriptReceivedEventArgs>? TranscriptReceived;
    public event EventHandler<SpeechRecognitionErrorEventArgs>? RecognitionError;
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public SessionState CurrentState => _currentState;

    public DeepgramStreamingService(
        IOptions<VoiceBridgeOptions> options,
        ILogger<DeepgramStreamingService> logger)
    {
        _options = options.Value.Deepgram;
        _logger = logger;

        // Dil konfigürasyonunu belirle
        if (_options.Language == "tr-en" || _options.Language == "dual")
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

        try
        {
            _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Primary stream (TR veya tek dil)
            _wsPrimary = await CreateAndConnectWebSocket(_primaryLanguage, ct);
            _receivePrimaryTask = ReceiveLoopAsync(_wsPrimary, _primaryLanguage, _receiveCts.Token);

            _logger.LogInformation("Primary stream bağlandı: {Lang}", _primaryLanguage);

            // Dual modda secondary stream (EN)
            if (_dualMode)
            {
                try
                {
                    _wsSecondary = await CreateAndConnectWebSocket(_secondaryLanguage, ct);
                    _receiveSecondaryTask = ReceiveLoopAsync(_wsSecondary, _secondaryLanguage, _receiveCts.Token);
                    _logger.LogInformation("Secondary stream bağlandı: {Lang}", _secondaryLanguage);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Secondary stream ({Lang}) bağlanamadı, sadece primary ile devam ediliyor", _secondaryLanguage);
                    _wsSecondary = null;
                }
            }

            SetState(SessionState.Listening);
            _logger.LogInformation("Deepgram bağlantısı kuruldu (DualMode: {Dual})", _dualMode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deepgram bağlantı hatası");
            SetState(SessionState.Error);
            RecognitionError?.Invoke(this, new SpeechRecognitionErrorEventArgs
            {
                Message = $"Bağlantı hatası: {ex.Message}",
                Exception = ex
            });
            throw;
        }
    }

    private async Task<ClientWebSocket> CreateAndConnectWebSocket(string language, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Token {_options.ApiKey}");

        var uri = BuildConnectionUri(language);
        _logger.LogInformation("Deepgram'a bağlanılıyor [{Lang}]: {Uri}", language, uri);

        await ws.ConnectAsync(uri, ct);
        return ws;
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken ct = default)
    {
        // Her iki stream'e de aynı audio'yu gönder
        var tasks = new List<Task>(2);

        if (_wsPrimary?.State == WebSocketState.Open)
        {
            tasks.Add(SendToSocketAsync(_wsPrimary, audioData, ct));
        }

        if (_dualMode && _wsSecondary?.State == WebSocketState.Open)
        {
            tasks.Add(SendToSocketAsync(_wsSecondary, audioData, ct));
        }

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
        _receiveCts?.Cancel();

        await CloseWebSocket(_wsPrimary, ct);
        await CloseWebSocket(_wsSecondary, ct);

        var tasks = new List<Task>();
        if (_receivePrimaryTask is not null) tasks.Add(SafeAwait(_receivePrimaryTask));
        if (_receiveSecondaryTask is not null) tasks.Add(SafeAwait(_receiveSecondaryTask));
        if (tasks.Count > 0) await Task.WhenAll(tasks);

        _wsPrimary?.Dispose();
        _wsSecondary?.Dispose();
        _wsPrimary = null;
        _wsSecondary = null;

        SetState(SessionState.Disconnected);
    }

    private static async Task CloseWebSocket(ClientWebSocket? ws, CancellationToken ct)
    {
        if (ws is null) return;
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                var closeMsg = Encoding.UTF8.GetBytes("{\"type\":\"CloseStream\"}");
                await ws.SendAsync(closeMsg, WebSocketMessageType.Text, true, ct);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", cts.Token);
            }
        }
        catch { }
    }

    private static async Task SafeAwait(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, string language, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Deepgram [{Lang}] bağlantıyı kapattı", language);
                    break;
                }

                messageBuffer.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(
                        messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                    messageBuffer.SetLength(0);

                    ProcessMessage(json, language);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "WebSocket [{Lang}] alım hatası", language);
        }
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

        // Düşük confidence'lı sonuçları filtrele (yanlış dil algılama önlemi)
        // Dual modda her iki stream de aynı sesi dinliyor,
        // yanlış dildeki stream düşük confidence verecek
        if (_dualMode && confidence < 0.3)
            return;

        // Dil: stream'in dilinden al
        var detectedLang = streamLanguage switch
        {
            "tr" => DetectedLanguage.Turkish,
            "en" => DetectedLanguage.English,
            _ => DetectedLanguage.Unknown
        };

        // Speaker diarization ve timing
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
