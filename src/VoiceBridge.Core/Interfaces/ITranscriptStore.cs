using VoiceBridge.Core.Models;

namespace VoiceBridge.Core.Interfaces;

public interface ITranscriptStore
{
    IReadOnlyList<TranscriptEntry> Entries { get; }
    IReadOnlyDictionary<int, SpeakerInfo> Speakers { get; }

    void AddOrUpdateEntry(TranscriptEntry entry);
    void UpdateTranslation(string entryId, string translatedText);
    void Clear();

    Task ExportAsTextAsync(string filePath, CancellationToken ct = default);
    Task ExportAsSrtAsync(string filePath, CancellationToken ct = default);
    Task ExportAsJsonAsync(string filePath, CancellationToken ct = default);
}
