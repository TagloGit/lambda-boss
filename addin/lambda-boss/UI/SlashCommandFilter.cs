namespace LambdaBoss.UI;

/// <summary>
///     Filters and ranks the popup's commands against a query, with or without a leading "/". An
///     empty query returns commands in their registration order; otherwise commands are scored by
///     <see cref="FuzzyMatcher" /> against the command name, ties in registration order.
/// </summary>
internal static class SlashCommandFilter
{
    public static IReadOnlyList<SlashCommand> Filter(IReadOnlyList<SlashCommand> commands, string query)
    {
        var trimmed = query.TrimStart('/');
        var available = commands.Where(c => c.IsAvailable?.Invoke() ?? true).ToList();

        if (string.IsNullOrEmpty(trimmed))
            return available;

        // Equal scores keep the registry's order, so the list never reshuffles (spec 0017).
        return available
            .Select((c, i) => (Cmd: c, Score: FuzzyMatcher.Score(trimmed, c.Name), Index: i))
            .Where(x => x.Score != FuzzyMatcher.NoMatch)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Cmd)
            .ToList();
    }
}