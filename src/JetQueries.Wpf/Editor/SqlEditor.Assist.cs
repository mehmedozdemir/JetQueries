using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using JetQueries.Sdk;

namespace JetQueries.Wpf.Editor;

/// <summary>
/// The editor's assistance: the completion list's look, signature help for the call the caret is in, the
/// Ctrl+hover link on names, and a "finding…" chip while Ctrl+Click looks an object up.
/// </summary>
public partial class SqlEditor
{
    private Popup? _signaturePopup;
    private SignatureHelp? _signature;
    private int _signatureIndex;
    private int _signatureVersion;
    private DispatcherTimer? _signatureTimer;
    private TextSpan? _link;
    private Border? _busyChip;
    private TextBlock? _busyText;
    private Views.Spinner? _busySpinner;

    /// <summary>
    /// Supplies signature help for a caret position (the call around it). Called on the UI thread; it must not
    /// block. Without it no parameter help is shown.
    /// </summary>
    public Func<string, int, CancellationToken, Task<SignatureHelp?>>? SignatureSource { get; set; }

    /// <summary>The open completion window, for tests.</summary>
    internal CompletionWindow? CompletionWindowShown => _completionWindow;

    /// <summary>The signature card shown now, for tests.</summary>
    internal FrameworkElement? SignatureCardShown => _signaturePopup is { IsOpen: true, Child: FrameworkElement card } ? card : null;

    /// <summary>Puts the caret at an offset, for tests.</summary>
    internal void MoveCaret(int offset) => _editor.CaretOffset = offset;

    /// <summary>Draws a link on a span as Ctrl+hover does, for tests.</summary>
    internal void ShowLink(TextSpan? span) => SetLink(span);

    /// <summary>The signature help shown now, for tests; null when closed.</summary>
    public SignatureHelp? ShownSignature => _signaturePopup is { IsOpen: true } ? _signature : null;

    /// <summary>The name underlined for Ctrl+Click, for tests.</summary>
    public TextSpan? LinkSpan => _link;

    /// <summary>The busy chip's text, for tests; null when hidden.</summary>
    public string? BusyShown => _busyChip is { Visibility: Visibility.Visible } ? _busyText!.Text : null;

    /// <summary>The text of the "finding…" chip at the top right; null hides it.</summary>
    public static readonly DependencyProperty BusyTextProperty = DependencyProperty.Register(
        nameof(BusyText), typeof(string), typeof(SqlEditor), new PropertyMetadata(null, (d, _) => ((SqlEditor)d).UpdateBusy()));

    /// <summary>Whether the chip turns (looking something up) or just says something (not found).</summary>
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(SqlEditor), new PropertyMetadata(false, (d, _) => ((SqlEditor)d).UpdateBusy()));

    public string? BusyText
    {
        get => (string?)GetValue(BusyTextProperty);
        set => SetValue(BusyTextProperty, value);
    }

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    /// <summary>Asks for signature help at the caret now (Ctrl+Shift+Space).</summary>
    public Task ShowSignatureHelpAsync() => RequestSignatureAsync();

    /// <summary>The editor and the chip over it; called once from the constructor.</summary>
    private void SetUpAssist()
    {
        _busyText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        _busySpinner = new Views.Spinner { Margin = new Thickness(0, 0, 8, 0) };
        _busySpinner.SetResourceReference(StyleProperty, "Spinner");
        var chipContent = new StackPanel { Orientation = Orientation.Horizontal };
        chipContent.Children.Add(_busySpinner);
        chipContent.Children.Add(_busyText);
        _busyChip = new Border
        {
            Child = chipContent,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 12, 4),
            Margin = new Thickness(0, 6, 22, 0),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _busyChip.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        _busyChip.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        _busyText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");

        var grid = new Grid();
        grid.Children.Add(_editor);
        grid.Children.Add(_busyChip);
        Content = grid;

        _editor.TextArea.TextView.LineTransformers.Add(new LinkColorizer(this));
        _editor.TextArea.PreviewMouseMove += (_, e) => UpdateLink(e);
        _editor.TextArea.MouseLeave += (_, _) => SetLink(null);
        _editor.TextArea.Caret.PositionChanged += (_, _) => OnCaretMovedForSignature();
        _editor.TextArea.LostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is not DependencyObject next || !IsInside(next))
            {
                CloseSignature();
            }
        };
        PreviewKeyUp += (_, e) =>
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            {
                SetLink(null);
            }
        };
    }

    private bool IsInside(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, this))
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateBusy()
    {
        if (_busyChip is null)
        {
            return;
        }

        _busyText!.Text = BusyText ?? string.Empty;
        _busySpinner!.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        _busyChip.Visibility = string.IsNullOrEmpty(BusyText) ? Visibility.Collapsed : Visibility.Visible;
        _editor.TextArea.Cursor = IsBusy ? Cursors.AppStarting : null;
    }

    // ------------------------------------------------------------------ Ctrl+hover link

    /// <summary>With Ctrl held, the name under the mouse is drawn as a link (underlined, accent, hand cursor).</summary>
    private void UpdateLink(MouseEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || LanguageService is null)
        {
            SetLink(null);
            return;
        }

        SetLink(OffsetAt(e) is { } offset ? NameAt(offset) : null);
    }

    /// <summary>Pressing Ctrl over a name shows its link at once (without moving the mouse).</summary>
    private void UpdateLinkFromKeyboard()
    {
        if (!_editor.TextArea.IsMouseOver)
        {
            return;
        }

        var view = _editor.TextArea.TextView;
        var position = view.GetPositionFloor(Mouse.GetPosition(view) + view.ScrollOffset);
        SetLink(position is { } found ? NameAt(_editor.Document.GetOffset(found.Location)) : null);
    }

    /// <summary>The identifier at an offset (the one a Ctrl+Click would open); null for keywords, strings, space.</summary>
    internal TextSpan? NameAt(int offset)
    {
        if (LanguageService is not { } service || offset < 0 || offset > _editor.Document.TextLength)
        {
            return null;
        }

        var line = _editor.Document.GetLineByOffset(offset);
        var tokens = service.TokenizeLine(_editor.Document.GetText(line), _colorizer.StateBefore(line.LineNumber)).Tokens;
        foreach (var token in tokens)
        {
            var start = line.Offset + token.Span.Start;
            if (token.Kind == SqlTokenKind.Identifier && start <= offset && offset < start + token.Span.Length)
            {
                return new TextSpan(start, token.Span.Length);
            }
        }

        return null;
    }

    private void SetLink(TextSpan? link)
    {
        if (link == _link)
        {
            return;
        }

        var old = _link;
        _link = link;
        _editor.TextArea.Cursor = link is null ? (IsBusy ? Cursors.AppStarting : null) : Cursors.Hand;
        foreach (var span in new[] { old, link })
        {
            if (span is { } s)
            {
                _editor.TextArea.TextView.Redraw(s.Start, s.Length, DispatcherPriority.Render);
            }
        }
    }

    /// <summary>Draws the Ctrl+hover link: underline and the accent colour.</summary>
    private sealed class LinkColorizer(SqlEditor owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (owner._link is not { } link || link.End <= line.Offset || link.Start >= line.EndOffset)
            {
                return;
            }

            var brush = owner.TryFindResource("Brush.Accent") as Brush ?? Brushes.DodgerBlue;
            ChangeLinePart(Math.Max(link.Start, line.Offset), Math.Min(link.End, line.EndOffset), element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
                element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
            });
        }
    }

    // ------------------------------------------------------------------ signature help

    private void OnTextEnteredForAssist(string text)
    {
        if (text is "(" or ",")
        {
            _ = RequestSignatureAsync();

            // A parameter that takes fixed values (a datepart, a format) offers them at once.
            _ = RequestCompletionAsync(valuesOnly: true);
        }
        else if (text is ")")
        {
            _ = RequestSignatureAsync();
        }
    }

    // While the help is open it follows the caret (another argument, out of the call); a short pause first.
    private void OnCaretMovedForSignature()
    {
        if (_signaturePopup is not { IsOpen: true })
        {
            return;
        }

        _signatureTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, (_, _) =>
        {
            _signatureTimer!.Stop();
            _ = RequestSignatureAsync();
        }, Dispatcher);
        _signatureTimer.Stop();
        _signatureTimer.Start();
    }

    private async Task RequestSignatureAsync()
    {
        if (SignatureSource is not { } source)
        {
            return;
        }

        var version = ++_signatureVersion;
        var offset = _editor.CaretOffset;
        SignatureHelp? help;
        try
        {
            help = await source(_editor.Document.Text, offset, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // Help is a convenience: a failing language service must never disturb typing.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        if (version != _signatureVersion)
        {
            return;
        }

        if (help is null || help.Signatures.Count == 0)
        {
            CloseSignature();
            return;
        }

        // The same call keeps the overload the user picked with ↑↓.
        _signatureIndex = _signature is { } previous && previous.CallStart == help.CallStart && previous.Signatures.Count == help.Signatures.Count
            ? _signatureIndex
            : help.ActiveSignature;
        _signature = help;
        ShowSignature();
    }

    private void ShowSignature()
    {
        if (_signature is not { } help)
        {
            return;
        }

        _signaturePopup ??= CreateSignaturePopup();
        var index = Math.Clamp(_signatureIndex, 0, help.Signatures.Count - 1);
        _signaturePopup.Child = SignatureCard(help, index);

        // Above the line of the call's name, starting at the name.
        var view = _editor.TextArea.TextView;
        var location = _editor.Document.GetLocation(Math.Clamp(help.CallStart, 0, _editor.Document.TextLength));
        var point = view.GetVisualPosition(new TextViewPosition(location), VisualYPosition.LineTop) - view.ScrollOffset;
        var inEditor = view.TranslatePoint(point, this);
        _signaturePopup.PlacementTarget = this;
        _signaturePopup.Placement = PlacementMode.Relative;
        _signaturePopup.HorizontalOffset = Math.Max(0, inEditor.X - 8);
        _signaturePopup.VerticalOffset = inEditor.Y;
        _signaturePopup.IsOpen = true;

        // Popups measure late: lift it above the line once its height is known.
        Dispatcher.BeginInvoke(() =>
        {
            if (_signaturePopup is { IsOpen: true, Child: FrameworkElement card })
            {
                _signaturePopup.VerticalOffset = inEditor.Y - card.ActualHeight - 2;
            }
        }, DispatcherPriority.Loaded);
    }

    private static Popup CreateSignaturePopup() => new()
    {
        AllowsTransparency = true,
        StaysOpen = true,
        Focusable = false,
        IsHitTestVisible = false,
        PopupAnimation = PopupAnimation.Fade,
    };

    /// <summary>The help card: NAME(param, PARAM, param) → type, then the active parameter and what the routine does.</summary>
    private static Border SignatureCard(SignatureHelp help, int index)
    {
        var signature = help.Signatures[index];
        var active = Math.Min(help.ActiveParameter, Math.Max(0, signature.Parameters.Count - 1));
        var code = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };
        code.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Code");
        code.FontSize = 12.5;

        static Run Part(string text, string brushKey, bool bold = false)
        {
            var run = new Run(text) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
            return run;
        }

        if (help.Signatures.Count > 1)
        {
            code.Inlines.Add(Part($"▲▼ {index + 1}/{help.Signatures.Count}  ", "Brush.TextMuted"));
        }

        code.Inlines.Add(Part(signature.Name, "Brush.Syntax.Function", bold: true));
        code.Inlines.Add(Part("(", "Brush.TextSecondary"));
        for (var i = 0; i < signature.Parameters.Count; i++)
        {
            var parameter = signature.Parameters[i];
            if (i > 0)
            {
                code.Inlines.Add(Part(", ", "Brush.TextSecondary"));
            }

            var label = (parameter.Direction is { } direction ? direction + " " : string.Empty) + parameter.Name +
                        (parameter.DataType is { } type ? " " + type : string.Empty);
            if (parameter.IsOptional)
            {
                label = "[" + label + "]";
            }

            if (i == active)
            {
                var run = Part(label, "Brush.Accent", bold: true);
                run.TextDecorations = TextDecorations.Underline;
                code.Inlines.Add(run);
            }
            else
            {
                code.Inlines.Add(Part(label, "Brush.Text"));
            }
        }

        if (signature.IsVariadic)
        {
            code.Inlines.Add(Part(", …", "Brush.TextSecondary"));
        }

        code.Inlines.Add(Part(")", "Brush.TextSecondary"));
        if (signature.ReturnType is { } returns)
        {
            code.Inlines.Add(Part("  → " + returns, "Brush.Syntax.Type"));
        }

        var stack = new StackPanel();
        stack.Children.Add(code);

        // The parameter under the caret: its values, if it takes a fixed set.
        if (active < signature.Parameters.Count && signature.Parameters[active] is { Values.Count: > 0 } withValues)
        {
            var values = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 620, FontSize = 11.5 };
            values.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            values.Text = withValues.Name + ": " + string.Join(", ", withValues.Values.Take(12).Select(static v => v.Text)) + (withValues.Values.Count > 12 ? ", …" : string.Empty);
            stack.Children.Add(values);
        }

        if (signature.Documentation is { Length: > 0 } documentation)
        {
            var doc = new TextBlock { Text = documentation, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 620, FontSize = 12 };
            doc.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
            stack.Children.Add(doc);
        }

        var card = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 9),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.25 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        return card;
    }

    /// <summary>↑↓ change the overload while the help is open (and no list is); Esc closes it.</summary>
    private bool HandleSignatureKey(Key key)
    {
        if (_signaturePopup is not { IsOpen: true } || _signature is not { } help)
        {
            return false;
        }

        if (key == Key.Escape && _completionWindow is null)
        {
            CloseSignature();
            return true;
        }

        if (help.Signatures.Count > 1 && _completionWindow is null && key is Key.Up or Key.Down)
        {
            _signatureIndex = (_signatureIndex + (key == Key.Down ? 1 : help.Signatures.Count - 1)) % help.Signatures.Count;
            ShowSignature();
            return true;
        }

        return false;
    }

    private void CloseSignature()
    {
        _signatureVersion++;
        _signature = null;
        if (_signaturePopup is not null)
        {
            _signaturePopup.IsOpen = false;
        }
    }

    // ------------------------------------------------------------------ completion list look

    /// <summary>The completion window as a card: rounded, shadowed, rows with a coloured kind badge, a hint line.</summary>
    private void StyleCompletionWindow(CompletionWindow window)
    {
        var list = window.CompletionList;
        window.Content = null;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        window.BorderThickness = new Thickness(0);
        window.Width = 460;
        window.SizeToContent = SizeToContent.Height;

        list.ListBox.ItemContainerStyle = (Style)FindResource("Completion.Row");
        list.ListBox.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        list.ListBox.ItemTemplate = (DataTemplate)FindResource("Completion.Item");
        list.ListBox.Padding = new Thickness(4);
        list.ListBox.MaxHeight = 300;
        ScrollViewer.SetHorizontalScrollBarVisibility(list.ListBox, ScrollBarVisibility.Disabled);

        var hint = new TextBlock { Text = Strings.CompletionHint, Margin = new Thickness(10, 4, 10, 6), FontSize = 11 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = hint };
        line.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        DockPanel.SetDock(line, Dock.Bottom);
        var dock = new DockPanel();
        dock.Children.Add(line);
        dock.Children.Add(list);

        var card = new Border
        {
            Child = dock,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.28 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        window.Content = card;
    }

    /// <summary>The glyph and the colour (a resource key) of a kind's badge in the list.</summary>
    internal static (string Glyph, string Brush) KindBadge(CompletionItemKind kind) => kind switch
    {
        CompletionItemKind.Table => ("", "Brush.Accent"),
        CompletionItemKind.View => ("", "Brush.Info"),
        CompletionItemKind.Column => ("", "Brush.Syntax.Type"),
        CompletionItemKind.Function => ("", "Brush.Syntax.Function"),
        CompletionItemKind.Procedure => ("", "Brush.Syntax.Function"),
        CompletionItemKind.Package => ("", "Brush.Warning"),
        CompletionItemKind.Sequence => ("", "Brush.Syntax.Number"),
        CompletionItemKind.Schema => ("", "Brush.TextSecondary"),
        CompletionItemKind.Database => ("", "Brush.TextSecondary"),
        CompletionItemKind.Alias => ("", "Brush.Success"),
        CompletionItemKind.Variable => ("", "Brush.Syntax.Number"),
        CompletionItemKind.Snippet => ("", "Brush.Warning"),
        CompletionItemKind.Value => ("", "Brush.Syntax.String"),
        _ => ("", "Brush.Syntax.Keyword"),
    };

    /// <summary>One row of the completion list: a kind badge, the name, what it is; a card beside it for the selected one.</summary>
    private sealed class CompletionData(SqlEditor owner, CompletionItem item) : ICompletionData
    {
        public ImageSource? Image => null;

        public string Text => item.Label;

        public object Content
        {
            get
            {
                var (glyph, brush) = KindBadge(item.Kind);
                var badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 9, 0) };
                var icon = new TextBlock
                {
                    Text = glyph, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
                icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
                badge.Child = icon;
                badge.SetResourceReference(Border.BackgroundProperty, "Brush.Chip");

                var detail = new TextBlock
                {
                    Text = item.Detail ?? KindName(item.Kind), Margin = new Thickness(14, 0, 2, 0), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center,
                };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
                var label = new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                label.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Code");

                var panel = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(badge, Dock.Left);
                DockPanel.SetDock(detail, Dock.Right);
                panel.Children.Add(badge);
                panel.Children.Add(detail);
                panel.Children.Add(label);
                return panel;
            }
        }

        /// <summary>The card beside the list: the full signature or type, and what it does.</summary>
        public object? Description
        {
            get
            {
                if (item.Documentation is null && item.Detail is null)
                {
                    return null;
                }

                var stack = new StackPanel { MaxWidth = 380 };
                var title = new TextBlock { Text = item.Detail ?? item.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Code");
                stack.Children.Add(title);
                if (item.Documentation is { Length: > 0 } documentation)
                {
                    var doc = new TextBlock { Text = documentation, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
                    doc.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                    stack.Children.Add(doc);
                }

                return stack;
            }
        }

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            var insert = item.InsertText ?? item.Label;
            textArea.Document.Replace(completionSegment, insert);

            // A function goes in with its parentheses; the caret goes between them and its parameters are shown.
            if (insert.EndsWith("()", StringComparison.Ordinal))
            {
                if (item.OpensCall)
                {
                    textArea.Caret.Offset = completionSegment.Offset + insert.Length - 1;
                    owner.Dispatcher.BeginInvoke(() =>
                    {
                        _ = owner.RequestSignatureAsync();
                        _ = owner.RequestCompletionAsync(valuesOnly: true);
                    }, DispatcherPriority.Background);
                }
            }
        }

        private static string KindName(CompletionItemKind kind) => kind switch
        {
            CompletionItemKind.Keyword => string.Empty,
            var other => Strings.Find("CompletionKind_" + other) ?? other.ToString().ToLowerInvariant(),
        };
    }
}
