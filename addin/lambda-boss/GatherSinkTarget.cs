namespace LambdaBoss;

/// <summary>
///     What the add-in read, on Excel's thread before the dialog opened, about a cell
///     Gather might write to instead of the one it started on (issue #470).
///     <see cref="SpillAnchor" /> is the anchor of the spill the cell is a child of, or
///     null when it is not inside one (an anchor is not its own child).
///     <see cref="IsPrecedent" /> is Excel's own answer (<c>Range.Precedents</c> of the start
///     cell): the calculation reads this cell, through whole-column, table or name refs too.
/// </summary>
public readonly record struct SinkCellInfo(
    bool IsEmpty, bool HasFormula, CellRef? SpillAnchor = null, bool IsPrecedent = false)
{
    public static readonly SinkCellInfo Empty = new(true, false);
}

/// <summary>Whether the result may be written to a target, and what to say about it.</summary>
public sealed record SinkTargetCheck(bool Blocked, string? Message)
{
    public static readonly SinkTargetCheck Fine = new(false, null);
}

/// <summary>
///     Issue #470 (behind <c>gather-next</c>): Alt+Right / Alt+Left in the Gather dialog
///     move the cell the result is written to along the start cell's row, so the cell
///     Gather started on (and the chain behind it) stays as a fallback. The written text is
///     the same wherever it lands: its references are A1 text, which names the same cells
///     from any cell, and filling down from the new cell shifts the row-relative ones as it
///     would from the start cell. Formulas that depend on the cell they sit in
///     (<c>COLUMN()</c>, <c>INDIRECT</c>, <c>OFFSET</c>) are the exception, and get a warning.
/// </summary>
public static class GatherSinkTarget
{
    /// <summary>How many targets Alt+Right steps through, one column per press.</summary>
    public const int MaxOffset = 10;

    private const int LastColumn = 16384;

    /// <summary>
    ///     The offset after a move of <paramref name="delta" />: never left of the start
    ///     cell, never past the last of <see cref="MaxOffset" /> targets or the sheet's last
    ///     column. <paramref name="firstOffset" /> is the first target's offset: 1, or for a
    ///     start cell that spills right, its spill's width, so the first Alt+Right steps over
    ///     the spill (writing inside it would break the start cell).
    /// </summary>
    public static int Move(CellRef start, int offset, int delta, int firstOffset = 1)
    {
        firstOffset = Math.Max(1, firstOffset);
        var max = Math.Min(LastOffset(firstOffset), LastColumn - start.Column);
        if (max < firstOffset)
            return 0;
        var next = offset + delta;
        if (next <= 0 || (delta < 0 && next < firstOffset))
            return 0;
        return Math.Max(firstOffset, Math.Min(max, next));
    }

    /// <summary>The furthest offset Alt+Right reaches.</summary>
    public static int LastOffset(int firstOffset) => Math.Max(1, firstOffset) + MaxOffset - 1;

    public static CellRef At(CellRef start, int offset) =>
        offset == 0 ? start : start with { Column = start.Column + offset };

    /// <summary>
    ///     Checks a target other than the start cell. Blocked when writing there would
    ///     change the calculation: the target is a walked cell or inside an input range or
    ///     spill, or the written formulas read it (a circular reference). Otherwise a
    ///     warning when the target holds something that would be overwritten.
    /// </summary>
    /// <param name="formulas">The text written to the target and, for a LAMBDA, the LAMBDA itself.</param>
    public static SinkTargetCheck Check(
        CellRef start,
        CellRef target,
        IReadOnlyList<BindingRow> bindings,
        IEnumerable<string> formulas,
        SinkCellInfo info)
    {
        if (target.Equals(start))
            return SinkTargetCheck.Fine;

        var addr = target.A1Address;

        if (info.SpillAnchor != null && info.SpillAnchor.Equals(start))
            return new SinkTargetCheck(true, $"{addr} is inside {start.A1Address}'s spill");

        if (info.IsPrecedent)
            return new SinkTargetCheck(true, $"{addr} is part of the calculation");

        foreach (var row in bindings)
        {
            var src = row.Source;
            if (src.Start.Equals(target) || src.Covers(target)
                || (info.SpillAnchor != null
                    && (src.Start.Equals(info.SpillAnchor) || info.SpillAnchor.Equals(row.SliceOf?.Start))))
                return new SinkTargetCheck(true, $"{addr} is part of the calculation");
        }

        foreach (var formula in formulas)
        foreach (var r in CellRefExtractor.Extract(formula, start.Sheet))
        {
            if (r.Start.Equals(target) || r.Covers(target)
                || (r.IsSpilled && info.SpillAnchor != null && r.Start.Equals(info.SpillAnchor)))
                return new SinkTargetCheck(true, $"{addr} is read by the formula (circular)");
        }

        foreach (var formula in formulas)
            if (DependsOnPosition(formula))
                return new SinkTargetCheck(false,
                    $"The formula uses COLUMN(), INDIRECT or OFFSET: check {addr}'s value");

        if (info.SpillAnchor != null)
            return new SinkTargetCheck(false,
                $"{addr} is inside {info.SpillAnchor.A1Address}'s spill, which will show #SPILL!");
        if (info.HasFormula)
            return new SinkTargetCheck(false, $"Overwrites the formula in {addr}");
        if (!info.IsEmpty)
            return new SinkTargetCheck(false, $"Overwrites the value in {addr}");
        return SinkTargetCheck.Fine;
    }

    /// <summary>
    ///     True when the formula may depend on the column it sits in: <c>COLUMN()</c> without
    ///     an argument, <c>INDIRECT</c> (R1C1 text is relative to the caller) or <c>OFFSET</c>.
    ///     <c>ROW()</c> is unaffected: the target is on the same row. String literals are ignored.
    /// </summary>
    internal static bool DependsOnPosition(string formula)
    {
        var code = StripStrings(formula).ToUpperInvariant().Replace(" ", "");
        return code.Contains("COLUMN()") || code.Contains("COLUMNS()")
               || code.Contains("INDIRECT(") || code.Contains("OFFSET(");
    }

    private static string StripStrings(string formula)
    {
        var sb = new System.Text.StringBuilder(formula.Length);
        var inString = false;
        foreach (var c in formula)
        {
            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString)
                sb.Append(c);
        }

        return sb.ToString();
    }
}
