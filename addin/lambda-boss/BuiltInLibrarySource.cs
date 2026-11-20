using System.Reflection;

using LambdaBoss.Common;

namespace LambdaBoss;

/// <summary>
///     The repo's own <c>lambdas/</c> folder, as it was when the add-in was built, carried inside
///     lambda-boss.dll (and so inside the packed XLL) as embedded resources. Used for the home repo
///     when GitHub can't be reached and nothing is cached, so a lone XLL on an offline machine
///     still has its library (a competition machine: spec 0013, "Offline and competition use").
/// </summary>
public sealed class BuiltInLibrarySource
{
    /// <summary>Resource names start with this; the csproj sets them as lambdas/&lt;lib&gt;/&lt;file&gt;.</summary>
    internal const string ResourcePrefix = "lambdas/";

    private readonly Assembly _assembly;
    private readonly Dictionary<string, Dictionary<string, string>> _resourcesByLibrary;

    public static BuiltInLibrarySource Default { get; } = new(typeof(BuiltInLibrarySource).Assembly);

    /// <summary>
    ///     When the library was copied into the add-in (the build time, from the csproj), or null when
    ///     the assembly carries no stamp. Offline, a cached library older than this loses to the
    ///     built-in copy (#452).
    /// </summary>
    public DateTime? BuiltAtUtc { get; }

    public BuiltInLibrarySource(Assembly assembly, DateTime? builtAtUtc = null)
    {
        _assembly = assembly;
        BuiltAtUtc = builtAtUtc ?? ReadBuiltAt(assembly);
        _resourcesByLibrary = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            // MSBuild's RecursiveDir gives backslashes on Windows.
            var path = resource.Replace('\\', '/');
            if (!path.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;

            var parts = path.Substring(ResourcePrefix.Length).Split('/');
            if (parts.Length != 2)
                continue;

            if (!_resourcesByLibrary.TryGetValue(parts[0], out var files))
                _resourcesByLibrary[parts[0]] = files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            files[parts[1]] = resource;
        }
    }

    /// <summary>Library folder names that have a <c>_library.yaml</c>, as on GitHub.</summary>
    public IReadOnlyList<string> ListLibraries() =>
        _resourcesByLibrary
            .Where(kv => kv.Value.ContainsKey("_library.yaml"))
            .Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    public bool Has(string libraryName) =>
        _resourcesByLibrary.TryGetValue(libraryName, out var files) && files.ContainsKey("_library.yaml");

    /// <summary>
    ///     The library's metadata and its .lambda and .const files, or null when it isn't built in.
    /// </summary>
    public FetchedLibrary? FetchLibrary(string libraryName)
    {
        if (!_resourcesByLibrary.TryGetValue(libraryName, out var resources)
            || !resources.TryGetValue("_library.yaml", out var yamlResource))
            return null;

        var metadata = LibraryMetadata.LoadFromString(Read(yamlResource));

        var files = new Dictionary<string, string>();
        foreach (var (fileName, resource) in resources)
        {
            if (fileName.EndsWith(".lambda", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".const", StringComparison.OrdinalIgnoreCase))
                files[fileName] = Read(resource);
        }

        Logger.Info($"BuiltInLibrarySource: Read library '{libraryName}' ({files.Count} files) from the add-in");
        return new FetchedLibrary(libraryName, metadata, files);
    }

    private static DateTime? ReadBuiltAt(Assembly assembly)
    {
        var stamp = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LibraryBuiltUtc")?.Value;
        return DateTime.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var when)
            ? when
            : null;
    }

    private string Read(string resource)
    {
        using var stream = _assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing built-in resource '{resource}'");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
