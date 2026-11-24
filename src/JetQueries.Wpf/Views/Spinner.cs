using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace JetQueries.Wpf.Views;

/// <summary>
/// A glyph that turns while it is visible. The animation is started and stopped in code: a storyboard started by a
/// style trigger keeps running when its row is removed from the tree, and a running animation keeps the
/// dispatcher from ever being idle.
/// </summary>
public sealed class Spinner : TextBlock
{
    private readonly RotateTransform _turn = new();

    public Spinner()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _turn;
        IsVisibleChanged += (_, _) => Update();
        Unloaded += (_, _) => _turn.BeginAnimation(RotateTransform.AngleProperty, null);
        Loaded += (_, _) => Update();
    }

    private void Update()
    {
        if (IsVisible && IsLoaded)
        {
            _turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            _turn.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }
}
