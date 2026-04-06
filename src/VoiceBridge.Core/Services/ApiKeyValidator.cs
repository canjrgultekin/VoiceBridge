using System.Net.Http.Headers;
using DeepL;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoiceBridge.Core.Configuration;
using VoiceBridge.Core.Interfaces;
using VoiceBridge.Core.Models;

namespace VoiceBridge.Core.Services;

public sealed class ApiKeyValidator : IApiKeyValidator, IDisposable
{
    private readonly VoiceBridgeOptions _options;
    private readonly ILogger<ApiKeyValidator> _logger;
    private readonly HttpClient _http;

    public ApiKeyValidator(
        IOptions<VoiceBridgeOptions> options,
        ILogger<ApiKeyValidator> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<ApiKeyValidationResult> ValidateAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("API key doğrulaması başlatılıyor");

        var deepgramTask = ValidateDeepgramAsync(ct);
        var deepLTask = ValidateDeepLAsync(ct);

        await Task.WhenAll(deepgramTask, deepLTask);

        var deepgram = await deepgramTask;
        var deepL = await deepLTask;

        var result = new ApiKeyValidationResult
        {
            DeepgramValid = deepgram.Valid,
            DeepgramError = deepgram.Error,
            DeepLValid = deepL.Valid,
            DeepLError = deepL.Error,
            DeepLRemainingCharacters = deepL.Remaining,
            DeepLCharacterLimit = deepL.Limit
        };

        _logger.LogInformation("API key doğrulaması tamamlandı: {Status}", result.BuildStatusMessage());
        return result;
    }

    private async Task<(bool Valid, string? Error)> ValidateDeepgramAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Deepgram.ApiKey))
            return (false, "API key girilmemiş");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepgram.com/v1/projects");
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", _options.Deepgram.ApiKey);

            using var response = await _http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
                return (true, null);

            var reason = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "API key geçersiz",
                System.Net.HttpStatusCode.Forbidden => "API key yetkisiz",
                System.Net.HttpStatusCode.TooManyRequests => "Rate limit aşıldı",
                _ => $"{(int)response.StatusCode} {response.ReasonPhrase}"
            };

            return (false, reason);
        }
        catch (TaskCanceledException)
        {
            return (false, "Bağlantı zaman aşımı");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Ağ hatası: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deepgram doğrulama beklenmeyen hata");
            return (false, ex.Message);
        }
    }

    private async Task<(bool Valid, string? Error, long Remaining, long Limit)> ValidateDeepLAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.DeepL.ApiKey))
            return (false, "API key girilmemiş", 0, 0);

        Translator? translator = null;
        try
        {
            translator = new Translator(_options.DeepL.ApiKey);
            var usage = await translator.GetUsageAsync(ct);

            if (usage.Character is null)
                return (true, null, 0, 0);

            var limit = (long)usage.Character.Limit;
            var count = (long)usage.Character.Count;
            var remaining = Math.Max(0, limit - count);

            return (true, null, remaining, limit);
        }
        catch (DeepL.AuthorizationException)
        {
            return (false, "API key geçersiz", 0, 0);
        }
        catch (DeepL.QuotaExceededException)
        {
            return (false, "Karakter kotası aşıldı", 0, 0);
        }
        catch (DeepL.ConnectionException ex)
        {
            return (false, $"Ağ hatası: {ex.Message}", 0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeepL doğrulama beklenmeyen hata");
            return (false, ex.Message, 0, 0);
        }
        finally
        {
            translator?.Dispose();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
