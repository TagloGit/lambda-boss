using System.Text.Json.Serialization;

namespace LambdaBoss;

/// <summary>
///     Configuration for a GitHub repository source of LAMBDA libraries.
/// </summary>
public sealed class RepoConfig
{
    public const string HomeRepoUrl = "https://github.com/TagloGit/lambda-boss";
    public const string StableBranch = "main";
    public const string LabsBranch = "develop";

    /// <summary>
    ///     The GitHub repository URL (e.g. "https://github.com/TagloGit/lambda-boss").
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    ///     Whether this repo is enabled for fetching. Disabled repos are excluded from search and browsing.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     UTC timestamp of the last successful fetch from this repo. Null if never fetched.
    /// </summary>
    public DateTime? LastFetched { get; set; }

    /// <summary>
    ///     The branch this repo is read from. Not saved: <see cref="Settings"/> resolves it from the
    ///     library channel.
    /// </summary>
    [JsonIgnore]
    public string Branch { get; set; } = StableBranch;

    /// <summary>
    ///     True for Lambda Boss's own repo, the only one with a Labs branch. Other repos stay on main.
    /// </summary>
    [JsonIgnore]
    public bool IsHomeRepo
    {
        get
        {
            var url = Url.TrimEnd('/');
            if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                url = url.Substring(0, url.Length - 4);

            return string.Equals(url, HomeRepoUrl, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    ///     Extracts the owner and repo name from the URL.
    ///     For "https://github.com/TagloGit/lambda-boss", returns ("TagloGit", "lambda-boss").
    /// </summary>
    public (string Owner, string Repo) ParseOwnerRepo()
    {
        // Handle URLs with or without trailing slash, with or without .git
        var uri = new Uri(Url.TrimEnd('/'));
        var segments = uri.AbsolutePath.Trim('/').Replace(".git", "").Split('/');

        if (segments.Length < 2)
            throw new FormatException($"Cannot parse owner/repo from URL: {Url}");

        return (segments[0], segments[1]);
    }

    /// <summary>
    ///     Returns a stable hash string for this repo, used for cache directory naming.
    ///     Each non-main branch gets its own key, so channels never read each other's cache.
    /// </summary>
    public string GetCacheKey()
    {
        var (owner, repo) = ParseOwnerRepo();
        var key = $"{owner}_{repo}".ToLowerInvariant();
        return Branch == StableBranch ? key : $"{key}__{Branch}";
    }
}
