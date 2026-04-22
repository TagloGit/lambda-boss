namespace LambdaBoss.UI;

/// <summary>
///     A command invocable from the main popup's Commands mode (typed as "/name").
/// </summary>
internal sealed record SlashCommand(string Name, string Description, Action Invoke);
