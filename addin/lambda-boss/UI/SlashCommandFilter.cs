namespace LambdaBoss.UI;

/// <summary>
///     Filters and ranks slash commands against a query. An empty query
///     returns commands in their registration order; otherwise commands are
///     scored by <see cref="FuzzyMatcher"/> against the command name.
/// </summary>
internal static class SlashCommandFilter
{
    public static IReadOnlyList<SlashCommand> Filter(IReadOnlyList<SlashCommand> commands, string query)
    {
        var trimmed = (query ?? string.Empty).TrimStart('/');

        if (string.IsNullOrEmpty(trimmed))
            return commands;

        return commands
            .Select(c => (Cmd: c, Score: FuzzyMatcher.Score(trimmed, c.Name)))
            .Where(x => x.Score != FuzzyMatcher.NoMatch)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Cmd.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Cmd)
            .ToList();
    }
}
