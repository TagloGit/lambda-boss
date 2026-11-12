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
///     sink's formula. Spill anchors keep their <see cref="WalkedCell.HasSpill" />
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
        return Walk(sink, source, restrictTo, excludedCells, demotedCells, false, null);
    }

    /// <summary>
    ///     Spec 0019 §1. With <paramref name="walkFormulaRanges" /> on, a range
    ///     over at least one formula cell (no cell in a spill, at most
    ///     <see cref="MaxWalkedRangeCells" /> cells, not in
    ///     <paramref name="excludedRanges" />) is not opaque: each of its cells is
    ///     walked like a single-cell precedent, the topological pass follows an edge
    ///     to every one, and the range is reported in
    ///     <see cref="WalkOutcome.ExpandedRanges" /> so the engine can stack the
    ///     cells' bindings back into the range's shape.
    /// </summary>
    public static WalkOutcome Walk(
        CellRef sink,
        ICellSource source,
        ISet<CellRef>? restrictTo,
        ISet<CellRef>? excludedCells,
        ISet<CellRef>? demotedCells,
        bool walkFormulaRanges,
        ISet<FormulaRef>? excludedRanges)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (!walkFormulaRanges)
            return WalkOnce(sink, source, restrictTo, excludedCells, demotedCells, false,
                excludedRanges, null, out _, out _);

        // Spec 0019: an expanded range is vetoed (and the walk redone without it, so no
        // orphan cell rows are left behind) when another reader needs it as a reference
        // (COUNTIF, OFFSET...) or when it overlaps a range that stays a promoted input.
        var vetoed = new HashSet<FormulaRef>();
        while (true)
        {
            var outcome = WalkOnce(sink, source, restrictTo, excludedCells, demotedCells, true,
                excludedRanges, vetoed, out var needsReference, out var opaque);
            if (outcome.IsCycle)
                return outcome;

            var added = false;
            foreach (var range in outcome.ExpandedRanges.Keys)
            {
                var veto = needsReference.Contains(range)
                           || opaque.Any(o => !o.Equals(range) && Overlaps(range, o));
                if (veto && vetoed.Add(range))
                    added = true;
            }

            if (!added)
                return outcome;
        }
    }

    private static bool Overlaps(FormulaRef a, FormulaRef b)
    {
        if (!string.Equals(a.Sheet, b.Sheet, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(a.ExternalWorkbook, b.ExternalWorkbook, StringComparison.OrdinalIgnoreCase))
            return false;
        var ae = a.End ?? a.Start;
        var be = b.End ?? b.Start;
        return a.Start.Row <= be.Row && b.Start.Row <= ae.Row
                                     && a.Start.Column <= be.Column && b.Start.Column <= ae.Column;
    }

    // Functions whose argument must stay a reference: a stack of values would change or break them.
    private static readonly System.Text.RegularExpressions.Regex NeedsReferencePattern = new(
        @"\b(?:COUNTIFS?|SUMIFS?|AVERAGEIFS?|MAXIFS|MINIFS|RANK(?:\.EQ|\.AVG)?|OFFSET|CELL|ROWS?|COLUMNS?|SUBTOTAL|AGGREGATE|INDIRECT|ISREF|AREAS)\s*\(",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static WalkOutcome WalkOnce(
        CellRef sink,
        ICellSource source,
        ISet<CellRef>? restrictTo,
        ISet<CellRef>? excludedCells,
        ISet<CellRef>? demotedCells,
        bool walkFormulaRanges,
        ISet<FormulaRef>? excludedRanges,
        ISet<FormulaRef>? vetoed,
        out HashSet<FormulaRef> needsReference,
        out HashSet<FormulaRef> opaque)
    {
        needsReference = new HashSet<FormulaRef>();
        opaque = new HashSet<FormulaRef>();
        var expanded = new Dictionary<FormulaRef, IReadOnlyList<CellRef>>();
        var expandMemo = new Dictionary<FormulaRef, IReadOnlyList<CellRef>?>();

        var byRef = new Dictionary<CellRef, WalkedCell>();
        // Spec 0010: every cell that turned out to live inside a spill,
        // mapped to that spill's anchor. Discovery pushes the anchor rather
        // than the referenced cell, so the topological pass has to follow
        // the same redirection or it would never reach the anchor from the
        // step that references a child.
        var anchorByCell = new Dictionary<CellRef, CellRef>();
        // Spec 0010 PR 4: the range precedents that resolved through a spill
        // and therefore carry a real graph edge to its anchor. Ranges that
        // straddle a spill boundary — or touch no spill at all — stay opaque
        // block precedents and are absent here, so the topological pass can
        // replay discovery's decision instead of re-deriving it.
        var slicedRanges = new HashSet<FormulaRef>();
        // Per-walk memo over ICellSource.GetSpill. The live source pays two
        // COM property reads per probe (~1.6 ms out-of-process), and the
        // precedent loop probes once per precedent *occurrence* — a cell
        // referenced by three steps was being read three times, on spill-free
        // sheets as much as spilling ones. The sheet can't change mid-walk,
        // so caching here is safe; the memo dies with the Walk call, so a
        // Recompute after an edit still sees fresh geometry.
        var spillByCell = new Dictionary<CellRef, SpillInfo?>();
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
            // processes its formula. Cell-above/left labels and spill geometry
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

            // The label search of the gather-next naming reads its own cells.
            var cellAbove = walkFormulaRanges ? null : source.GetCellAboveText(cell);
            var cellLeft = walkFormulaRanges ? null : source.GetCellLeftText(cell);
            // Anchor-only flag: GetSpill is non-null for spill children too,
            // and only the anchor's RHS may carry the `#` suffix (`B1#` on a
            // child is #REF!). Children never reach this point anyway — the
            // precedent loop below redirects them to their anchor — but the
            // flag stays anchor-only so the invariant is local.
            var spill = GetSpillMemo(cell, source, spillByCell);
            var hasSpill = spill != null && spill.Anchor == cell;

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
                    // Spec 0010 removed the spill-flag normalisation that used
                    // to collapse `A1#` onto `A1`: the two map to different
                    // replacements (`arr` versus `INDEX(arr,1,1)`), so they
                    // must stay distinct precedents with distinct lookup
                    // entries.
                    precedents = CellRefExtractor.Extract(formula, cell.Sheet);
                }
            }

            byRef[cell] = new WalkedCell(
                cell, formula, cellAbove, cellLeft, precedents, hasSpill, hasSourceFormula);

            foreach (var p in precedents)
            {
                if (walkFormulaRanges && p.IsRange)
                {
                    IReadOnlyList<CellRef>? rangeCells = null;
                    if (formula != null && NeedsReferencePattern.IsMatch(formula))
                        needsReference.Add(p);
                    else
                        rangeCells = TryExpandRange(
                            p, source, spillByCell, expandMemo, excludedRanges, vetoed, restrictTo);
                    if (rangeCells == null)
                        opaque.Add(p);
                    if (rangeCells != null)
                    {
                        expanded[p] = rangeCells;
                        foreach (var rangeCell in rangeCells)
                        {
                            if (excludedCells != null && excludedCells.Contains(rangeCell))
                                continue;
                            if (!byRef.ContainsKey(rangeCell))
                                stack.Push(rangeCell);
                        }

                        continue;
                    }
                }

                var target = ResolveWalkTarget(p, source, anchorByCell, slicedRanges, spillByCell);
                if (target == null)
                    continue;
                // Excluded precedents don't get walked — that's how the
                // user's "Include" toggle drops a cell and its
                // upstream-only-reachable sub-tree. The ref still lives in
                // this cell's Precedents list (we just don't push it), so
                // the engine sees the precedent for role classification
                // and the rewriter passes the literal cell-ref through.
                if (excludedCells != null && excludedCells.Contains(target))
                    continue;
                if (!byRef.ContainsKey(target))
                    stack.Push(target);
            }
        }

        return CycleAwareTopoSort(sink, byRef, anchorByCell, slicedRanges, expanded, leafRestricted);
    }

    /// <summary>The most cells a range may have and still be walked rather than promoted.</summary>
    internal const int MaxWalkedRangeCells = 64;

    /// <summary>
    ///     The cells of a range to walk (row-major), or null when it stays an opaque
    ///     range: excluded, external, a single cell, over
    ///     <see cref="MaxWalkedRangeCells" />, touching a spill, or holding no
    ///     formula (all-literal data stays a range input). Memoised per walk.
    /// </summary>
    private static IReadOnlyList<CellRef>? TryExpandRange(
        FormulaRef p,
        ICellSource source,
        Dictionary<CellRef, SpillInfo?> spillByCell,
        Dictionary<FormulaRef, IReadOnlyList<CellRef>?> memo,
        ISet<FormulaRef>? excludedRanges,
        ISet<FormulaRef>? vetoed,
        ISet<CellRef>? restrictTo)
    {
        if (memo.TryGetValue(p, out var cached))
            return cached;

        var cells = ExpandRangeCells(p, source, spillByCell, excludedRanges, vetoed, restrictTo);
        memo[p] = cells;
        return cells;
    }

    private static IReadOnlyList<CellRef>? ExpandRangeCells(
        FormulaRef p,
        ICellSource source,
        Dictionary<CellRef, SpillInfo?> spillByCell,
        ISet<FormulaRef>? excludedRanges,
        ISet<FormulaRef>? vetoed,
        ISet<CellRef>? restrictTo)
    {
        if (!p.IsRange || p.IsSpilled || p.IsExternal)
            return null;
        if (excludedRanges != null && excludedRanges.Contains(p))
            return null;
        if (vetoed != null && vetoed.Contains(p))
            return null;

        var start = p.Start;
        var end = p.End!;
        var rows = end.Row - start.Row + 1;
        var cols = end.Column - start.Column + 1;
        if (rows < 1 || cols < 1 || rows * cols < 2 || rows * cols > MaxWalkedRangeCells)
            return null;

        var cells = new List<CellRef>(rows * cols);
        var anyFormula = false;
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            var cell = new CellRef(start.Sheet, start.Column + c, start.Row + r);
            if (GetSpillMemo(cell, source, spillByCell) != null)
                return null;
            if (source.GetFormula(cell) != null)
                anyFormula = true;
            else if (source.ReadCell(cell).IsEmpty)
                return null; // a blank cell would change what the range means
            cells.Add(cell);
        }

        // A selection-restricted walk only expands a range it reaches into.
        if (restrictTo != null && !cells.Any(restrictTo.Contains))
            return null;

        return anyFormula ? cells : null;
    }

    /// <summary>
    ///     The cell a precedent should be walked into, or null when the
    ///     precedent isn't walkable at all.
    ///
    ///     Range refs aren't expanded into their constituent cells — they're
    ///     an opaque "block" precedent that the engine promotes to a single
    ///     leaf input, so they return null. The exception is a range that lies
    ///     <em>wholly inside</em> a spill (spec 0010, PR 4): the engine turns
    ///     it into a slice of the anchor's array — a degenerate single-cell
    ///     range (<c>A2:A2</c>) into <c>INDEX</c>, a sub-block into
    ///     <c>TAKE</c>/<c>DROP</c> — so it has to reach the anchor like a bare
    ///     cell ref would. A range with one endpoint outside the spill
    ///     straddles its boundary, is inexpressible as a slice, and stays
    ///     opaque so the engine promotes it as today.
    ///
    ///     Any reference landing inside a spill resolves to that spill's
    ///     <em>anchor</em> — the walker recurses into the anchor, never a
    ///     child (a child has no formula of its own, so walking it would
    ///     produce a stray leaf input with a <c>#</c>-suffixed RHS that
    ///     evaluates to <c>#REF!</c>). The redirection is also spec 0010's
    ///     <em>anchor discovery</em>: an anchor that nothing references
    ///     directly is still pulled into the walk, because otherwise there
    ///     would be no array for the slice to index into.
    ///
    ///     Every range ref that resolves through a spill is recorded in
    ///     <paramref name="slicedRanges" />, which is what the topological
    ///     pass keys on to decide whether a range precedent carries a graph
    ///     edge — so the two passes cannot drift.
    /// </summary>
    private static CellRef? ResolveWalkTarget(
        FormulaRef p,
        ICellSource source,
        Dictionary<CellRef, CellRef> anchorByCell,
        HashSet<FormulaRef> slicedRanges,
        Dictionary<CellRef, SpillInfo?> spillByCell)
    {
        var spill = GetSpillMemo(p.Start, source, spillByCell);
        if (spill == null)
            return p.IsRange ? null : p.Start;

        // Straddling range: one endpoint outside the spill. Not a slice, so
        // it stays an opaque range precedent and promotes as today.
        if (p.IsRange && !spill.Contains(p.End!))
            return null;

        if (p.IsRange)
            slicedRanges.Add(p);
        if (!spill.Anchor.Equals(p.Start))
            anchorByCell[p.Start] = spill.Anchor;
        return spill.Anchor;
    }

    /// <summary>
    ///     <see cref="ICellSource.GetSpill" /> behind a per-walk memo. A null
    ///     result (the common case — the cell isn't in a spill) is cached too,
    ///     so a non-spilling cell referenced by many steps costs one probe per
    ///     walk rather than one per reference.
    /// </summary>
    private static SpillInfo? GetSpillMemo(
        CellRef cell, ICellSource source, Dictionary<CellRef, SpillInfo?> spillByCell)
    {
        if (spillByCell.TryGetValue(cell, out var cached))
            return cached;

        var spill = source.GetSpill(cell);
        spillByCell[cell] = spill;
        return spill;
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
        CellRef sink,
        Dictionary<CellRef, WalkedCell> byRef,
        Dictionary<CellRef, CellRef> anchorByCell,
        HashSet<FormulaRef> slicedRanges,
        Dictionary<FormulaRef, IReadOnlyList<CellRef>> expanded,
        int leafRestrictedCount)
    {
        // An expanded range (spec 0019) contributes one edge per cell. Cached,
        // because a cell is re-popped once per child.
        var edgesByCell = new Dictionary<CellRef, IReadOnlyList<FormulaRef>>();
        IReadOnlyList<FormulaRef> EdgesOf(WalkedCell node)
        {
            if (expanded.Count == 0)
                return node.Precedents;
            if (edgesByCell.TryGetValue(node.Ref, out var cachedEdges))
                return cachedEdges;
            var flat = new List<FormulaRef>();
            foreach (var pre in node.Precedents)
            {
                if (pre.IsRange && expanded.TryGetValue(pre, out var cells))
                    foreach (var c in cells)
                        flat.Add(new FormulaRef(c));
                else
                    flat.Add(pre);
            }

            edgesByCell[node.Ref] = flat;
            return flat;
        }

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
            var edges = EdgesOf(node);
            if (nextChild < edges.Count)
            {
                stack.Push((cell, nextChild + 1));
                var pre = edges[nextChild];
                // A range precedent carries a graph edge when discovery
                // resolved it through a spill (replayed from the recorded
                // set, so the geometry is never re-derived and the two passes
                // cannot disagree about which ranges are slices) — or when it
                // is a degenerate one-cell range, which the pass has always
                // followed. That second clause does NOT mirror
                // <see cref="ResolveWalkTarget" />, which leaves a degenerate
                // range outside any spill unwalked; the asymmetry is
                // deliberate and load-bearing. It is the only thing that
                // catches a circular reference expressed as a self-covering
                // degenerate range (`B1 = SUM(C1:C1)+1` with sink
                // `C1 = B1*2`), and dropping it would trade a correct refusal
                // for a silently self-referential LET.
                if (pre.IsRange
                    && !pre.Start.Equals(pre.End)
                    && !slicedRanges.Contains(pre))
                    continue;
                // Follow the same child→anchor redirection discovery used,
                // otherwise a step that only references a spill child would
                // have no edge to the anchor and the anchor would never be
                // emitted (nor its position in the topological order fixed
                // ahead of the step that slices it).
                var child = pre.Start;
                if (anchorByCell.TryGetValue(child, out var resolvedAnchor))
                    child = resolvedAnchor;
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

        return WalkOutcome.Success(ordered, leafRestrictedCount, expanded);
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
    private static readonly IReadOnlyDictionary<FormulaRef, IReadOnlyList<CellRef>> NoExpandedRanges =
        new Dictionary<FormulaRef, IReadOnlyList<CellRef>>();

    private WalkOutcome(
        IReadOnlyList<WalkedCell>? cells,
        IReadOnlyList<CellRef>? cycle,
        int leafRestrictedCount,
        IReadOnlyDictionary<FormulaRef, IReadOnlyList<CellRef>>? expandedRanges = null)
    {
        Cells = cells;
        Cycle = cycle;
        LeafRestrictedCount = leafRestrictedCount;
        ExpandedRanges = expandedRanges ?? NoExpandedRanges;
    }

    /// <summary>
    ///     Spec 0019: the ranges the walk expanded into their cells (row-major),
    ///     keyed by the range as written. Always empty unless the walk was asked to
    ///     walk formula ranges.
    /// </summary>
    public IReadOnlyDictionary<FormulaRef, IReadOnlyList<CellRef>> ExpandedRanges { get; }

    public IReadOnlyList<WalkedCell>? Cells { get; }
    public IReadOnlyList<CellRef>? Cycle { get; }
    public int LeafRestrictedCount { get; }

    public bool IsCycle => Cycle != null;

    public static WalkOutcome Success(
        IReadOnlyList<WalkedCell> cells,
        int leafRestrictedCount = 0,
        IReadOnlyDictionary<FormulaRef, IReadOnlyList<CellRef>>? expandedRanges = null)
    {
        return new WalkOutcome(cells, null, leafRestrictedCount, expandedRanges);
    }

    public static WalkOutcome WithCycle(IReadOnlyList<CellRef> cycle)
    {
        return new WalkOutcome(null, cycle, 0);
    }
}