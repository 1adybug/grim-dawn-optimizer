using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace GrimDawnOptimizer.App.Views;

public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        InitializeComponent();
    }

    private void CandidatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetailPanel is null || DetailTransform is null || !System.Windows.SystemParameters.ClientAreaAnimation) return;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        DetailPanel.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = easing });
        DetailTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = easing });
    }
}
