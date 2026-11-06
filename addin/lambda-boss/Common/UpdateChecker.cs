using System.Net.Http;
using System.Text.Json;

namespace LambdaBoss.Common;

/// <summary>
///     Checks for newer releases on GitHub and exposes result for notification.
///     All failures are silent — network errors are logged but never shown to the user.
/// </summary>
internal static class UpdateChecker
{
    private static Uri? _latestReleaseUri;
    private static string? _userAgent;

    public static string? NewVersionAvailable { get; private set; }

    public static string? ReleaseUrl { get; private set; }

    public static event Action? UpdateAvailable;

    public static void Initialize(string repoUrl, string userAgent)
    {
        _latestReleaseUri = new Uri(repoUrl);
        _userAgent = userAgent;
        NewVersionAvailable = null;
        ReleaseUrl = null;
    }

    public static async void CheckForUpdateAsync(Version currentVersion)
    {
        if (_latestReleaseUri == null || _userAgent == null)
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);

            var json = await client.GetStringAsync(_latestReleaseUri);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString();
            var htmlUrl = root.GetProperty("html_url").GetString();

            if (tagName == null || htmlUrl == null)
            {
                return;
            }

            var remoteVersion = ParseVersion(tagName);
            if (remoteVersion == null)
            {
                return;
            }

            if (remoteVersion > currentVersion)
            {
                NewVersionAvailable = remoteVersion.ToString();
                ReleaseUrl = htmlUrl;
                Logger.Info($"Update available: v{NewVersionAvailable} (current: v{currentVersion})");
                UpdateAvailable?.Invoke();
            }
            else
            {
                Logger.Info($"No update available (current: v{currentVersion}, latest: v{remoteVersion})");
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"Update check failed (silent): {ex.Message}");
        }
    }

    /// <summary>
    ///     On the Labs channel: reports an update when the rolling dev-latest pre-release was built
    ///     from a different commit than this add-in. Dev builds share a base version, so only the
    ///     commit tells them apart.
    /// </summary>
    public static async void CheckForDevBuildAsync(string devReleaseUrl, string? currentCommit)
    {
        if (_userAgent == null)
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);

            var json = await client.GetStringAsync(new Uri(devReleaseUrl));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var notes = root.GetProperty("body").GetString();
            var htmlUrl = root.GetProperty("html_url").GetString();
            var remoteVersion = ParseNotesField(notes, "version");
            var remoteCommit = ParseNotesField(notes, "commit");

            if (remoteCommit == null || htmlUrl == null)
            {
                return;
            }

            if (IsDifferentBuild(currentCommit, remoteCommit))
            {
                NewVersionAvailable = remoteVersion ?? remoteCommit;
                ReleaseUrl = htmlUrl;
                Logger.Info($"Dev build available: {NewVersionAvailable} (current commit: {currentCommit})");
                UpdateAvailable?.Invoke();
            }
            else
            {
                Logger.Info($"Dev build is current ({remoteVersion})");
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"Dev build check failed (silent): {ex.Message}");
        }
    }

    /// <summary>
    ///     Reads a "name: value" line from dev-latest's release notes.
    /// </summary>
    public static string? ParseNotesField(string? notes, string name)
    {
        if (notes == null)
        {
            return null;
        }

        var prefix = name + ":";
        foreach (var line in notes.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed.Substring(prefix.Length).Trim();
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    public static bool IsDifferentBuild(string? currentCommit, string remoteCommit) =>
        !string.Equals(currentCommit, remoteCommit, StringComparison.OrdinalIgnoreCase);

    public static Version? ParseVersion(string tag)
    {
        var cleaned = tag.TrimStart('v', 'V');
        return Version.TryParse(cleaned, out var version) ? version : null;
    }

    internal static void Reset()
    {
        _latestReleaseUri = null;
        _userAgent = null;
        NewVersionAvailable = null;
        ReleaseUrl = null;
    }
}
