using Xunit;

namespace LambdaBoss.Tests;

public class SourceCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SourceCache _cache;
    private readonly RepoConfig _config;

    public SourceCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LambdaBoss_CacheTests_" + Guid.NewGuid().ToString("N")[..8]);
        _cache = new SourceCache(_tempDir);
        _config = new RepoConfig { Url = "https://github.com/TestOwner/test-repo" };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static FetchedLibrary CreateTestLibrary(string name = "test")
    {
        var metadata = LibraryMetadata.LoadFromString(
            $"name: {name}\ndescription: A test library\ndefault_prefix: tst");

        var files = new Dictionary<string, string>
        {
            ["Double.lambda"] = "Double = LAMBDA(x, x * 2);",
            ["Triple.lambda"] = "Triple = LAMBDA(x, x * 3);"
        };

        return new FetchedLibrary(name, metadata, files);
    }

    [Fact]
    public void IsCached_WhenNotStored_ReturnsFalse()
    {
        Assert.False(_cache.IsCached(_config, "test"));
    }

    [Fact]
    public void Store_ThenIsCached_ReturnsTrue()
    {
        var library = CreateTestLibrary();
        _cache.Store(_config, library);

        Assert.True(_cache.IsCached(_config, "test"));
    }

    [Fact]
    public void Store_ThenLoad_ReturnsLibraryWithCorrectMetadata()
    {
        var library = CreateTestLibrary();
        _cache.Store(_config, library);

        var loaded = _cache.Load(_config, "test");

        Assert.NotNull(loaded);
        Assert.Equal("test", loaded!.Name);
        Assert.Equal("test", loaded.Metadata.Name);
        Assert.Equal("tst", loaded.Metadata.DefaultPrefix);
    }

    [Fact]
    public void Store_ThenLoad_ReturnsAllFiles()
    {
        var library = CreateTestLibrary();
        _cache.Store(_config, library);

        var loaded = _cache.Load(_config, "test");

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Files.Count);
        Assert.Contains("Double.lambda", loaded.Files.Keys);
        Assert.Contains("Triple.lambda", loaded.Files.Keys);
        Assert.Contains("x * 2", loaded.Files["Double.lambda"]);
    }

    [Fact]
    public void Load_WhenNotCached_ReturnsNull()
    {
        var loaded = _cache.Load(_config, "nonexistent");
        Assert.Null(loaded);
    }

    [Fact]
    public void Invalidate_RemovesCachedLibrary()
    {
        var library = CreateTestLibrary();
        _cache.Store(_config, library);
        Assert.True(_cache.IsCached(_config, "test"));

        _cache.Invalidate(_config, "test");

        Assert.False(_cache.IsCached(_config, "test"));
    }

    [Fact]
    public void Invalidate_WhenNotCached_DoesNotThrow()
    {
        _cache.Invalidate(_config, "nonexistent");
    }

    [Fact]
    public void InvalidateAll_RemovesAllLibrariesForRepo()
    {
        _cache.Store(_config, CreateTestLibrary("lib1"));
        _cache.Store(_config, CreateTestLibrary("lib2"));
        Assert.True(_cache.IsCached(_config, "lib1"));
        Assert.True(_cache.IsCached(_config, "lib2"));

        _cache.InvalidateAll(_config);

        Assert.False(_cache.IsCached(_config, "lib1"));
        Assert.False(_cache.IsCached(_config, "lib2"));
    }

    [Fact]
    public void Store_OverwritesExistingCache()
    {
        var library1 = CreateTestLibrary();
        _cache.Store(_config, library1);

        // Create updated library with different file content
        var metadata = LibraryMetadata.LoadFromString("name: test\ndescription: Updated\ndefault_prefix: tst");
        var files = new Dictionary<string, string>
        {
            ["Double.lambda"] = "Double = LAMBDA(x, x * 99);"
        };
        var library2 = new FetchedLibrary("test", metadata, files);
        _cache.Store(_config, library2);

        var loaded = _cache.Load(_config, "test");
        Assert.NotNull(loaded);
        Assert.Contains("x * 99", loaded!.Files["Double.lambda"]);
    }
}
