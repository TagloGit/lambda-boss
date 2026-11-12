namespace LambdaBoss;

/// <summary>
///     Wraps a source for the length of one gather so <see cref="ICellSource.ReadCell" />, the
///     probe the label search and range expansion lean on, costs one COM read per cell however
///     often it is asked (spec 0019). Everything else passes straight through. Never kept across
///     gathers, so an edit between a Recompute and the last run is always seen.
/// </summary>
internal sealed class ReadCellMemoSource : ICellSource
{
    private readonly ICellSource _inner;
    private readonly Dictionary<CellRef, CellContent> _memo = new();

    public ReadCellMemoSource(ICellSource inner)
    {
        _inner = inner;
    }

    public string SinkSheet => _inner.SinkSheet;
    public string? GetFormula(CellRef cell) => _inner.GetFormula(cell);
    public string? GetCellAboveText(CellRef cell) => _inner.GetCellAboveText(cell);
    public string? GetCellLeftText(CellRef cell) => _inner.GetCellLeftText(cell);
    public SpillInfo? GetSpill(CellRef cell) => _inner.GetSpill(cell);
    public bool IsLambdaName(string name) => _inner.IsLambdaName(name);

    public CellContent ReadCell(CellRef cell)
    {
        if (_memo.TryGetValue(cell, out var cached))
            return cached;
        var content = _inner.ReadCell(cell);
        _memo[cell] = content;
        return content;
    }
}
