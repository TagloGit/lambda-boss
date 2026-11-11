using LambdaBoss.UI;

using Xunit;

namespace LambdaBoss.Tests;

public class ExactLibraryMatchTests
{
    private static readonly IReadOnlyList<LibraryDisplayItem> Libraries = new[]
    {
        new LibraryDisplayItem { DisplayName = "Array", DefaultPrefix = "array" },
        new LibraryDisplayItem { DisplayName = "Lookup", DefaultPrefix = "lookup" },
        new LibraryDisplayItem { DisplayName = "Maps", DefaultPrefix = "maps" },
        new LibraryDisplayItem { DisplayName = "Grid Tools", DefaultPrefix = "grid" },
    };

    [Theory]
    [InlineData("maps", "Maps")]
    [InlineData("MAPS", "Maps")]
    [InlineData("  maps ", "Maps")]
    [InlineData("lookup", "Lookup")]
    [InlineData("grid tools", "Grid Tools")]
    [InlineData("grid", "Grid Tools")]
    public void TypedNameOrPrefix_FindsThatLibrary(string typed, string expected)
    {
        Assert.Equal(expected, ExactLibraryMatch.Find(Libraries, typed)?.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("map")]
    [InlineData("maze")]
    [InlineData("tlookup")]
    [InlineData("maps2")]
    public void AnythingElse_FindsNothing_SoTheRankingDecides(string typed)
    {
        Assert.Null(ExactLibraryMatch.Find(Libraries, typed));
    }

    [Fact]
    public void NameMatch_BeatsAnotherLibrarysPrefix()
    {
        var libs = new[]
        {
            new LibraryDisplayItem { DisplayName = "Extras", DefaultPrefix = "maps" },
            new LibraryDisplayItem { DisplayName = "Maps", DefaultPrefix = "m" },
        };

        Assert.Equal("Maps", ExactLibraryMatch.Find(libs, "maps")?.DisplayName);
    }
}
