using Taglo.Excel.Common;

namespace LambdaBoss;

/// <summary>
///     Caches fetched GitHub library files to local disk.
///     Cache root: %LOCALAPPDATA%\LambdaBoss\cache\{repo-key}\{library}\
/// </summary>
public class SourceCache
{
    private readonly string _cacheRoot;

    public SourceCache(string? cacheRootOverride = null)
    {
        _cacheRoot = cacheRootOverride
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LambdaBoss",
                "cache");
    }

    /// <summary>
    ///     Writes a fetched library to the cache.
    /// </summary>
    public void Store(RepoConfig config, FetchedLibrary library)
    {
        var dir = GetLibraryDir(config, library.Name);
        Directory.CreateDirectory(dir);

        // Write _library.yaml content
        var yamlContent = $"name: {library.Metadata.Name}\n"
            + $"description: {library.Metadata.Description}\n"
            + $"default_prefix: {library.Metadata.DefaultPrefix}\n";
        File.WriteAllText(Path.Combine(dir, "_library.yaml"), yamlContent);

        // Write each .lambda file
        foreach (var (fileName, content) in library.Files)
        {
            File.WriteAllText(Path.Combine(dir, fileName), content);
        }

        Logger.Info($"SourceCache: Stored library '{library.Name}' ({library.Files.Count} files) for {config.GetCacheKey()}");
    }

    /// <summary>
    ///     Attempts to load a library from cache. Returns null if not cached.
    /// </summary>
    public FetchedLibrary? Load(RepoConfig config, string libraryName)
    {
        var dir = GetLibraryDir(config, libraryName);
        var yamlPath = Path.Combine(dir, "_library.yaml");

        if (!File.Exists(yamlPath))
            return null;

        var metadata = LibraryMetadata.LoadFromFile(yamlPath);

        var files = new Dictionary<string, string>();
        foreach (var filePath in Directory.GetFiles(dir, "*.lambda"))
        {
            var fileName = Path.GetFileName(filePath);
            files[fileName] = File.ReadAllText(filePath);
        }

        Logger.Info($"SourceCache: Loaded library '{libraryName}' ({files.Count} files) from cache");
        return new FetchedLibrary(libraryName, metadata, files);
    }

    /// <summary>
    ///     Returns true if a library is present in the cache.
    /// </summary>
    public bool IsCached(RepoConfig config, string libraryName)
    {
        var yamlPath = Path.Combine(GetLibraryDir(config, libraryName), "_library.yaml");
        return File.Exists(yamlPath);
    }

    /// <summary>
    ///     Invalidates (deletes) the cached data for a specific library.
    /// </summary>
    public void Invalidate(RepoConfig config, string libraryName)
    {
        var dir = GetLibraryDir(config, libraryName);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            Logger.Info($"SourceCache: Invalidated cache for '{libraryName}' in {config.GetCacheKey()}");
        }
    }

    /// <summary>
    ///     Invalidates all cached data for a repo.
    /// </summary>
    public void InvalidateAll(RepoConfig config)
    {
        var dir = GetRepoDir(config);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            Logger.Info($"SourceCache: Invalidated all cache for {config.GetCacheKey()}");
        }
    }

    private string GetRepoDir(RepoConfig config) =>
        Path.Combine(_cacheRoot, config.GetCacheKey());

    private string GetLibraryDir(RepoConfig config, string libraryName) =>
        Path.Combine(_cacheRoot, config.GetCacheKey(), libraryName);
}
