using Xunit;

namespace LambdaBoss.Tests;

public class RepoConfigTests
{
    [Fact]
    public void ParseOwnerRepo_StandardUrl_ExtractsCorrectly()
    {
        var config = new RepoConfig { Url = "https://github.com/TagloGit/lambda-boss" };
        var (owner, repo) = config.ParseOwnerRepo();

        Assert.Equal("TagloGit", owner);
        Assert.Equal("lambda-boss", repo);
    }

    [Fact]
    public void ParseOwnerRepo_TrailingSlash_ExtractsCorrectly()
    {
        var config = new RepoConfig { Url = "https://github.com/TagloGit/lambda-boss/" };
        var (owner, repo) = config.ParseOwnerRepo();

        Assert.Equal("TagloGit", owner);
        Assert.Equal("lambda-boss", repo);
    }

    [Fact]
    public void ParseOwnerRepo_DotGitSuffix_ExtractsCorrectly()
    {
        var config = new RepoConfig { Url = "https://github.com/TagloGit/lambda-boss.git" };
        var (owner, repo) = config.ParseOwnerRepo();

        Assert.Equal("TagloGit", owner);
        Assert.Equal("lambda-boss", repo);
    }

    [Fact]
    public void ParseOwnerRepo_InvalidUrl_Throws()
    {
        var config = new RepoConfig { Url = "https://github.com/solo" };

        Assert.Throws<FormatException>(() => config.ParseOwnerRepo());
    }

    [Fact]
    public void GetCacheKey_ReturnsLowercaseOwnerRepo()
    {
        var config = new RepoConfig { Url = "https://github.com/TagloGit/Lambda-Boss" };
        var key = config.GetCacheKey();

        Assert.Equal("taglogit_lambda-boss", key);
    }

    [Fact]
    public void Defaults_EnabledTrue_LastFetchedNull()
    {
        var config = new RepoConfig();

        Assert.True(config.Enabled);
        Assert.Null(config.LastFetched);
    }
}
