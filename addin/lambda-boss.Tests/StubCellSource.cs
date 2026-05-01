namespace LambdaBoss.Tests;

/// <summary>
///     In-memory <see cref="ICellSource" /> used by gather tests. Cells
///     are seeded by A1 address; formulas start with <c>=</c>, labels are
///     plain text used for the cell-above lookup.
/// </summary>
internal sealed class StubCellSource : ICellSource
{
    private readonly Dictionary<string, string> _formulas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

    public StubCellSource(string sheet = "Sheet1")
    {
        SinkSheet = sheet;
    }

    public string SinkSheet { get; }

    public StubCellSource WithFormula(string a1, string formula)
    {
        _formulas[a1] = formula;
        return this;
    }

    public StubCellSource WithLabel(string a1, string label)
    {
        _labels[a1] = label;
        return this;
    }

    public string? GetFormula(CellRef cell)
    {
        return _formulas.TryGetValue(cell.A1Address, out var f) ? f : null;
    }

    public string? GetCellAboveText(CellRef cell)
    {
        if (cell.Row <= 1)
            return null;
        var aboveAddress = $"{CellRef.ColumnLetters(cell.Column)}{cell.Row - 1}";
        return _labels.TryGetValue(aboveAddress, out var l) ? l : null;
    }

    public string? GetCellLeftText(CellRef cell)
    {
        if (cell.Column <= 1)
            return null;
        var leftAddress = $"{CellRef.ColumnLetters(cell.Column - 1)}{cell.Row}";
        return _labels.TryGetValue(leftAddress, out var l) ? l : null;
    }

    public CellRef Ref(string a1)
    {
        var (col, row) = ParseA1(a1);
        return new CellRef(SinkSheet, col, row);
    }

    private static (int Col, int Row) ParseA1(string a1)
    {
        var i = 0;
        while (i < a1.Length && char.IsLetter(a1[i])) i++;
        var col = CellRef.LettersToColumn(a1[..i]);
        var row = int.Parse(a1[i..]);
        return (col, row);
    }
}
