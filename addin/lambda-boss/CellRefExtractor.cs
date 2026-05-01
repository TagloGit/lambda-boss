using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     Pulls A1-style cell references out of a formula string. PR 1 scope:
///     unqualified single-cell A1 refs (with optional dollar anchors) on the
///     same sheet as the sink. Range refs (<c>A1:B2</c>), sheet-qualified
///     refs (<c>Sheet1!A1</c>), and spill refs (<c>A1#</c>) are recognised
///     only enough to be excluded — they're handled by later PRs. String
///     literals are skipped wholesale.
/// </summary>
internal static class CellRefExtractor
{
    private static readonly Regex CellRefPattern = new(
        // Lookbehind: not preceded by an identifier char, '!' (sheet-qualified)
        // or ':' (range). Lookahead: not followed by an identifier char,
        // '!' or ':' (range), '(' (function call like LOG10(), where LOG10
        // matches the letters+digits shape but isn't a cell ref) or '#'
        // (spill — out of scope for PR 1).
        @"(?<![A-Za-z0-9_.!:])\$?([A-Za-z]{1,3})\$?([0-9]+)(?![A-Za-z0-9_.!:(#])",
        RegexOptions.CultureInvariant);

    /// <summary>
    ///     Returns the unique single-cell A1-style refs found in
    ///     <paramref name="formula" />, all qualified with
    ///     <paramref name="sinkSheet" />. Order is the order of first
    ///     appearance.
    /// </summary>
    public static IReadOnlyList<CellRef> Extract(string formula, string sinkSheet)
    {
        if (string.IsNullOrEmpty(formula))
            return Array.Empty<CellRef>();

        var seen = new HashSet<(int Col, int Row)>();
        var refs = new List<CellRef>();

        var i = 0;
        while (i < formula.Length)
        {
            if (formula[i] == '"')
            {
                i = SkipString(formula, i);
                continue;
            }

            // Find the next quote so we only scan the unquoted segment.
            var nextQuote = formula.IndexOf('"', i);
            var segEnd = nextQuote < 0 ? formula.Length : nextQuote;
            var segment = formula[i..segEnd];

            foreach (Match m in CellRefPattern.Matches(segment))
            {
                var col = CellRef.LettersToColumn(m.Groups[1].Value);
                var row = int.Parse(m.Groups[2].Value);
                if (seen.Add((col, row)))
                    refs.Add(new CellRef(sinkSheet, col, row));
            }

            i = segEnd;
        }

        return refs;
    }

    /// <summary>
    ///     Rewrites every in-scope cell ref in <paramref name="formula" />
    ///     to the binding name supplied by <paramref name="lookup" />. Refs
    ///     not in <paramref name="lookup" /> (out-of-scope, named ranges,
    ///     LAMBDA names, function names, etc.) are left untouched. String
    ///     literals are preserved verbatim.
    /// </summary>
    public static string Rewrite(
        string formula,
        string sinkSheet,
        IReadOnlyDictionary<CellRef, string> lookup)
    {
        if (string.IsNullOrEmpty(formula) || lookup.Count == 0)
            return formula;

        var result = new System.Text.StringBuilder(formula.Length);
        var i = 0;
        while (i < formula.Length)
        {
            if (formula[i] == '"')
            {
                var end = SkipString(formula, i);
                result.Append(formula, i, end - i);
                i = end;
                continue;
            }

            var nextQuote = formula.IndexOf('"', i);
            var segEnd = nextQuote < 0 ? formula.Length : nextQuote;
            var segment = formula[i..segEnd];
            var rewritten = CellRefPattern.Replace(segment, m =>
            {
                var col = CellRef.LettersToColumn(m.Groups[1].Value);
                var row = int.Parse(m.Groups[2].Value);
                var key = new CellRef(sinkSheet, col, row);
                return lookup.TryGetValue(key, out var name) ? name : m.Value;
            });
            result.Append(rewritten);
            i = segEnd;
        }
        return result.ToString();
    }

    private static int SkipString(string text, int openQuoteIndex)
    {
        var i = openQuoteIndex + 1;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return text.Length;
    }
}
