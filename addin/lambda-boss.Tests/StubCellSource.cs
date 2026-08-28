namespace LambdaBoss.Tests;

/// <summary>
///     In-memory <see cref="ICellSource" /> used by gather tests. Cells are
///     seeded by A1 address; formulas start with <c>=</c>, labels are plain
///     text used for the cell-above and cell-left lookups. Cross-sheet
///     seeding uses an <c>"Sheet!A1"</c> address; bare addresses default to
///     the source's sink sheet.
/// </summary>
internal sealed class StubCellSource : ICellSource
{
    // Keys are normalised to "Sheet!A1" with the sheet name compared
    // case-insensitively (matches CellRef equality).
    private readonly Dictionary<string, string> _formulas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);
    // Keyed by every cell in the spill rectangle (anchor and children alike),
    // mirroring Range.SpillParent, which resolves the anchor from any of them.
    private readonly Dictionary<string, SpillInfo> _spills = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lambdaNames = new(StringComparer.OrdinalIgnoreCase);

    public StubCellSource(string sheet = "Sheet1")
    {
        SinkSheet = sheet;
    }

    public string SinkSheet { get; }

    public StubCellSource WithFormula(string a1, string formula)
    {
        _formulas[Qualify(a1)] = formula;
        return this;
    }

    public StubCellSource WithLabel(string a1, string label)
    {
        _labels[Qualify(a1)] = label;
        return this;
    }

    /// <summary>
    ///     Marks <paramref name="anchor" /> as a spill anchor filling a
    ///     <paramref name="rows" />×<paramref name="cols" /> rectangle, and
    ///     registers every cell in that rectangle as part of the spill.
    ///     Mirrors what the live adapter learns from <c>Range.SpillParent</c>
    ///     plus <c>Range.SpillingToRange</c>.
    /// </summary>
    public StubCellSource WithSpill(string anchor, int rows, int cols)
    {
        var (sheet, col, row) = ParseAddress(anchor, SinkSheet);
        var info = new SpillInfo(new CellRef(sheet, col, row), rows, cols);
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
            _spills[Key(sheet, col + c, row + r)] = info;
        return this;
    }

    /// <summary>
    ///     Registers <paramref name="name" /> as a workbook-scoped LAMBDA.
    ///     Mirrors what the live adapter learns from <c>Workbook.Names</c>
    ///     plus <see cref="LambdaSignatureParser.IsLambdaFormula" />.
    /// </summary>
    public StubCellSource WithLambdaName(string name)
    {
        _lambdaNames.Add(name);
        return this;
    }

    public string? GetFormula(CellRef cell)
    {
        if (cell.IsExternal)
            return null;
        return _formulas.TryGetValue(Key(cell.Sheet, cell.Column, cell.Row), out var f) ? f : null;
    }

    public string? GetCellAboveText(CellRef cell)
    {
        if (cell.IsExternal || cell.Row <= 1)
            return null;
        return _labels.TryGetValue(Key(cell.Sheet, cell.Column, cell.Row - 1), out var l) ? l : null;
    }

    public string? GetCellLeftText(CellRef cell)
    {
        if (cell.IsExternal || cell.Column <= 1)
            return null;
        return _labels.TryGetValue(Key(cell.Sheet, cell.Column - 1, cell.Row), out var l) ? l : null;
    }

    public SpillInfo? GetSpill(CellRef cell)
    {
        if (cell.IsExternal)
            return null;
        return _spills.TryGetValue(Key(cell.Sheet, cell.Column, cell.Row), out var info) ? info : null;
    }

    public bool IsLambdaName(string name)
    {
        return !string.IsNullOrEmpty(name) && _lambdaNames.Contains(name);
    }

    /// <summary>
    ///     Build a CellRef from <c>"A1"</c> (defaults to the sink sheet) or
    ///     <c>"Sheet!A1"</c>. Quoted sheet names (<c>"'My Sheet'!A1"</c>)
    ///     are recognised so tests can mirror what the extractor sees.
    /// </summary>
    public CellRef Ref(string a1)
    {
        var (sheet, col, row) = ParseAddress(a1, SinkSheet);
        return new CellRef(sheet, col, row);
    }

    private string Qualify(string a1)
    {
        var (sheet, col, row) = ParseAddress(a1, SinkSheet);
        return Key(sheet, col, row);
    }

    private static string Key(string sheet, int col, int row) =>
        $"{sheet}!{CellRef.ColumnLetters(col)}{row}";

    private static (string Sheet, int Col, int Row) ParseAddress(string addr, string defaultSheet)
    {
        var bang = addr.IndexOf('!');
        string sheet;
        string a1;
        if (bang >= 0)
        {
            sheet = addr[..bang];
            if (sheet.Length >= 2 && sheet[0] == '\'' && sheet[^1] == '\'')
                sheet = sheet[1..^1].Replace("''", "'");
            a1 = addr[(bang + 1)..];
        }
        else
        {
            sheet = defaultSheet;
            a1 = addr;
        }

        var i = 0;
        while (i < a1.Length && (char.IsLetter(a1[i]) || a1[i] == '$')) i++;
        var letters = a1[..i].Replace("$", "");
        var rowText = a1[i..].Replace("$", "");
        var col = CellRef.LettersToColumn(letters);
        var row = int.Parse(rowText);
        return (sheet, col, row);
    }
}
