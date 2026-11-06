using System.Text.Json;
using System.Text.Json.Serialization;

using LambdaBoss.Common;

namespace LambdaBoss;

/// <summary>
///     Which branch of Lambda Boss's own library the add-in reads.
/// </summary>
public enum LibraryChannel
{
    Stable,
    Labs
}

/// <summary>
///     Saves the channel by name and reads anything unknown as Stable, so one bad value can't
///     reset the rest of the user's settings.
/// </summary>
internal sealed class LibraryChannelConverter : JsonConverter<LibraryChannel>
{
    public override LibraryChannel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
        && string.Equals(reader.GetString(), nameof(LibraryChannel.Labs), StringComparison.OrdinalIgnoreCase)
            ? LibraryChannel.Labs
            : LibraryChannel.Stable;

    public override void Write(Utf8JsonWriter writer, LibraryChannel value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new LibraryChannelConverter() }
    };

    private static Settings? _current;

    /// <summary>
    ///     Configured repository sources.
    /// </summary>
    public List<RepoConfig> Repos { get; set; } = new() { new RepoConfig { Url = RepoConfig.HomeRepoUrl } };

    /// <summary>
    ///     Configured local directory sources.
    /// </summary>
    public List<LocalSourceConfig> LocalSources { get; set; } = new();

    /// <summary>
    ///     Excel keyboard shortcut string (ExcelDNA format). Default: Ctrl+Shift+L.
    /// </summary>
    public string KeyboardShortcut { get; set; } = "^+L";

    /// <summary>
    ///     How long cached library data remains valid, in minutes. 0 = no expiry.
    /// </summary>
    public int CacheTtlMinutes { get; set; } = 60;

    /// <summary>
    ///     <see cref="CacheTtlMinutes"/> as a TimeSpan, or null when expiry is disabled (0 or negative).
    /// </summary>
    [JsonIgnore]
    public TimeSpan? CacheTtl => CacheTtlMinutes > 0 ? TimeSpan.FromMinutes(CacheTtlMinutes) : null;

    /// <summary>
    ///     Which branch of Lambda Boss's own library to read: Stable (main) or Labs (develop).
    /// </summary>
    public LibraryChannel LibraryChannel { get; set; } = LibraryChannel.Stable;

    /// <summary>
    ///     Labs switches that are on, by switch id. See <see cref="Labs"/>.
    /// </summary>
    public HashSet<string> LabsSwitchesOn { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
                settings.Repos.Add(new RepoConfig { Url = RepoConfig.HomeRepoUrl });

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
    ///     Returns only the enabled repos, each pointed at the branch the library channel selects.
    ///     Lambda Boss's own repo follows the channel; other repos stay on main.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<RepoConfig> EnabledRepos =>
        Repos.Where(r => r.Enabled)
            .Select(r => r.OnBranch(r.IsHomeRepo && LibraryChannel == LibraryChannel.Labs
                ? RepoConfig.LabsBranch
                : RepoConfig.StableBranch))
            .ToList();

    /// <summary>
    ///     Returns only the enabled local sources.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<LocalSourceConfig> EnabledLocalSources =>
        LocalSources.Where(s => s.Enabled).ToList();

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
    ///     Adds a local directory source if not already present. Returns true if added.
    /// </summary>
    public bool AddLocalSource(string path)
    {
        path = path.TrimEnd('\\', '/');

        if (LocalSources.Any(s => string.Equals(
            s.Path.TrimEnd('\\', '/'), path, StringComparison.OrdinalIgnoreCase)))
            return false;

        LocalSources.Add(new LocalSourceConfig { Path = path });
        return true;
    }

    /// <summary>
    ///     Removes a local directory source by path. Returns true if removed.
    /// </summary>
    public bool RemoveLocalSource(string path)
    {
        path = path.TrimEnd('\\', '/');
        return LocalSources.RemoveAll(s =>
            string.Equals(s.Path.TrimEnd('\\', '/'), path, StringComparison.OrdinalIgnoreCase)) > 0;
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
