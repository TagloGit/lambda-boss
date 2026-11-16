namespace LambdaBoss;

/// <summary>
///     What /State itself added round a fold when answers were ticked as LET names (spec 0018
///     addendum, "How /State tells its lines from Tim's"). A record is the plain text around the
///     fold before the add (debug wrapper and tail off, the fold call itself excluded). Untick-all
///     takes the <c>fold,</c> line (or the LET) off only when the text it would give back is recorded;
///     any other fold line is Tim's and stays. One process-wide instance serves an Excel session;
///     a restart empties it, which only makes /State more careful.
/// </summary>
public sealed class StateAnswerMade
{
    /// <summary>The instance the add-in uses. Tests make their own.</summary>
    public static StateAnswerMade Shared { get; } = new();

    private readonly HashSet<(string, string)> _made = new();

    public void Record(string before, string after)
    {
        lock (_made) _made.Add((before, after));
    }

    public bool Contains(string before, string after)
    {
        lock (_made) return _made.Contains((before, after));
    }
}
