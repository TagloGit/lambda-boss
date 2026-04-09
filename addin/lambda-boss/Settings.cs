using System.Text.Json;
using System.Text.Json.Serialization;

using Taglo.Excel.Common;

namespace LambdaBoss;

/// <summary>
///     Persists user settings to %APPDATA%\LambdaBoss\settings.json.
/// </summary>
public sealed class Settings
{
    private static readonly string SettingsDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LambdaBoss");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly RepoConfig DefaultRepo = new()
    {
        Url = "https://github.com/TagloGit/lambda-boss"
    };

    private static Settings? _current;

    /// <summary>
    ///     Configured repository sources.
    /// </summary>
    public List<RepoConfig> Repos { get; set; } = new() { DefaultRepo };

    /// <summary>
    ///     Excel keyboard shortcut string (ExcelDNA format). Default: Ctrl+Shift+L.
    /// </summary>
    public string KeyboardShortcut { get; set; } = "^+L";

    /// <summary>
    ///     How long cached library data remains valid, in minutes. 0 = no expiry.
    /// </summary>
    public int CacheTtlMinutes { get; set; } = 60;

    /// <summary>
    ///     Returns the current settings instance, loading from disk on first access.
    /// </summary>
    public static Settings Current => _current ??= Load();

    /// <summary>
    ///     Loads settings from disk. Returns defaults if the file doesn't exist or is invalid.
    /// </summary>
    public static Settings Load(string? path = null)
    {
        var filePath = path ?? SettingsPath;

        try
        {
            if (!File.Exists(filePath))
                return new Settings();

            var json = File.ReadAllText(filePath);
            var settings = JsonSerializer.Deserialize<Settings>(json, JsonOptions);

            if (settings == null)
                return new Settings();

            // Ensure there's always at least the default repo
            if (settings.Repos.Count == 0)
                settings.Repos.Add(DefaultRepo);

            return settings;
        }
        catch (Exception ex)
        {
            Logger.Error("Settings.Load", ex);
            return new Settings();
        }
    }

    /// <summary>
    ///     Saves the current settings to disk.
    /// </summary>
    public void Save(string? path = null)
    {
        var filePath = path ?? SettingsPath;

        try
        {
            var dir = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error("Settings.Save", ex);
        }
    }

    /// <summary>
    ///     Returns only the enabled repos.
    /// </summary>
    public IReadOnlyList<RepoConfig> EnabledRepos =>
        Repos.Where(r => r.Enabled).ToList();

    /// <summary>
    ///     Adds a repo by URL if not already present. Returns true if added.
    /// </summary>
    public bool AddRepo(string url)
    {
        url = url.TrimEnd('/');

        if (Repos.Any(r => string.Equals(r.Url.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase)))
            return false;

        Repos.Add(new RepoConfig { Url = url });
        return true;
    }

    /// <summary>
    ///     Removes a repo by URL. Returns true if removed.
    /// </summary>
    public bool RemoveRepo(string url)
    {
        url = url.TrimEnd('/');
        return Repos.RemoveAll(r =>
            string.Equals(r.Url.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>
    ///     Replaces the singleton with a fresh load from disk. Used after settings UI changes.
    /// </summary>
    public static void Reload(string? path = null)
    {
        _current = Load(path);
    }

    /// <summary>
    ///     Replaces the singleton with the given instance. Used for testing and after UI edits.
    /// </summary>
    public static void SetCurrent(Settings settings)
    {
        _current = settings;
    }
}
