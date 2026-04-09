using Xunit;

namespace LambdaBoss.Tests;

public class LambdaLoaderIntegrationTests
{
    [Fact]
    public void LoadLibrary_TestLibrary_ReturnsAllLambdasWithPrefix()
    {
        // Use the test library that ships with the repo
        var libraryPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "lambdas", "test"));

        var lambdas = LambdaLoader.LoadLibrary(libraryPath);

        Assert.Equal(3, lambdas.Count);
        Assert.All(lambdas, l => Assert.StartsWith("tst.", l.Name));
        Assert.All(lambdas, l => Assert.StartsWith("=LAMBDA(", l.Formula));
    }

    [Fact]
    public void LoadLibrary_TestLibrary_ContainsExpectedNames()
    {
        var libraryPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "lambdas", "test"));

        var lambdas = LambdaLoader.LoadLibrary(libraryPath);
        var names = lambdas.Select(l => l.Name).ToList();

        Assert.Contains("tst.Double", names);
        Assert.Contains("tst.Triple", names);
        Assert.Contains("tst.AddN", names);
    }

    [Fact]
    public void LoadLibrary_NonExistentPath_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            LambdaLoader.LoadLibrary("/nonexistent/path"));
    }
}
