using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceBridge.Core.Events;
using VoiceBridge.Core.Interfaces;
using VoiceBridge.Core.Models;

namespace VoiceBridge.Platform.Windows.Audio;

public sealed class WasapiAudioCaptureService : IAudioCaptureService
{
    private readonly ILogger<WasapiAudioCaptureService> _logger;
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private WaveFormat? _targetFormat;
    private bool _isCapturing;

    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    public event EventHandler<AudioCaptureErrorEventArgs>? CaptureError;

    public bool IsCapturing => _isCapturing;

    public WasapiAudioCaptureService(ILogger<WasapiAudioCaptureService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<AudioDeviceInfo> GetAvailableDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            var enumerator = new MMDeviceEnumerator();

            var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            var defaultCapture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            foreach (var device in captureDevices)
            {
                devices.Add(new AudioDeviceInfo(
                    device.ID, device.FriendlyName,
                    device.ID == defaultCapture.ID, AudioSourceType.Microphone));
            }

            var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            foreach (var device in renderDevices)
            {
                devices.Add(new AudioDeviceInfo(
                    device.ID, $"[System] {device.FriendlyName}",
                    device.ID == defaultRender.ID, AudioSourceType.SystemAudio));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ses cihazları listelenirken hata");
        }

        return devices;
    }

    public Task StartCaptureAsync(AudioCaptureRequest request, CancellationToken ct = default)
    {
        if (_isCapturing)
            throw new InvalidOperationException("Ses yakalama zaten aktif");

        _targetFormat = new WaveFormat(request.SampleRate, request.BitsPerSample, request.Channels);

        try
        {
            if (request.SourceType is AudioSourceType.Microphone or AudioSourceType.Both)
                StartMicrophoneCapture(request.DeviceId);

            if (request.SourceType is AudioSourceType.SystemAudio or AudioSourceType.Both)
                StartLoopbackCapture();

            _isCapturing = true;
            _logger.LogInformation("Ses yakalama başlatıldı: {SourceType}", request.SourceType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ses yakalama başlatılamadı");
            CaptureError?.Invoke(this, new AudioCaptureErrorEventArgs
            {
                Message = $"Ses yakalama başlatılamadı: {ex.Message}",
                Exception = ex
            });
            throw;
        }

        return Task.CompletedTask;
    }

    private void StartMicrophoneCapture(string? deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(deviceId);

        _micCapture = new WasapiCapture(device)
        {
            WaveFormat = new WaveFormat(_targetFormat!.SampleRate, 16, 1)
        };

        _micCapture.DataAvailable += OnMicDataAvailable;
        _micCapture.RecordingStopped += OnRecordingStopped;
        _micCapture.StartRecording();
        _logger.LogInformation("Mikrofon yakalama başlatıldı: {Device}", device.FriendlyName);
    }

    private void StartLoopbackCapture()
    {
        _loopbackCapture = new WasapiLoopbackCapture();
        _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
        _loopbackCapture.RecordingStopped += OnRecordingStopped;
        _loopbackCapture.StartRecording();
        _logger.LogInformation("Sistem sesi yakalama başlatıldı (Loopback)");
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        try
        {
            var converted = ConvertAudio(e.Buffer, e.BytesRecorded, _micCapture!.WaveFormat);
            AudioDataAvailable?.Invoke(this, new AudioDataEventArgs
            {
                Buffer = converted,
                SourceType = AudioSourceType.Microphone
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mikrofon veri dönüşüm hatası");
        }
    }

    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        try
        {
            var converted = ConvertAudio(e.Buffer, e.BytesRecorded, _loopbackCapture!.WaveFormat);
            AudioDataAvailable?.Invoke(this, new AudioDataEventArgs
            {
                Buffer = converted,
                SourceType = AudioSourceType.SystemAudio
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loopback veri dönüşüm hatası");
        }
    }

    private ReadOnlyMemory<byte> ConvertAudio(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat)
    {
        if (sourceFormat.SampleRate == _targetFormat!.SampleRate &&
            sourceFormat.BitsPerSample == _targetFormat.BitsPerSample &&
            sourceFormat.Channels == _targetFormat.Channels)
        {
            return new ReadOnlyMemory<byte>(buffer, 0, bytesRecorded);
        }

        // Stereo veya float format ise önce mono int16'ya çevir
        if (sourceFormat.Channels > 1 ||
            (sourceFormat.BitsPerSample == 32 && sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat))
        {
            var monoSamples = new List<byte>();
            var sampleBytes = sourceFormat.BitsPerSample / 8;
            var frameBytes = sampleBytes * sourceFormat.Channels;

            for (int i = 0; i < bytesRecorded; i += frameBytes)
            {
                if (i + frameBytes > bytesRecorded) break;

                if (sourceFormat.BitsPerSample == 32 && sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    float sum = 0;
                    for (int ch = 0; ch < sourceFormat.Channels; ch++)
                        sum += BitConverter.ToSingle(buffer, i + ch * 4);

                    float avg = sum / sourceFormat.Channels;
                    short sample = (short)(Math.Clamp(avg, -1f, 1f) * short.MaxValue);
                    monoSamples.AddRange(BitConverter.GetBytes(sample));
                }
                else if (sourceFormat.BitsPerSample == 16)
                {
                    int sum = 0;
                    for (int ch = 0; ch < sourceFormat.Channels; ch++)
                        sum += BitConverter.ToInt16(buffer, i + ch * 2);

                    short avg = (short)(sum / sourceFormat.Channels);
                    monoSamples.AddRange(BitConverter.GetBytes(avg));
                }
            }

            var monoBytes = monoSamples.ToArray();
            var monoFormat = new WaveFormat(sourceFormat.SampleRate, 16, 1);

            if (sourceFormat.SampleRate != _targetFormat.SampleRate)
                return ResampleAudio(monoBytes, monoFormat);

            return monoBytes;
        }

        // Mono ama farklı sample rate
        if (sourceFormat.SampleRate != _targetFormat.SampleRate)
            return ResampleAudio(buffer.AsSpan(0, bytesRecorded).ToArray(), sourceFormat);

        return new ReadOnlyMemory<byte>(buffer, 0, bytesRecorded);
    }

    private ReadOnlyMemory<byte> ResampleAudio(byte[] data, WaveFormat sourceFormat)
    {
        using var sourceStream = new RawSourceWaveStream(new MemoryStream(data), sourceFormat);
        using var resampler = new MediaFoundationResampler(sourceStream, _targetFormat!);
        resampler.ResamplerQuality = 60;

        var outputBuffer = new byte[data.Length * 4];
        int bytesRead = resampler.Read(outputBuffer, 0, outputBuffer.Length);
        return new ReadOnlyMemory<byte>(outputBuffer, 0, bytesRead);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            var source = sender == _micCapture ? "Mikrofon" : "Sistem sesi";
            _logger.LogError(e.Exception, "{Source} kaydı durdu (hata)", source);
            CaptureError?.Invoke(this, new AudioCaptureErrorEventArgs
            {
                Message = $"{source} hatası",
                Exception = e.Exception
            });
        }
    }

    public Task StopCaptureAsync(CancellationToken ct = default)
    {
        _micCapture?.StopRecording();
        _loopbackCapture?.StopRecording();
        _isCapturing = false;
        _logger.LogInformation("Ses yakalama durduruldu");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _micCapture?.StopRecording();
        _micCapture?.Dispose();
        _loopbackCapture?.StopRecording();
        _loopbackCapture?.Dispose();
        _isCapturing = false;
        return ValueTask.CompletedTask;
    }
}
