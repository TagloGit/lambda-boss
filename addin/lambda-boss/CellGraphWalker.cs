namespace LambdaBoss;

/// <summary>
///     Walks the precedent graph rooted at a sink cell. Discovery uses
///     <see cref="ICellSource" /> for cell-shape lookups and
///     <see cref="CellRefExtractor" /> to find precedents inside formulas.
///     PR 4 introduces range-aware walking: cell precedents recurse as
///     before, range precedents are recorded on the cell but not expanded
///     into their constituent cells. The engine post-processes ranges to
///     promote each unique range to a leaf input and drop walked cells that
///     fall inside any promoted range. The returned cells are in topological
///     order (precedents before dependents) with the sink as the final
///     entry. PR 7 layers cycle detection on top.
/// </summary>
internal static class CellGraphWalker
{
    public static IReadOnlyList<WalkedCell> Walk(CellRef sink, ICellSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var byRef = new Dictionary<CellRef, WalkedCell>();
        var stack = new Stack<CellRef>();
        stack.Push(sink);

        while (stack.Count > 0)
        {
            var cell = stack.Pop();
            if (byRef.ContainsKey(cell))
                continue;

            var formula = source.GetFormula(cell);
            var cellAbove = source.GetCellAboveText(cell);
            var cellLeft = source.GetCellLeftText(cell);
            var hasSpill = source.HasSpill(cell);

            IReadOnlyList<FormulaRef> precedents;
            if (formula == null)
            {
                precedents = Array.Empty<FormulaRef>();
            }
            else
            {
                // Unqualified refs in this cell's formula resolve against
                // its OWN sheet, not the sink's — otherwise crossing into
                // Sheet1 from a sink on Sheet2 would mis-route Sheet1's
                // internal `B1` references back to Sheet2.
                precedents = CellRefExtractor.Extract(formula, cell.Sheet);
            }

            byRef[cell] = new WalkedCell(cell, formula, cellAbove, cellLeft, precedents, hasSpill);

            // Spill anchors are opaque from the walker's perspective: the
            // engine will bind them as `A1#` inputs (their formula is NOT
            // inlined as a step), so anything the formula references
            // contributes nothing to the LET via this path. Skip pushing
            // precedents — cells reached only through a spill anchor's
            // formula don't appear in the binding list. Cells reached via
            // other paths still get walked from those paths.
            if (hasSpill)
                continue;

            foreach (var p in precedents)
            {
                // Range refs aren't expanded into their constituent cells —
                // they're an opaque "block" precedent that the engine
                // promotes to a single leaf input. Cells covered by the
                // range that happen to be reached via OTHER precedents are
                // walked normally and dropped post-walk.
                if (p.IsRange)
                    continue;
                if (!byRef.ContainsKey(p.Start))
                    stack.Push(p.Start);
            }
        }

        return TopoSort(sink, byRef);
    }

    /// <summary>
    ///     Iterative DFS post-order: precedents emitted before dependents,
    ///     sink last. PR 1 assumes no cycles, so we don't track grey-state
    ///     here — that machinery lands in PR 7.
    /// </summary>
    private static List<WalkedCell> TopoSort(CellRef sink, Dictionary<CellRef, WalkedCell> byRef)
    {
        var ordered = new List<WalkedCell>(byRef.Count);
        var visited = new HashSet<CellRef>();
        var stack = new Stack<(CellRef Cell, int NextChild)>();
        stack.Push((sink, 0));

        while (stack.Count > 0)
        {
            var (cell, nextChild) = stack.Pop();
            var node = byRef[cell];
            if (nextChild < node.Precedents.Count)
            {
                stack.Push((cell, nextChild + 1));
                var pre = node.Precedents[nextChild];
                if (pre.IsRange)
                    continue;
                var child = pre.Start;
                if (!visited.Contains(child) && byRef.ContainsKey(child))
                    stack.Push((child, 0));
            }
            else if (visited.Add(cell))
            {
                ordered.Add(node);
            }
        }

        return ordered;
    }
}
