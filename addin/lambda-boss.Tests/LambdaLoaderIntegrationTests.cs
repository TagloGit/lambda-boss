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
}
