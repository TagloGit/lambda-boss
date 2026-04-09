using ExcelDna.Integration;

using Taglo.Excel.Common;

namespace LambdaBoss;

/// <summary>
///     Injects LAMBDA definitions into the active workbook's Name Manager via COM interop.
/// </summary>
public static class LambdaLoader
{
    /// <summary>
    ///     Adds or updates a named LAMBDA formula in the active workbook's Name Manager.
    /// </summary>
    /// <param name="name">The name to register (e.g. "tst.Double").</param>
    /// <param name="formula">The LAMBDA formula including the = prefix (e.g. "=LAMBDA(x, x*2)").</param>
    public static void InjectLambda(string name, string formula)
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
                Logger.Info($"InjectLambda: Updated '{name}'");
            }
            catch
            {
                // Name doesn't exist — add it
                workbook.Names.Add(name, formula);
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
