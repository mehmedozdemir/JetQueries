using System.Windows;
using System.Windows.Controls;
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
/// The SQL text editor: AvalonEdit with line numbers and syntax colouring driven by an
/// <see cref="ISqlLanguageService"/>. The control knows no SQL dialect itself; colours come from the brush
/// properties, which the application binds to UiKit's syntax tokens.
/// </summary>
public class SqlEditor : UserControl
{
    /// <summary>The text. Two-way: typing updates it, setting it replaces the document.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SqlEditor),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, static (d, e) => ((SqlEditor)d).OnTextPropertyChanged()));

    /// <summary>Tokenizes lines for colouring; without one the text is shown plain.</summary>
    public static readonly DependencyProperty LanguageServiceProperty = DependencyProperty.Register(
        nameof(LanguageService), typeof(ISqlLanguageService), typeof(SqlEditor),
        new PropertyMetadata(null, static (d, _) => ((SqlEditor)d).OnLanguageServiceChanged()));

    /// <summary>Colour of keywords.</summary>
    public static readonly DependencyProperty KeywordBrushProperty = BrushProperty(nameof(KeywordBrush));

    /// <summary>Colour of string literals.</summary>
    public static readonly DependencyProperty StringBrushProperty = BrushProperty(nameof(StringBrush));

    /// <summary>Colour of comments (drawn in italics).</summary>
    public static readonly DependencyProperty CommentBrushProperty = BrushProperty(nameof(CommentBrush));

    /// <summary>Colour of numbers.</summary>
    public static readonly DependencyProperty NumberBrushProperty = BrushProperty(nameof(NumberBrush));

    /// <summary>Colour of parameters and bind variables.</summary>
    public static readonly DependencyProperty ParameterBrushProperty = BrushProperty(nameof(ParameterBrush));

    /// <summary>Colour of function names.</summary>
    public static readonly DependencyProperty FunctionBrushProperty = BrushProperty(nameof(FunctionBrush));

    /// <summary>Colour of data type names.</summary>
    public static readonly DependencyProperty TypeBrushProperty = BrushProperty(nameof(TypeBrush));

    /// <summary>Colour of operators and punctuation.</summary>
    public static readonly DependencyProperty OperatorBrushProperty = BrushProperty(nameof(OperatorBrush));

    /// <summary>Colour of the wavy underline under syntax errors.</summary>
    public static readonly DependencyProperty ErrorBrushProperty = DependencyProperty.Register(
        nameof(ErrorBrush), typeof(Brush), typeof(SqlEditor),
        new PropertyMetadata(null, static (d, _) => ((SqlEditor)d)._editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection)));

    /// <summary>Colour of the line numbers.</summary>
    public static readonly DependencyProperty LineNumberBrushProperty = DependencyProperty.Register(
        nameof(LineNumberBrush), typeof(Brush), typeof(SqlEditor),
        new PropertyMetadata(null, static (d, e) => ((SqlEditor)d)._editor.LineNumbersForeground = e.NewValue as Brush ?? Brushes.Gray));

    /// <summary>Background of the selected text.</summary>
    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(SqlEditor),
        new PropertyMetadata(null, static (d, e) => ((SqlEditor)d)._editor.TextArea.SelectionBrush = e.NewValue as Brush));

    /// <summary>Zoom factor of the text: 1 is the editor font size; Ctrl+wheel and Ctrl+0 change it.</summary>
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(SqlEditor),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, static (d, _) => ((SqlEditor)d).UpdateFont(), static (_, v) => ClampZoom((double)v)));

    /// <summary>Smallest and largest zoom.</summary>
    public const double MinZoom = 0.5;

    /// <inheritdoc cref="MinZoom" />
    public const double MaxZoom = 4;

    private readonly TextEditor _editor;
    private readonly SqlColorizer _colorizer;
    private readonly DispatcherTimer _diagnosticTimer;
    private readonly ToolTip _toolTip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    private IReadOnlyList<SqlDiagnostic> _diagnostics = [];
    private int _diagnosticVersion;
    private bool _updatingText;
    private (int Start, int Length)? _flash;
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private CompletionWindow? _completionWindow;
    private int _completionVersion;

    /// <summary>Creates the editor.</summary>
    public SqlEditor()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = true,
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6, 4, 0, 0),
        };
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 4;
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.HighlightCurrentLine = false;
        _editor.TextArea.SelectionCornerRadius = 2;
        _editor.TextArea.SelectionBorder = null;
        _editor.SetBinding(ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        _editor.SetBinding(FontFamilyProperty, new System.Windows.Data.Binding(nameof(FontFamily)) { Source = this });
        UpdateFont();
        PreviewMouseWheel += OnPreviewMouseWheel;

        _colorizer = new SqlColorizer(this);
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _editor.Document.Changed += OnDocumentChanged;
        _editor.TextChanged += OnEditorTextChanged;
        _editor.TextArea.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _editor.TextArea.TextEntered += OnTextEntered;
        PreviewKeyDown += OnPreviewKeyDown;

        var view = _editor.TextArea.TextView;
        view.BackgroundRenderers.Add(new SquiggleRenderer(this));
        view.BackgroundRenderers.Add(new FlashRenderer(this));
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _flash = null;
            _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        };
        _editor.MouseHover += OnMouseHover;
        _editor.MouseHoverStopped += (_, _) => _toolTip.IsOpen = false;
        _editor.TextArea.PreviewMouseLeftButtonDown += OnPreviewMouseDown;

        // A right click outside the selection moves the caret there first, so the menu acts where it was opened.
        _editor.TextArea.PreviewMouseRightButtonDown += OnPreviewRightButtonDown;

        // Errors are looked for once typing pauses, not on every key.
        _diagnosticTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _diagnosticTimer.Tick += (_, _) =>
        {
            _diagnosticTimer.Stop();
            _ = RefreshDiagnosticsAsync();
        };
        Content = _editor;
    }

    /// <summary>
    /// Supplies the syntax errors of a text. Called on the UI thread when typing pauses; it must not block.
    /// Without it nothing is underlined.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<SqlDiagnostic>>>? DiagnosticSource { get; set; }

    /// <summary>
    /// Supplies completion items for a caret position: the whole text, the caret offset and a token that is
    /// canceled when the request is no longer wanted. Called on the UI thread; it must not block. Without it
    /// the editor offers no completion.
    /// </summary>
    public Func<string, int, CancellationToken, Task<IReadOnlyList<CompletionItem>>>? CompletionSource { get; set; }

    /// <summary>Raised with a text offset when the user asks to go to the definition of what is there (Ctrl+Click, F12).</summary>
    public event EventHandler<int>? NavigationRequested;

    /// <summary>Raised when the selection changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The errors currently underlined.</summary>
    public IReadOnlyList<SqlDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Whether the completion list is showing.</summary>
    public bool IsCompletionOpen => _completionWindow is not null;

    /// <summary>The labels in the completion list, in order; empty when it is not showing.</summary>
    public IReadOnlyList<string> CompletionLabels =>
        _completionWindow is null ? [] : [.. _completionWindow.CompletionList.CompletionData.Select(static d => d.Text)];

    /// <summary>Zoom factor of the text (1 = 100 %).</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>One zoom step in or out from <paramref name="zoom"/>: 10 % steps, as in Visual Studio.</summary>
    public static double StepZoom(double zoom, bool zoomIn) => ClampZoom(Math.Round((zoom + (zoomIn ? 0.1 : -0.1)) * 10) / 10);

    /// <summary>The text.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The language service used for colouring.</summary>
    public ISqlLanguageService? LanguageService
    {
        get => (ISqlLanguageService?)GetValue(LanguageServiceProperty);
        set => SetValue(LanguageServiceProperty, value);
    }

    /// <summary>Colour of keywords.</summary>
    public Brush? KeywordBrush { get => (Brush?)GetValue(KeywordBrushProperty); set => SetValue(KeywordBrushProperty, value); }

    /// <summary>Colour of string literals.</summary>
    public Brush? StringBrush { get => (Brush?)GetValue(StringBrushProperty); set => SetValue(StringBrushProperty, value); }

    /// <summary>Colour of comments.</summary>
    public Brush? CommentBrush { get => (Brush?)GetValue(CommentBrushProperty); set => SetValue(CommentBrushProperty, value); }

    /// <summary>Colour of numbers.</summary>
    public Brush? NumberBrush { get => (Brush?)GetValue(NumberBrushProperty); set => SetValue(NumberBrushProperty, value); }

    /// <summary>Colour of parameters.</summary>
    public Brush? ParameterBrush { get => (Brush?)GetValue(ParameterBrushProperty); set => SetValue(ParameterBrushProperty, value); }

    /// <summary>Colour of function names.</summary>
    public Brush? FunctionBrush { get => (Brush?)GetValue(FunctionBrushProperty); set => SetValue(FunctionBrushProperty, value); }

    /// <summary>Colour of data type names.</summary>
    public Brush? TypeBrush { get => (Brush?)GetValue(TypeBrushProperty); set => SetValue(TypeBrushProperty, value); }

    /// <summary>Colour of operators.</summary>
    public Brush? OperatorBrush { get => (Brush?)GetValue(OperatorBrushProperty); set => SetValue(OperatorBrushProperty, value); }

    /// <summary>Colour of the wavy underline under syntax errors.</summary>
    public Brush? ErrorBrush { get => (Brush?)GetValue(ErrorBrushProperty); set => SetValue(ErrorBrushProperty, value); }

    /// <summary>Colour of the line numbers.</summary>
    public Brush? LineNumberBrush { get => (Brush?)GetValue(LineNumberBrushProperty); set => SetValue(LineNumberBrushProperty, value); }

    /// <summary>Background of the selected text.</summary>
    public Brush? SelectionBrush { get => (Brush?)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    /// <summary>The selected text; empty when nothing is selected.</summary>
    public string SelectedText => _editor.SelectedText;

    /// <summary>The caret offset.</summary>
    public int CaretOffset
    {
        get => _editor.CaretOffset;
        set => _editor.CaretOffset = value;
    }

    /// <summary>Moves keyboard focus into the text.</summary>
    public void FocusText() => _editor.TextArea.Focus();

    /// <summary>
    /// The snippet text (with <c>$0</c> for the caret) for a shortcut typed before Tab; null when the word is no
    /// shortcut (Tab then indents as usual).
    /// </summary>
    public Func<string, string?>? SnippetLookup { get; set; }

    /// <summary>Writes a snippet at the caret (in place of the selection), indented like its line; the caret goes to its $0.</summary>
    public void InsertSnippet(string snippet)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        var start = _editor.SelectionLength > 0 ? _editor.SelectionStart : _editor.CaretOffset;
        ReplaceWithSnippet(start, _editor.SelectionLength, snippet);
    }

    /// <summary>Expands the shortcut before the caret, if it is one; false when it is not.</summary>
    public bool ExpandSnippetAtCaret()
    {
        if (SnippetLookup is not { } lookup || _editor.SelectionLength > 0)
        {
            return false;
        }

        var document = _editor.Document;
        var end = _editor.CaretOffset;
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(document.GetCharAt(start - 1)) || document.GetCharAt(start - 1) == '_'))
        {
            start--;
        }

        // Only a whole word: "xsel" or "t.sel" are not the shortcut "sel".
        if (start == end || (start > 0 && document.GetCharAt(start - 1) is '.' or '@' or '#' or '$') ||
            lookup(document.GetText(start, end - start)) is not { } snippet)
        {
            return false;
        }

        ReplaceWithSnippet(start, end - start, snippet);
        return true;
    }

    private void ReplaceWithSnippet(int start, int length, string snippet)
    {
        var document = _editor.Document;
        var line = document.GetLineByOffset(start);
        var lineText = document.GetText(line.Offset, line.Length);
        var indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];
        var newLine = line.DelimiterLength == 2 || document.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var (text, caret) = JetQueries.Presentation.ViewModels.SqlSnippets.Expand(snippet, indent, newLine);
        document.Replace(start, length, text);
        _editor.Select(start + caret, 0);
        _editor.CaretOffset = start + caret;
    }

    /// <summary>Highlights a range for a moment (the statement Ctrl+Enter runs).</summary>
    public void Flash(int start, int length)
    {
        _flash = (start, length);
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    /// <summary>The range highlighted now, for the renderer and tests; null when none.</summary>
    internal (int Start, int Length)? Flashed => _flash;

    /// <summary>The text can be read and selected but not changed (previews).</summary>
    public bool IsReadOnly
    {
        get => _editor.IsReadOnly;
        set => _editor.IsReadOnly = value;
    }

    /// <summary>Some text is selected.</summary>
    public bool HasSelection => _editor.SelectionLength > 0;

    /// <summary>The last change can be undone.</summary>
    public bool CanUndo => _editor.CanUndo;

    /// <summary>An undone change can be done again.</summary>
    public bool CanRedo => _editor.CanRedo;

    /// <summary>Cuts the selection to the clipboard.</summary>
    public void Cut() => _editor.Cut();

    /// <summary>Copies the selection to the clipboard.</summary>
    public void Copy() => _editor.Copy();

    /// <summary>Pastes the clipboard at the caret.</summary>
    public void Paste() => _editor.Paste();

    /// <summary>Selects the whole text.</summary>
    public void SelectAll() => _editor.SelectAll();

    /// <summary>Undoes the last change.</summary>
    public void Undo() => _editor.Undo();

    /// <summary>Does an undone change again.</summary>
    public void Redo() => _editor.Redo();

    /// <summary>
    /// Replaces the selection, or the whole text when nothing is selected, with what <paramref name="transform"/>
    /// makes of it, as one step that Undo takes back. Nothing changes when the text was edited meanwhile, or when
    /// the transform gives null.
    /// </summary>
    /// <returns>Whether the text changed.</returns>
    public async Task<bool> TransformAsync(Func<string, Task<string?>> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var document = _editor.Document;
        var selected = _editor.SelectionLength > 0;
        var start = selected ? _editor.SelectionStart : 0;
        var length = selected ? _editor.SelectionLength : document.TextLength;
        var version = document.Version;
        var result = await transform(document.GetText(start, length));
        var unchanged = version is not null && document.Version is { } now && version.BelongsToSameDocumentAs(now) && version.CompareAge(now) == 0;
        if (result is null || !unchanged || result == document.GetText(start, length))
        {
            return false;
        }

        document.Replace(start, length, result);
        if (selected)
        {
            _editor.Select(start, result.Length);
        }
        else
        {
            _editor.CaretOffset = Math.Min(_editor.CaretOffset, document.TextLength);
        }

        return true;
    }

    /// <summary>
    /// Comments out the lines of the selection (or the caret's line) with <c>--</c>, or, when they all are already,
    /// takes the comment marks away. One step for Undo.
    /// </summary>
    public void ToggleLineComment()
    {
        var document = _editor.Document;
        var first = document.GetLineByOffset(_editor.SelectionLength > 0 ? _editor.SelectionStart : _editor.CaretOffset);
        var last = _editor.SelectionLength > 0 ? document.GetLineByOffset(_editor.SelectionStart + _editor.SelectionLength) : first;
        if (last.LineNumber > first.LineNumber && last.Offset == _editor.SelectionStart + _editor.SelectionLength)
        {
            // A selection ending at the start of a line does not take that line.
            last = last.PreviousLine;
        }

        var lines = new List<DocumentLine>();
        for (var line = first; line is not null && line.LineNumber <= last.LineNumber; line = line.NextLine)
        {
            lines.Add(line);
        }

        string Text(DocumentLine line) => document.GetText(line.Offset, line.Length);
        var filled = lines.Where(l => !string.IsNullOrWhiteSpace(Text(l))).ToList();
        if (filled.Count == 0)
        {
            return;
        }

        var commented = filled.All(l => Text(l).TrimStart().StartsWith("--", StringComparison.Ordinal));
        var indent = filled.Min(l => Text(l).Length - Text(l).TrimStart().Length);
        document.BeginUpdate();
        try
        {
            // From the bottom up, so that offsets above stay valid.
            foreach (var line in Enumerable.Reverse(filled))
            {
                var text = Text(line);
                if (commented)
                {
                    var at = text.IndexOf("--", StringComparison.Ordinal);
                    var remove = text.Length > at + 2 && text[at + 2] == ' ' ? 3 : 2;
                    document.Remove(line.Offset + at, remove);
                }
                else
                {
                    document.Insert(line.Offset + indent, "-- ");
                }
            }
        }
        finally
        {
            document.EndUpdate();
        }
    }

    /// <summary>Upper- or lower-cases the selection, or the word at the caret. One step for Undo.</summary>
    public void ChangeCase(bool upper)
    {
        var document = _editor.Document;
        int start, length;
        if (_editor.SelectionLength > 0)
        {
            (start, length) = (_editor.SelectionStart, _editor.SelectionLength);
        }
        else
        {
            start = _editor.CaretOffset;
            var end = start;
            while (start > 0 && IsWordChar(document.GetCharAt(start - 1)))
            {
                start--;
            }

            while (end < document.TextLength && IsWordChar(document.GetCharAt(end)))
            {
                end++;
            }

            length = end - start;
        }

        if (length == 0)
        {
            return;
        }

        var text = document.GetText(start, length);

        // Invariant culture: "i" becomes "I" (not "İ"), as SQL keywords and identifiers expect.
        var changed = upper ? text.ToUpperInvariant() : text.ToLowerInvariant();
        if (changed != text)
        {
            var selected = _editor.SelectionLength > 0;
            document.Replace(start, length, changed);
            if (selected)
            {
                _editor.Select(start, changed.Length);
            }
        }

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';
    }

    /// <summary>Opens the completion list at the caret, as Ctrl+Space does.</summary>
    public Task ShowCompletionAsync() => RequestCompletionAsync();

    /// <summary>Asks the <see cref="DiagnosticSource"/> now, without waiting for a pause in typing.</summary>
    public async Task RefreshDiagnosticsAsync()
    {
        var source = DiagnosticSource;
        var version = ++_diagnosticVersion;
        IReadOnlyList<SqlDiagnostic> diagnostics = [];
        if (source is not null)
        {
            try
            {
                diagnostics = await source(_editor.Document.Text, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // Error checking is a convenience: a failing language service must never disturb typing.
            catch (Exception)
#pragma warning restore CA1031
            {
                diagnostics = [];
            }
        }

        // The text changed again while this was being computed: the newer request will bring the answer.
        if (version == _diagnosticVersion)
        {
            _diagnostics = diagnostics;
            _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        }
    }

    internal (Brush? Brush, bool Italic) StyleFor(SqlTokenKind kind) => kind switch
    {
        SqlTokenKind.Keyword => (KeywordBrush, false),
        SqlTokenKind.StringLiteral => (StringBrush, false),
        SqlTokenKind.Comment => (CommentBrush, true),
        SqlTokenKind.NumberLiteral => (NumberBrush, false),
        SqlTokenKind.Parameter => (ParameterBrush, false),
        SqlTokenKind.Function => (FunctionBrush, false),
        SqlTokenKind.DataType => (TypeBrush, false),
        SqlTokenKind.Operator => (OperatorBrush, false),
        _ => (null, false),
    };

    internal void Redraw() => _editor.TextArea.TextView.Redraw();

    private static double ClampZoom(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, MinZoom, MaxZoom) : 1;

    /// <inheritdoc />
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty && _editor is not null)
        {
            UpdateFont();
        }
    }

    private void UpdateFont() => _editor.FontSize = FontSize * Zoom;

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            Zoom = StepZoom(Zoom, e.Delta > 0);
            e.Handled = true;
        }
    }

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(SqlEditor), new PropertyMetadata(null, static (d, _) => ((SqlEditor)d).Redraw()));

    private void OnTextPropertyChanged()
    {
        if (_updatingText || _editor.Document.Text == Text)
        {
            return;
        }

        _updatingText = true;
        try
        {
            _editor.Document.Text = Text ?? string.Empty;
        }
        finally
        {
            _updatingText = false;
        }
    }

    private void OnLanguageServiceChanged()
    {
        _colorizer.Invalidate(1);
        Redraw();
    }

    private void OnTextEntered(object? sender, TextCompositionEventArgs e)
    {
        // While the list is open it filters itself as the user types. Otherwise a new word, a "." or a space
        // (after FROM, JOIN, …) may call for one; the source decides whether there is anything to offer.
        if (_completionWindow is null && e.Text is [var c] && (char.IsLetter(c) || c is '_' or '.' or ' ' or '[' or '"'))
        {
            _ = RequestCompletionAsync();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && _completionWindow is null && ExpandSnippetAtCaret())
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _ = RequestCompletionAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.None)
        {
            NavigationRequested?.Invoke(this, _editor.CaretOffset);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D0 or Key.NumPad0)
        {
            Zoom = 1;
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract)
        {
            Zoom = StepZoom(Zoom, e.Key is Key.OemPlus or Key.Add);
            e.Handled = true;
        }
    }

    private void OnPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (OffsetAt(e) is { } offset && (offset < _editor.SelectionStart || offset > _editor.SelectionStart + _editor.SelectionLength || _editor.SelectionLength == 0))
        {
            _editor.Select(offset, 0);
            _editor.CaretOffset = offset;
        }

        _editor.TextArea.Focus();
    }

    private int? OffsetAt(MouseEventArgs e)
    {
        var view = _editor.TextArea.TextView;
        var position = view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset);
        return position is { } found ? _editor.Document.GetOffset(found.Location) : null;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && OffsetAt(e) is { } offset)
        {
            NavigationRequested?.Invoke(this, offset);
            e.Handled = true;
        }
    }

    private void OnMouseHover(object sender, MouseEventArgs e)
    {
        var message = OffsetAt(e) is { } offset
            ? _diagnostics.FirstOrDefault(d => d.Span.Start <= offset && offset < Math.Max(d.Span.End, d.Span.Start + 1))?.Message
            : null;
        if (message is null)
        {
            return;
        }

        _toolTip.PlacementTarget = this;
        _toolTip.Content = message;
        _toolTip.IsOpen = true;
        e.Handled = true;
    }

    private async Task RequestCompletionAsync()
    {
        var source = CompletionSource;
        if (source is null)
        {
            return;
        }

        // Only the newest request matters; an older answer is recognized by its number and dropped. Requests
        // are answered from cached metadata in milliseconds, so they are left to finish rather than canceled.
        var version = ++_completionVersion;
        var offset = _editor.CaretOffset;
        IReadOnlyList<CompletionItem> items;
        try
        {
            items = await source(_editor.Document.Text, offset, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // Completion is a convenience: a failing language service must never disturb typing.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        // The caret moved on while the answer was on its way: a newer request covers the new position.
        if (version != _completionVersion || items.Count == 0 || _editor.CaretOffset != offset || _completionWindow is not null)
        {
            return;
        }

        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = Math.Clamp(items[0].ReplaceSpan.Start, 0, offset),
            EndOffset = offset,
            MinWidth = 280,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = false,
        };
        window.SetResourceReference(BackgroundProperty, "Brush.Card");
        window.SetResourceReference(ForegroundProperty, "Brush.Text");
        window.SetResourceReference(BorderBrushProperty, "Brush.BorderStrong");
        window.BorderThickness = new Thickness(1);
        window.CompletionList.ListBox.SetResourceReference(BackgroundProperty, "Brush.Card");
        window.CompletionList.ListBox.SetResourceReference(ForegroundProperty, "Brush.Text");
        window.CompletionList.ListBox.BorderThickness = new Thickness(0);
        foreach (var item in items)
        {
            window.CompletionList.CompletionData.Add(new CompletionData(item));
        }

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_completionWindow, window))
            {
                _completionWindow = null;
            }
        };
        _completionWindow = window;
        window.Show();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _colorizer.Invalidate(_editor.Document.GetLineByOffset(Math.Min(e.Offset, _editor.Document.TextLength)).LineNumber);
        if (DiagnosticSource is not null)
        {
            _diagnosticTimer.Stop();
            _diagnosticTimer.Start();
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_updatingText)
        {
            return;
        }

        _updatingText = true;
        try
        {
            SetCurrentValue(TextProperty, _editor.Document.Text);
        }
        finally
        {
            _updatingText = false;
        }
    }

    /// <summary>
    /// Colours each visible line from the language service's tokens. A line's tokens depend on the state the
    /// previous line ended in (inside a block comment or not), so end states are cached per line and
    /// recomputed from the first changed line down, lazily, when a line is drawn.
    /// </summary>
    private sealed class SqlColorizer(SqlEditor owner) : DocumentColorizingTransformer
    {
        // _endStates[i] is the state after line i + 1; valid for the first _validLines lines.
        private readonly List<int> _endStates = [];
        private int _validLines;

        public void Invalidate(int fromLine) => _validLines = Math.Min(_validLines, Math.Max(0, fromLine - 1));

        protected override void ColorizeLine(DocumentLine line)
        {
            var service = owner.LanguageService;
            if (service is null)
            {
                return;
            }

            var document = CurrentContext.Document;
            var number = line.LineNumber;

            // Bring the cache up to the line before this one.
            for (var n = _validLines + 1; n < number; n++)
            {
                var previous = document.GetLineByNumber(n);
                Store(n, service.TokenizeLine(document.GetText(previous), StateBefore(n)).EndState);
            }

            var result = service.TokenizeLine(document.GetText(line), StateBefore(number));
            var known = number <= _endStates.Count ? _endStates[number - 1] : (int?)null;
            Store(number, result.EndState);
            if (known is { } old && old != result.EndState)
            {
                // A comment was opened or closed here: the lines below, already drawn, are now coloured wrongly.
                owner.Dispatcher.BeginInvoke(owner.Redraw, DispatcherPriority.Background);
            }

            foreach (var token in result.Tokens)
            {
                var (brush, italic) = owner.StyleFor(token.Kind);
                if (brush is null)
                {
                    continue;
                }

                ChangeLinePart(
                    line.Offset + token.Span.Start,
                    line.Offset + Math.Min(token.Span.End, line.Length),
                    element =>
                    {
                        element.TextRunProperties.SetForegroundBrush(brush);
                        if (italic)
                        {
                            var face = element.TextRunProperties.Typeface;
                            element.TextRunProperties.SetTypeface(new Typeface(face.FontFamily, FontStyles.Italic, face.Weight, face.Stretch));
                        }
                    });
            }
        }

        private int StateBefore(int lineNumber) => lineNumber <= 1 ? 0 : _endStates[lineNumber - 2];

        private void Store(int lineNumber, int endState)
        {
            if (lineNumber <= _endStates.Count)
            {
                _endStates[lineNumber - 1] = endState;
            }
            else
            {
                _endStates.Add(endState);
            }

            _validLines = Math.Max(_validLines, lineNumber);
        }
    }

    /// <summary>Draws a wavy line under each diagnostic's span, wherever it is visible.</summary>
    private sealed class SquiggleRenderer(SqlEditor owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (owner._diagnostics.Count == 0 || owner.ErrorBrush is not { } brush || !textView.VisualLinesValid || textView.Document is not { } document)
            {
                return;
            }

            var pen = new Pen(brush, 1);
            pen.Freeze();
            var length = document.TextLength;
            foreach (var diagnostic in owner._diagnostics)
            {
                if (length == 0)
                {
                    break;
                }

                // The text may have changed since the diagnostics were computed: keep the span inside it,
                // and at least one character wide so that an error at the very end is still visible.
                var start = Math.Clamp(diagnostic.Span.Start, 0, length - 1);
                var end = Math.Clamp(diagnostic.Span.End, start + 1, length);
                var segment = new TextSegment { StartOffset = start, EndOffset = end };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        var y = rect.Bottom - 1;
                        context.BeginFigure(new Point(rect.Left, y), isFilled: false, isClosed: false);
                        var up = true;
                        for (var x = rect.Left + 2; x <= Math.Max(rect.Right, rect.Left + 4); x += 2)
                        {
                            context.LineTo(new Point(x, up ? y - 2 : y), isStroked: true, isSmoothJoin: false);
                            up = !up;
                        }
                    }

                    geometry.Freeze();
                    drawingContext.DrawGeometry(null, pen, geometry);
                }
            }
        }
    }

    /// <summary>One row of the completion list: the name, then what it is in a muted colour.</summary>
    private sealed class CompletionData(CompletionItem item) : ICompletionData
    {
        public ImageSource? Image => null;

        public string Text => item.Label;

        public object Content
        {
            get
            {
                var panel = new DockPanel { LastChildFill = true };
                var detail = new TextBlock { Text = item.Detail ?? Kind(item.Kind), Margin = new Thickness(16, 0, 0, 0), FontSize = 11.5 };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
                DockPanel.SetDock(detail, Dock.Right);
                panel.Children.Add(detail);
                panel.Children.Add(new TextBlock { Text = item.Label });
                return panel;
            }
        }

        public object? Description => item.Detail;

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, item.InsertText ?? item.Label);

        private static string Kind(CompletionItemKind kind) => kind switch
        {
            CompletionItemKind.Keyword => string.Empty,
            var other => other.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>Paints the flashed range in the selection colour, softly.</summary>
    private sealed class FlashRenderer(SqlEditor owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (owner._flash is not { } flash || owner.SelectionBrush is not { } brush)
            {
                return;
            }

            var length = owner._editor.Document.TextLength;
            var start = Math.Clamp(flash.Start, 0, length);
            var segment = new TextSegment { StartOffset = start, EndOffset = Math.Clamp(flash.Start + flash.Length, start, length) };
            var builder = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true };
            builder.AddSegment(textView, segment);
            if (builder.CreateGeometry() is { } geometry)
            {
                drawingContext.DrawGeometry(brush, null, geometry);
            }
        }
    }
}
