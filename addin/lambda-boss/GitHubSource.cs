using System.Net.Http;
using System.Text.Json;

using Taglo.Excel.Common;

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
        // GET /repos/{owner}/{repo}/contents/lambdas → JSON array of entries
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/contents/lambdas";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "LambdaBoss");
        request.Headers.Add("Accept", "application/vnd.github.v3+json");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<JsonElement[]>(json)!;

        var libraries = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.GetProperty("type").GetString() == "dir")
            {
                libraries.Add(entry.GetProperty("name").GetString()!);
            }
        }

        Logger.Info($"GitHubSource: Found {libraries.Count} libraries in {_owner}/{_repo}");
        return libraries;
    }

    /// <summary>
    ///     Fetches a raw file from the repository's default branch.
    /// </summary>
    public async Task<string> FetchFileAsync(string path)
    {
        var url = $"https://raw.githubusercontent.com/{_owner}/{_repo}/main/{path}";
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
    ///     Lists all .lambda filenames in a library folder via the GitHub API.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListLambdaFilesAsync(string libraryName)
    {
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/contents/lambdas/{libraryName}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "LambdaBoss");
        request.Headers.Add("Accept", "application/vnd.github.v3+json");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<JsonElement[]>(json)!;

        var files = new List<string>();
        foreach (var entry in entries)
        {
            var name = entry.GetProperty("name").GetString()!;
            if (name.EndsWith(".lambda", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(name);
            }
        }

        return files;
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

        Logger.Info($"GitHubSource: Fetched library '{libraryName}' ({files.Count} files) from {_owner}/{_repo}");
        return new FetchedLibrary(libraryName, metadata, files);
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
    ///     Parses all .lambda files and returns prefixed name/formula pairs,
    ///     applying the library's default prefix.
    /// </summary>
    public IReadOnlyList<(string Name, string Formula)> LoadWithPrefix(string? prefixOverride = null)
    {
        var prefix = prefixOverride ?? Metadata.DefaultPrefix;
        var parsed = new List<(string Name, string Formula)>();
        var allNames = new List<string>();

        // First pass: parse all files to collect names
        foreach (var (_, content) in Files)
        {
            var (name, formula) = LambdaParser.Parse(content);
            parsed.Add((name, formula));
            allNames.Add(name);
        }

        // Second pass: apply prefix rewriting
        var results = new List<(string Name, string Formula)>();
        foreach (var (name, formula) in parsed)
        {
            var rewrittenFormula = PrefixRewriter.Apply(formula, prefix, allNames);
            var prefixedName = string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
            results.Add((prefixedName, rewrittenFormula));
        }

        return results;
    }
}
