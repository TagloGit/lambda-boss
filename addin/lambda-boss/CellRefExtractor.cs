using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     Pulls cell-shaped references out of a formula string. Recognises
///     single cells (<c>A1</c> with optional dollar anchors), ranges
///     (<c>A1:A3</c>), spill refs (<c>A1#</c>), and any of those forms with
///     a sheet qualifier — unquoted (<c>Sheet1!A1</c>), quoted
///     (<c>'My Sheet'!A1</c>), or external workbook (<c>[Wb.xlsx]Sheet1!A1</c>,
///     <c>'[Wb.xlsx]My Sheet'!A1</c>, <c>'C:\path\[Wb.xlsx]Sheet'!A1</c>).
///     PR 5 consumes the trailing <c>#</c> on spill refs as part of the
///     match so the rewriter collapses the whole <c>A1#</c> token to a
///     binding name; the engine learns spill-ness from
///     <see cref="ICellSource.HasSpill" />, not from this flag. String
///     literals are skipped wholesale.
/// </summary>
internal static class CellRefExtractor
{
    // The single combined pattern walks each non-string segment looking for
    // an optional sheet qualifier (in one of four forms) followed by an A1
    // cell address with an optional ":cell" range tail OR a single '#'
    // spill marker (mutually exclusive — Excel has no `A1:A3#` syntax).
    // The bare alternative is a zero-width assertion that guards bare-cell
    // matches against being part of a larger token (mid-identifier, after
    // '!' / ':' / '#' etc.) — without it, the regex would also match the
    // row digits at the tail of a sheet name. The trailing range tail is
    // greedy by default, so `A1:A3` matches as one ref rather than two.
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
            @"(?<![A-Za-z0-9_.!:'\]#])(?<sheet>[A-Za-z_][A-Za-z0-9_.]*)!" +
            @"|" +
            // bare A1 — lookbehind also rejects '#' so a spill ref's tail
            // doesn't seed a follow-on bare-cell match (e.g. `A1#B1`
            // shouldn't extract B1 as a separate cell on the spill side).
            @"(?<![A-Za-z0-9_.!:'\]#])" +
        @")" +
        @"\$?(?<col>[A-Za-z]{1,3})\$?(?<row>[0-9]+)" +
        // Optional tail: either a `:cell` range end OR a `#` spill marker,
        // but not both. The capture is non-greedy across the alternation —
        // a range-shape match shadows the spill alternative because Excel
        // can't have a range AND a spill suffix on the same ref.
        @"(?:" +
            @":\$?(?<col2>[A-Za-z]{1,3})\$?(?<row2>[0-9]+)" +
            @"|" +
            @"(?<spill>\#)" +
        @")?" +
        // Trailing guards: not a longer identifier, not a sheet qualifier
        // ('!' for the next ref's sheet name), not a further range tail
        // (':') or a function-call tail ('('), not a further spill marker.
        @"(?![A-Za-z0-9_.!:(#])",
        RegexOptions.CultureInvariant);

    /// <summary>
    ///     Returns the unique formula refs found in <paramref name="formula" />
    ///     in order of first appearance. Single cells and ranges arrive as
    ///     distinct <see cref="FormulaRef" /> values. Unqualified refs use
    ///     <paramref name="defaultSheet" /> as the host sheet (the formula's
    ///     own sheet, NOT the sink's sheet — the walker recurses into other
    ///     sheets). Sheet-qualified refs use the qualifier as written;
    ///     external-workbook refs carry the workbook name through.
    /// </summary>
    public static IReadOnlyList<FormulaRef> Extract(string formula, string defaultSheet)
    {
        if (string.IsNullOrEmpty(formula))
            return Array.Empty<FormulaRef>();

        var seen = new HashSet<FormulaRef>();
        var refs = new List<FormulaRef>();

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
                var formulaRef = BuildFormulaRef(m, defaultSheet);
                if (seen.Add(formulaRef))
                    refs.Add(formulaRef);
            }

            i = segEnd;
        }

        return refs;
    }

    /// <summary>
    ///     Rewrites every in-scope ref in <paramref name="formula" /> to the
    ///     binding name supplied by <paramref name="lookup" />. Refs not in
    ///     <paramref name="lookup" /> (out-of-scope cells, named ranges,
    ///     LAMBDA names, function names, etc.) are left untouched. Ranges
    ///     are matched as a single token, so a range present in the lookup
    ///     collapses to one binding name even when its endpoints alone are
    ///     also bound elsewhere. The default-sheet rule matches
    ///     <see cref="Extract" />.
    /// </summary>
    public static string Rewrite(
        string formula,
        string defaultSheet,
        IReadOnlyDictionary<FormulaRef, string> lookup)
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
                var key = BuildFormulaRef(m, defaultSheet);
                return lookup.TryGetValue(key, out var name) ? name : m.Value;
            });
            result.Append(rewritten);
            i = segEnd;
        }
        return result.ToString();
    }

    private static FormulaRef BuildFormulaRef(Match m, string defaultSheet)
    {
        var start = BuildCellRef(m, defaultSheet);
        if (!m.Groups["col2"].Success)
            return new FormulaRef(start);

        // Range tail: End shares the sheet/workbook of Start (Excel
        // disallows cross-sheet ranges), so we don't reparse the qualifier.
        var endCol = CellRef.LettersToColumn(m.Groups["col2"].Value);
        var endRow = int.Parse(m.Groups["row2"].Value);
        var end = new CellRef(start.Sheet, endCol, endRow, start.ExternalWorkbook);
        return new FormulaRef(start, end);
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
