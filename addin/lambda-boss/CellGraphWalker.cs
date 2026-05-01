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
///     entry. PR 7 adds DFS-coloured cycle detection during the topological
///     pass — on a back-edge to a grey ancestor the walker returns a
///     <see cref="WalkOutcome" /> carrying the cycle's cells in path order
///     so the engine can refuse with a clear diagnostic instead of
///     spinning forever. PR 9 adds an optional <c>restrictTo</c> set: when
///     non-null, any non-sink cell outside the set is treated as a leaf
///     (formula <c>null</c>, no precedents) so its sub-tree is dropped
///     from the walk and its cell-ref appears as an input on the boundary.
///     The sink itself is never restricted — gathering always processes the
///     sink's formula. Spill anchors keep their <see cref="ICellSource.HasSpill" />
///     flag even when leaf-restricted so the engine still emits <c>A1#</c>
///     on the boundary input, preserving the array semantics of the
///     dropped sub-tree.
///     PR 10 adds an optional <c>excludedCells</c> set: cells in the set
///     are not pushed onto the walk stack and never appear in the returned
///     <see cref="WalkedCell" /> list. The sink itself is exempt (the
///     dialog never lets the user exclude the sink). An excluded cell's
///     ref still appears in any calling step's
///     <see cref="WalkedCell.Precedents" />, so the engine's role
///     classification can keep that step a step (its formula still has
///     content), and the rewriter leaves the ref as a literal cell address
///     because the lookup dictionary won't contain it.
/// </summary>
internal static class CellGraphWalker
{
    public static WalkOutcome Walk(CellRef sink, ICellSource source)
    {
        return Walk(sink, source, null);
    }

    public static WalkOutcome Walk(
        CellRef sink, ICellSource source, IReadOnlySet<CellRef>? restrictTo)
    {
        return Walk(sink, source, restrictTo, null);
    }

    public static WalkOutcome Walk(
        CellRef sink,
        ICellSource source,
        IReadOnlySet<CellRef>? restrictTo,
        IReadOnlySet<CellRef>? excludedCells)
    {
        ArgumentNullException.ThrowIfNull(source);

        var byRef = new Dictionary<CellRef, WalkedCell>();
        var leafRestricted = 0;
        var stack = new Stack<CellRef>();
        stack.Push(sink);

        while (stack.Count > 0)
        {
            var cell = stack.Pop();
            if (byRef.ContainsKey(cell))
                continue;

            // Cell is leaf-restricted when a restriction set is given and
            // this cell isn't in it. The sink is exempt — gather always
            // processes its formula. Cell-above/left labels and HasSpill
            // are still queried so the boundary input's binding name and
            // `#`-suffix come out the same as a natural leaf would.
            var isRestricted = restrictTo != null
                               && !cell.Equals(sink)
                               && !restrictTo.Contains(cell);

            var cellAbove = source.GetCellAboveText(cell);
            var cellLeft = source.GetCellLeftText(cell);
            var hasSpill = source.HasSpill(cell);

            string? formula;
            IReadOnlyList<FormulaRef> precedents;
            if (isRestricted)
            {
                formula = null;
                precedents = Array.Empty<FormulaRef>();
                leafRestricted++;
            }
            else
            {
                formula = source.GetFormula(cell);
                if (formula == null)
                    precedents = Array.Empty<FormulaRef>();
                else
                {
                    // Unqualified refs in this cell's formula resolve against
                    // its OWN sheet, not the sink's — otherwise crossing into
                    // Sheet1 from a sink on Sheet2 would mis-route Sheet1's
                    // internal `B1` references back to Sheet2.
                    precedents = CellRefExtractor.Extract(formula, cell.Sheet);
                }
            }

            byRef[cell] = new WalkedCell(cell, formula, cellAbove, cellLeft, precedents, hasSpill);

            foreach (var p in precedents)
            {
                // Range refs aren't expanded into their constituent cells —
                // they're an opaque "block" precedent that the engine
                // promotes to a single leaf input. Cells covered by the
                // range that happen to be reached via OTHER precedents are
                // walked normally and dropped post-walk.
                if (p.IsRange)
                    continue;
                // Excluded precedents don't get walked — that's how the
                // user's "Include" toggle drops a cell and its
                // upstream-only-reachable sub-tree. The ref still lives in
                // this cell's Precedents list (we just don't push it), so
                // the engine sees the precedent for role classification
                // and the rewriter passes the literal cell-ref through.
                if (excludedCells != null && excludedCells.Contains(p.Start))
                    continue;
                if (!byRef.ContainsKey(p.Start))
                    stack.Push(p.Start);
            }
        }

        return CycleAwareTopoSort(sink, byRef, leafRestricted);
    }

    /// <summary>
    ///     Iterative DFS post-order with grey/black colouring. A child
    ///     already on the DFS path (grey) means a back-edge — i.e. a
    ///     cycle. The cycle's cells are extracted from the path stack in
    ///     topological order (the back-edge target first, then each
    ///     ancestor up to the cell that closed the loop) and surfaced via
    ///     <see cref="WalkOutcome" />. Discovery (Walk's first phase)
    ///     terminates fine in the presence of cycles thanks to the
    ///     <c>byRef.ContainsKey</c> guard, but topo sort would otherwise
    ///     spin forever pushing back and forth between the cycle's cells.
    /// </summary>
    private static WalkOutcome CycleAwareTopoSort(
        CellRef sink, Dictionary<CellRef, WalkedCell> byRef, int leafRestrictedCount)
    {
        var ordered = new List<WalkedCell>(byRef.Count);
        var visited = new HashSet<CellRef>();
        var onPath = new HashSet<CellRef>();
        var stack = new Stack<(CellRef Cell, int NextChild)>();
        stack.Push((sink, 0));
        onPath.Add(sink);

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
                if (!byRef.ContainsKey(child))
                    continue;
                if (onPath.Contains(child))
                    return WalkOutcome.WithCycle(ExtractCycle(stack, child));
                if (!visited.Contains(child))
                {
                    stack.Push((child, 0));
                    onPath.Add(child);
                }
            }
            else if (visited.Add(cell))
            {
                onPath.Remove(cell);
                ordered.Add(node);
            }
        }

        return WalkOutcome.Success(ordered, leafRestrictedCount);
    }

    /// <summary>
    ///     Builds the cycle's cell list from the DFS path stack at the
    ///     moment a back-edge is detected. The stack iterates top-to-
    ///     bottom and at this point holds the current cell at the top
    ///     followed by each ancestor down to the sink. We collect from
    ///     top (current cell) until we hit <paramref name="target" /> (the
    ///     back-edge destination) and reverse so the result reads from
    ///     <paramref name="target" /> through to the cell that closed the
    ///     loop.
    /// </summary>
    private static List<CellRef> ExtractCycle(
        Stack<(CellRef Cell, int NextChild)> stack, CellRef target)
    {
        var path = new List<CellRef>();
        foreach (var (c, _) in stack)
        {
            path.Add(c);
            if (c.Equals(target))
                break;
        }

        path.Reverse();
        return path;
    }
}

/// <summary>
///     Result of CellGraphWalker.Walk: either a topo-ordered
///     list of cells or a cycle's cell list. Exactly one of
///     <see cref="Cells" /> and <see cref="Cycle" /> is non-null.
///     <see cref="LeafRestrictedCount" /> reports how many cells were
///     leaf-restricted by the walker's <c>restrictTo</c> set (PR 9) — zero
///     for unrestricted walks. The engine subtracts this from
///     <see cref="Cells" />'s count to derive the "M" in the
///     <c>Walking M of N cells from &lt;addr&gt;</c> header.
/// </summary>
internal readonly struct WalkOutcome
{
    private WalkOutcome(
        IReadOnlyList<WalkedCell>? cells,
        IReadOnlyList<CellRef>? cycle,
        int leafRestrictedCount)
    {
        Cells = cells;
        Cycle = cycle;
        LeafRestrictedCount = leafRestrictedCount;
    }

    public IReadOnlyList<WalkedCell>? Cells { get; }
    public IReadOnlyList<CellRef>? Cycle { get; }
    public int LeafRestrictedCount { get; }

    public bool IsCycle => Cycle != null;

    public static WalkOutcome Success(IReadOnlyList<WalkedCell> cells, int leafRestrictedCount = 0)
    {
        return new WalkOutcome(cells, null, leafRestrictedCount);
    }

    public static WalkOutcome WithCycle(IReadOnlyList<CellRef> cycle)
    {
        return new WalkOutcome(null, cycle, 0);
    }
}