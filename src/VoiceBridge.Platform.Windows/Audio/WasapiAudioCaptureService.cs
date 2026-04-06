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

    private const int RetryIntervalMs = 2000;
    private const int MaxRetryAttempts = 60;  // ~2 dakika

    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    public event EventHandler<AudioCaptureErrorEventArgs>? CaptureError;
    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;

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
        {
            // Düzgün kapanış (Stop çağrıldı)
            return;
        }

        _logger.LogWarning(e.Exception, "{Source} kaydı beklenmedik şekilde durdu", source);

        // Sadece kullanıcı hala capture istiyorsa retry başlat
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
                    // Eski capture'ları temizle
                    DisposeCaptures();

                    // Yeniden başlat
                    StartCaptureInternal(request);
                    _isCapturing = true;

                    _logger.LogInformation("Ses yakalama yeniden bağlandı (deneme {Attempt})", attempt);

                    // UI'a bildir: cihaz tekrar aktif
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
        _logger.LogInformation("Ses yakalama durduruldu");
        return Task.CompletedTask;
    }

    // IMMNotificationClient — Windows ses alt sisteminden bildirimleri yakalar
    // Bu metodlar COM thread'inden çağrılır

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        try
        {
            var device = _enumerator.GetDevice(deviceId);
            var name = device.FriendlyName;
            var affects = _shouldBeCapturing && deviceId == _currentDeviceId;

            _logger.LogInformation("Cihaz durum değişti: {Name} -> {State} (Etkiler: {Affects})",
                name, newState, affects);

            // Eğer kullandığımız cihaz tekrar Active olduysa retry'ı hemen tetikle
            if (affects && newState == DeviceState.Active && _isRetrying)
            {
                _logger.LogInformation("Kullanılan cihaz tekrar aktif, retry hemen tetikleniyor");
                // Retry loop zaten döndüğü için bir sonraki iterasyonda yakalayacak
            }

            // Cihaz çıkarıldı/disabled olduysa ve kullanıyorsak retry başlat
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
        // Property değişiklikleri (volume vs.) bizim için önemli değil
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
