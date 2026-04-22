using LambdaBoss.UI;

using Xunit;

namespace LambdaBoss.Tests;

public class SlashCommandFilterTests
{
    private static readonly IReadOnlyList<SlashCommand> Registry = new[]
    {
        new SlashCommand("LET to LAMBDA", "Convert LET cell", () => { }),
        new SlashCommand("Edit Lambda", "Expand LAMBDA call", () => { }),
        new SlashCommand("Load Library", "Switch to Library mode", () => { }),
        new SlashCommand("Settings", "Open settings window", () => { }),
    };

    [Fact]
    public void EmptyQuery_ReturnsAllInRegistrationOrder()
    {
        var result = SlashCommandFilter.Filter(Registry, "");

        Assert.Equal(
            new[] { "LET to LAMBDA", "Edit Lambda", "Load Library", "Settings" },
            result.Select(c => c.Name));
    }

    [Fact]
    public void LeadingSlash_IsStripped()
    {
        var withSlash = SlashCommandFilter.Filter(Registry, "/set");
        var withoutSlash = SlashCommandFilter.Filter(Registry, "set");

        Assert.Equal(
            withoutSlash.Select(c => c.Name),
            withSlash.Select(c => c.Name));
    }

    [Fact]
    public void OnlySlash_ReturnsAll()
    {
        var result = SlashCommandFilter.Filter(Registry, "/");

        Assert.Equal(Registry.Count, result.Count);
    }

    [Fact]
    public void ExactNameMatch_ReturnsOnlyThatCommand()
    {
        var result = SlashCommandFilter.Filter(Registry, "settings");

        Assert.Single(result);
        Assert.Equal("Settings", result[0].Name);
    }

    [Fact]
    public void UnmatchableQuery_ReturnsEmpty()
    {
        var result = SlashCommandFilter.Filter(Registry, "xyz");

        Assert.Empty(result);
    }

    [Fact]
    public void LetQuery_RanksLetToLambdaFirst()
    {
        // "le" is an initialism for LET and also matches "Load Library"
        // and "Edit Lambda". "LET to LAMBDA" starts with "LE" so should
        // score highest via the word-boundary bonus.
        var result = SlashCommandFilter.Filter(Registry, "le");

        Assert.NotEmpty(result);
        Assert.Equal("LET to LAMBDA", result[0].Name);
    }

    [Fact]
    public void CaseInsensitive_MatchesRegardlessOfCase()
    {
        var upper = SlashCommandFilter.Filter(Registry, "SETTINGS");
        var lower = SlashCommandFilter.Filter(Registry, "settings");

        Assert.Equal(
            lower.Select(c => c.Name),
            upper.Select(c => c.Name));
    }
}
