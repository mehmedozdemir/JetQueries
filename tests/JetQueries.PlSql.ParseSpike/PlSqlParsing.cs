using System.Diagnostics;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;

namespace JetQueries.PlSql.ParseSpike;

/// <summary>Result of parsing one text.</summary>
internal sealed record ParseOutcome(
    PlSqlParser.Sql_scriptContext Tree,
    CommonTokenStream Tokens,
    int SyntaxErrors,
    string? FirstError,
    bool UsedFullLl,
    double Milliseconds);

/// <summary>How the parser predicts and recovers.</summary>
internal enum ParseMode
{
    /// <summary>SLL that bails on the first error, then a full-LL pass with recovery if needed.</summary>
    TwoStage,

    /// <summary>One pass: SLL prediction with error recovery. Faster on broken input, slightly less precise.</summary>
    SllWithRecovery,
}

internal static class PlSqlParsing
{
    /// <summary>
    /// Parses with ANTLR's recommended two-stage strategy: fast SLL prediction that bails on the first error,
    /// then full LL with error recovery only when SLL failed. Valid input almost always finishes in stage one;
    /// input with syntax errors always pays for both stages.
    /// </summary>
    public static ParseOutcome Parse(string text, ParseMode mode = ParseMode.TwoStage)
    {
        var started = Stopwatch.GetTimestamp();
        var errors = new ErrorCollector();

        var lexer = new PlSqlLexer(CharStreams.fromString(text));
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errors);
        var tokens = new CommonTokenStream(lexer);

        var parser = new PlSqlParser(tokens);
        parser.RemoveErrorListeners();
        parser.Interpreter.PredictionMode = PredictionMode.SLL;

        PlSqlParser.Sql_scriptContext tree;
        var usedFullLl = false;
        if (mode == ParseMode.SllWithRecovery)
        {
            parser.AddErrorListener(errors);
            tree = parser.sql_script();
            return new ParseOutcome(tree, tokens, errors.Count, errors.First, false, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        parser.ErrorHandler = new BailErrorStrategy();
        try
        {
            tree = parser.sql_script();
        }
        catch (ParseCanceledException)
        {
            usedFullLl = true;
            tokens.Seek(0);
            parser.Reset();
            parser.AddErrorListener(errors);
            parser.ErrorHandler = new DefaultErrorStrategy();
            parser.Interpreter.PredictionMode = PredictionMode.LL;
            tree = parser.sql_script();
        }

        return new ParseOutcome(
            tree,
            tokens,
            errors.Count,
            errors.First,
            usedFullLl,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Tokenizes only; what syntax colouring needs.</summary>
    public static (int Tokens, double Milliseconds) Lex(string text)
    {
        var started = Stopwatch.GetTimestamp();
        var lexer = new PlSqlLexer(CharStreams.fromString(text));
        lexer.RemoveErrorListeners();
        var count = lexer.GetAllTokens().Count;
        return (count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Tokens on the default channel, without EOF.</summary>
    public static List<IToken> SignificantTokens(CommonTokenStream tokens) =>
        tokens.GetTokens().Where(static t => t.Channel == TokenConstants.DefaultChannel && t.Type != TokenConstants.EOF).ToList();

    /// <summary>
    /// For every terminal: the chain of rule indices from the root down to it, keyed by the token's start offset.
    /// Two trees that give a token the same chain agree on "where in the grammar" that token is.
    /// </summary>
    public static Dictionary<int, int[]> TerminalRulePaths(IParseTree root)
    {
        var paths = new Dictionary<int, int[]>();
        var stack = new List<int>();
        Visit(root);
        return paths;

        void Visit(IParseTree node)
        {
            if (node is ITerminalNode terminal)
            {
                if (terminal.Symbol.Type != TokenConstants.EOF && terminal.Symbol.StartIndex >= 0)
                {
                    paths[terminal.Symbol.StartIndex] = [.. stack];
                }

                return;
            }

            var rule = (ParserRuleContext)node;
            stack.Add(rule.RuleIndex);
            for (var i = 0; i < rule.ChildCount; i++)
            {
                Visit(rule.GetChild(i));
            }

            stack.RemoveAt(stack.Count - 1);
        }
    }

    /// <summary>All contexts of the given rules as (rule, start offset, stop offset).</summary>
    public static HashSet<(int Rule, int Start, int Stop)> Contexts(IParseTree root, HashSet<int> rules)
    {
        var found = new HashSet<(int, int, int)>();
        Visit(root);
        return found;

        void Visit(IParseTree node)
        {
            if (node is not ParserRuleContext rule)
            {
                return;
            }

            // Contexts created during error recovery can lack a stop token or be empty.
            if (rules.Contains(rule.RuleIndex) && rule.Start is not null && rule.Stop is not null &&
                rule.Stop.StopIndex >= rule.Start.StartIndex)
            {
                found.Add((rule.RuleIndex, rule.Start.StartIndex, rule.Stop.StopIndex));
            }

            for (var i = 0; i < rule.ChildCount; i++)
            {
                Visit(rule.GetChild(i));
            }
        }
    }

    /// <summary>
    /// Every query block with the names of the tables referenced directly in its FROM clause (tables of nested
    /// query blocks belong to those blocks).
    /// </summary>
    public static List<QueryBlockInfo> QueryBlocks(IParseTree root)
    {
        var blocks = new List<QueryBlockInfo>();
        var open = new Stack<QueryBlockInfo>();
        Visit(root, insideTableRef: false);
        return blocks;

        void Visit(IParseTree node, bool insideTableRef)
        {
            if (node is not ParserRuleContext rule || rule.Start is null)
            {
                return;
            }

            var isBlock = rule.RuleIndex == PlSqlParser.RULE_query_block;
            if (isBlock)
            {
                var info = new QueryBlockInfo(rule.Start.StartIndex, rule.Stop?.StopIndex ?? int.MaxValue, []);
                blocks.Add(info);
                open.Push(info);
                insideTableRef = false;
            }
            else if (rule.RuleIndex == PlSqlParser.RULE_table_ref_aux)
            {
                insideTableRef = true;
            }
            else if (insideTableRef && rule.RuleIndex == PlSqlParser.RULE_tableview_name && open.Count > 0 && rule.Stop is not null)
            {
                open.Peek().Tables.Add((rule.GetText(), rule.Stop.StopIndex));
                return;
            }

            for (var i = 0; i < rule.ChildCount; i++)
            {
                Visit(rule.GetChild(i), insideTableRef);
            }

            if (isBlock)
            {
                open.Pop();
            }
        }
    }

    private sealed class ErrorCollector : IAntlrErrorListener<int>, IAntlrErrorListener<IToken>
    {
        public int Count { get; private set; }

        public string? First { get; private set; }

        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) =>
            Add(line, charPositionInLine, msg);

        public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) =>
            Add(line, charPositionInLine, msg);

        private void Add(int line, int column, string message)
        {
            Count++;
            First ??= $"{line}:{column} {message}";
        }
    }
}

/// <summary>A query block and the tables its FROM clause references directly.</summary>
internal sealed record QueryBlockInfo(int Start, int Stop, List<(string Name, int Stop)> Tables);
