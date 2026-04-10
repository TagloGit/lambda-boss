using Taglo.Excel.Common;

namespace LambdaBoss;

/// <summary>
///     Tracks which LAMBDA libraries have been loaded into which workbooks during the current session.
///     Session-only — not persisted to disk.
/// </summary>
public static class WorkbookTracker
{
    // Keyed by workbook Name (e.g. "Book1.xlsx")
    private static readonly Dictionary<string, List<LoadedLibrary>> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Records that a library was loaded into a workbook.
    /// </summary>
    public static void Record(string workbookName, RepoConfig repoConfig, string libraryName,
        string prefix, IReadOnlyList<(string Name, string Formula)> lambdas)
    {
        if (!_loaded.TryGetValue(workbookName, out var list))
        {
            list = new List<LoadedLibrary>();
            _loaded[workbookName] = list;
        }

        // Replace existing entry for same repo+library (re-load or update)
        list.RemoveAll(l =>
            string.Equals(l.LibraryName, libraryName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.RepoConfig.Url, repoConfig.Url, StringComparison.OrdinalIgnoreCase));

        list.Add(new LoadedLibrary
        {
            RepoConfig = repoConfig,
            LibraryName = libraryName,
            Prefix = prefix,
            Lambdas = lambdas.ToDictionary(l => l.Name, l => l.Formula, StringComparer.OrdinalIgnoreCase),
            LoadedAt = DateTime.UtcNow
        });

        Logger.Info($"WorkbookTracker: Recorded {lambdas.Count} lambdas for '{libraryName}' in '{workbookName}'");
    }

    /// <summary>
    ///     Returns all libraries loaded into the given workbook.
    /// </summary>
    public static IReadOnlyList<LoadedLibrary> GetLoaded(string workbookName)
    {
        if (_loaded.TryGetValue(workbookName, out var list))
            return list;

        return Array.Empty<LoadedLibrary>();
    }

    /// <summary>
    ///     Returns true if the given library is loaded in the workbook.
    /// </summary>
    public static bool IsLoaded(string workbookName, string libraryName, string repoUrl)
    {
        if (!_loaded.TryGetValue(workbookName, out var list))
            return false;

        return list.Any(l =>
            string.Equals(l.LibraryName, libraryName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.RepoConfig.Url, repoUrl, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Finds a loaded library by name and repo URL. Returns null if not found.
    /// </summary>
    public static LoadedLibrary? Find(string workbookName, string libraryName, string repoUrl)
    {
        if (!_loaded.TryGetValue(workbookName, out var list))
            return null;

        return list.FirstOrDefault(l =>
            string.Equals(l.LibraryName, libraryName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.RepoConfig.Url, repoUrl, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Clears all tracking data. Used for testing.
    /// </summary>
    internal static void Clear()
    {
        _loaded.Clear();
    }
}

/// <summary>
///     Represents a library that was loaded into a workbook during this session.
/// </summary>
public sealed class LoadedLibrary
{
    public RepoConfig RepoConfig { get; init; } = null!;
    public string LibraryName { get; init; } = "";
    public string Prefix { get; init; } = "";
    public Dictionary<string, string> Lambdas { get; init; } = new();
    public DateTime LoadedAt { get; init; }
}
