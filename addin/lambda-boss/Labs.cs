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

    /// <summary>
    ///     /State from any form of a state engine (spec 0015, #429). It replaced the four 0011
    ///     switches (state-let-to-lambda, state-scaffold, state-edit-lambda, state-edit), so it
    ///     also turns on the state-aware LET to LAMBDA and Edit Lambda's fold stepper.
    /// </summary>
    public static readonly LabsSwitch State = new(
        "state-any-form",
        "/State from any form",
        "/State on the active cell: add, rename or remove a state variable, and move the engine between REDUCE, SCAN, library folds and the unfolded LET step, inline or named. Also turns on state steps in LET to LAMBDA and Edit Lambda's k stepper on a fold.");

    public static readonly LabsSwitch CheckFormula = new(
        "check-formula",
        "Check Formula (Ctrl+Shift+C)",
        "Points at unmatched or misplaced brackets and unknown names in the active cell's formula, or in the formula being edited. Also a ribbon button. The key is the CheckFormulaShortcut setting in settings.json, read when Excel starts.");

    public static readonly LabsSwitch GatherNext = new(
        "gather-next",
        "Gather next",
        "Gather walks ranges over formula cells, names inputs and steps from the nearest label, and can save the gathered LET as a named LAMBDA from its own dialog (Lambda name box, Alt+Up/Down to reorder inputs, Alt+I to toggle a row's role, Alt+Right/Left to write the result to a cell further right and keep the start cell).");

    public static readonly IReadOnlyList<LabsSwitch> All =
        new[] { PopupChannelBadge, State, CheckFormula, GatherNext };

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
