using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private SessionState _currentState = SessionState.Idle;

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
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_currentState == SessionState.Listening)
            return;

        SetState(SessionState.Connecting);

        try
        {
            _ws?.Dispose();
            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("Authorization", $"Token {_options.ApiKey}");

            var uri = BuildConnectionUri();
            _logger.LogInformation("Deepgram'a bağlanılıyor: {Uri}", uri);

            await _ws.ConnectAsync(uri, ct);

            _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

            SetState(SessionState.Listening);
            _logger.LogInformation("Deepgram bağlantısı kuruldu");
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

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken ct = default)
    {
        if (_ws is null || _ws.State != WebSocketState.Open)
            return;

        try
        {
            await _ws.SendAsync(audioData, WebSocketMessageType.Binary, true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio gönderim hatası");
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_ws is null)
            return;

        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                var closeMsg = JsonSerializer.SerializeToUtf8Bytes(
                    new { type = "CloseStream" });
                await _ws.SendAsync(closeMsg, WebSocketMessageType.Text, true, ct);

                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", closeCts.Token);
                }
                catch (OperationCanceledException) { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deepgram kapanış hatası");
        }
        finally
        {
            _receiveCts?.Cancel();

            if (_receiveTask is not null)
            {
                try { await _receiveTask; }
                catch (OperationCanceledException) { }
            }

            _ws?.Dispose();
            _ws = null;
            SetState(SessionState.Disconnected);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Deepgram bağlantıyı kapattı");
                    break;
                }

                messageBuffer.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(
                        messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                    messageBuffer.SetLength(0);

                    ProcessMessage(json);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "WebSocket alım hatası");
            RecognitionError?.Invoke(this, new SpeechRecognitionErrorEventArgs
            {
                Message = $"WebSocket hatası: {ex.Message}",
                Exception = ex
            });
        }
        finally
        {
            if (_currentState == SessionState.Listening)
                SetState(SessionState.Disconnected);
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.GetProperty("type").GetString();

            if (type == "Results")
            {
                ProcessTranscriptResult(root);
            }
            else if (type == "Metadata")
            {
                _logger.LogDebug("Deepgram metadata alındı");
            }
            else if (type == "UtteranceEnd")
            {
                _logger.LogDebug("Utterance sonu algılandı");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deepgram mesaj işleme hatası: {Json}", json[..Math.Min(200, json.Length)]);
        }
    }

    private void ProcessTranscriptResult(JsonElement root)
    {
        var channel = root.GetProperty("channel");
        var alternatives = channel.GetProperty("alternatives");

        if (alternatives.GetArrayLength() == 0)
            return;

        var best = alternatives[0];
        var transcript = best.GetProperty("transcript").GetString();

        if (string.IsNullOrWhiteSpace(transcript))
            return;

        var isFinal = root.GetProperty("is_final").GetBoolean();
        var confidence = best.GetProperty("confidence").GetDouble();

        // Dil tespiti - channel seviyesinde
        var detectedLang = DetectedLanguage.Unknown;
        if (channel.TryGetProperty("detected_language", out var langProp))
        {
            var langCode = langProp.GetString();
            detectedLang = ParseLanguageCode(langCode);
        }

        // Speaker diarization
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

            // Kelime bazlı dil tespiti (channel seviyesinde yoksa)
            if (detectedLang == DetectedLanguage.Unknown &&
                firstWord.TryGetProperty("language", out var wordLangProp))
            {
                detectedLang = ParseLanguageCode(wordLangProp.GetString());
            }
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

    private static DetectedLanguage ParseLanguageCode(string? code) => code switch
    {
        "tr" => DetectedLanguage.Turkish,
        "en" => DetectedLanguage.English,
        _ => DetectedLanguage.Unknown
    };

    private Uri BuildConnectionUri()
    {
        var queryParams = new Dictionary<string, string>
        {
            ["model"] = _options.Model,
            ["language"] = _options.Language,
            ["sample_rate"] = _options.SampleRate.ToString(),
            ["encoding"] = _options.Encoding,
            ["channels"] = _options.Channels.ToString(),
            ["smart_format"] = _options.SmartFormat.ToString().ToLower(),
            ["punctuate"] = _options.Punctuate.ToString().ToLower(),
            ["diarize"] = _options.Diarize.ToString().ToLower(),
            ["interim_results"] = _options.InterimResults.ToString().ToLower()
        };

        // Multilingual code-switching için endpointing=100 öneriliyor
        if (_options.Language == "multi")
        {
            queryParams["endpointing"] = "100";
        }

        // utterance_end sadece streaming'de, endpointing ile birlikte
        if (_options.UtteranceEnd)
        {
            queryParams["utterance_end_ms"] = _options.UtteranceEndMs.ToString();
        }

        // NOT: detect_language streaming'de desteklenmiyor
        // language=multi zaten otomatik dil tespiti yapıyor

        var qs = string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return new Uri($"wss://api.deepgram.com/v1/listen?{qs}");
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
