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
///     PR 11 adds an optional <c>demotedCells</c> set: cells in the set
///     are visited (so they appear in the returned list and remain
///     bindings) but treated as leaves — formula nulled, precedents not
///     pushed — so any precedents reachable only via the demoted cell
///     drop as orphans, exactly mirroring the leaf-restriction effect for
///     cells the user demoted via the role toggle. Distinct from
///     <c>restrictTo</c> in two ways: (1) demotion is tracked separately
///     from <see cref="WalkOutcome.LeafRestrictedCount" /> so the dialog
///     header hint doesn't drift on every role toggle (selection
///     restriction is the anchor, role overrides are user edits); (2)
///     promotion of leaf-restricted cells (passed in as members of
///     <c>restrictTo</c> via the engine's union) overrides the
///     restriction, which the demotion path doesn't need.
/// </summary>
internal static class CellGraphWalker
{
    public static WalkOutcome Walk(CellRef sink, ICellSource source)
    {
        return Walk(sink, source, null);
    }

    public static WalkOutcome Walk(
        CellRef sink, ICellSource source, ISet<CellRef>? restrictTo)
    {
        return Walk(sink, source, restrictTo, null);
    }

    public static WalkOutcome Walk(
        CellRef sink,
        ICellSource source,
        ISet<CellRef>? restrictTo,
        ISet<CellRef>? excludedCells)
    {
        return Walk(sink, source, restrictTo, excludedCells, null);
    }

    public static WalkOutcome Walk(
        CellRef sink,
        ICellSource source,
        ISet<CellRef>? restrictTo,
        ISet<CellRef>? excludedCells,
        ISet<CellRef>? demotedCells)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

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

            // Demoted cells (user role toggle) get the same null-formula
            // treatment as leaf-restricted cells but skip the
            // LeafRestrictedCount bump — they aren't a selection
            // narrowing and shouldn't drift the dialog header. The sink
            // can't be demoted (the dialog never offers a role toggle on
            // the sink — it's the LET body, not a binding), so the
            // sink-exempt rule is implicit but we mirror it for safety.
            var isDemoted = demotedCells != null
                            && !cell.Equals(sink)
                            && demotedCells.Contains(cell);

            var cellAbove = source.GetCellAboveText(cell);
            var cellLeft = source.GetCellLeftText(cell);
            var hasSpill = source.HasSpill(cell);

            // Always probe the source for the cell's formula, even when
            // the cell is restricted or demoted — the engine uses
            // <see cref="WalkedCell.HasSourceFormula" /> to gate the role
            // toggle, and that decision needs the underlying source state
            // independent of the user's restriction/demotion choice.
            // Restricted/demoted cells then null out their walked
            // formula so the rest of the engine treats them as leaves.
            var sourceFormula = source.GetFormula(cell);
            var hasSourceFormula = sourceFormula != null;

            string? formula;
            IReadOnlyList<FormulaRef> precedents;
            if (isRestricted)
            {
                formula = null;
                precedents = Array.Empty<FormulaRef>();
                leafRestricted++;
            }
            else if (isDemoted)
            {
                formula = null;
                precedents = Array.Empty<FormulaRef>();
            }
            else
            {
                formula = sourceFormula;
                if (formula == null)
                    precedents = Array.Empty<FormulaRef>();
                else
                {
                    // Unqualified refs in this cell's formula resolve against
                    // its OWN sheet, not the sink's — otherwise crossing into
                    // Sheet1 from a sink on Sheet2 would mis-route Sheet1's
                    // internal `B1` references back to Sheet2.
                    //
                    // Strip the IsSpilled flag for cell-level precedents.
                    // /Gather has always treated A1 and A1# as the same
                    // precedent — spill info lives on
                    // <see cref="WalkedCell.HasSpill" />, not on the ref. The
                    // shared FormulaRef now carries IsSpilled (spec 0008
                    // needs it for /Refactor's distinct-binding rule), but
                    // /Gather's downstream code keys on the non-spilled
                    // anchor; normalising here keeps that contract intact.
                    // Ranges are left as-is (IsSpilled is always false on
                    // ranges).
                    precedents = NormaliseSpillFlag(
                        CellRefExtractor.Extract(formula, cell.Sheet));
                }
            }

            byRef[cell] = new WalkedCell(
                cell, formula, cellAbove, cellLeft, precedents, hasSpill, hasSourceFormula);

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
    ///     Returns a new precedent list where every spilled single-cell
    ///     FormulaRef is replaced by its non-spilled equivalent and
    ///     duplicates are collapsed in first-seen order. Range refs are
    ///     pass-through. Used so /Gather's downstream code (which keys on
    ///     non-spilled anchors) keeps working after spec 0008 split A1
    ///     and A1# into distinct FormulaRefs.
    /// </summary>
    private static IReadOnlyList<FormulaRef> NormaliseSpillFlag(
        IReadOnlyList<FormulaRef> precedents)
    {
        if (precedents.Count == 0)
            return precedents;
        var any = false;
        for (var i = 0; i < precedents.Count; i++)
            if (precedents[i].IsSpilled) { any = true; break; }
        if (!any)
            return precedents;

        var seen = new HashSet<FormulaRef>();
        var result = new List<FormulaRef>(precedents.Count);
        foreach (var p in precedents)
        {
            var normalised = p.IsSpilled ? new FormulaRef(p.Start) : p;
            if (seen.Add(normalised))
                result.Add(normalised);
        }
        return result;
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