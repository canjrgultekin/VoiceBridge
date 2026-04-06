namespace VoiceBridge.Core.Models;

public sealed class TranscriptEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public int SpeakerIndex { get; init; }
    public string SpeakerLabel => $"Speaker {SpeakerIndex + 1}";
    public DetectedLanguage Language { get; init; }
    public string OriginalText { get; init; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public bool IsInterim { get; init; }
    public bool IsTranslationPending { get; set; }
    public double Confidence { get; init; }
    public double StartTime { get; init; }
    public double EndTime { get; init; }
}
