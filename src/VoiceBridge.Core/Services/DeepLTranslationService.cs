using DeepL;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoiceBridge.Core.Configuration;
using VoiceBridge.Core.Interfaces;
using VoiceBridge.Core.Models;

namespace VoiceBridge.Core.Services;

public sealed class DeepLTranslationService : ITranslationService
{
    private readonly Translator _translator;
    private readonly DeepLOptions _options;
    private readonly ILogger<DeepLTranslationService> _logger;

    public DeepLTranslationService(
        IOptions<VoiceBridgeOptions> options,
        ILogger<DeepLTranslationService> logger)
    {
        _options = options.Value.DeepL;
        _logger = logger;
        _translator = new Translator(_options.ApiKey);
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        DetectedLanguage sourceLanguage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TranslationResult("", text, "", sourceLanguage, DetectedLanguage.Unknown, false, "Boş metin");

        var (sourceLang, targetLang, targetLanguage) = ResolveLanguagePair(sourceLanguage);

        try
        {
            var options = new TextTranslateOptions
            {
                ModelType = _options.ModelType == "quality_optimized"
                    ? ModelType.QualityOptimized
                    : ModelType.LatencyOptimized
            };

            if (_options.Formality != "default")
            {
                options.Formality = _options.Formality switch
                {
                    "more" => Formality.More,
                    "less" => Formality.Less,
                    "prefer_more" => Formality.PreferMore,
                    "prefer_less" => Formality.PreferLess,
                    _ => Formality.Default
                };
            }

            var result = await _translator.TranslateTextAsync(
                text, sourceLang, targetLang, options, ct);

            _logger.LogDebug(
                "Çeviri tamamlandı: [{Source}] {Original} -> [{Target}] {Translated}",
                sourceLanguage, text[..Math.Min(50, text.Length)],
                targetLanguage, result.Text[..Math.Min(50, result.Text.Length)]);

            return new TranslationResult(
                "", text, result.Text, sourceLanguage, targetLanguage, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Çeviri hatası: {Text}", text[..Math.Min(50, text.Length)]);
            return new TranslationResult(
                "", text, "", sourceLanguage, targetLanguage, false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests,
        CancellationToken ct = default)
    {
        if (requests.Count == 0)
            return [];

        // Dil çiftine göre grupla (DeepL batch'te aynı dil çifti olmalı)
        var results = new List<TranslationResult>(requests.Count);

        var grouped = requests.GroupBy(r => r.SourceLanguage);

        foreach (var group in grouped)
        {
            var items = group.ToList();
            var (sourceLang, targetLang, targetLanguage) = ResolveLanguagePair(group.Key);

            try
            {
                var texts = items.Select(i => i.Text).ToArray();
                var options = new TextTranslateOptions
                {
                    ModelType = _options.ModelType == "quality_optimized"
                        ? ModelType.QualityOptimized
                        : ModelType.LatencyOptimized
                };

                var translations = await _translator.TranslateTextAsync(
                    texts, sourceLang, targetLang, options, ct);

                for (int i = 0; i < items.Count; i++)
                {
                    results.Add(new TranslationResult(
                        items[i].Id,
                        items[i].Text,
                        translations[i].Text,
                        group.Key,
                        targetLanguage,
                        true));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch çeviri hatası, {Count} öğe", items.Count);
                foreach (var item in items)
                {
                    results.Add(new TranslationResult(
                        item.Id, item.Text, "", group.Key, targetLanguage, false, ex.Message));
                }
            }
        }

        return results;
    }

    private static (string? sourceLang, string targetLang, DetectedLanguage targetLanguage) ResolveLanguagePair(
        DetectedLanguage source)
    {
        return source switch
        {
            DetectedLanguage.Turkish => ("tr", "en-US", DetectedLanguage.English),
            DetectedLanguage.English => ("en", "tr", DetectedLanguage.Turkish),
            // Bilinmeyen dil ise Türkçe kabul et, İngilizceye çevir
            _ => (null, "en-US", DetectedLanguage.English)
        };
    }
}
