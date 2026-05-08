using ExcelDna.Integration;
using System.Windows;

using Taglo.Excel.Common;

namespace LambdaBoss.Commands;

/// <summary>
///     POC: imports every lambda from every enabled library across all configured
///     repos and local sources, using each library's default prefix.
/// </summary>
internal static class ImportAllLambdasCommand
{
    public static void Run()
    {
        Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        IReadOnlyList<LibraryInfo> libraries;
        var libraryLambdas = new List<(LibraryInfo Info, IReadOnlyList<(string Name, string Formula)> Lambdas)>();
        var fetchFailures = new List<(string Label, string Error)>();

        try
        {
            var provider = new LibraryProvider(
                Settings.Current.EnabledRepos,
                localSources: Settings.Current.EnabledLocalSources);

            libraries = await provider.GetLibrariesAsync();

            foreach (var info in libraries)
            {
                try
                {
                    IReadOnlyList<(string Name, string Formula)> lambdas;
                    if (info.IsLocal)
                    {
                        lambdas = provider.LoadLocalLibrary(
                            info.LocalSourceConfig!, info.FolderName, info.DefaultPrefix);
                    }
                    else
                    {
                        lambdas = await provider.LoadLibraryAsync(
                            info.RepoConfig, info.FolderName, info.DefaultPrefix);
                    }
                    libraryLambdas.Add((info, lambdas));
                }
                catch (Exception ex)
                {
                    Logger.Error($"ImportAllLambdas: Failed to load library '{info.FolderName}'", ex);
                    fetchFailures.Add(($"{info.RepoLabel}/{info.FolderName}", ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("ImportAllLambdas/Fetch", ex);
            ExcelAsyncUtil.QueueAsMacro(() => ShowError($"Failed to fetch libraries: {ex.Message}"));
            return;
        }

        ExcelAsyncUtil.QueueAsMacro(() =>
        {
            try
            {
                InjectAll(libraryLambdas, fetchFailures);
            }
            catch (Exception ex)
            {
                Logger.Error("ImportAllLambdas/Inject", ex);
                ShowError($"Failed during injection: {ex.Message}");
            }
        });
    }

    private static void InjectAll(
        IReadOnlyList<(LibraryInfo Info, IReadOnlyList<(string Name, string Formula)> Lambdas)> libraryLambdas,
        IReadOnlyList<(string Label, string Error)> fetchFailures)
    {
        if (libraryLambdas.Count == 0 && fetchFailures.Count == 0)
        {
            MessageBox.Show(
                "No libraries are configured. Add a repo or local source via Settings first.",
                "Import All Lambdas",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Snapshot existing names once so we can classify add vs update vs unchanged.
        IReadOnlyList<ScannedLibrary> alreadyLoaded;
        try
        {
            alreadyLoaded = LambdaLoader.ScanLoadedLibraries();
        }
        catch (Exception ex)
        {
            Logger.Error("ImportAllLambdas: ScanLoadedLibraries failed", ex);
            alreadyLoaded = Array.Empty<ScannedLibrary>();
        }

        var totalAdded = 0;
        var totalUpdated = 0;
        var totalUnchanged = 0;
        var librariesImported = 0;
        var injectFailures = new List<(string Label, string Error)>();

        foreach (var (info, lambdas) in libraryLambdas)
        {
            var sourceLabel = info.IsLocal
                ? info.LocalSourceConfig!.Path
                : info.RepoConfig.Url;

            var comment = LambdaLoader.BuildComment(sourceLabel, info.FolderName, info.DefaultPrefix);

            var existing = alreadyLoaded.FirstOrDefault(s =>
                string.Equals(s.LibraryName, info.FolderName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    s.RepoUrl.TrimEnd('/', '\\'),
                    sourceLabel.TrimEnd('/', '\\'),
                    StringComparison.OrdinalIgnoreCase));

            try
            {
                foreach (var (name, formula) in lambdas)
                {
                    if (existing != null && existing.Lambdas.TryGetValue(name, out var oldFormula))
                    {
                        if (string.Equals(formula, oldFormula, StringComparison.Ordinal))
                            totalUnchanged++;
                        else
                            totalUpdated++;
                    }
                    else
                    {
                        totalAdded++;
                    }

                    LambdaLoader.InjectLambda(name, formula, comment);
                }
                librariesImported++;
            }
            catch (Exception ex)
            {
                Logger.Error($"ImportAllLambdas: Inject failed for '{info.FolderName}'", ex);
                injectFailures.Add(($"{info.RepoLabel}/{info.FolderName}", ex.Message));
            }
        }

        Logger.Info($"ImportAllLambdas: imported {librariesImported} libraries (added={totalAdded}, updated={totalUpdated}, unchanged={totalUnchanged})");

        var summary = $"Imported {librariesImported} libraries.\n\n"
                    + $"  Added:     {totalAdded}\n"
                    + $"  Updated:   {totalUpdated}\n"
                    + $"  Unchanged: {totalUnchanged}";

        var failures = fetchFailures.Concat(injectFailures).ToList();
        if (failures.Count > 0)
        {
            summary += $"\n\n{failures.Count} libraries failed:";
            foreach (var (label, error) in failures.Take(10))
                summary += $"\n  • {label}: {error}";
            if (failures.Count > 10)
                summary += $"\n  … and {failures.Count - 10} more (see log)";
        }

        var icon = failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
        MessageBox.Show(summary, "Import All Lambdas", MessageBoxButton.OK, icon);
    }

    private static void ShowError(string message)
    {
        try
        {
            MessageBox.Show(message, "Import All Lambdas", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            Logger.Info($"ImportAllLambdas/ShowError: {message}");
        }
    }
}
