using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     Pulls A1-style cell references out of a formula string. PR 3 scope:
///     bare refs (<c>A1</c> with optional dollar anchors), sheet-qualified
///     refs in both unquoted (<c>Sheet1!A1</c>) and quoted
///     (<c>'My Sheet'!A1</c>) forms, and external-workbook refs in the open
///     (<c>[Wb.xlsx]Sheet1!A1</c>) and quoted (<c>'[Wb.xlsx]My Sheet'!A1</c>,
///     <c>'C:\path\[Wb.xlsx]Sheet'!A1</c>) forms. Range refs and spill refs
///     are still recognised only enough to be excluded — they're handled by
///     PR 4/5. String literals are skipped wholesale.
/// </summary>
internal static class CellRefExtractor
{
    // The single combined pattern walks each non-string segment looking for
    // an optional sheet qualifier (in one of four forms) followed by an A1
    // cell address. The bare alternative is a zero-width assertion that
    // guards bare-cell matches against being part of a larger token (mid-
    // identifier, after '!' / ':', etc.) — without it, the regex would also
    // match the row digits at the tail of a sheet name.
    private static readonly Regex CellRefPattern = new(
        @"(?:" +
            // 'C:\path\[Wb]Sheet'!  or  '[Wb]My Sheet'!  — quoted form
            @"'(?<wbPath>[^']*)\[(?<wbQ>[^\[\]]+)\](?<sheetWbQ>(?:[^']|'')*)'!" +
            @"|" +
            // [Wb]Sheet!  — open external workbook, simple sheet
            @"\[(?<wb>[^\[\]]+)\](?<sheetWb>[A-Za-z_][A-Za-z0-9_.]*)!" +
            @"|" +
            // 'Sheet'!  — quoted in-workbook sheet name
            @"'(?<sheetQ>(?:[^']|'')+)'!" +
            @"|" +
            // Sheet!  — unquoted in-workbook sheet name
            @"(?<![A-Za-z0-9_.!:'\]])(?<sheet>[A-Za-z_][A-Za-z0-9_.]*)!" +
            @"|" +
            // bare A1 — same lookbehind guard as before, plus '/']' to
            // reject bare matches immediately after a closed external tag.
            @"(?<![A-Za-z0-9_.!:'\]])" +
        @")" +
        @"\$?(?<col>[A-Za-z]{1,3})\$?(?<row>[0-9]+)" +
        // Trailing guards: not a longer identifier, not a sheet qualifier
        // ('!' for the next ref's sheet name), not a range tail (':') or a
        // function-call tail ('('), not a spill marker ('#' — PR 5).
        @"(?![A-Za-z0-9_.!:(#])",
        RegexOptions.CultureInvariant);

    /// <summary>
    ///     Returns the unique cell refs found in <paramref name="formula" />
    ///     in order of first appearance. Unqualified refs use
    ///     <paramref name="defaultSheet" /> as the host sheet (the formula's
    ///     own sheet, NOT the sink's sheet — the walker recurses into other
    ///     sheets). Sheet-qualified refs use the qualifier as written;
    ///     external-workbook refs carry the workbook name through.
    /// </summary>
    public static IReadOnlyList<CellRef> Extract(string formula, string defaultSheet)
    {
        if (string.IsNullOrEmpty(formula))
            return Array.Empty<CellRef>();

        var seen = new HashSet<CellRef>();
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
            // Note: single-quoted sheet names contain '!' / '[' / etc. but
            // never embedded double-quotes (Excel disallows " in sheet
            // names), so it's safe to slice on " here.
            var nextQuote = formula.IndexOf('"', i);
            var segEnd = nextQuote < 0 ? formula.Length : nextQuote;
            var segment = formula[i..segEnd];

            foreach (Match m in CellRefPattern.Matches(segment))
            {
                var cellRef = BuildCellRef(m, defaultSheet);
                if (seen.Add(cellRef))
                    refs.Add(cellRef);
            }

            i = segEnd;
        }

        return refs;
    }

    /// <summary>
    ///     Rewrites every in-scope cell ref in <paramref name="formula" />
    ///     to the binding name supplied by <paramref name="lookup" />. Refs
    ///     not in <paramref name="lookup" /> (out-of-scope, named ranges,
    ///     LAMBDA names, function names, etc.) are left untouched. The
    ///     default-sheet rule matches <see cref="Extract" />.
    /// </summary>
    public static string Rewrite(
        string formula,
        string defaultSheet,
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
                var key = BuildCellRef(m, defaultSheet);
                return lookup.TryGetValue(key, out var name) ? name : m.Value;
            });
            result.Append(rewritten);
            i = segEnd;
        }
        return result.ToString();
    }

    private static CellRef BuildCellRef(Match m, string defaultSheet)
    {
        var col = CellRef.LettersToColumn(m.Groups["col"].Value);
        var row = int.Parse(m.Groups["row"].Value);

        if (m.Groups["wbQ"].Success)
        {
            // Quoted external form. The workbook name is whatever sat
            // inside [...]; any path prefix outside the brackets is
            // discarded for equality, but we don't reconstruct it on
            // emit — closed-workbook paths get re-resolved by Excel.
            var wb = m.Groups["wbQ"].Value;
            var sheet = m.Groups["sheetWbQ"].Value.Replace("''", "'");
            return new CellRef(sheet, col, row, wb);
        }

        if (m.Groups["wb"].Success)
        {
            return new CellRef(m.Groups["sheetWb"].Value, col, row, m.Groups["wb"].Value);
        }

        if (m.Groups["sheetQ"].Success)
        {
            var sheet = m.Groups["sheetQ"].Value.Replace("''", "'");
            return new CellRef(sheet, col, row);
        }

        if (m.Groups["sheet"].Success)
        {
            return new CellRef(m.Groups["sheet"].Value, col, row);
        }

        return new CellRef(defaultSheet, col, row);
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
