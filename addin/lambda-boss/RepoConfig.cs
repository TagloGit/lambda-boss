namespace LambdaBoss;

/// <summary>
///     Configuration for a GitHub repository source of LAMBDA libraries.
/// </summary>
public sealed class RepoConfig
{
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
    /// </summary>
    public string GetCacheKey()
    {
        var (owner, repo) = ParseOwnerRepo();
        return $"{owner}_{repo}".ToLowerInvariant();
    }
}
