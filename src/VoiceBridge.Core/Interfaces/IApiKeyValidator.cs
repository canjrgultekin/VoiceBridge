using VoiceBridge.Core.Models;

namespace VoiceBridge.Core.Interfaces;

public interface IApiKeyValidator
{
    Task<ApiKeyValidationResult> ValidateAsync(CancellationToken ct = default);
}
