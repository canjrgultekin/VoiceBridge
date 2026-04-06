using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using VoiceBridge.App.Desktop.ViewModels;
using VoiceBridge.App.Desktop.Views;
using VoiceBridge.Core;
using VoiceBridge.Platform.Windows;

namespace VoiceBridge.App.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.user.json", optional: true, reloadOnChange: true)
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "logs", "voicebridge-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        services.AddSingleton<IConfiguration>(configuration);

        // Core servisleri (platform-agnostic)
        services.AddVoiceBridgeCore(configuration);

        // Windows platform servisleri
        services.AddWindowsPlatform();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Views
        services.AddTransient<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
