using System.Text;

namespace LambdaBoss;

/// <summary>
///     The top-level Gather entry point. Walks the precedent graph, names
///     each cell, classifies it as input or step, rewrites step formulas
///     to refer to bindings, and emits a single <c>=LET(...)</c> formula
///     equivalent to the sink-rooted calculation graph. PR 2 scope: PR 1's
///     chain/branched DAG support plus full naming completeness — cell-above
///     fallback to cell-left, sanitization via <see cref="LetNameSanitizer" />,
///     and final-collision suffixing.
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
        var inScope = walked.Select(w => w.Ref).ToHashSet();

        // Name every cell except the sink. Sink contributes the LET body.
        var nonSink = walked.Where(w => !w.Ref.Equals(sink)).ToList();

        var nameByRef = AssignNames(nonSink);

        var bindings = new List<BindingRow>();
        var inputs = new List<BindingRow>();
        var steps = new List<BindingRow>();

        foreach (var cell in nonSink)
        {
            var name = nameByRef[cell.Ref];
            var role = ClassifyRole(cell, inScope);
            string rhs;
            if (role == BindingRole.Input)
            {
                rhs = cell.Ref.A1Address;
            }
            else
            {
                rhs = CellRefExtractor.Rewrite(StripLeadingEquals(cell.Formula!), source.SinkSheet, nameByRef);
            }

            var row = new BindingRow(cell.Ref, role, name, rhs);
            if (role == BindingRole.Input)
                inputs.Add(row);
            else
                steps.Add(row);
        }

        // Inputs first, then steps in topo order — matches the spec's
        // dialog ordering and gives a natural read for the synthesised LET.
        bindings.AddRange(inputs);
        bindings.AddRange(steps);

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
    ///     Picks a binding name for each cell in topological order. Tries
    ///     cell-above first, then cell-left, both routed through
    ///     <see cref="LetNameSanitizer" />. Falls back to <c>step_N</c> only
    ///     when both neighbours yield nothing usable. Final collisions —
    ///     two cells producing the same name — are resolved by suffixing
    ///     <c>_2</c>, <c>_3</c>, … in topological order.
    /// </summary>
    private static Dictionary<CellRef, string> AssignNames(IReadOnlyList<WalkedCell> nonSink)
    {
        var nameByRef = new Dictionary<CellRef, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fallbackCounter = 0;

        foreach (var cell in nonSink)
        {
            var baseName = LetNameSanitizer.Sanitize(cell.CellAboveText)
                           ?? LetNameSanitizer.Sanitize(cell.CellLeftText);

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
            nameByRef[cell.Ref] = finalName;
        }

        return nameByRef;
    }

    private static BindingRole ClassifyRole(WalkedCell cell, HashSet<CellRef> inScope)
    {
        if (cell.Formula == null)
            return BindingRole.Input;
        // A formula cell whose precedents are all out of scope (none of the
        // refs in its formula are themselves walked) is treated as an
        // input — its RHS is the cell ref. That preserves the spec's "the
        // LET binds the cell, not the formula" default; promotion to step
        // is a follow-up in PR 11.
        var hasInScopePrecedent = cell.Precedents.Any(inScope.Contains);
        return hasInScopePrecedent ? BindingRole.Step : BindingRole.Input;
    }

    private static string StripLeadingEquals(string formula)
    {
        var trimmed = formula.TrimStart();
        return trimmed.StartsWith('=') ? trimmed[1..] : trimmed;
    }
}
