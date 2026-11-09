using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>What Check Formula found wrong with one span of a formula (spec 0014).</summary>
internal enum FormulaProblemKind
{
    /// <summary>A <c>)</c> with nothing left to close.</summary>
    ExtraCloseBracket,

    /// <summary>A <c>(</c> that is never closed.</summary>
    UnclosedBracket,

    /// <summary>A comma outside every function: an earlier <c>)</c> closed the call too soon.</summary>
    CommaOutsideFunction,

    /// <summary>A string, quoted sheet name, structured reference or array constant that never ends.</summary>
    Unterminated,

    /// <summary>A LET or LAMBDA whose arguments don't have the right shape.</summary>
    BadShape,

    /// <summary>A name that resolves to nothing.</summary>
    UnknownName,

    /// <summary>A library LAMBDA that isn't defined in this workbook yet.</summary>
    NotLoaded,

    /// <summary>A sheet, table or table column that doesn't exist.</summary>
    UnknownReference,

    /// <summary>Not an error: a LET name that is never used (often the other half of a typo).</summary>
    UnusedName
}

/// <summary>
///     One finding. <see cref="Start"/> and <see cref="Length"/> index the checked text.
///     <see cref="RelatedStart"/> marks a second span worth looking at (the <c>)</c> that closed a
///     call too soon), or -1. <see cref="Suggestion"/> is the nearest valid name, if any.
/// </summary>
internal sealed record FormulaProblem(
    FormulaProblemKind Kind,
    int Start,
    int Length,
    string Message,
    string? Suggestion = null,
    int RelatedStart = -1,
    int RelatedLength = 0)
{
    public bool IsNote => Kind == FormulaProblemKind.UnusedName;
}

/// <summary>
///     The names a formula may use besides native functions and its own LET/LAMBDA names: a
///     snapshot of the workbook taken by <see cref="Commands.CheckFormulaCommand"/>. Every
///     collection compares case-insensitively. An empty <see cref="Sheets"/> or
///     <see cref="Tables"/> means "unknown", and sheet or table checks are skipped.
/// </summary>
internal sealed class FormulaNameContext
{
    public static readonly FormulaNameContext Empty = new();

    /// <summary>Defined names, bare (<c>fares</c>) and sheet-scoped (<c>Sheet1!fares</c>).</summary>
    public HashSet<string> DefinedNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Table name to its column names.</summary>
    public Dictionary<string, HashSet<string>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Sheets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Library LAMBDA names the popup knows, bare and prefixed (they may not be loaded).</summary>
    public HashSet<string> LibraryNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names Excel was asked about and resolved (add-in functions, for instance).</summary>
    public HashSet<string> OtherKnownNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the snapshot was taken (UTC), or null for a context built by hand.</summary>
    public DateTime? TakenUtc { get; set; }
}

internal sealed class FormulaCheckResult
{
    public FormulaCheckResult(string formula, IReadOnlyList<FormulaProblem> problems)
    {
        Formula = formula;
        Problems = problems;
    }

    public string Formula { get; }

    public IReadOnlyList<FormulaProblem> Problems { get; }

    public bool HasErrors => Problems.Any(p => !p.IsNote);
}

/// <summary>
///     Check Formula (spec 0014, issue #419): finds unmatched or misplaced brackets and names that
///     resolve to nothing, with positions, in one left-to-right pass over
///     <see cref="FormulaParser.Tokenize"/>'s tokens. It never builds a tree, so it works on a
///     formula Excel refused, and its cost is linear in the formula's length. Scope follows LET and
///     LAMBDA: a LET name is visible after its comma, to the end of its LET; a LAMBDA parameter to
///     the end of its LAMBDA. Pure: the caller supplies the workbook's names.
/// </summary>
internal static class FormulaChecker
{
    /// <summary>At most this many problems are reported; the rest are counted in the last one.</summary>
    public const int MaxProblems = 30;

    private static readonly Regex CellRef = new(
        @"^\$?([A-Za-z]{1,3})\$?([0-9]{1,7})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ColumnOnly = new(
        @"^\$?[A-Za-z]{1,3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly char[] RefChars = { '!', '[', '\'' };

    private static readonly string[] HiddenPrefixes = { "_xlfn.", "_xlws.", "_xlpm.", "_xludf." };

    private static readonly HashSet<string> TableSpecials = new(StringComparer.OrdinalIgnoreCase)
    {
        "#All", "#Data", "#Headers", "#Totals", "#This Row", "@", ""
    };

    public static FormulaCheckResult Check(string formula, FormulaNameContext? names = null)
    {
        if (formula is null)
            throw new ArgumentNullException(nameof(formula));
        names ??= FormulaNameContext.Empty;

        var problems = new List<FormulaProblem>();
        var text = formula;

        // An unterminated string or bracket hides everything after it from the lexer: report
        // it, then check the text before it.
        var unterminated = FindUnterminated(formula);
        if (unterminated is { } u)
        {
            problems.Add(u.Problem);
            text = formula[..u.CutAt];
        }

        IReadOnlyList<FormulaToken> tokens;
        try
        {
            tokens = FormulaParser.Tokenize(text);
        }
        catch (FormatException)
        {
            return new FormulaCheckResult(formula, problems);
        }

        new Pass(tokens, names, problems).Run();

        // Brackets first (they are usually the cause), then shape, names, and notes last.
        problems = problems.OrderBy(p => Priority(p.Kind)).ThenBy(p => p.Start).ToList();
        if (problems.Count > MaxProblems)
        {
            var more = problems.Count - MaxProblems + 1;
            problems = problems.Take(MaxProblems - 1).ToList();
            problems.Add(new FormulaProblem(FormulaProblemKind.UnusedName, formula.Length, 0,
                $"…and {more} more."));
        }

        return new FormulaCheckResult(formula, problems);
    }

    private static int Priority(FormulaProblemKind kind) => kind switch
    {
        FormulaProblemKind.Unterminated => 0,
        FormulaProblemKind.ExtraCloseBracket => 1,
        FormulaProblemKind.CommaOutsideFunction => 1,
        FormulaProblemKind.UnclosedBracket => 1,
        FormulaProblemKind.BadShape => 2,
        FormulaProblemKind.UnusedName => 9,
        _ => 5
    };

    // ------------------------------------------------------------ unterminated

    private static (FormulaProblem Problem, int CutAt)? FindUnterminated(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var start = i;
            switch (c)
            {
                case '"':
                    i++;
                    while (i < s.Length && !(s[i] == '"' && (i + 1 >= s.Length || s[i + 1] != '"')))
                        i += s[i] == '"' ? 2 : 1;
                    if (i >= s.Length)
                        return (new FormulaProblem(FormulaProblemKind.Unterminated, start, 1,
                            "This \" starts text that never ends: a closing \" is missing."), start);
                    i++;
                    continue;
                case '\'':
                    try
                    {
                        i = FormulaParser.SkipSingleQuoted(s, i);
                    }
                    catch (FormatException)
                    {
                        return (new FormulaProblem(FormulaProblemKind.Unterminated, start, 1,
                            "This ' starts a sheet name that never ends: a closing ' is missing."), start);
                    }

                    continue;
                case '[':
                    try
                    {
                        i = FormulaParser.SkipBrackets(s, i);
                    }
                    catch (FormatException)
                    {
                        return (new FormulaProblem(FormulaProblemKind.Unterminated, start, 1,
                            "This [ is never closed: a ] is missing."), start);
                    }

                    continue;
                case ']':
                    return (new FormulaProblem(FormulaProblemKind.Unterminated, start, 1,
                        "This ] has no [ to close."), start);
                case '{':
                    var close = s.IndexOf('}', i);
                    if (close < 0)
                        return (new FormulaProblem(FormulaProblemKind.Unterminated, start, 1,
                            "This { starts an array constant that is never closed: a } is missing."), start);
                    // Strings inside an array constant can hold '}' — let the quote case handle them.
                    i++;
                    continue;
                default:
                    i++;
                    continue;
            }
        }

        return null;
    }

    // ------------------------------------------------------------ main pass

    private sealed class Declaration
    {
        public Declaration(FormulaToken token, bool isParameter, string owner)
        {
            Token = token;
            IsParameter = isParameter;
            Owner = owner;
        }

        /// <summary>LET or LAMBDA.</summary>
        public string Owner { get; }

        public FormulaToken Token { get; }
        public string Name => Strip(Token.Text);
        public bool IsParameter { get; }
        public bool Used { get; set; }
    }

    private sealed class Frame
    {
        public Frame(FormulaToken? nameToken, FormulaToken open)
        {
            NameToken = nameToken;
            Open = open;
            Function = nameToken is null ? null : Strip(nameToken.Text).ToUpperInvariant();
        }

        public FormulaToken? NameToken { get; }
        public FormulaToken Open { get; }

        /// <summary>The called function, upper case and unprefixed; null for a bare bracket.</summary>
        public string? Function { get; }

        public int ArgIndex { get; set; }
        public int ArgTokens { get; set; }
        public FormulaToken? ArgFirst { get; set; }
        public FormulaToken? ArgSoleName { get; set; }
        public List<Declaration> Declarations { get; } = new();

        public bool IsLet => Function == "LET";
        public bool IsLambda => Function == "LAMBDA";

        /// <summary>Where the call starts, for messages: the name if there is one.</summary>
        public int Start => NameToken?.Start ?? Open.Start;

        public string Label => NameToken is null ? "(" : NameToken.Text + "(";
    }

    private sealed class Pass
    {
        private readonly IReadOnlyList<FormulaToken> _t;
        private readonly FormulaNameContext _names;
        private readonly List<FormulaProblem> _problems;
        private readonly List<Frame> _stack = new();
        private readonly List<Declaration> _all = new();

        // The last call closed at the top level, and the ')' that closed it.
        private Frame? _lastClosed;
        private FormulaToken? _lastCloser;

        public Pass(IReadOnlyList<FormulaToken> tokens, FormulaNameContext names, List<FormulaProblem> problems)
        {
            _t = tokens;
            _names = names;
            _problems = problems;
        }

        private Frame? Top => _stack.Count == 0 ? null : _stack[^1];

        public void Run()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                var tok = _t[i];
                switch (tok.Type)
                {
                    case FormulaTokenType.Eof:
                        break;
                    case FormulaTokenType.LParen:
                        OnOpen(i);
                        break;
                    case FormulaTokenType.RParen:
                        OnClose(tok);
                        break;
                    case FormulaTokenType.Comma:
                        OnComma(tok);
                        break;
                    case FormulaTokenType.Name:
                        OnName(i);
                        break;
                    default:
                        CountArgToken(tok, isSoleNameCandidate: false);
                        break;
                }
            }

            // A LET name never used is often the other half of a typo (ouyt defined, out used).
            foreach (var d in _all.Where(d => !d.Used && !d.IsParameter))
                Add(new FormulaProblem(FormulaProblemKind.UnusedName, d.Token.Start, d.Token.Text.Length,
                    $"{d.Token.Text} is defined but never used."));

            // Brackets never closed, outermost first.
            foreach (var f in _stack)
            {
                Add(new FormulaProblem(FormulaProblemKind.UnclosedBracket, f.Start, f.Open.End - f.Start,
                    $"{f.Label} is never closed: a ) is missing, at the end or somewhere inside it."));
            }
        }

        private void Add(FormulaProblem p) => _problems.Add(p);

        private void CountArgToken(FormulaToken tok, bool isSoleNameCandidate)
        {
            var f = Top;
            if (f is null)
                return;
            if (f.ArgTokens == 0)
                f.ArgFirst = tok;
            f.ArgTokens++;
            f.ArgSoleName = f.ArgTokens == 1 && isSoleNameCandidate ? tok : null;
        }

        private void OnOpen(int i)
        {
            var open = _t[i];
            var prev = i > 0 ? _t[i - 1] : null;
            var nameToken = prev is { Type: FormulaTokenType.Name } && open.Lead.Length == 0 ? prev : null;

            // The call (name + bracket) is one thing inside the enclosing argument.
            if (nameToken is null)
                CountArgToken(open, isSoleNameCandidate: false);
            else if (Top is { } outer)
                outer.ArgSoleName = null;

            _stack.Add(new Frame(nameToken, open));
        }

        private void OnComma(FormulaToken comma)
        {
            var f = Top;
            if (f is null)
            {
                if (_lastClosed is not null && _lastCloser is not null)
                    Add(new FormulaProblem(FormulaProblemKind.CommaOutsideFunction, comma.Start, 1,
                        $"This comma is outside every function: {_lastClosed.Label} was already closed by the ) " +
                        $"at character {_lastCloser.Start + 1}. That ) is probably misplaced.",
                        RelatedStart: _lastCloser.Start, RelatedLength: 1));
                else
                    Add(new FormulaProblem(FormulaProblemKind.CommaOutsideFunction, comma.Start, 1,
                        "This comma is outside every function."));
                return;
            }

            FinishArgument(f, isLast: false);
            f.ArgIndex++;
            f.ArgTokens = 0;
            f.ArgFirst = null;
            f.ArgSoleName = null;
        }

        private void OnClose(FormulaToken close)
        {
            var f = Top;
            if (f is null)
            {
                if (_lastClosed is not null && _lastCloser is not null)
                    Add(new FormulaProblem(FormulaProblemKind.ExtraCloseBracket, close.Start, 1,
                        $"Extra ): nothing is left to close. {_lastClosed.Label} at character " +
                        $"{_lastClosed.Start + 1} was closed by the ) at character {_lastCloser.Start + 1}.",
                        RelatedStart: _lastCloser.Start, RelatedLength: 1));
                else
                    Add(new FormulaProblem(FormulaProblemKind.ExtraCloseBracket, close.Start, 1,
                        "Extra ): there is no ( for it to close."));
                return;
            }

            var empty = f.ArgIndex == 0 && f.ArgTokens == 0;
            if (!empty)
                FinishArgument(f, isLast: true);
            FinishFrameShape(f, close);

            _stack.RemoveAt(_stack.Count - 1);
            if (_stack.Count == 0)
            {
                _lastClosed = f;
                _lastCloser = close;
            }

            // A call is one operand of the enclosing argument; a later name can't be "the sole name".
            if (Top is { } outer && f.NameToken is not null)
                outer.ArgSoleName = null;
        }

        /// <summary>Records LET names and LAMBDA parameters as their commas arrive.</summary>
        private void FinishArgument(Frame f, bool isLast)
        {
            if (isLast)
                return;

            if (f.IsLet && f.ArgIndex % 2 == 0)
            {
                if (f.ArgSoleName is { } n)
                    Declare(f, n, isParameter: false);
                else
                    Add(new FormulaProblem(FormulaProblemKind.BadShape,
                        f.ArgFirst?.Start ?? f.Open.End, Math.Max(1, (f.ArgFirst?.Text.Length) ?? 1),
                        $"LET expects a name here (argument {f.ArgIndex + 1}), not a calculation. " +
                        "Names and values must alternate; a misplaced ) or comma shifts them."));
            }
            else if (f.IsLambda)
            {
                if (f.ArgSoleName is { } n)
                    Declare(f, n, isParameter: true);
                else
                    Add(new FormulaProblem(FormulaProblemKind.BadShape,
                        f.ArgFirst?.Start ?? f.Open.End, Math.Max(1, (f.ArgFirst?.Text.Length) ?? 1),
                        "LAMBDA expects a parameter name here: only its last argument is a calculation."));
            }
        }

        private void Declare(Frame f, FormulaToken n, bool isParameter)
        {
            var bare = Strip(n.Text);
            if (IsCellReference(bare) || bare.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                                      || bare.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
                                      || bare.IndexOfAny(RefChars) >= 0)
                Add(new FormulaProblem(FormulaProblemKind.BadShape, n.Start, n.Text.Length,
                    $"{n.Text} can't be a {(isParameter ? "LAMBDA parameter" : "LET name")}: " +
                    "Excel reads it as a cell reference or value."));
            var decl = new Declaration(n, isParameter, isParameter ? "LAMBDA" : "LET");
            f.Declarations.Add(decl);
            _all.Add(decl);
        }

        private void FinishFrameShape(Frame f, FormulaToken? closer)
        {
            var argCount = f.ArgIndex + (f.ArgIndex == 0 && f.ArgTokens == 0 ? 0 : 1);
            var at = closer is null ? "" : $" The ) at character {closer.Start + 1} closes it.";

            if (f.IsLet)
            {
                if (argCount < 3 || argCount % 2 == 0)
                    Add(new FormulaProblem(FormulaProblemKind.BadShape, f.Start, f.Open.End - f.Start,
                        $"LET has {argCount} argument{(argCount == 1 ? "" : "s")}: it needs name, value pairs " +
                        $"and then a final calculation (an odd number, at least 3).{at}",
                        RelatedStart: closer?.Start ?? -1, RelatedLength: closer is null ? 0 : 1));

            }
            else if (f.IsLambda && argCount == 0)
            {
                Add(new FormulaProblem(FormulaProblemKind.BadShape, f.Start, f.Open.End - f.Start,
                    $"LAMBDA needs at least a calculation.{at}"));
            }
        }

        private void OnName(int i)
        {
            var tok = _t[i];
            var next = i + 1 < _t.Count ? _t[i + 1] : null;
            var prev = i > 0 ? _t[i - 1] : null;
            var isCall = next is { Type: FormulaTokenType.LParen } && next.Lead.Length == 0;
            var f = Top;

            // A LET name or LAMBDA parameter being declared: checked when its comma arrives.
            var isDeclaration = !isCall && f is not null && f.ArgTokens == 0
                                && next is { Type: FormulaTokenType.Comma }
                                && ((f.IsLet && f.ArgIndex % 2 == 0) || f.IsLambda);

            CountArgToken(tok, isSoleNameCandidate: !isCall);
            if (isDeclaration)
                return;

            var nextIsColon = next is { Type: FormulaTokenType.Operator, Text: ":" } && next.Lead.Length == 0;
            var prevIsColon = prev is { Type: FormulaTokenType.Operator, Text: ":" } && tok.Lead.Length == 0;
            Resolve(tok, isCall, nextIsColon || prevIsColon);
        }

        // ------------------------------------------------------------ resolution

        private Declaration? FindInScope(string name)
        {
            for (var s = _stack.Count - 1; s >= 0; s--)
            {
                var decls = _stack[s].Declarations;
                for (var d = decls.Count - 1; d >= 0; d--)
                    if (decls[d].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return decls[d];
            }

            return null;
        }

        private IEnumerable<string> ScopeNames() =>
            _stack.SelectMany(f => f.Declarations).Select(d => d.Name);

        private void Resolve(FormulaToken tok, bool isCall, bool besideColon)
        {
            var raw = tok.Text;

            if (raw.IndexOf('[') >= 0)
            {
                ResolveStructured(tok);
                return;
            }

            var bang = raw.LastIndexOf('!');
            if (bang >= 0)
            {
                ResolveSheetQualified(tok, bang, isCall);
                return;
            }

            var name = Strip(raw);
            if (name.Length == 0)
                return;

            if (isCall)
            {
                if (FindInScope(name) is { } d)
                {
                    d.Used = true;
                    return;
                }

                if (ExcelFunctions.IsNative(name) || IsWorkbookName(name))
                    return;

                ReportUnknown(tok, name, isCall: true);
                return;
            }

            if (name.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                return;

            if (FindInScope(name) is { } decl)
            {
                decl.Used = true;
                return;
            }

            if (IsCellReference(name) || (besideColon && ColumnOnly.IsMatch(name)))
                return;

            // A native function passed by name (BYROW(x, SUM)) is valid.
            if (IsWorkbookName(name) || _names.Tables.ContainsKey(name) || ExcelFunctions.IsNative(name))
                return;

            ReportUnknown(tok, name, isCall: false);
        }

        private bool IsWorkbookName(string name) =>
            _names.DefinedNames.Contains(name) || _names.OtherKnownNames.Contains(name);

        private void ReportUnknown(FormulaToken tok, string name, bool isCall)
        {
            // Defined in a LET or LAMBDA that has already closed: say so, it's usually a misplaced ).
            var stranded = _all.LastOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (stranded is not null)
            {
                stranded.Used = true;
                Add(new FormulaProblem(FormulaProblemKind.UnknownName, tok.Start, tok.Text.Length,
                    $"{tok.Text} is out of scope here: it's defined at character {stranded.Token.Start + 1}, " +
                    $"inside a {stranded.Owner} that has already closed.",
                    RelatedStart: stranded.Token.Start, RelatedLength: stranded.Token.Text.Length));
                return;
            }

            if (_names.LibraryNames.Contains(name))
            {
                Add(new FormulaProblem(FormulaProblemKind.NotLoaded, tok.Start, tok.Text.Length,
                    $"{tok.Text} is a library LAMBDA that isn't loaded in this workbook. Load it from the Lambda Boss popup."));
                return;
            }

            var candidates = ScopeNames()
                .Concat(_names.DefinedNames.Where(n => n.IndexOf('!') < 0))
                .Concat(_names.LibraryNames)
                .Concat(_names.OtherKnownNames);
            candidates = isCall
                ? candidates.Concat(ExcelFunctions.All)
                : candidates.Concat(_names.Tables.Keys);

            var nearest = Nearest(name, candidates);
            string message;
            if (nearest is not null)
            {
                message = $"{tok.Text} isn't defined anywhere. Did you mean {nearest}?";
            }
            else if (RunTogether(name, isCall) is { } split)
            {
                nearest = split.Suggestion;
                message = $"{tok.Text} isn't defined anywhere. It contains {split.Found}: two names run together?";
            }
            else
            {
                message = isCall
                    ? $"{tok.Text} isn't a function, a LAMBDA in this workbook, or a LET name."
                    : $"{tok.Text} isn't a LET or LAMBDA name in scope, a defined name, a table or a cell.";
            }

            Add(new FormulaProblem(FormulaProblemKind.UnknownName, tok.Start, tok.Text.Length, message, nearest));
        }

        /// <summary>
        ///     For <c>TOCOPOSTOCELL</c>: the longest known name (4+ letters) inside the unknown one,
        ///     and the nearest name to what's left before it (<c>TOCO</c> → TOCOL).
        /// </summary>
        private (string Found, string Suggestion)? RunTogether(string name, bool isCall)
        {
            var pool = ScopeNames().Concat(_names.DefinedNames).Concat(_names.LibraryNames)
                .Concat(isCall ? ExcelFunctions.All : Enumerable.Empty<string>())
                .Where(n => n.Length >= 4 && n.Length < name.Length && n.IndexOf('!') < 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(n => n.Length)
                .FirstOrDefault();
            if (pool is null)
                return null;

            var at = name.IndexOf(pool, StringComparison.OrdinalIgnoreCase);
            var before = name[..at];
            var after = name[(at + pool.Length)..];
            var fix = before.Length >= 2
                ? Nearest(before, ExcelFunctions.All.Concat(_names.LibraryNames).Concat(_names.DefinedNames))
                : null;
            var suggestion = fix is not null && after.Length == 0 ? $"{fix}({pool}" : pool;
            return (pool, suggestion);
        }

        private void ResolveSheetQualified(FormulaToken tok, int bang, bool isCall)
        {
            var raw = tok.Text;
            var sheetPart = raw[..bang];
            var rest = raw[(bang + 1)..];

            // A workbook-qualified or 3-D reference: out of scope.
            if (sheetPart.IndexOf('[') >= 0 || sheetPart.IndexOf(':') >= 0)
                return;

            var sheet = sheetPart.Length >= 2 && sheetPart[0] == '\'' && sheetPart[^1] == '\''
                ? sheetPart[1..^1].Replace("''", "'")
                : sheetPart;

            if (_names.Sheets.Count > 0 && !_names.Sheets.Contains(sheet))
            {
                var near = Nearest(sheet, _names.Sheets);
                Add(new FormulaProblem(FormulaProblemKind.UnknownReference, tok.Start, bang,
                    $"There's no sheet called {sheetPart}." + (near is null ? "" : $" Did you mean {near}?"),
                    near));
                return;
            }

            if (rest.Length == 0 || IsCellReference(rest) || ColumnOnly.IsMatch(rest)
                || rest.All(char.IsDigit) || isCall)
                return;

            if (IsWorkbookName(rest) || _names.DefinedNames.Contains($"{sheetPart}!{rest}")
                                     || _names.DefinedNames.Contains($"{sheet}!{rest}"))
                return;

            var nearest = Nearest(rest, _names.DefinedNames.Select(n => n[(n.LastIndexOf('!') + 1)..]));
            Add(new FormulaProblem(FormulaProblemKind.UnknownName, tok.Start + bang + 1, rest.Length,
                $"{rest} isn't a cell or a defined name on {sheetPart}." +
                (nearest is null ? "" : $" Did you mean {nearest}?"), nearest));
        }

        private void ResolveStructured(FormulaToken tok)
        {
            var raw = tok.Text;
            var open = raw.IndexOf('[');
            var tableText = raw[..open];
            var bang = tableText.LastIndexOf('!');
            if (raw.StartsWith("[", StringComparison.Ordinal) || bang >= 0 && tableText.IndexOf('[') >= 0)
                return; // [@Col] inside a table, or an external workbook: out of scope.
            if (bang >= 0)
                tableText = tableText[(bang + 1)..];
            if (tableText.Length == 0 || _names.Tables.Count == 0)
                return;

            if (!_names.Tables.TryGetValue(tableText, out var columns))
            {
                if (IsWorkbookName(tableText))
                    return;
                var near = Nearest(tableText, _names.Tables.Keys);
                Add(new FormulaProblem(FormulaProblemKind.UnknownReference, tok.Start + (bang + 1), tableText.Length,
                    $"There's no table called {tableText}." + (near is null ? "" : $" Did you mean {near}?"), near));
                return;
            }

            foreach (var (col, offset) in ColumnSpecifiers(raw, open))
            {
                if (TableSpecials.Contains(col) || columns.Contains(col))
                    continue;
                var near = Nearest(col, columns);
                Add(new FormulaProblem(FormulaProblemKind.UnknownReference, tok.Start + offset, col.Length,
                    $"Table {tableText} has no column called {col}." + (near is null ? "" : $" Did you mean {near}?"),
                    near));
            }
        }
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    ///     The innermost bracketed items of a structured reference, unescaped, with their offset in
    ///     the token: <c>t[[#Data],[Fare]]</c> gives <c>#Data</c> and <c>Fare</c>; <c>t[@Fare]</c>
    ///     gives <c>Fare</c>.
    /// </summary>
    internal static IEnumerable<(string Column, int Offset)> ColumnSpecifiers(string raw, int open)
    {
        var results = new List<(string, int)>();
        var i = open;
        while (i < raw.Length)
        {
            if (raw[i] != '[')
            {
                i++;
                continue;
            }

            // Find the matching ']' for this '[' if no '[' comes first (an innermost item).
            var j = i + 1;
            var sb = new System.Text.StringBuilder();
            var innermost = true;
            while (j < raw.Length && raw[j] != ']')
            {
                if (raw[j] == '[')
                {
                    innermost = false;
                    break;
                }

                if (raw[j] == '\'' && j + 1 < raw.Length)
                {
                    sb.Append(raw[j + 1]);
                    j += 2;
                    continue;
                }

                sb.Append(raw[j]);
                j++;
            }

            if (!innermost)
            {
                i = j;
                continue;
            }

            var item = sb.ToString();
            var offset = i + 1;
            if (item.StartsWith("@", StringComparison.Ordinal) && item.Length > 1)
            {
                item = item[1..];
                offset++;
            }

            item = item.Trim();
            if (item.Length > 0)
                results.Add((item, offset));
            i = j + 1;
        }

        return results;
    }

    internal static string Strip(string name)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var p in HiddenPrefixes)
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[p.Length..];
                    changed = true;
                }
        }

        return name;
    }

    /// <summary>A1-style cell address within Excel's grid (XFD1048576), with or without <c>$</c>.</summary>
    internal static bool IsCellReference(string s)
    {
        var m = CellRef.Match(s);
        if (!m.Success)
            return false;

        var col = 0;
        foreach (var ch in m.Groups[1].Value.ToUpperInvariant())
            col = col * 26 + (ch - 'A' + 1);
        if (col > 16384)
            return false;

        return long.TryParse(m.Groups[2].Value, out var row) && row >= 1 && row <= 1048576;
    }

    /// <summary>
    ///     The candidate closest to <paramref name="name"/> by edit distance (a swap counts as one
    ///     edit), case-insensitive, if it is close enough to be a typo: one edit for names up to 4
    ///     characters, two up to 8, three beyond.
    /// </summary>
    internal static string? Nearest(string name, IEnumerable<string> candidates)
    {
        var limit = name.Length <= 4 ? 1 : name.Length <= 8 ? 2 : 3;
        var upper = name.ToUpperInvariant();
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var c in candidates)
        {
            if (Math.Abs(c.Length - name.Length) > limit)
                continue;
            if (c.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var d = Distance(upper, c.ToUpperInvariant(), Math.Min(limit, bestDistance));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }

        return bestDistance <= limit ? best : null;
    }

    /// <summary>Optimal-string-alignment distance; returns more than <paramref name="cap"/> early when it can.</summary>
    internal static int Distance(string a, string b, int cap = int.MaxValue)
    {
        var n = a.Length;
        var m = b.Length;
        var prev2 = new int[m + 1];
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (var j = 0; j <= m; j++)
            prev[j] = j;

        for (var i = 1; i <= n; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, prev2[j - 2] + 1);
                cur[j] = v;
                if (v < rowMin)
                    rowMin = v;
            }

            if (rowMin > cap)
                return cap + 1;

            (prev2, prev, cur) = (prev, cur, prev2);
        }

        return prev[m];
    }

}
