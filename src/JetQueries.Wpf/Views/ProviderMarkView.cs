using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace JetQueries.Wpf.Views;

/// <summary>
/// The mark of a database type: its logo in the type's colour on a light rounded tile (the same in light and dark
/// themes, like an application icon), so that SQL Server, Oracle and Redis, all red, still differ by shape.
/// </summary>
public sealed class ProviderMarkView : Border
{
    public static readonly DependencyProperty MarkProperty = DependencyProperty.Register(
        nameof(Mark), typeof(ProviderMark), typeof(ProviderMarkView), new PropertyMetadata(null, (d, _) => ((ProviderMarkView)d).Update()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ProviderMarkView), new PropertyMetadata(16d, (d, _) => ((ProviderMarkView)d).Update()));

    private static readonly Dictionary<string, Geometry?> Geometries = new(StringComparer.Ordinal);

    private static readonly SolidColorBrush Tile = Frozen(Color.FromRgb(0xF7, 0xF8, 0xFA));

    private static readonly SolidColorBrush Edge = Frozen(Color.FromArgb(0x26, 0x00, 0x00, 0x00));

    private readonly Path _shape = new();

    public ProviderMarkView()
    {
        // The mark is drawn on its 24 x 24 canvas, scaled as a whole, so every mark keeps its place in the tile.
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(_shape);
        Child = new Viewbox { Child = canvas };
        SnapsToDevicePixels = true;
        Update();
    }

    /// <summary>What to draw; the default mark when null.</summary>
    public ProviderMark? Mark
    {
        get => (ProviderMark?)GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    /// <summary>Width and height of the tile.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private void Update()
    {
        var mark = Mark ?? ProviderMark.Default;
        Width = Height = Size;
        CornerRadius = new CornerRadius(Math.Round(Size * 0.24));
        Padding = new Thickness(Math.Max(1, Math.Round(Size * 0.1)));
        Background = Tile;
        BorderBrush = Edge;
        BorderThickness = new Thickness(Size >= 24 ? 1 : 0.5);
        _shape.Fill = Brush(mark.Color);

        // A mark an extension got wrong falls back to the default one rather than failing the view.
        _shape.Data = GeometryOf(mark.PathData) ?? GeometryOf(ProviderMark.Default.PathData);
    }

    private static SolidColorBrush Brush(string color)
    {
        var value = uint.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var brush = new SolidColorBrush(Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Geometry? GeometryOf(string data)
    {
        if (!Geometries.TryGetValue(data, out var geometry))
        {
            try
            {
                geometry = Geometry.Parse(data);
                geometry.Freeze();
            }
            catch (FormatException)
            {
                geometry = null;
            }
            catch (InvalidOperationException)
            {
                geometry = null;
            }

            Geometries[data] = geometry;
        }

        return geometry;
    }
}
