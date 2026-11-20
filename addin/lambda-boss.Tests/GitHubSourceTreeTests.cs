using System.Net;

using Xunit;

namespace LambdaBoss.Tests;

public class GitHubSourceTreeTests
{
    private static readonly RepoConfig TestConfig = new()
    {
        Url = "https://github.com/TestOwner/test-repo"
    };

    private const string TreeUrl = "api.github.com/repos/TestOwner/test-repo/git/trees/main";
    private const string ContentsUrl = "api.github.com/repos/TestOwner/test-repo/contents/lambdas";

    private const string ContentsLibraries = @"[
        { ""name"": ""string"", ""type"": ""dir"" },
        { ""name"": ""map"", ""type"": ""dir"" },
        { ""name"": ""README.md"", ""type"": ""file"" }
    ]";

    private const string TreeResponse = @"{ ""truncated"": false, ""tree"": [
        { ""path"": ""README.md"", ""type"": ""blob"" },
        { ""path"": ""lambdas"", ""type"": ""tree"" },
        { ""path"": ""lambdas/string"", ""type"": ""tree"" },
        { ""path"": ""lambdas/string/_library.yaml"", ""type"": ""blob"" },
        { ""path"": ""lambdas/string/Upper.lambda"", ""type"": ""blob"" },
        { ""path"": ""lambdas/string/Sep.const"", ""type"": ""blob"" },
        { ""path"": ""lambdas/string/sub"", ""type"": ""tree"" },
        { ""path"": ""lambdas/string/sub/Deep.lambda"", ""type"": ""blob"" },
        { ""path"": ""lambdas/map"", ""type"": ""tree"" },
        { ""path"": ""lambdas/map/Each.lambda"", ""type"": ""blob"" },
        { ""path"": ""lambdas/README.md"", ""type"": ""blob"" }
    ] }";

    [Fact]
    public async Task Tree_AnswersAllListingsWithOneApiCall()
    {
        var handler = new MockHttpHandler();
        handler.Register(TreeUrl, TreeResponse);

        var source = new GitHubSource(TestConfig, new HttpClient(handler));
        var libraries = await source.ListLibrariesAsync();
        var stringFiles = await source.ListLambdaFilesAsync("string");
        var mapFiles = await source.ListLambdaFilesAsync("map");

        Assert.Equal(new[] { "string", "map" }, libraries);
        Assert.Equal(new[] { "Upper.lambda", "Sep.const" }, stringFiles);
        Assert.Equal(new[] { "Each.lambda" }, mapFiles);
        Assert.Equal(1, handler.ApiRequestCount);
    }

    [Fact]
    public async Task Tree_Truncated_FallsBackToContentsApi()
    {
        var handler = new MockHttpHandler();
        handler.Register(TreeUrl, @"{ ""truncated"": true, ""tree"": [] }");
        handler.Register(ContentsUrl, ContentsLibraries);

        var source = new GitHubSource(TestConfig, new HttpClient(handler));
        var libraries = await source.ListLibrariesAsync();

        Assert.Equal(new[] { "string", "map" }, libraries);
        Assert.Contains(handler.Requests, u => u.Contains("/contents/lambdas"));
    }

    [Fact]
    public async Task RateLimit_ThrowsWithResetTime_AndDoesNotFallBack()
    {
        var handler = new MockHttpHandler();
        handler.Register(TreeUrl, "{}", HttpStatusCode.Forbidden, new Dictionary<string, string>
        {
            ["X-RateLimit-Remaining"] = "0",
            ["X-RateLimit-Reset"] = "1700000000"
        });
        handler.Register(ContentsUrl, ContentsLibraries);

        var source = new GitHubSource(TestConfig, new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() => source.ListLibrariesAsync());

        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), ex.ResetUtc);
        Assert.Contains("22:13 UTC", ex.Message);
        Assert.DoesNotContain(handler.Requests, u => u.Contains("/contents/"));
    }

    [Fact]
    public async Task PlainForbidden_FallsBackLikeOtherFailures()
    {
        var handler = new MockHttpHandler();
        handler.Register(TreeUrl, "{}", HttpStatusCode.Forbidden);
        handler.Register(ContentsUrl, ContentsLibraries);

        var source = new GitHubSource(TestConfig, new HttpClient(handler));
        var libraries = await source.ListLibrariesAsync();

        Assert.Equal(2, libraries.Count);
    }
}
