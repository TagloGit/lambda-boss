using Xunit;

namespace LambdaBoss.Tests;

public class LibraryProviderTests : IDisposable
{
    private readonly string _tempCacheDir = Path.Combine(Path.GetTempPath(), $"lb-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempCacheDir))
            Directory.Delete(_tempCacheDir, recursive: true);
    }

    private LibraryProvider CreateProvider(MockHttpHandler handler)
    {
        return new LibraryProvider(
            new[] { TestConfig },
            new HttpClient(handler),
            new SourceCache(_tempCacheDir));
    }
    private static readonly RepoConfig TestConfig = new()
    {
        Url = "https://github.com/TestOwner/test-repo"
    };

    private static readonly string LibrariesApiResponse = @"[
        { ""name"": ""string"", ""type"": ""dir"" },
        { ""name"": ""map"", ""type"": ""dir"" },
        { ""name"": ""README.md"", ""type"": ""file"" }
    ]";

    private static readonly string StringLibraryContentsApiResponse = @"[
        { ""name"": ""_library.yaml"", ""type"": ""file"" },
        { ""name"": ""Split.lambda"", ""type"": ""file"" },
        { ""name"": ""PadLeft.lambda"", ""type"": ""file"" }
    ]";

    private static readonly string MapLibraryContentsApiResponse = @"[
        { ""name"": ""_library.yaml"", ""type"": ""file"" },
        { ""name"": ""BFS.lambda"", ""type"": ""file"" }
    ]";

    private static readonly string StringYaml = @"name: String Utilities
description: Common string manipulation functions
default_prefix: str";

    private static readonly string MapYaml = @"name: Map Navigation
description: Grid pathfinding functions
default_prefix: map";

    private static readonly string SplitLambda = "Split = LAMBDA(text, delimiter, TEXTSPLIT(text, delimiter));";
    private static readonly string PadLeftLambda = "PadLeft = LAMBDA(text, length, REPT(\" \", length - LEN(text)) & text);";
    private static readonly string BfsLambda = "BFS = LAMBDA(grid, start, grid);";

    private static MockHttpHandler CreateFullHandler()
    {
        var handler = new MockHttpHandler();

        // Register specific library URLs BEFORE the general listing URL,
        // because MockHttpHandler uses Contains matching and the general pattern
        // is a substring of the specific patterns.

        // String library
        handler.Register("api.github.com/repos/TestOwner/test-repo/contents/lambdas/string", StringLibraryContentsApiResponse);
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/string/_library.yaml", StringYaml);
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/string/Split.lambda", SplitLambda);
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/string/PadLeft.lambda", PadLeftLambda);

        // Map library
        handler.Register("api.github.com/repos/TestOwner/test-repo/contents/lambdas/map", MapLibraryContentsApiResponse);
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/map/_library.yaml", MapYaml);
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/map/BFS.lambda", BfsLambda);

        // General library listing (last — so specific paths match first)
        handler.Register("api.github.com/repos/TestOwner/test-repo/contents/lambdas", LibrariesApiResponse);

        return handler;
    }

    [Fact]
    public async Task GetLibrariesAsync_ReturnsBothLibraries()
    {
        var handler = CreateFullHandler();
        var provider = CreateProvider(handler);

        var libraries = await provider.GetLibrariesAsync();

        Assert.Equal(2, libraries.Count);
        Assert.Contains(libraries, l => l.DisplayName == "String Utilities");
        Assert.Contains(libraries, l => l.DisplayName == "Map Navigation");
    }

    [Fact]
    public async Task GetLibrariesAsync_PopulatesMetadata()
    {
        var handler = CreateFullHandler();
        var provider = CreateProvider(handler);

        var libraries = await provider.GetLibrariesAsync();

        var stringLib = libraries.First(l => l.FolderName == "string");
        Assert.Equal("String Utilities", stringLib.DisplayName);
        Assert.Equal("Common string manipulation functions", stringLib.Description);
        Assert.Equal("str", stringLib.DefaultPrefix);
        Assert.Equal(2, stringLib.LambdaCount);
        Assert.Equal("TestOwner/test-repo", stringLib.RepoLabel);
    }

    [Fact]
    public async Task GetAllLambdasAsync_ReturnsAllLambdas()
    {
        var handler = CreateFullHandler();
        var provider = CreateProvider(handler);

        var lambdas = await provider.GetAllLambdasAsync();

        Assert.Equal(3, lambdas.Count);
        Assert.Contains(lambdas, l => l.Name == "Split");
        Assert.Contains(lambdas, l => l.Name == "PadLeft");
        Assert.Contains(lambdas, l => l.Name == "BFS");
    }

    [Fact]
    public async Task GetAllLambdasAsync_LinksToCorrectLibrary()
    {
        var handler = CreateFullHandler();
        var provider = CreateProvider(handler);

        var lambdas = await provider.GetAllLambdasAsync();

        var split = lambdas.First(l => l.Name == "Split");
        Assert.Equal("string", split.LibraryInfo.FolderName);
        Assert.Equal("String Utilities", split.LibraryInfo.DisplayName);

        var bfs = lambdas.First(l => l.Name == "BFS");
        Assert.Equal("map", bfs.LibraryInfo.FolderName);
    }

    [Fact]
    public async Task GetLibrariesAsync_CachesInMemory()
    {
        var handler = CreateFullHandler();
        var provider = CreateProvider(handler);

        var first = await provider.GetLibrariesAsync();
        var second = await provider.GetLibrariesAsync();

        // Same reference means no re-fetch
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetLibrariesAsync_SkipsDisabledRepos()
    {
        var disabledConfig = new RepoConfig
        {
            Url = "https://github.com/TestOwner/test-repo",
            Enabled = false
        };

        var handler = CreateFullHandler();
        var provider = new LibraryProvider(
            new[] { disabledConfig },
            new HttpClient(handler),
            new SourceCache(_tempCacheDir));

        var libraries = await provider.GetLibrariesAsync();

        Assert.Empty(libraries);
    }

    [Fact]
    public async Task LoadLibraryAsync_ReturnsPrefixedLambdas()
    {
        var handler = CreateFullHandler();
        var provider = CreateProvider(handler);

        var lambdas = await provider.LoadLibraryAsync(TestConfig, "string", "str");

        Assert.Equal(2, lambdas.Count);
        Assert.Contains(lambdas, l => l.Name == "str.Split");
        Assert.Contains(lambdas, l => l.Name == "str.PadLeft");
    }

    [Fact]
    public async Task LoadLibraryAsync_EmptyPrefix_ReturnsBarenames()
    {
        var handler = CreateFullHandler();
        var provider = CreateProvider(handler);

        var lambdas = await provider.LoadLibraryAsync(TestConfig, "string", "");

        Assert.Contains(lambdas, l => l.Name == "Split");
        Assert.Contains(lambdas, l => l.Name == "PadLeft");
    }

    [Fact]
    public async Task InvalidateCache_ForcesRefetch()
    {
        var handler = CreateFullHandler();
        var provider = CreateProvider(handler);

        // Load once to populate cache
        await provider.LoadLibraryAsync(TestConfig, "string", "str");

        // Invalidate and load again — should still work (re-fetches)
        provider.InvalidateCache(TestConfig, "string");
        var lambdas = await provider.LoadLibraryAsync(TestConfig, "string", "str");

        Assert.Equal(2, lambdas.Count);
    }

    [Fact]
    public async Task RefreshAsync_ClearsAndReloadsData()
    {
        var handler = CreateFullHandler();
        var provider = CreateProvider(handler);

        var first = await provider.GetLibrariesAsync();
        Assert.Equal(2, first.Count);

        await provider.RefreshAsync();
        var second = await provider.GetLibrariesAsync();

        // Different reference means data was re-fetched
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
    }
}
