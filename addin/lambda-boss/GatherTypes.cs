namespace LambdaBoss;

/// <summary>
///     A workbook cell address. PR 1 uses single-sheet refs only; the
///     <see cref="Sheet" /> field is always the sink's sheet name in this
///     slice but is included so cross-sheet support can land in PR 3 without
///     changing the type shape. <see cref="Column" /> and <see cref="Row" />
///     are 1-based to match Excel's COM addressing.
/// </summary>
public sealed record CellRef(string Sheet, int Column, int Row)
{
    /// <summary>The unqualified A1-style address, e.g. <c>A1</c>, <c>BC27</c>.</summary>
    public string A1Address => $"{ColumnLetters(Column)}{Row}";

    public override string ToString() => A1Address;

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
}

/// <summary>
///     A cell discovered by <see cref="CellGraphWalker" />. The
///     <see cref="Formula" /> is null when the cell holds a literal value
///     (treated as a leaf input). <see cref="CellAboveText" /> is the text
///     of the cell directly above (used for binding-name derivation), or
///     null when the cell is in row 1 or the cell above is empty.
/// </summary>
public sealed record WalkedCell(
    CellRef Ref,
    string? Formula,
    string? CellAboveText,
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
///     against <c>Range</c> on the macro thread; tests stub it with an
///     in-memory dictionary so the engine is exercisable without Excel.
/// </summary>
public interface ICellSource
{
    /// <summary>The sheet the sink lives on; PR 1 walks within it only.</summary>
    string SinkSheet { get; }

    /// <summary>
    ///     Returns the cell's formula text starting with <c>=</c>, or null
    ///     when the cell is empty or holds a literal value.
    /// </summary>
    string? GetFormula(CellRef cell);

    /// <summary>
    ///     Returns the displayed text of the cell directly above
    ///     <paramref name="cell" />, or null if that cell is empty or
    ///     <paramref name="cell" /> is in row 1.
    /// </summary>
    string? GetCellAboveText(CellRef cell);
}
