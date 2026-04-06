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
    [ObservableProperty] private string _deepLModelType = "latency_optimized";
    [ObservableProperty] private string _deepLFormality = "default";
    [ObservableProperty] private bool _autoTranslate = true;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasChanges;

    public SettingsViewModel(IOptions<VoiceBridgeOptions> options)
    {
        var opts = options.Value;
        _deepgramApiKey = opts.Deepgram.ApiKey;
        _deepLApiKey = opts.DeepL.ApiKey;
        _deepgramModel = opts.Deepgram.Model;
        _deepLModelType = opts.DeepL.ModelType;
        _deepLFormality = opts.DeepL.Formality;
        _autoTranslate = opts.Translation.AutoTranslate;

        // Eğer user config varsa oradan da oku (override)
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
        }
        catch { /* İlk kullanımda dosya yoksa sorun değil */ }
    }

    partial void OnDeepgramApiKeyChanged(string value) => HasChanges = true;
    partial void OnDeepLApiKeyChanged(string value) => HasChanges = true;
    partial void OnDeepgramModelChanged(string value) => HasChanges = true;
    partial void OnDeepLModelTypeChanged(string value) => HasChanges = true;
    partial void OnAutoTranslateChanged(bool value) => HasChanges = true;

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
                        ["Model"] = DeepgramModel
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
