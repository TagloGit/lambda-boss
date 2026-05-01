using System.Text;
using System.Text.RegularExpressions;

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
    ///     Selection-aware overload (PR 7). When
    ///     <paramref name="selection" /> contains more than one cell, the
    ///     engine first checks the multi-sink rule: if 2+ selected cells
    ///     have no in-scope dependent (i.e. aren't transitively referenced
    ///     by another selected cell), the engine refuses with a
    ///     <see cref="GatherDiagnosticKind.MultipleSinks" /> diagnostic.
    ///     Cycle detection runs whether or not the selection is multi-cell;
    ///     a cycle anywhere in the walked graph surfaces as
    ///     <see cref="GatherDiagnosticKind.Cycle" />. PR 9 will additionally
    ///     use the selection to restrict the walk; for PR 7 the selection
    ///     only feeds the multi-sink check.
    /// </summary>
    public static GatherResult? Gather(CellRef sink, IReadOnlyList<CellRef> selection, ICellSource source)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(source);

        var sinkFormula = source.GetFormula(sink);
        if (sinkFormula == null)
            return null;

        // Multi-sink check uses the cycle-aware walker on each selected
        // cell. If any walk hits a cycle, surface that cycle diagnostic
        // first — cycles are an unconditional refusal, while multi-sink
        // is selection-shape dependent.
        var multiSinkDiagnostic = CheckMultipleSinks(sink, selection, sinkFormula, source);
        if (multiSinkDiagnostic != null)
            return multiSinkDiagnostic;

        var outcome = CellGraphWalker.Walk(sink, source);
        if (outcome.IsCycle)
            return RefusedWithCycle(sink, sinkFormula, outcome.Cycle!);

        var walked = outcome.Cells!;

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

        // The pool of names already taken — outer ranges and outer cells
        // — used as the collision baseline when splicing inner-LET binding
        // names. Inner expansions add to this set as they go so a second
        // nested LET sees the first's names.
        var used = new HashSet<string>(nameByRef.Values, StringComparer.OrdinalIgnoreCase);

        var stepRows = new List<BindingRow>();
        foreach (var cell in nonSink)
        {
            var cellFormulaRef = new FormulaRef(cell.Ref);
            var name = nameByRef[cellFormulaRef];
            var role = ClassifyRole(cell, nameByRef);
            if (role == BindingRole.Input)
            {
                // Bare A1 for in-sheet refs, sheet-qualified otherwise,
                // workbook-qualified for externals — DisplayAddress handles
                // the quoting rules so the RHS round-trips cleanly. A
                // spilling leaf appends '#' so the binding represents the
                // whole array rather than just the anchor cell's value;
                // step-classified cells skip the suffix because their RHS
                // is the rewritten formula (array semantics flow through
                // it naturally).
                var inputRhs = cell.Ref.DisplayAddress(source.SinkSheet);
                if (cell.HasSpill)
                    inputRhs += "#";
                bindings.Add(new BindingRow(cellFormulaRef, BindingRole.Input, name, inputRhs));
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

            stepRows.Add(new BindingRow(cellFormulaRef, BindingRole.Step, name, stepRhs));
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
        // cell address — promotion to step is a follow-up in PR 11. Spill
        // anchors follow the same rule: a spilling cell with in-scope
        // precedents is a step (RHS = rewritten formula, array semantics
        // flow through naturally); a spilling leaf is an input with RHS
        // suffixed by `#` to preserve the dynamic-array binding.
        return cell.Precedents.Any(nameByRef.ContainsKey) ? BindingRole.Step : BindingRole.Input;
    }

    private static string StripLeadingEquals(string formula)
    {
        var trimmed = formula.TrimStart();
        return trimmed.StartsWith('=') ? trimmed[1..] : trimmed;
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
            var role = binding.IsCalculation ? BindingRole.Step : BindingRole.Input;
            innerRows.Add(new BindingRow(hostCell, role, finalName, rhs));
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

    private static readonly Regex BareIdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.]*$",
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
    ///     identified by Excel's name-shape rule (<c>[A-Za-z_][A-Za-z0-9_.]*</c>).
    ///     Strings (<c>"..."</c>) and single-quoted sheet/workbook
    ///     qualifiers (<c>'My Sheet'!</c>) are skipped wholesale. A token
    ///     followed by <c>!</c> is treated as a sheet qualifier and left
    ///     alone — that's a cell-ref position, not a name reference.
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

    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

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
