using System.Text;
using System.Text.RegularExpressions;

using LambdaBoss.Commands;

namespace LambdaBoss;

/// <summary>
///     The top-level Gather entry point. Walks the precedent graph, names
///     each cell and range, classifies each as input or step, rewrites
///     step formulas to refer to bindings, and emits a single
///     <c>=LET(...)</c> formula equivalent to the sink-rooted calculation
///     graph. PR 4 promotes range refs to single-input bindings: each
///     unique range encountered while walking becomes one leaf input, and
///     any walked cell that falls inside a promoted range is dropped from
///     the bindings list. PR 6 expands a step whose formula is itself a
///     <c>=LET(...)</c> by splicing the inner LET's bindings inline ahead
///     of the step row; inner names that collide with already-assigned
///     names are auto-suffixed (<c>x</c> → <c>x_2</c>) silently.
/// </summary>
public static class GatherEngine
{
    /// <summary>
    ///     Runs the gather over <paramref name="sink" />. Returns null if
    ///     the sink has no formula — the caller (slash command) treats this
    ///     as a silent no-op rather than an error. Cycles in the precedent
    ///     graph (PR 7) surface as a non-null result with
    ///     <see cref="GatherResult.Diagnostic" /> set; bindings and
    ///     synthesised LET are empty in that case.
    /// </summary>
    public static GatherResult? Gather(CellRef sink, ICellSource source)
    {
        return Gather(sink, new[] { sink }, source);
    }

    /// <summary>
    ///     Selection-aware overload. Spec 0010 normalises the selection
    ///     first: any spill child in it maps to its anchor, so a drag over a
    ///     spill range reads as one selected calculation. A sink that is
    ///     itself a spill child refuses outright with
    ///     <see cref="GatherDiagnosticKind.SpillChildSink" />.
    ///     When <paramref name="selection" /> then still
    ///     contains more than one cell, the engine checks the
    ///     multi-sink rule (PR 7): if 2+ selected cells have no in-scope
    ///     dependent (i.e. aren't transitively referenced by another
    ///     selected cell), the engine refuses with a
    ///     <see cref="GatherDiagnosticKind.MultipleSinks" /> diagnostic.
    ///     Cycle detection runs whether or not the selection is multi-cell;
    ///     a cycle anywhere in the walked graph surfaces as
    ///     <see cref="GatherDiagnosticKind.Cycle" />.
    ///     PR 9 adds selection-restricted walking: when the multi-selection
    ///     covers the active cell plus others, the walker leaf-restricts
    ///     any precedent that isn't in the selection — its sub-tree drops
    ///     out of the LET and its cell-ref appears as an input on the
    ///     boundary. The result's <see cref="GatherResult.WalkedCount" />
    ///     and <see cref="GatherResult.FreeWalkCount" /> let the dialog
    ///     render the header hint (free: <c>Walking N cells from &lt;addr&gt;</c>;
    ///     restricted: <c>Walking M of N cells from &lt;addr&gt; — restricted by selection</c>).
    /// </summary>
    public static GatherResult? Gather(CellRef sink, IReadOnlyList<CellRef> selection, ICellSource source)
    {
        return GatherInternal(sink, selection, source, excluded: null);
    }

    /// <summary>
    ///     Re-runs the gather with the dialog's current row state. Each
    ///     <see cref="RowState" /> with <see cref="RowState.Include" /> =
    ///     false drops its <see cref="RowState.Source" /> from the LET (PR
    ///     10): the walker treats the cell as if it didn't exist, so any
    ///     precedents reachable only via that cell drop too. Range
    ///     exclusions stop the range from being promoted to a binding (the
    ///     literal range stays in the calling step's formula). Each row's
    ///     <see cref="RowState.RoleOverride" /> (PR 11) flips the
    ///     classification: a step demoted to an input drops out of the
    ///     walk's recursion (its precedents only stay if reached via
    ///     another path); an input promoted to a step bakes the cell's
    ///     formula into the binding RHS and walks any cell-refs that
    ///     formula carries (including ones leaf-restricted by the original
    ///     selection — promotion overrides the restriction). Skips the
    ///     multi-sink and pure-LAMBDA-call diagnostic checks — those
    ///     gated the initial
    ///     <see cref="Gather(CellRef, IReadOnlyList{CellRef}, ICellSource)" />
    ///     call so the dialog wouldn't be open if either fired; cycle
    ///     detection still runs (exclusion only removes nodes, but the
    ///     walker's cycle check is cheap and defensive).
    /// </summary>
    public static GatherResult? Recompute(
        CellRef sink,
        IReadOnlyList<CellRef> selection,
        ICellSource source,
        IReadOnlyList<RowState> rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        var excluded = new HashSet<FormulaRef>();
        Dictionary<FormulaRef, BindingRole>? roleOverrides = null;
        Dictionary<FormulaRef, string>? nameOverrides = null;
        foreach (var r in rows)
        {
            if (!r.Include)
                excluded.Add(r.Source);
            if (r.RoleOverride.HasValue)
            {
                roleOverrides ??= new Dictionary<FormulaRef, BindingRole>();
                roleOverrides[r.Source] = r.RoleOverride.Value;
            }
            if (r.NameOverride is { Length: > 0 } nameOverride)
            {
                nameOverrides ??= new Dictionary<FormulaRef, string>();
                nameOverrides[r.Source] = nameOverride;
            }
        }
        return GatherInternal(sink, selection, source, excluded, roleOverrides, nameOverrides);
    }

    private static GatherResult? GatherInternal(
        CellRef sink,
        IReadOnlyList<CellRef> selection,
        ICellSource source,
        ISet<FormulaRef>? excluded,
        IReadOnlyDictionary<FormulaRef, BindingRole>? roleOverrides = null,
        IReadOnlyDictionary<FormulaRef, string>? nameOverrides = null)
    {
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        if (selection is null) throw new ArgumentNullException(nameof(selection));
        if (source is null) throw new ArgumentNullException(nameof(source));

        // Spill-child sink (spec 0010). A child has no formula of its own,
        // so without this check it would fall straight through the
        // no-formula early return below as a silent no-op — indistinguishable
        // from /Gather being broken. Checked ahead of GetFormula for exactly
        // that reason, and outside the `excluded == null` diagnostic gate
        // below because the sink can't change between Gather and Recompute
        // (so this is unreachable on the Recompute path, and cheap and
        // defensive if it ever isn't).
        var sinkSpill = source.GetSpill(sink);
        if (sinkSpill != null && !sinkSpill.Anchor.Equals(sink))
            return RefusedWithSpillChildSink(sink, sinkSpill, source);

        var sinkFormula = source.GetFormula(sink);
        if (sinkFormula == null)
            return null;

        // Selection normalisation (spec 0010). Every spill child in the
        // selection maps to its anchor before the multi-sink check runs and
        // before restrictTo is built, so dragging across a spill range
        // selects the calculation that produced it rather than its output
        // cells.
        var effectiveSelection = NormaliseSelection(selection, source);

        // Split exclusion set by ref shape: cells get propagated to the
        // walker (which skips pushing them onto the stack), ranges stay
        // here and gate the engine's range-promotion + coverage logic.
        // The sink itself is exempt — the dialog never offers an Include
        // checkbox on the sink (it's the LET body, not a binding row), so
        // a defensive Remove keeps malformed inputs from breaking gather.
        HashSet<CellRef>? excludedCells = null;
        HashSet<FormulaRef>? excludedRanges = null;
        if (excluded != null && excluded.Count > 0)
        {
            excludedCells = new HashSet<CellRef>();
            excludedRanges = new HashSet<FormulaRef>();
            foreach (var fr in excluded)
            {
                if (fr.IsRange)
                    excludedRanges.Add(fr);
                else if (!fr.Start.Equals(sink))
                    excludedCells.Add(fr.Start);
            }
        }

        // PR 11: split role overrides into walker-facing cell sets.
        // Demoted cells need the walker to stop recursing through them
        // so their upstream-only-reachable precedents drop. Promoted
        // cells need the walker to load their formula even when the
        // selection would have leaf-restricted them — the engine
        // unions promoted cells into restrictTo below to override that.
        // Range refs and the sink itself ignore overrides; excluded
        // cells already drop entirely so any override on them is moot
        // (we still record them so ClassifyRole sees consistent state
        // if the engine ever consults the override map for a cell that
        // slipped through, though none currently does).
        HashSet<CellRef>? demotedCells = null;
        HashSet<CellRef>? promotedCells = null;
        if (roleOverrides != null && roleOverrides.Count > 0)
        {
            foreach (var (fr, role) in roleOverrides)
            {
                if (fr.IsRange) continue;
                if (fr.Start.Equals(sink)) continue;
                if (role == BindingRole.Input)
                {
                    demotedCells ??= new HashSet<CellRef>();
                    demotedCells.Add(fr.Start);
                }
                else if (role == BindingRole.Step)
                {
                    promotedCells ??= new HashSet<CellRef>();
                    promotedCells.Add(fr.Start);
                }
            }
        }

        // Diagnostic checks (PR 7/8) only gate the initial Gather. On
        // Recompute the dialog wouldn't be open if either the multi-sink
        // or pure-LAMBDA-call rule had fired, and exclusion can't
        // introduce them: dropping a cell can't turn a single sink into
        // multiple sinks, and the sink's formula text doesn't change.
        if (excluded == null)
        {
            // Pure-LAMBDA-call sink check (PR 8). A formula like
            // `=Foo(A1, B1)` where Foo is a registered LAMBDA can't be
            // gathered — the cell already IS a LAMBDA invocation, so the
            // author should expand it via /EditLambda first and then re-run
            // /Gather on the resulting LET. TryParseLambdaCall returns
            // non-null only when the whole formula is a single call (modulo
            // trailing whitespace), so wrapped calls like `=Foo(A1) + 1`
            // slip through and walk normally. The IsLambdaName check
            // distinguishes a registered LAMBDA from a built-in like
            // `=SUM(A1, B1)` — built-ins aren't workbook names so they
            // also walk normally.
            var lambdaCallDiagnostic = CheckLambdaCallSink(sink, sinkFormula, source);
            if (lambdaCallDiagnostic != null)
                return lambdaCallDiagnostic;

            // Multi-sink check uses the cycle-aware walker on each selected
            // cell. If any walk hits a cycle, surface that cycle diagnostic
            // first — cycles are an unconditional refusal, while multi-sink
            // is selection-shape dependent.
            var multiSinkDiagnostic = CheckMultipleSinks(
                sink, effectiveSelection, sinkFormula, source);
            if (multiSinkDiagnostic != null)
                return multiSinkDiagnostic;
        }

        // Free walk first — surfaces cycles ahead of any restriction work
        // and gives us "N" (the count of cells the walk would have visited
        // without restriction) for the dialog header. The restricted walk
        // is a strict sub-walk of this graph (subgraph of an acyclic graph
        // is acyclic), so cycle detection on the free walk is sufficient.
        // The free walk doesn't apply exclusion — the dialog header reads
        // a count of "what the walker would visit on a fresh open", which
        // stays stable across Include toggles so the user sees one
        // anchor count rather than a number that drifts on every click.
        var freeOutcome = CellGraphWalker.Walk(sink, source);
        if (freeOutcome.IsCycle)
            return RefusedWithCycle(sink, sinkFormula, freeOutcome.Cycle!);

        var freeWalkCount = freeOutcome.Cells!.Count;

        // Restricted/excluded/demoted walk: composes the selection
        // restriction (PR 9), the user's Include exclusions (PR 10), and
        // PR 11's role overrides. Exclusion drops cells entirely;
        // demotion treats cells as leaves; promotion un-leaf-restricts
        // by union'ing into restrictTo (the walker's full-walk set). A
        // cell could in principle be both demoted and promoted via the
        // same dictionary (the rowstate parser would've stored only the
        // last value, but defensively we emit at-most-one membership per
        // cell here too — promotion's restrictTo union is harmless even
        // for demoted cells because demotion is checked first inside the
        // walker).
        WalkOutcome outcome;
        var hasRestriction = effectiveSelection.Count > 1;
        var hasExclusion = excludedCells != null && excludedCells.Count > 0;
        var hasDemotion = demotedCells != null && demotedCells.Count > 0;
        var hasPromotion = promotedCells != null && promotedCells.Count > 0;
        if (hasRestriction || hasExclusion || hasDemotion || hasPromotion)
        {
            HashSet<CellRef>? restrictTo = null;
            if (hasRestriction)
            {
                // Promoted cells override leaf-restriction so their
                // formulas are loaded and their precedents pushed —
                // exactly what "promote pulls new precedents in" means
                // when the cell sat outside the original selection.
                restrictTo = new HashSet<CellRef>(effectiveSelection);
                if (hasPromotion)
                    restrictTo.UnionWith(promotedCells!);
            }
            outcome = CellGraphWalker.Walk(
                sink, source, restrictTo, excludedCells, demotedCells);
        }
        else
        {
            outcome = freeOutcome;
        }

        var walked = outcome.Cells!;
        var walkedCount = walked.Count - outcome.LeafRestrictedCount;

        // Collect unique range refs encountered anywhere in the walk. Order
        // is by first-encountered; that becomes the binding order for
        // ranges (which always sort before cell bindings). Excluded ranges
        // (PR 10) skip both promotion and coverage: the literal range
        // text stays in the calling step's formula and any cells reached
        // via other paths reappear as their own bindings instead of being
        // dropped under a range that no longer exists.
        var ranges = new List<FormulaRef>();
        var rangeSet = new HashSet<FormulaRef>();
        // Spec 0010 PR 5: promoted ranges that overlap a spill without lying
        // wholly inside it, keyed to the anchor they straddle. Carried onto
        // the range's binding row so the dialog can warn; the promotion
        // itself is unchanged.
        var straddledAnchorByRange = new Dictionary<FormulaRef, CellRef>();
        foreach (var cell in walked)
        {
            foreach (var p in cell.Precedents)
            {
                if (!p.IsRange) continue;
                if (excludedRanges != null && excludedRanges.Contains(p)) continue;
                // Spec 0010, range-promotion precedence: a range lying wholly
                // inside a spill takes the slice path below and never promotes
                // to a range input — whether it is a degenerate single cell
                // (`A2:A2`, a scalar in Excel), a band, an interior block, or
                // the whole spill. A range straddling the spill's boundary is
                // inexpressible as a slice and promotes exactly as today —
                // carrying the anchor it straddles so the dialog can warn.
                if (WhollyInsideSpill(p, source) != null) continue;
                if (!rangeSet.Add(p)) continue;
                ranges.Add(p);
                var straddled = StraddledSpillAnchor(p, source);
                if (straddled != null)
                    straddledAnchorByRange[p] = straddled;
            }
        }

        // Cells covered by any range are dropped from the bindings list,
        // even if they would otherwise have been steps. Two exemptions: the
        // sink (it's the LET body, not a binding) and, per spec 0010, a spill
        // anchor — it is the array every slice of it indexes into, so losing
        // its row would leave those slices with nothing to name.
        var coveredByRange = new HashSet<CellRef>();
        foreach (var r in ranges)
        {
            foreach (var cell in walked)
            {
                if (cell.Ref.Equals(sink))
                    continue;
                if (cell.HasSpill)
                    continue;
                if (r.Covers(cell.Ref))
                    coveredByRange.Add(cell.Ref);
            }
        }

        var nonSink = walked
            .Where(w => !w.Ref.Equals(sink) && !coveredByRange.Contains(w.Ref))
            .ToList();

        // Spec 0010 — spill slices. A reference landing wholly inside the
        // spill range of a cell that is already a binding becomes its own
        // binding row holding a named slice of the anchor's array. The
        // single-cell cases are one code path: `B1` (a spill child, which has
        // no formula of its own) and `A1` (a scalar reference to the spilling
        // anchor, which Excel reads as the top-left value — the pre-existing
        // silent widening this fixes). PR 4 adds range refs: a sub-block
        // becomes a `TAKE`/`DROP` slice row, and a range spanning the whole
        // spill needs no row at all — it aliases straight to the anchor's
        // binding name. `A1#` is excluded throughout: it IS the anchor's
        // binding.
        var anchorCells = new HashSet<CellRef>();
        foreach (var c in nonSink)
            if (c.HasSpill)
                anchorCells.Add(c.Ref);

        var slices = new List<SpillSlice>();
        var slicesByAnchor = new Dictionary<CellRef, List<SpillSlice>>();
        // Refs that resolve to the anchor's own binding name with no row of
        // their own: a range spanning the entire spill and covering more than
        // one cell. Resolved into `nameByRef` once names are assigned.
        var wholeArrayRefs = new List<(FormulaRef Ref, FormulaRef AnchorRowRef)>();
        if (anchorCells.Count > 0)
        {
            var sliceKeys = new HashSet<FormulaRef>();
            // `walked`, not `nonSink` — the sink's own formula gets rewritten
            // into the LET body, so a slice it references needs a row too.
            foreach (var cell in walked)
            foreach (var p in cell.Precedents)
            {
                if (p.IsSpilled) continue;
                if (p.Start.Equals(sink)) continue;
                if (excludedCells != null && excludedCells.Contains(p.Start)) continue;
                if (coveredByRange.Contains(p.Start)) continue;
                var spill = WhollyInsideSpill(p, source);
                if (spill == null) continue;
                // The anchor has to be a binding for there to be an array to
                // slice. It normally is — the walker redirects every in-spill
                // precedent to the anchor — but the user may have excluded it,
                // in which case the reference stays a literal cell address.
                if (!anchorCells.Contains(spill.Anchor)) continue;
                var end = p.End ?? p.Start;
                var r1 = p.Start.Row - spill.Anchor.Row + 1;
                var c1 = p.Start.Column - spill.Anchor.Column + 1;
                var r2 = end.Row - spill.Anchor.Row + 1;
                var c2 = end.Column - spill.Anchor.Column + 1;
                // Defensive: an inverted range (`B2:A1`) would blow up the
                // builder's argument validation. Excel normalises these on
                // entry, so this only guards against a malformed extraction.
                if (r1 < 1 || c1 < 1 || r2 < r1 || c2 < c1
                    || r2 > spill.Rows || c2 > spill.Columns)
                    continue;
                if (!sliceKeys.Add(p)) continue;

                // A range spanning the whole spill and more than one cell is
                // the array itself — it rewrites to the anchor's binding name
                // rather than earning a row that would just re-alias it. A
                // one-cell range is never this case: it takes the scalar
                // `INDEX` path even on a 1×1 spill, where it is simultaneously
                // the whole array.
                var spansWholeSpill = r1 == 1 && c1 == 1
                                      && r2 == spill.Rows
                                      && c2 == spill.Columns;
                var spansMoreThanOneCell = r2 > r1 || c2 > c1;
                if (spansWholeSpill && spansMoreThanOneCell)
                {
                    wholeArrayRefs.Add((p, new FormulaRef(spill.Anchor, IsSpilled: true)));
                    continue;
                }

                var slice = new SpillSlice(
                    p, spill.Anchor, r1, c1, r2, c2, spill.Rows, spill.Columns,
                    p.IsRange ? SliceRefShape.Range : SliceRefShape.SingleCell);
                slices.Add(slice);
                if (!slicesByAnchor.TryGetValue(spill.Anchor, out var forAnchor))
                    slicesByAnchor[spill.Anchor] = forAnchor = new List<SpillSlice>();
                forAnchor.Add(slice);
            }
        }

        // Names: ranges first (in encounter order), then cells (in topo
        // order), then slices. This keeps inputs grouped at the top of the
        // LET — ranges are always inputs — which matches the spec's dialog
        // ordering. Slices name last because their final fallback is derived
        // from the anchor's name, which has to be settled first.
        // PR 12: user name overrides are claimed first so auto-derived
        // names suffix around them (<c>x_2</c>) rather than the other way
        // around — an override is the user's authoritative choice.
        var nameByRef = AssignNames(ranges, nonSink, slices, source, nameOverrides);

        // A range spanning the whole spill has no row; it simply resolves to
        // the anchor's binding name, so the rewriter collapses the `A2:B4`
        // token to `extracted` the same way it collapses `A2#`.
        foreach (var (wholeRef, anchorRowRef) in wholeArrayRefs)
            if (nameByRef.TryGetValue(anchorRowRef, out var anchorName))
                nameByRef[wholeRef] = anchorName;

        // Build the in-scope set used to classify each cell as input vs
        // step. A cell is a step iff at least one of its precedents has a
        // binding name. Range precedents always have a binding (they're
        // promoted leaves), so a cell whose only in-scope precedent is a
        // range becomes a step whose RHS rewrites the range to its name.
        var bindings = new List<BindingRow>();

        foreach (var range in ranges)
        {
            var name = nameByRef[range];
            var rhs = range.DisplayAddress(source.SinkSheet);
            straddledAnchorByRange.TryGetValue(range, out var straddledAnchor);
            bindings.Add(new BindingRow(
                range, BindingRole.Input, name, rhs,
                StraddlesSpillAnchor: straddledAnchor));
        }

        // The pool of names already taken — outer ranges and outer cells
        // — used as the collision baseline when splicing inner-LET binding
        // names. Inner expansions add to this set as they go so a second
        // nested LET sees the first's names.
        var used = new HashSet<string>(nameByRef.Values, StringComparer.OrdinalIgnoreCase);

        var stepRows = new List<BindingRow>();

        // Slice rows are emitted immediately after their anchor's own row,
        // into whichever list that row landed in — so the concatenation of
        // inputs-then-steps still reads anchor, slices, dependants.
        void AppendSlices(WalkedCell anchorCell, FormulaRef anchorRowRef, List<BindingRow> target)
        {
            if (!anchorCell.HasSpill) return;
            if (!slicesByAnchor.TryGetValue(anchorCell.Ref, out var forAnchor)) return;
            var anchorName = nameByRef[anchorRowRef];
            foreach (var slice in forAnchor)
                target.Add(new BindingRow(
                    slice.Ref,
                    BindingRole.Input,
                    nameByRef[slice.Ref],
                    SpillSliceBuilder.Build(
                        anchorName, slice.SpillRows, slice.SpillColumns,
                        slice.Row, slice.RowEnd, slice.Column, slice.ColumnEnd, slice.Shape),
                    // No formula to bake and nothing to demote to, so the
                    // role toggle is meaningless on a slice row.
                    CanToggleRole: false,
                    SliceOf: anchorRowRef));
        }

        foreach (var cell in nonSink)
        {
            var cellFormulaRef = RowRef(cell);
            var name = nameByRef[cellFormulaRef];
            var role = ClassifyRole(cell, nameByRef, excluded, roleOverrides);
            if (role == BindingRole.Input)
            {
                // Bare A1 for in-sheet refs, sheet-qualified otherwise,
                // workbook-qualified for externals — DisplayAddress handles
                // the quoting rules so the RHS round-trips cleanly. A
                // spilling anchor appends '#' so the binding represents the
                // whole array rather than just the anchor cell's value;
                // step-classified cells skip the suffix because their RHS
                // is the rewritten formula (array semantics flow through
                // it naturally).
                var inputRhs = cell.Ref.DisplayAddress(source.SinkSheet);
                if (cell.HasSpill)
                    inputRhs += "#";
                // CanToggleRole = "this row can be promoted to a step".
                // Inputs without a source formula (literal-value cells)
                // can't be promoted — there's nothing to bake into the
                // RHS — so the dialog hides the toggle for them.
                bindings.Add(new BindingRow(
                    cellFormulaRef, BindingRole.Input, name, inputRhs,
                    CanToggleRole: cell.HasSourceFormula));
                AppendSlices(cell, cellFormulaRef, bindings);
                continue;
            }

            // Step. If the formula is purely a LET (no trailing content
            // after the LET's close paren — `=LET(x, 1, x+1)` yes,
            // `=LET(x, 1, x+1)+A1` no), expand it inline: splice the
            // inner bindings (with collision-renames applied) ahead of
            // this step row, and use the inner body as the step's RHS.
            // Otherwise the RHS is the cell's formula with in-scope refs
            // rewritten to binding names — both code paths use the cell's
            // own sheet as the default for unqualified refs.
            var formulaText = cell.Formula!;
            string stepRhs;
            // True when we tentatively free the cell's outer name so
            // the inner LET can claim it; we re-reserve below if the
            // cell turns out not to alias after all.
            var freedOuterName = false;
            if (IsPureLetFormula(formulaText))
            {
                // If the LET's body is a bare identifier, the outer
                // step row is going to alias-eliminate (the body, after
                // rewrites, will be a single binding name). In that
                // case the cell's outer name is never actually used as
                // a binding label, so freeing it before expansion lets
                // the inner LET's bindings claim it without the
                // collision-suffix dance — e.g. cell labelled `y` with
                // formula `=LET(x, J6, y, x+5, y)` keeps the inner `y`
                // unsuffixed instead of forcing it to `y_2`. Bodies
                // that are calculations (`y+1`, `f(y)`) take the
                // original PR 6 path: outer name stays reserved, inner
                // collisions suffix.
                if (TryGetBareIdentifier(LetParser.Parse(formulaText).Body) != null)
                {
                    used.Remove(name);
                    freedOuterName = true;
                }

                var (innerRows, body) = ExpandNestedLet(
                    formulaText, cellFormulaRef, cell.Ref.Sheet, used, nameByRef);
                stepRows.AddRange(innerRows);
                stepRhs = body;
            }
            else
            {
                stepRhs = CellRefExtractor.Rewrite(
                    StripLeadingEquals(formulaText), cell.Ref.Sheet, nameByRef);
            }

            // Alias elimination: a step whose rewritten RHS is just a
            // bare existing binding name (e.g. `=A1` rewrites to
            // `numbers` when A1 already binds to `numbers`) is a no-op
            // rebind. Drop the row and redirect this cell's outer name
            // to the alias target so downstream cells (and the sink
            // body) rewrite their refs straight through.
            var stepAlias = TryGetBareIdentifier(stepRhs);
            if (stepAlias != null && used.Contains(stepAlias))
            {
                nameByRef[cellFormulaRef] = stepAlias;
                // The row is gone but its slices aren't — they index into
                // the alias target, which is an existing binding declared
                // earlier in the LET.
                AppendSlices(cell, cellFormulaRef, stepRows);
                continue;
            }

            // Not aliased after all — re-reserve the cell's name we
            // tentatively freed. If an inner binding has since claimed
            // it, suffix.
            if (freedOuterName)
            {
                if (used.Contains(name))
                {
                    name = ResolveCollision(name, used);
                    nameByRef[cellFormulaRef] = name;
                }
                used.Add(name);
            }

            // Steps always have a formula (we just rewrote it into
            // stepRhs), so they're always demote-toggleable — flipping
            // a step to input replaces stepRhs with the cell-ref and
            // drops any precedents that were only reachable via this
            // cell.
            stepRows.Add(new BindingRow(
                cellFormulaRef, BindingRole.Step, name, stepRhs,
                CanToggleRole: true));
            AppendSlices(cell, cellFormulaRef, stepRows);
        }

        // Range and cell inputs first, then steps in topo order — matches
        // the spec's dialog ordering and gives a natural read for the
        // synthesised LET.
        bindings.AddRange(stepRows);

        // Sink itself can be a LET. Expand it inline — its bindings
        // splice in after the step rows and its inner body becomes the
        // outer LET's body. Without this, a `=LET(...)` sink would emit
        // the inner LET nested inside the synthesised outer LET, which
        // works but defeats the point of gathering. Same alias-elimination
        // rules apply via ExpandNestedLet.
        string bodyText;
        if (IsPureLetFormula(sinkFormula))
        {
            var (sinkInnerRows, innerBody) = ExpandNestedLet(
                sinkFormula, new FormulaRef(sink), sink.Sheet, used, nameByRef);
            bindings.AddRange(sinkInnerRows);
            bodyText = innerBody;
        }
        else
        {
            bodyText = CellRefExtractor.Rewrite(
                StripLeadingEquals(sinkFormula), source.SinkSheet, nameByRef);
        }

        // PR 10: when every row was excluded (or alias-elimination
        // collapsed the bindings to nothing) the LET would be invalid —
        // Excel requires at least one binding pair. Emit just the body
        // as a bare formula in that case; the synthesised result is
        // observably the original formula minus any in-scope rewrites,
        // which is the right output for a "gather nothing" pass.
        var sb = new StringBuilder();
        sb.Append('=');
        if (bindings.Count == 0)
        {
            sb.Append(bodyText);
        }
        else
        {
            FormulaFormatter.AppendLet(
                sb,
                indent: 0,
                bindings.Select(b => (b.Name, b.Rhs)).ToList(),
                bodyText);
        }

        return new GatherResult(
            sink, sinkFormula, bindings, sb.ToString(),
            WalkedCount: walkedCount, FreeWalkCount: freeWalkCount);
    }

    /// <summary>
    ///     Pure-LAMBDA-call sink check (PR 8). Returns a refusal result
    ///     when the sink's formula is exactly a call to a registered
    ///     LAMBDA (e.g. <c>=Foo(A1, B1)</c>); the message points the
    ///     author at <c>/EditLambda</c>. Reuses
    ///     <see cref="EditLambdaCommand.TryParseLambdaCall" /> which
    ///     enforces the "no trailing content" rule, so wrapped calls
    ///     like <c>=Foo(A1) + 1</c> walk normally. The
    ///     <see cref="ICellSource.IsLambdaName" /> probe distinguishes a
    ///     registered LAMBDA from a built-in function call (a built-in
    ///     like SUM isn't a workbook name) and from the engine's own
    ///     pure-<c>=LET(...)</c> sink path (LET isn't a workbook name
    ///     either, so the engine still expands it inline).
    /// </summary>
    private static GatherResult? CheckLambdaCallSink(
        CellRef sink, string sinkFormula, ICellSource source)
    {
        var call = EditLambdaCommand.TryParseLambdaCall(sinkFormula);
        if (call == null)
            return null;
        if (!source.IsLambdaName(call.Name))
            return null;

        var diagnostic = new GatherDiagnostic(
            GatherDiagnosticKind.LambdaCallSink,
            "This cell is a LAMBDA call. Run /EditLambda first to expand it " +
            "into a LET, then re-run /Gather.",
            new[] { sink });

        return new GatherResult(
            sink, sinkFormula, Array.Empty<BindingRow>(), string.Empty, diagnostic);
    }

    /// <summary>
    ///     Selection normalisation (spec 0010). Maps every spill child in
    ///     the selection to its anchor and drops the duplicates that
    ///     collapse produces, so a drag across <c>A1:C2</c> where <c>A1</c>
    ///     spills into it arrives at the rest of the engine as the single
    ///     cell <c>A1</c> — the calculation the author dragged over, not its
    ///     six output cells. Runs before the multi-sink check (which would
    ///     otherwise see six sink candidates) and before <c>restrictTo</c>
    ///     is built (which would otherwise leaf-restrict the anchor's own
    ///     precedents against a set of cells that aren't in the graph).
    ///
    ///     Deduplication is part of the contract, not a tidy-up: the
    ///     multi-sink count is over the list, so leaving five copies of the
    ///     anchor behind would refuse the very selection this exists to
    ///     allow. It also incidentally hardens the count against a raw
    ///     selection that repeats a cell across two Ctrl-clicked areas.
    ///
    ///     Cost is one <c>GetSpill</c> per selected cell — the same order
    ///     as the multi-sink check's per-cell walk that follows it, and on
    ///     non-spilling cells the live adapter's probe is a single COM
    ///     property read.
    /// </summary>
    private static IReadOnlyList<CellRef> NormaliseSelection(
        IReadOnlyList<CellRef> selection, ICellSource source)
    {
        if (selection.Count <= 1)
            return selection;

        var seen = new HashSet<CellRef>();
        var normalised = new List<CellRef>(selection.Count);
        var changed = false;
        foreach (var cell in selection)
        {
            var spill = source.GetSpill(cell);
            var mapped = spill?.Anchor ?? cell;
            if (!mapped.Equals(cell))
                changed = true;
            if (!seen.Add(mapped))
            {
                changed = true;
                continue;
            }
            normalised.Add(mapped);
        }

        return changed ? normalised : selection;
    }

    /// <summary>
    ///     Builds a refusal result for a sink that is a spill child (spec
    ///     0010). Names the anchor in the message so the fix is one click
    ///     away: <c>"D4 is inside A1's spill range. Gather from A1
    ///     instead."</c> Addresses are sheet-qualified only when they leave
    ///     the sink's sheet, matching how binding RHS text renders.
    /// </summary>
    private static GatherResult RefusedWithSpillChildSink(
        CellRef sink, SpillInfo spill, ICellSource source)
    {
        var child = sink.DisplayAddress(source.SinkSheet);
        var anchor = spill.Anchor.DisplayAddress(source.SinkSheet);
        var diagnostic = new GatherDiagnostic(
            GatherDiagnosticKind.SpillChildSink,
            $"{child} is inside {anchor}'s spill range. Gather from {anchor} instead.",
            new[] { sink, spill.Anchor });

        return new GatherResult(
            sink, string.Empty, Array.Empty<BindingRow>(), string.Empty, diagnostic);
    }

    /// <summary>
    ///     Multi-sink check (PR 7). For a multi-cell selection, walks each
    ///     selected cell and marks the others it transitively reaches; the
    ///     selected cells nobody reached are independent sinks. If 2+
    ///     remain, the user has accidentally multi-selected disconnected
    ///     calculations and the engine refuses. Single-cell selection
    ///     short-circuits — there's only one possible sink. If any sub-walk
    ///     hits a cycle, the cycle diagnostic wins (cycles are an
    ///     unconditional refusal regardless of selection shape) — so the
    ///     author always sees the more fundamental error first.
    /// </summary>
    private static GatherResult? CheckMultipleSinks(
        CellRef sink, IReadOnlyList<CellRef> selection, string sinkFormula, ICellSource source)
    {
        if (selection.Count <= 1)
            return null;

        var selectionSet = new HashSet<CellRef>(selection);
        var covered = new HashSet<CellRef>();
        foreach (var cell in selection)
        {
            var outcome = CellGraphWalker.Walk(cell, source);
            if (outcome.IsCycle)
                return RefusedWithCycle(sink, sinkFormula, outcome.Cycle!);

            foreach (var walked in outcome.Cells!)
            {
                if (walked.Ref.Equals(cell))
                    continue;
                if (selectionSet.Contains(walked.Ref))
                    covered.Add(walked.Ref);
            }
        }

        var sinkCount = selection.Count(s => !covered.Contains(s));
        if (sinkCount <= 1)
            return null;

        var diagnostic = new GatherDiagnostic(
            GatherDiagnosticKind.MultipleSinks,
            "The selection contains multiple independent calculations.\n\n" +
            "To gather, select a single sink cell, or restrict the selection " +
            "to a single calculation chain.",
            Array.Empty<CellRef>());

        return new GatherResult(
            sink, sinkFormula, Array.Empty<BindingRow>(), string.Empty, diagnostic);
    }

    /// <summary>
    ///     Builds a refusal result with a cycle diagnostic, formatting the
    ///     cell list as a closed path (each cell joined by <c>→</c>, with
    ///     the cycle's first cell repeated at the end so the loop is
    ///     visible). Sheet names are always included so cross-sheet cycles
    ///     remain unambiguous in the dialog text.
    /// </summary>
    private static GatherResult RefusedWithCycle(
        CellRef sink, string sinkFormula, IReadOnlyList<CellRef> cycle)
    {
        var path = string.Join(" → ", cycle.Select(FormatCell));
        var closed = $"{path} → {FormatCell(cycle[0])}";
        var message =
            "Cell graph contains a circular reference:\n\n" +
            $"  {closed}\n\n" +
            "Excel itself flags this as a circular reference. Resolve it before running /Gather.";
        var diagnostic = new GatherDiagnostic(GatherDiagnosticKind.Cycle, message, cycle);
        return new GatherResult(
            sink, sinkFormula, Array.Empty<BindingRow>(), string.Empty, diagnostic);
    }

    private static string FormatCell(CellRef cell) => $"{cell.Sheet}!{cell.A1Address}";

    /// <summary>
    ///     Picks a binding name for each range and each non-sink cell.
    ///     Ranges name first (their topological position is "before any
    ///     cell that references them"). Each ref tries cell-above first,
    ///     then cell-left, both routed through <see cref="LetNameSanitizer" />.
    ///     Falls back to <c>step_N</c> only when both neighbours yield
    ///     nothing usable. Final collisions — two refs producing the same
    ///     name — are resolved by suffixing <c>_2</c>, <c>_3</c>, … in
    ///     binding order.
    /// </summary>
    private static Dictionary<FormulaRef, string> AssignNames(
        IReadOnlyList<FormulaRef> ranges,
        IReadOnlyList<WalkedCell> nonSink,
        IReadOnlyList<SpillSlice> slices,
        ICellSource source,
        IReadOnlyDictionary<FormulaRef, string>? nameOverrides)
    {
        var nameByRef = new Dictionary<FormulaRef, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fallbackCounter = 0;

        // PR 12: claim every user-supplied override up front so the name
        // is locked in `used` before auto-derivation runs. An auto-derived
        // name that would have collided with an override now suffixes
        // around it instead — matching the spec's "user rename wins"
        // intent. Two user overrides that share a name collide here too;
        // the second one suffixes (<c>x</c> → <c>x_2</c>) so the LET
        // stays valid even when the dialog hasn't (or can't) prevent the
        // collision client-side. Iteration order picks the winner — for
        // overrides on cells, that's topo order, so an upstream cell
        // claims the bare name and downstream cells get the suffix.
        if (nameOverrides != null && nameOverrides.Count > 0)
        {
            foreach (var range in ranges)
                if (nameOverrides.TryGetValue(range, out var overrideName))
                {
                    var resolved = ResolveCollision(overrideName, used);
                    nameByRef[range] = resolved;
                    used.Add(resolved);
                }

            foreach (var cell in nonSink)
            {
                var fr = RowRef(cell);
                if (nameOverrides.TryGetValue(fr, out var overrideName))
                {
                    var resolved = ResolveCollision(overrideName, used);
                    nameByRef[fr] = resolved;
                    used.Add(resolved);
                }
            }

            foreach (var slice in slices)
                if (nameOverrides.TryGetValue(slice.Ref, out var overrideName))
                {
                    var resolved = ResolveCollision(overrideName, used);
                    nameByRef[slice.Ref] = resolved;
                    used.Add(resolved);
                }
        }

        foreach (var range in ranges)
        {
            if (nameByRef.ContainsKey(range)) continue;
            // Use the range Start cell's neighbours for the label hint —
            // ranges typically have a header above their top-left corner.
            var aboveText = range.IsExternal ? null : SafeAbove(source, range.Start);
            var leftText = range.IsExternal ? null : SafeLeft(source, range.Start);
            var baseName = LetNameSanitizer.Sanitize(aboveText)
                           ?? LetNameSanitizer.Sanitize(leftText);
            nameByRef[range] = AssignOne(baseName, used, ref fallbackCounter);
        }

        foreach (var cell in nonSink)
        {
            var fr = RowRef(cell);
            if (nameByRef.ContainsKey(fr)) continue;
            var baseName = LetNameSanitizer.Sanitize(cell.CellAboveText)
                           ?? LetNameSanitizer.Sanitize(cell.CellLeftText);
            nameByRef[fr] = AssignOne(baseName, used, ref fallbackCounter);
        }

        // Slice rows: the spec 0005 ladder with one new final fallback —
        // `<anchorName>_<rowMajorIndex>` in place of the generic `step_N`,
        // so the row reads as "part of <anchor>" and groups visually with
        // its source. The existing collision suffixing applies on top,
        // which is what turns a scalar reference to the anchor itself into
        // `<anchorLabel>_2` (it shares the anchor's cell-above label by
        // construction).
        foreach (var slice in slices)
        {
            if (nameByRef.ContainsKey(slice.Ref)) continue;
            var anchorName = nameByRef.TryGetValue(slice.AnchorRowRef, out var an) ? an : null;
            var cellRef = slice.Ref.Start;
            var baseName = LetNameSanitizer.Sanitize(source.GetCellAboveText(cellRef))
                           ?? LetNameSanitizer.Sanitize(source.GetCellLeftText(cellRef))
                           ?? (anchorName == null ? null : $"{anchorName}_{slice.RowMajorIndex}");
            nameByRef[slice.Ref] = AssignOne(baseName, used, ref fallbackCounter);
        }

        return nameByRef;
    }

    /// <summary>
    ///     The spill a reference lies <em>wholly</em> inside, or null when it
    ///     touches no spill or straddles one's boundary. A single-cell ref is
    ///     inside iff <see cref="ICellSource.GetSpill" /> knows it; a range is
    ///     inside iff both endpoints land in the same spill rectangle. This is
    ///     the one predicate behind spec 0010's range-promotion precedence:
    ///     wholly-inside means "slice", anything else means "promote as
    ///     today", and both call sites (promotion and slice construction) read
    ///     it so they cannot disagree. Explicit spill refs (<c>A1#</c>) are
    ///     never slices — they are the anchor's binding.
    /// </summary>
    private static SpillInfo? WhollyInsideSpill(FormulaRef p, ICellSource source)
    {
        if (p.IsSpilled)
            return null;
        var spill = source.GetSpill(p.Start);
        if (spill == null)
            return null;
        if (p.IsRange && !spill.Contains(p.End!))
            return null;
        return spill;
    }

    /// <summary>
    ///     The anchor of the spill a promoted <em>range</em> partly overlaps,
    ///     or null when it overlaps none. The complement of
    ///     <see cref="WhollyInsideSpill" /> for ranges: wholly-inside is a
    ///     slice, no overlap is an ordinary range, and partial overlap is the
    ///     straddle spec 0010 PR 5 warns about. Purely a UI signal — the
    ///     caller promotes the range identically either way, so a missed
    ///     detection costs a warning, never correctness.
    ///
    ///     Detection probes the range's four corners rather than its cells:
    ///     one <see cref="ICellSource.GetSpill" /> per corner is bounded,
    ///     where scanning would cost a COM probe per cell of an arbitrarily
    ///     large range. Every single-edge overflow (the range hanging over the
    ///     spill's top, left, right or bottom) puts at least one corner inside
    ///     the spill, so all four are caught. The bounded blind spot is a
    ///     range that <em>crosses right through</em> or wholly encloses a
    ///     spill — no corner of it lands inside, so no probe finds the spill
    ///     and the row goes unmarked. That shape (<c>SUM(A1:Z100)</c> over a
    ///     region that happens to contain a spill) reads as a deliberate bulk
    ///     range rather than a nearly-a-slice reference, which is the case the
    ///     warning exists for.
    /// </summary>
    private static CellRef? StraddledSpillAnchor(FormulaRef p, ICellSource source)
    {
        if (!p.IsRange || p.IsSpilled)
            return null;
        var start = p.Start;
        var end = p.End!;
        var corners = new[]
        {
            start,
            end,
            new CellRef(start.Sheet, end.Column, start.Row, start.ExternalWorkbook),
            new CellRef(start.Sheet, start.Column, end.Row, start.ExternalWorkbook)
        };
        var probed = new HashSet<CellRef>();
        foreach (var corner in corners)
        {
            if (!probed.Add(corner))
                continue;
            var spill = source.GetSpill(corner);
            if (spill == null)
                continue;
            // A range wholly inside the spill is a slice, not a straddle; it
            // never reaches promotion, but the guard keeps this predicate
            // meaningful on its own.
            if (spill.Contains(start) && spill.Contains(end))
                return null;
            return spill.Anchor;
        }

        return null;
    }

    /// <summary>
    ///     The <see cref="FormulaRef" /> that identifies a walked cell's own
    ///     binding row. A spilling anchor's binding <em>is</em> the array, so
    ///     its row is keyed on the spilled ref (<c>A1#</c>) — which leaves
    ///     the non-spilled key (<c>A1</c>) free to identify the slice row for
    ///     a scalar reference to the same cell. Spec 0010's "the lookup
    ///     dictionary carries both keys" is exactly this split.
    /// </summary>
    private static FormulaRef RowRef(WalkedCell cell) =>
        cell.HasSpill
            ? new FormulaRef(cell.Ref, IsSpilled: true)
            : new FormulaRef(cell.Ref);

    private static string AssignOne(string? baseName, HashSet<string> used, ref int fallbackCounter)
    {
        string finalName;
        if (baseName == null)
        {
            // No usable label — burn through step_N until we find a
            // number that hasn't already been used by a sanitised label.
            do
            {
                fallbackCounter++;
                finalName = $"step_{fallbackCounter}";
            } while (used.Contains(finalName));
        }
        else
        {
            finalName = baseName;
            var suffix = 2;
            while (used.Contains(finalName))
            {
                finalName = $"{baseName}_{suffix}";
                suffix++;
            }
        }

        used.Add(finalName);
        return finalName;
    }

    // Range-label probes share the cell-source contract used for cell-
    // derived names but skip externals (their neighbour cells aren't
    // reachable via the live workbook).
    private static string? SafeAbove(ICellSource source, CellRef cell)
    {
        return source.GetCellAboveText(cell);
    }

    private static string? SafeLeft(ICellSource source, CellRef cell)
    {
        return source.GetCellLeftText(cell);
    }

    private static BindingRole ClassifyRole(
        WalkedCell cell,
        IReadOnlyDictionary<FormulaRef, string> nameByRef,
        ISet<FormulaRef>? excluded,
        IReadOnlyDictionary<FormulaRef, BindingRole>? roleOverrides)
    {
        // PR 11: a row-level role override forces the classification.
        // Demotion (Input override on a step) lands here as a request to
        // bind the cell-ref instead of the rewritten formula; the
        // walker's demotion path has already nulled the formula and
        // emptied the precedents so the natural Formula==null branch
        // below would return Input anyway, but the explicit check makes
        // the intent legible.
        // Promotion (Step override on an input) requires a non-null
        // formula — without one there's nothing to render as the RHS.
        // The dialog hides the toggle for cells without a formula in the
        // source so this path is defensive; an out-of-source promote
        // silently falls through to natural classification (Input,
        // because Formula==null).
        // A spilling anchor's row is keyed on the spilled ref, but accept an
        // override keyed on the bare cell too — the two identify the same
        // row and callers predating spec 0010 pass the bare form.
        var fr = RowRef(cell);
        if (roleOverrides != null
            && (roleOverrides.TryGetValue(fr, out var overrideRole)
                || roleOverrides.TryGetValue(new FormulaRef(cell.Ref), out overrideRole)))
        {
            if (overrideRole == BindingRole.Step && cell.Formula != null)
                return BindingRole.Step;
            if (overrideRole == BindingRole.Input)
                return BindingRole.Input;
        }
        // External refs and missing-sheet refs surface here as null-formula
        // cells (the source can't reach them) and so naturally classify as
        // input — exactly what we want.
        if (cell.Formula == null)
            return BindingRole.Input;
        // A formula cell is a step iff at least one precedent has a binding
        // (cell-binding for in-scope cells, range-binding for promoted
        // ranges). Cells whose precedents were all dropped (covered by a
        // range, out of scope, etc.) revert to inputs whose RHS is the
        // cell address — promotion to step is a follow-up in PR 11. Spill
        // anchors follow the same rule: a spilling cell with in-scope
        // precedents is a step (RHS = rewritten formula, array semantics
        // flow through naturally); a spilling leaf is an input with RHS
        // suffixed by `#` to preserve the dynamic-array binding.
        // PR 10: a precedent the user explicitly excluded still keeps the
        // cell a step. The rewritten formula will carry the excluded
        // ref's literal text (the rewriter leaves unmapped refs alone),
        // which is the spec's "calling step keeps the cell-ref" — turning
        // the cell into an input would discard the formula instead.
        foreach (var p in cell.Precedents)
        {
            if (nameByRef.ContainsKey(p))
                return BindingRole.Step;
            if (excluded != null && excluded.Contains(p))
                return BindingRole.Step;
        }
        return BindingRole.Input;
    }

    /// <summary>
    ///     One reference that landed inside a spill and will become its own
    ///     binding row (spec 0010, <em>Slices are binding rows</em>).
    ///     <see cref="Ref" /> is the reference exactly as it was written —
    ///     the child cell, the anchor cell for a scalar reference to it, or a
    ///     range lying inside the spill — and doubles as the row's identity
    ///     and its rewrite-lookup key.
    ///     <see cref="Row" />..<see cref="RowEnd" /> and
    ///     <see cref="Column" />..<see cref="ColumnEnd" /> are the reference's
    ///     rectangle, 1-based relative to the anchor; the two ends coincide
    ///     for a single-cell reference.
    /// </summary>
    private sealed record SpillSlice(
        FormulaRef Ref,
        CellRef Anchor,
        int Row,
        int Column,
        int RowEnd,
        int ColumnEnd,
        int SpillRows,
        int SpillColumns,
        SliceRefShape Shape)
    {
        /// <summary>The anchor's own binding-row key — always the spilled form.</summary>
        public FormulaRef AnchorRowRef => new(Anchor, IsSpilled: true);

        /// <summary>
        ///     The slice's linear position inside the spill in row-major
        ///     order (<c>A1</c>→1, <c>B1</c>→2 for a 1×2 spill), used by the
        ///     anchor-derived naming fallback.
        /// </summary>
        public int RowMajorIndex => (Row - 1) * SpillColumns + Column;
    }

    private static string StripLeadingEquals(string formula)
    {
        var trimmed = formula.TrimStart();
        return trimmed.StartsWith("=", StringComparison.Ordinal) ? trimmed[1..] : trimmed;
    }

    /// <summary>
    ///     True when <paramref name="formula" /> is a single
    ///     <c>=LET(...)</c> that closes at the end of the formula (modulo
    ///     trailing whitespace). Distinct from
    ///     <see cref="LetParser.IsLetFormula" />, which only checks the
    ///     prefix — a formula like <c>=LET(x, 1, x+1) + A1</c> would slip
    ///     through that check and have its trailing <c>+ A1</c> silently
    ///     dropped on expansion. We guard against that here by requiring
    ///     the LET to be the whole formula.
    /// </summary>
    private static bool IsPureLetFormula(string formula)
    {
        if (!LetParser.IsLetFormula(formula))
            return false;

        var openParen = formula.IndexOf('(');
        if (openParen < 0)
            return false;
        var closeParen = LetParser.FindMatchingClose(formula, openParen);
        if (closeParen < 0)
            return false;

        for (var i = closeParen + 1; i < formula.Length; i++)
            if (!char.IsWhiteSpace(formula[i]))
                return false;
        return true;
    }

    /// <summary>
    ///     Expands an inner <c>=LET(...)</c> living on <paramref name="hostCell" />
    ///     into a list of binding rows plus the inner body, both as they
    ///     should appear in the outer LET. Each inner binding's name is
    ///     resolved against <paramref name="used" /> by suffixing
    ///     <c>_2</c>, <c>_3</c>, … on collision; the renamed bindings are
    ///     reflected throughout the inner LET (later RHSes and the body).
    ///     Outer-cell refs inside any inner RHS or the body are rewritten
    ///     to outer binding names via <paramref name="nameByRef" />, with
    ///     <paramref name="defaultSheet" /> as the host cell's own sheet
    ///     so unqualified refs resolve like they do in the live formula.
    ///     Inner-binding renames are applied BEFORE the cell-ref rewrite —
    ///     otherwise an outer cell that maps to a name matching an inner
    ///     binding (e.g. outer <c>A1</c> named <c>x</c>, inner
    ///     <c>=LET(x, ...)</c>) would see its produced <c>x</c> token
    ///     incorrectly captured by the inner-rename pass. Each inner
    ///     binding row carries the host cell's <see cref="FormulaRef" />
    ///     as its <c>Source</c> so the dialog can group it with the parent
    ///     step; future PRs can refine the display.
    /// </summary>
    private static (List<BindingRow> InnerRows, string Body) ExpandNestedLet(
        string letFormula,
        FormulaRef hostCell,
        string defaultSheet,
        HashSet<string> used,
        IReadOnlyDictionary<FormulaRef, string> nameByRef)
    {
        var parsed = LetParser.Parse(letFormula);
        var innerRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var innerRows = new List<BindingRow>(parsed.Bindings.Count);

        foreach (var binding in parsed.Bindings)
        {
            // Apply inner renames first so a later cell-rewrite that
            // produces a token coincidentally matching an inner name isn't
            // captured by the rename pass.
            var rhs = ApplyInnerRenames(binding.RhsText, innerRenames);
            rhs = CellRefExtractor.Rewrite(rhs, defaultSheet, nameByRef);

            // Alias elimination: if the rewritten RHS is exactly an
            // existing outer binding name, the inner binding is a no-op
            // rebind. Skip emitting a row and propagate the alias via
            // the rename map so subsequent inner RHSes and the body
            // collapse straight to the target name. Don't claim the
            // original inner name in `used` — it's not a binding anymore.
            var alias = TryGetBareIdentifier(rhs);
            if (alias != null && used.Contains(alias))
            {
                innerRenames[binding.Name] = alias;
                continue;
            }

            var finalName = ResolveCollision(binding.Name, used);
            if (!string.Equals(finalName, binding.Name, StringComparison.OrdinalIgnoreCase))
                innerRenames[binding.Name] = finalName;
            used.Add(finalName);

            // The inner binding's role mirrors its RHS shape: a pure
            // value/reference is an input, a calculation is a step.
            // Inputs/steps are rendered identically in the LET text — this
            // distinction only matters for the dialog's visual grouping.
            // Inner-LET rows are flagged as expansions so the dialog can
            // hide the Include checkbox: they share their host cell's
            // Source and don't represent a discrete cell the user can drop
            // independently — toggling the host cell's checkbox is the
            // way to remove an inner LET from the gather output.
            var role = binding.IsCalculation ? BindingRole.Step : BindingRole.Input;
            innerRows.Add(new BindingRow(hostCell, role, finalName, rhs, IsExpansion: true));
        }

        var body = ApplyInnerRenames(parsed.Body, innerRenames);
        body = CellRefExtractor.Rewrite(body, defaultSheet, nameByRef);

        return (innerRows, body);
    }

    private static string ResolveCollision(string baseName, HashSet<string> used)
    {
        if (!used.Contains(baseName))
            return baseName;
        var suffix = 2;
        while (used.Contains($"{baseName}_{suffix}"))
            suffix++;
        return $"{baseName}_{suffix}";
    }

    // Mirrors LetParser.IdentifierPattern. '?' is permitted anywhere
    // except the first character so a binding renamed to a
    // predicate-style name like 'Help?' is recognised as a single token.
    private static readonly Regex BareIdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.?]*$",
        RegexOptions.CultureInvariant);

    /// <summary>
    ///     Returns the trimmed text if it is exactly a bare Excel-name
    ///     identifier (the same shape <see cref="LetParser" /> validates
    ///     binding names against); otherwise null. Used by the alias-
    ///     elimination passes — a binding/step whose RHS reduces to a
    ///     single existing binding name is a no-op rebind that the
    ///     engine collapses by propagating the rename.
    /// </summary>
    private static string? TryGetBareIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;
        return BareIdentifierPattern.IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>
    ///     Replaces every bare-identifier occurrence of a key in
    ///     <paramref name="renames" /> with the mapped value. Tokens are
    ///     identified by Excel's name-shape rule (<c>[A-Za-z_][A-Za-z0-9_.?]*</c>) —
    ///     <c>?</c> is part of the name body so a rename of <c>Help?</c>
    ///     matches the whole token instead of just <c>Help</c>. Strings
    ///     (<c>"..."</c>) and single-quoted sheet/workbook qualifiers
    ///     (<c>'My Sheet'!</c>) are skipped wholesale. A token followed by
    ///     <c>!</c> is treated as a sheet qualifier and left alone —
    ///     that's a cell-ref position, not a name reference.
    /// </summary>
    private static string ApplyInnerRenames(string text, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0 || string.IsNullOrEmpty(text))
            return text;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '"')
            {
                var end = SkipDoubleQuoted(text, i);
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }

            if (c == '\'')
            {
                var end = SkipSingleQuoted(text, i);
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }

            if (IsIdentStart(c))
            {
                var start = i;
                i++;
                while (i < text.Length && IsIdentPart(text[i])) i++;
                var token = text[start..i];

                // Followed by '!' → this is a sheet qualifier inside a
                // cell ref like Sheet1!A1; never a name reference.
                var isSheetQualifier = i < text.Length && text[i] == '!';
                if (!isSheetQualifier && renames.TryGetValue(token, out var renamed))
                    sb.Append(renamed);
                else
                    sb.Append(token);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    // '?' is allowed anywhere except the first char of an Excel name, so
    // it's part of the identifier body but not its start. Without this,
    // the tokenizer would split 'Help?' into 'Help' + '?' and an inner
    // rename keyed on 'Help?' would never match the whole token.
    private static bool IsIdentPart(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '?';

    private static int SkipDoubleQuoted(string text, int openQuoteIndex)
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

    private static int SkipSingleQuoted(string text, int openQuoteIndex)
    {
        var i = openQuoteIndex + 1;
        while (i < text.Length)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
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
