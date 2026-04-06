using System.Collections.ObjectModel;
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

    [ObservableProperty] private string _statusText = "Hazır — Başlamak için API key'lerinizi Ayarlar'dan girin";
    [ObservableProperty] private string _connectionState = "Bağlantı yok";
    [ObservableProperty] private AudioDeviceViewModel? _selectedMicrophone;
    [ObservableProperty] private SourceTypeOption? _selectedSourceType;
    [ObservableProperty] private int _speakerCount;
    [ObservableProperty] private int _entryCount;
    [ObservableProperty] private string _sessionDuration = "00:00:00";

    private DateTime _sessionStartTime;
    private System.Threading.Timer? _durationTimer;

    public MainViewModel(
        TranscriptManager transcriptManager,
        IAudioCaptureService audioCaptureService,
        IServiceProvider serviceProvider,
        IOptions<VoiceBridgeOptions> options,
        ILogger<MainViewModel> logger)
    {
        _transcriptManager = transcriptManager;
        _audioCaptureService = audioCaptureService;
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;

        // Thread-safe koleksiyon senkronizasyonu
        BindingOperations.EnableCollectionSynchronization(Entries, _entriesLock);

        _transcriptManager.EntryAdded += OnEntryAdded;
        _transcriptManager.TranslationCompleted += OnTranslationCompleted;
        _transcriptManager.StateChanged += OnStateChanged;

        SelectedSourceType = SourceTypeOptions[0];
        LoadAudioDevices();

        if (!string.IsNullOrEmpty(_options.Deepgram.ApiKey) && !string.IsNullOrEmpty(_options.DeepL.ApiKey))
            StatusText = "Hazır";
    }

    private void LoadAudioDevices()
    {
        try
        {
            var devices = _audioCaptureService.GetAvailableDevices();
            foreach (var device in devices.Where(d => d.SourceType == AudioSourceType.Microphone))
            {
                var vm = new AudioDeviceViewModel(device);
                MicrophoneDevices.Add(vm);
                if (device.IsDefault) SelectedMicrophone = vm;
            }
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
                Application.Current?.Dispatcher.Invoke(() =>
                    SessionDuration = elapsed.ToString(@"hh\:mm\:ss"));
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
    private async Task ExportAsTextAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Text dosyası (*.txt)|*.txt",
            FileName = $"VoiceBridge_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog() == true)
        {
            await _transcriptManager.ExportAsTextAsync(dialog.FileName);
            StatusText = $"Dışa aktarıldı: {dialog.FileName}";
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
            await _transcriptManager.ExportAsSrtAsync(dialog.FileName);
            StatusText = $"Dışa aktarıldı: {dialog.FileName}";
        }
    }

    private void OnEntryAdded(object? sender, TranscriptReceivedEventArgs e)
    {
        var entry = e.Entry;

        lock (_entriesLock)
        {
            if (entry.IsInterim)
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

        Application.Current?.Dispatcher.Invoke(() =>
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
            }
        }
    }

    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ConnectionState = e.NewState switch
            {
                SessionState.Idle => "Bağlantı yok",
                SessionState.Connecting => "Bağlanıyor...",
                SessionState.Listening => "🟢 Bağlı - Dinleniyor",
                SessionState.Error => "🔴 Hata",
                SessionState.Disconnected => "Bağlantı kesildi",
                _ => "Bilinmiyor"
            };
        });
    }

    public void Dispose()
    {
        _durationTimer?.Dispose();
        _transcriptManager.EntryAdded -= OnEntryAdded;
        _transcriptManager.TranslationCompleted -= OnTranslationCompleted;
        _transcriptManager.StateChanged -= OnStateChanged;
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
