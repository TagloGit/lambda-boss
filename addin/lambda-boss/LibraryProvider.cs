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
