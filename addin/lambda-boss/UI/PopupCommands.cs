namespace LambdaBoss.UI;

/// <summary>
///     The popup's commands in their fixed order (spec 0017). The order is for muscle memory, so
///     it never follows recent use: opening the popup and pressing Enter always runs the first.
/// </summary>
internal static class PopupCommands
{
    public const string ImportAllBare = "Import All Lambdas (no prefix)";
    public const string State = "State";
    public const string Debug = "Debug";
    public const string EditLambda = "Edit Lambda";
    public const string LetToLambda = "LET to LAMBDA";
    public const string Gather = "Gather";
    public const string Refactor = "Refactor";
    public const string Unnest = "Unnest";
    public const string Library = "Library";
    public const string ImportAllPrefixed = "Import All Lambdas";
    public const string Settings = "Settings";

    public static readonly IReadOnlyList<string> Order = new[]
    {
        ImportAllBare,
        State,
        Debug,
        EditLambda,
        LetToLambda,
        Gather,
        Refactor,
        Unnest,
        Library,
        ImportAllPrefixed,
        Settings
    };

    /// <summary>
    ///     The commands sorted into <see cref="Order" />. Commands sharing a name (a Labs variant) keep
    ///     their relative order; a name not in the list goes last.
    /// </summary>
    public static IReadOnlyList<SlashCommand> InOrder(IEnumerable<SlashCommand> commands) =>
        commands
            .Select((c, i) => (Cmd: c, At: IndexOf(c.Name), I: i))
            .OrderBy(x => x.At)
            .ThenBy(x => x.I)
            .Select(x => x.Cmd)
            .ToList();

    private static int IndexOf(string name)
    {
        for (var i = 0; i < Order.Count; i++)
            if (string.Equals(Order[i], name, StringComparison.Ordinal))
                return i;
        return int.MaxValue;
    }
}
