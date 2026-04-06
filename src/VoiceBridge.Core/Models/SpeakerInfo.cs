namespace VoiceBridge.Core.Models;

public sealed class SpeakerInfo
{
    public int Index { get; init; }
    public string Label => $"Speaker {Index + 1}";
    public string? CustomName { get; set; }
    public string DisplayName => CustomName ?? Label;
    public DetectedLanguage PrimaryLanguage { get; set; } = DetectedLanguage.Unknown;
    public int UtteranceCount { get; set; }
    public TimeSpan TotalSpeakTime { get; set; }
}
