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
            var sheetPart = SheetPartForQualifiedRef(Sheet);
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

    public override string ToString() => A1Address;

    public bool Equals(CellRef? other) =>
        other is not null
        && string.Equals(Sheet, other.Sheet, StringComparison.OrdinalIgnoreCase)
        && Column == other.Column
        && Row == other.Row
        && string.Equals(ExternalWorkbook, other.ExternalWorkbook, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Sheet, StringComparer.OrdinalIgnoreCase);
        hash.Add(Column);
        hash.Add(Row);
        hash.Add(ExternalWorkbook, StringComparer.OrdinalIgnoreCase);
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
            col = col * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
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
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.')
                return true;
        }
        return false;
    }

    private static string EscapeQuotes(string name) => name.Replace("'", "''");

    private static string SheetPartForQualifiedRef(string sheet) =>
        NeedsQuotes(sheet) ? EscapeQuotes(sheet) : sheet;
}

/// <summary>
///     A cell discovered by <see cref="CellGraphWalker" />. The
///     <see cref="Formula" /> is null when the cell holds a literal value or
///     is unreachable (external workbook ref, missing sheet) — both treated
///     as leaf inputs. <see cref="CellAboveText" /> and
///     <see cref="CellLeftText" /> are the text of the cells directly above
///     and to the left on the cell's own sheet, null when out of range,
///     empty, or non-string.
/// </summary>
public sealed record WalkedCell(
    CellRef Ref,
    string? Formula,
    string? CellAboveText,
    string? CellLeftText,
    IReadOnlyList<CellRef> Precedents);

/// <summary>
///     A finalised LET binding row in PR 1's read-only dialog. PR 2 onwards
///     will allow the user to edit <see cref="Name" /> and toggle role.
/// </summary>
public sealed record BindingRow(
    CellRef CellRef,
    BindingRole Role,
    string Name,
    string Rhs);

public enum BindingRole
{
    Input,
    Step,
}

/// <summary>
///     The output of <see cref="GatherEngine" />: enough state to render
///     the dialog and write the synthesised LET back to the sink on Save.
/// </summary>
public sealed record GatherResult(
    CellRef Sink,
    string OriginalFormula,
    IReadOnlyList<BindingRow> Bindings,
    string SynthesisedLet);

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
}
