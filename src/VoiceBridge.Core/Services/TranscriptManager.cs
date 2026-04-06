using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoiceBridge.Core.Configuration;
using VoiceBridge.Core.Events;
using VoiceBridge.Core.Interfaces;
using VoiceBridge.Core.Models;

namespace VoiceBridge.Core.Services;

public sealed class TranscriptManager : ITranscriptStore, IAsyncDisposable
{
    private readonly ISpeechRecognitionService _stt;
    private readonly IAudioCaptureService _audio;
    private readonly ITranslationService _translation;
    private readonly TranslationOptions _translationOptions;
    private readonly ILogger<TranscriptManager> _logger;

    private readonly List<TranscriptEntry> _entries = [];
    private readonly Dictionary<int, SpeakerInfo> _speakers = [];
    private readonly ConcurrentQueue<TranscriptEntry> _translationQueue = new();
    private readonly object _lock = new();

    private CancellationTokenSource? _sessionCts;
    private Task? _translationWorker;

    public event EventHandler<TranscriptReceivedEventArgs>? EntryAdded;
    public event EventHandler<TranslationCompletedEventArgs>? TranslationCompleted;
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public IReadOnlyList<TranscriptEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public IReadOnlyDictionary<int, SpeakerInfo> Speakers
    {
        get { lock (_lock) return new Dictionary<int, SpeakerInfo>(_speakers); }
    }

    public TranscriptManager(
        ISpeechRecognitionService stt,
        IAudioCaptureService audio,
        ITranslationService translation,
        IOptions<VoiceBridgeOptions> options,
        ILogger<TranscriptManager> logger)
    {
        _stt = stt;
        _audio = audio;
        _translation = translation;
        _translationOptions = options.Value.Translation;
        _logger = logger;

        _stt.TranscriptReceived += OnTranscriptReceived;
        _stt.RecognitionError += OnRecognitionError;
        _stt.StateChanged += OnStateChanged;
        _audio.AudioDataAvailable += OnAudioDataAvailable;
        _audio.CaptureError += OnCaptureError;
    }

    public async Task StartSessionAsync(
        AudioCaptureRequest captureRequest,
        CancellationToken ct = default)
    {
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Deepgram'a bağlan
        await _stt.ConnectAsync(_sessionCts.Token);

        // Çeviri worker'ı başlat
        _translationWorker = RunTranslationWorkerAsync(_sessionCts.Token);

        // Ses yakalamayı başlat
        await _audio.StartCaptureAsync(captureRequest, _sessionCts.Token);

        _logger.LogInformation("Session başlatıldı: {SourceType}", captureRequest.SourceType);
    }

    public async Task StopSessionAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Session durduruluyor...");

        await _audio.StopCaptureAsync(ct);
        await _stt.DisconnectAsync(ct);

        _sessionCts?.Cancel();

        if (_translationWorker is not null)
        {
            try { await _translationWorker; }
            catch (OperationCanceledException) { }
        }

        _logger.LogInformation("Session durduruldu");
    }

    private async void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        try
        {
            await _stt.SendAudioAsync(e.Buffer, _sessionCts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio veri gönderim hatası");
        }
    }

    private void OnTranscriptReceived(object? sender, TranscriptReceivedEventArgs e)
    {
        var entry = e.Entry;

        lock (_lock)
        {
            if (entry.IsInterim)
            {
                // Interim: son interim'i güncelle veya ekle
                var existing = _entries.FindLastIndex(x =>
                    x.IsInterim && x.SpeakerIndex == entry.SpeakerIndex);

                if (existing >= 0)
                    _entries[existing] = entry;
                else
                    _entries.Add(entry);
            }
            else
            {
                // Final: interim'leri kaldır, final ekle
                _entries.RemoveAll(x =>
                    x.IsInterim && x.SpeakerIndex == entry.SpeakerIndex);
                _entries.Add(entry);
            }

            // Speaker bilgisini güncelle
            if (!_speakers.TryGetValue(entry.SpeakerIndex, out var speaker))
            {
                speaker = new SpeakerInfo { Index = entry.SpeakerIndex };
                _speakers[entry.SpeakerIndex] = speaker;
            }

            speaker.UtteranceCount++;
            speaker.TotalSpeakTime += TimeSpan.FromSeconds(entry.EndTime - entry.StartTime);

            if (entry.Language != DetectedLanguage.Unknown)
                speaker.PrimaryLanguage = entry.Language;
        }

        EntryAdded?.Invoke(this, e);

        // Final transcript'i çeviri kuyruğuna ekle
        if (!entry.IsInterim && _translationOptions.AutoTranslate)
        {
            _translationQueue.Enqueue(entry);
        }
    }

    private async Task RunTranslationWorkerAsync(CancellationToken ct)
    {
        _logger.LogInformation("Çeviri worker başlatıldı");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_translationOptions.BatchDelayMs, ct);

                var batch = new List<TranscriptEntry>();
                while (batch.Count < _translationOptions.MaxBatchSize &&
                       _translationQueue.TryDequeue(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count == 0) continue;

                var requests = batch
                    .Select(e => new TranslationRequest(e.Id, e.OriginalText, e.Language))
                    .ToList();

                var results = await _translation.TranslateBatchAsync(requests, ct);

                foreach (var result in results)
                {
                    if (!result.Success) continue;

                    UpdateTranslation(result.Id, result.TranslatedText);

                    TranslationCompleted?.Invoke(this, new TranslationCompletedEventArgs
                    {
                        EntryId = result.Id,
                        TranslatedText = result.TranslatedText
                    });
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Çeviri worker hatası");
                await Task.Delay(1000, ct);
            }
        }

        _logger.LogInformation("Çeviri worker durduruldu");
    }

    private void OnRecognitionError(object? sender, SpeechRecognitionErrorEventArgs e)
    {
        _logger.LogError(e.Exception, "STT hatası: {Message}", e.Message);
    }

    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        _logger.LogInformation("STT durumu değişti: {Old} -> {New}", e.OldState, e.NewState);
        StateChanged?.Invoke(this, e);
    }

    private void OnCaptureError(object? sender, AudioCaptureErrorEventArgs e)
    {
        _logger.LogError(e.Exception, "Ses yakalama hatası: {Message}", e.Message);
    }

    public void AddOrUpdateEntry(TranscriptEntry entry)
    {
        lock (_lock)
        {
            var idx = _entries.FindIndex(x => x.Id == entry.Id);
            if (idx >= 0)
                _entries[idx] = entry;
            else
                _entries.Add(entry);
        }
    }

    public void UpdateTranslation(string entryId, string translatedText)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == entryId);
            if (entry is not null)
            {
                entry.TranslatedText = translatedText;
                entry.IsTranslationPending = false;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _speakers.Clear();
        }
    }

    public async Task ExportAsTextAsync(string filePath, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"VoiceBridge Transcript - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('=', 60));

        IReadOnlyList<TranscriptEntry> snapshot;
        lock (_lock) { snapshot = _entries.Where(e => !e.IsInterim).ToList(); }

        foreach (var entry in snapshot)
        {
            var langFlag = entry.Language == DetectedLanguage.Turkish ? "🇹🇷" : "🇬🇧";
            sb.AppendLine();
            sb.AppendLine($"[{entry.Timestamp:HH:mm:ss}] {entry.SpeakerLabel} {langFlag}");
            sb.AppendLine($"  {entry.OriginalText}");
            if (!string.IsNullOrEmpty(entry.TranslatedText))
                sb.AppendLine($"  → {entry.TranslatedText}");
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8, ct);
    }

    public async Task ExportAsSrtAsync(string filePath, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        IReadOnlyList<TranscriptEntry> snapshot;
        lock (_lock) { snapshot = _entries.Where(e => !e.IsInterim).ToList(); }

        for (int i = 0; i < snapshot.Count; i++)
        {
            var e = snapshot[i];
            sb.AppendLine($"{i + 1}");
            sb.AppendLine($"{FormatSrtTime(e.StartTime)} --> {FormatSrtTime(e.EndTime)}");
            sb.AppendLine($"[{e.SpeakerLabel}] {e.OriginalText}");
            if (!string.IsNullOrEmpty(e.TranslatedText))
                sb.AppendLine(e.TranslatedText);
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8, ct);
    }

    public async Task ExportAsJsonAsync(string filePath, CancellationToken ct = default)
    {
        IReadOnlyList<TranscriptEntry> snapshot;
        lock (_lock) { snapshot = _entries.Where(e => !e.IsInterim).ToList(); }

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, ct);
    }

    private static string FormatSrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }

    public async ValueTask DisposeAsync()
    {
        _stt.TranscriptReceived -= OnTranscriptReceived;
        _stt.RecognitionError -= OnRecognitionError;
        _stt.StateChanged -= OnStateChanged;
        _audio.AudioDataAvailable -= OnAudioDataAvailable;
        _audio.CaptureError -= OnCaptureError;

        _sessionCts?.Cancel();

        if (_translationWorker is not null)
        {
            try { await _translationWorker; }
            catch (OperationCanceledException) { }
        }
    }
}
