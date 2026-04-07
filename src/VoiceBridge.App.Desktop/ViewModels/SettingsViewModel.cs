using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using VoiceBridge.Core.Configuration;

namespace VoiceBridge.App.Desktop.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private static readonly string SettingsFilePath =
        Path.Combine(AppContext.BaseDirectory, "appsettings.user.json");

    [ObservableProperty] private string _deepgramApiKey = string.Empty;
    [ObservableProperty] private string _deepLApiKey = string.Empty;
    [ObservableProperty] private string _deepgramModel = "nova-3";
    [ObservableProperty] private string _deepgramLanguage = "tr-en";
    [ObservableProperty] private string _deepLModelType = "latency_optimized";
    [ObservableProperty] private string _deepLFormality = "default";
    [ObservableProperty] private bool _autoTranslate = true;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasChanges;

    // Audio Filtering (diarization fix)
    [ObservableProperty] private bool _enableVad = true;
    [ObservableProperty] private float _vadThreshold = 0.5f;
    [ObservableProperty] private int _vadHangoverMs = 400;
    [ObservableProperty] private bool _enableNoiseGate = true;
    [ObservableProperty] private double _noiseGateDbfs = -45.0;
    [ObservableProperty] private int _minWordCount = 2;

    public SettingsViewModel(IOptions<VoiceBridgeOptions> options)
    {
        var opts = options.Value;
        _deepgramApiKey = opts.Deepgram.ApiKey;
        _deepLApiKey = opts.DeepL.ApiKey;
        _deepgramModel = opts.Deepgram.Model;
        _deepgramLanguage = opts.Deepgram.Language;
        _deepLModelType = opts.DeepL.ModelType;
        _deepLFormality = opts.DeepL.Formality;
        _autoTranslate = opts.Translation.AutoTranslate;

        _enableVad = opts.AudioFiltering.EnableVad;
        _vadThreshold = opts.AudioFiltering.VadThreshold;
        _vadHangoverMs = opts.AudioFiltering.VadHangoverMs;
        _enableNoiseGate = opts.AudioFiltering.EnableNoiseGate;
        _noiseGateDbfs = opts.AudioFiltering.NoiseGateDbfs;
        _minWordCount = opts.AudioFiltering.MinWordCount;

        LoadUserSettings();
    }

    private void LoadUserSettings()
    {
        if (!File.Exists(SettingsFilePath)) return;

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var node = JsonNode.Parse(json);
            var vb = node?["VoiceBridge"];
            if (vb is null) return;

            var dg = vb["Deepgram"];
            if (dg?["ApiKey"]?.GetValue<string>() is { Length: > 0 } dgKey)
                DeepgramApiKey = dgKey;
            if (dg?["Model"]?.GetValue<string>() is { Length: > 0 } dgModel)
                DeepgramModel = dgModel;
            if (dg?["Language"]?.GetValue<string>() is { Length: > 0 } dgLang)
                DeepgramLanguage = dgLang;

            var dl = vb["DeepL"];
            if (dl?["ApiKey"]?.GetValue<string>() is { Length: > 0 } dlKey)
                DeepLApiKey = dlKey;
            if (dl?["ModelType"]?.GetValue<string>() is { Length: > 0 } dlModel)
                DeepLModelType = dlModel;
            if (dl?["Formality"]?.GetValue<string>() is { Length: > 0 } dlForm)
                DeepLFormality = dlForm;

            var tr = vb["Translation"];
            if (tr?["AutoTranslate"] is not null)
                AutoTranslate = tr["AutoTranslate"]!.GetValue<bool>();

            var af = vb["AudioFiltering"];
            if (af is not null)
            {
                if (af["EnableVad"] is not null)
                    EnableVad = af["EnableVad"]!.GetValue<bool>();
                if (af["VadThreshold"] is not null)
                    VadThreshold = (float)af["VadThreshold"]!.GetValue<double>();
                if (af["VadHangoverMs"] is not null)
                    VadHangoverMs = af["VadHangoverMs"]!.GetValue<int>();
                if (af["EnableNoiseGate"] is not null)
                    EnableNoiseGate = af["EnableNoiseGate"]!.GetValue<bool>();
                if (af["NoiseGateDbfs"] is not null)
                    NoiseGateDbfs = af["NoiseGateDbfs"]!.GetValue<double>();
                if (af["MinWordCount"] is not null)
                    MinWordCount = af["MinWordCount"]!.GetValue<int>();
            }

            HasChanges = false;
        }
        catch { }
    }

    partial void OnDeepgramApiKeyChanged(string value) => HasChanges = true;
    partial void OnDeepLApiKeyChanged(string value) => HasChanges = true;
    partial void OnDeepgramModelChanged(string value) => HasChanges = true;
    partial void OnDeepgramLanguageChanged(string value) => HasChanges = true;
    partial void OnDeepLModelTypeChanged(string value) => HasChanges = true;
    partial void OnDeepLFormalityChanged(string value) => HasChanges = true;
    partial void OnAutoTranslateChanged(bool value) => HasChanges = true;
    partial void OnEnableVadChanged(bool value) => HasChanges = true;
    partial void OnVadThresholdChanged(float value) => HasChanges = true;
    partial void OnVadHangoverMsChanged(int value) => HasChanges = true;
    partial void OnEnableNoiseGateChanged(bool value) => HasChanges = true;
    partial void OnNoiseGateDbfsChanged(double value) => HasChanges = true;
    partial void OnMinWordCountChanged(int value) => HasChanges = true;

    [RelayCommand]
    private void Save()
    {
        try
        {
            var settings = new JsonObject
            {
                ["VoiceBridge"] = new JsonObject
                {
                    ["Deepgram"] = new JsonObject
                    {
                        ["ApiKey"] = DeepgramApiKey,
                        ["Model"] = DeepgramModel,
                        ["Language"] = DeepgramLanguage
                    },
                    ["DeepL"] = new JsonObject
                    {
                        ["ApiKey"] = DeepLApiKey,
                        ["ModelType"] = DeepLModelType,
                        ["Formality"] = DeepLFormality
                    },
                    ["Translation"] = new JsonObject
                    {
                        ["AutoTranslate"] = AutoTranslate
                    },
                    ["AudioFiltering"] = new JsonObject
                    {
                        ["EnableVad"] = EnableVad,
                        ["VadThreshold"] = VadThreshold,
                        ["VadHangoverMs"] = VadHangoverMs,
                        ["EnableNoiseGate"] = EnableNoiseGate,
                        ["NoiseGateDbfs"] = NoiseGateDbfs,
                        ["MinWordCount"] = MinWordCount
                    }
                }
            };

            var json = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);

            HasChanges = false;
            StatusMessage = "Ayarlar kaydedildi. Değişikliklerin uygulanması için uygulamayı yeniden başlatın.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Kaydetme hatası: {ex.Message}";
        }
    }

    public bool ValidateApiKeys()
    {
        return !string.IsNullOrWhiteSpace(DeepgramApiKey)
            && !string.IsNullOrWhiteSpace(DeepLApiKey);
    }
}
