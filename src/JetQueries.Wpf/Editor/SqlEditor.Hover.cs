using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.ViewModels;
using UiKit.Controls;

namespace JetQueries.Wpf.Editor;

/// <summary>
/// The card shown when the mouse rests on a table, view or column (<see cref="HoverSource"/>): the object and its
/// kind, a table's columns with their types and keys, a column's type, table and flags.
/// </summary>
public partial class SqlEditor
{
    // Only the newest hover shows its card: an older answer, or one after the mouse moved on, is dropped.
    private int _hoverVersion;

    // The card is a popup the mouse can enter (a tooltip closes the moment the mouse leaves the word): it stays
    // while the mouse is on the word or on the card, and closes a moment after the mouse left both.
    private Popup? _hoverPopup;
    private DispatcherTimer? _hoverCloseTimer;

    /// <summary>The card shown last, for tests.</summary>
    internal SqlHoverInfo? LastHover { get; private set; }

    /// <summary>The open hover card, for tests.</summary>
    internal FrameworkElement? HoverCardShown => _hoverPopup is { IsOpen: true, Child: FrameworkElement card } ? card : null;

    /// <summary>Looks up the name at <paramref name="offset"/> and shows its card at the mouse.</summary>
    internal async Task ShowHoverAsync(int offset)
    {
        if (HoverSource is not { } source)
        {
            return;
        }

        var version = ++_hoverVersion;
        SqlHoverInfo? info;
        try
        {
            info = await source(_editor.Document.Text, offset, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version != _hoverVersion || info is null)
        {
            return;
        }

        // The mouse is reading the card already: a hover behind it does not replace it.
        if (_hoverPopup is { IsOpen: true, Child: UIElement { IsMouseOver: true } })
        {
            return;
        }

        LastHover = info;
        _toolTip.IsOpen = false;
        _hoverPopup ??= CreateHoverPopup();
        _hoverPopup.IsOpen = false;
        _hoverPopup.Child = HoverCard(info, () =>
        {
            CloseHover();
            NavigationRequested?.Invoke(this, offset);
        });
        _hoverPopup.PlacementTarget = this;
        _hoverPopup.IsOpen = true;
        _hoverCloseTimer?.Stop();
    }

    /// <summary>The mouse left the word: the card closes soon unless the mouse reaches it.</summary>
    private void StopHover()
    {
        _hoverVersion++;
        _toolTip.IsOpen = false;
        if (_hoverPopup is { IsOpen: true })
        {
            _hoverCloseTimer ??= CreateHoverCloseTimer();
            _hoverCloseTimer.Stop();
            _hoverCloseTimer.Start();
        }
    }

    /// <summary>Closes the card at once (typing, a click, scrolling, Esc, the window left).</summary>
    internal void CloseHover()
    {
        _hoverVersion++;
        _hoverCloseTimer?.Stop();
        if (_hoverPopup is { IsOpen: true })
        {
            _hoverPopup.IsOpen = false;
        }
    }

    private DispatcherTimer CreateHoverCloseTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_hoverPopup is { IsOpen: true, Child: UIElement { IsMouseOver: false } })
            {
                _hoverPopup.IsOpen = false;
            }
        };
        return timer;
    }

    private Popup CreateHoverPopup()
    {
        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            Placement = PlacementMode.Mouse,
            PopupAnimation = PopupAnimation.Fade,
        };

        // Leaving the card closes it a moment later (back on a word, a new hover takes over).
        popup.MouseLeave += (_, _) => StopHover();
        popup.MouseEnter += (_, _) => _hoverCloseTimer?.Stop();
        return popup;
    }

    /// <summary>The card in its frame: scrolls when a table has many columns, with a link to the object's details.</summary>
    internal static Border HoverCard(SqlHoverInfo info, Action? openDetails)
    {
        var body = Card(info);
        var content = new DockPanel();
        if (openDetails is not null)
        {
            var link = new Hyperlink(new Run(Strings.HoverOpenDetails)) { TextDecorations = null, Cursor = Cursors.Hand, Focusable = false };
            link.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent");
            link.Click += (_, _) => openDetails();
            var footer = new TextBlock(link) { Margin = new Thickness(2, 8, 2, 0), FontSize = 11.5 };
            DockPanel.SetDock(footer, Dock.Bottom);
            content.Children.Add(footer);
        }

        content.Children.Add(new ScrollViewer
        {
            Content = body,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        var card = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.25 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        card.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        return card;
    }

    internal static StackPanel Card(SqlHoverInfo info)
    {
        var stack = new StackPanel { MaxWidth = 460, Margin = new Thickness(2) };

        // "TABLO  dbo.Cards"
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var kind = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        kind.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        var kindText = new TextBlock { Text = info.Kind.ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        kindText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
        kind.Child = kindText;
        DockPanel.SetDock(kind, Dock.Left);
        head.Children.Add(kind);
        var title = new TextBlock { Text = info.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Code");
        head.Children.Add(title);
        stack.Children.Add(head);

        if (info.Subtitle is { Length: > 0 } subtitle)
        {
            var sub = new TextBlock { Text = subtitle, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            sub.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Code");
            stack.Children.Add(sub);
        }

        // A column: its key and nullability.
        if (info.Column is { } column)
        {
            var flags = new StringBuilder(column.IsNullable ? Strings.HoverNullable : Strings.HoverNotNull);
            if (column.IsPrimaryKey)
            {
                flags.Insert(0, Strings.HoverPrimaryKey + " · ");
            }

            var text = new TextBlock { Text = flags.ToString(), Margin = new Thickness(0, 3, 0, 0), FontSize = 11.5 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
            stack.Children.Add(text);
        }

        // A table: its columns, key ones marked.
        if (info.Columns.Count > 0)
        {
            var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < info.Columns.Count; i++)
            {
                var c = info.Columns[i];
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (c.IsPrimaryKey)
                {
                    var key = new TextBlock { Text = Icons.Key, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                    key.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
                    key.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warning");
                    Grid.SetRow(key, i);
                    grid.Children.Add(key);
                }

                var name = new TextBlock { Text = c.Name, Margin = new Thickness(0, 1, 14, 1) };
                name.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Code");
                Grid.SetRow(name, i);
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);
                var type = new TextBlock { Text = c.DataType + (c.IsNullable ? string.Empty : "  NOT NULL"), Margin = new Thickness(0, 1, 0, 1), TextTrimming = TextTrimming.CharacterEllipsis };
                type.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
                type.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Code");
                Grid.SetRow(type, i);
                Grid.SetColumn(type, 2);
                grid.Children.Add(type);
            }

            stack.Children.Add(grid);
            if (info.MoreColumns > 0)
            {
                var more = new TextBlock
                {
                    Text = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.HoverMoreColumns), info.MoreColumns),
                    Margin = new Thickness(18, 4, 0, 0),
                    FontSize = 11.5,
                };
                more.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
                stack.Children.Add(more);
            }
        }

        return stack;
    }
}
