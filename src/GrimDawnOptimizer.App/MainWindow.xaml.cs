using System.Windows;
using System.Windows.Media.Animation;
using GrimDawnOptimizer.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace GrimDawnOptimizer.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel = new();
    private bool _historyFlushedForClose;
    private bool _systemThemeWatching;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        ApplyTheme(_viewModel.SelectedThemePreference);
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            ApplyTheme(_viewModel.SelectedThemePreference);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedThemePreference))
            ApplyTheme(_viewModel.SelectedThemePreference);
        else if (e.PropertyName == nameof(MainViewModel.CurrentWorkspace))
            Dispatcher.BeginInvoke(AnimateWorkspace);
    }

    private void ApplyTheme(Services.ThemePreference preference)
    {
        if (ApplicationThemeManager.IsSystemHighContrast()) preference = Services.ThemePreference.System;

        if (preference == Services.ThemePreference.System)
        {
            if (!_systemThemeWatching)
            {
                SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
                _systemThemeWatching = true;
            }
            else
            {
                ApplicationThemeManager.ApplySystemTheme(updateAccent: true);
            }
            return;
        }

        if (_systemThemeWatching && IsLoaded)
        {
            SystemThemeWatcher.UnWatch(this);
            _systemThemeWatching = false;
        }

        var theme = preference == Services.ThemePreference.Dark
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: true);
    }

    private void AnimateWorkspace()
    {
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        WorkspaceHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
        WorkspaceTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_historyFlushedForClose) return;
        e.Cancel = true;
        await Task.WhenAll(_viewModel.FlushSourceHistoryAsync(), _viewModel.FlushUiPreferencesAsync());
        _historyFlushedForClose = true;
        Close();
    }
}
