namespace LambdaBoss;

/// <summary>
///     Where an input row came from. <see cref="Extracted" /> is a cell ref
///     the walker pulled out of the source formula's body or a calculation
///     binding's RHS. <see cref="ExistingLetValue" /> is a value binding
///     already present in an existing LET — the binding's name and RHS are
///     pre-populated and the row can still be renamed, reordered, dropped,
///     or marked as the survivor of a merge. PR 3 adds
///     <see cref="PromotedNamedRange" /> and <see cref="PromotedExternalRef" />:
///     rows that were originally promotable but the user (or the dialog's
///     initial state) has promoted into the inputs section. PR 4 adds
///     <see cref="PromotedLiteral" /> for promoted numeric / string /
///     boolean literals. The dialog uses the badge to communicate the
///     row's provenance.
/// </summary>
public enum RefactorRowOrigin
{
    Extracted,
    ExistingLetValue,
    PromotedNamedRange,
    PromotedExternalRef,
    PromotedLiteral
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
///     Spec 0008 / PR 3 — a candidate identifier or external ref that the
///     engine found in the formula but didn't promote to an input binding
///     by default. <see cref="Kind" /> tells the dialog whether the row
///     represents a workbook/worksheet defined name or an external-workbook
///     ref. <see cref="Token" /> is the user-facing label (the identifier
///     text for named ranges, the host-relative display address for
///     external refs). <see cref="Occurrences" /> is the number of times
///     the token appears in the source formula — handy for the author
///     deciding whether promoting it saves work. When the user toggles
///     Promote on, the dialog passes the row's <see cref="Key" /> back to
///     <see cref="RefactorEngine.Recompute" /> via
///     <see cref="RefactorRowState" /> (the engine recognises the
///     <c>extref:</c> / <c>named:</c> key prefix and materialises the row
///     as an input binding with the requested name).
/// </summary>
public sealed record RefactorPromotableRow(
    string Key,
    RefactorPromotableKind Kind,
    string Token,
    int Occurrences);

public enum RefactorPromotableKind
{
    /// <summary>A workbook- or worksheet-scoped defined name (not a LAMBDA).</summary>
    NamedRange,

    /// <summary>An external-workbook ref (<c>[Wb.xlsx]Sheet!A1</c>, etc.).</summary>
    ExternalRef,

    /// <summary>
    ///     A numeric, string, or boolean literal (spec 0008 / PR 4). The
    ///     <see cref="RefactorPromotableRow.Token" /> is the original text of
    ///     the first occurrence (preserving formatting like <c>0.20</c>);
    ///     occurrences dedupe by parsed value, not spelling. Promoting it
    ///     replaces every occurrence with the binding name.
    /// </summary>
    Literal
}

/// <summary>
///     User's per-row state passed back into
///     <see cref="RefactorEngine.Recompute" />. PR 2 keys rows by
///     <see cref="Key" /> (the matching <see cref="RefactorInputRow.Key" />
///     or <see cref="RefactorPromotableRow.Key" />) instead of
///     <see cref="FormulaRef" /> so rows without a single-ref source
///     (existing-LET value bindings whose RHS is a literal or named range)
///     can be tracked too. PR 3 reuses the same record for promoted
///     promotables — the dialog adds the promotable's Key to this list
///     when the user toggles Promote on, with an auto-allocated or
///     user-edited Name. The dialog produces the row order by resequencing
///     this list before the call.
/// </summary>
public sealed record RefactorRowState(
    string Key,
    string Name,
    bool Include = true);

/// <summary>
///     The engine's output. <see cref="OriginalFormula" /> is the formula
///     text passed in (handy for the dialog header). <see cref="Inputs" />
///     is the in-dialog-order list of input rows. <see cref="Promotables" />
///     is the list of un-promoted candidates (named ranges, external refs)
///     the dialog shows in its "Promote to input" section; promoted rows
///     materialise as <see cref="RefactorInputRow" /> in
///     <see cref="Inputs" /> and DON'T appear here.
///     <see cref="CalcBindings" /> carries the rewritten calculation
///     bindings from an existing LET (empty for non-LET formulas).
///     <see cref="SynthesisedLet" /> is the formatted <c>=LET(...)</c>
///     text ready to write back to the active cell on Save (or the
///     unchanged formula when there's nothing to refactor).
///     <see cref="Diagnostic" /> is non-null when the engine refused (PR 2:
///     <c>MalformedLet</c>); in that case <see cref="Inputs" />,
///     <see cref="Promotables" />, and <see cref="CalcBindings" /> are
///     empty and <see cref="SynthesisedLet" /> is the original formula
///     unchanged.
/// </summary>
public sealed record RefactorResult(
    string OriginalFormula,
    IReadOnlyList<RefactorInputRow> Inputs,
    IReadOnlyList<RefactorCalcBindingRow> CalcBindings,
    string SynthesisedLet,
    IReadOnlyList<RefactorPromotableRow> Promotables,
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

/// <summary>
///     Spec 0008 / PR 3 — workbook-name lookup the engine consults when
///     deciding which bare identifiers in the formula are candidate
///     defined-name references (and which should be excluded as LAMBDA
///     names). The live adapter in <c>RefactorCommand</c> populates the
///     dictionary once from <c>workbook.Names</c> AND the active sheet's
///     <c>worksheet.Names</c>, unioned with case-insensitive collation.
///     <see cref="WorkbookNames" /> maps name → <c>RefersTo</c> text;
///     entries whose <c>RefersTo</c> starts with <c>=LAMBDA(</c> are
///     filtered out by the engine via
///     <see cref="LambdaSignatureParser.IsLambdaFormula" />. Tests pass an
///     in-memory stub; callers that don't need promotable named ranges
///     (e.g. PR 1's tracer tests) pass null.
/// </summary>
public interface IWorkbookContext
{
    IReadOnlyDictionary<string, string> WorkbookNames { get; }
}
