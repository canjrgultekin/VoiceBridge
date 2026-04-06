using Microsoft.Extensions.DependencyInjection;
using VoiceBridge.Core.Interfaces;
using VoiceBridge.Platform.Windows.Audio;

namespace VoiceBridge.Platform.Windows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IAudioCaptureService, WasapiAudioCaptureService>();
        return services;
    }
}
