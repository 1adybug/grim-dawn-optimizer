using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrimDawnOptimizer.App.ViewModels;
using Microsoft.Win32;

namespace GrimDawnOptimizer.App.Views;

public partial class SourcesView : UserControl
{
    public SourcesView()
    {
        InitializeComponent();
    }

    private async void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFolderDialog { Title = "选择 Grim Dawn 游戏目录" };
        if (Directory.Exists(viewModel.SelectedInstallation?.RootPath))
            dialog.InitialDirectory = viewModel.SelectedInstallation!.RootPath;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await viewModel.UseGameFolderAsync(dialog.FolderName);
    }

    private async void BrowseSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFolderDialog { Title = "选择 Grim Dawn save 目录" };
        if (Directory.Exists(viewModel.SelectedSaveLocation?.RootPath))
            dialog.InitialDirectory = viewModel.SelectedSaveLocation!.RootPath;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await viewModel.UseSaveFolderAsync(dialog.FolderName);
    }
}
