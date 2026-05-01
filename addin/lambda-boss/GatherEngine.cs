using System.Text;
using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     The top-level Gather entry point. Walks the precedent graph, names
///     each cell, classifies it as input or step, rewrites step formulas
///     to refer to bindings, and emits a single <c>=LET(...)</c> formula
///     equivalent to the sink-rooted calculation graph. PR 1 scope: chain
///     and branched DAGs of formula cells on a single sheet, with no
///     ranges, spills, nested LETs, or cycle handling.
/// </summary>
public static class GatherEngine
{
    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.]*$",
        RegexOptions.CultureInvariant);

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

        var nameByRef = new Dictionary<CellRef, string>();
        var fallbackCounter = 0;
        foreach (var cell in nonSink)
        {
            string name;
            if (cell.CellAboveText != null && IdentifierPattern.IsMatch(cell.CellAboveText))
            {
                name = cell.CellAboveText;
            }
            else
            {
                fallbackCounter++;
                name = $"step_{fallbackCounter}";
            }
            nameByRef[cell.Ref] = name;
        }

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
