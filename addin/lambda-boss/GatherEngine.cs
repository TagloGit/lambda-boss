using System.Text;

namespace LambdaBoss;

/// <summary>
///     The top-level Gather entry point. Walks the precedent graph, names
///     each cell and range, classifies each as input or step, rewrites
///     step formulas to refer to bindings, and emits a single
///     <c>=LET(...)</c> formula equivalent to the sink-rooted calculation
///     graph. PR 4 promotes range refs to single-input bindings: each
///     unique range encountered while walking becomes one leaf input, and
///     any walked cell that falls inside a promoted range is dropped from
///     the bindings list.
/// </summary>
public static class GatherEngine
{
    /// <summary>
    ///     Runs the gather over <paramref name="sink" />. Returns null if
    ///     the sink has no formula — the caller (slash command) treats this
    ///     as a silent no-op rather than an error.
    /// </summary>
    public static GatherResult? Gather(CellRef sink, ICellSource source)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(source);

        var sinkFormula = source.GetFormula(sink);
        if (sinkFormula == null)
            return null;

        var walked = CellGraphWalker.Walk(sink, source);

        // Collect unique range refs encountered anywhere in the walk. Order
        // is by first-encountered; that becomes the binding order for
        // ranges (which always sort before cell bindings).
        var ranges = new List<FormulaRef>();
        var rangeSet = new HashSet<FormulaRef>();
        foreach (var cell in walked)
        {
            foreach (var p in cell.Precedents)
            {
                if (p.IsRange && rangeSet.Add(p))
                    ranges.Add(p);
            }
        }

        // Cells covered by any range are dropped from the bindings list,
        // even if they would otherwise have been steps. The sink itself is
        // never dropped — it's the LET body, not a binding.
        var coveredByRange = new HashSet<CellRef>();
        foreach (var r in ranges)
        {
            foreach (var cell in walked)
            {
                if (cell.Ref.Equals(sink))
                    continue;
                if (r.Covers(cell.Ref))
                    coveredByRange.Add(cell.Ref);
            }
        }

        var nonSink = walked
            .Where(w => !w.Ref.Equals(sink) && !coveredByRange.Contains(w.Ref))
            .ToList();

        // Names: ranges first (in encounter order), then cells (in topo
        // order). This keeps inputs grouped at the top of the LET — ranges
        // are always inputs — which matches the spec's dialog ordering.
        var nameByRef = AssignNames(ranges, nonSink, source);

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
            bindings.Add(new BindingRow(range, BindingRole.Input, name, rhs));
        }

        var stepRows = new List<BindingRow>();
        foreach (var cell in nonSink)
        {
            var cellFormulaRef = new FormulaRef(cell.Ref);
            var name = nameByRef[cellFormulaRef];
            var role = ClassifyRole(cell, nameByRef);
            string rhs;
            if (role == BindingRole.Input)
            {
                // Bare A1 for in-sheet refs, sheet-qualified otherwise,
                // workbook-qualified for externals — DisplayAddress handles
                // the quoting rules so the RHS round-trips cleanly.
                rhs = cell.Ref.DisplayAddress(source.SinkSheet);
            }
            else
            {
                // The step's formula was extracted using its own sheet as
                // the default; the rewrite needs that same default so refs
                // resolve to the same FormulaRef keys the walker built.
                rhs = CellRefExtractor.Rewrite(StripLeadingEquals(cell.Formula!), cell.Ref.Sheet, nameByRef);
            }

            var row = new BindingRow(cellFormulaRef, role, name, rhs);
            if (role == BindingRole.Input)
                bindings.Add(row);
            else
                stepRows.Add(row);
        }

        // Range and cell inputs first, then steps in topo order — matches
        // the spec's dialog ordering and gives a natural read for the
        // synthesised LET.
        bindings.AddRange(stepRows);

        var bodyText = CellRefExtractor.Rewrite(
            StripLeadingEquals(sinkFormula), source.SinkSheet, nameByRef);

        var sb = new StringBuilder();
        sb.Append('=');
        FormulaFormatter.AppendLet(
            sb,
            indent: 0,
            bindings.Select(b => (b.Name, b.Rhs)).ToList(),
            bodyText);

        return new GatherResult(sink, sinkFormula, bindings, sb.ToString());
    }

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
        ICellSource source)
    {
        var nameByRef = new Dictionary<FormulaRef, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fallbackCounter = 0;

        foreach (var range in ranges)
        {
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
            var baseName = LetNameSanitizer.Sanitize(cell.CellAboveText)
                           ?? LetNameSanitizer.Sanitize(cell.CellLeftText);
            nameByRef[new FormulaRef(cell.Ref)] = AssignOne(baseName, used, ref fallbackCounter);
        }

        return nameByRef;
    }

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

    private static BindingRole ClassifyRole(WalkedCell cell, IReadOnlyDictionary<FormulaRef, string> nameByRef)
    {
        // External refs and missing-sheet refs surface here as null-formula
        // cells (the source can't reach them) and so naturally classify as
        // input — exactly what we want.
        if (cell.Formula == null)
            return BindingRole.Input;
        // A formula cell is a step iff at least one precedent has a binding
        // (cell-binding for in-scope cells, range-binding for promoted
        // ranges). Cells whose precedents were all dropped (covered by a
        // range, out of scope, etc.) revert to inputs whose RHS is the
        // cell address — promotion to step is a follow-up in PR 11.
        return cell.Precedents.Any(nameByRef.ContainsKey) ? BindingRole.Step : BindingRole.Input;
    }

    private static string StripLeadingEquals(string formula)
    {
        var trimmed = formula.TrimStart();
        return trimmed.StartsWith('=') ? trimmed[1..] : trimmed;
    }
}
