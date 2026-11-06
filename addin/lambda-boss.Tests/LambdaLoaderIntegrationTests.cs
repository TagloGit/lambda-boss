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
        Assert.Contains("maps.TOPOS", names);

        // The constant's literal is preserved (array-row separators intact).
        var arrowsAll = loaded.Single(l => l.Name == "maps._arrowsAll");
        Assert.Equal("={\"↑\";\"↗\";\"→\";\"↘\";\"↓\";\"↙\";\"←\";\"↖\"}", arrowsAll.Formula);

        // A constant derived from another constant gets the other's reference prefixed too (#380).
        var dirAll = loaded.Single(l => l.Name == "maps._dirAll");
        Assert.Equal("=maps._mult * {-1;-1;0;1;1;1;0;-1} + {0;1;1;1;0;-1;-1;-1}", dirAll.Formula);

        // Lambdas read the one multiplier constant, prefixed with the library (#380).
        var topos = loaded.Single(l => l.Name == "maps.TOPOS");
        Assert.Contains("maps._mult", topos.Formula);
        Assert.Contains("IF(ISOMITTED(mult), maps._mult, mult)", topos.Formula);
    }
}
