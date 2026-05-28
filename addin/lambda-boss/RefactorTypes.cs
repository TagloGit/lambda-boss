namespace LambdaBoss;

/// <summary>
///     Where an input row came from. <see cref="Extracted" /> is a cell ref
///     the walker pulled out of the source formula's body or a calculation
///     binding's RHS. <see cref="ExistingLetValue" /> is a value binding
///     already present in an existing LET — the binding's name and RHS are
///     pre-populated and the row can still be renamed, reordered, dropped,
///     or marked as the survivor of a merge.
/// </summary>
public enum RefactorRowOrigin
{
    Extracted,
    ExistingLetValue
}

/// <summary>
///     Spec 0008 — one row in the dialog's Inputs section. PR 2 widens the
///     PR 1 record: <see cref="Source" /> is now nullable (existing LET
///     value bindings whose RHS isn't a single ref carry only a binding
///     name, not a <see cref="FormulaRef" />); <see cref="Key" /> is the
///     stable identity the dialog uses to pass state back via
///     <see cref="RefactorRowState" />; <see cref="Origin" /> tells the
///     dialog whether to badge the row; <see cref="MergedFrom" /> lists
///     the original binding names that were merged into this row (so the
///     dialog can show a "merged ← b" note next to the survivor).
/// </summary>
public sealed record RefactorInputRow(
    string Key,
    FormulaRef? Source,
    string Name,
    string Rhs,
    RefactorRowOrigin Origin,
    IReadOnlyList<string>? MergedFrom = null);

/// <summary>
///     Spec 0008 / PR 2 — a calculation binding from an existing LET, after
///     the engine has rewritten its RHS to use the final input-binding
///     names. Read-only in the dialog (name + RHS); kept in original
///     source order in the synthesised LET (emitted after all value
///     bindings).
/// </summary>
public sealed record RefactorCalcBindingRow(
    string Name,
    string RewrittenRhs);

/// <summary>
///     User's per-row state passed back into
///     <see cref="RefactorEngine.Recompute" />. PR 2 keys rows by
///     <see cref="Key" /> (the matching <see cref="RefactorInputRow.Key" />)
///     instead of <see cref="FormulaRef" /> so rows without a single-ref
///     source (existing-LET value bindings whose RHS is a literal or named
///     range) can be tracked too. The dialog produces the row order by
///     resequencing this list before the call.
/// </summary>
public sealed record RefactorRowState(
    string Key,
    string Name,
    bool Include = true);

/// <summary>
///     The engine's output. <see cref="OriginalFormula" /> is the formula
///     text passed in (handy for the dialog header). <see cref="Inputs" />
///     is the in-dialog-order list of input rows. <see cref="CalcBindings" />
///     carries the rewritten calculation bindings from an existing LET
///     (empty for non-LET formulas). <see cref="SynthesisedLet" /> is the
///     formatted <c>=LET(...)</c> text ready to write back to the active
///     cell on Save (or the unchanged formula when there's nothing to
///     refactor). <see cref="Diagnostic" /> is non-null when the engine
///     refused (PR 2: <c>MalformedLet</c>); in that case
///     <see cref="Inputs" /> and <see cref="CalcBindings" /> are empty and
///     <see cref="SynthesisedLet" /> is the original formula unchanged.
/// </summary>
public sealed record RefactorResult(
    string OriginalFormula,
    IReadOnlyList<RefactorInputRow> Inputs,
    IReadOnlyList<RefactorCalcBindingRow> CalcBindings,
    string SynthesisedLet,
    RefactorDiagnostic? Diagnostic = null);

/// <summary>
///     A refusal reason. PR 2 emits only <see cref="RefactorDiagnosticKind.MalformedLet" />
///     (PR 1's <c>ExistingLet</c> refusal is gone — the engine now handles
///     existing LETs). Further PRs add new kinds as needed.
/// </summary>
public sealed record RefactorDiagnostic(
    RefactorDiagnosticKind Kind,
    string Message);

public enum RefactorDiagnosticKind
{
    /// <summary>
    ///     The active cell starts with <c>=LET(</c> but <see cref="LetParser.Parse" />
    ///     couldn't tokenise it (unbalanced parens, odd-arg count, invalid
    ///     binding name). The slash command surfaces the message instead of
    ///     opening the dialog.
    /// </summary>
    MalformedLet
}
