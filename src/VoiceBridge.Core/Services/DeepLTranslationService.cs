using DeepL;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoiceBridge.Core.Configuration;
using VoiceBridge.Core.Interfaces;
using VoiceBridge.Core.Models;

namespace VoiceBridge.Core.Services;

public sealed class DeepLTranslationService : ITranslationService, IDisposable
{
    private readonly Translator _translator;
    private readonly DeepLOptions _options;
    private readonly ILogger<DeepLTranslationService> _logger;

    private const int MaxRetries = 3;
    private static readonly int[] RetryDelaysMs = [300, 800, 2000];

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
        var options = BuildOptions();

        Exception? lastException = null;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delay = RetryDelaysMs[Math.Min(attempt - 1, RetryDelaysMs.Length - 1)];
                _logger.LogDebug("DeepL çeviri yeniden deneniyor (deneme {Attempt}/{Max}, {Delay}ms)",
                    attempt, MaxRetries, delay);
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException)
                {
                    return new TranslationResult("", text, "", sourceLanguage, targetLanguage, false, "İptal edildi");
                }
            }

            try
            {
                var result = await _translator.TranslateTextAsync(
                    text, sourceLang, targetLang, options, ct);

                return new TranslationResult(
                    "", text, result.Text, sourceLanguage, targetLanguage, true);
            }
            catch (AuthorizationException ex)
            {
                // Kalıcı hata, retry anlamsız
                _logger.LogError(ex, "DeepL API key yetkilendirme hatası");
                return new TranslationResult("", text, "", sourceLanguage, targetLanguage, false,
                    "API key geçersiz");
            }
            catch (QuotaExceededException ex)
            {
                // Kalıcı hata, retry anlamsız
                _logger.LogError(ex, "DeepL kota aşıldı");
                return new TranslationResult("", text, "", sourceLanguage, targetLanguage, false,
                    "Aylık karakter kotası aşıldı");
            }
            catch (TooManyRequestsException ex)
            {
                // Geçici, retry et
                lastException = ex;
                _logger.LogWarning("DeepL rate limit, retry yapılacak");
            }
            catch (ConnectionException ex)
            {
                // Geçici, retry et
                lastException = ex;
                _logger.LogWarning("DeepL bağlantı hatası, retry yapılacak");
            }
            catch (OperationCanceledException)
            {
                return new TranslationResult("", text, "", sourceLanguage, targetLanguage, false, "İptal edildi");
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "DeepL çeviri hatası, retry yapılacak");
            }
        }

        _logger.LogError(lastException, "DeepL çeviri {Max} deneme sonunda başarısız oldu", MaxRetries);
        return new TranslationResult("", text, "", sourceLanguage, targetLanguage, false,
            lastException?.Message ?? "Bilinmeyen hata");
    }

    public async Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests,
        CancellationToken ct = default)
    {
        if (requests.Count == 0)
            return [];

        var results = new List<TranslationResult>(requests.Count);
        var grouped = requests.GroupBy(r => r.SourceLanguage);

        foreach (var group in grouped)
        {
            var items = group.ToList();
            var (sourceLang, targetLang, targetLanguage) = ResolveLanguagePair(group.Key);
            var options = BuildOptions();
            var texts = items.Select(i => i.Text).ToArray();

            Exception? lastException = null;
            bool succeeded = false;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    var delay = RetryDelaysMs[Math.Min(attempt - 1, RetryDelaysMs.Length - 1)];
                    try { await Task.Delay(delay, ct); }
                    catch (OperationCanceledException) { break; }
                }

                try
                {
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

                    succeeded = true;
                    break;
                }
                catch (AuthorizationException ex)
                {
                    _logger.LogError(ex, "DeepL API key yetkilendirme hatası");
                    lastException = ex;
                    break;
                }
                catch (QuotaExceededException ex)
                {
                    _logger.LogError(ex, "DeepL kota aşıldı");
                    lastException = ex;
                    break;
                }
                catch (OperationCanceledException)
                {
                    lastException = new OperationCanceledException("İptal edildi");
                    break;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "DeepL batch çeviri denemesi {Attempt} başarısız", attempt + 1);
                }
            }

            if (!succeeded)
            {
                _logger.LogError(lastException, "DeepL batch çeviri {Count} öğe için başarısız", items.Count);
                foreach (var item in items)
                {
                    results.Add(new TranslationResult(
                        item.Id, item.Text, "", group.Key, targetLanguage, false,
                        lastException?.Message ?? "Bilinmeyen hata"));
                }
            }
        }

        return results;
    }

    private TextTranslateOptions BuildOptions()
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

        return options;
    }

    private static (string? sourceLang, string targetLang, DetectedLanguage targetLanguage) ResolveLanguagePair(
        DetectedLanguage source)
    {
        return source switch
        {
            DetectedLanguage.Turkish => ("tr", "en-US", DetectedLanguage.English),
            DetectedLanguage.English => ("en", "tr", DetectedLanguage.Turkish),
            _ => (null, "en-US", DetectedLanguage.English)
        };
    }

    public void Dispose()
    {
        _translator.Dispose();
    }
}
