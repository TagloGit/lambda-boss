using LambdaBoss.UI;

using Xunit;

#pragma warning disable CA1707

namespace LambdaBoss.Tests;

/// <summary>Spec 0017: the popup opens on its commands; keys move off K and L.</summary>
public class CommandsFirstTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public CommandsFirstTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LambdaBoss_CommandsFirst_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static SlashCommand Cmd(string name, Func<bool>? available = null) => new(name, "", () => { }, available);

    /// <summary>The popup's registry as the window builds it (deliberately not in the fixed order).</summary>
    private static IReadOnlyList<SlashCommand> Registry() => PopupCommands.InOrder(new[]
    {
        Cmd("LET to LAMBDA"),
        Cmd("Edit Lambda", () => false),
        Cmd("Edit Lambda", () => true),
        Cmd("Gather"),
        Cmd("Refactor"),
        Cmd("Unnest"),
        Cmd("State"),
        Cmd("Debug"),
        Cmd("Library"),
        Cmd("Import All Lambdas"),
        Cmd("Import All Lambdas (no prefix)"),
        Cmd("Settings"),
    });

    // --- The fixed order ---

    [Fact]
    public void DefaultOrder_ImportAllBareFirst_ThenTheMostUsed()
    {
        var names = SlashCommandFilter.Filter(Registry(), "").Select(c => c.Name).ToList();

        Assert.Equal(new[]
        {
            "Import All Lambdas (no prefix)", "State", "Debug", "Edit Lambda", "LET to LAMBDA",
            "Gather", "Refactor", "Unnest", "Library", "Import All Lambdas", "Settings"
        }, names);
    }

    [Fact]
    public void DefaultOrder_DebugIsRightAfterState_LibraryNextToPrefixedImport()
    {
        var order = PopupCommands.Order.ToList();

        Assert.Equal(order.IndexOf(PopupCommands.State) + 1, order.IndexOf(PopupCommands.Debug));
        Assert.Equal(order.IndexOf(PopupCommands.Library) + 1, order.IndexOf(PopupCommands.ImportAllPrefixed));
        Assert.Equal(PopupCommands.ImportAllBare, order[0]);
    }

    [Fact]
    public void InOrder_KeepsVariantsTogetherInTheirOrder_AndUnknownNamesLast()
    {
        var a = Cmd("Edit Lambda");
        var b = Cmd("Edit Lambda");
        var sorted = PopupCommands.InOrder(new[] { Cmd("Zzz"), a, b, Cmd("State") });

        Assert.Equal(new[] { "State", "Edit Lambda", "Edit Lambda", "Zzz" }, sorted.Select(c => c.Name));
        Assert.Same(a, sorted[1]);
        Assert.Same(b, sorted[2]);
    }

    [Fact]
    public void EmptyQuery_FirstCommandIsImportAllBare_SoEnterImportsEverythingBare()
    {
        Assert.Equal(PopupCommands.ImportAllBare, SlashCommandFilter.Filter(Registry(), "")[0].Name);
    }

    // --- Filtering with or without "/" ---

    [Theory]
    [InlineData("sta")]
    [InlineData("/sta")]
    [InlineData("STA")]
    public void Sta_RunsState(string typed)
    {
        Assert.Equal("State", SlashCommandFilter.Filter(Registry(), typed)[0].Name);
    }

    [Theory]
    [InlineData("deb", "Debug")]
    [InlineData("/deb", "Debug")]
    [InlineData("lib", "Library")]
    [InlineData("/lib", "Library")]
    [InlineData("edit", "Edit Lambda")]
    [InlineData("let", "LET to LAMBDA")]
    public void Typing_WithOrWithoutSlash_FindsTheCommand(string typed, string expected)
    {
        Assert.Equal(expected, SlashCommandFilter.Filter(Registry(), typed)[0].Name);
    }

    [Theory]
    [InlineData("i")]
    [InlineData("e")]
    [InlineData("a")]
    public void SameQuery_WithOrWithoutSlash_GivesTheSameList(string typed)
    {
        Assert.Equal(
            SlashCommandFilter.Filter(Registry(), typed).Select(c => c.Name),
            SlashCommandFilter.Filter(Registry(), "/" + typed).Select(c => c.Name));
    }

    [Fact]
    public void EqualScores_KeepTheFixedOrder_NotAlphabetical()
    {
        // The same name scores the same, so only the tie-break orders them.
        var zeta = new SlashCommand("Same", "zeta", () => { });
        var alpha = new SlashCommand("Same", "alpha", () => { });

        var result = SlashCommandFilter.Filter(new[] { zeta, alpha }, "sa");

        Assert.Same(zeta, result[0]);
        Assert.Same(alpha, result[1]);
    }

    // --- Opening mode per setting ---

    [Fact]
    public void OpensOnCommands_ByDefault()
    {
        var nav = new PopupNavigation();

        Assert.Equal(PopupMode.Commands, nav.Open(new Settings().PopupOpensOn));
    }

    [Fact]
    public void OpensOnLibraries_WhenTheSettingSaysSo()
    {
        var nav = new PopupNavigation();

        Assert.Equal(PopupMode.Library, nav.Open(PopupOpensOn.Libraries));
    }

    [Fact]
    public void CommandsFirst_TypingWithoutSlash_StaysOnCommands()
    {
        var nav = new PopupNavigation();
        nav.Open(PopupOpensOn.Commands);

        Assert.Equal(PopupMode.Commands, nav.ModeForText("sta"));
        Assert.Equal(PopupMode.Commands, nav.ModeForText("/sta"));
        Assert.Equal(PopupMode.Commands, nav.ModeForText(""));
    }

    [Fact]
    public void CommandsFirst_EscOnCommands_Closes()
    {
        var nav = new PopupNavigation();
        nav.Open(PopupOpensOn.Commands);

        Assert.Equal(PopupEscape.Close, nav.Escape());
    }

    [Fact]
    public void CommandsFirst_LibraryView_WorksAsBefore_AndEscReturnsToCommands()
    {
        var nav = new PopupNavigation();
        nav.Open(PopupOpensOn.Commands);
        nav.Set(PopupMode.Library); // the Library command

        Assert.Equal(PopupMode.Library, nav.ModeForText(""));
        Assert.Equal(PopupMode.Search, nav.ModeForText("xlook"));
        Assert.Equal(PopupMode.Commands, nav.ModeForText("/"));
        Assert.Equal(PopupEscape.BackToCommands, nav.Escape());

        nav.Set(PopupMode.Search); // Tab
        Assert.Equal(PopupEscape.BackToCommands, nav.Escape());
    }

    [Fact]
    public void Libraries_BehavesAsBefore()
    {
        var nav = new PopupNavigation();
        nav.Open(PopupOpensOn.Libraries);

        Assert.Equal(PopupMode.Library, nav.ModeForText(""));
        Assert.Equal(PopupMode.Search, nav.ModeForText("sta"));
        Assert.Equal(PopupMode.Commands, nav.ModeForText("/sta"));
        Assert.Equal(PopupEscape.Close, nav.Escape());
        Assert.Equal(PopupNavigation.LibrariesHint, nav.Hint);

        nav.Set(PopupMode.Commands);
        Assert.Equal(PopupMode.Library, nav.ModeForText(""));
        Assert.Equal(PopupEscape.ClearText, nav.Escape());
        Assert.Equal(PopupNavigation.LibrariesHint, nav.Hint);
    }

    [Fact]
    public void CommandsFirst_HintsNameEscsJob()
    {
        var nav = new PopupNavigation();
        nav.Open(PopupOpensOn.Commands);
        Assert.Contains("Esc close", nav.Hint);
        Assert.Contains("Enter run", nav.Hint);

        nav.Set(PopupMode.Library);
        Assert.Contains("Esc commands", nav.Hint);
        Assert.Contains("Tab switch", nav.Hint);
    }

    // --- Settings ---

    [Fact]
    public void PopupOpensOn_RoundTrips_AndReadsUnknownAsCommands()
    {
        new Settings { PopupOpensOn = PopupOpensOn.Libraries }.Save(_settingsPath);
        Assert.Equal(PopupOpensOn.Libraries, Settings.Load(_settingsPath).PopupOpensOn);
        Assert.Contains("\"Libraries\"", File.ReadAllText(_settingsPath));

        File.WriteAllText(_settingsPath, """{"PopupOpensOn":"Sideways"}""");
        Assert.Equal(PopupOpensOn.Commands, Settings.Load(_settingsPath).PopupOpensOn);
    }

    [Fact]
    public void OldSettings_OnCtrlShiftL_MoveToCtrlShiftJ_Once()
    {
        File.WriteAllText(_settingsPath, """{"KeyboardShortcut":"^+L","CacheTtlMinutes":60}""");

        var first = Settings.Load(_settingsPath);
        Assert.Equal("^+J", first.KeyboardShortcut);
        Assert.True(first.PopupShortcutMoved);

        // Saved, so the move happens once: choosing ^+L again afterwards sticks.
        Assert.True(Settings.Load(_settingsPath).PopupShortcutMoved);
        first.KeyboardShortcut = "^+L";
        first.Save(_settingsPath);
        Assert.Equal("^+L", Settings.Load(_settingsPath).KeyboardShortcut);
    }

    [Fact]
    public void OldSettings_OnAnotherKey_KeepIt()
    {
        File.WriteAllText(_settingsPath, """{"KeyboardShortcut":"^+M"}""");

        var settings = Settings.Load(_settingsPath);

        Assert.Equal("^+M", settings.KeyboardShortcut);
        Assert.True(settings.PopupShortcutMoved);
    }

    [Fact]
    public void Defaults_PopupCtrlShiftJ_CheckFormulaCtrlShiftC()
    {
        var settings = Settings.Load(_settingsPath);

        Assert.Equal("^+J", settings.KeyboardShortcut);
        Assert.Equal("^+C", settings.CheckFormulaShortcut);
        Assert.Equal(PopupOpensOn.Commands, settings.PopupOpensOn);
    }

    // --- Shortcut keys ---

    [Theory]
    [InlineData("^+C", true, true, false, 0x43, "Ctrl+Shift+C")]
    [InlineData("^+K", true, true, false, 0x4B, "Ctrl+Shift+K")]
    [InlineData("^%q", true, false, true, 0x51, "Ctrl+Alt+Q")]
    [InlineData("^3", true, false, false, 0x33, "Ctrl+3")]
    [InlineData("+{F2}", false, true, false, 0x71, "Shift+F2")]
    [InlineData(" ^+c ", true, true, false, 0x43, "Ctrl+Shift+C")]
    public void ShortcutKey_Parses(string onKey, bool ctrl, bool shift, bool alt, int vk, string label)
    {
        Assert.True(ShortcutKey.TryParse(onKey, out var key));
        Assert.Equal(ctrl, key.Ctrl);
        Assert.Equal(shift, key.Shift);
        Assert.Equal(alt, key.Alt);
        Assert.Equal(vk, key.VirtualKey);
        Assert.Equal(label, key.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("C")]
    [InlineData("+C")]
    [InlineData("^+")]
    [InlineData("^+CC")]
    [InlineData("^{ENTER}")]
    [InlineData("^{F13}")]
    public void ShortcutKey_Refuses(string? onKey)
    {
        Assert.False(ShortcutKey.TryParse(onKey, out _));
    }

    [Fact]
    public void ShortcutKey_BadSetting_FallsBackToTheDefault()
    {
        Assert.Equal("Ctrl+Shift+C", ShortcutKey.ParseOr("nonsense", Settings.DefaultCheckFormulaShortcut).Label);
    }
}
