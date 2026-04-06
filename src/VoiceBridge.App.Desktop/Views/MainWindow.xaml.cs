using System.ComponentModel;
using System.Windows;
using VoiceBridge.App.Desktop.ViewModels;

namespace VoiceBridge.App.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closing;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        // Auto-scroll — her zaman UI thread'inde çalışmalı
        _viewModel.Entries.CollectionChanged += (_, _) =>
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(ScrollToLatest);
            }
            else
            {
                ScrollToLatest();
            }
        };
    }

    private void ScrollToLatest()
    {
        try
        {
            if (TranscriptList.Items.Count > 0)
            {
                TranscriptList.ScrollIntoView(TranscriptList.Items[^1]);
            }
        }
        catch
        {
            // Collection değişim anında race condition olabilir, yok say
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closing)
        {
            base.OnClosing(e);
            return;
        }

        // Session aktifse önce graceful stop
        if (_viewModel.IsSessionActive)
        {
            e.Cancel = true;
            _closing = true;

            try
            {
                await _viewModel.StopSessionIfActiveAsync();
            }
            catch { /* Yok say, nasılsa kapanıyor */ }

            Close();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
