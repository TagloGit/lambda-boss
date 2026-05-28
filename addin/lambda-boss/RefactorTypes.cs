namespace LambdaBoss;

/// <summary>
///     Spec 0008 / PR 1 — types shared between <see cref="RefactorEngine" />
///     and the <c>/Refactor</c> slash command. The tracer slice only needs
///     the input-row and result records; promotable-row and calc-binding
///     records arrive in PR 2/3.
/// </summary>
public sealed record RefactorInputRow(
    FormulaRef Source,
    string Name,
    string Rhs);

/// <summary>
///     User's per-row state passed back into
///     <see cref="RefactorEngine.Recompute" />. PR 1 carries just the
///     binding name and Include flag — reorder happens dialog-side by
///     resequencing the list before the call. <see cref="Source" />
///     identifies the row so the engine can map it back onto the
///     extracted refs (which the engine re-derives from the formula on
///     every recompute).
/// </summary>
public sealed record RefactorRowState(
    FormulaRef Source,
    string Name,
    bool Include = true);

/// <summary>
///     The engine's output. <see cref="OriginalFormula" /> is the formula
///     text passed in (handy for the dialog header). <see cref="Inputs" />
///     is the in-dialog-order list of input rows; PR 1 always emits them
///     in first-seen ref order on the initial call.
///     <see cref="SynthesisedLet" /> is the formatted <c>=LET(...)</c>
///     text ready to write back to the active cell on Save (or the
///     unchanged formula when every row was dropped).
///     <see cref="Diagnostic" /> is non-null when the engine refused
///     (PR 1: <c>ExistingLet</c>); in that case <see cref="Inputs" /> is
///     empty and <see cref="SynthesisedLet" /> is the original formula
///     unchanged.
/// </summary>
public sealed record RefactorResult(
    string OriginalFormula,
    IReadOnlyList<RefactorInputRow> Inputs,
    string SynthesisedLet,
    RefactorDiagnostic? Diagnostic = null);

/// <summary>
///     A refusal reason. PR 1 only emits <see cref="RefactorDiagnosticKind.ExistingLet" />.
///     PR 2 removes that case; further PRs add new kinds as needed
///     (e.g. malformed formula).
/// </summary>
public sealed record RefactorDiagnostic(
    RefactorDiagnosticKind Kind,
    string Message);

public enum RefactorDiagnosticKind
{
    /// <summary>
    ///     The active cell already holds a <c>=LET(...)</c>. PR 1 refuses
    ///     and instructs the user to wait for PR 2.
    /// </summary>
    ExistingLet
}
