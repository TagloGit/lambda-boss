using Xunit;

namespace LambdaBoss.Tests;

public class LambdaLoaderIntegrationTests
{
    [Fact]
    public void LoadLibrary_StringLibrary_ReturnsLambdasWithPrefix()
    {
        var libraryPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "lambdas", "string"));

        var lambdas = LambdaLoader.LoadLibrary(libraryPath);

        Assert.NotEmpty(lambdas);
        Assert.All(lambdas, l => Assert.StartsWith("string.", l.Name));
        Assert.All(lambdas, l => Assert.StartsWith("=LAMBDA(", l.Formula));
    }

    [Fact]
    public void LoadLibrary_StringLibrary_ContainsExplode()
    {
        var libraryPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "lambdas", "string"));

        var lambdas = LambdaLoader.LoadLibrary(libraryPath);
        var names = lambdas.Select(l => l.Name).ToList();

        Assert.Contains("string.EXPLODE", names);
    }

    [Fact]
    public void LoadLibrary_NonExistentPath_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            LambdaLoader.LoadLibrary("/nonexistent/path"));
    }

    [Fact]
    public void LoadLibrary_MapsLibrary_LoadsConstantsAlongsideLambdas()
    {
        var libraryPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "lambdas", "maps"));

        var loaded = LambdaLoader.LoadLibrary(libraryPath);
        var names = loaded.Select(l => l.Name).ToList();

        // The shipped arrow/direction constants load with the library prefix.
        Assert.Contains("maps._arrowsAll", names);
        Assert.Contains("maps._dirAll", names);

        // Lambdas in the same library still load.
        Assert.Contains("maps.CELLTOPOS", names);

        // The constant's literal is preserved (array-row separators intact).
        var arrowsAll = loaded.Single(l => l.Name == "maps._arrowsAll");
        Assert.Equal("={\"↑\";\"↓\";\"←\";\"→\";\"↖\";\"↗\";\"↙\";\"↘\"}", arrowsAll.Formula);

        var dirAll = loaded.Single(l => l.Name == "maps._dirAll");
        Assert.Equal("={-100000;100000;-1;1;-100001;-99999;99999;100001}", dirAll.Formula);
    }
}
