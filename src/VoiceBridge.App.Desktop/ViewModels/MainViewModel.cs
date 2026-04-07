using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using VoiceBridge.Core.Configuration;
using VoiceBridge.Core.Events;
using VoiceBridge.Core.Interfaces;
using VoiceBridge.Core.Models;
using VoiceBridge.Core.Services;

namespace VoiceBridge.App.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly TranscriptManager _transcriptManager;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IApiKeyValidator _apiKeyValidator;
    private readonly IServiceProvider _serviceProvider;
    private readonly VoiceBridgeOptions _options;
    private readonly ILogger<MainViewModel> _logger;
    private readonly object _entriesLock = new();

    public ObservableCollection<TranscriptEntryViewModel> Entries { get; } = [];
    public ObservableCollection<AudioDeviceViewModel> MicrophoneDevices { get; } = [];
    public ObservableCollection<SourceTypeOption> SourceTypeOptions { get; } =
    [
        new("🎤  Mikrofon", AudioSourceType.Microphone),
        new("🔊  Sistem Sesi", AudioSourceType.SystemAudio),
        new("🎧  Her İkisi", AudioSourceType.Both)
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSessionCommand))]
    private bool _isSessionActive;

    [ObservableProperty] private string _statusText = "Başlatılıyor...";
    [ObservableProperty] private string _connectionState = "Bağlantı yok";
    [ObservableProperty] private AudioDeviceViewModel? _selectedMicrophone;
    [ObservableProperty] private SourceTypeOption? _selectedSourceType;
    [ObservableProperty] private int _speakerCount;
    [ObservableProperty] private int _entryCount;
    [ObservableProperty] private string _sessionDuration = "00:00:00";
    [ObservableProperty] private bool _apiKeysValid;
    [ObservableProperty] private double _micLevel;
    [ObservableProperty] private double _micLevelPeak;

    private DateTime _sessionStartTime;
    private System.Threading.Timer? _durationTimer;
    private bool _disposed;

    public MainViewModel(
        TranscriptManager transcriptManager,
        IAudioCaptureService audioCaptureService,
        IApiKeyValidator apiKeyValidator,
        IServiceProvider serviceProvider,
        IOptions<VoiceBridgeOptions> options,
        ILogger<MainViewModel> logger)
    {
        _transcriptManager = transcriptManager;
        _audioCaptureService = audioCaptureService;
        _apiKeyValidator = apiKeyValidator;
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;

        BindingOperations.EnableCollectionSynchronization(Entries, _entriesLock);

        _transcriptManager.EntryAdded += OnEntryAdded;
        _transcriptManager.TranslationCompleted += OnTranslationCompleted;
        _transcriptManager.StateChanged += OnStateChanged;
        _transcriptManager.AudioDeviceChanged += OnAudioDeviceChanged;
        _transcriptManager.AudioCaptureError += OnAudioCaptureError;

        // Audio level metering — direkt audio service'ten dinle
        _audioCaptureService.AudioLevelChanged += OnAudioLevelChanged;

        SelectedSourceType = SourceTypeOptions[0];
        LoadAudioDevices();

        _ = ValidateApiKeysOnStartupAsync();
    }

    private async Task ValidateApiKeysOnStartupAsync()
    {
        try
        {
            StatusText = "API key'ler doğrulanıyor...";

            if (string.IsNullOrWhiteSpace(_options.Deepgram.ApiKey) ||
                string.IsNullOrWhiteSpace(_options.DeepL.ApiKey))
            {
                await RunOnUiAsync(() =>
                {
                    ApiKeysValid = false;
                    StatusText = "API key'ler eksik — Ayarlar'dan girin";
                });
                return;
            }

            var result = await _apiKeyValidator.ValidateAsync();

            await RunOnUiAsync(() =>
            {
                ApiKeysValid = result.AllValid;
                StatusText = result.BuildStatusMessage();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup API key doğrulama hatası");
            await RunOnUiAsync(() =>
            {
                ApiKeysValid = false;
                StatusText = $"API key doğrulama hatası: {ex.Message}";
            });
        }
    }

    private void LoadAudioDevices()
    {
        try
        {
            MicrophoneDevices.Clear();
            var devices = _audioCaptureService.GetAvailableDevices();
            foreach (var device in devices.Where(d => d.SourceType == AudioSourceType.Microphone))
            {
                var vm = new AudioDeviceViewModel(device);
                MicrophoneDevices.Add(vm);
                if (device.IsDefault) SelectedMicrophone = vm;
            }

            if (SelectedMicrophone is null && MicrophoneDevices.Count > 0)
                SelectedMicrophone = MicrophoneDevices[0];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ses cihazları yüklenemedi");
            StatusText = "Ses cihazları yüklenemedi";
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var settingsVm = _serviceProvider.GetRequiredService<SettingsViewModel>();
        var window = new Views.SettingsWindow(settingsVm)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    [RelayCommand(CanExecute = nameof(CanStartSession))]
    private async Task StartSessionAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.Deepgram.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.DeepL.ApiKey))
        {
            StatusText = "API key'ler eksik — Ayarlar'dan girin ve uygulamayı yeniden başlatın";
            OpenSettings();
            return;
        }

        try
        {
            StatusText = "Bağlanıyor...";
            var sourceType = SelectedSourceType?.SourceType ?? AudioSourceType.Microphone;

            var request = new AudioCaptureRequest(
                SelectedMicrophone?.Id, sourceType, 16000, 16, 1);

            await _transcriptManager.StartSessionAsync(request);

            IsSessionActive = true;
            _sessionStartTime = DateTime.Now;

            _durationTimer = new System.Threading.Timer(_ =>
            {
                var elapsed = DateTime.Now - _sessionStartTime;
                _ = RunOnUiAsync(() => SessionDuration = elapsed.ToString(@"hh\:mm\:ss"));
            }, null, 0, 1000);

            StatusText = "Dinleniyor...";
            _logger.LogInformation("Session başlatıldı: {SourceType}", sourceType);
        }
        catch (Exception ex)
        {
            StatusText = $"Hata: {ex.Message}";
            IsSessionActive = false;
            _logger.LogError(ex, "Session başlatılamadı");
        }
    }

    private bool CanStartSession() => !IsSessionActive;

    [RelayCommand(CanExecute = nameof(CanStopSession))]
    private async Task StopSessionAsync()
    {
        try
        {
            await _transcriptManager.StopSessionAsync();
            _durationTimer?.Dispose();
            _durationTimer = null;
            IsSessionActive = false;
            MicLevel = 0;
            MicLevelPeak = 0;
            StatusText = "Durduruldu";
            ConnectionState = "Bağlantı kesildi";
        }
        catch (Exception ex)
        {
            StatusText = $"Durdurma hatası: {ex.Message}";
            _logger.LogError(ex, "Session durdurulamadı");
        }
    }

    private bool CanStopSession() => IsSessionActive;

    [RelayCommand]
    private void ClearTranscript()
    {
        lock (_entriesLock)
        {
            Entries.Clear();
        }
        _transcriptManager.Clear();
        EntryCount = 0;
        SpeakerCount = 0;
    }

    [RelayCommand]
    private void CopyAll()
    {
        try
        {
            var sb = new StringBuilder();
            List<TranscriptEntryViewModel> snapshot;
            lock (_entriesLock) { snapshot = Entries.Where(x => !x.IsInterim).ToList(); }

            foreach (var entry in snapshot)
            {
                sb.AppendLine($"[{entry.Timestamp}] {entry.SpeakerLabel} {entry.LanguageFlag}");
                sb.AppendLine(entry.OriginalText);
                if (!string.IsNullOrEmpty(entry.TranslatedText) && !entry.IsTranslationFailed)
                    sb.AppendLine($"→ {entry.TranslatedText}");
                sb.AppendLine();
            }

            if (sb.Length == 0)
            {
                StatusText = "Kopyalanacak içerik yok";
                return;
            }

            Clipboard.SetText(sb.ToString());
            StatusText = $"📋 {snapshot.Count} kayıt panoya kopyalandı";
        }
        catch (Exception ex)
        {
            StatusText = $"Kopyalama hatası: {ex.Message}";
            _logger.LogError(ex, "CopyAll hatası");
        }
    }

    [RelayCommand]
    private async Task ExportAsTextAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Text dosyası (*.txt)|*.txt",
            FileName = $"VoiceBridge_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await _transcriptManager.ExportAsTextAsync(dialog.FileName);
                StatusText = $"💾 Dışa aktarıldı: {dialog.FileName}";
            }
            catch (Exception ex)
            {
                StatusText = $"Dışa aktarma hatası: {ex.Message}";
                _logger.LogError(ex, "TXT export hatası");
            }
        }
    }

    [RelayCommand]
    private async Task ExportAsSrtAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Altyazı dosyası (*.srt)|*.srt",
            FileName = $"VoiceBridge_{DateTime.Now:yyyyMMdd_HHmmss}.srt"
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await _transcriptManager.ExportAsSrtAsync(dialog.FileName);
                StatusText = $"💾 Dışa aktarıldı: {dialog.FileName}";
            }
            catch (Exception ex)
            {
                StatusText = $"Dışa aktarma hatası: {ex.Message}";
                _logger.LogError(ex, "SRT export hatası");
            }
        }
    }

    public async Task StopSessionIfActiveAsync()
    {
        if (IsSessionActive)
        {
            try { await _transcriptManager.StopSessionAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Shutdown sırasında session durdurma hatası"); }
        }
    }

    private void OnEntryAdded(object? sender, TranscriptReceivedEventArgs e)
    {
        var entry = e.Entry;

        lock (_entriesLock)
        {
            if (e.IsUpdate)
            {
                // Smart merge: mevcut Id'li ViewModel'i bul ve içeriğini güncelle.
                // Yeni satır eklemek yerine son satırı genişletiyoruz.
                var target = Entries.FirstOrDefault(x => x.Id == entry.Id);
                if (target is not null)
                {
                    target.UpdateFrom(entry);
                }
                else
                {
                    // Edge case: hedef bulunamadı (UI senkron değil) → fallback yeni ekle
                    Entries.Add(new TranscriptEntryViewModel(entry));
                }
            }
            else if (entry.IsInterim)
            {
                var existing = Entries.LastOrDefault(x =>
                    x.IsInterim && x.SpeakerIndex == entry.SpeakerIndex);
                if (existing is not null)
                    existing.UpdateFrom(entry);
                else
                    Entries.Add(new TranscriptEntryViewModel(entry));
            }
            else
            {
                var interims = Entries
                    .Where(x => x.IsInterim && x.SpeakerIndex == entry.SpeakerIndex)
                    .ToList();
                foreach (var interim in interims)
                    Entries.Remove(interim);

                Entries.Add(new TranscriptEntryViewModel(entry));
            }
        }

        _ = RunOnUiAsync(() =>
        {
            EntryCount = Entries.Count(x => !x.IsInterim);
            SpeakerCount = _transcriptManager.Speakers.Count;
        });
    }

    private void OnTranslationCompleted(object? sender, TranslationCompletedEventArgs e)
    {
        lock (_entriesLock)
        {
            var entry = Entries.FirstOrDefault(x => x.Id == e.EntryId);
            if (entry is not null)
            {
                entry.TranslatedText = e.TranslatedText;
                entry.IsTranslationPending = false;
                entry.IsTranslationFailed = !e.Success;
            }
        }
    }

    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        _ = RunOnUiAsync(() =>
        {
            ConnectionState = e.NewState switch
            {
                SessionState.Idle => "Bağlantı yok",
                SessionState.Connecting => "Bağlanıyor...",
                SessionState.Listening => "🟢 Bağlı - Dinleniyor",
                SessionState.Reconnecting => "🟡 Yeniden bağlanıyor...",
                SessionState.Error => "🔴 Hata",
                SessionState.Disconnected => "Bağlantı kesildi",
                _ => "Bilinmiyor"
            };

            if (e.NewState == SessionState.Reconnecting)
                StatusText = "Bağlantı koptu, yeniden bağlanılıyor...";
            else if (e.NewState == SessionState.Listening && e.OldState == SessionState.Reconnecting)
                StatusText = "Yeniden bağlandı — Dinleniyor";
        });
    }

    private void OnAudioDeviceChanged(object? sender, AudioDeviceChangedEventArgs e)
    {
        _ = RunOnUiAsync(() =>
        {
            LoadAudioDevices();

            if (e.AffectsCurrentSession && e.DeviceName.Contains("yeniden bağlandı"))
            {
                StatusText = "🟢 Mikrofon yeniden bağlandı — Dinleniyor";
                ConnectionState = "🟢 Bağlı - Dinleniyor";
                return;
            }

            if (e.AffectsCurrentSession && IsSessionActive)
            {
                StatusText = e.ChangeType == AudioDeviceChangeType.Removed
                    ? $"⚠️ Kullanılan mikrofon çıkarıldı ({e.DeviceName})"
                    : $"⚠️ Mikrofon durumu değişti: {e.DeviceName}";

                _logger.LogWarning("Aktif session'u etkileyen cihaz değişikliği: {Change} - {Device}",
                    e.ChangeType, e.DeviceName);
            }
        });
    }

    private void OnAudioCaptureError(object? sender, AudioCaptureErrorEventArgs e)
    {
        _ = RunOnUiAsync(() =>
        {
            ConnectionState = "🟡 Mikrofon yeniden bağlanıyor...";
            StatusText = $"🟡 {e.Message}";
            _logger.LogWarning("Audio capture error UI'a iletildi: {Message}", e.Message);
        });
    }

    private void OnAudioLevelChanged(object? sender, AudioLevelEventArgs e)
    {
        // Mikrofon seviyesi UI'a (0-100 arası)
        var rmsPercent = e.Rms * 100.0;
        var peakPercent = e.Peak * 100.0;

        _ = RunOnUiAsync(() =>
        {
            MicLevel = rmsPercent;

            // Peak hold (decay): yeni peak yüksekse anında al, aksi halde yavaşça düş
            if (peakPercent > MicLevelPeak)
                MicLevelPeak = peakPercent;
            else
                MicLevelPeak = Math.Max(0, MicLevelPeak - 2);
        });
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _durationTimer?.Dispose();
        _transcriptManager.EntryAdded -= OnEntryAdded;
        _transcriptManager.TranslationCompleted -= OnTranslationCompleted;
        _transcriptManager.StateChanged -= OnStateChanged;
        _transcriptManager.AudioDeviceChanged -= OnAudioDeviceChanged;
        _transcriptManager.AudioCaptureError -= OnAudioCaptureError;
        _audioCaptureService.AudioLevelChanged -= OnAudioLevelChanged;
    }
}

public partial class TranscriptEntryViewModel : ObservableObject
{
    public string Id { get; }
    public int SpeakerIndex { get; }

    [ObservableProperty] private string _originalText;
    [ObservableProperty] private string _translatedText;
    [ObservableProperty] private bool _isInterim;
    [ObservableProperty] private bool _isTranslationPending;
    [ObservableProperty] private bool _isTranslationFailed;
    [ObservableProperty] private string _languageFlag;
    [ObservableProperty] private string _speakerLabel;
    [ObservableProperty] private string _timestamp;
    [ObservableProperty] private DetectedLanguage _language;

    public TranscriptEntryViewModel(TranscriptEntry entry)
    {
        Id = entry.Id;
        SpeakerIndex = entry.SpeakerIndex;
        _originalText = entry.OriginalText;
        _translatedText = entry.TranslatedText;
        _isInterim = entry.IsInterim;
        _isTranslationPending = entry.IsTranslationPending;
        _isTranslationFailed = entry.IsTranslationFailed;
        _language = entry.Language;
        _languageFlag = entry.Language == DetectedLanguage.Turkish ? "🇹🇷" : "🇬🇧";
        _speakerLabel = entry.SpeakerLabel;
        _timestamp = entry.Timestamp.ToString("HH:mm:ss");
    }

    public void UpdateFrom(TranscriptEntry entry)
    {
        OriginalText = entry.OriginalText;
        Language = entry.Language;
        LanguageFlag = entry.Language == DetectedLanguage.Turkish ? "🇹🇷" : "🇬🇧";
        IsInterim = entry.IsInterim;
        IsTranslationPending = entry.IsTranslationPending;
        TranslatedText = entry.TranslatedText;
        IsTranslationFailed = entry.IsTranslationFailed;
    }

    [RelayCommand]
    private void CopyEntry()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{Timestamp}] {SpeakerLabel} {LanguageFlag}");
            sb.AppendLine(OriginalText);
            if (!string.IsNullOrEmpty(TranslatedText) && !IsTranslationFailed)
                sb.AppendLine($"→ {TranslatedText}");

            Clipboard.SetText(sb.ToString());
        }
        catch
        {
            // Sessizce yok say
        }
    }
}

public sealed class AudioDeviceViewModel(AudioDeviceInfo device)
{
    public string Id { get; } = device.Id;
    public string Name { get; } = device.IsDefault ? $"⭐ {device.Name}" : device.Name;
    public bool IsDefault { get; } = device.IsDefault;
}

public sealed class SourceTypeOption(string displayName, AudioSourceType sourceType)
{
    public string DisplayName { get; } = displayName;
    public AudioSourceType SourceType { get; } = sourceType;
    public override string ToString() => DisplayName;
}
