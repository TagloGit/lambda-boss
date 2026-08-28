namespace LambdaBoss;

/// <summary>
///     The shape of the reference being sliced, as it was written in the
///     author's formula. The slice ladder keys on the shape of the
///     <em>reference</em>, not the shape of the result — that is what keeps
///     <c>INDEX</c> (scalar) and <c>TAKE</c> (array) from being confused on a
///     1×1 spill, where <c>A1#</c> asks for the array and <c>A1</c> asks for
///     the scalar even though both cover the same one cell.
/// </summary>
internal enum SliceRefShape
{
    /// <summary>A spilled-array reference, e.g. <c>A1#</c>. Always the whole array.</summary>
    SpillRef,

    /// <summary>A single-cell reference, e.g. <c>B1</c>.</summary>
    SingleCell,

    /// <summary>A contiguous range reference, e.g. <c>A2:B3</c> — including the degenerate <c>A2:A2</c>.</summary>
    Range
}

/// <summary>
///     Pure generator for the slice expression that replaces a reference
///     landing inside a spill range (spec 0010, <em>The slice ladder</em>).
///     No COM, no engine types beyond the binding name string — every input
///     is a plain integer, so the whole ladder is exhaustible in unit tests.
///
///     Given the spill's dimensions R×C, the reference's rectangle within the
///     spill (1-based rows <c>r1..r2</c>, columns <c>c1..c2</c>, relative to
///     the anchor) and the reference's shape, <see cref="Build" /> returns one
///     of exactly four forms: <c>arr</c>, <c>INDEX(arr,r,c)</c>,
///     <c>TAKE(arr,…)</c>, or <c>TAKE(DROP(arr,…),…)</c>.
///
///     All offsets are positional and freeze at gather time; the generator
///     deliberately emits no shape-robust forms such as
///     <c>INDEX(arr,1,COLUMNS(arr))</c> (see spec 0010, <em>Out of Scope</em>).
/// </summary>
internal static class SpillSliceBuilder
{
    /// <summary>
    ///     Builds the slice expression for a reference covering rows
    ///     <paramref name="r1" />..<paramref name="r2" /> and columns
    ///     <paramref name="c1" />..<paramref name="c2" /> of a
    ///     <paramref name="spillRows" />×<paramref name="spillColumns" />
    ///     spill bound to <paramref name="bindingName" />.
    ///
    ///     The ladder, in order:
    ///     <list type="number">
    ///         <item>
    ///             A <see cref="SliceRefShape.SpillRef" /> is the whole array
    ///             by definition — <c>arr</c>. Checked before the single-cell
    ///             rule so that <c>A1#</c> on a 1×1 spill still yields the
    ///             array; the rectangle is ignored (it can only ever be the
    ///             full spill).
    ///         </item>
    ///         <item>
    ///             A rectangle covering exactly one cell — <c>INDEX(arr,r1,c1)</c>,
    ///             unconditionally. Checked <em>before</em> the whole-array rule,
    ///             so a range covering one cell (<c>A2:A2</c>) takes the scalar
    ///             path even when the spill is itself 1×1 and that range is
    ///             therefore also the whole array. Excel's <c>=A1:A1</c> yields a
    ///             scalar, and a 1×1 array is not a scalar — it collapses
    ///             <c>SEQUENCE</c>/<c>REGEXEXTRACT</c> downstream.
    ///         </item>
    ///         <item>
    ///             A rectangle spanning the full spill (and more than one cell)
    ///             — <c>arr</c>.
    ///         </item>
    ///         <item>
    ///             Otherwise a band or block: each axis picks its own selector
    ///             (see <see cref="AxisSelector" />) and the two compose into at
    ///             most one <c>DROP</c> and one <c>TAKE</c>.
    ///         </item>
    ///     </list>
    ///
    ///     Because the single-cell rule is checked before the band/block rules,
    ///     <c>TAKE</c> is only ever emitted for a rectangle spanning more than
    ///     one cell — no path can hand a 1×1 array to a reference that asked
    ///     for a scalar.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="bindingName" /> is null, empty, or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The spill dimensions are not positive, or the rectangle is inverted
    ///     or falls outside the spill.
    /// </exception>
    internal static string Build(
        string bindingName,
        int spillRows,
        int spillColumns,
        int r1,
        int r2,
        int c1,
        int c2,
        SliceRefShape shape)
    {
        if (string.IsNullOrWhiteSpace(bindingName))
            throw new ArgumentException("Binding name must be non-empty.", nameof(bindingName));
        if (spillRows < 1)
            throw new ArgumentOutOfRangeException(nameof(spillRows), spillRows, "Spill rows must be 1 or more.");
        if (spillColumns < 1)
            throw new ArgumentOutOfRangeException(nameof(spillColumns), spillColumns, "Spill columns must be 1 or more.");
        if (r1 < 1 || r2 < r1 || r2 > spillRows)
            throw new ArgumentOutOfRangeException(nameof(r1), $"Rows {r1}..{r2} fall outside a spill of {spillRows} row(s).");
        if (c1 < 1 || c2 < c1 || c2 > spillColumns)
            throw new ArgumentOutOfRangeException(nameof(c1), $"Columns {c1}..{c2} fall outside a spill of {spillColumns} column(s).");

        // 1. An explicit spill reference is the whole array, whatever its size.
        if (shape == SliceRefShape.SpillRef)
            return bindingName;

        var height = r2 - r1 + 1;
        var width = c2 - c1 + 1;

        // 2. Single cell — positional, never shape-robust, and checked before
        //    the whole-array rule so a one-cell range stays a scalar.
        if (height == 1 && width == 1)
            return $"INDEX({bindingName},{r1},{c1})";

        // 3. The whole array, spanning more than one cell.
        if (height == spillRows && width == spillColumns)
            return bindingName;

        // 4. Band or block: decide each axis independently, then compose.
        AxisSelector(r1, height, spillRows, out var rowDrop, out var rowTake);
        AxisSelector(c1, width, spillColumns, out var colDrop, out var colTake);

        var target = rowDrop != 0 || colDrop != 0
            ? Render("DROP", bindingName, rowDrop, colDrop)
            : bindingName;

        return Render("TAKE", target, rowTake, colTake);
    }

    /// <summary>
    ///     The selector for one axis, given the 1-based <paramref name="start" />
    ///     of the reference on that axis, its <paramref name="len" />, and the
    ///     spill's <paramref name="total" /> extent:
    ///
    ///     <list type="bullet">
    ///         <item><c>start=1</c> and <c>len=total</c> — <em>all</em>: contributes nothing (0, 0).</item>
    ///         <item><c>start=1</c> — <c>TAKE +len</c>.</item>
    ///         <item>flush to the end — <c>TAKE -len</c>, edge-relative, no counting.</item>
    ///         <item>interior — <c>DROP start-1</c> then <c>TAKE +len</c>.</item>
    ///     </list>
    ///
    ///     A negative take composes correctly with a <c>DROP</c> on the other
    ///     axis because the axis drops are independent: <c>TAKE(DROP(arr,,1),-1,2)</c>
    ///     takes the last row of everything after the first column.
    /// </summary>
    private static void AxisSelector(int start, int len, int total, out int drop, out int take)
    {
        if (start == 1 && len == total)
        {
            drop = 0;
            take = 0;
        }
        else if (start == 1)
        {
            drop = 0;
            take = len;
        }
        else if (start + len - 1 == total)
        {
            drop = 0;
            take = -len;
        }
        else
        {
            drop = start - 1;
            take = len;
        }
    }

    /// <summary>
    ///     Renders <c>fn(inner, rowArg, colArg)</c>, omitting the arguments
    ///     for axes that contribute nothing (encoded as 0). A trailing
    ///     omission drops the argument entirely (<c>TAKE(arr,3)</c>); an
    ///     interior omission renders as a bare comma (<c>TAKE(arr,,-1)</c>).
    ///     Both arguments zero cannot arise from <see cref="Build" /> — a
    ///     <c>DROP</c> is only rendered when at least one axis drops, and by
    ///     the time a <c>TAKE</c> is rendered the whole-array rule has already
    ///     returned — but it degrades to the untouched target rather than
    ///     emitting <c>TAKE(arr)</c>.
    /// </summary>
    private static string Render(string fn, string inner, int rowArg, int colArg)
    {
        if (rowArg == 0 && colArg == 0)
            return inner;
        if (colArg == 0)
            return $"{fn}({inner},{rowArg})";
        if (rowArg == 0)
            return $"{fn}({inner},,{colArg})";
        return $"{fn}({inner},{rowArg},{colArg})";
    }
}
