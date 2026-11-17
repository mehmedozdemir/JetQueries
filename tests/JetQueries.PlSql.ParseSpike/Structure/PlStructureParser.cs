using System.Collections.Frozen;

namespace JetQueries.PlSql.ParseSpike.Structure;

internal enum PlNodeKind
{
    Script,
    SqlPlusCommand,
    SqlStatement,
    PackageSpec,
    PackageBody,
    TypeSpec,
    TypeBody,
    Trigger,
    Procedure,
    Function,
    AnonymousBlock,
    Other,
    Parameter,
    Variable,
    Constant,
    ExceptionDeclaration,
    TypeDeclaration,
    Subtype,
    Cursor,
    Pragma,
    Block,
    If,
    Loop,
    Case,
    Sql,
    Statement,
    Handler,
}

internal sealed class PlNode(PlNodeKind kind, int start)
{
    public PlNodeKind Kind { get; } = kind;

    public int Start { get; } = start;

    /// <summary>Exclusive end offset.</summary>
    public int End { get; set; }

    public string? Name { get; set; }

    public int NameStart { get; set; } = -1;

    /// <summary>Offset of the PROCEDURE / FUNCTION keyword of a routine.</summary>
    public int KeywordStart { get; set; } = -1;

    /// <summary>True for a routine with a PL/SQL body, false for a specification, forward declaration or call spec.</summary>
    public bool HasBody { get; set; }

    public List<PlNode>? Children { get; private set; }

    public void Add(PlNode child) => (Children ??= []).Add(child);

    public IEnumerable<PlNode> Descendants()
    {
        if (Children is null)
        {
            yield break;
        }

        foreach (var child in Children)
        {
            yield return child;
            foreach (var nested in child.Descendants())
            {
                yield return nested;
            }
        }
    }
}

internal readonly record struct PlDiagnostic(int Offset, string Message);

internal sealed record PlScript(PlNode Root, IReadOnlyList<PlDiagnostic> Diagnostics, int TokenCount);

/// <summary>
/// Hand-written recursive-descent parser for the structure of an Oracle script: SQL*Plus commands, SQL statements
/// (as ranges), PL/SQL units, routines, declarations, blocks and statements. Expressions and the inside of SQL
/// statements are skipped, not parsed. It never throws on input, always terminates, and resynchronizes at
/// ';', END, statement keywords and the '/' line, so damage stays local.
/// </summary>
internal sealed class PlStructureParser
{
    private const int MaxDiagnostics = 200;

    private static readonly FrozenSet<string> SqlPlusCommands = FrozenSet.ToFrozenSet(
        [
            "ACC", "ACCEPT", "BREAK", "BTITLE", "CL", "CLEAR", "COL", "COLUMN", "COMPUTE", "CONN", "CONNECT", "DEF", "DEFINE",
            "DESC", "DESCRIBE", "DISCONNECT", "EXEC", "EXECUTE", "EXIT", "HO", "HOST", "PASSWORD", "PAUSE", "PRINT", "PRO",
            "PROMPT", "QUIT", "REM", "REMARK", "SET", "SHO", "SHOW", "SHUTDOWN", "SPO", "SPOOL", "STA", "START", "STARTUP",
            "TIMING", "TTITLE", "UNDEF", "UNDEFINE", "VAR", "VARIABLE", "WHENEVER",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> SqlPlusLookup =
        SqlPlusCommands.GetAlternateLookup<ReadOnlySpan<char>>();

    private readonly string _text;
    private readonly List<PlDiagnostic> _diagnostics = [];
    private List<PlToken> _t = [];
    private int _i;
    private int _openIf;
    private int _openLoop;
    private int _openCase;
    private int _handlers;
    private int _tokenCount;

    private PlStructureParser(string text) => _text = text;

    [Flags]
    private enum Stop
    {
        None = 0,
        Semicolon = 1,
        Then = 2,
        Loop = 4,
        IsAs = 8,
        Comma = 16,
        RParen = 32,
        When = 64,
        Hard = 128,
        Eof = 256,
        End = 512,
    }

    private enum RoutineMode
    {
        Declaration,
        Standalone,
        TypeSpecMember,
    }

    private PlToken Cur => _t[Math.Min(_i, _t.Count - 1)];

    private PlKeyword Kw => Cur.Keyword;

    private int PreviousEnd => _i > 0 ? _t[Math.Min(_i, _t.Count) - 1].End : Cur.Start;

    public static PlScript Parse(string text)
    {
        var parser = new PlStructureParser(text);
        var root = parser.ParseScript();
        return new PlScript(root, parser._diagnostics, parser._tokenCount);
    }

    // -----------------------------------------------------------------------------------------------------
    // Script level: the way SQL*Plus cuts a file into commands
    // -----------------------------------------------------------------------------------------------------
    private PlNode ParseScript()
    {
        var text = _text;
        var root = new PlNode(PlNodeKind.Script, 0) { End = text.Length };
        var lexer = new PlLexer(text);
        var tokens = new List<PlToken>(Math.Max(16, text.Length / 6));

        while (true)
        {
            lexer.SkipTrivia();
            var start = lexer.Position;
            if (start >= text.Length)
            {
                break;
            }

            if (TrySqlPlusLine(start, out var lineEnd))
            {
                root.Add(new PlNode(PlNodeKind.SqlPlusCommand, start) { End = lineEnd });
                lexer.Position = lineEnd;
                continue;
            }

            tokens.Clear();
            var decided = false;
            var plsql = false;
            var java = false;
            var hasDirectives = false;
            var look = 0;
            var depth = 0;
            while (true)
            {
                var token = lexer.Next();
                if (token.Kind == PlTokenKind.Eof || (token.Kind == PlTokenKind.Slash && token.AloneOnLine))
                {
                    break;
                }

                tokens.Add(token);
                hasDirectives |= token.Kind == PlTokenKind.Directive;

                if (!decided)
                {
                    if (look == 0)
                    {
                        if (token.Keyword is PlKeyword.Declare or PlKeyword.Begin or PlKeyword.Procedure or PlKeyword.Function || token.Kind == PlTokenKind.LabelOpen)
                        {
                            decided = plsql = true;
                        }
                        else if (token.Keyword == PlKeyword.Create)
                        {
                            look = 1;
                        }
                        else
                        {
                            decided = true;
                        }
                    }
                    else if (token.Keyword is PlKeyword.Package or PlKeyword.Procedure or PlKeyword.Function or PlKeyword.Trigger or PlKeyword.Type or PlKeyword.Library)
                    {
                        decided = plsql = true;
                    }
                    else if (token.Keyword == PlKeyword.Java)
                    {
                        java = true;
                        break;
                    }
                    else if (++look > 6 || !IsCreatePrefix(token))
                    {
                        decided = true;
                    }
                }

                if (!plsql)
                {
                    if (token.Kind == PlTokenKind.LParen)
                    {
                        depth++;
                    }
                    else if (token.Kind == PlTokenKind.RParen)
                    {
                        depth--;
                    }
                    else if (token.Kind == PlTokenKind.Semicolon && depth <= 0)
                    {
                        break;
                    }
                }
            }

            if (tokens.Count == 0)
            {
                continue;
            }

            _tokenCount += tokens.Count;
            if (java)
            {
                // Java source is not PL/SQL: take everything up to the '/' line as one opaque unit.
                var end = EndOfSlashTerminatedText(lexer.Position);
                root.Add(new PlNode(PlNodeKind.Other, start) { End = end });
                lexer.Position = end;
                continue;
            }

            if (!plsql)
            {
                root.Add(new PlNode(PlNodeKind.SqlStatement, start) { End = tokens[^1].End });
                continue;
            }

            _t = hasDirectives ? WithoutDirectives(tokens) : tokens;
            _t.Add(new PlToken(PlTokenKind.Eof, PlKeyword.None, tokens[^1].End, 0, false));
            _i = 0;
            _openIf = _openLoop = _openCase = _handlers = 0;
            ParseUnits(root);
        }

        return root;
    }

    private bool IsCreatePrefix(PlToken token)
    {
        if (token.Keyword is PlKeyword.Or or PlKeyword.Replace or PlKeyword.Editionable or PlKeyword.Noneditionable)
        {
            return true;
        }

        var word = _text.AsSpan(token.Start, token.Length);
        return word.Equals("AND", StringComparison.OrdinalIgnoreCase) || word.Equals("COMPILE", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("RESOLVE", StringComparison.OrdinalIgnoreCase);
    }

    private bool TrySqlPlusLine(int start, out int end)
    {
        var text = _text;
        end = start;
        var c = text[start];
        if (c != '@')
        {
            var i = start;
            while (i < text.Length && char.IsAsciiLetter(text[i]))
            {
                i++;
            }

            if (i == start || (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$' or '#' or '.')) ||
                !SqlPlusLookup.Contains(text.AsSpan(start, i - start)))
            {
                return false;
            }
        }

        // A trailing '-' continues the command on the next line.
        var from = start;
        while (true)
        {
            var newLine = text.IndexOf('\n', from);
            end = newLine < 0 ? text.Length : newLine;
            var line = text.AsSpan(from, end - from).TrimEnd();
            if (newLine < 0 || line.Length == 0 || line[^1] != '-' || line.EndsWith("--", StringComparison.Ordinal))
            {
                return true;
            }

            from = newLine + 1;
        }
    }

    private int EndOfSlashTerminatedText(int from)
    {
        var text = _text;
        var lineStart = from;
        while (lineStart < text.Length)
        {
            var newLine = text.IndexOf('\n', lineStart);
            var lineEnd = newLine < 0 ? text.Length : newLine;
            if (text.AsSpan(lineStart, lineEnd - lineStart).Trim() is "/")
            {
                return lineEnd;
            }

            lineStart = lineEnd + 1;
        }

        return text.Length;
    }

    /// <summary>
    /// Conditional compilation: keeps the $IF branch and drops $ELSIF / $ELSE branches and $ERROR directives, so
    /// the parser sees one consistent variant of the source.
    /// </summary>
    private static List<PlToken> WithoutDirectives(List<PlToken> source)
    {
        var result = new List<PlToken>(source.Count);
        var i = 0;
        Branch(emit: true, nested: false);
        return result;

        void Branch(bool emit, bool nested)
        {
            while (i < source.Count)
            {
                var token = source[i];
                if (token.Kind != PlTokenKind.Directive)
                {
                    if (emit)
                    {
                        result.Add(token);
                    }

                    i++;
                    continue;
                }

                switch (token.Keyword)
                {
                    case PlKeyword.DirectiveIf:
                        SkipPast(PlKeyword.DirectiveThen);
                        Branch(emit, nested: true);
                        while (i < source.Count && source[i].Keyword is PlKeyword.DirectiveElsif or PlKeyword.DirectiveElse)
                        {
                            if (source[i].Keyword == PlKeyword.DirectiveElsif)
                            {
                                SkipPast(PlKeyword.DirectiveThen);
                            }
                            else
                            {
                                i++;
                            }

                            Branch(emit: false, nested: true);
                        }

                        if (i < source.Count && source[i].Keyword == PlKeyword.DirectiveEnd)
                        {
                            i++;
                        }

                        break;
                    case PlKeyword.DirectiveError:
                        SkipPast(PlKeyword.DirectiveEnd);
                        break;
                    case PlKeyword.DirectiveElsif or PlKeyword.DirectiveElse or PlKeyword.DirectiveEnd when nested:
                        return;
                    default:
                        i++;
                        break;
                }
            }
        }

        void SkipPast(PlKeyword keyword)
        {
            i++;
            while (i < source.Count && !(source[i].Kind == PlTokenKind.Directive && source[i].Keyword == keyword))
            {
                i++;
            }

            i = Math.Min(i + 1, source.Count);
        }
    }

    // -----------------------------------------------------------------------------------------------------
    // Units
    // -----------------------------------------------------------------------------------------------------
    private void ParseUnits(PlNode root)
    {
        var afterAnonymousBlock = false;
        while (Cur.Kind != PlTokenKind.Eof)
        {
            var before = _i;
            var start = Cur.Start;
            if (Kw == PlKeyword.Create)
            {
                _i++;
                while (IsCreatePrefix(Cur))
                {
                    _i++;
                }

                switch (Kw)
                {
                    case PlKeyword.Package:
                        ParsePackage(root, start);
                        break;
                    case PlKeyword.Procedure or PlKeyword.Function:
                        ParseRoutine(root, start, RoutineMode.Standalone);
                        break;
                    case PlKeyword.Trigger:
                        ParseTrigger(root, start);
                        break;
                    case PlKeyword.Type:
                        ParseType(root, start);
                        break;
                    default:
                        SkipToEof();
                        root.Add(new PlNode(PlNodeKind.Other, start) { End = PreviousEnd });
                        break;
                }
            }
            else if (Kw is PlKeyword.Procedure or PlKeyword.Function)
            {
                ParseRoutine(root, start, RoutineMode.Standalone);
            }
            else
            {
                while (Cur.Kind == PlTokenKind.LabelOpen)
                {
                    SkipLabel();
                }

                if (Kw is PlKeyword.Declare or PlKeyword.Begin)
                {
                    ParseBlock(root, PlNodeKind.AnonymousBlock);
                    afterAnonymousBlock = true;
                    continue;
                }

                // Text after the end of a unit with no '/' line in between. Worksheet-style scripts do this after
                // an anonymous block and tools accept it; after a stored unit it means an END came too early.
                if (!afterAnonymousBlock)
                {
                    Report("unexpected text after the end of the unit");
                }

                var statement = new PlNode(PlNodeKind.SqlStatement, start);
                root.Add(statement);
                var depth = 0;
                while (Cur.Kind != PlTokenKind.Eof)
                {
                    var kind = Cur.Kind;
                    depth += kind == PlTokenKind.LParen ? 1 : kind == PlTokenKind.RParen ? -1 : 0;
                    _i++;
                    if (kind == PlTokenKind.Semicolon && depth <= 0)
                    {
                        break;
                    }
                }

                statement.End = PreviousEnd;
                afterAnonymousBlock = true;
                continue;
            }

            afterAnonymousBlock = false;

            if (_i == before)
            {
                Report("unexpected text");
                SkipToEof();
            }
        }
    }

    private void ParsePackage(PlNode parent, int start)
    {
        _i++;
        var body = Kw == PlKeyword.Body;
        if (body)
        {
            _i++;
        }

        var node = new PlNode(body ? PlNodeKind.PackageBody : PlNodeKind.PackageSpec, start);
        parent.Add(node);
        ReadName(node);
        if (!SkipWrapped() && ExpectIsAs())
        {
            ParseDeclarations(node, compoundTrigger: false);
            if (Kw == PlKeyword.Begin)
            {
                ParseBeginSection(node);
            }

            ParseEnd();
        }

        node.End = PreviousEnd;
    }

    private void ParseTrigger(PlNode parent, int start)
    {
        _i++;
        var node = new PlNode(PlNodeKind.Trigger, start);
        parent.Add(node);
        ReadName(node);
        if (SkipWrapped())
        {
            node.End = PreviousEnd;
            return;
        }

        var depth = 0;
        while (Cur.Kind != PlTokenKind.Eof)
        {
            if (Cur.Kind == PlTokenKind.LParen)
            {
                depth++;
            }
            else if (Cur.Kind == PlTokenKind.RParen)
            {
                depth--;
            }
            else if (depth <= 0 && Kw is PlKeyword.Declare or PlKeyword.Begin or PlKeyword.Compound or PlKeyword.Call)
            {
                break;
            }

            _i++;
        }

        switch (Kw)
        {
            case PlKeyword.Declare or PlKeyword.Begin:
                ParseBlock(node, PlNodeKind.AnonymousBlock);
                break;
            case PlKeyword.Compound:
                _i += 2;
                while (true)
                {
                    ParseDeclarations(node, compoundTrigger: true);
                    if (Kw is not (PlKeyword.Before or PlKeyword.After or PlKeyword.Instead))
                    {
                        break;
                    }

                    // BEFORE STATEMENT IS ... BEGIN ... END BEFORE STATEMENT;
                    var section = new PlNode(PlNodeKind.AnonymousBlock, Cur.Start);
                    node.Add(section);
                    if (ExpectIsAs())
                    {
                        ParseDeclarations(section, compoundTrigger: false);
                        if (Kw == PlKeyword.Begin)
                        {
                            ParseBeginSection(section);
                        }

                        if (Kw == PlKeyword.End)
                        {
                            _i++;
                            while (Cur.Kind is not (PlTokenKind.Semicolon or PlTokenKind.Eof))
                            {
                                _i++;
                            }

                            ExpectSemicolon();
                        }
                        else
                        {
                            Report("END expected");
                        }
                    }

                    section.End = PreviousEnd;
                }

                ParseEnd();
                break;
            case PlKeyword.Call:
                SkipToEof();
                break;
            default:
                Report("trigger body expected");
                break;
        }

        node.End = PreviousEnd;
    }

    private void ParseType(PlNode parent, int start)
    {
        _i++;
        var body = Kw == PlKeyword.Body;
        if (body)
        {
            _i++;
        }

        var node = new PlNode(body ? PlNodeKind.TypeBody : PlNodeKind.TypeSpec, start);
        parent.Add(node);
        ReadName(node);
        if (SkipWrapped())
        {
            node.End = PreviousEnd;
            return;
        }

        if (body)
        {
            if (ExpectIsAs())
            {
                while (true)
                {
                    if (Kw == PlKeyword.End || Cur.Kind == PlTokenKind.Eof)
                    {
                        break;
                    }

                    var memberStart = Cur.Start;
                    if (AtMember(out var keywordIndex))
                    {
                        _i = keywordIndex;
                        ParseRoutine(node, memberStart, RoutineMode.Declaration);
                    }
                    else if (Cur.Kind == PlTokenKind.Semicolon)
                    {
                        _i++;
                    }
                    else
                    {
                        Report("MEMBER or STATIC routine expected");
                        ParseSimple(node, PlNodeKind.Other, nameOffset: -1);
                    }
                }

                ParseEnd();
            }

            node.End = PreviousEnd;
            return;
        }

        // Specification: only an object type has members; collections and incomplete types are skipped.
        var objectType = false;
        while (Cur.Kind != PlTokenKind.Eof)
        {
            if (Cur.Kind == PlTokenKind.Semicolon)
            {
                _i++;
                break;
            }

            if (Kw is PlKeyword.Object or PlKeyword.Under)
            {
                objectType = true;
                _i++;
            }
            else if (Cur.Kind == PlTokenKind.LParen)
            {
                if (objectType)
                {
                    ParseTypeElements(node);
                    objectType = false;
                }
                else
                {
                    SkipBalanced();
                }
            }
            else if (Kw == PlKeyword.Create)
            {
                break;
            }
            else
            {
                _i++;
            }
        }

        node.End = PreviousEnd;
    }

    private void ParseTypeElements(PlNode type)
    {
        _i++;
        while (true)
        {
            if (Cur.Kind == PlTokenKind.RParen)
            {
                _i++;
                return;
            }

            if (Cur.Kind is PlTokenKind.Eof or PlTokenKind.Semicolon)
            {
                Report("')' expected");
                return;
            }

            var start = Cur.Start;
            Stop stop;
            if (AtMember(out var keywordIndex))
            {
                _i = keywordIndex;
                ParseRoutine(type, start, RoutineMode.TypeSpecMember);
                stop = Cur.Kind == PlTokenKind.Comma ? Stop.Comma : Cur.Kind == PlTokenKind.RParen ? Stop.RParen : Stop.None;
            }
            else
            {
                var element = new PlNode(Kw == PlKeyword.Pragma ? PlNodeKind.Pragma : PlNodeKind.Variable, start);
                if (element.Kind == PlNodeKind.Variable && IsNameToken(Cur))
                {
                    element.Name = TextOf(Cur);
                    element.NameStart = start;
                }

                _i++;
                stop = SkipUntil(Stop.Comma | Stop.RParen);
                element.End = PreviousEnd;
                type.Add(element);
            }

            if (stop == Stop.Comma)
            {
                _i++;
            }
            else if (stop != Stop.RParen)
            {
                Report("',' or ')' expected");
                return;
            }
        }
    }

    /// <summary>Looks past NOT FINAL / OVERRIDING / MEMBER / STATIC / CONSTRUCTOR / MAP / ORDER for a routine keyword.</summary>
    private bool AtMember(out int keywordIndex)
    {
        var j = _i;
        while (j < _t.Count - 1 && _t[j].Keyword is PlKeyword.Not or PlKeyword.Final or PlKeyword.Instantiable or PlKeyword.Overriding
                   or PlKeyword.Member or PlKeyword.Static or PlKeyword.Constructor or PlKeyword.Map or PlKeyword.Order)
        {
            j++;
        }

        keywordIndex = j;
        return _t[j].Keyword is PlKeyword.Procedure or PlKeyword.Function;
    }

    // -----------------------------------------------------------------------------------------------------
    // Routines and declarations
    // -----------------------------------------------------------------------------------------------------
    private void ParseRoutine(PlNode parent, int start, RoutineMode mode)
    {
        var node = new PlNode(Kw == PlKeyword.Procedure ? PlNodeKind.Procedure : PlNodeKind.Function, start) { KeywordStart = Cur.Start };
        parent.Add(node);
        _i++;
        if (!ReadName(node))
        {
            Report("routine name expected");
        }

        if (SkipWrapped())
        {
            node.End = PreviousEnd;
            return;
        }

        if (Cur.Kind == PlTokenKind.LParen)
        {
            ParseParameters(node);
        }

        var memberStops = mode == RoutineMode.TypeSpecMember ? Stop.Comma | Stop.RParen : Stop.None;
        var stop = SkipUntil(Stop.IsAs | memberStops);
        switch (stop)
        {
            case Stop.Semicolon:
                _i++;
                break;
            case Stop.Comma or Stop.RParen:
                break;
            case Stop.IsAs:
                _i++;
                if (Kw is PlKeyword.Language or PlKeyword.External || mode == RoutineMode.TypeSpecMember)
                {
                    // Call specification: implemented in Java or C.
                    stop = SkipUntil(memberStops);
                    if (stop == Stop.Semicolon)
                    {
                        _i++;
                    }
                    else if (mode != RoutineMode.TypeSpecMember && stop != Stop.Eof)
                    {
                        Report("';' expected");
                    }

                    break;
                }

                node.HasBody = true;
                if (Kw == PlKeyword.Declare)
                {
                    _i++;
                }

                ParseDeclarations(node, compoundTrigger: false);
                if (Kw == PlKeyword.Begin)
                {
                    ParseBeginSection(node);
                    ParseEnd();
                }
                else
                {
                    Report("BEGIN expected");
                }

                break;
            default:
                if (mode != RoutineMode.Standalone || stop != Stop.Eof)
                {
                    Report("IS, AS or ';' expected");
                }

                break;
        }

        node.End = PreviousEnd;
    }

    private void ParseParameters(PlNode routine)
    {
        _i++;
        while (true)
        {
            if (Cur.Kind == PlTokenKind.RParen)
            {
                _i++;
                return;
            }

            var parameter = new PlNode(PlNodeKind.Parameter, Cur.Start);
            if (IsNameToken(Cur))
            {
                parameter.Name = TextOf(Cur);
                parameter.NameStart = Cur.Start;
            }

            var stop = SkipUntil(Stop.Comma | Stop.RParen);
            parameter.End = PreviousEnd;
            routine.Add(parameter);
            if (stop == Stop.Comma)
            {
                _i++;
            }
            else if (stop == Stop.RParen)
            {
                _i++;
                return;
            }
            else
            {
                Report("')' expected");
                return;
            }
        }
    }

    /// <summary>Declarations up to (not including) BEGIN or END.</summary>
    private void ParseDeclarations(PlNode parent, bool compoundTrigger)
    {
        while (true)
        {
            var token = Cur;
            if (token.Kind == PlTokenKind.Eof)
            {
                return;
            }

            if (token.Kind == PlTokenKind.Semicolon)
            {
                _i++;
                continue;
            }

            var next = Peek(1);
            switch (token.Keyword)
            {
                case PlKeyword.Begin or PlKeyword.End or PlKeyword.Create:
                    return;
                case PlKeyword.Before or PlKeyword.After or PlKeyword.Instead when compoundTrigger:
                    return;
                case PlKeyword.Procedure or PlKeyword.Function:
                    ParseRoutine(parent, token.Start, RoutineMode.Declaration);
                    continue;
                case PlKeyword.Type when IsNameToken(next) && Peek(2).Keyword == PlKeyword.Is:
                    ParseSimple(parent, PlNodeKind.TypeDeclaration, nameOffset: 1);
                    continue;
                case PlKeyword.Subtype when IsNameToken(next):
                    ParseSimple(parent, PlNodeKind.Subtype, nameOffset: 1);
                    continue;
                case PlKeyword.Cursor when IsNameToken(next):
                    ParseSimple(parent, PlNodeKind.Cursor, nameOffset: 1);
                    continue;
                case PlKeyword.Pragma:
                    ParseSimple(parent, PlNodeKind.Pragma, nameOffset: -1);
                    continue;
                case PlKeyword.Declare:
                    _i++;
                    continue;
                default:
                    break;
            }

            if (IsNameToken(token) && token.Keyword is not (PlKeyword.If or PlKeyword.Loop or PlKeyword.For or PlKeyword.While or PlKeyword.Case
                    or PlKeyword.Elsif or PlKeyword.Else or PlKeyword.When or PlKeyword.Then or PlKeyword.Exception))
            {
                var kind = next.Keyword switch
                {
                    PlKeyword.Constant => PlNodeKind.Constant,
                    PlKeyword.Exception => PlNodeKind.ExceptionDeclaration,
                    _ => PlNodeKind.Variable,
                };
                ParseSimple(parent, kind, nameOffset: 0);
            }
            else
            {
                Report("declaration expected");
                ParseSimple(parent, PlNodeKind.Other, nameOffset: -1);
            }
        }
    }

    // -----------------------------------------------------------------------------------------------------
    // Blocks and statements
    // -----------------------------------------------------------------------------------------------------
    private void ParseBlock(PlNode parent, PlNodeKind kind)
    {
        var node = new PlNode(kind, Cur.Start);
        parent.Add(node);
        if (Kw == PlKeyword.Declare)
        {
            _i++;
            ParseDeclarations(node, compoundTrigger: false);
        }

        if (Kw == PlKeyword.Begin)
        {
            ParseBeginSection(node);
            ParseEnd();
        }
        else
        {
            Report("BEGIN expected");
        }

        node.End = PreviousEnd;
    }

    /// <summary>BEGIN statements [EXCEPTION handlers], stopping in front of END.</summary>
    private void ParseBeginSection(PlNode node)
    {
        _i++;
        ParseStatements(node, PlKeyword.None);
        if (Kw != PlKeyword.Exception)
        {
            return;
        }

        _i++;
        _handlers++;
        while (Kw == PlKeyword.When)
        {
            var handler = new PlNode(PlNodeKind.Handler, Cur.Start);
            node.Add(handler);
            _i++;
            SkipCondition();
            ParseStatements(handler, PlKeyword.None);
            handler.End = PreviousEnd;
        }

        _handlers--;
    }

    private void ParseStatements(PlNode parent, PlKeyword closer)
    {
        while (true)
        {
            var token = Cur;
            switch (token.Kind)
            {
                case PlTokenKind.Eof:
                    return;
                case PlTokenKind.Semicolon:
                    _i++;
                    continue;
                case PlTokenKind.LabelOpen:
                    SkipLabel();
                    continue;
                default:
                    break;
            }

            switch (token.Keyword)
            {
                case PlKeyword.End:
                    {
                        var after = Peek(1).Keyword;
                        var variant = after is PlKeyword.If or PlKeyword.Loop or PlKeyword.Case ? after : PlKeyword.None;
                        if (variant == closer || variant == PlKeyword.None || IsOpen(variant))
                        {
                            return;
                        }

                        Report($"END {variant.ToString().ToUpperInvariant()} without a matching {variant.ToString().ToUpperInvariant()}");
                        _i += 2;
                        continue;
                    }

                case PlKeyword.Exception or PlKeyword.Procedure or PlKeyword.Function or PlKeyword.Create:
                    return;
                case PlKeyword.Elsif when _openIf > 0:
                case PlKeyword.Else when _openIf > 0 || _openCase > 0:
                case PlKeyword.When when _openCase > 0 || _handlers > 0:
                    return;
                case PlKeyword.Elsif or PlKeyword.Else or PlKeyword.When or PlKeyword.Then:
                    Report($"unexpected {token.Keyword.ToString().ToUpperInvariant()}");
                    _i++;
                    continue;
                case PlKeyword.Declare or PlKeyword.Begin:
                    ParseBlock(parent, PlNodeKind.Block);
                    continue;
                case PlKeyword.If:
                    ParseIf(parent);
                    continue;
                case PlKeyword.Case:
                    ParseCase(parent);
                    continue;
                case PlKeyword.Loop or PlKeyword.While or PlKeyword.For:
                    ParseLoop(parent);
                    continue;
                case PlKeyword.Select or PlKeyword.Insert or PlKeyword.Update or PlKeyword.Delete or PlKeyword.Merge or PlKeyword.With
                    or PlKeyword.Commit or PlKeyword.Rollback or PlKeyword.Savepoint or PlKeyword.Lock or PlKeyword.Forall:
                    ParseSimple(parent, PlNodeKind.Sql, nameOffset: -1);
                    continue;
                default:
                    ParseSimple(parent, PlNodeKind.Statement, nameOffset: -1);
                    continue;
            }
        }
    }

    private bool IsOpen(PlKeyword variant) => variant switch
    {
        PlKeyword.If => _openIf > 0,
        PlKeyword.Loop => _openLoop > 0,
        PlKeyword.Case => _openCase > 0,
        _ => false,
    };

    private void ParseIf(PlNode parent)
    {
        var node = new PlNode(PlNodeKind.If, Cur.Start);
        parent.Add(node);
        _openIf++;
        _i++;
        SkipCondition();
        ParseStatements(node, PlKeyword.If);
        while (true)
        {
            if (Kw == PlKeyword.Elsif)
            {
                _i++;
                SkipCondition();
            }
            else if (Kw == PlKeyword.Else)
            {
                _i++;
            }
            else
            {
                break;
            }

            ParseStatements(node, PlKeyword.If);
        }

        _openIf--;
        ParseEnd(PlKeyword.If);
        node.End = PreviousEnd;
    }

    private void ParseCase(PlNode parent)
    {
        var node = new PlNode(PlNodeKind.Case, Cur.Start);
        parent.Add(node);
        _i++;
        if (Kw != PlKeyword.When)
        {
            var stop = SkipUntil(Stop.When);
            if (stop != Stop.When)
            {
                Report("WHEN expected");
                if (stop == Stop.Semicolon)
                {
                    _i++;
                }

                node.End = PreviousEnd;
                return;
            }
        }

        _openCase++;
        while (Kw == PlKeyword.When)
        {
            _i++;
            SkipCondition();
            ParseStatements(node, PlKeyword.Case);
        }

        if (Kw == PlKeyword.Else)
        {
            _i++;
            ParseStatements(node, PlKeyword.Case);
        }

        _openCase--;
        ParseEnd(PlKeyword.Case);
        node.End = PreviousEnd;
    }

    private void ParseLoop(PlNode parent)
    {
        var node = new PlNode(PlNodeKind.Loop, Cur.Start);
        parent.Add(node);
        if (Kw != PlKeyword.Loop)
        {
            _i++;
            var stop = SkipUntil(Stop.Loop);
            if (stop != Stop.Loop)
            {
                Report("LOOP expected");
                if (stop == Stop.Semicolon)
                {
                    _i++;
                }

                node.End = PreviousEnd;
                return;
            }
        }

        _openLoop++;
        _i++;
        ParseStatements(node, PlKeyword.Loop);
        _openLoop--;
        ParseEnd(PlKeyword.Loop);
        node.End = PreviousEnd;
    }

    /// <summary>A statement or declaration that ends at the next ';'. Its inside is not parsed.</summary>
    private void ParseSimple(PlNode parent, PlNodeKind kind, int nameOffset)
    {
        var node = new PlNode(kind, Cur.Start);
        parent.Add(node);
        if (nameOffset >= 0)
        {
            var name = Peek(nameOffset);
            node.Name = TextOf(name);
            node.NameStart = name.Start;
            _i += nameOffset;
        }

        _i++;
        if (SkipUntil(Stop.None) == Stop.Semicolon)
        {
            _i++;
        }
        else
        {
            Report("';' expected");
        }

        node.End = PreviousEnd;
    }

    /// <summary>The condition of IF / ELSIF / WHEN, up to and including THEN.</summary>
    private void SkipCondition()
    {
        var stop = SkipUntil(Stop.Then);
        if (stop == Stop.Then)
        {
            _i++;
            return;
        }

        Report("THEN expected");
        if (stop == Stop.Semicolon)
        {
            _i++;
        }
    }

    /// <summary>END [IF | LOOP | CASE] [label] ';'. Reports and consumes nothing when a different END is there.</summary>
    private void ParseEnd(PlKeyword variant = PlKeyword.None)
    {
        var after = Peek(1).Keyword;
        var found = after is PlKeyword.If or PlKeyword.Loop or PlKeyword.Case ? after : PlKeyword.None;
        if (Kw != PlKeyword.End || found != variant)
        {
            Report(variant == PlKeyword.None ? "END expected" : $"END {variant.ToString().ToUpperInvariant()} expected");
            return;
        }

        _i += variant == PlKeyword.None ? 1 : 2;
        if (variant != PlKeyword.If && IsNameToken(Cur) && Peek(1).Kind == PlTokenKind.Semicolon)
        {
            ReadName(null);
        }

        ExpectSemicolon();
    }

    private void ExpectSemicolon()
    {
        if (Cur.Kind == PlTokenKind.Semicolon)
        {
            _i++;
        }
        else if (Cur.Kind != PlTokenKind.Eof)
        {
            Report("';' expected");
        }
    }

    private bool ExpectIsAs()
    {
        if (SkipUntil(Stop.IsAs) == Stop.IsAs)
        {
            _i++;
            return true;
        }

        Report("IS or AS expected");
        return false;
    }

    // -----------------------------------------------------------------------------------------------------
    // Token helpers
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Advances to the next token the caller can act on and returns what it is, without consuming it. Parentheses
    /// and CASE ... END are skipped as a whole. A ';', an END that closes no CASE, and keywords that can only
    /// start a statement or a declaration always stop the scan: that is what keeps a missing ';' or ')' local.
    /// </summary>
    private Stop SkipUntil(Stop stops)
    {
        var parens = 0;
        var cases = 0;
        var tokens = _t;
        var last = tokens.Count - 1;
        for (; ; _i++)
        {
            if (_i >= last)
            {
                _i = last;
                return Stop.Eof;
            }

            var token = tokens[_i];
            switch (token.Kind)
            {
                case PlTokenKind.Semicolon:
                    return Stop.Semicolon;
                case PlTokenKind.LParen:
                    parens++;
                    continue;
                case PlTokenKind.RParen:
                    if (parens > 0)
                    {
                        parens--;
                    }
                    else if ((stops & Stop.RParen) != 0)
                    {
                        return Stop.RParen;
                    }

                    continue;
                case PlTokenKind.Comma:
                    if (parens == 0 && cases == 0 && (stops & Stop.Comma) != 0)
                    {
                        return Stop.Comma;
                    }

                    continue;
                case PlTokenKind.Identifier:
                    break;
                default:
                    continue;
            }

            var open = parens == 0 && cases == 0;
            switch (token.Keyword)
            {
                case PlKeyword.Case:
                    cases++;
                    break;
                case PlKeyword.End:
                    if (cases == 0)
                    {
                        return Stop.End;
                    }

                    cases--;
                    break;
                case PlKeyword.Then when open && (stops & Stop.Then) != 0:
                    return Stop.Then;
                case PlKeyword.When when open && (stops & Stop.When) != 0:
                    return Stop.When;
                case PlKeyword.Loop:
                    if ((stops & Stop.Loop) == 0)
                    {
                        return Stop.Hard;
                    }

                    if (open)
                    {
                        return Stop.Loop;
                    }

                    break;
                case PlKeyword.Is or PlKeyword.As when open && (stops & Stop.IsAs) != 0:
                    // RETURN SELF AS RESULT
                    if (token.Keyword == PlKeyword.As && _i > 0 && tokens[_i - 1].Keyword == PlKeyword.Self && tokens[_i + 1].Keyword == PlKeyword.Result)
                    {
                        break;
                    }

                    return Stop.IsAs;
                case PlKeyword.Begin or PlKeyword.Declare or PlKeyword.If or PlKeyword.Elsif:
                    return Stop.Hard;
                case PlKeyword.Procedure or PlKeyword.Function when parens == 0:
                    // Inside parentheses they are legal: ACCESSIBLE BY (PROCEDURE p, FUNCTION f).
                    return Stop.Hard;
                default:
                    break;
            }
        }
    }

    private void SkipBalanced()
    {
        var depth = 0;
        while (Cur.Kind is not (PlTokenKind.Eof or PlTokenKind.Semicolon))
        {
            if (Cur.Kind == PlTokenKind.LParen)
            {
                depth++;
            }
            else if (Cur.Kind == PlTokenKind.RParen && --depth <= 0)
            {
                _i++;
                return;
            }

            _i++;
        }
    }

    private void SkipLabel()
    {
        _i++;
        for (var n = 0; n < 4 && Cur.Kind is not (PlTokenKind.LabelClose or PlTokenKind.Semicolon or PlTokenKind.Eof); n++)
        {
            _i++;
        }

        if (Cur.Kind == PlTokenKind.LabelClose)
        {
            _i++;
        }
    }

    private void SkipToEof() => _i = _t.Count - 1;

    /// <summary>Wrapped (obfuscated) source has no structure to read.</summary>
    private bool SkipWrapped()
    {
        if (Kw != PlKeyword.Wrapped)
        {
            return false;
        }

        SkipToEof();
        return true;
    }

    private PlToken Peek(int offset) => _t[Math.Min(_i + offset, _t.Count - 1)];

    private string TextOf(PlToken token) => _text.Substring(token.Start, token.Length);

    private static bool IsNameToken(PlToken token) => token.Kind switch
    {
        PlTokenKind.QuotedIdentifier or PlTokenKind.Substitution => true,
        PlTokenKind.Identifier => token.Keyword is not (PlKeyword.Is or PlKeyword.As or PlKeyword.Begin or PlKeyword.End or PlKeyword.Return or PlKeyword.Declare),
        _ => false,
    };

    /// <summary>Reads a possibly qualified name (schema.package, &amp;schema..package).</summary>
    private bool ReadName(PlNode? node)
    {
        if (!IsNameToken(Cur))
        {
            return false;
        }

        var start = Cur.Start;
        _i++;
        while (true)
        {
            var previous = _t[_i - 1];
            if (Cur.Kind == PlTokenKind.Dot && IsNameToken(Peek(1)))
            {
                _i += 2;
            }
            else if (previous.Kind == PlTokenKind.Substitution && _text[previous.End - 1] == '.' && Cur.Start == previous.End && IsNameToken(Cur))
            {
                _i++;
            }
            else
            {
                break;
            }
        }

        if (node is not null)
        {
            node.Name = _text[start..PreviousEnd];
            node.NameStart = start;
        }

        return true;
    }

    private void Report(string message)
    {
        if (_diagnostics.Count < MaxDiagnostics)
        {
            _diagnostics.Add(new PlDiagnostic(Cur.Start, message));
        }
    }
}
