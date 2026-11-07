namespace LambdaBoss;

/// <summary>
///     One switchable Labs feature. Id is what the user's settings store, so never rename it.
/// </summary>
public sealed record LabsSwitch(string Id, string Title, string Description);

/// <summary>
///     New add-in features ship off by default behind a switch here, until Tim picks them.
///     To add one: declare it, list it in <see cref="All"/>, and guard the feature with
///     <see cref="IsOn"/>. The settings window shows every listed switch and saves its state.
///     Delete a switch (and its guard) once its feature ships to everyone, or is dropped.
/// </summary>
public static class Labs
{
    public static readonly LabsSwitch PopupChannelBadge = new(
        "popup-channel-badge",
        "Show library channel in the popup",
        "Adds the channel the library is read from to the popup's status bar.");

    public static readonly LabsSwitch StateLetToLambda = new(
        "state-let-to-lambda",
        "State steps in LET to LAMBDA",
        "Turns a SetVars/GetVar or HSTACK state step into LAMBDA(state, input, ...) and offers the REDUCE fold line.");

    public static readonly LabsSwitch StateScaffold = new(
        "state-scaffold",
        "/State: new state step",
        "Adds the /State popup command, which writes a SetVars/GetVar state step from names and initial values.");

    public static readonly IReadOnlyList<LabsSwitch> All = new[] { PopupChannelBadge, StateLetToLambda, StateScaffold };

    public static bool IsOn(LabsSwitch labsSwitch) =>
        Settings.Current.LabsSwitchesOn.Contains(labsSwitch.Id);

    /// <summary>
    ///     The short name of a channel, as the ribbon, About box and popup show it.
    /// </summary>
    public static string ChannelLabel(LibraryChannel channel) =>
        channel == LibraryChannel.Labs
            ? $"Labs · {RepoConfig.LabsBranch}"
            : $"Stable · {RepoConfig.StableBranch}";
}
