namespace JetQueries.Sdk;

/// <summary>
/// Editor intelligence for one SQL dialect, independent of any editor control. Implementations are stateless
/// and thread-safe; the host calls them from background jobs, debounces, and cancels superseded requests.
/// </summary>
/// <remarks>
/// Preview contract: may change until SDK 1.0. v0 covers colouring, diagnostics, completion and go-to-definition;
/// hover, signature help, formatting, folding and find-references are added as the editor grows.
/// </remarks>
public interface ISqlLanguageService
{
    /// <summary>
    /// Tokenizes a single line for syntax colouring. Synchronous by design: it is called for visible lines only
    /// and must be fast (no parsing, no I/O).
    /// </summary>
    /// <param name="line">The text of one line, without its line terminator.</param>
    /// <param name="startState">
    /// The <see cref="SqlLineTokens.EndState"/> of the previous line, or <c>0</c> for the first line. It carries
    /// constructs that span lines, such as block comments and string literals.
    /// </param>
    /// <returns>The tokens of the line and the state to pass to the next line.</returns>
    SqlLineTokens TokenizeLine(ReadOnlySpan<char> line, int startState);

    /// <summary>Parses the document and reports syntax errors. Must tolerate incomplete SQL.</summary>
    /// <param name="document">The document to analyze.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    /// <returns>The diagnostics; empty if the document is valid.</returns>
    Task<IReadOnlyList<SqlDiagnostic>> GetDiagnosticsAsync(SqlDocument document, CancellationToken cancellationToken);

    /// <summary>Gets completion proposals at a position.</summary>
    /// <param name="document">The document being edited.</param>
    /// <param name="offset">Zero-based caret offset in <see cref="SqlDocument.Text"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Proposals in the service's preferred order; the host applies fuzzy filtering and recency ranking.</returns>
    Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(SqlDocument document, int offset, CancellationToken cancellationToken);

    /// <summary>Resolves the identifier at a position to what it refers to (Ctrl+Click, F12).</summary>
    /// <param name="document">The document being edited.</param>
    /// <param name="offset">Zero-based offset inside the identifier.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The definition, or <see langword="null"/> if nothing navigable is at that position.</returns>
    Task<SqlDefinition?> GetDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken);
}

/// <summary>A document handed to an <see cref="ISqlLanguageService"/>.</summary>
/// <param name="Text">The full text of the document.</param>
/// <param name="Metadata">
/// Metadata of the connection the document is bound to (normally the host's cache), or <see langword="null"/>
/// when the document has no connection; the service then offers keyword- and document-based results only.
/// </param>
/// <param name="DefaultSchema">Schema used to resolve unqualified names, if known.</param>
public sealed record SqlDocument(string Text, IMetadataProvider? Metadata = null, string? DefaultSchema = null)
{
    /// <summary>Database used to resolve names without a database part, if known (the query window's current database).</summary>
    public string? DefaultDatabase { get; init; }
}

/// <summary>A range of characters in a document or line.</summary>
/// <param name="Start">Zero-based offset of the first character.</param>
/// <param name="Length">Number of characters.</param>
public readonly record struct TextSpan(int Start, int Length)
{
    /// <summary>Offset just past the last character.</summary>
    public int End => Start + Length;

    /// <summary>Whether <paramref name="offset"/> lies inside the span or at its end.</summary>
    /// <param name="offset">The offset to test.</param>
    /// <returns><see langword="true"/> if <paramref name="offset"/> is within [<see cref="Start"/>, <see cref="End"/>].</returns>
    public bool Contains(int offset) => offset >= Start && offset <= End;
}

/// <summary>Result of <see cref="ISqlLanguageService.TokenizeLine"/>.</summary>
/// <param name="Tokens">Tokens in order; spans are relative to the start of the line. Whitespace is omitted.</param>
/// <param name="EndState">Opaque lexer state at the end of the line; <c>0</c> means "not inside a multi-line construct".</param>
public sealed record SqlLineTokens(IReadOnlyList<SqlToken> Tokens, int EndState);

/// <summary>A classified piece of text.</summary>
/// <param name="Span">Where the token is.</param>
/// <param name="Kind">What the token is.</param>
public readonly record struct SqlToken(TextSpan Span, SqlTokenKind Kind);

/// <summary>Token classes used for syntax colouring.</summary>
public enum SqlTokenKind
{
    /// <summary>Anything not covered by another kind.</summary>
    Text,

    /// <summary>A reserved word.</summary>
    Keyword,

    /// <summary>A plain or quoted identifier.</summary>
    Identifier,

    /// <summary>A string literal.</summary>
    StringLiteral,

    /// <summary>A numeric literal.</summary>
    NumberLiteral,

    /// <summary>A line or block comment.</summary>
    Comment,

    /// <summary>An operator.</summary>
    Operator,

    /// <summary>Punctuation such as parentheses, commas and semicolons.</summary>
    Punctuation,

    /// <summary>A bind variable or parameter (<c>@p</c>, <c>:p</c>, <c>$1</c>).</summary>
    Parameter,

    /// <summary>A built-in function name.</summary>
    Function,

    /// <summary>A data type name.</summary>
    DataType,
}

/// <summary>A problem found in a document.</summary>
/// <param name="Span">The offending range.</param>
/// <param name="Message">Description shown to the user.</param>
/// <param name="Severity">How serious the problem is.</param>
public sealed record SqlDiagnostic(TextSpan Span, string Message, SqlDiagnosticSeverity Severity);

/// <summary>Severity of a <see cref="SqlDiagnostic"/>.</summary>
public enum SqlDiagnosticSeverity
{
    /// <summary>A hint.</summary>
    Info,

    /// <summary>Probably a mistake.</summary>
    Warning,

    /// <summary>The SQL will not run.</summary>
    Error,
}

/// <summary>One completion proposal.</summary>
/// <param name="Label">Text shown in the list and matched against what the user typed.</param>
/// <param name="Kind">What the proposal is; selects the icon.</param>
/// <param name="ReplaceSpan">The range of the document that <see cref="InsertText"/> replaces.</param>
public sealed record CompletionItem(string Label, CompletionItemKind Kind, TextSpan ReplaceSpan)
{
    /// <summary>
    /// Text to insert, already quoted where the dialect requires it; <see langword="null"/> means use
    /// <see cref="Label"/>.
    /// </summary>
    public string? InsertText { get; init; }

    /// <summary>Short secondary text shown next to the label (data type, schema, signature).</summary>
    public string? Detail { get; init; }
}

/// <summary>Kinds of completion proposals.</summary>
public enum CompletionItemKind
{
    /// <summary>A reserved word.</summary>
    Keyword,

    /// <summary>A database.</summary>
    Database,

    /// <summary>A schema.</summary>
    Schema,

    /// <summary>A table.</summary>
    Table,

    /// <summary>A view.</summary>
    View,

    /// <summary>A column.</summary>
    Column,

    /// <summary>A function, built-in or user-defined.</summary>
    Function,

    /// <summary>A stored procedure.</summary>
    Procedure,

    /// <summary>A package.</summary>
    Package,

    /// <summary>A sequence.</summary>
    Sequence,

    /// <summary>A table alias or CTE name declared in the document.</summary>
    Alias,

    /// <summary>A variable or parameter.</summary>
    Variable,

    /// <summary>A multi-token snippet (for example a generated <c>JOIN … ON</c> clause).</summary>
    Snippet,
}

/// <summary>
/// What an identifier refers to. Exactly one of <see cref="Target"/> and <see cref="DocumentSpan"/> is set.
/// </summary>
/// <param name="Target">The catalog object referred to, when the target lives in the database.</param>
/// <param name="DocumentSpan">The declaration's range, when the target is declared in the same document (alias, CTE, variable).</param>
/// <param name="MemberName">A member inside <see cref="Target"/> (column, package procedure) to reveal, if any.</param>
public sealed record SqlDefinition(DatabaseObject? Target, TextSpan? DocumentSpan = null, string? MemberName = null);
