using System.Text;

namespace LambdaBoss;

/// <summary>
///     Spec 0008 / PR 1 — the <c>/Refactor</c> entry point. Takes a
///     formula and the active cell's sheet, hoists every cell ref and
///     range into its own LET binding, and emits a tidy
///     <c>=LET(...)</c>. Existing LETs are refused in PR 1 with an
///     <see cref="RefactorDiagnosticKind.ExistingLet" /> diagnostic; PR 2
///     removes that refusal.
///
///     The tracer is intentionally minimal: <see cref="Refactor" />
///     produces an initial pass with auto-named bindings;
///     <see cref="Recompute" /> re-runs with the dialog's per-row state
///     so the user can rename, drop, and reorder.
/// </summary>
public static class RefactorEngine
{
    /// <summary>
    ///     Runs the initial refactor over <paramref name="formula" />,
    ///     assigning each extracted ref an auto-name in first-seen
    ///     order (<c>input1</c>, <c>input2</c>, …). <paramref name="activeSheet" />
    ///     is the sheet the formula lives on; unqualified refs resolve
    ///     against it and in-sheet refs render bare in binding RHSes.
    /// </summary>
    public static RefactorResult Refactor(string formula, string activeSheet)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));
        if (activeSheet is null) throw new ArgumentNullException(nameof(activeSheet));

        if (LetParser.IsLetFormula(formula))
        {
            return new RefactorResult(
                formula,
                Array.Empty<RefactorInputRow>(),
                formula,
                new RefactorDiagnostic(
                    RefactorDiagnosticKind.ExistingLet,
                    "Refactor on existing LET formulas is coming in PR 2 (spec 0008)."));
        }

        var extracted = CellRefExtractor.Extract(formula, activeSheet);
        var rows = new List<RefactorInputRow>(extracted.Count);
        var nameIndex = 1;
        foreach (var r in extracted)
        {
            var rhs = r.DisplayAddress(activeSheet);
            rows.Add(new RefactorInputRow(r, $"input{nameIndex}", rhs));
            nameIndex++;
        }
        var let = Synthesise(formula, activeSheet, rows);
        return new RefactorResult(formula, rows, let);
    }

    /// <summary>
    ///     Re-runs the refactor with the dialog's per-row state.
    ///     <paramref name="rowStates" /> is the full set of rows in
    ///     user-chosen order; rows with <see cref="RefactorRowState.Include" />
    ///     = false are dropped from the LET (their tokens stay in the
    ///     rewritten body as-is). The engine trusts the dialog's name
    ///     validation: collisions and invalid names get passed through
    ///     verbatim so the user's typed text round-trips.
    /// </summary>
    public static RefactorResult Recompute(
        string formula,
        string activeSheet,
        IReadOnlyList<RefactorRowState> rowStates)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));
        if (activeSheet is null) throw new ArgumentNullException(nameof(activeSheet));
        if (rowStates is null) throw new ArgumentNullException(nameof(rowStates));

        if (LetParser.IsLetFormula(formula))
        {
            return new RefactorResult(
                formula,
                Array.Empty<RefactorInputRow>(),
                formula,
                new RefactorDiagnostic(
                    RefactorDiagnosticKind.ExistingLet,
                    "Refactor on existing LET formulas is coming in PR 2 (spec 0008)."));
        }

        var rows = new List<RefactorInputRow>(rowStates.Count);
        foreach (var rs in rowStates)
        {
            if (!rs.Include) continue;
            var rhs = rs.Source.DisplayAddress(activeSheet);
            rows.Add(new RefactorInputRow(rs.Source, rs.Name, rhs));
        }

        var let = Synthesise(formula, activeSheet, rows);
        return new RefactorResult(formula, rows, let);
    }

    private static string Synthesise(
        string formula,
        string activeSheet,
        IReadOnlyList<RefactorInputRow> rows)
    {
        if (rows.Count == 0)
        {
            // No refs at all (or all dropped) — return the formula
            // verbatim. The dialog can still open on an empty input set
            // so the user sees that there's nothing to extract.
            return formula;
        }

        var lookup = new Dictionary<FormulaRef, string>(rows.Count);
        foreach (var row in rows)
            lookup[row.Source] = row.Name;

        var body = CellRefExtractor.Rewrite(
            StripLeadingEquals(formula), activeSheet, lookup);

        var bindings = rows
            .Select(r => (r.Name, Value: r.Source.DisplayAddress(activeSheet)))
            .ToList();

        var sb = new StringBuilder();
        sb.Append('=');
        FormulaFormatter.AppendLet(sb, indent: 0, bindings, body);
        return sb.ToString();
    }

    private static string StripLeadingEquals(string formula)
    {
        var trimmed = formula.TrimStart();
        return trimmed.StartsWith("=", StringComparison.Ordinal) ? trimmed[1..] : trimmed;
    }
}
