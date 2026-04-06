using VoiceBridge.Core.Models;

namespace VoiceBridge.Core.Interfaces;

public interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(
        string text,
        DetectedLanguage sourceLanguage,
        CancellationToken ct = default);

    Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests,
        CancellationToken ct = default);
}

public sealed record TranslationRequest(
    string Id,
    string Text,
    DetectedLanguage SourceLanguage
);

public sealed record TranslationResult(
    string Id,
    string OriginalText,
    string TranslatedText,
    DetectedLanguage SourceLanguage,
    DetectedLanguage TargetLanguage,
    bool Success,
    string? ErrorMessage = null
);
