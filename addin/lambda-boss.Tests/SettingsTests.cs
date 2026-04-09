using Xunit;

#pragma warning disable CA1707

namespace LambdaBoss.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public SettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LambdaBoss_SettingsTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaults()
    {
        var settings = Settings.Load(_settingsPath);

        Assert.Single(settings.Repos);
        Assert.Equal("https://github.com/TagloGit/lambda-boss", settings.Repos[0].Url);
        Assert.Equal("^+L", settings.KeyboardShortcut);
        Assert.Equal(60, settings.CacheTtlMinutes);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var settings = new Settings
        {
            KeyboardShortcut = "^+M",
            CacheTtlMinutes = 120
        };
        settings.Repos.Add(new RepoConfig { Url = "https://github.com/Test/repo2" });

        settings.Save(_settingsPath);

        var loaded = Settings.Load(_settingsPath);

        Assert.Equal("^+M", loaded.KeyboardShortcut);
        Assert.Equal(120, loaded.CacheTtlMinutes);
        Assert.Equal(2, loaded.Repos.Count);
    }

    [Fact]
    public void Load_WhenFileIsInvalidJson_ReturnsDefaults()
    {
        File.WriteAllText(_settingsPath, "not valid json {{{");

        var settings = Settings.Load(_settingsPath);

        Assert.Single(settings.Repos);
        Assert.Equal("^+L", settings.KeyboardShortcut);
    }

    [Fact]
    public void Load_WhenReposEmpty_AddsDefaultRepo()
    {
        File.WriteAllText(_settingsPath, """{"Repos":[],"KeyboardShortcut":"^+L","CacheTtlMinutes":60}""");

        var settings = Settings.Load(_settingsPath);

        Assert.Single(settings.Repos);
        Assert.Equal("https://github.com/TagloGit/lambda-boss", settings.Repos[0].Url);
    }

    [Fact]
    public void AddRepo_NewUrl_ReturnsTrue()
    {
        var settings = new Settings();
        var result = settings.AddRepo("https://github.com/Test/new-repo");

        Assert.True(result);
        Assert.Equal(2, settings.Repos.Count);
    }

    [Fact]
    public void AddRepo_DuplicateUrl_ReturnsFalse()
    {
        var settings = new Settings();
        settings.AddRepo("https://github.com/Test/new-repo");
        var result = settings.AddRepo("https://github.com/Test/new-repo");

        Assert.False(result);
        Assert.Equal(2, settings.Repos.Count);
    }

    [Fact]
    public void AddRepo_DuplicateWithTrailingSlash_ReturnsFalse()
    {
        var settings = new Settings();
        settings.AddRepo("https://github.com/Test/new-repo");
        var result = settings.AddRepo("https://github.com/Test/new-repo/");

        Assert.False(result);
    }

    [Fact]
    public void AddRepo_CaseInsensitive_ReturnsFalse()
    {
        var settings = new Settings();
        settings.AddRepo("https://github.com/Test/New-Repo");
        var result = settings.AddRepo("https://github.com/test/new-repo");

        Assert.False(result);
    }

    [Fact]
    public void RemoveRepo_ExistingUrl_ReturnsTrue()
    {
        var settings = new Settings();
        settings.AddRepo("https://github.com/Test/new-repo");

        var result = settings.RemoveRepo("https://github.com/Test/new-repo");

        Assert.True(result);
        Assert.Single(settings.Repos);
    }

    [Fact]
    public void RemoveRepo_NonExistentUrl_ReturnsFalse()
    {
        var settings = new Settings();
        var result = settings.RemoveRepo("https://github.com/Test/nonexistent");

        Assert.False(result);
    }

    [Fact]
    public void EnabledRepos_ExcludesDisabled()
    {
        var settings = new Settings();
        settings.Repos[0].Enabled = false;
        settings.AddRepo("https://github.com/Test/enabled-repo");

        var enabled = settings.EnabledRepos;

        Assert.Single(enabled);
        Assert.Equal("https://github.com/Test/enabled-repo", enabled[0].Url);
    }

    [Fact]
    public void Save_CreatesDirectoryIfNeeded()
    {
        var deepPath = Path.Combine(_tempDir, "sub", "deep", "settings.json");

        var settings = new Settings();
        settings.Save(deepPath);

        Assert.True(File.Exists(deepPath));
    }

    [Fact]
    public void SetCurrent_OverridesSingleton()
    {
        var custom = new Settings { CacheTtlMinutes = 999 };
        Settings.SetCurrent(custom);

        Assert.Equal(999, Settings.Current.CacheTtlMinutes);

        // Reset for other tests
        Settings.SetCurrent(new Settings());
    }
}
