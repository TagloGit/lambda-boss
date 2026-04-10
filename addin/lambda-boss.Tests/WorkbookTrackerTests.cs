using Xunit;

namespace LambdaBoss.Tests;

public class WorkbookTrackerTests : IDisposable
{
    public WorkbookTrackerTests()
    {
        WorkbookTracker.Clear();
    }

    public void Dispose()
    {
        WorkbookTracker.Clear();
    }

    private static readonly RepoConfig TestConfig = new()
    {
        Url = "https://github.com/TestOwner/test-repo"
    };

    [Fact]
    public void Record_TracksLoadedLibrary()
    {
        var lambdas = new List<(string Name, string Formula)>
        {
            ("str.Split", "=LAMBDA(text, TEXTSPLIT(text))"),
            ("str.PadLeft", "=LAMBDA(text, len, REPT(\" \", len) & text)")
        };

        WorkbookTracker.Record("Book1.xlsx", TestConfig, "string", "str", lambdas);

        Assert.True(WorkbookTracker.IsLoaded("Book1.xlsx", "string", TestConfig.Url));
    }

    [Fact]
    public void IsLoaded_ReturnsFalse_WhenNotLoaded()
    {
        Assert.False(WorkbookTracker.IsLoaded("Book1.xlsx", "string", TestConfig.Url));
    }

    [Fact]
    public void GetLoaded_ReturnsAllLoadedLibraries()
    {
        var lambdas1 = new List<(string, string)> { ("str.Split", "=LAMBDA(x, x)") };
        var lambdas2 = new List<(string, string)> { ("map.BFS", "=LAMBDA(x, x)") };

        WorkbookTracker.Record("Book1.xlsx", TestConfig, "string", "str", lambdas1);
        WorkbookTracker.Record("Book1.xlsx", TestConfig, "map", "map", lambdas2);

        var loaded = WorkbookTracker.GetLoaded("Book1.xlsx");

        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public void Record_ReplacesExistingEntry_ForSameLibrary()
    {
        var lambdas1 = new List<(string, string)> { ("str.Split", "=LAMBDA(x, x)") };
        var lambdas2 = new List<(string, string)>
        {
            ("str.Split", "=LAMBDA(x, x*2)"),
            ("str.PadLeft", "=LAMBDA(x, x)")
        };

        WorkbookTracker.Record("Book1.xlsx", TestConfig, "string", "str", lambdas1);
        WorkbookTracker.Record("Book1.xlsx", TestConfig, "string", "str", lambdas2);

        var loaded = WorkbookTracker.GetLoaded("Book1.xlsx");

        Assert.Single(loaded);
        Assert.Equal(2, loaded[0].Lambdas.Count);
    }

    [Fact]
    public void Find_ReturnsLoadedLibrary()
    {
        var lambdas = new List<(string, string)> { ("str.Split", "=LAMBDA(x, x)") };
        WorkbookTracker.Record("Book1.xlsx", TestConfig, "string", "str", lambdas);

        var found = WorkbookTracker.Find("Book1.xlsx", "string", TestConfig.Url);

        Assert.NotNull(found);
        Assert.Equal("string", found.LibraryName);
        Assert.Equal("str", found.Prefix);
    }

    [Fact]
    public void Find_ReturnsNull_WhenNotLoaded()
    {
        var found = WorkbookTracker.Find("Book1.xlsx", "string", TestConfig.Url);

        Assert.Null(found);
    }

    [Fact]
    public void TracksPerWorkbook()
    {
        var lambdas = new List<(string, string)> { ("str.Split", "=LAMBDA(x, x)") };
        WorkbookTracker.Record("Book1.xlsx", TestConfig, "string", "str", lambdas);

        Assert.True(WorkbookTracker.IsLoaded("Book1.xlsx", "string", TestConfig.Url));
        Assert.False(WorkbookTracker.IsLoaded("Book2.xlsx", "string", TestConfig.Url));
    }
}
