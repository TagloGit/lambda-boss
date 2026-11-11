namespace LambdaBoss.UI;

/// <summary>What the popup's list shows.</summary>
internal enum PopupMode
{
    Library,
    Search,
    Commands
}

/// <summary>What Esc does.</summary>
internal enum PopupEscape
{
    /// <summary>Hide the popup.</summary>
    Close,

    /// <summary>Clear the box: the old two-step exit from "/" commands back to the library list.</summary>
    ClearText,

    /// <summary>Leave the library view for the commands (commands-first).</summary>
    BackToCommands
}

/// <summary>
///     The popup's mode rules (spec 0017), kept apart from the window so they can be tested.
///     Commands-first: the popup opens on its commands, typing filters them with or without a
///     leading "/", Library opens the old library view and Esc there comes back. Libraries: the
///     behaviour before spec 0017, where "/" enters the commands and everything else is libraries.
/// </summary>
internal sealed class PopupNavigation
{
    public PopupOpensOn OpensOn { get; private set; } = PopupOpensOn.Commands;
    public PopupMode Mode { get; private set; } = PopupMode.Commands;

    private bool CommandsFirst => OpensOn == PopupOpensOn.Commands;

    /// <summary>The mode the popup opens in.</summary>
    public PopupMode Open(PopupOpensOn opensOn)
    {
        OpensOn = opensOn;
        Mode = CommandsFirst ? PopupMode.Commands : PopupMode.Library;
        return Mode;
    }

    /// <summary>Sets the mode outright: Tab, a mode label, the Library command.</summary>
    public void Set(PopupMode mode) => Mode = mode;

    /// <summary>
    ///     The mode the box's text asks for. A leading "/" is always the commands. On the commands
    ///     in commands-first, any text filters them. Otherwise blank is the library list and text
    ///     searches lambdas, as before.
    /// </summary>
    public PopupMode ModeForText(string raw)
    {
        if (raw.StartsWith("/", StringComparison.Ordinal))
            return PopupMode.Commands;
        if (CommandsFirst && Mode == PopupMode.Commands)
            return PopupMode.Commands;
        return string.IsNullOrEmpty(raw.Trim()) ? PopupMode.Library : PopupMode.Search;
    }

    public PopupEscape Escape() => Mode switch
    {
        PopupMode.Commands => CommandsFirst ? PopupEscape.Close : PopupEscape.ClearText,
        _ => CommandsFirst ? PopupEscape.BackToCommands : PopupEscape.Close
    };

    /// <summary>The status line when nothing else is being said.</summary>
    public string Hint => !CommandsFirst
        ? LibrariesHint
        : Mode == PopupMode.Commands
            ? "Type to filter · ↑↓ navigate · Enter run · Esc close"
            : "↑↓ navigate · Enter load · Tab switch · Esc commands";

    public const string LibrariesHint = "↑↓ navigate · Enter load · Tab switch · Esc close";
}
