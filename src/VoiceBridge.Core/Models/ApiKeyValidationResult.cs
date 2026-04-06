namespace VoiceBridge.Core.Models;

public sealed record ApiKeyValidationResult
{
    public bool DeepgramValid { get; init; }
    public string? DeepgramError { get; init; }
    public bool DeepLValid { get; init; }
    public string? DeepLError { get; init; }
    public long DeepLRemainingCharacters { get; init; }
    public long DeepLCharacterLimit { get; init; }

    public bool AllValid => DeepgramValid && DeepLValid;

    public string BuildStatusMessage()
    {
        if (AllValid)
        {
            if (DeepLCharacterLimit > 0)
            {
                var usedPercent = 100.0 * (DeepLCharacterLimit - DeepLRemainingCharacters) / DeepLCharacterLimit;
                return $"API key'ler doğrulandı — DeepL: {DeepLRemainingCharacters:N0}/{DeepLCharacterLimit:N0} karakter kaldı ({usedPercent:F0}% kullanıldı)";
            }
            return "API key'ler doğrulandı";
        }

        var errors = new List<string>();
        if (!DeepgramValid) errors.Add($"Deepgram: {DeepgramError}");
        if (!DeepLValid) errors.Add($"DeepL: {DeepLError}");
        return $"API key doğrulama başarısız — {string.Join(" | ", errors)}";
    }
}
