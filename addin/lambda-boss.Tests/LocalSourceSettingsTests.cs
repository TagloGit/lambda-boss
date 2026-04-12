using Xunit;

#pragma warning disable CA1707

namespace LambdaBoss.Tests;

public class LocalSourceSettingsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public LocalSourceSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LambdaBoss_LocalSettings_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void DefaultSettings_HasEmptyLocalSources()
    {
        var settings = new Settings();
        Assert.Empty(settings.LocalSources);
    }

    [Fact]
    public void AddLocalSource_NewPath_ReturnsTrue()
    {
        var settings = new Settings();
        var result = settings.AddLocalSource(@"C:\Users\test\lambdas");

        Assert.True(result);
        Assert.Single(settings.LocalSources);
        Assert.Equal(@"C:\Users\test\lambdas", settings.LocalSources[0].Path);
    }

    [Fact]
    public void AddLocalSource_DuplicatePath_ReturnsFalse()
    {
        var settings = new Settings();
        settings.AddLocalSource(@"C:\Users\test\lambdas");
        var result = settings.AddLocalSource(@"C:\Users\test\lambdas");

        Assert.False(result);
        Assert.Single(settings.LocalSources);
    }

    [Fact]
    public void AddLocalSource_DuplicateWithTrailingSlash_ReturnsFalse()
    {
        var settings = new Settings();
        settings.AddLocalSource(@"C:\Users\test\lambdas");
        var result = settings.AddLocalSource(@"C:\Users\test\lambdas\");

        Assert.False(result);
    }

    [Fact]
    public void AddLocalSource_CaseInsensitive_ReturnsFalse()
    {
        var settings = new Settings();
        settings.AddLocalSource(@"C:\Users\Test\Lambdas");
        var result = settings.AddLocalSource(@"c:\users\test\lambdas");

        Assert.False(result);
    }

    [Fact]
    public void RemoveLocalSource_ExistingPath_ReturnsTrue()
    {
        var settings = new Settings();
        settings.AddLocalSource(@"C:\Users\test\lambdas");

        var result = settings.RemoveLocalSource(@"C:\Users\test\lambdas");

        Assert.True(result);
        Assert.Empty(settings.LocalSources);
    }

    [Fact]
    public void RemoveLocalSource_NonExistentPath_ReturnsFalse()
    {
        var settings = new Settings();
        var result = settings.RemoveLocalSource(@"C:\nonexistent");

        Assert.False(result);
    }

    [Fact]
    public void EnabledLocalSources_ExcludesDisabled()
    {
        var settings = new Settings();
        settings.AddLocalSource(@"C:\enabled");
        settings.AddLocalSource(@"C:\disabled");
        settings.LocalSources[1].Enabled = false;

        var enabled = settings.EnabledLocalSources;

        Assert.Single(enabled);
        Assert.Equal(@"C:\enabled", enabled[0].Path);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsLocalSources()
    {
        var settings = new Settings();
        settings.AddLocalSource(@"C:\Users\test\lambdas");
        settings.LocalSources[0].Enabled = true;

        settings.Save(_settingsPath);

        var loaded = Settings.Load(_settingsPath);

        Assert.Single(loaded.LocalSources);
        Assert.Equal(@"C:\Users\test\lambdas", loaded.LocalSources[0].Path);
        Assert.True(loaded.LocalSources[0].Enabled);
    }

    [Fact]
    public void LocalSourceConfig_DisplayName_ReturnsFolderName()
    {
        var config = new LocalSourceConfig { Path = @"C:\Users\trjac\repositories\lambda-boss\lambdas" };
        Assert.Equal("lambdas", config.DisplayName);
    }

    [Fact]
    public void LocalSourceConfig_DisplayName_HandlesTrailingSlash()
    {
        var config = new LocalSourceConfig { Path = @"C:\Users\trjac\lambdas\" };
        Assert.Equal("lambdas", config.DisplayName);
    }
}
