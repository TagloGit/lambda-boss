namespace LambdaBoss;

/// <summary>
///     Spec 0009 — what kind of AST node a step was extracted from, used by the
///     dialog to badge each row ("function: SUMSQ" / "operator: −").
/// </summary>
public enum UnnestStepOrigin
{
    /// <summary>A function-call node — e.g. <c>SUMSQ(...)</c>, <c>XLOOKUP(...)</c>.</summary>
    Function,

    /// <summary>A binary-operator node — e.g. <c>a - b</c>, <c>x * 100</c>, <c>p &amp; q</c>.</summary>
    Operator
}

/// <summary>
///     Spec 0009 — one extracted step in the decomposition. Rows are returned
///     leaf-first (each step precedes every use of it). <see cref="Key" /> is
///     the stable identity the dialog echoes back via <see cref="UnnestRowState" />
///     (the leaf-first ordinal of the step candidate — invariant across
///     <see cref="UnnestEngine.Recompute" /> because the source formula text
///     doesn't change). <see cref="Name" /> is the current (auto- or user-)
///     assigned binding name; <see cref="Rhs" /> is the node's expression with
///     every included child step already substituted to its step name;
///     <see cref="Origin" /> / <see cref="OriginLabel" /> drive the badge; and
///     <see cref="Include" /> is the per-row toggle (default on — when off the
///     step is inlined back into its parent and its children re-parent to the
///     grandparent, so the row is reported but emits no binding).
/// </summary>
public sealed record UnnestStepRow(
    string Key,
    string Name,
    string Rhs,
    UnnestStepOrigin Origin,
    string OriginLabel,
    bool Include);

/// <summary>
///     User's per-row state passed back into <see cref="UnnestEngine.Recompute" />.
///     <see cref="Key" /> matches a <see cref="UnnestStepRow.Key" />;
///     <see cref="Name" /> is the (possibly user-edited) binding name — when
///     empty the engine re-auto-names the row; <see cref="Include" /> toggles
///     whether the step is emitted as a binding or inlined back into its parent.
/// </summary>
public sealed record UnnestRowState(
    string Key,
    string Name,
    bool Include = true);

/// <summary>
///     The engine's output. <see cref="OriginalFormula" /> is the formula text
///     passed in (handy for the dialog header). <see cref="Steps" /> is the
///     leaf-first list of extracted steps. <see cref="SynthesisedLet" /> is the
///     formatted <c>=LET(...)</c> ready to write back to the active cell on Save
///     — or the original formula unchanged when there are no included steps
///     (a no-op rewrite). <see cref="Diagnostic" /> is non-null when the engine
///     refused; in that case <see cref="Steps" /> is empty and
///     <see cref="SynthesisedLet" /> is the original formula unchanged.
/// </summary>
public sealed record UnnestResult(
    string OriginalFormula,
    IReadOnlyList<UnnestStepRow> Steps,
    string SynthesisedLet,
    UnnestDiagnostic? Diagnostic = null);

/// <summary>A refusal reason — the slash command surfaces the message instead of opening the dialog.</summary>
public sealed record UnnestDiagnostic(
    UnnestDiagnosticKind Kind,
    string Message);

public enum UnnestDiagnosticKind
{
    /// <summary>
    ///     The formula couldn't be parsed into an expression tree
    ///     (unexpected token, unbalanced brackets, …).
    /// </summary>
    MalformedFormula,

    /// <summary>
    ///     The active cell holds a <c>=LET(...)</c> whose top-level structure
    ///     couldn't be parsed by <see cref="LetParser" /> (wrong argument
    ///     count, unbalanced parens, an invalid binding name, …). The engine
    ///     refuses rather than attempt an explosion over a malformed LET — the
    ///     command surfaces the message instead of opening the dialog, matching
    ///     <c>ConvertLetToLambdaCommand</c>'s wording.
    /// </summary>
    MalformedLet
}
