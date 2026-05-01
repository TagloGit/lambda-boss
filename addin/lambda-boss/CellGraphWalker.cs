namespace LambdaBoss;

/// <summary>
///     Walks the precedent graph rooted at a sink cell. Discovery uses
///     <see cref="ICellSource" /> for cell-shape lookups and
///     <see cref="CellRefExtractor" /> to find precedents inside formulas.
///     PR 3 widens scope to all sheets in the active workbook: cross-sheet
///     refs are walked normally; external-workbook refs surface as leaves
///     because <see cref="ICellSource.GetFormula" /> returns null for them.
///     The returned cells are in topological order (precedents before
///     dependents) with the sink as the final entry. PR 7 layers cycle
///     detection on top.
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

            IReadOnlyList<CellRef> precedents;
            if (formula == null)
            {
                precedents = Array.Empty<CellRef>();
            }
            else
            {
                // Unqualified refs in this cell's formula resolve against
                // its OWN sheet, not the sink's — otherwise crossing into
                // Sheet1 from a sink on Sheet2 would mis-route Sheet1's
                // internal `B1` references back to Sheet2.
                precedents = CellRefExtractor.Extract(formula, cell.Sheet);
            }

            byRef[cell] = new WalkedCell(cell, formula, cellAbove, cellLeft, precedents);

            foreach (var p in precedents)
            {
                if (!byRef.ContainsKey(p))
                    stack.Push(p);
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
                var child = node.Precedents[nextChild];
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
