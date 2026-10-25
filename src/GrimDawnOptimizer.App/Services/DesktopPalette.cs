using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace GrimDawnOptimizer.App.Services;

/// <summary>Canonical desktop colors. Views consume semantic resource names only.</summary>
public static class DesktopPalette
{
    public static void Apply(ApplicationTheme theme)
    {
        var dark = theme == ApplicationTheme.Dark;
        var contrast = SystemParameters.HighContrast;
        Set("AppCanvasBrush", contrast ? SystemColors.WindowColor : Color(dark ? "#1E1E20" : "#F5F5F7"));
        Set("AppSurfaceBrush", contrast ? SystemColors.WindowColor : Color(dark ? "#2A2A2D" : "#FFFFFF"));
        Set("AppSidebarBrush", contrast ? SystemColors.ControlColor : Color(dark ? "#252527" : "#ECECEE"));
        Set("AppTextBrush", contrast ? SystemColors.WindowTextColor : Color(dark ? "#F5F5F7" : "#1D1D1F"));
        Set("AppSecondaryBrush", contrast ? SystemColors.WindowTextColor : Color(dark ? "#AEAEB4" : "#63636A"));
        Set("AppBorderBrush", contrast ? SystemColors.WindowTextColor : Color(dark ? "#414145" : "#DADADD"));
        Set("AppAccentBrush", contrast ? SystemColors.HighlightColor : Color(dark ? "#0A84FF" : "#007AFF"));
        Set("AppOnAccentBrush", contrast ? SystemColors.HighlightTextColor : Colors.White);
        Set("AppAccentSoftBrush", contrast ? SystemColors.ControlColor : Color(dark ? "#183A5B" : "#E1EEFF"));
        Set("AppHoverBrush", contrast ? SystemColors.ControlColor : Color(dark ? "#363639" : "#E7E7EB"));
        Set("AppWarningBrush", contrast ? SystemColors.WindowTextColor : Color(dark ? "#FFD080" : "#8A5100"));
        Set("AppWarningSoftBrush", contrast ? SystemColors.WindowColor : Color(dark ? "#3D3322" : "#FFF4DE"));
        Set("AppSuccessBrush", contrast ? SystemColors.WindowTextColor : Color(dark ? "#66D28B" : "#247346"));
        Set("WindowCloseBrush", contrast ? SystemColors.WindowTextColor : Color("#FF5F57"));
        Set("WindowMinimizeBrush", contrast ? SystemColors.WindowTextColor : Color("#FEBC2E"));
        Set("WindowMaximizeBrush", contrast ? SystemColors.WindowTextColor : Color("#28C840"));
        Alias("TextFillColorPrimaryBrush", "AppTextBrush");
        Alias("TextFillColorSecondaryBrush", "AppSecondaryBrush");
        Alias("TextFillColorTertiaryBrush", "AppSecondaryBrush");
        Alias("DividerStrokeColorDefaultBrush", "AppBorderBrush");
        Alias("ControlStrokeColorDefaultBrush", "AppBorderBrush");
        Alias("ControlFillColorDefaultBrush", "AppSurfaceBrush");
        Alias("LayerFillColorDefaultBrush", "AppSurfaceBrush");
        Alias("SolidBackgroundFillColorBaseBrush", "AppCanvasBrush");
        Alias("AccentFillColorDefaultBrush", "AppAccentBrush");
        Alias("AccentFillColorSecondaryBrush", "AppAccentBrush");
        Alias("AccentFillColorTertiaryBrush", "AppAccentBrush");
        Alias("AccentTextFillColorPrimaryBrush", "AppAccentBrush");
        Alias("AccentTextFillColorSecondaryBrush", "AppAccentBrush");
    }
    private static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static void Set(string name, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[name] = brush;
    }
    private static void Alias(string name, string source) =>
        Application.Current.Resources[name] = Application.Current.Resources[source];
}
