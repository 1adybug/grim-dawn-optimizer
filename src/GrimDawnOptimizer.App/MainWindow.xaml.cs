using System.Windows;
using GrimDawnOptimizer.App.ViewModels;
using Microsoft.Win32;

namespace GrimDawnOptimizer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private async void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Grim Dawn 游戏目录" };
        if (dialog.ShowDialog(this) == true) await _viewModel.UseGameFolderAsync(dialog.FolderName);
    }

    private async void BrowseSave_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Grim Dawn save 目录" };
        if (dialog.ShowDialog(this) == true) await _viewModel.UseSaveFolderAsync(dialog.FolderName);
    }
}
