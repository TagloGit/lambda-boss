using Taglo.Excel.Common;

namespace LambdaBoss;

/// <summary>
///     Reads LAMBDA library contents from a local filesystem directory.
///     Always reads fresh from disk — no caching.
/// </summary>
public class LocalDirectorySource
{
    private readonly LocalSourceConfig _config;

    public LocalDirectorySource(LocalSourceConfig config)
    {
        _config = config;
    }

    /// <summary>
    ///     Discovers all library sub-folders (directories containing _library.yaml).
    /// </summary>
    public IReadOnlyList<string> ListLibraries()
    {
        if (!Directory.Exists(_config.Path))
        {
            Logger.Info($"LocalDirectorySource: Path does not exist: {_config.Path}");
            return Array.Empty<string>();
        }

        var libraries = new List<string>();
        foreach (var dir in Directory.GetDirectories(_config.Path))
        {
            var yamlPath = Path.Combine(dir, "_library.yaml");
            if (File.Exists(yamlPath))
            {
                libraries.Add(Path.GetFileName(dir));
            }
        }

        Logger.Info($"LocalDirectorySource: Found {libraries.Count} libraries in {_config.Path}");
        return libraries;
    }

    /// <summary>
    ///     Reads a complete library from disk: metadata + all .lambda file contents.
    /// </summary>
    public FetchedLibrary FetchLibrary(string libraryName)
    {
        var libraryDir = Path.Combine(_config.Path, libraryName);
        var yamlPath = Path.Combine(libraryDir, "_library.yaml");

        var metadata = LibraryMetadata.LoadFromFile(yamlPath);

        var files = new Dictionary<string, string>();
        foreach (var filePath in Directory.GetFiles(libraryDir, "*.lambda"))
        {
            var fileName = Path.GetFileName(filePath);
            files[fileName] = File.ReadAllText(filePath);
        }

        Logger.Info($"LocalDirectorySource: Read library '{libraryName}' ({files.Count} files) from {_config.Path}");
        return new FetchedLibrary(libraryName, metadata, files);
    }
}
