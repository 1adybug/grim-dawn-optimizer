using System.IO;
using System.Windows;
using GrimDawnOptimizer.App.ViewModels;
using Microsoft.Win32;

namespace GrimDawnOptimizer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _historyFlushedForClose;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Closing += MainWindow_Closing;
    }

    private async void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Grim Dawn 游戏目录" };
        if (Directory.Exists(_viewModel.SelectedInstallation?.RootPath))
            dialog.InitialDirectory = _viewModel.SelectedInstallation!.RootPath;
        if (dialog.ShowDialog(this) == true) await _viewModel.UseGameFolderAsync(dialog.FolderName);
    }

    private async void BrowseSave_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Grim Dawn save 目录" };
        if (Directory.Exists(_viewModel.SelectedSaveLocation?.RootPath))
            dialog.InitialDirectory = _viewModel.SelectedSaveLocation!.RootPath;
        if (dialog.ShowDialog(this) == true) await _viewModel.UseSaveFolderAsync(dialog.FolderName);
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_historyFlushedForClose) return;
        e.Cancel = true;
        await _viewModel.FlushSourceHistoryAsync();
        _historyFlushedForClose = true;
        Close();
    }
}
