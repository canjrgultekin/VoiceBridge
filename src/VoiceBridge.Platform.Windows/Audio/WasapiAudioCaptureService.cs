using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using VoiceBridge.Core.Events;
using VoiceBridge.Core.Interfaces;
using VoiceBridge.Core.Models;

namespace VoiceBridge.Platform.Windows.Audio;

public sealed class WasapiAudioCaptureService : IAudioCaptureService, IMMNotificationClient
{
    private readonly ILogger<WasapiAudioCaptureService> _logger;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly object _captureLock = new();

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private WaveFormat? _targetFormat;
    private bool _isCapturing;
    private string? _currentDeviceId;
    private bool _notificationsRegistered;

    // Otomatik yeniden bağlanma için
    private AudioCaptureRequest? _lastRequest;
    private bool _shouldBeCapturing;
    private CancellationTokenSource? _retryCts;
    private Task? _retryTask;
    private bool _isRetrying;

    // Ses seviyesi metering için throttling
    private DateTime _lastMicLevelEmit = DateTime.MinValue;
    private DateTime _lastLoopbackLevelEmit = DateTime.MinValue;
    private const int LevelEmitIntervalMs = 50;  // 20 Hz update

    private const int RetryIntervalMs = 2000;
    private const int MaxRetryAttempts = 60;

    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    public event EventHandler<AudioCaptureErrorEventArgs>? CaptureError;
    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;
    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    public bool IsCapturing => _isCapturing;
    public string? CurrentDeviceId => _currentDeviceId;

    public WasapiAudioCaptureService(ILogger<WasapiAudioCaptureService> logger)
    {
        _logger = logger;
        _enumerator = new MMDeviceEnumerator();

        try
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
            _notificationsRegistered = true;
            _logger.LogInformation("Ses cihazı bildirimleri kaydedildi");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device notification callback kaydedilemedi");
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetAvailableDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            var captureDevices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            var defaultCapture = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            foreach (var device in captureDevices)
            {
                devices.Add(new AudioDeviceInfo(
                    device.ID, device.FriendlyName,
                    device.ID == defaultCapture.ID, AudioSourceType.Microphone));
            }

            var renderDevices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var defaultRender = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

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
        _lastRequest = request;
        _shouldBeCapturing = true;

        try
        {
            StartCaptureInternal(request);
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

    private void StartCaptureInternal(AudioCaptureRequest request)
    {
        lock (_captureLock)
        {
            if (request.SourceType is AudioSourceType.Microphone or AudioSourceType.Both)
                StartMicrophoneCapture(request.DeviceId);

            if (request.SourceType is AudioSourceType.SystemAudio or AudioSourceType.Both)
                StartLoopbackCapture();
        }
    }

    private void StartMicrophoneCapture(string? deviceId)
    {
        var device = string.IsNullOrEmpty(deviceId)
            ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : _enumerator.GetDevice(deviceId);

        _currentDeviceId = device.ID;

        _micCapture = new WasapiCapture(device)
        {
            WaveFormat = new WaveFormat(_targetFormat!.SampleRate, 16, 1)
        };

        _micCapture.DataAvailable += OnMicDataAvailable;
        _micCapture.RecordingStopped += OnRecordingStopped;
        _micCapture.StartRecording();
        _logger.LogInformation("Mikrofon yakalama başlatıldı: {Device} ({Id})",
            device.FriendlyName, device.ID);
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

            // Ses seviyesi (throttled)
            EmitLevelIfDue(converted, AudioSourceType.Microphone, ref _lastMicLevelEmit);
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

            EmitLevelIfDue(converted, AudioSourceType.SystemAudio, ref _lastLoopbackLevelEmit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loopback veri dönüşüm hatası");
        }
    }

    private void EmitLevelIfDue(ReadOnlyMemory<byte> pcm16Buffer, AudioSourceType source, ref DateTime lastEmit)
    {
        var now = DateTime.UtcNow;
        if ((now - lastEmit).TotalMilliseconds < LevelEmitIntervalMs)
            return;

        lastEmit = now;

        var (peak, rms) = CalculateLevels(pcm16Buffer.Span);

        AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs
        {
            Peak = peak,
            Rms = rms,
            SourceType = source
        });
    }

    private static (double Peak, double Rms) CalculateLevels(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length < 2) return (0, 0);

        int sampleCount = pcm16.Length / 2;
        double sumSquares = 0;
        int peakRaw = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            int abs = sample < 0 ? -sample : sample;
            if (abs > peakRaw) peakRaw = abs;
            sumSquares += sample * sample;
        }

        double rms = Math.Sqrt(sumSquares / sampleCount) / 32768.0;
        double peak = peakRaw / 32768.0;

        return (Math.Min(1.0, peak), Math.Min(1.0, rms));
    }

    private ReadOnlyMemory<byte> ConvertAudio(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat)
    {
        if (sourceFormat.SampleRate == _targetFormat!.SampleRate &&
            sourceFormat.BitsPerSample == _targetFormat.BitsPerSample &&
            sourceFormat.Channels == _targetFormat.Channels)
        {
            return new ReadOnlyMemory<byte>(buffer, 0, bytesRecorded);
        }

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
        var source = sender == _micCapture ? "Mikrofon" : "Sistem sesi";

        if (e.Exception is null)
            return;

        _logger.LogWarning(e.Exception, "{Source} kaydı beklenmedik şekilde durdu", source);

        if (_shouldBeCapturing && _lastRequest is not null)
        {
            _isCapturing = false;

            CaptureError?.Invoke(this, new AudioCaptureErrorEventArgs
            {
                Message = $"{source} bağlantısı koptu, yeniden bağlanılıyor...",
                Exception = e.Exception
            });

            StartRetryLoop();
        }
        else
        {
            CaptureError?.Invoke(this, new AudioCaptureErrorEventArgs
            {
                Message = $"{source} hatası",
                Exception = e.Exception
            });
        }
    }

    private void StartRetryLoop()
    {
        lock (_captureLock)
        {
            if (_isRetrying) return;
            _isRetrying = true;

            _retryCts?.Cancel();
            _retryCts?.Dispose();
            _retryCts = new CancellationTokenSource();

            var ct = _retryCts.Token;
            var request = _lastRequest!;
            _retryTask = Task.Run(() => RetryCaptureLoopAsync(request, ct), CancellationToken.None);
        }
    }

    private async Task RetryCaptureLoopAsync(AudioCaptureRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Ses yakalama yeniden bağlanma döngüsü başladı");

        int attempt = 0;
        try
        {
            while (_shouldBeCapturing && !ct.IsCancellationRequested && attempt < MaxRetryAttempts)
            {
                attempt++;

                try { await Task.Delay(RetryIntervalMs, ct); }
                catch (OperationCanceledException) { return; }

                try
                {
                    DisposeCaptures();

                    StartCaptureInternal(request);
                    _isCapturing = true;

                    _logger.LogInformation("Ses yakalama yeniden bağlandı (deneme {Attempt})", attempt);

                    DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs
                    {
                        DeviceId = _currentDeviceId ?? string.Empty,
                        DeviceName = "Mikrofon yeniden bağlandı",
                        ChangeType = AudioDeviceChangeType.StateChanged,
                        AffectsCurrentSession = true
                    });

                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Mikrofon retry denemesi {Attempt} başarısız: {Error}",
                        attempt, ex.Message);
                }
            }

            if (_shouldBeCapturing && !ct.IsCancellationRequested)
            {
                _logger.LogError("Ses yakalama {Max} deneme sonunda yeniden bağlanamadı", MaxRetryAttempts);
                CaptureError?.Invoke(this, new AudioCaptureErrorEventArgs
                {
                    Message = "Mikrofon yeniden bağlanamadı. Windows mikrofon ayarlarını kontrol edin ve session'ı yeniden başlatın."
                });
            }
        }
        finally
        {
            _isRetrying = false;
        }
    }

    private void DisposeCaptures()
    {
        try { _micCapture?.StopRecording(); } catch { }
        try { _micCapture?.Dispose(); } catch { }
        _micCapture = null;

        try { _loopbackCapture?.StopRecording(); } catch { }
        try { _loopbackCapture?.Dispose(); } catch { }
        _loopbackCapture = null;
    }

    public Task StopCaptureAsync(CancellationToken ct = default)
    {
        _shouldBeCapturing = false;
        _retryCts?.Cancel();

        DisposeCaptures();

        _isCapturing = false;
        _currentDeviceId = null;

        // Seviye metresini sıfırla
        AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs
        {
            Peak = 0,
            Rms = 0,
            SourceType = AudioSourceType.Microphone
        });

        _logger.LogInformation("Ses yakalama durduruldu");
        return Task.CompletedTask;
    }

    // IMMNotificationClient

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        try
        {
            var device = _enumerator.GetDevice(deviceId);
            var name = device.FriendlyName;
            var affects = _shouldBeCapturing && deviceId == _currentDeviceId;

            _logger.LogInformation("Cihaz durum değişti: {Name} -> {State} (Etkiler: {Affects})",
                name, newState, affects);

            if (affects && newState != DeviceState.Active && _isCapturing)
            {
                _logger.LogWarning("Kullanılan cihaz devre dışı bırakıldı, retry başlatılıyor");
                _isCapturing = false;
                if (_lastRequest is not null)
                    StartRetryLoop();
            }

            DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs
            {
                DeviceId = deviceId,
                DeviceName = name,
                ChangeType = AudioDeviceChangeType.StateChanged,
                AffectsCurrentSession = affects
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnDeviceStateChanged hata");
        }
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
        try
        {
            var device = _enumerator.GetDevice(pwstrDeviceId);
            DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs
            {
                DeviceId = pwstrDeviceId,
                DeviceName = device.FriendlyName,
                ChangeType = AudioDeviceChangeType.Added,
                AffectsCurrentSession = false
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnDeviceAdded hata");
        }
    }

    public void OnDeviceRemoved(string deviceId)
    {
        var affects = _shouldBeCapturing && deviceId == _currentDeviceId;
        _logger.LogInformation("Cihaz kaldırıldı: {Id} (Etkiler: {Affects})", deviceId, affects);

        if (affects && _isCapturing)
        {
            _isCapturing = false;
            if (_lastRequest is not null)
                StartRetryLoop();
        }

        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs
        {
            DeviceId = deviceId,
            DeviceName = "(kaldırılmış cihaz)",
            ChangeType = AudioDeviceChangeType.Removed,
            AffectsCurrentSession = affects
        });
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Capture || role != Role.Communications)
            return;

        if (string.IsNullOrEmpty(defaultDeviceId))
            return;

        try
        {
            var device = _enumerator.GetDevice(defaultDeviceId);
            _logger.LogInformation("Varsayılan mikrofon değişti: {Name}", device.FriendlyName);

            DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs
            {
                DeviceId = defaultDeviceId,
                DeviceName = device.FriendlyName,
                ChangeType = AudioDeviceChangeType.DefaultChanged,
                AffectsCurrentSession = false
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnDefaultDeviceChanged hata");
        }
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }

    public ValueTask DisposeAsync()
    {
        _shouldBeCapturing = false;
        _retryCts?.Cancel();

        DisposeCaptures();

        if (_notificationsRegistered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); }
            catch (Exception ex) { _logger.LogWarning(ex, "Notification callback unregister hata"); }
        }

        try { _enumerator.Dispose(); } catch { }
        try { _retryCts?.Dispose(); } catch { }

        _isCapturing = false;
        return ValueTask.CompletedTask;
    }
}
