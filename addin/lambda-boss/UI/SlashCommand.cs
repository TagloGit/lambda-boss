namespace LambdaBoss.UI;

/// <summary>
///     A command in the popup's Commands mode, filtered by typing its name (a leading "/" is optional).
/// </summary>
internal sealed record SlashCommand(string Name, string Description, Action Invoke, Func<bool>? IsAvailable = null);
