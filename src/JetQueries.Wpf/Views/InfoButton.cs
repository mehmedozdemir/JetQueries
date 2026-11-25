using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.ViewModels;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>
/// The small "i" beside a tile, chart or list of a DBA screen: a click opens a card explaining it to a developer who
/// has never been a DBA (what it shows, normal / watch / problem values, what to do). Shown while Settings (or
/// DBA > Açıklamalar) has the explanations on.
/// </summary>
public sealed class InfoButton : Button
{
    private Popup? _popup;

    public InfoButton()
    {
        Width = 18;
        Height = 18;
        Padding = new Thickness(0);
        Margin = new Thickness(6, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = true;
        Cursor = Cursors.Hand;
        ToolTip = Strings.HelpTip;
        SetResourceReference(StyleProperty, "Btn.Icon");
        SetResourceReference(VisibilityProperty, "DbaHelp.Visibility");
        Ui.SetIcon(this, "");
        FontSize = 12;
        Width = 18;
        Height = 18;
        Click += (_, _) => Show();
    }

    /// <summary>The topic explained (<see cref="DbaHelp"/>).</summary>
    public string? Topic
    {
        get => (string?)GetValue(TopicProperty);
        set => SetValue(TopicProperty, value);
    }

    public static readonly DependencyProperty TopicProperty = DependencyProperty.Register(nameof(Topic), typeof(string), typeof(InfoButton));

    /// <summary>The card shown last, for tests.</summary>
    internal FrameworkElement? ShownCard => _popup?.Child as FrameworkElement;

    /// <summary>Opens the explanation under the button.</summary>
    internal void Show()
    {
        if (Topic is not { } topic || DbaHelp.Get(topic) is not { } help)
        {
            return;
        }

        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = Card(help, () => _popup!.IsOpen = false),
        };
        _popup.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _popup.IsOpen = false;
            }
        };
        _popup.IsOpen = true;
    }

    /// <summary>The explanation card: title, what it shows, how to read it (normal / watch / problem), what to do.</summary>
    internal static FrameworkElement Card(DbaHelpTopic help, Action close)
    {
        ArgumentNullException.ThrowIfNull(help);
        var stack = new StackPanel();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var closeButton = new Button { Width = 24, Height = 24, ToolTip = Strings.Dismiss };
        closeButton.SetResourceReference(StyleProperty, "Btn.Icon");
        Ui.SetIcon(closeButton, Icons.Clear);
        closeButton.Click += (_, _) => close();
        DockPanel.SetDock(closeButton, Dock.Right);
        head.Children.Add(closeButton);
        var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        var glyph = new TextBlock { Text = "", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
        badge.Child = glyph;
        head.Children.Add(badge);
        var title = new TextBlock { Text = help.Title, FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        head.Children.Add(title);
        stack.Children.Add(head);

        stack.Children.Add(Heading(Strings.HelpWhat));
        stack.Children.Add(Paragraph(help.What));

        if (help.HasLevels)
        {
            stack.Children.Add(Heading(Strings.HelpLevels));
            var levels = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            levels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            levels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var row = 0;
            foreach (var (text, label, icon, brush) in new[]
            {
                (help.Good, Strings.HelpGood, "", "Brush.Success"),
                (help.Watch, Strings.HelpWatch, "", "Brush.Warning"),
                (help.Bad, Strings.HelpBad, "", "Brush.Danger"),
            })
            {
                if (text is null)
                {
                    continue;
                }

                levels.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // A status is never colour alone: an icon and its word go with it.
                var tag = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 12, 3), VerticalAlignment = VerticalAlignment.Top };
                var mark = new TextBlock { Text = icon, FontSize = 12, Margin = new Thickness(0, 2, 6, 0) };
                mark.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
                mark.SetResourceReference(TextBlock.ForegroundProperty, brush);
                tag.Children.Add(mark);
                tag.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
                Grid.SetRow(tag, row);
                levels.Children.Add(tag);
                var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
                body.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                Grid.SetRow(body, row);
                Grid.SetColumn(body, 1);
                levels.Children.Add(body);
                row++;
            }

            stack.Children.Add(levels);
        }

        stack.Children.Add(Heading(Strings.HelpAction));
        stack.Children.Add(Paragraph(help.Action, last: true));

        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560 };
        var card = new Border
        {
            Child = scroll,
            Width = 440,
            Padding = new Thickness(16, 12, 12, 14),
            Margin = new Thickness(0, 0, 12, 12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.28 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        card.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        return card;
    }

    private static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text.ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        return heading;
    }

    private static TextBlock Paragraph(string text, bool last = false) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, last ? 0 : 12), LineHeight = 19 };
}

/// <summary>The help topic of the performance analysis' shown part.</summary>
public sealed class PerformanceHelpTopicConverter : IValueConverter
{
    public static PerformanceHelpTopicConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        PerformanceSection.MissingIndexes => DbaHelp.PerfMissing,
        PerformanceSection.IndexUsage => DbaHelp.PerfUsage,
        PerformanceSection.Fragmentation => DbaHelp.PerfFragmentation,
        _ => DbaHelp.PerfHeavy,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
