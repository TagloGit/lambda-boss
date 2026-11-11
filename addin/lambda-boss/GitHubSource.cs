using System.Net.Http;
using System.Text.Json;

using LambdaBoss.Common;

namespace LambdaBoss;

/// <summary>
///     Fetches LAMBDA library contents from a GitHub repository.
///     Uses the GitHub REST API for directory discovery and raw.githubusercontent.com for file content.
/// </summary>
public class GitHubSource
{
    private readonly HttpClient _httpClient;
    private readonly RepoConfig _config;
    private readonly string _owner;
    private readonly string _repo;

    public GitHubSource(RepoConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        (_owner, _repo) = config.ParseOwnerRepo();
    }

    /// <summary>
    ///     Discovers all library folder names under the lambdas/ directory.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListLibrariesAsync()
    {
        var tree = await GetTreeAsync();
        if (tree != null)
        {
            var fromTree = new List<string>();
            foreach (var (path, isDir) in tree)
            {
                if (isDir && path.StartsWith("lambdas/", StringComparison.Ordinal)
                          && path.IndexOf('/', 8) < 0)
                {
                    fromTree.Add(path.Substring(8));
                }
            }

            Logger.Info($"GitHubSource: Found {fromTree.Count} libraries in {_owner}/{_repo}@{_config.Branch} (tree)");
            return fromTree;
        }

        // GET /repos/{owner}/{repo}/contents/lambdas → JSON array of entries
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/contents/lambdas?ref={_config.Branch}";
        var json = await GetApiJsonAsync(url);
        var entries = JsonSerializer.Deserialize<JsonElement[]>(json)!;

        var libraries = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.GetProperty("type").GetString() == "dir")
            {
                libraries.Add(entry.GetProperty("name").GetString()!);
            }
        }

        Logger.Info($"GitHubSource: Found {libraries.Count} libraries in {_owner}/{_repo}@{_config.Branch}");
        return libraries;
    }

    private readonly object _treeLock = new();
    private Task<List<(string Path, bool IsDir)>?>? _treeTask;

    /// <summary>
    ///     One recursive git-tree call per instance answers every listing. Returns null when the tree
    ///     is unusable (404, truncated, bad JSON) so callers fall back to the contents API; a rate
    ///     limit throws <see cref="GitHubRateLimitException"/>.
    /// </summary>
    private Task<List<(string Path, bool IsDir)>?> GetTreeAsync()
    {
        lock (_treeLock)
        {
            return _treeTask ??= LoadTreeAsync();
        }
    }

    private async Task<List<(string Path, bool IsDir)>?> LoadTreeAsync()
    {
        // A request that never answers (offline, timeout) throws from here: the contents API would
        // only wait out the same timeout again.
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/git/trees/{_config.Branch}?recursive=1";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "LambdaBoss");
        request.Headers.Add("Accept", "application/vnd.github.v3+json");
        var response = await _httpClient.SendAsync(request);
        ThrowIfRateLimited(response);
        if (!response.IsSuccessStatusCode)
        {
            Logger.Info($"GitHubSource: git tree answered {(int)response.StatusCode}; using contents API");
            return null;
        }

        try
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            {
                Logger.Info("GitHubSource: git tree truncated; using contents API");
                return null;
            }

            var result = new List<(string Path, bool IsDir)>();
            foreach (var item in root.GetProperty("tree").EnumerateArray())
            {
                var path = item.GetProperty("path").GetString()!;
                var type = item.GetProperty("type").GetString();
                if (type == "tree") result.Add((path, true));
                else if (type == "blob") result.Add((path, false));
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Logger.Info($"GitHubSource: git tree unreadable ({ex.Message}); using contents API");
            return null;
        }
    }

    /// <summary>
    ///     GETs an api.github.com URL and returns the body, throwing <see cref="GitHubRateLimitException"/>
    ///     on a rate limit and HttpRequestException on any other failure.
    /// </summary>
    private async Task<string> GetApiJsonAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "LambdaBoss");
        request.Headers.Add("Accept", "application/vnd.github.v3+json");

        var response = await _httpClient.SendAsync(request);
        ThrowIfRateLimited(response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static void ThrowIfRateLimited(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code != 403 && code != 429) return;

        var remaining = FirstHeader(response, "X-RateLimit-Remaining");
        if (code != 429 && remaining?.Trim() != "0") return;

        DateTime? reset = null;
        if (long.TryParse(FirstHeader(response, "X-RateLimit-Reset"), out var seconds))
        {
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }

        throw new GitHubRateLimitException(reset);
    }

    private static string? FirstHeader(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }

    /// <summary>
    ///     Fetches a raw file from the branch the repo config points at.
    /// </summary>
    public async Task<string> FetchFileAsync(string path)
    {
        var url = $"https://raw.githubusercontent.com/{_owner}/{_repo}/{_config.Branch}/{path}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "LambdaBoss");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    ///     Fetches the library metadata (_library.yaml) for a given library.
    /// </summary>
    public async Task<LibraryMetadata> FetchLibraryMetadataAsync(string libraryName)
    {
        var yaml = await FetchFileAsync($"lambdas/{libraryName}/_library.yaml");
        return LibraryMetadata.LoadFromString(yaml);
    }

    /// <summary>
    ///     Lists all .lambda and .const filenames in a library folder via the GitHub API.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListLambdaFilesAsync(string libraryName)
    {
        var tree = await GetTreeAsync();
        if (tree != null)
        {
            var prefix = $"lambdas/{libraryName}/";
            var fromTree = new List<string>();
            foreach (var (path, isDir) in tree)
            {
                if (isDir || !path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var name = path.Substring(prefix.Length);
                if (name.IndexOf('/') >= 0) continue;
                if (IsLambdaFile(name)) fromTree.Add(name);
            }

            return fromTree;
        }

        var url = $"https://api.github.com/repos/{_owner}/{_repo}/contents/lambdas/{libraryName}?ref={_config.Branch}";
        var json = await GetApiJsonAsync(url);
        var entries = JsonSerializer.Deserialize<JsonElement[]>(json)!;

        var files = new List<string>();
        foreach (var entry in entries)
        {
            var name = entry.GetProperty("name").GetString()!;
            if (IsLambdaFile(name))
            {
                files.Add(name);
            }
        }

        return files;
    }

    private static bool IsLambdaFile(string name)
    {
        return name.EndsWith(".lambda", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".const", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Fetches a complete library: metadata + all .lambda file contents.
    ///     Returns the metadata and a dictionary of filename → content.
    /// </summary>
    public async Task<FetchedLibrary> FetchLibraryAsync(string libraryName)
    {
        var metadata = await FetchLibraryMetadataAsync(libraryName);
        var fileNames = await ListLambdaFilesAsync(libraryName);

        var files = new Dictionary<string, string>();
        foreach (var fileName in fileNames)
        {
            var content = await FetchFileAsync($"lambdas/{libraryName}/{fileName}");
            files[fileName] = content;
        }

        _config.LastFetched = DateTime.UtcNow;

        Logger.Info($"GitHubSource: Fetched library '{libraryName}' ({files.Count} files) from {_owner}/{_repo}@{_config.Branch}");
        return new FetchedLibrary(libraryName, metadata, files);
    }
}

/// <summary>
///     GitHub's unauthenticated API limit is used up. <see cref="ResetUtc"/> is when it lifts, if known.
/// </summary>
public class GitHubRateLimitException : HttpRequestException
{
    public DateTime? ResetUtc { get; }

    public GitHubRateLimitException(DateTime? resetUtc)
        : base(resetUtc.HasValue
            ? $"GitHub's hourly limit for this network is used up until {resetUtc.Value:HH:mm} UTC"
            : "GitHub's hourly limit for this network is used up")
    {
        ResetUtc = resetUtc;
    }
}

/// <summary>
///     Represents a fully fetched library from GitHub: metadata + all .lambda file contents.
/// </summary>
public sealed class FetchedLibrary
{
    public string Name { get; }
    public LibraryMetadata Metadata { get; }
    public IReadOnlyDictionary<string, string> Files { get; }

    public FetchedLibrary(string name, LibraryMetadata metadata, IReadOnlyDictionary<string, string> files)
    {
        Name = name;
        Metadata = metadata;
        Files = files;
    }

    /// <summary>
    ///     Parses all .lambda and .const files and returns prefixed name/formula pairs,
    ///     applying the library's default prefix. Constant names are tracked separately
    ///     so references to them (which carry no parens) are prefixed correctly.
    /// </summary>
    public IReadOnlyList<(string Name, string Formula)> LoadWithPrefix(string? prefixOverride = null)
    {
        var prefix = prefixOverride ?? Metadata.DefaultPrefix;
        var parsed = new List<(string Name, string Formula)>();
        var lambdaNames = new List<string>();
        var constNames = new List<string>();

        // First pass: parse all files to collect names, branching on extension.
        foreach (var (fileName, content) in Files)
        {
            if (fileName.EndsWith(".const", StringComparison.OrdinalIgnoreCase))
            {
                var (name, formula) = ConstParser.Parse(content);
                parsed.Add((name, formula));
                constNames.Add(name);
            }
            else
            {
                var (name, formula) = LambdaParser.Parse(content);
                parsed.Add((name, formula));
                lambdaNames.Add(name);
            }
        }

        // Second pass: apply prefix rewriting
        var results = new List<(string Name, string Formula)>();
        foreach (var (name, formula) in parsed)
        {
            var rewrittenFormula = PrefixRewriter.Apply(formula, prefix, lambdaNames, constNames);
            var prefixedName = string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
            results.Add((prefixedName, rewrittenFormula));
        }

        return results;
    }
}
