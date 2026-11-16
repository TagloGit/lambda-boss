using System.Globalization;
using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     The cells the example check reads (spec 0020). Rows and columns are 1-based sheet indices.
///     <see cref="Value"/> returns what <c>Range.Value2</c> gives: a double, a string, a bool, an
///     int (an error code) or null for an empty cell.
/// </summary>
internal interface IExampleSheet
{
    object? Value(int row, int column);

    bool IsFormula(int row, int column);

    /// <summary>The last column worth reading (the used range's), at least the formula's.</summary>
    int LastColumn { get; }
}

/// <summary>
///     What was written. <paramref name="Formula"/> is the example cell's <c>Formula2</c> (the top
///     cell of the filled family, or the spilling cell). <paramref name="OtherRows"/> are the family's
///     other rows (a fill) or the rows the result spills into below the formula (a spill): a value
///     on the example row only counts when it differs on one of them.
/// </summary>
internal sealed record ExampleCheckRequest(
    string Formula,
    int Row,
    int Column,
    IReadOnlyList<int> OtherRows,
    IReadOnlyCollection<int> TargetColumns);

/// <summary>
///     One literal that looks like a value from the worked example. <see cref="Start"/> and
///     <see cref="Length"/> index the formula. <see cref="SourceRow"/>/<see cref="SourceColumn"/>
///     are the matching cell (0 when the match is the level's example text).
/// </summary>
internal sealed record ExampleLiteralFinding(
    int Start,
    int Length,
    string Literal,
    int SourceRow,
    int SourceColumn,
    string SourceAddress,
    string Message)
{
    public bool FromText => SourceRow == 0;
}

/// <summary>
///     The example check (spec 0020, excel-esports#80): after a formula is filled to several cells,
///     or spills, flags a constant in it that equals a value from the worked example, the slip
///     where the example passes because the value came from the example. Three sources, in order:
///     a typed or computed cell on the example row (left or right of the formula, within its
///     block) whose column varies across the family and that the formula doesn't read; the first
///     row of a multi-row range the formula reads; a number the level's example text states and
///     its rules text doesn't. Pure and read-only.
/// </summary>
internal static class ExampleLiteralChecker
{
    /// <summary>Rows of the family (or spill) compared to decide whether a column varies.</summary>
    public const int MaxCompareRows = 50;

    /// <summary>How far above the formula the level's text is looked for.</summary>
    public const int TextRowsAbove = 80;

    /// <summary>Columns searched for the level's text.</summary>
    public const int TextColumns = 30;

    private const int MaxRowColumns = 200;
    private const int MaxRangeColumns = 30;
    private const int MinProseLength = 26;

    /// <summary>
    ///     Never flagged: 0, ±1, and unit or scale constants (100, 1000, 10000, 100000, 1000000).
    /// </summary>
    private static readonly HashSet<double> TrivialNumbers = new() { 0, 1, -1, 100, 1000, 10000, 100000, 1000000 };

    /// <summary>
    ///     Function → which 0-based arguments are structure (an index, a count, a column number, a
    ///     format, a lookup's mode), not data. Literals there are never flagged.
    /// </summary>
    private static readonly Dictionary<string, Func<int, bool>> StructuralArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INDEX"] = i => i >= 1, ["OFFSET"] = _ => true, ["TAKE"] = i => i >= 1, ["DROP"] = i => i >= 1,
        ["CHOOSECOLS"] = i => i >= 1, ["CHOOSEROWS"] = i => i >= 1, ["SEQUENCE"] = _ => true,
        ["ROUND"] = i => i == 1, ["ROUNDUP"] = i => i == 1, ["ROUNDDOWN"] = i => i == 1, ["MROUND"] = i => i == 1,
        ["TRUNC"] = i => i >= 1, ["MOD"] = i => i == 1, ["QUOTIENT"] = i => i == 1, ["POWER"] = i => i == 1,
        ["LEFT"] = i => i >= 1, ["RIGHT"] = i => i >= 1, ["MID"] = i => i >= 1,
        ["XLOOKUP"] = i => i >= 3, ["XMATCH"] = i => i >= 2, ["MATCH"] = i => i >= 2,
        ["VLOOKUP"] = i => i >= 2, ["HLOOKUP"] = i => i >= 2, ["LOOKUP"] = _ => false,
        ["FIND"] = i => i >= 2, ["SEARCH"] = i => i >= 2, ["SUBSTITUTE"] = i => i >= 3,
        ["TOCOL"] = i => i >= 1, ["TOROW"] = i => i >= 1, ["SORT"] = i => i >= 1, ["SORTBY"] = i => i >= 1,
        ["UNIQUE"] = i => i >= 1, ["EXPAND"] = i => i >= 1, ["REPT"] = i => i >= 1,
        ["LARGE"] = i => i >= 1, ["SMALL"] = i => i >= 1, ["RANK"] = i => i >= 2, ["RANK.EQ"] = i => i >= 2,
        ["RANK.AVG"] = i => i >= 2, ["PERCENTILE"] = i => i >= 1, ["QUARTILE"] = i => i >= 1,
        ["DATE"] = _ => true, ["TIME"] = _ => true, ["TEXT"] = i => i >= 1,
        ["REGEXEXTRACT"] = i => i >= 2, ["REGEXREPLACE"] = i => i >= 3, ["REGEXTEST"] = i => i >= 2,
        ["RANDBETWEEN"] = _ => true, ["RANDARRAY"] = _ => true,
        ["EDATE"] = i => i >= 1, ["EOMONTH"] = i => i >= 1, ["WEEKDAY"] = i => i >= 1, ["WEEKNUM"] = i => i >= 1,
        ["SUBTOTAL"] = i => i == 0, ["AGGREGATE"] = i => i <= 1, ["MAKEARRAY"] = i => i <= 1,
        ["BASE"] = i => i >= 1, ["DECIMAL"] = i => i >= 1, ["LOG"] = i => i >= 1,
        ["COLUMN"] = _ => true, ["ROW"] = _ => true, ["ADDRESS"] = _ => true, ["INDIRECT"] = _ => true,
        ["TEXTSPLIT"] = i => i >= 1, ["TEXTJOIN"] = i => i <= 1, ["TEXTBEFORE"] = i => i >= 1,
        ["TEXTAFTER"] = i => i >= 1, ["ARRAYTOTEXT"] = _ => true, ["VALUETOTEXT"] = _ => true,
        ["CHAR"] = _ => true, ["UNICHAR"] = _ => true, ["CODE"] = _ => true, ["UNICODE"] = _ => true,
        ["FILTER"] = i => i >= 2, ["IFERROR"] = i => i >= 1, ["IFNA"] = i => i >= 1,
        ["WRAPROWS"] = i => i >= 1, ["WRAPCOLS"] = i => i >= 1, ["GROUPBY"] = i => i >= 2, ["PIVOTBY"] = i => i >= 3,
        ["BITAND"] = _ => true, ["BITOR"] = _ => true, ["BITXOR"] = _ => true, ["BITLSHIFT"] = _ => true,
        ["BITRSHIFT"] = _ => true, ["DEC2BIN"] = _ => true, ["BIN2DEC"] = _ => true, ["DEC2HEX"] = _ => true,
        ["HEX2DEC"] = _ => true, ["CEILING"] = i => i >= 1, ["FLOOR"] = i => i >= 1,
        ["CEILING.MATH"] = i => i >= 1, ["FLOOR.MATH"] = i => i >= 1,
        // An in-formula lookup table: its keys and results are the case's rules.
        ["SWITCH"] = i => i >= 1,
        ["CHOOSE"] = i => i == 0,
        ["LAMBDA"] = _ => true
    };

    private static readonly string[] HiddenPrefixes = { "_xlfn.", "_xlws.", "_xlpm.", "_xludf." };

    private static readonly Regex CellRefPattern = new(
        @"^(\$?)([A-Za-z]{1,3})(\$?)([0-9]{1,7})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Heading = new(
        @"^\s*(level|bonus)\s*\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExampleStart = new(
        @"^\s*(for\s+)?example", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProseNumber = new(
        @"(?<![\w.])-?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?%?", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Word = new(@"\w+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ------------------------------------------------------------ the check

    /// <summary>
    ///     The findings for one written formula, most certain first (cells before text). Empty when
    ///     nothing matches. <paramref name="ruleWords"/> gives the lower-cased words of the
    ///     workbook's prose outside its examples; it's asked only when a text literal matches a cell.
    /// </summary>
    public static IReadOnlyList<ExampleLiteralFinding> Check(ExampleCheckRequest request, IExampleSheet sheet,
        Func<IReadOnlyCollection<string>>? ruleWords = null)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (sheet is null)
            throw new ArgumentNullException(nameof(sheet));

        var formula = request.Formula;
        if (string.IsNullOrEmpty(formula) || formula[0] != '=')
            return Array.Empty<ExampleLiteralFinding>();

        List<FormulaLiteral> literals;
        List<FormulaCellRef> refs;
        try
        {
            (literals, refs) = Scan(formula);
        }
        catch (FormatException)
        {
            return Array.Empty<ExampleLiteralFinding>();
        }

        literals.RemoveAll(l => l.Structural || IsTrivial(l));
        if (literals.Count == 0)
            return Array.Empty<ExampleLiteralFinding>();

        var candidates = Candidates(request, sheet, refs);
        var (rulesNumbers, exampleNumbers) = LevelNumbers(request, sheet);

        IReadOnlyCollection<string>? words = null;
        var findings = new List<ExampleLiteralFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lit in literals)
        {
            var small = lit.IsNumber && Math.Abs(lit.Number) <= 10 && Math.Abs(lit.Number % 1) < 1e-12;
            var hit = candidates.FirstOrDefault(c => Same(lit, c.Value) && !(small && c.IsFormula));
            if (hit is not null && !lit.IsNumber && lit.Text.Trim().Length >= 2)
            {
                // A word the rules use ("Bull", "Azure") is the case's vocabulary, not the example's data.
                words ??= ruleWords?.Invoke() ?? Array.Empty<string>();
                if (InProse(lit.Text, words))
                    hit = null;
            }

            if (hit is not null)
            {
                if (seen.Add($"{lit.Source}|{hit.Address}"))
                    findings.Add(new ExampleLiteralFinding(lit.Start, lit.Length, lit.Source, hit.Row, hit.Column,
                        hit.Address, CellMessage(lit, hit)));
                continue;
            }

            if (lit.IsNumber && exampleNumbers.TryGetValue(Key(lit.Number), out var line)
                             && !rulesNumbers.Contains(Key(lit.Number)))
            {
                if (seen.Add($"{lit.Source}|text"))
                    findings.Add(new ExampleLiteralFinding(lit.Start, lit.Length, lit.Source, 0, 0, "",
                        $"{lit.Source} is a number from this level's example (\"{Excerpt(line, lit.Number)}\"), "
                        + "and its rules don't mention it. If it's the example's value, compute it or point at it."));
            }
        }

        return findings;
    }

    private static string CellMessage(FormulaLiteral lit, Candidate hit) =>
        hit.FromRange
            ? $"{lit.Source} equals {hit.Address}, the first row of a range this formula reads, and that column changes further down. "
              + "If it's the example's value, it won't be right for the other rows."
            : $"{lit.Source} equals {hit.Address} on the example row, and that column changes on the other rows. "
              + $"Use {hit.Address} instead of the typed value?";

    // ------------------------------------------------------------ candidates

    private sealed record Candidate(object Value, int Row, int Column, string Address, bool IsFormula, bool FromRange);

    private static List<Candidate> Candidates(ExampleCheckRequest request, IExampleSheet sheet, List<FormulaCellRef> refs)
    {
        var list = new List<Candidate>();
        var others = request.OtherRows.Take(MaxCompareRows).ToList();
        var read = new HashSet<(int, int)>(refs.Where(r => r.IsSingleCell).Select(r => (r.Row1, r.Col1)));

        // 1. The example row, both ways from the formula, within its block (two empty columns end it).
        if (others.Count > 0)
        {
            var last = Math.Min(Math.Max(sheet.LastColumn, request.Column), MaxRowColumns);
            foreach (var c in Block(sheet, request.Row, request.Column, -1, last)
                         .Concat(Block(sheet, request.Row, request.Column, 1, last)))
            {
                if (request.TargetColumns.Contains(c) || read.Contains((request.Row, c)))
                    continue; // a cell the formula reads is used, not replaced
                var v = sheet.Value(request.Row, c);
                if (!Usable(v))
                    continue;
                if (others.Any(r => !Same(v!, sheet.Value(r, c))))
                    list.Add(new Candidate(v!, request.Row, c, Address(request.Row, c), sheet.IsFormula(request.Row, c), false));
            }
        }

        // 2. The first row of each multi-row range the formula reads, where its column varies.
        foreach (var r in refs.Where(r => !r.IsSingleCell && r.Row2 > r.Row1 && r.Col2 - r.Col1 < MaxRangeColumns))
        {
            var lastRow = Math.Min(r.Row2, r.Row1 + MaxCompareRows);
            for (var c = r.Col1; c <= r.Col2; c++)
            {
                var v = sheet.Value(r.Row1, c);
                if (!Usable(v))
                    continue;
                var varies = false;
                for (var row = r.Row1 + 1; row <= lastRow && !varies; row++)
                    varies = !Same(v!, sheet.Value(row, c));
                if (varies)
                    list.Add(new Candidate(v!, r.Row1, c, Address(r.Row1, c), sheet.IsFormula(r.Row1, c), true));
            }
        }

        return list;
    }

    private static IEnumerable<int> Block(IExampleSheet sheet, int row, int column, int step, int lastColumn)
    {
        var gap = 0;
        for (var c = column + step; c >= 1 && c <= lastColumn; c += step)
        {
            if (sheet.Value(row, c) is null && !sheet.IsFormula(row, c))
            {
                if (++gap >= 2)
                    yield break;
                continue;
            }

            gap = 0;
            yield return c;
        }
    }

    private static bool Usable(object? v) =>
        v switch
        {
            null => false,
            bool => false,
            int => false, // an error value
            string s => s.Trim().Length > 0,
            _ => true
        };

    // ------------------------------------------------------------ the level's text

    /// <summary>
    ///     Numbers stated in the level's text above the formula: its rules (before the first line
    ///     starting "Example") and its example (from there on), keyed by <see cref="Key"/>; each
    ///     example number keeps its line for the message.
    /// </summary>
    private static (HashSet<double> Rules, Dictionary<double, string> Example) LevelNumbers(ExampleCheckRequest request,
        IExampleSheet sheet)
    {
        var lines = new List<string>();
        var lastCol = Math.Min(TextColumns, Math.Max(1, sheet.LastColumn));
        for (var r = request.Row - 1; r >= 1 && r >= request.Row - TextRowsAbove; r--)
        {
            var row = new List<string>();
            var heading = false;
            for (var c = 1; c <= lastCol; c++)
            {
                if (sheet.Value(r, c) is not string s)
                    continue;
                if (Heading.IsMatch(s))
                    heading = true;
                if (s.Length >= MinProseLength)
                    row.Add(s);
            }

            lines.InsertRange(0, row);
            if (heading)
                break;
        }

        var rules = new HashSet<double>();
        var example = new Dictionary<double, string>();
        var inExample = false;
        foreach (var line in lines)
        {
            if (!inExample && ExampleStart.IsMatch(line))
                inExample = true;
            foreach (var n in Numbers(line))
            {
                if (inExample)
                {
                    if (!example.ContainsKey(n))
                        example[n] = line;
                }
                else
                    rules.Add(n);
            }
        }

        return (rules, example);
    }

    internal static IEnumerable<double> Numbers(string line)
    {
        foreach (Match m in ProseNumber.Matches(line))
        {
            var t = m.Value.Replace(",", "");
            var pct = t.EndsWith("%", StringComparison.Ordinal);
            if (pct)
                t = t[..^1];
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                continue;
            yield return Key(v);
            if (pct)
                yield return Key(v / 100);
        }
    }

    /// <summary>
    ///     The lower-cased words of prose (text cells of 26+ characters) outside example paragraphs.
    ///     A level or bonus heading ends an example. Used for the workbook's rules vocabulary.
    /// </summary>
    public static HashSet<string> RuleWords(IEnumerable<string> cellTexts)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var inExample = false;
        foreach (var s in cellTexts)
        {
            if (Heading.IsMatch(s))
                inExample = false;
            if (ExampleStart.IsMatch(s))
                inExample = true;
            if (inExample || s.Length < MinProseLength)
                continue;
            foreach (Match m in Word.Matches(s))
                words.Add(m.Value.ToLowerInvariant());
        }

        return words;
    }

    private static bool InProse(string text, IReadOnlyCollection<string> words)
    {
        var parts = Word.Matches(text).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).ToList();
        return parts.Count > 0 && parts.All(words.Contains);
    }

    private static string Excerpt(string line, double number)
    {
        var at = -1;
        foreach (Match m in ProseNumber.Matches(line))
        {
            var t = m.Value.Replace(",", "").TrimEnd('%');
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                && (Key(v) == Key(number) || (m.Value.EndsWith("%", StringComparison.Ordinal) && Key(v / 100) == Key(number))))
            {
                at = m.Index;
                break;
            }
        }

        if (at < 0 || line.Length <= 70)
            return line.Trim();
        var from = Math.Max(0, at - 35);
        var to = Math.Min(line.Length, at + 35);
        return (from > 0 ? "…" : "") + line[from..to].Trim() + (to < line.Length ? "…" : "");
    }

    // ------------------------------------------------------------ formula scan

    /// <summary>A number or string literal in the formula, with whether its argument slot is structure.</summary>
    internal sealed record FormulaLiteral(int Start, int Length, string Source, bool IsNumber, double Number, string Text,
        bool Structural);

    /// <summary>A same-sheet A1 reference (1-based, normalised so Row1 ≤ Row2 and Col1 ≤ Col2).</summary>
    internal sealed record FormulaCellRef(int Row1, int Col1, int Row2, int Col2)
    {
        public bool IsSingleCell => Row1 == Row2 && Col1 == Col2;
    }

    /// <summary>The formula's literals and same-sheet references, in one pass over its tokens.</summary>
    internal static (List<FormulaLiteral> Literals, List<FormulaCellRef> Refs) Scan(string formula)
    {
        var tokens = FormulaParser.Tokenize(formula);
        var literals = new List<FormulaLiteral>();
        var refs = new List<FormulaCellRef>();
        var stack = new Stack<(string? Fn, int Arg)>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
            switch (t.Type)
            {
                case FormulaTokenType.Name when next?.Type == FormulaTokenType.LParen:
                    stack.Push((Strip(t.Text), 0));
                    i++; // the bracket belongs to the call
                    break;
                case FormulaTokenType.LParen:
                    stack.Push((null, 0));
                    break;
                case FormulaTokenType.RParen:
                    if (stack.Count > 0)
                        stack.Pop();
                    break;
                case FormulaTokenType.Comma:
                    if (stack.Count > 0)
                    {
                        var top = stack.Pop();
                        stack.Push((top.Fn, top.Arg + 1));
                    }

                    break;
                case FormulaTokenType.Name:
                    AddRef(tokens, i, refs);
                    break;
                case FormulaTokenType.Number:
                case FormulaTokenType.String:
                {
                    // Only the innermost slot counts: inside a plain bracket, ROUND((x)*2, 3)'s 2 is data.
                    var structural = stack.Count > 0 && stack.Peek() is { Fn: { } fn, Arg: var arg }
                                     && StructuralArgs.TryGetValue(fn, out var pred) && pred(arg);

                    if (t.Type == FormulaTokenType.String)
                    {
                        var text = t.Text.Length >= 2 ? t.Text[1..^1].Replace("\"\"", "\"") : "";
                        literals.Add(new FormulaLiteral(t.Start, t.Text.Length, t.Text, false, 0, text, structural));
                        break;
                    }

                    if (!double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        break;
                    var start = t.Start;
                    var source = t.Text;
                    var prev = i > 0 ? tokens[i - 1] : null;
                    var before = i > 1 ? tokens[i - 2] : null;
                    if (prev is { Type: FormulaTokenType.Operator, Text: "-" }
                        && (before is null || before.Type is FormulaTokenType.LParen or FormulaTokenType.Comma
                            || (before.Type == FormulaTokenType.Operator && before.Text != "%")))
                    {
                        v = -v;
                        start = prev.Start;
                        source = "-" + t.Text;
                    }

                    if (next is { Type: FormulaTokenType.Operator, Text: "%" })
                    {
                        v /= 100;
                        source += "%";
                    }

                    literals.Add(new FormulaLiteral(start, t.End - start + (source.EndsWith("%", StringComparison.Ordinal) ? 1 : 0),
                        source, true, v, t.Text, structural));
                    break;
                }
            }
        }

        return (literals, refs);
    }

    private static void AddRef(IReadOnlyList<FormulaToken> tokens, int i, List<FormulaCellRef> refs)
    {
        var t = tokens[i];
        // The second half of A1:B5 was handled with the first.
        if (i >= 2 && tokens[i - 1] is { Type: FormulaTokenType.Operator, Text: ":" } && tokens[i - 2].Type == FormulaTokenType.Name
            && TryCell(tokens[i - 2].Text, out _, out _))
            return;
        var text = t.Text;
        if (text.IndexOf('!') >= 0 || text.IndexOf('[') >= 0)
            return; // another sheet, or a structured reference
        string? second = null;
        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            second = text[(colon + 1)..];
            text = text[..colon];
        }
        else if (i + 2 < tokens.Count && tokens[i + 1] is { Type: FormulaTokenType.Operator, Text: ":" }
                                      && tokens[i + 2].Type == FormulaTokenType.Name)
            second = tokens[i + 2].Text;

        if (!TryCell(text, out var r1, out var c1))
            return;
        if (second is null)
        {
            refs.Add(new FormulaCellRef(r1, c1, r1, c1));
            return;
        }

        if (TryCell(second, out var r2, out var c2))
            refs.Add(new FormulaCellRef(Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2)));
    }

    internal static bool TryCell(string text, out int row, out int column)
    {
        row = column = 0;
        var m = CellRefPattern.Match(text);
        if (!m.Success)
            return false;
        column = 0;
        foreach (var ch in m.Groups[2].Value.ToUpperInvariant())
            column = column * 26 + (ch - 'A' + 1);
        if (column > 16384)
            return false;
        row = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        return row is >= 1 and <= 1048576;
    }

    private static string Strip(string name)
    {
        foreach (var p in HiddenPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return name[p.Length..].ToUpperInvariant();
        return name.ToUpperInvariant();
    }

    // ------------------------------------------------------------ helpers

    private static bool IsTrivial(FormulaLiteral l) =>
        l.IsNumber ? TrivialNumbers.Contains(l.Number) : l.Text.Trim().Length == 0;

    private static bool Same(FormulaLiteral lit, object value) =>
        lit.IsNumber
            ? value is double d && Near(lit.Number, d)
            : value is string s && string.Equals(lit.Text.Trim(), s.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Same(object a, object? b) =>
        (a, b) switch
        {
            (double x, double y) => Near(x, y),
            (string x, string y) => string.Equals(x.Trim(), y.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => Equals(a, b)
        };

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    /// <summary>A number rounded to 9 decimals, as a set key.</summary>
    private static double Key(double v) => Math.Round(v, 9);

    internal static string Address(int row, int column)
    {
        var col = "";
        for (var c = column; c > 0; c = (c - 1) / 26)
            col = (char)('A' + (c - 1) % 26) + col;
        return col + row.ToString(CultureInfo.InvariantCulture);
    }
}
