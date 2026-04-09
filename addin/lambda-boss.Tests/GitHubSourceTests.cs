using Xunit;

namespace LambdaBoss.Tests;

public class GitHubSourceTests
{
    private static readonly RepoConfig TestConfig = new()
    {
        Url = "https://github.com/TestOwner/test-repo"
    };

    private static readonly string LibrariesApiResponse = @"[
        { ""name"": ""string"", ""type"": ""dir"" },
        { ""name"": ""map"", ""type"": ""dir"" },
        { ""name"": ""README.md"", ""type"": ""file"" }
    ]";

    private static readonly string LibraryContentsApiResponse = @"[
        { ""name"": ""_library.yaml"", ""type"": ""file"" },
        { ""name"": ""Double.lambda"", ""type"": ""file"" },
        { ""name"": ""Triple.lambda"", ""type"": ""file"" }
    ]";

    private static readonly string LibraryYaml = @"name: Test
description: Test library
default_prefix: tst";

    private static readonly string DoubleLambda =
        "Double = LAMBDA(x, x * 2);";

    private static readonly string TripleLambda =
        "Triple = LAMBDA(x, x * 3);";

    [Fact]
    public async Task ListLibraries_ReturnsOnlyDirectories()
    {
        var handler = new MockHttpHandler();
        handler.Register("api.github.com/repos/TestOwner/test-repo/contents/lambdas", LibrariesApiResponse);

        var source = new GitHubSource(TestConfig, new HttpClient(handler));
        var libraries = await source.ListLibrariesAsync();

        Assert.Equal(2, libraries.Count);
        Assert.Contains("string", libraries);
        Assert.Contains("map", libraries);
    }

    [Fact]
    public async Task FetchFile_ReturnsContent()
    {
        var handler = new MockHttpHandler();
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/test/_library.yaml", LibraryYaml);

        var source = new GitHubSource(TestConfig, new HttpClient(handler));
        var content = await source.FetchFileAsync("lambdas/test/_library.yaml");

        Assert.Contains("default_prefix: tst", content);
    }

    [Fact]
    public async Task FetchLibraryMetadata_DeserializesYaml()
    {
        var handler = new MockHttpHandler();
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/test/_library.yaml", LibraryYaml);

        var source = new GitHubSource(TestConfig, new HttpClient(handler));
        var metadata = await source.FetchLibraryMetadataAsync("test");

        Assert.Equal("Test", metadata.Name);
        Assert.Equal("tst", metadata.DefaultPrefix);
    }

    [Fact]
    public async Task ListLambdaFiles_ReturnsOnlyLambdaFiles()
    {
        var handler = new MockHttpHandler();
        handler.Register("api.github.com/repos/TestOwner/test-repo/contents/lambdas/test", LibraryContentsApiResponse);

        var source = new GitHubSource(TestConfig, new HttpClient(handler));
        var files = await source.ListLambdaFilesAsync("test");

        Assert.Equal(2, files.Count);
        Assert.Contains("Double.lambda", files);
        Assert.Contains("Triple.lambda", files);
    }

    [Fact]
    public async Task FetchLibrary_ReturnsCompleteLibrary()
    {
        var handler = new MockHttpHandler();
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/test/_library.yaml", LibraryYaml);
        handler.Register("api.github.com/repos/TestOwner/test-repo/contents/lambdas/test", LibraryContentsApiResponse);
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/test/Double.lambda", DoubleLambda);
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/test/Triple.lambda", TripleLambda);

        var source = new GitHubSource(TestConfig, new HttpClient(handler));
        var library = await source.FetchLibraryAsync("test");

        Assert.Equal("test", library.Name);
        Assert.Equal("Test", library.Metadata.Name);
        Assert.Equal("tst", library.Metadata.DefaultPrefix);
        Assert.Equal(2, library.Files.Count);
        Assert.Contains("Double.lambda", library.Files.Keys);
        Assert.Contains("Triple.lambda", library.Files.Keys);
    }

    [Fact]
    public async Task FetchLibrary_UpdatesLastFetched()
    {
        var config = new RepoConfig { Url = "https://github.com/TestOwner/test-repo" };
        var handler = new MockHttpHandler();
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/test/_library.yaml", LibraryYaml);
        handler.Register("api.github.com/repos/TestOwner/test-repo/contents/lambdas/test", LibraryContentsApiResponse);
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/test/Double.lambda", DoubleLambda);
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/test/Triple.lambda", TripleLambda);

        Assert.Null(config.LastFetched);

        var source = new GitHubSource(config, new HttpClient(handler));
        await source.FetchLibraryAsync("test");

        Assert.NotNull(config.LastFetched);
    }

    [Fact]
    public async Task FetchLibrary_LoadWithPrefix_AppliesPrefixCorrectly()
    {
        var handler = new MockHttpHandler();
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/test/_library.yaml", LibraryYaml);
        handler.Register("api.github.com/repos/TestOwner/test-repo/contents/lambdas/test", LibraryContentsApiResponse);
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/test/Double.lambda", DoubleLambda);
        handler.Register("raw.githubusercontent.com/TestOwner/test-repo/main/lambdas/test/Triple.lambda", TripleLambda);

        var source = new GitHubSource(TestConfig, new HttpClient(handler));
        var library = await source.FetchLibraryAsync("test");
        var lambdas = library.LoadWithPrefix();

        Assert.Equal(2, lambdas.Count);
        Assert.Contains(lambdas, l => l.Name == "tst.Double");
        Assert.Contains(lambdas, l => l.Name == "tst.Triple");
    }
}
