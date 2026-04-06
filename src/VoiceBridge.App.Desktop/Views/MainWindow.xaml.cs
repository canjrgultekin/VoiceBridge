using System.Windows;
using VoiceBridge.App.Desktop.ViewModels;

namespace VoiceBridge.App.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        // Auto-scroll transcript
        _viewModel.Entries.CollectionChanged += (_, _) =>
        {
            if (TranscriptList.Items.Count > 0)
            {
                TranscriptList.ScrollIntoView(
                    TranscriptList.Items[^1]);
            }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
