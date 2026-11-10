using LambdaBoss.Common;

using Xunit;

#pragma warning disable CA1707

namespace LambdaBoss.Tests;

public class LibraryChannelTests : IDisposable
{
    private const string Yaml = "name: Test\ndescription: Test library\ndefault_prefix: tst";

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "LambdaBoss_ChannelTests_" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static Settings WithRepos(LibraryChannel channel, params string[] urls) => new()
    {
        LibraryChannel = channel,
        Repos = urls.Select(u => new RepoConfig { Url = u }).ToList()
    };

    [Fact]
    public void Default_IsStableAndReadsMain()
    {
        var settings = new Settings();

        Assert.Equal(LibraryChannel.Stable, settings.LibraryChannel);
        Assert.Equal("main", settings.EnabledRepos.Single().Branch);
    }

    [Fact]
    public void Labs_PointsHomeRepoAtDevelop()
    {
        var settings = WithRepos(LibraryChannel.Labs, RepoConfig.HomeRepoUrl);

        Assert.Equal("develop", settings.EnabledRepos.Single().Branch);
    }

    [Theory]
    [InlineData("https://github.com/taglogit/LAMBDA-boss/")]
    [InlineData("https://github.com/TagloGit/lambda-boss.git")]
    public void Labs_RecognisesHomeRepoSpellings(string url)
    {
        var settings = WithRepos(LibraryChannel.Labs, url);

        Assert.Equal("develop", settings.EnabledRepos.Single().Branch);
    }

    [Fact]
    public void Labs_LeavesOtherReposOnMain()
    {
        var settings = WithRepos(LibraryChannel.Labs, RepoConfig.HomeRepoUrl, "https://github.com/Other/repo");

        Assert.Equal("main", settings.EnabledRepos.Single(r => !r.IsHomeRepo).Branch);
    }

    [Fact]
    public void SwitchingBackToStable_ReturnsHomeRepoToMain()
    {
        var settings = WithRepos(LibraryChannel.Labs, RepoConfig.HomeRepoUrl);
        _ = settings.EnabledRepos;

        settings.LibraryChannel = LibraryChannel.Stable;

        Assert.Equal("main", settings.EnabledRepos.Single().Branch);
    }

    [Fact]
    public void ChangingChannel_LeavesARunningLoadOnItsBranch()
    {
        var settings = WithRepos(LibraryChannel.Labs, RepoConfig.HomeRepoUrl);
        var running = settings.EnabledRepos.Single();

        settings.LibraryChannel = LibraryChannel.Stable;
        _ = settings.EnabledRepos;

        Assert.Equal("develop", running.Branch);
        Assert.EndsWith("__develop", running.GetCacheKey());
    }

    [Fact]
    public void FetchTime_ReachesTheConfiguredRepo()
    {
        var settings = WithRepos(LibraryChannel.Labs, RepoConfig.HomeRepoUrl);
        var fetched = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);

        settings.EnabledRepos.Single().LastFetched = fetched;

        Assert.Equal(fetched, settings.Repos.Single().LastFetched);
    }

    [Theory]
    [InlineData("\"Nightly\"")]
    [InlineData("7")]
    public void Settings_WithUnknownChannel_KeepTheRestAndReadStable(string channel)
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, "{\"KeyboardShortcut\":\"^+K\",\"LibraryChannel\":" + channel + "}");

        var loaded = Settings.Load(path);

        Assert.Equal("^+K", loaded.KeyboardShortcut);
        Assert.Equal(LibraryChannel.Stable, loaded.LibraryChannel);
    }

    [Fact]
    public void CacheKey_StableKeepsExistingKey_LabsGetsItsOwn()
    {
        var config = new RepoConfig { Url = RepoConfig.HomeRepoUrl };
        var stable = config.GetCacheKey();

        config.Branch = RepoConfig.LabsBranch;

        Assert.Equal("taglogit_lambda-boss", stable);
        Assert.Equal("taglogit_lambda-boss__develop", config.GetCacheKey());
    }

    [Fact]
    public void Settings_RoundTripChannelAndSwitches()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "settings.json");
        var settings = new Settings { LibraryChannel = LibraryChannel.Labs };
        settings.LabsSwitchesOn.Add(Labs.PopupChannelBadge.Id);

        settings.Save(path);
        var loaded = Settings.Load(path);

        Assert.Equal(LibraryChannel.Labs, loaded.LibraryChannel);
        Assert.Contains(Labs.PopupChannelBadge.Id, loaded.LabsSwitchesOn);
        Assert.Contains("\"Labs\"", File.ReadAllText(path));
    }

    [Fact]
    public void Settings_FromBeforeChannels_LoadsAsStableWithSwitchesOff()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, "{\"KeyboardShortcut\":\"^+K\"}");

        var loaded = Settings.Load(path);

        Assert.Equal("^+K", loaded.KeyboardShortcut);
        Assert.Equal(LibraryChannel.Stable, loaded.LibraryChannel);
        Assert.Empty(loaded.LabsSwitchesOn);
    }

    [Fact]
    public void Branch_IsNotSaved()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "settings.json");
        var settings = WithRepos(LibraryChannel.Labs, RepoConfig.HomeRepoUrl);
        _ = settings.EnabledRepos;

        settings.Save(path);

        Assert.DoesNotContain("Branch", File.ReadAllText(path));
    }

    [Fact]
    public async Task GitHubSource_ReadsEveryPathFromTheConfiguredBranch()
    {
        var config = new RepoConfig { Url = "https://github.com/TestOwner/test-repo", Branch = "develop" };
        var handler = new MockHttpHandler();
        handler.Register("contents/lambdas/test?ref=develop", "[{\"name\":\"Double.lambda\",\"type\":\"file\"}]");
        handler.Register("contents/lambdas?ref=develop", "[{\"name\":\"test\",\"type\":\"dir\"}]");
        handler.Register("TestOwner/test-repo/develop/lambdas/test/_library.yaml", Yaml);
        handler.Register("TestOwner/test-repo/develop/lambdas/test/Double.lambda", "Double = LAMBDA(x, x * 2);");
        var source = new GitHubSource(config, new HttpClient(handler));

        Assert.Equal("test", (await source.ListLibrariesAsync()).Single());
        Assert.Single((await source.FetchLibraryAsync("test")).Files);
    }

    [Fact]
    public async Task GitHubSource_StableNamesMainExplicitly()
    {
        // The repo's default branch is develop, so an unqualified contents call would read Labs.
        var config = new RepoConfig { Url = "https://github.com/TestOwner/test-repo" };
        var handler = new MockHttpHandler();
        handler.Register("contents/lambdas?ref=main", "[{\"name\":\"test\",\"type\":\"dir\"}]");
        var source = new GitHubSource(config, new HttpClient(handler));

        Assert.Equal("test", (await source.ListLibrariesAsync()).Single());
    }

    [Fact]
    public void SourceCache_KeepsChannelsApart()
    {
        var cache = new SourceCache(_tempDir);
        var stable = new RepoConfig { Url = RepoConfig.HomeRepoUrl };
        var labs = new RepoConfig { Url = RepoConfig.HomeRepoUrl, Branch = RepoConfig.LabsBranch };
        var metadata = LibraryMetadata.LoadFromString(Yaml);
        var library = new FetchedLibrary("test", metadata, new Dictionary<string, string>());

        cache.Store(labs, library);

        Assert.True(cache.IsCached(labs, "test"));
        Assert.False(cache.IsCached(stable, "test"));
    }

    [Fact]
    public void State_IsOneSwitch_ListedAndOffByDefault()
    {
        Assert.Contains(Labs.State, Labs.All);
        Assert.DoesNotContain(Labs.State.Id, new Settings().LabsSwitchesOn);
        Assert.DoesNotContain(Labs.All, s =>
            s.Id is "state-let-to-lambda" or "state-scaffold" or "state-edit-lambda" or "state-edit");
    }

    [Fact]
    public void State_OldSwitchesMapToTheNewOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lb-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ \"LabsSwitchesOn\": [\"state-edit\", \"check-formula\"] }");
            var loaded = Settings.Load(path);
            Assert.Contains(Labs.State.Id, loaded.LabsSwitchesOn);
            Assert.Contains(Labs.CheckFormula.Id, loaded.LabsSwitchesOn);
            Assert.DoesNotContain("state-edit", loaded.LabsSwitchesOn);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ArgumentHints_AreOnByDefault_AndNoLongerALabsSwitch()
    {
        Assert.True(new Settings().ArgumentHints);
        Assert.DoesNotContain(Labs.All, s => s.Id == "lambda-intellisense");
    }

    [Fact]
    public void ArgumentHints_OldSettingsFileWithoutTheValue_ReadsAsOn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lb-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ \"LabsSwitchesOn\": [\"lambda-intellisense\"] }");
            Assert.True(Settings.Load(path).ArgumentHints);

            File.WriteAllText(path, "{ \"ArgumentHints\": false }");
            Assert.False(Settings.Load(path).ArgumentHints);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LabsSwitches_HaveUniqueIds()
    {
        Assert.Equal(Labs.All.Count, Labs.All.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("0.2.0-dev.57+abcdef0123456789", "abcdef0123456789", "0.2.0-dev.57 (abcdef0)")]
    [InlineData("0.2.0+abc", "abc", "0.2.0 (abc)")]
    [InlineData("0.2.0", null, "0.2.0")]
    public void BuildInfo_ParsesInformationalVersion(string informational, string? commit, string display)
    {
        Assert.Equal(commit, BuildInfo.ParseCommit(informational));
        Assert.Equal(display, BuildInfo.ParseDisplay(informational));
    }

    [Fact]
    public void UpdateChecker_ReadsDevLatestNotes()
    {
        const string notes = "Dev build of `develop`.\r\n\r\nversion: 0.2.0-dev.57\r\ncommit: abc123\r\nbuilt: now";

        Assert.Equal("0.2.0-dev.57", UpdateChecker.ParseNotesField(notes, "version"));
        Assert.Equal("abc123", UpdateChecker.ParseNotesField(notes, "commit"));
        Assert.Null(UpdateChecker.ParseNotesField(notes, "missing"));
        Assert.Null(UpdateChecker.ParseNotesField(null, "commit"));
    }

    [Theory]
    [InlineData("abc", "ABC", false)]
    [InlineData("abc", "def", true)]
    [InlineData(null, "def", true)]
    public void UpdateChecker_ComparesCommits(string? current, string remote, bool different)
    {
        Assert.Equal(different, UpdateChecker.IsDifferentBuild(current, remote));
    }
}
