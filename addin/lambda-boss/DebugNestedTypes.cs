namespace LambdaBoss;

/// <summary>
///     Spec 0010 (spike) — issue #279. One <c>LAMBDA(...)</c> scope discovered
///     inside a formula. <c>/Unnest</c> answers "show me the steps of a static
///     nested formula"; <c>/Debug Nested</c> answers the different question
///     "inside <c>BYROW(arr, LAMBDA(r, …))</c> the body runs once per element
///     with <c>r</c> bound dynamically — what does each step compute?". A scope
///     is the unit the user pins to a concrete example and watches.
///
///     <para>
///     <see cref="Params" /> are the names this lambda binds; <see cref="EnclosingParams" />
///     are the names bound by ancestor lambda scopes that are still in scope
///     inside this body (outer-first). <see cref="HostFunction" /> is the call
///     that receives this lambda as an argument (<c>BYROW</c>, <c>MAP</c>, a
///     custom <c>PAIROP</c>, …) — it determines whether a sensible default pin
///     can be suggested. <see cref="Depth" /> (0 = outermost lambda) drives the
///     dialog's indentation.
///     </para>
/// </summary>
public sealed record DebugScope(
    string Key,
    int Depth,
    string HostFunction,
    IReadOnlyList<string> Params,
    IReadOnlyList<string> EnclosingParams,
    string BodyText,
    string Label);

/// <summary>
///     The complete, outer-first list of parameters in scope inside a
///     <see cref="DebugScope" />'s body (<see cref="DebugScope.EnclosingParams" />
///     followed by <see cref="DebugScope.Params" />). Each must be pinned to a
///     concrete example value for the body's steps to evaluate.
/// </summary>
public sealed record DebugPin(string Param, string Expression);

/// <summary>
///     One inspectable step of a pinned scope's body: the <see cref="UnnestEngine" />
///     decomposition (<see cref="Name" /> / <see cref="Rhs" />) plus an
///     <see cref="EvaluableFormula" /> — a self-contained <c>=LET(...)</c> that
///     binds every pin and every preceding step, then returns this step — so the
///     add-in can compute its live value for the pinned example (mechanism C).
/// </summary>
public sealed record DebugStep(
    string Key,
    string Name,
    string Rhs,
    string EvaluableFormula);

/// <summary>
///     The watch model for one pinned <see cref="DebugScope" />: its body's
///     decomposed <see cref="Steps" /> (leaf-first) and a
///     <see cref="FinalEvaluableFormula" /> for the body's overall result. When
///     the body can't be decomposed, <see cref="Diagnostic" /> is set and
///     <see cref="Steps" /> is empty.
/// </summary>
public sealed record DebugWatch(
    string ScopeKey,
    IReadOnlyList<DebugStep> Steps,
    string FinalName,
    string FinalEvaluableFormula,
    DebugDiagnostic? Diagnostic = null);

/// <summary>
///     Result of scanning a formula for lambda scopes. <see cref="Scopes" /> is
///     the flattened, pre-order (outer-first) list; <see cref="Diagnostic" /> is
///     set (and <see cref="Scopes" /> empty) when the formula can't be parsed or
///     holds no lambda to debug.
/// </summary>
public sealed record DebugDiscovery(
    IReadOnlyList<DebugScope> Scopes,
    DebugDiagnostic? Diagnostic = null);

/// <summary>
///     Spec 0010 (spike) — how a free name referenced by a lambda body is
///     classified, which decides how it's supplied on the scratch sheet:
///     <see cref="Param" />/<see cref="EnclosingParam" /> need a sample value
///     (live slice or probe-captured), a <see cref="LetBinding" /> is
///     reconstructed from its real definition as a sheet-scoped name, and an
///     <see cref="External" /> name (table column, workbook name, cell ref)
///     resolves on its own (modulo sheet-local ref qualification).
/// </summary>
public enum DebugInputKind
{
    /// <summary>A parameter bound by the scope's own lambda.</summary>
    Param,

    /// <summary>A parameter bound by an enclosing lambda.</summary>
    EnclosingParam,

    /// <summary>A binding from an enclosing <c>LET</c> (its definition is reconstructable).</summary>
    LetBinding,

    /// <summary>Anything else — a table ref, workbook name, or cell reference.</summary>
    External
}

/// <summary>
///     A free name referenced by a lambda body, classified by
///     <see cref="DebugInputKind" />. <see cref="Definition" /> is the enclosing
///     <c>LET</c> binding's RHS text for a <see cref="DebugInputKind.LetBinding" />
///     (so it can be rebuilt on the scratch sheet); null otherwise.
/// </summary>
public sealed record DebugInput(string Name, DebugInputKind Kind, string? Definition);

/// <summary>
///     The computed value of a step's <see cref="DebugStep.EvaluableFormula" />
///     for the pinned example, as Excel rendered it (mechanism C). <see cref="Display" />
///     is the formatted text — a scalar, an Excel error string (<c>#VALUE!</c>,
///     <c>#NAME?</c>, …), or a compact preview of a spilled array;
///     <see cref="IsError" /> drives the dialog's error styling.
/// </summary>
public sealed record DebugValue(string Display, bool IsError);

/// <summary>A refusal reason surfaced by the <c>/Debug Nested</c> command instead of the dialog.</summary>
public sealed record DebugDiagnostic(DebugDiagnosticKind Kind, string Message);

public enum DebugDiagnosticKind
{
    /// <summary>The formula couldn't be parsed into an expression tree.</summary>
    MalformedFormula,

    /// <summary>The formula parsed but contains no <c>LAMBDA(...)</c> to debug.</summary>
    NoLambda,

    /// <summary>The selected scope's body couldn't be decomposed for inspection.</summary>
    MalformedBody
}
