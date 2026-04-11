using ExcelDna.Integration;

using Taglo.Excel.Common;

namespace LambdaBoss;

/// <summary>
///     Injects LAMBDA definitions into the active workbook's Name Manager via COM interop.
/// </summary>
public static class LambdaLoader
{
    /// <summary>
    ///     Marker prefix for Name Manager comments stamped by Lambda Boss.
    /// </summary>
    internal const string CommentMarker = "[LambdaBoss]";

    /// <summary>
    ///     Adds or updates a named LAMBDA formula in the active workbook's Name Manager.
    ///     Optionally stamps a comment with library provenance metadata.
    /// </summary>
    /// <param name="name">The name to register (e.g. "tst.Double").</param>
    /// <param name="formula">The LAMBDA formula including the = prefix (e.g. "=LAMBDA(x, x*2)").</param>
    /// <param name="comment">Optional comment to stamp on the name (for provenance tracking).</param>
    public static void InjectLambda(string name, string formula, string? comment = null)
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            dynamic workbook = app.ActiveWorkbook;

            if (workbook == null)
            {
                Logger.Info("InjectLambda: No active workbook");
                return;
            }

            // Try to update existing name first, fall back to adding new
            try
            {
                dynamic existing = workbook.Names.Item(name);
                existing.RefersTo = formula;
                if (comment != null)
                    existing.Comment = comment;
                Logger.Info($"InjectLambda: Updated '{name}'");
            }
            catch
            {
                // Name doesn't exist — add it
                workbook.Names.Add(name, formula);
                if (comment != null)
                {
                    try
                    {
                        dynamic added = workbook.Names.Item(name);
                        added.Comment = comment;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"InjectLambda: Failed to set comment on '{name}'", ex);
                    }
                }
                Logger.Info($"InjectLambda: Added '{name}'");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("InjectLambda", ex);
            throw;
        }
    }

    /// <summary>
    ///     Builds the comment string stamped on each Name Manager entry.
    /// </summary>
    public static string BuildComment(string repoUrl, string libraryName, string prefix)
    {
        return $"{CommentMarker} {repoUrl.TrimEnd('/')}|{libraryName}|{prefix}";
    }

    /// <summary>
    ///     Scans the active workbook's Name Manager for entries stamped by Lambda Boss.
    ///     Returns a list of loaded library records grouped by repo+library.
    /// </summary>
    public static IReadOnlyList<ScannedLibrary> ScanLoadedLibraries()
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            dynamic workbook = app.ActiveWorkbook;

            if (workbook == null)
                return Array.Empty<ScannedLibrary>();

            var groups = new Dictionary<string, ScannedLibrary>(StringComparer.OrdinalIgnoreCase);

            foreach (dynamic name in workbook.Names)
            {
                try
                {
                    string comment = name.Comment ?? "";
                    if (!comment.StartsWith(CommentMarker))
                        continue;

                    var metadata = comment[CommentMarker.Length..].Trim();
                    var parts = metadata.Split('|');
                    if (parts.Length < 3)
                        continue;

                    var repoUrl = parts[0].Trim();
                    var libraryName = parts[1].Trim();
                    var prefix = parts[2].Trim();

                    string nameText = name.Name;
                    string refersTo = name.RefersTo;

                    var key = $"{repoUrl}|{libraryName}".ToLowerInvariant();
                    if (!groups.TryGetValue(key, out var scanned))
                    {
                        scanned = new ScannedLibrary
                        {
                            RepoUrl = repoUrl,
                            LibraryName = libraryName,
                            Prefix = prefix
                        };
                        groups[key] = scanned;
                    }

                    scanned.Lambdas[nameText] = refersTo;
                }
                catch
                {
                    // Skip names that can't be read
                }
            }

            Logger.Info($"ScanLoadedLibraries: Found {groups.Count} loaded libraries");
            return groups.Values.ToList();
        }
        catch (Exception ex)
        {
            Logger.Error("ScanLoadedLibraries", ex);
            return Array.Empty<ScannedLibrary>();
        }
    }

    /// <summary>
    ///     Loads all .lambda files from a library folder, applies the library's prefix,
    ///     and returns the prefixed name/formula pairs ready for injection.
    /// </summary>
    /// <param name="libraryPath">Path to the library folder containing _library.yaml and .lambda files.</param>
    /// <returns>List of (prefixed name, rewritten formula) tuples.</returns>
    public static IReadOnlyList<(string Name, string Formula)> LoadLibrary(string libraryPath)
    {
        var metadataPath = Path.Combine(libraryPath, "_library.yaml");
        if (!File.Exists(metadataPath))
            throw new FileNotFoundException($"Library metadata not found: {metadataPath}");

        var metadata = LibraryMetadata.LoadFromFile(metadataPath);
        var prefix = metadata.DefaultPrefix;

        var lambdaFiles = Directory.GetFiles(libraryPath, "*.lambda");
        var results = new List<(string Name, string Formula)>();
        var allNames = new List<string>();

        // First pass: parse all files to collect names
        var parsed = new List<(string Name, string Formula)>();
        foreach (var file in lambdaFiles)
        {
            var (name, formula) = LambdaParser.ParseFile(file);
            parsed.Add((name, formula));
            allNames.Add(name);
        }

        // Second pass: apply prefix rewriting to all formulas
        foreach (var (name, formula) in parsed)
        {
            var rewrittenFormula = PrefixRewriter.Apply(formula, prefix, allNames);
            var prefixedName = string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
            results.Add((prefixedName, rewrittenFormula));
        }

        Logger.Info($"LoadLibrary: Loaded {results.Count} lambdas from '{metadata.Name}' with prefix '{prefix}'");

        return results;
    }

    /// <summary>
    ///     Loads a library from disk and injects all lambdas into the active workbook.
    /// </summary>
    public static void InjectLibrary(string libraryPath)
    {
        var lambdas = LoadLibrary(libraryPath);
        foreach (var (name, formula) in lambdas)
        {
            InjectLambda(name, formula);
        }
    }

    /// <summary>
    ///     Returns the hardcoded tracer-bullet LAMBDAs for the popup.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Formula)> GetTracerBulletLambdas()
    {
        return new[]
        {
            ("DOUBLE", "=LAMBDA(x, x*2)"),
            ("TRIPLE", "=LAMBDA(x, x*3)"),
            ("ADDN", "=LAMBDA(x, n, x+n)")
        };
    }
}

/// <summary>
///     A library detected in the workbook's Name Manager via comment scanning.
/// </summary>
public sealed class ScannedLibrary
{
    public string RepoUrl { get; init; } = "";
    public string LibraryName { get; init; } = "";
    public string Prefix { get; init; } = "";
    public Dictionary<string, string> Lambdas { get; } = new(StringComparer.OrdinalIgnoreCase);
}
