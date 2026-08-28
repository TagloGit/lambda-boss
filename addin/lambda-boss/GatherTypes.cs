namespace LambdaBoss;

/// <summary>
///     A workbook cell address. PR 3 adds cross-sheet and external-workbook
///     support: <see cref="Sheet" /> identifies the worksheet (any sheet in
///     the active workbook for in-scope refs); <see cref="ExternalWorkbook" />
///     is non-null for refs into another open or closed workbook (treated as
///     leaves by the walker). Equality is case-insensitive on
///     <see cref="Sheet" /> and <see cref="ExternalWorkbook" /> to match
///     Excel's own name resolution.
/// </summary>
public sealed record CellRef(string Sheet, int Column, int Row, string? ExternalWorkbook = null)
{
    /// <summary>The unqualified A1-style address, e.g. <c>A1</c>, <c>BC27</c>.</summary>
    public string A1Address => $"{ColumnLetters(Column)}{Row}";

    /// <summary>True for refs into an external workbook.</summary>
    public bool IsExternal => ExternalWorkbook != null;

    public bool Equals(CellRef? other)
    {
        return other is not null
               && string.Equals(Sheet, other.Sheet, StringComparison.OrdinalIgnoreCase)
               && Column == other.Column
               && Row == other.Row
               && string.Equals(ExternalWorkbook, other.ExternalWorkbook, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The form to emit as a LET binding RHS when the LET lives on
    ///     <paramref name="hostSheet" />. In-sheet refs render bare;
    ///     cross-sheet refs are sheet-qualified and quoted only if the sheet
    ///     name needs quoting; external refs always include the workbook tag.
    /// </summary>
    public string DisplayAddress(string hostSheet)
    {
        if (IsExternal)
        {
            var wb = ExternalWorkbook!;
            // If either the workbook or sheet needs quoting, the entire
            // qualifier goes inside one set of single quotes around
            // [Wb]Sheet, with embedded quotes doubled.
            if (NeedsQuotes(Sheet) || NeedsQuotes(wb))
                return $"'[{wb}]{EscapeQuotes(Sheet)}'!{A1Address}";
            return $"[{wb}]{Sheet}!{A1Address}";
        }

        if (string.Equals(Sheet, hostSheet, StringComparison.OrdinalIgnoreCase))
            return A1Address;
        if (NeedsQuotes(Sheet))
            return $"'{EscapeQuotes(Sheet)}'!{A1Address}";
        return $"{Sheet}!{A1Address}";
    }

    public override string ToString()
    {
        return A1Address;
    }

    public override int GetHashCode()
    {
        // Microsoft.Bcl.HashCode on net48 calls StringComparer.GetHashCode
        // directly, which throws on null where .NET 6's built-in HashCode
        // tolerates it. Sheet is annotated non-nullable so we trust the
        // contract; ExternalWorkbook is genuinely nullable, so we coalesce
        // to "" before hashing.
        var hash = new HashCode();
        hash.Add(Sheet, StringComparer.OrdinalIgnoreCase);
        hash.Add(Column);
        hash.Add(Row);
        hash.Add(ExternalWorkbook ?? "", StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }

    internal static string ColumnLetters(int column)
    {
        if (column < 1)
            throw new ArgumentOutOfRangeException(nameof(column), "Column must be 1-based.");
        var letters = "";
        var n = column;
        while (n > 0)
        {
            n--;
            letters = (char)('A' + n % 26) + letters;
            n /= 26;
        }

        return letters;
    }

    internal static int LettersToColumn(string letters)
    {
        if (string.IsNullOrEmpty(letters))
            throw new ArgumentException("Letters must be non-empty.", nameof(letters));
        var col = 0;
        foreach (var c in letters)
        {
            if (!char.IsLetter(c))
                throw new ArgumentException($"Invalid column letter: '{c}'.", nameof(letters));
            col = col * 26 + (char.ToUpperInvariant(c) - 'A') + 1;
        }

        return col;
    }

    /// <summary>
    ///     Excel allows unquoted sheet/workbook qualifiers when they look
    ///     like an identifier (letters/digits/underscore/dot, not starting
    ///     with a digit). The dot exemption matters for workbook names with
    ///     extensions (<c>Other.xlsx</c>) and for sheet names like
    ///     <c>Data.v2</c> that authors sometimes write — we'd be otherwise
    ///     adding noise quotes around perfectly valid bare qualifiers.
    /// </summary>
    private static bool NeedsQuotes(string name)
    {
        if (string.IsNullOrEmpty(name))
            return true;
        if (char.IsDigit(name[0]))
            return true;
        foreach (var c in name)
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.')
                return true;
        return false;
    }

    private static string EscapeQuotes(string name)
    {
        return name.Replace("'", "''");
    }
}

/// <summary>
///     A reference appearing inside a formula — either a single cell
///     (<see cref="End" /> null) or a contiguous range (<see cref="End" />
///     set, on the same sheet as <see cref="Start" />). The walker tracks
///     range refs without recursing into their constituent cells; the engine
///     promotes each unique range to a single leaf input and drops walked
///     cells covered by any promoted range. Equality follows
///     <see cref="CellRef" />'s case-insensitive sheet rule, plus the
///     <see cref="IsSpilled" /> flag so <c>A1</c> and <c>A1#</c> dedupe
///     as distinct refs — what spec 0008 (<c>/Refactor</c>) needs. Range
///     refs always have <see cref="IsSpilled" /> = false (Excel has no
///     <c>A1:B5#</c> syntax). <see cref="CellRefExtractor.Rewrite" />
///     transparently falls back from a spilled key to its non-spilled
///     equivalent when the spilled key isn't in the lookup, so
///     <c>/Gather</c> — which only ever registers non-spilled keys —
///     keeps its PR 5 behaviour of collapsing <c>A1#</c> tokens to the
///     anchor cell's binding name.
/// </summary>
public sealed record FormulaRef(CellRef Start, CellRef? End = null, bool IsSpilled = false)
{
    public bool IsRange => End is not null;

    /// <summary>
    ///     The convenience accessor most callers want; for ranges it returns
    ///     <c>Start:End</c> so the value still uniquely identifies the ref.
    ///     Does NOT include the spill <c>#</c> suffix — call
    ///     <see cref="DisplayAddress" /> for the LET-binding render form.
    /// </summary>
    public string A1Address => IsRange ? $"{Start.A1Address}:{End!.A1Address}" : Start.A1Address;

    /// <summary>The host sheet (always shared between Start and End for ranges).</summary>
    public string Sheet => Start.Sheet;

    public bool IsExternal => Start.IsExternal;

    public string? ExternalWorkbook => Start.ExternalWorkbook;

    /// <summary>
    ///     True if this range covers <paramref name="cell" /> — used by the
    ///     engine to drop walked cells that fall inside a promoted range.
    ///     Always false for single-cell refs.
    /// </summary>
    public bool Covers(CellRef cell)
    {
        if (!IsRange)
            return false;
        if (!string.Equals(Start.Sheet, cell.Sheet, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(Start.ExternalWorkbook, cell.ExternalWorkbook, StringComparison.OrdinalIgnoreCase))
            return false;
        return cell.Column >= Start.Column && cell.Column <= End!.Column
                                           && cell.Row >= Start.Row && cell.Row <= End.Row;
    }

    /// <summary>
    ///     The form to emit as a LET binding RHS when the LET lives on
    ///     <paramref name="hostSheet" />. For ranges, the sheet qualifier is
    ///     emitted once on Start; the End cell's address is rendered bare.
    ///     Single-cell refs flagged as <see cref="IsSpilled" /> append a
    ///     trailing <c>#</c> so the binding represents the whole spilled
    ///     array rather than just the anchor's value.
    /// </summary>
    public string DisplayAddress(string hostSheet)
    {
        if (IsRange)
            return $"{Start.DisplayAddress(hostSheet)}:{End!.A1Address}";
        var addr = Start.DisplayAddress(hostSheet);
        return IsSpilled ? addr + "#" : addr;
    }

    public bool Equals(FormulaRef? other)
    {
        return other is not null
               && Start.Equals(other.Start)
               && Equals(End, other.End)
               && IsSpilled == other.IsSpilled;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Start);
        hash.Add(End);
        hash.Add(IsSpilled);
        return hash.ToHashCode();
    }
}

/// <summary>
///     A cell discovered by <see cref="CellGraphWalker" />. The
///     <see cref="Formula" /> is null when the cell holds a literal value,
///     is unreachable (external workbook ref, missing sheet), or has been
///     leaf-restricted by the selection (PR 9) or demoted by the user (PR
///     11) — all treated as leaf inputs in the immediate walk.
///     <see cref="HasSourceFormula" /> reports the underlying source state
///     independent of restriction or demotion: true iff the cell has a
///     formula in the workbook. The engine uses it (PR 11) to flag cells
///     that can't be promoted to step (no formula → no RHS to bake) so
///     the dialog can hide the role toggle. <see cref="CellAboveText" />
///     and <see cref="CellLeftText" /> are the text of the cells directly
///     above and to the left on the cell's own sheet, null when out of
///     range, empty, or non-string. <see cref="Precedents" /> mixes
///     single-cell and range refs; the walker only recurses into
///     single-cell precedents. <see cref="HasSpill" /> is true when the
///     cell is the <em>anchor</em> of a dynamic-array spill — i.e. when
///     <see cref="ICellSource.GetSpill" /> reports the cell as its own
///     anchor, so the cell's formula spills into a range. The
///     engine uses it only to suffix <c>#</c> on the RHS of a spilling
///     input so the binding represents the whole array; spilling cells
///     with in-scope precedents are still steps whose RHS is the
///     rewritten formula.
/// </summary>
public sealed record WalkedCell(
    CellRef Ref,
    string? Formula,
    string? CellAboveText,
    string? CellLeftText,
    IReadOnlyList<FormulaRef> Precedents,
    bool HasSpill = false,
    bool HasSourceFormula = false);

/// <summary>
///     A finalised LET binding row. The <see cref="Source" /> is a single
///     cell for cell-derived bindings and a range for range-promoted
///     leaves (PR 4). PR 10 adds <see cref="IsExpansion" />: rows produced
///     by inlining an inner <c>=LET(...)</c> share their host cell's
///     <see cref="Source" />, so they're indistinguishable from the host's
///     own row by source alone — this flag lets the dialog hide the
///     Include checkbox on the inner rows (the user toggles the host
///     cell, and its inner rows follow). PR 11 adds
///     <see cref="CanToggleRole" />: false for ranges (no formula to
///     bake), inner-LET expansions (host owns the role), and inputs whose
///     underlying cell has no formula in the source (literal value);
///     true for everything else, where the dialog renders the
///     promote/demote toggle.
///
///     Spec 0010 adds <see cref="SliceOf" />: non-null on <em>slice</em>
///     rows — a reference that landed inside a spill and was rewritten as a
///     named slice of the anchor's binding (<c>INDEX(arr,r,c)</c> and, from
///     PR 4, <c>TAKE</c>/<c>DROP</c> forms). The value is the anchor's own
///     row <see cref="Source" />, which identifies the parent for the
///     dialog's Include cascading. Slice rows are always
///     <see cref="BindingRole.Input" /> with <see cref="CanToggleRole" />
///     false — there is no formula to bake, so demotion is meaningless.
///
///     A spilling anchor's own row carries a <see cref="Source" /> whose
///     <see cref="FormulaRef.IsSpilled" /> is true: the binding <em>is</em>
///     the array (<c>A1#</c>), which is what makes it distinct from a slice
///     row for the scalar reference <c>A1</c> to the same cell.
/// </summary>
public sealed record BindingRow(
    FormulaRef Source,
    BindingRole Role,
    string Name,
    string Rhs,
    bool IsExpansion = false,
    bool CanToggleRole = false,
    FormulaRef? SliceOf = null);

/// <summary>
///     Per-row state passed to <see cref="GatherEngine.Recompute" /> by the
///     dialog. Carries the binding's <see cref="Source" /> — the ref the
///     row represents — plus the user's Include choice (PR 10), an
///     optional role override (PR 11), and an optional name override
///     (PR 12).
///
///     A row state with <see cref="Include" /> = false drops the matching
///     ref from the LET: the binding disappears, any precedents reachable
///     only via the dropped cell also drop, and references to the dropped
///     cell in any calling step's formula stay as literal cell-refs (the
///     <see cref="CellRefExtractor.Rewrite" /> path leaves unmapped refs
///     untouched, which is exactly the spec behaviour).
///
///     <see cref="RoleOverride" /> forces the row's classification when
///     non-null, overriding the engine's natural input/step decision.
///     <c>Step</c> on a cell that would naturally be an input promotes it:
///     the cell's formula becomes the binding's RHS (with in-scope refs
///     rewritten) and any precedents the formula references are pulled
///     into the walk — including ones that were previously leaf-restricted
///     by the selection. <c>Input</c> on a cell that would naturally be a
///     step demotes it: the binding's RHS becomes the cell's address and
///     the walker stops descending into the cell's precedents, so any
///     precedents that were only reachable through this cell drop as
///     orphans. Range refs and the sink itself ignore the override
///     (ranges have no formula to bake; the sink is the LET body, not a
///     binding row). Cells without a formula in the source ignore a
///     <c>Step</c> override defensively — there's no formula to render
///     as the RHS, so the dialog hides the toggle in those rows.
///
///     <see cref="NameOverride" /> replaces the engine's auto-derived
///     binding name with the user's chosen one. Overrides are claimed
///     before auto-derivation runs, so an auto-derived name that would
///     have collided with the override suffixes around it (<c>x_2</c>)
///     instead of the other way around. The dialog enforces that
///     overrides are valid identifiers and don't collide with each
///     other before passing them to <see cref="GatherEngine.Recompute" />,
///     so the engine accepts them as authoritative — passing a
///     collision-causing override produces an invalid LET (garbage in,
///     garbage out).
/// </summary>
public sealed record RowState(
    FormulaRef Source,
    bool Include = true,
    BindingRole? RoleOverride = null,
    string? NameOverride = null);

public enum BindingRole
{
    Input,
    Step
}

/// <summary>
///     The output of <see cref="GatherEngine" />: enough state to render
///     the dialog and write the synthesised LET back to the sink on Save.
///     <see cref="Diagnostic" /> is non-null when the engine refused (PR 7+ —
///     cycle in the precedent graph or multi-sink selection); in that case
///     <see cref="Bindings" /> is empty and <see cref="SynthesisedLet" /> is
///     empty too. Callers must check <see cref="Diagnostic" /> before using
///     the rest of the result. PR 9 adds <see cref="WalkedCount" /> and
///     <see cref="FreeWalkCount" /> so the dialog can render the header
///     hint: <c>WalkedCount</c> ("M") is cells the walker actually walked
///     into, and <c>FreeWalkCount</c> ("N") is cells the walk would have
///     visited without selection-based restriction. Both are zero when the
///     engine refused.
/// </summary>
public sealed record GatherResult(
    CellRef Sink,
    string OriginalFormula,
    IReadOnlyList<BindingRow> Bindings,
    string SynthesisedLet,
    GatherDiagnostic? Diagnostic = null,
    int WalkedCount = 0,
    int FreeWalkCount = 0);

/// <summary>
///     A reason the engine refused to synthesise a LET. Surfaced by
///     <see cref="GatherEngine" /> via <see cref="GatherResult.Diagnostic" />
///     so the slash-command handler can render a <c>MessageBox</c> instead
///     of opening the dialog. PR 7 introduces cycle and multi-sink
///     diagnostics; later PRs may add more kinds (e.g. LAMBDA-call sink in
///     PR 8).
/// </summary>
public sealed record GatherDiagnostic(
    GatherDiagnosticKind Kind,
    string Message,
    IReadOnlyList<CellRef> Cells);

public enum GatherDiagnosticKind
{
    /// <summary>The precedent graph contains a cycle.</summary>
    Cycle,

    /// <summary>The multi-selection contains 2+ cells with no in-scope dependent.</summary>
    MultipleSinks,

    /// <summary>
    ///     The sink's formula is exactly a single call to a registered
    ///     LAMBDA (e.g. <c>=Foo(A1, B1)</c>). The author should run
    ///     <c>/EditLambda</c> first to expand it into a LET, then re-run
    ///     <c>/Gather</c>.
    /// </summary>
    LambdaCallSink
}

/// <summary>
///     The geometry of the dynamic-array spill a cell belongs to:
///     <see cref="Anchor" /> is the cell holding the formula that spills,
///     and <see cref="Rows" />/<see cref="Columns" /> are the dimensions of
///     the spill rectangle the anchor fills. Returned by
///     <see cref="ICellSource.GetSpill" /> for the anchor and for every
///     child alike — a cell is the anchor iff <c>Anchor == cell</c>.
///
///     Excel guarantees spill ranges are disjoint (overlap is
///     <c>#SPILL!</c>), so a cell belongs to at most one spill and there is
///     no ambiguity to resolve.
/// </summary>
public sealed record SpillInfo(CellRef Anchor, int Rows, int Columns);

/// <summary>
///     COM-free abstraction over the active workbook used by
///     <see cref="CellGraphWalker" />. The live adapter implements this
///     against the <c>Workbook</c> COM object; tests stub it with an
///     in-memory dictionary so the engine is exercisable without Excel.
///     PR 3 widens the contract to span all sheets in the workbook —
///     implementations resolve <see cref="CellRef.Sheet" /> case-insensitively
///     against the workbook's worksheets and return null for external refs
///     and refs into sheets that don't exist.
/// </summary>
public interface ICellSource
{
    /// <summary>
    ///     The sheet the sink lives on. Used as the default sheet for
    ///     unqualified refs in the sink's own formula and for the binding
    ///     RHS rendering rule (in-sheet refs stay bare).
    /// </summary>
    string SinkSheet { get; }

    /// <summary>
    ///     Returns the cell's formula text starting with <c>=</c>, or null
    ///     when the cell is empty, holds a literal value, lives on a sheet
    ///     not in the active workbook, or is an external-workbook ref.
    /// </summary>
    string? GetFormula(CellRef cell);

    /// <summary>
    ///     Returns the displayed text of the cell directly above
    ///     <paramref name="cell" /> on its own sheet, or null if the
    ///     neighbour is out of range, empty, non-string, the cell is in row
    ///     1, or the cell is unreachable.
    /// </summary>
    string? GetCellAboveText(CellRef cell);

    /// <summary>
    ///     Returns the displayed text of the cell directly to the left of
    ///     <paramref name="cell" /> on its own sheet, or null if the
    ///     neighbour is out of range, empty, non-string, the cell is in
    ///     column 1, or the cell is unreachable.
    /// </summary>
    string? GetCellLeftText(CellRef cell);

    /// <summary>
    ///     The spill geometry <paramref name="cell" /> belongs to, or null
    ///     when the cell isn't part of a dynamic-array spill. Non-null for
    ///     both the anchor and every child of a spill; the anchor test is
    ///     <c>GetSpill(c)?.Anchor == c</c>. Always null for external refs
    ///     and unreachable cells. The live adapter reads
    ///     <c>Range.SpillParent</c> then <c>Range.SpillingToRange</c> on
    ///     the anchor — modern Excel 365 only, no fallback for older
    ///     builds. The engine uses this to force a spilling anchor to bind
    ///     as an input with RHS <c>A1#</c>; spec 0010 builds slice
    ///     expressions from the geometry.
    /// </summary>
    SpillInfo? GetSpill(CellRef cell);

    /// <summary>
    ///     True when <paramref name="name" /> resolves to a workbook-scoped
    ///     LAMBDA. The live adapter checks <c>Workbook.Names</c> and the
    ///     name's <c>RefersTo</c> via <see cref="LambdaSignatureParser.IsLambdaFormula" />.
    ///     PR 8 uses this to refuse pure-LAMBDA-call sinks (e.g.
    ///     <c>=Foo(A1, B1)</c>) and steer the author to <c>/EditLambda</c>
    ///     first. Names that don't exist on the workbook (built-in
    ///     functions like <c>SUM</c>, sheet-scoped names, free-floating
    ///     identifiers) return false so the engine treats their calls as
    ///     ordinary expressions and walks them normally.
    /// </summary>
    bool IsLambdaName(string name);
}