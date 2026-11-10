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

    /// <summary>A function called with too few or too many arguments (issue #424).</summary>
    ArgumentCount,

    /// <summary>Not an error: a LET name that is never used (often the other half of a typo).</summary>
    UnusedName,

    /// <summary>Not a finding: the line that says how many more were left out.</summary>
    More
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
    public bool IsNote => Kind is FormulaProblemKind.UnusedName or FormulaProblemKind.More;

    /// <summary>The bracket repair (issue #426) clears it: it is a consequence of the bracket, not a problem of its own.</summary>
    public bool IsConsequence { get; init; }

    /// <summary>
    ///     Text that could replace this problem's span (a mistyped name's nearest valid names, as
    ///     they'd be written), nearest first; at most 3. Empty when there's no safe one-span fix.
    /// </summary>
    public IReadOnlyList<string> Replacements { get; init; } = Array.Empty<string>();

    /// <summary>A bracket, comma, shape or argument-count fault: the kind one misplaced <c>)</c> can cause.</summary>
    public bool IsStructural => Kind is FormulaProblemKind.ExtraCloseBracket or FormulaProblemKind.UnclosedBracket
                                    or FormulaProblemKind.CommaOutsideFunction or FormulaProblemKind.BadShape
                                    or FormulaProblemKind.ArgumentCount
                                || (Kind == FormulaProblemKind.UnknownName && RelatedStart >= 0);
}

/// <summary>
///     One single-bracket edit the repair search tried (issue #426): remove the <c>)</c> at
///     <see cref="Position"/>, or insert one before it. <see cref="Fixed"/> is the whole formula
///     after the edit; <see cref="CallStart"/>/<see cref="CallLength"/> index it and mark the call
///     the edit changes (empty when none), so the window can show what the edit does.
///     <see cref="Remaining"/> counts the bracket, shape and argument faults left (0: fully valid).
/// </summary>
internal sealed record BracketRepair(
    bool Insert,
    int Position,
    string Fixed,
    int Remaining,
    int CallStart,
    int CallLength,
    string? CallName)
{
    public string Action => Insert ? "Add a ) here" : "Remove this )";

    /// <summary>
    ///     The report's first line: "Remove the ) after start, TAKE(moves, 5) (character 101)."
    ///     Quotes up to about 24 characters before the spot, from its own line.
    /// </summary>
    public string Describe(string formula)
    {
        var lineStart = formula.LastIndexOfAny(new[] { '\n', '\r' }, Math.Max(0, Position - 1)) + 1;
        if (Position == 0)
            lineStart = 0;
        var from = Math.Max(lineStart, Position - 24);
        var before = formula[from..Position];
        var cut = from > lineStart;
        if (cut)
        {
            // Start at a word: after the first space, comma or bracket.
            var k = before.IndexOfAny(new[] { ' ', ',', '(' });
            if (k >= 0 && k < before.Length - 1)
                before = before[(k + 1)..];
        }

        before = before.Trim();
        var where = before.Length == 0 ? "" : $" after {(cut ? "…" : "")}{before}";
        return Insert
            ? $"Add a ){where} (character {Position + 1})."
            : $"Remove the ){where} (character {Position + 1}).";
    }
}

/// <summary>What the repair search concluded.</summary>
internal enum RepairOutcome
{
    /// <summary>No bracket, comma or argument-count fault: nothing to search for.</summary>
    NotNeeded,

    /// <summary>Exactly one single edit makes the formula valid.</summary>
    One,

    /// <summary>Several single edits each make it valid; ranked.</summary>
    Several,

    /// <summary>None makes it valid; the edits that leave fewest faults, ranked.</summary>
    Closest,

    /// <summary>No single edit helps.</summary>
    NoneFound,

    /// <summary>The formula is too long to search, or the search ran out of time.</summary>
    Skipped
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

    /// <summary>Library LAMBDA name (bare and prefixed) to its argument range, from the header metadata.</summary>
    public Dictionary<string, (int Min, int Max)> LibraryArgumentCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the snapshot was taken (UTC), or null for a context built by hand.</summary>
    public DateTime? TakenUtc { get; set; }

    /// <summary>
    ///     False until the workbook's names have been read at least once: unknown names are then
    ///     reported with a warning that they may be defined names.
    /// </summary>
    public bool NamesRead { get; set; } = true;

    /// <summary>A copy for another thread to read while this one changes.</summary>
    public FormulaNameContext Clone()
    {
        var c = new FormulaNameContext { TakenUtc = TakenUtc, NamesRead = NamesRead };
        c.DefinedNames.UnionWith(DefinedNames);
        c.Sheets.UnionWith(Sheets);
        c.LibraryNames.UnionWith(LibraryNames);
        c.OtherKnownNames.UnionWith(OtherKnownNames);
        foreach (var (k, v) in LibraryArgumentCounts)
            c.LibraryArgumentCounts[k] = v;
        foreach (var (k, v) in Tables)
            c.Tables[k] = new HashSet<string>(v, StringComparer.OrdinalIgnoreCase);
        return c;
    }
}

internal sealed class FormulaCheckResult
{
    public FormulaCheckResult(string formula, IReadOnlyList<FormulaProblem> problems,
        RepairOutcome repair = RepairOutcome.NotNeeded, IReadOnlyList<BracketRepair>? repairs = null,
        string? repairNote = null)
    {
        Formula = formula;
        Problems = problems;
        Repair = repair;
        Repairs = repairs ?? Array.Empty<BracketRepair>();
        RepairNote = repairNote;
    }

    public string Formula { get; }

    public IReadOnlyList<FormulaProblem> Problems { get; }

    public RepairOutcome Repair { get; }

    /// <summary>The edits to show, best first: the one fix, the several, or the closest.</summary>
    public IReadOnlyList<BracketRepair> Repairs { get; }

    /// <summary>Why the search was skipped or cut short, for the window; null otherwise.</summary>
    public string? RepairNote { get; }

    /// <summary>Exactly one edit makes the formula valid, and the search tried every edit.</summary>
    public bool HasSingleFix => Repair == RepairOutcome.One && RepairNote is null;

    /// <summary>The same result with a different problem list (Excel confirmed some names).</summary>
    public FormulaCheckResult WithProblems(IReadOnlyList<FormulaProblem> problems) =>
        new(Formula, problems, Repair, Repairs, RepairNote);

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

    private static readonly string[] HiddenPrefixes = { "_xlfn.", "_xlws.", "_xlpm.", "_xludf.", "_xleta." };

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
        catch (FormatException ex)
        {
            problems.Add(new FormulaProblem(FormulaProblemKind.Unterminated, 0, 0,
                $"Lambda Boss couldn't read this formula: {ex.Message}"));
            return new FormulaCheckResult(formula, problems);
        }

        var pass = new Pass(tokens, names, problems, reportUnclosed: unterminated is null, quick: false);
        pass.Run();

        // Brackets first (they are usually the cause), then shape, names, and notes last.
        problems = problems.OrderBy(p => Priority(p.Kind)).ThenBy(p => p.Start).ToList();

        var repair = RepairOutcome.NotNeeded;
        IReadOnlyList<BracketRepair> repairs = Array.Empty<BracketRepair>();
        string? note = null;
        if (unterminated is null && problems.Any(p => p.Kind is FormulaProblemKind.ExtraCloseBracket
                or FormulaProblemKind.UnclosedBracket or FormulaProblemKind.CommaOutsideFunction
                or FormulaProblemKind.ArgumentCount))
        {
            (repair, repairs, note) = SearchRepairs(formula, tokens, names, problems, pass);
            if (repair == RepairOutcome.One && note is null)
                problems = problems.Select(p => p.IsStructural ? p with { IsConsequence = true } : p).ToList();
        }

        if (problems.Count > MaxProblems)
        {
            var more = problems.Count - MaxProblems + 1;
            problems = problems.Take(MaxProblems - 1).ToList();
            problems.Add(new FormulaProblem(FormulaProblemKind.More, formula.Length, 0,
                $"…and {more} more."));
        }

        return new FormulaCheckResult(formula, problems, repair, repairs, note);
    }

    // ------------------------------------------------------------ repair search (#426)

    /// <summary>Above this length the repair search isn't tried.</summary>
    public const int MaxRepairLength = 12000;

    /// <summary>The repair search stops after this long and says so.</summary>
    public static TimeSpan RepairBudget { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    ///     Tries every single <c>)</c> removal, and every <c>)</c> insertion where an argument ends
    ///     (before a comma, a <c>)</c> or the end), re-checking the formula after each. Each re-check
    ///     is one linear pass over the existing tokens, without suggestions.
    /// </summary>
    private static (RepairOutcome, IReadOnlyList<BracketRepair>, string?) SearchRepairs(
        string formula, IReadOnlyList<FormulaToken> tokens, FormulaNameContext names,
        IReadOnlyList<FormulaProblem> problems, Pass original)
    {
        var originalClosers = original.Closers;
        if (formula.Length > MaxRepairLength)
            return (RepairOutcome.Skipped, Array.Empty<BracketRepair>(),
                $"Bracket repair search skipped: the formula is over {MaxRepairLength:N0} characters.");

        // One ) edit moves the balance by one, so it can only help when the formula is off by
        // exactly one: an extra ) (try removals) or a missing one (try insertions).
        var balance = tokens.Count(t => t.Type == FormulaTokenType.RParen)
                      - tokens.Count(t => t.Type == FormulaTokenType.LParen);
        if (balance == 0)
            return (RepairOutcome.NotNeeded, Array.Empty<BracketRepair>(), null);
        if (Math.Abs(balance) > 1)
            return (RepairOutcome.NoneFound, Array.Empty<BracketRepair>(), null);
        var removals = balance > 0;

        var baseline = problems.Count(p => p.IsStructural);
        var first = problems.First(p => p.IsStructural);

        // Rank near the first fault: for a misplaced ) the bracket that closed the call too soon;
        // for an extra one, the extra ) itself.
        var anchor = first.Kind == FormulaProblemKind.CommaOutsideFunction && first.RelatedStart >= 0
            ? first.RelatedStart
            : first.Start;

        // The text before the first fault reads the same after any later edit, so only edits up
        // to it can make the formula valid. Tried nearest that point first (and insertions at a
        // line's end first), so a search cut short by the clock has tried the likeliest.
        var last = Math.Min(tokens.Count - 1, original.FirstStructuralToken);
        var detectedAt = tokens[last].Start;
        var candidates = new List<(bool Insert, int Index, int Pos)>();
        for (var i = 0; i <= last; i++)
        {
            var t = tokens[i];
            if (removals)
            {
                if (t.Type == FormulaTokenType.RParen)
                    candidates.Add((false, i, t.Start));
                continue;
            }

            if (t.Type is not (FormulaTokenType.Comma or FormulaTokenType.RParen or FormulaTokenType.Eof))
                continue;

            // Not "()" from a bare bracket: Excel refuses an empty one.
            if (i > 0 && tokens[i - 1].Type == FormulaTokenType.LParen
                      && !(i > 1 && tokens[i - 2].Type == FormulaTokenType.Name && tokens[i - 1].Lead.Length == 0))
                continue;
            candidates.Add((true, i, i > 0 ? tokens[i - 1].End : t.Start - t.Lead.Length));
        }
        candidates = candidates
            .OrderBy(c => c.Insert && AtLineEnd(c.Pos) ? 0 : 1)
            .ThenBy(c => Math.Abs(detectedAt - c.Pos))
            .ToList();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var byResult = new Dictionary<string, BracketRepair>(StringComparer.Ordinal);
        var tried = 0;
        var best = baseline - 1;
        foreach (var (insert, index, pos) in candidates)
        {
            if (clock.Elapsed > RepairBudget)
                break;
            tried++;

            var fixedText = insert ? formula.Insert(pos, ")") : formula.Remove(pos, 1);
            var edited = new List<FormulaToken>(tokens.Count + 1);
            FormulaToken? inserted = null;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (insert && i == index)
                {
                    inserted = new FormulaToken(FormulaTokenType.RParen, "", ")", pos);
                    edited.Add(inserted);
                }

                if (!insert && i == index)
                    continue;
                edited.Add(tokens[i]);
            }

            var found = new List<FormulaProblem>();
            // Stop reading once it can't beat (or tie) the best edit so far.
            var pass = new Pass(edited, names, found, reportUnclosed: true, quick: true) { AbortAt = best + 1 };
            pass.Run();
            var remaining = pass.StructuralCount;
            if (remaining > best)
                continue;
            best = remaining;

            // The call the edit changes: the one the inserted ) now closes, or the one the
            // removed ) used to close (it now runs further).
            int Shift(int at) => insert ? (at >= pos ? at + 1 : at) : (at > pos ? at - 1 : at);
            ClosedCall? call = null;
            if (insert)
                call = pass.Closers.Values.FirstOrDefault(c => ReferenceEquals(c.Closer, inserted));
            else
            {
                var removed = tokens[index];
                var was = originalClosers.Values.FirstOrDefault(c => ReferenceEquals(c.Closer, removed));
                if (was is not null && pass.Closers.TryGetValue(was.Open, out var now))
                    call = now;
            }

            var repair = call is null
                ? new BracketRepair(insert, pos, fixedText, remaining, -1, 0, null)
                : new BracketRepair(insert, pos, fixedText, remaining, Shift(call.Start),
                    (ReferenceEquals(call.Closer, inserted) ? pos + 1 : Shift(call.Closer.End)) - Shift(call.Start),
                    call.Name);
            // Identical results keep the later edit: in "))" the second bracket is the stray one.
            if (!byResult.TryGetValue(fixedText, out var same) || same.Position < pos)
                byResult[fixedText] = repair;
        }

        var cut = tried < candidates.Count
            ? $"Bracket repair search stopped after {tried} of {candidates.Count} edits (time limit)."
            : null;

        // Best first: fewest faults left; then an insertion at the end of a line or of the formula
        // (one binding per line); then nearest the first fault.
        bool AtLineEnd(int j)
        {
            while (j < formula.Length && formula[j] is ' ' or '\t')
                j++;
            return j >= formula.Length || formula[j] is '\n' or '\r';
        }

        var ranked = byResult.Values
            .OrderBy(r => r.Remaining)
            .ThenBy(r => r.Insert && AtLineEnd(r.Position) ? 0 : 1)
            .ThenBy(r => Math.Abs(r.Position - anchor))
            .ToList();

        if (ranked.Count == 0)
            return (cut is null ? RepairOutcome.NoneFound : RepairOutcome.Skipped, ranked, cut);
        var valid = ranked.Where(r => r.Remaining == 0).ToList();
        if (valid.Count == 1)
            return (RepairOutcome.One, valid, cut);
        if (valid.Count > 1)
            return (RepairOutcome.Several, valid.Take(5).ToList(), cut);
        var fewest = ranked[0].Remaining;
        return (RepairOutcome.Closest, ranked.Where(r => r.Remaining == fewest).Take(3).ToList(), cut);
    }

    /// <summary>A call and the <c>)</c> that closed it, in a pass's own token coordinates.</summary>
    private sealed record ClosedCall(FormulaToken Open, int Start, string Name, FormulaToken Closer);

    private static int Priority(FormulaProblemKind kind) => kind switch
    {
        FormulaProblemKind.Unterminated => 0,
        FormulaProblemKind.ExtraCloseBracket => 1,
        FormulaProblemKind.CommaOutsideFunction => 1,
        FormulaProblemKind.UnclosedBracket => 1,
        FormulaProblemKind.BadShape => 2,
        FormulaProblemKind.ArgumentCount => 2,
        FormulaProblemKind.UnusedName => 9,
        FormulaProblemKind.More => 10,
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
                    try
                    {
                        i = FormulaParser.SkipArray(s, i);
                    }
                    catch (FormatException)
                    {
                        return (new FormulaProblem(FormulaProblemKind.Unterminated, start, 1,
                            "This { starts an array constant that is never closed: a } is missing."), start);
                    }

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
        public Declaration(FormulaToken token, string name, bool isParameter, string owner, Frame frame, int visibleFrom)
        {
            Token = token;
            Name = name;
            IsParameter = isParameter;
            Owner = owner;
            Frame = frame;
            VisibleFromArg = visibleFrom;
        }

        public Frame Frame { get; }

        /// <summary>The first argument of its LET or LAMBDA that can see it.</summary>
        public int VisibleFromArg { get; }

        /// <summary>LET or LAMBDA.</summary>
        public string Owner { get; }

        public FormulaToken Token { get; }
        public string Name { get; }
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

        /// <summary>The argument range of the called function, when it's known (#424).</summary>
        public (int Min, int Max)? Arity { get; set; }

        /// <summary>The range came from a library LAMBDA's header, not Excel's own table.</summary>
        public bool ArityFromLibrary { get; set; }

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

        private readonly bool _reportUnclosed;

        // The repair search's re-checks: no name suggestions (they're the slow part).
        private readonly bool _quick;

        public Pass(IReadOnlyList<FormulaToken> tokens, FormulaNameContext names, List<FormulaProblem> problems,
            bool reportUnclosed, bool quick)
        {
            _t = tokens;
            _names = names;
            _problems = problems;
            _reportUnclosed = reportUnclosed;
            _quick = quick;
        }

        /// <summary>Each closed call's opening bracket, with its closer.</summary>
        public Dictionary<FormulaToken, ClosedCall> Closers { get; } = new();

        private Frame? Top => _stack.Count == 0 ? null : _stack[^1];

        /// <summary>
        ///     The index of the token being read when the first bracket, shape or argument fault was
        ///     found (the token count if at the end), or -1. An edit after it can't clear that fault.
        /// </summary>
        public int FirstStructuralToken { get; private set; } = -1;

        /// <summary>A quick pass stops once it has found this many such faults.</summary>
        public int AbortAt { get; set; } = int.MaxValue;

        public int StructuralCount { get; private set; }

        private int _i;

        public void Run()
        {
            for (var i = 0; i < _t.Count && StructuralCount < AbortAt; i++)
            {
                _i = i;
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

            _i = _t.Count;
            if (StructuralCount >= AbortAt)
                return;

            // A LET name never used is often the other half of a typo (ouyt defined, out used).
            foreach (var d in _all.Where(d => !d.Used && !d.IsParameter))
                Add(new FormulaProblem(FormulaProblemKind.UnusedName, d.Token.Start, d.Token.Text.Length,
                    $"{d.Token.Text} is defined but never used."));

            // Brackets never closed, outermost first. (After an unterminated string they're noise.)
            foreach (var f in _reportUnclosed ? _stack : new List<Frame>())
            {
                Add(new FormulaProblem(FormulaProblemKind.UnclosedBracket, f.Start, f.Open.End - f.Start,
                    $"{f.Label} is never closed: a ) is missing, at the end or somewhere inside it."));
            }
        }

        private void Add(FormulaProblem p)
        {
            if (p.IsStructural)
            {
                if (StructuralCount == 0)
                    FirstStructuralToken = _i;
                StructuralCount++;
            }

            _problems.Add(p);
        }

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

            var frame = new Frame(nameToken, open);
            if (nameToken is not null)
            {
                frame.Arity = ArityOf(nameToken.Text, out var fromLibrary);
                frame.ArityFromLibrary = fromLibrary;
            }
            _stack.Add(frame);
        }

        /// <summary>A native function's or library LAMBDA's argument range, unless a LET or LAMBDA name shadows it.</summary>
        private (int Min, int Max)? ArityOf(string raw, out bool fromLibrary)
        {
            fromLibrary = false;
            if (raw.IndexOfAny(RefChars) >= 0)
                return null;
            var name = Strip(raw);
            if (FindInScope(name) is not null)
                return null;
            if (ExcelFunctionArity.TryGet(name, out var native))
                return native;
            fromLibrary = _names.LibraryArgumentCounts.TryGetValue(name, out var lib);
            return fromLibrary ? lib : null;
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
            Closers[f.Open] = new ClosedCall(f.Open, f.Start, f.Label, close);

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

            // An optional LAMBDA parameter: LAMBDA([x], …).
            if (isParameter && bare.Length > 2 && bare[0] == '[' && bare[^1] == ']')
                bare = bare[1..^1];

            if (IsCellReference(bare) || bare.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                                      || bare.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
                                      || bare.Equals("R", StringComparison.OrdinalIgnoreCase)
                                      || bare.Equals("C", StringComparison.OrdinalIgnoreCase)
                                      || bare.IndexOfAny(RefChars) >= 0)
                Add(new FormulaProblem(FormulaProblemKind.BadShape, n.Start, n.Text.Length,
                    $"{n.Text} can't be a {(isParameter ? "LAMBDA parameter" : "LET name")}: " +
                    "Excel reads it as a cell reference or value."));

            // A LET name is visible from the argument after its value; a parameter from the next argument.
            var decl = new Declaration(n, bare, isParameter, isParameter ? "LAMBDA" : "LET", f,
                isParameter ? f.ArgIndex + 1 : f.ArgIndex + 2);
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
            else if (f.Arity is { } a && closer is not null && (argCount < a.Min || argCount > a.Max))
            {
                var name = f.NameToken!.Text;
                var has = $"{name} has {argCount} argument{(argCount == 1 ? "" : "s")}";
                var needs = argCount < a.Min
                    ? a.Max == a.Min ? $"it needs {a.Min}" : a.Max >= 255 ? $"it needs at least {a.Min}" : $"it needs {a.Min} to {a.Max}"
                    : a.Max == 0 ? "it takes none" : $"it takes at most {a.Max}";
                Add(new FormulaProblem(FormulaProblemKind.ArgumentCount, f.Start, f.Open.End - f.Start,
                    $"{has}; {needs}{(f.ArityFromLibrary ? " (by its library header)" : "")}.{at}",
                    RelatedStart: closer.Start, RelatedLength: 1));
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
                var frame = _stack[s];
                var decls = frame.Declarations;
                for (var d = decls.Count - 1; d >= 0; d--)
                    if (frame.ArgIndex >= decls[d].VisibleFromArg
                        && decls[d].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
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
                var why = _stack.Contains(stranded.Frame)
                    ? $"it's used in its own definition or before it: it's defined at character {stranded.Token.Start + 1}."
                    : $"it's defined at character {stranded.Token.Start + 1}, inside a {stranded.Owner} that has already closed.";
                Add(new FormulaProblem(FormulaProblemKind.UnknownName, tok.Start, tok.Text.Length,
                    $"{tok.Text} is out of scope here: {why}",
                    RelatedStart: stranded.Token.Start, RelatedLength: stranded.Token.Text.Length));
                return;
            }

            if (_names.LibraryNames.Contains(name))
            {
                Add(new FormulaProblem(FormulaProblemKind.NotLoaded, tok.Start, tok.Text.Length,
                    $"{tok.Text} is a library LAMBDA that isn't loaded in this workbook. Load it from the Lambda Boss popup."));
                return;
            }

            if (_quick)
            {
                Add(new FormulaProblem(FormulaProblemKind.UnknownName, tok.Start, tok.Text.Length, ""));
                return;
            }

            var candidates = ScopeNames()
                .Concat(_names.DefinedNames.Where(n => n.IndexOf('!') < 0))
                .Concat(_names.LibraryNames)
                .Concat(_names.OtherKnownNames);
            candidates = isCall
                ? candidates.Concat(ExcelFunctions.All)
                : candidates.Concat(_names.Tables.Keys);

            var candidateList = candidates.ToList();
            var nearest = Nearest(name, candidateList);
            var replacements = nearest is null ? Array.Empty<string>() : NearestAll(name, candidateList);
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

            if (!_names.NamesRead)
                message += " (The workbook's names haven't been read yet, so it may be a defined name: check a cell once to read them.)";

            Add(new FormulaProblem(FormulaProblemKind.UnknownName, tok.Start, tok.Text.Length, message, nearest)
                { Replacements = replacements });
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

            var pool = _names.DefinedNames.Select(n => n[(n.LastIndexOf('!') + 1)..]).ToList();
            var nearest = Nearest(rest, pool);
            Add(new FormulaProblem(FormulaProblemKind.UnknownName, tok.Start + bang + 1, rest.Length,
                $"{rest} isn't a cell or a defined name on {sheetPart}." +
                (nearest is null ? "" : $" Did you mean {nearest}?"), nearest)
                { Replacements = nearest is null ? Array.Empty<string>() : NearestAll(rest, pool) });
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
                    $"There's no table called {tableText}." + (near is null ? "" : $" Did you mean {near}?"), near)
                    { Replacements = near is null ? Array.Empty<string>() : NearestAll(tableText, _names.Tables.Keys) });
                return;
            }

            foreach (var (col, offset, rawLength) in ColumnSpecifiers(raw, open))
            {
                if (TableSpecials.Contains(col) || columns.Contains(col))
                    continue;
                var near = Nearest(col, columns);
                Add(new FormulaProblem(FormulaProblemKind.UnknownReference, tok.Start + offset, rawLength,
                    $"Table {tableText} has no column called {col}." + (near is null ? "" : $" Did you mean {near}?"),
                    near)
                    {
                        Replacements = near is null
                            ? Array.Empty<string>()
                            : NearestAll(col, columns).Select(EscapeColumn).ToList()
                    });
            }
        }
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    ///     The innermost bracketed items of a structured reference, unescaped, with their offset in
    ///     the token: <c>t[[#Data],[Fare]]</c> gives <c>#Data</c> and <c>Fare</c>; <c>t[@Fare]</c>
    ///     gives <c>Fare</c>.
    /// </summary>
    internal static IEnumerable<(string Column, int Offset, int RawLength)> ColumnSpecifiers(string raw, int open)
    {
        var results = new List<(string, int, int)>();
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

            // Span in the raw text: after '[' (and '@' and spaces), before trailing spaces and ']'.
            var rawStart = i + 1;
            if (rawStart < j && raw[rawStart] == '@')
                rawStart++;
            while (rawStart < j && raw[rawStart] == ' ')
                rawStart++;
            var rawEnd = j;
            while (rawEnd > rawStart && raw[rawEnd - 1] == ' ')
                rawEnd--;

            var item = sb.ToString();
            if (item.StartsWith("@", StringComparison.Ordinal) && item.Length > 1)
                item = item[1..];
            item = item.Trim();
            if (item.Length > 0)
                results.Add((item, rawStart, rawEnd - rawStart));
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

    /// <summary>
    ///     Every candidate close enough to be a typo of <paramref name="name"/> (the limits of
    ///     <see cref="Nearest"/>), nearest first, distinct, at most <paramref name="max"/>.
    /// </summary>
    internal static IReadOnlyList<string> NearestAll(string name, IEnumerable<string> candidates, int max = 3)
    {
        var limit = name.Length <= 4 ? 1 : name.Length <= 8 ? 2 : 3;
        var upper = name.ToUpperInvariant();
        return candidates
            .Where(c => Math.Abs(c.Length - name.Length) <= limit && !c.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => (Name: c, D: Distance(upper, c.ToUpperInvariant(), limit)))
            .Where(x => x.D <= limit)
            .OrderBy(x => x.D)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>A table column name as written inside <c>[ ]</c>: <c>' [ ] #</c> escaped with <c>'</c>.</summary>
    internal static string EscapeColumn(string column)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in column)
        {
            if (ch is '\'' or '[' or ']' or '#')
                sb.Append('\'');
            sb.Append(ch);
        }

        return sb.ToString();
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
