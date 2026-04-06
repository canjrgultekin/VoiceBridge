using VoiceBridge.Core.Events;

namespace VoiceBridge.Core.Interfaces;

public interface IAudioCaptureService : IAsyncDisposable
{
    event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    event EventHandler<AudioCaptureErrorEventArgs>? CaptureError;

    IReadOnlyList<AudioDeviceInfo> GetAvailableDevices();
    Task StartCaptureAsync(AudioCaptureRequest request, CancellationToken ct = default);
    Task StopCaptureAsync(CancellationToken ct = default);
    bool IsCapturing { get; }
}

public sealed record AudioCaptureRequest(
    string? DeviceId,
    Models.AudioSourceType SourceType,
    int SampleRate = 16000,
    int BitsPerSample = 16,
    int Channels = 1
);

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    bool IsDefault,
    Models.AudioSourceType SourceType
);
