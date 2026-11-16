namespace LambdaBoss;

/// <summary>
///     What /State itself added round a fold when answers were ticked as LET names (spec 0018
///     addendum, "How /State tells its lines from Tim's"). A record is a cell (workbook, sheet and
///     address) and the plain text around the fold before the add (debug wrapper and tail off, the
///     fold call itself excluded). Untick-all takes the <c>fold,</c> line (or the LET) off only when
///     the text it would give back is recorded for that cell; any other fold line is Tim's and stays.
///     Records are made only when a change is written to the cell, never by a preview. One
///     process-wide instance serves an Excel session; a restart empties it, which only makes /State
///     more careful.
/// </summary>
public sealed class StateAnswerMade
{
    /// <summary>The instance the add-in uses. Tests make their own.</summary>
    public static StateAnswerMade Shared { get; } = new();

    private readonly HashSet<(string, string, string)> _made = new();

    /// <summary>A context for one Apply on <paramref name="cell" /> (null: no cell, nothing matches).</summary>
    public StateAnswerContext For(string? cell) => new(this, cell);

    internal void Record(string cell, string before, string after)
    {
        lock (_made) _made.Add((cell, before, after));
    }

    internal bool Contains(string cell, string before, string after)
    {
        lock (_made) return _made.Contains((cell, before, after));
    }
}

/// <summary>
///     One Apply's view of the records: lookups by the cell being edited, and what the apply would
///     record held back in <see cref="Pending" /> until <see cref="Commit" /> (the cell was written).
/// </summary>
public sealed class StateAnswerContext
{
    private readonly StateAnswerMade _store;
    private readonly string? _cell;
    private readonly List<(string, string)> _pending = new();

    internal StateAnswerContext(StateAnswerMade store, string? cell)
    {
        _store = store;
        _cell = cell;
    }

    public IReadOnlyList<(string Before, string After)> Pending => _pending;

    internal bool Contains(string before, string after) =>
        _cell != null && _store.Contains(_cell, before, after);

    internal void Add(string before, string after) => _pending.Add((before, after));

    /// <summary>Makes the pending records real. Call only after the formula is in the cell.</summary>
    public void Commit()
    {
        if (_cell == null) return;
        foreach (var (before, after) in _pending) _store.Record(_cell, before, after);
        _pending.Clear();
    }
}
