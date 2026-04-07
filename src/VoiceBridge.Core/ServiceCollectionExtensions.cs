using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VoiceBridge.Core.Configuration;
using VoiceBridge.Core.Interfaces;
using VoiceBridge.Core.Services;

namespace VoiceBridge.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVoiceBridgeCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<VoiceBridgeOptions>(
            configuration.GetSection(VoiceBridgeOptions.SectionName));

        services.AddSingleton<ISpeechRecognitionService, DeepgramStreamingService>();
        services.AddSingleton<ITranslationService, DeepLTranslationService>();
        services.AddSingleton<IApiKeyValidator, ApiKeyValidator>();
        services.AddSingleton<IVoiceActivityDetector, SileroVadDetector>();
        services.AddSingleton<TranscriptManager>();
        services.AddSingleton<ITranscriptStore>(sp => sp.GetRequiredService<TranscriptManager>());

        return services;
    }
}
