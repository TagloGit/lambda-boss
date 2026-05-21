using System.Net.Http;
using System.Text.Json;

namespace LambdaBoss.Common;

/// <summary>
///     Checks for newer releases on GitHub and exposes result for notification.
///     All failures are silent — network errors are logged but never shown to the user.
/// </summary>
public static class UpdateChecker
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
