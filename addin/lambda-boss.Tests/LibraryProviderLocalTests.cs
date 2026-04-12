using Xunit;

#pragma warning disable CA1707

namespace LambdaBoss.Tests;

public class LibraryProviderLocalTests : IDisposable
{
    private readonly string _tempDir;

    public LibraryProviderLocalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LambdaBoss_ProviderLocal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private void CreateTestLibrary(string name, string prefix, params string[] lambdaNames)
    {
        var libDir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "_library.yaml"),
            $"name: {name}\ndescription: Test lib\ndefault_prefix: {prefix}");

        foreach (var lambdaName in lambdaNames)
        {
            File.WriteAllText(Path.Combine(libDir, $"{lambdaName}.lambda"),
                $"{lambdaName} = LAMBDA(x, x);");
        }
    }

    [Fact]
    public async Task RefreshAsync_IncludesLocalSources()
    {
        CreateTestLibrary("math", "m", "Double", "Triple");

        var localConfig = new LocalSourceConfig { Path = _tempDir };
        var provider = new LibraryProvider(
            Array.Empty<RepoConfig>(),
            localSources: new[] { localConfig });

        var libraries = await provider.GetLibrariesAsync();

        Assert.Single(libraries);
        Assert.Equal("math", libraries[0].DisplayName);
        Assert.True(libraries[0].IsLocal);
        Assert.Equal(2, libraries[0].LambdaCount);
    }

    [Fact]
    public async Task RefreshAsync_LocalSourcePopulatesLambdas()
    {
        CreateTestLibrary("string", "str", "Split", "Join");

        var localConfig = new LocalSourceConfig { Path = _tempDir };
        var provider = new LibraryProvider(
            Array.Empty<RepoConfig>(),
            localSources: new[] { localConfig });

        var lambdas = await provider.GetAllLambdasAsync();

        Assert.Equal(2, lambdas.Count);
        Assert.Contains(lambdas, l => l.Name == "Split");
        Assert.Contains(lambdas, l => l.Name == "Join");
    }

    [Fact]
    public void LoadLocalLibrary_ReturnsPrefixedFormulas()
    {
        CreateTestLibrary("math", "m", "Double");

        var localConfig = new LocalSourceConfig { Path = _tempDir };
        var provider = new LibraryProvider(
            Array.Empty<RepoConfig>(),
            localSources: new[] { localConfig });

        var results = provider.LoadLocalLibrary(localConfig, "math", "m");

        Assert.Single(results);
        Assert.Equal("m.Double", results[0].Name);
    }

    [Fact]
    public async Task RefreshAsync_DisabledLocalSource_IsExcluded()
    {
        CreateTestLibrary("math", "m", "Double");

        var localConfig = new LocalSourceConfig { Path = _tempDir, Enabled = false };
        var provider = new LibraryProvider(
            Array.Empty<RepoConfig>(),
            localSources: new[] { localConfig });

        var libraries = await provider.GetLibrariesAsync();

        Assert.Empty(libraries);
    }

    [Fact]
    public async Task RefreshAsync_AlwaysReadsFreshFromDisk()
    {
        CreateTestLibrary("math", "m", "Double");

        var localConfig = new LocalSourceConfig { Path = _tempDir };
        var provider = new LibraryProvider(
            Array.Empty<RepoConfig>(),
            localSources: new[] { localConfig });

        // First load
        var lambdas1 = await provider.GetAllLambdasAsync();
        Assert.Single(lambdas1);

        // Add a new lambda file
        var libDir = Path.Combine(_tempDir, "math");
        File.WriteAllText(Path.Combine(libDir, "Triple.lambda"), "Triple = LAMBDA(x, x*3);");

        // Force refresh (simulates reload)
        await provider.RefreshAsync();
        var lambdas2 = await provider.GetAllLambdasAsync();

        Assert.Equal(2, lambdas2.Count);
    }
}
