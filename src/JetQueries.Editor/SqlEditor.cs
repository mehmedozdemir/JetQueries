using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using JetQueries.Sdk;

namespace JetQueries.Editor;

/// <summary>
/// The SQL text editor: AvaloniaEdit with line numbers and syntax colouring driven by an
/// <see cref="ISqlLanguageService"/>. The control knows no SQL dialect itself; colours come from the brush
/// properties, which the application binds to its theme.
/// </summary>
public class SqlEditor : UserControl
{
    /// <summary>The text. Two-way: typing updates it, setting it replaces the document.</summary>
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<SqlEditor, string>(nameof(Text), string.Empty, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Tokenizes lines for colouring; without one the text is shown plain.</summary>
    public static readonly StyledProperty<ISqlLanguageService?> LanguageServiceProperty =
        AvaloniaProperty.Register<SqlEditor, ISqlLanguageService?>(nameof(LanguageService));

    /// <summary>Colour of keywords.</summary>
    public static readonly StyledProperty<IBrush?> KeywordBrushProperty = AvaloniaProperty.Register<SqlEditor, IBrush?>(nameof(KeywordBrush));

    /// <summary>Colour of string literals.</summary>
    public static readonly StyledProperty<IBrush?> StringBrushProperty = AvaloniaProperty.Register<SqlEditor, IBrush?>(nameof(StringBrush));

    /// <summary>Colour of comments.</summary>
    public static readonly StyledProperty<IBrush?> CommentBrushProperty = AvaloniaProperty.Register<SqlEditor, IBrush?>(nameof(CommentBrush));

    /// <summary>Colour of numbers.</summary>
    public static readonly StyledProperty<IBrush?> NumberBrushProperty = AvaloniaProperty.Register<SqlEditor, IBrush?>(nameof(NumberBrush));

    /// <summary>Colour of parameters and bind variables.</summary>
    public static readonly StyledProperty<IBrush?> ParameterBrushProperty = AvaloniaProperty.Register<SqlEditor, IBrush?>(nameof(ParameterBrush));

    /// <summary>Colour of function and data type names.</summary>
    public static readonly StyledProperty<IBrush?> FunctionBrushProperty = AvaloniaProperty.Register<SqlEditor, IBrush?>(nameof(FunctionBrush));

    private readonly TextEditor _editor;
    private readonly SqlColorizer _colorizer;
    private bool _updatingText;
    private CompletionWindow? _completionWindow;
    private int _completionVersion;

    /// <summary>Creates the editor.</summary>
    public SqlEditor()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            FontSize = 13,
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 4;
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;

        _colorizer = new SqlColorizer(this);
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _editor.Document.Changed += OnDocumentChanged;
        _editor.TextChanged += OnEditorTextChanged;
        _editor.TextArea.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _editor.TextArea.TextEntered += OnTextEntered;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Content = _editor;
    }

    /// <summary>
    /// Supplies completion items for a caret position: the whole text, the caret offset and a token that is
    /// canceled when the request is no longer wanted. Called on the UI thread; it must not block. Without it
    /// the editor offers no completion.
    /// </summary>
    public Func<string, int, CancellationToken, Task<IReadOnlyList<CompletionItem>>>? CompletionSource { get; set; }

    /// <summary>Whether the completion list is showing.</summary>
    public bool IsCompletionOpen => _completionWindow is not null;

    /// <summary>The labels in the completion list, in order; empty when it is not showing.</summary>
    public IReadOnlyList<string> CompletionLabels =>
        _completionWindow is null ? [] : [.. _completionWindow.CompletionList.CompletionData.Select(static d => d.Text)];

    /// <summary>Raised when the selection changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The text.</summary>
    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The language service used for colouring.</summary>
    public ISqlLanguageService? LanguageService
    {
        get => GetValue(LanguageServiceProperty);
        set => SetValue(LanguageServiceProperty, value);
    }

    /// <summary>Colour of keywords.</summary>
    public IBrush? KeywordBrush
    {
        get => GetValue(KeywordBrushProperty);
        set => SetValue(KeywordBrushProperty, value);
    }

    /// <summary>Colour of string literals.</summary>
    public IBrush? StringBrush
    {
        get => GetValue(StringBrushProperty);
        set => SetValue(StringBrushProperty, value);
    }

    /// <summary>Colour of comments.</summary>
    public IBrush? CommentBrush
    {
        get => GetValue(CommentBrushProperty);
        set => SetValue(CommentBrushProperty, value);
    }

    /// <summary>Colour of numbers.</summary>
    public IBrush? NumberBrush
    {
        get => GetValue(NumberBrushProperty);
        set => SetValue(NumberBrushProperty, value);
    }

    /// <summary>Colour of parameters and bind variables.</summary>
    public IBrush? ParameterBrush
    {
        get => GetValue(ParameterBrushProperty);
        set => SetValue(ParameterBrushProperty, value);
    }

    /// <summary>Colour of function and data type names.</summary>
    public IBrush? FunctionBrush
    {
        get => GetValue(FunctionBrushProperty);
        set => SetValue(FunctionBrushProperty, value);
    }

    /// <summary>The selected text; empty when nothing is selected.</summary>
    public string SelectedText => _editor.SelectedText;

    /// <summary>Moves keyboard focus into the text.</summary>
    public void FocusText() => _editor.TextArea.Focus();

    internal IBrush? BrushFor(SqlTokenKind kind) => kind switch
    {
        SqlTokenKind.Keyword => KeywordBrush,
        SqlTokenKind.StringLiteral => StringBrush,
        SqlTokenKind.Comment => CommentBrush,
        SqlTokenKind.NumberLiteral => NumberBrush,
        SqlTokenKind.Parameter => ParameterBrush,
        SqlTokenKind.Function or SqlTokenKind.DataType => FunctionBrush,
        _ => null,
    };

    internal void Redraw() => _editor.TextArea.TextView.Redraw();

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            if (!_updatingText && _editor.Document.Text != Text)
            {
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
        }
        else if (change.Property == LanguageServiceProperty)
        {
            _colorizer.Invalidate(1);
            Redraw();
        }
        else if (change.Property == ForegroundProperty)
        {
            _editor.Foreground = Foreground;
        }
        else if (change.Property == KeywordBrushProperty || change.Property == StringBrushProperty || change.Property == CommentBrushProperty ||
                 change.Property == NumberBrushProperty || change.Property == ParameterBrushProperty || change.Property == FunctionBrushProperty)
        {
            Redraw();
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        // While the list is open it filters itself as the user types. Otherwise a new word, a "." or a space
        // (after FROM, JOIN, …) may call for one; the source decides whether there is anything to offer.
        if (_completionWindow is null && e.Text is [var c] && (char.IsLetter(c) || c is '_' or '.' or ' ' or '['))
        {
            RequestCompletion();
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            RequestCompletion();
            e.Handled = true;
        }
    }

    // async void: an event-driven UI action; every failure is contained below.
    private async void RequestCompletion()
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
            MinWidth = 260,
        };
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

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e) =>
        _colorizer.Invalidate(_editor.Document.GetLineByOffset(Math.Min(e.Offset, _editor.Document.TextLength)).LineNumber);

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
                Dispatcher.UIThread.Post(owner.Redraw, DispatcherPriority.Background);
            }

            foreach (var token in result.Tokens)
            {
                if (owner.BrushFor(token.Kind) is { } brush)
                {
                    ChangeLinePart(
                        line.Offset + token.Span.Start,
                        line.Offset + Math.Min(token.Span.End, line.Length),
                        element => element.TextRunProperties.SetForegroundBrush(brush));
                }
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

    /// <summary>One row of the completion list.</summary>
    private sealed class CompletionData(CompletionItem item) : ICompletionData
    {
        public Avalonia.Media.IImage? Image => null;

        public string Text => item.Label;

        public object Content => item.Label;

        public object? Description => item.Detail;

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, item.InsertText ?? item.Label);
    }
}
