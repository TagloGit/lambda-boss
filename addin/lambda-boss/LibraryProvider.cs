using System.Net.Http;

using LambdaBoss.Common;

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
    private readonly List<LocalSourceConfig> _localSources;
    private readonly BuiltInLibrarySource _builtIn;

    // Repos (by cache key) whose listing failed on the last refresh. Library loads skip the
    // network for them, so an offline session doesn't wait on a timeout per library.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _offlineRepos = new();

    private List<LibraryInfo>? _libraries;
    private List<LambdaInfo>? _lambdas;

    /// <summary>
    ///     A request that hasn't answered in this long gives up, so an offline machine whose network
    ///     swallows requests falls back to the built-in library in seconds, not HttpClient's 100.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public LibraryProvider(IEnumerable<RepoConfig> repos, HttpClient? httpClient = null,
        SourceCache? cache = null, IEnumerable<LocalSourceConfig>? localSources = null,
        BuiltInLibrarySource? builtIn = null)
    {
        _repos = repos.ToList();
        _localSources = localSources?.ToList() ?? new List<LocalSourceConfig>();
        _httpClient = httpClient ?? new HttpClient { Timeout = RequestTimeout };
        _cache = cache ?? new SourceCache();
        _builtIn = builtIn ?? BuiltInLibrarySource.Default;
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
        FallbackNote = null;
        _loadNotes.TryRemove(libraryName, out _);
        var offline = config.IsHomeRepo && _offlineRepos.ContainsKey(config.GetCacheKey());
        var library = await GetLibraryAsync(config, SourceFor(config), libraryName, offline, fresh: true);
        _loadNotes[libraryName] = FallbackNote;

        // The popup compares workbooks with what a load gives now, not the session's first refresh.
        foreach (var info in _libraries ?? new List<LibraryInfo>())
        {
            if (!info.IsLocal && info.RepoConfig.GetCacheKey() == config.GetCacheKey()
                && string.Equals(info.FolderName, libraryName, StringComparison.OrdinalIgnoreCase))
                info.Library = library;
        }

        return library.LoadWithPrefix(prefix);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _loadNotes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The fallback note of the last load of this library (null: it came from GitHub).</summary>
    public string? FallbackNoteFor(string libraryName) =>
        _loadNotes.TryGetValue(libraryName, out var note) ? note : null;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (GitHubSource Source, DateTime At)> _sources = new();

    /// <summary>
    ///     One source per repo for a minute, so loads in a row (Import All, a reload of several
    ///     libraries) share one listing call; after that a load lists afresh.
    /// </summary>
    private GitHubSource SourceFor(RepoConfig config)
    {
        var key = config.GetCacheKey();
        var now = DateTime.UtcNow;
        if (_sources.TryGetValue(key, out var held) && now - held.At < TimeSpan.FromMinutes(1))
            return held.Source;
        var source = new GitHubSource(config, _httpClient);
        _sources[key] = (source, now);
        return source;
    }

    /// <summary>
    ///     Set when the last refresh or load couldn't use GitHub and fell back to a saved copy: what
    ///     happened and which copy, for the popup's status line. Null when everything came fresh.
    /// </summary>
    public string? FallbackNote { get; private set; }

    /// <summary>
    ///     A GitHub library. With <paramref name="fresh"/> (every load into a workbook) it always comes
    ///     from GitHub when GitHub answers; otherwise (the popup's list) the disk cache within its TTL
    ///     is used first. When GitHub can't be used (a failed fetch, or <paramref name="offline"/>
    ///     after listing failed) the newer of the disk cache and the copy built into the add-in wins,
    ///     so an old cache never outranks a newer add-in, and the reverse (#452).
    /// </summary>
    private async Task<FetchedLibrary> GetLibraryAsync(
        RepoConfig config, GitHubSource source, string libraryName, bool offline, bool fresh = false)
    {
        if (!offline && !fresh)
        {
            var cached = _cache.Load(config, libraryName);
            if (cached != null)
                return cached;
        }

        Exception? failure = null;
        if (!offline)
        {
            try
            {
                var fetched = await source.FetchLibraryAsync(libraryName);
                try { _cache.Store(config, fetched); }
                catch (Exception cacheEx)
                {
                    Logger.Error($"LibraryProvider: Cache write failed for '{libraryName}'", cacheEx);
                }

                return fetched;
            }
            catch (Exception ex)
            {
                Logger.Error($"LibraryProvider: Fetch of '{libraryName}' failed; trying a saved copy", ex);
                failure = ex;
            }
        }

        var why = failure is GitHubRateLimitException limit ? limit.Message
            : offline && _offlineRepos.TryGetValue(config.GetCacheKey(), out var listing) ? listing
            : "GitHub can't be reached";
        if (Fallback(config, libraryName, why) is { } saved)
            return saved;

        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        throw new InvalidOperationException($"Library '{libraryName}' isn't built into the add-in");
    }

    /// <summary>
    ///     The newer of the disk cache (any age) and, for the home repo, the built-in copy; null when
    ///     there's neither. Records which one in <see cref="FallbackNote"/>.
    /// </summary>
    private FetchedLibrary? Fallback(RepoConfig config, string libraryName, string why)
    {
        var builtIn = config.IsHomeRepo ? _builtIn.FetchLibrary(libraryName) : null;
        var cachedAt = _cache.StoredAtUtc(config, libraryName);
        var cacheIsNewer = cachedAt.HasValue
            && (builtIn == null || (_builtIn.BuiltAtUtc.HasValue && cachedAt.Value > _builtIn.BuiltAtUtc.Value));

        if (cacheIsNewer && _cache.Load(config, libraryName, ignoreTtl: true) is { } cached)
        {
            FallbackNote = $"{why}: using the copy saved {cachedAt!.Value:d MMM HH:mm} UTC";
            Logger.Info($"LibraryProvider: '{libraryName}' from the disk cache ({cachedAt:o}): {why}");
            return cached;
        }

        if (builtIn == null)
            return null;

        FallbackNote = _builtIn.BuiltAtUtc is { } built
            ? $"{why}: using the library built into the add-in ({built:d MMM HH:mm} UTC)"
            : $"{why}: using the library built into the add-in";
        Logger.Info($"LibraryProvider: '{libraryName}' from the built-in copy: {why}");
        return builtIn;
    }

    /// <summary>
    ///     Loads a library from a local directory source. Always reads fresh from disk.
    /// </summary>
    public IReadOnlyList<(string Name, string Formula)> LoadLocalLibrary(
        LocalSourceConfig config, string libraryName, string prefix)
    {
        var source = new LocalDirectorySource(config);
        var library = source.FetchLibrary(libraryName);
        return library.LoadWithPrefix(prefix);
    }

    /// <summary>
    ///     Invalidates the disk cache for a specific library so the next LoadLibraryAsync re-fetches.
    /// </summary>
    public void InvalidateCache(RepoConfig config, string libraryName)
    {
        // Offline the cache can't be replaced, so keep it (the popup invalidates before each load).
        if (config.IsHomeRepo && _offlineRepos.ContainsKey(config.GetCacheKey()))
            return;

        _cache.Invalidate(config, libraryName);
    }

    /// <summary>
    ///     Clears in-memory cache and re-fetches all data from GitHub.
    /// </summary>
    /// <param name="invalidateCache">
    ///     When true, the disk cache for every enabled repo is deleted first so all libraries are
    ///     re-fetched from GitHub. When false, cached libraries within their TTL are reused.
    /// </param>
    public async Task RefreshAsync(bool invalidateCache = false)
    {
        FallbackNote = null;
        var libraries = new List<LibraryInfo>();
        var lambdas = new List<LambdaInfo>();

        foreach (var config in _repos.Where(r => r.Enabled))
        {
            try
            {
                var source = new GitHubSource(config, _httpClient);
                var (owner, repo) = config.ParseOwnerRepo();
                var repoLabel = $"{owner}/{repo}";

                IReadOnlyList<string> libraryNames;
                var offline = false;
                try
                {
                    libraryNames = await source.ListLibrariesAsync();
                    _offlineRepos.TryRemove(config.GetCacheKey(), out _);
                }
                catch (Exception ex) when (config.IsHomeRepo && _builtIn.ListLibraries().Count > 0)
                {
                    // No network (a competition machine): the library built into the add-in.
                    Logger.Error($"LibraryProvider: Can't list {repoLabel}; using the libraries built into the add-in", ex);
                    libraryNames = _builtIn.ListLibraries();
                    offline = true;
                    _offlineRepos[config.GetCacheKey()] = ex is GitHubRateLimitException limit ? limit.Message : "GitHub can't be reached";
                }

                // Only once GitHub has answered: offline, a refresh keeps the cache it can't replace.
                if (invalidateCache && !offline)
                {
                    try { _cache.InvalidateAll(config); }
                    catch (Exception cacheEx)
                    {
                        Logger.Error($"LibraryProvider: Cache invalidation failed for {repoLabel}", cacheEx);
                    }
                }

                foreach (var libName in libraryNames)
                {
                    FetchedLibrary? fetched;
                    try
                    {
                        fetched = await GetLibraryAsync(config, source, libName, offline);
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
                        LambdaCount = fetched.Files.Count,
                        Library = fetched
                    };
                    libraries.Add(info);

                    // Parse individual lambdas and constants for search.
                    PopulateSearchEntries(fetched, info, lambdas, libName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"LibraryProvider: Failed to list libraries from {config.Url}", ex);
            }
        }

        // Load from local directory sources
        foreach (var localConfig in _localSources.Where(s => s.Enabled))
        {
            try
            {
                var source = new LocalDirectorySource(localConfig);
                var libraryNames = source.ListLibraries();

                foreach (var libName in libraryNames)
                {
                    FetchedLibrary fetched;
                    try
                    {
                        fetched = source.FetchLibrary(libName);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"LibraryProvider: Failed to read local library '{libName}' from {localConfig.Path}", ex);
                        continue;
                    }

                    var info = new LibraryInfo
                    {
                        RepoConfig = null!,
                        LocalSourceConfig = localConfig,
                        RepoLabel = localConfig.DisplayName,
                        FolderName = libName,
                        DisplayName = fetched.Metadata.Name,
                        Description = fetched.Metadata.Description,
                        DefaultPrefix = fetched.Metadata.DefaultPrefix,
                        LambdaCount = fetched.Files.Count,
                        Library = fetched
                    };
                    libraries.Add(info);

                    PopulateSearchEntries(fetched, info, lambdas, $"local {libName}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"LibraryProvider: Failed to list local libraries from {localConfig.Path}", ex);
            }
        }

        _libraries = libraries;
        _lambdas = lambdas;

        // Argument hints (spec 0013) know every LAMBDA the popup does. A no-op unless the
        // hints are running.
        LambdaHints.OnLibrariesRefreshed(lambdas);

        Logger.Info($"LibraryProvider: Loaded {libraries.Count} libraries, {lambdas.Count} lambdas");
    }

    /// <summary>
    ///     Parses every file in a fetched library into <see cref="LambdaInfo"/> search entries,
    ///     branching on extension: .const files parse as constants, everything else as LAMBDAs.
    /// </summary>
    private static void PopulateSearchEntries(
        FetchedLibrary fetched, LibraryInfo info, List<LambdaInfo> lambdas, string libLabel)
    {
        foreach (var (fileName, content) in fetched.Files)
        {
            try
            {
                var isConstant = fileName.EndsWith(".const", StringComparison.OrdinalIgnoreCase);
                var (name, formula) = isConstant
                    ? ConstParser.Parse(content)
                    : LambdaParser.Parse(content);
                var description = LambdaParser.ExtractDescription(content) ?? "";
                LambdaDoc? doc = null;
                if (!isConstant)
                {
                    try { doc = LambdaDocParser.Parse(content); }
                    catch (Exception docEx)
                    {
                        Logger.Error($"LibraryProvider: No hint metadata for '{fileName}' in {libLabel}", docEx);
                    }
                }

                lambdas.Add(new LambdaInfo
                {
                    Name = name,
                    Formula = formula,
                    LibraryInfo = info,
                    FileName = fileName,
                    Description = description,
                    IsConstant = isConstant,
                    Doc = doc
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"LibraryProvider: Failed to parse '{fileName}' in {libLabel}", ex);
            }
        }
    }
}

/// <summary>
///     Display model for a library in the popup.
/// </summary>
public sealed class LibraryInfo
{
    public RepoConfig RepoConfig { get; init; } = null!;
    public LocalSourceConfig? LocalSourceConfig { get; init; }
    public string RepoLabel { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string DefaultPrefix { get; init; } = "";
    public int LambdaCount { get; init; }

    /// <summary>
    ///     The library's files as the add-in has them now, so the popup can tell whether a
    ///     workbook's copy is out of date (#452). Null only for hand-built test data.
    /// </summary>
    public FetchedLibrary? Library { get; internal set; }

    /// <summary>
    ///     Whether this library comes from a local directory source.
    /// </summary>
    public bool IsLocal => LocalSourceConfig != null;
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

    /// <summary>
    ///     Description extracted from the header comment of the .lambda file.
    ///     Empty string when no description was found.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    ///     Whether this entry is a named-range constant (.const) rather than a LAMBDA.
    /// </summary>
    public bool IsConstant { get; init; }

    /// <summary>
    ///     The header metadata the argument hints show (spec 0013). Null for constants and
    ///     for files whose header can't be read.
    /// </summary>
    public LambdaDoc? Doc { get; init; }
}

