using System.Net.Http;

using Taglo.Excel.Common;

namespace LambdaBoss;

/// <summary>
///     Orchestrates fetching and caching of library data from configured GitHub repos.
///     Provides the data model consumed by the popup UI.
/// </summary>
public class LibraryProvider
{
    private readonly HttpClient _httpClient;
    private readonly SourceCache _cache;
    private readonly List<RepoConfig> _repos;

    private List<LibraryInfo>? _libraries;
    private List<LambdaInfo>? _lambdas;

    public LibraryProvider(IEnumerable<RepoConfig> repos, HttpClient? httpClient = null, SourceCache? cache = null)
    {
        _repos = repos.ToList();
        _httpClient = httpClient ?? new HttpClient();
        _cache = cache ?? new SourceCache();
    }

    /// <summary>
    ///     Returns all known libraries across all enabled repos.
    ///     Fetches from GitHub on first call, then uses in-memory cache.
    /// </summary>
    public async Task<IReadOnlyList<LibraryInfo>> GetLibrariesAsync()
    {
        if (_libraries != null)
            return _libraries;

        await RefreshAsync();
        return _libraries!;
    }

    /// <summary>
    ///     Returns all known lambdas across all enabled repos.
    ///     Fetches from GitHub on first call, then uses in-memory cache.
    /// </summary>
    public async Task<IReadOnlyList<LambdaInfo>> GetAllLambdasAsync()
    {
        if (_lambdas != null)
            return _lambdas;

        await RefreshAsync();
        return _lambdas!;
    }

    /// <summary>
    ///     Fetches a library and returns prefixed name/formula pairs ready for injection.
    /// </summary>
    public async Task<IReadOnlyList<(string Name, string Formula)>> LoadLibraryAsync(
        RepoConfig config, string libraryName, string prefix)
    {
        var source = new GitHubSource(config, _httpClient);

        // Try cache first
        var library = _cache.Load(config, libraryName);
        if (library == null)
        {
            library = await source.FetchLibraryAsync(libraryName);
            try { _cache.Store(config, library); }
            catch (Exception cacheEx)
            {
                Logger.Error($"LibraryProvider: Cache write failed for '{libraryName}'", cacheEx);
            }
        }

        return library.LoadWithPrefix(prefix);
    }

    /// <summary>
    ///     Re-fetches a library from GitHub (bypassing cache), returns prefixed name/formula pairs
    ///     and a diff against the previously loaded version.
    /// </summary>
    public async Task<UpdateResult> UpdateLibraryAsync(
        LoadedLibrary loaded, string? prefixOverride = null)
    {
        var prefix = prefixOverride ?? loaded.Prefix;
        var source = new GitHubSource(loaded.RepoConfig, _httpClient);

        // Always fetch fresh — invalidate cache first
        _cache.Invalidate(loaded.RepoConfig, loaded.LibraryName);

        var library = await source.FetchLibraryAsync(loaded.LibraryName);
        try { _cache.Store(loaded.RepoConfig, library); }
        catch (Exception cacheEx)
        {
            Logger.Error($"LibraryProvider: Cache write failed for '{loaded.LibraryName}'", cacheEx);
        }

        var fresh = library.LoadWithPrefix(prefix);

        // Diff against what was previously loaded
        var added = new List<string>();
        var updated = new List<string>();
        var unchanged = new List<string>();

        foreach (var (name, formula) in fresh)
        {
            if (loaded.Lambdas.TryGetValue(name, out var oldFormula))
            {
                if (string.Equals(formula, oldFormula, StringComparison.Ordinal))
                    unchanged.Add(name);
                else
                    updated.Add(name);
            }
            else
            {
                added.Add(name);
            }
        }

        // Invalidate in-memory list cache so next GetLibrariesAsync reflects changes
        _libraries = null;
        _lambdas = null;

        return new UpdateResult(fresh, added, updated, unchanged);
    }

    /// <summary>
    ///     Clears in-memory cache and re-fetches all data from GitHub.
    /// </summary>
    public async Task RefreshAsync()
    {
        var libraries = new List<LibraryInfo>();
        var lambdas = new List<LambdaInfo>();

        foreach (var config in _repos.Where(r => r.Enabled))
        {
            try
            {
                var source = new GitHubSource(config, _httpClient);
                var (owner, repo) = config.ParseOwnerRepo();
                var repoLabel = $"{owner}/{repo}";

                var libraryNames = await source.ListLibrariesAsync();

                foreach (var libName in libraryNames)
                {
                    FetchedLibrary? fetched;
                    try
                    {
                        // Try cache first
                        fetched = _cache.Load(config, libName);
                        if (fetched == null)
                        {
                            fetched = await source.FetchLibraryAsync(libName);
                            try { _cache.Store(config, fetched); }
                            catch (Exception cacheEx)
                            {
                                Logger.Error($"LibraryProvider: Cache write failed for '{libName}'", cacheEx);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"LibraryProvider: Failed to fetch library '{libName}' from {repoLabel}", ex);
                        continue;
                    }

                    var info = new LibraryInfo
                    {
                        RepoConfig = config,
                        RepoLabel = repoLabel,
                        FolderName = libName,
                        DisplayName = fetched.Metadata.Name,
                        Description = fetched.Metadata.Description,
                        DefaultPrefix = fetched.Metadata.DefaultPrefix,
                        LambdaCount = fetched.Files.Count
                    };
                    libraries.Add(info);

                    // Parse individual lambdas for search
                    foreach (var (fileName, content) in fetched.Files)
                    {
                        try
                        {
                            var (name, formula) = LambdaParser.Parse(content);
                            lambdas.Add(new LambdaInfo
                            {
                                Name = name,
                                Formula = formula,
                                LibraryInfo = info,
                                FileName = fileName
                            });
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"LibraryProvider: Failed to parse '{fileName}' in {libName}", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"LibraryProvider: Failed to list libraries from {config.Url}", ex);
            }
        }

        _libraries = libraries;
        _lambdas = lambdas;

        Logger.Info($"LibraryProvider: Loaded {libraries.Count} libraries, {lambdas.Count} lambdas");
    }
}

/// <summary>
///     Display model for a library in the popup.
/// </summary>
public sealed class LibraryInfo
{
    public RepoConfig RepoConfig { get; init; } = null!;
    public string RepoLabel { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string DefaultPrefix { get; init; } = "";
    public int LambdaCount { get; init; }
}

/// <summary>
///     Display model for an individual lambda in the popup search results.
/// </summary>
public sealed class LambdaInfo
{
    public string Name { get; init; } = "";
    public string Formula { get; init; } = "";
    public LibraryInfo LibraryInfo { get; init; } = null!;
    public string FileName { get; init; } = "";
}

/// <summary>
///     Result of an update operation: the fresh lambdas and a diff summary.
/// </summary>
public sealed class UpdateResult
{
    public IReadOnlyList<(string Name, string Formula)> Lambdas { get; }
    public IReadOnlyList<string> Added { get; }
    public IReadOnlyList<string> Updated { get; }
    public IReadOnlyList<string> Unchanged { get; }

    public UpdateResult(
        IReadOnlyList<(string Name, string Formula)> lambdas,
        IReadOnlyList<string> added,
        IReadOnlyList<string> updated,
        IReadOnlyList<string> unchanged)
    {
        Lambdas = lambdas;
        Added = added;
        Updated = updated;
        Unchanged = unchanged;
    }

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Added.Count > 0) parts.Add($"{Added.Count} new");
            if (Updated.Count > 0) parts.Add($"{Updated.Count} updated");
            if (Unchanged.Count > 0) parts.Add($"{Unchanged.Count} unchanged");
            return parts.Count > 0 ? string.Join(", ", parts) : "no changes";
        }
    }
}
