using System.Windows;
using System.Windows.Interop;

using ExcelDna.Integration;

using LambdaBoss.Common;
using LambdaBoss.UI;

namespace LambdaBoss.Commands;

/// <summary>
///     Slash-command handler for <c>/State</c> (spec 0015, lambda-boss#429): always on the active
///     cell. An empty cell gets a new state engine; a cell holding one in any form (a fold with an
///     inline or named step, or the unfolded LET step) opens with its variables, form and name, and
///     Save rewrites the cell and stores or updates the named LAMBDA.
/// </summary>
internal static class StateStepCommand
{
    public static void Run()
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            dynamic workbook = app.ActiveWorkbook;
            if (workbook == null)
                return;

            dynamic cell = app.ActiveCell;
            string formula = Convert.ToString(cell.Formula2) ?? "";
            if (formula.Length == 0 && cell.Value2 != null)
            {
                ShowError("The active cell holds a value or is part of a spill. Pick an empty cell, or the cell that holds the state engine.");
                return;
            }

            Dictionary<string, string> names = ConvertLetToLambdaCommand.GetExistingNames(workbook);
            Func<string, string?> refersTo = n => names.TryGetValue(n, out var r) ? r : null;
            var folds = LambdaHints.Catalog.StateFolds();

            var read = StateEngines.Read(formula, refersTo, folds);
            if (read.Refusal != null)
            {
                ShowError(folds.Count == 0
                    ? read.Refusal + " (Library folds are known once the popup has read the libraries: open it, wait a moment and try again.)"
                    : read.Refusal);
                return;
            }

            string? prefix = StateScaffold.FindPrefix(names);
            // A missing Loops loads bare, so the cell reads SetVars, not loops.SetVars (spec 0015).
            var engine = read.IsEmpty ? StateEngine.New(prefix ?? "") : read.Engine!;

            // With no Loops in the workbook, a cell already spelled loops.SetVars keeps that spelling.
            var loadAs = prefix ?? (engine.Prefix.Length > 0 ? engine.Prefix : null);
            Func<StateFoldKind, string> callName = k => CallName(k, names, loadAs);

            string? note = null;
            if (read.IsEmpty && prefix == null)
                note = "The Loops library isn't loaded: Save loads it, with no prefix.";
            if (folds.Count == 0)
                note = (note == null ? "" : note + " ") + "Library folds show once the popup has read the libraries.";
            if (!read.IsEmpty && StateDebug.BlankSeedNote(engine) is { } blankSeed)
                note = (note == null ? "" : note + " ") + blankSeed;

            string address = cell.Address(false, false);
            dynamic sheet = cell.Worksheet;
            var excelHwnd = new IntPtr(app.Hwnd);

            StateEngineChange? change = null;
            ShowLambdaPopupCommand.InvokeOnWindowThread(dispatcher =>
            {
                dispatcher.Invoke(() =>
                {
                    var window = new StateWindow(engine, folds, refersTo, callName, note);
                    var wpfHwnd = new WindowInteropHelper(window).EnsureHandle();
                    WindowPositioner.CenterOnExcel(excelHwnd, wpfHwnd);
                    if (window.ShowDialog() == true) change = window.Change;
                });
            });
            if (change == null) return;

            var load = LibrariesToLoad(engine, change, prefix, loadAs, names);
            if (load.Count == 0)
            {
                Save(sheet, address, formula, engine, change, names);
                return;
            }

            LoadThenSave(sheet, address, formula, engine, change, load, loadAs);
        }
        catch (Exception ex)
        {
            Logger.Error("State", ex);
            ShowError($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    ///     How the workbook spells a fold: bare, with the prefix it was loaded under, or with the
    ///     library's folder as the prefix it will be loaded with.
    /// </summary>
    private static string CallName(StateFoldKind kind, IReadOnlyDictionary<string, string> names, string? statePrefix)
    {
        if (kind.IsNative) return kind.Name;
        if (IsLambda(names, kind.Name)) return kind.Name;

        var prefixed = names.Keys
            .Where(n => !n.Contains('!') && n.Contains('.') && StateStepParser.IsNamed(n, kind.Name) && IsLambda(names, n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault();
        if (prefixed != null) return prefixed;

        return string.IsNullOrEmpty(kind.Library)
            ? kind.Name
            : StateScaffold.NameToLoad(kind.Library!, statePrefix, kind.Library!, kind.Name);
    }

    private static bool IsLambda(IReadOnlyDictionary<string, string> names, string name) =>
        names.TryGetValue(name, out var refersTo) && LambdaSignatureParser.IsLambdaFormula(refersTo);

    /// <summary>
    ///     The libraries Save loads first: Loops when a new engine has no SetVars/GetVar, and a
    ///     library fold's own library when the workbook doesn't have that fold yet.
    /// </summary>
    private static List<string> LibrariesToLoad(
        StateEngine engine, StateEngineChange change, string? prefix, string? loadAs, IReadOnlyDictionary<string, string> names)
    {
        var load = new List<string>();
        if (engine.IsNew && prefix == null) load.Add(StateScaffold.LibraryFolder);

        var fold = change.Fold;
        if (fold is { IsNative: false } && !string.IsNullOrEmpty(fold.Library)
            && !IsLambda(names, CallName(fold, names, loadAs))
            && !load.Contains(fold.Library!, StringComparer.OrdinalIgnoreCase))
            load.Add(fold.Library!);

        return load;
    }

    /// <summary>Stores the named step, then writes the cell.</summary>
    private static void Save(
        dynamic sheet, string address, string original, StateEngine engine, StateEngineChange change,
        IReadOnlyDictionary<string, string> names)
    {
        var prefix = StateScaffold.FindPrefix(names) ?? (engine.Prefix.Length > 0 ? engine.Prefix : null);
        var result = StateEngines.Apply(
            engine, change, n => names.TryGetValue(n, out var r) ? r : null, k => CallName(k, names, prefix));
        if (result.Formula == null)
        {
            ShowError(result.Error ?? "Nothing was changed.");
            return;
        }

        try
        {
            dynamic target = sheet.Range[address];
            if ((Convert.ToString(target.Formula2) ?? "") != original)
            {
                ShowError($"{address} changed while /State was open, so nothing was written.");
                return;
            }

            string? previous = null;
            if (result.DefinitionName != null)
            {
                previous = names.TryGetValue(result.DefinitionName, out var old) ? old : null;
                LambdaLoader.InjectLambda(result.DefinitionName, result.Definition!);
                Logger.Info($"State: Stored {result.DefinitionName}");
            }

            try
            {
                target.Formula2 = result.Formula;
            }
            catch
            {
                // The cell stays as it was, so the name goes back too.
                if (previous != null) LambdaLoader.InjectLambda(result.DefinitionName!, previous);
                else if (result.DefinitionName != null) sheet.Parent.Names.Item(result.DefinitionName).Delete();
                throw;
            }

            Logger.Info($"State: Written to {address}");
        }
        catch (Exception ex)
        {
            Logger.Error("State/Write", ex);
            ShowError($"Excel refused the formula, so the cell wasn't changed. ({ex.Message})");
        }
    }

    /// <summary>
    ///     Fetches the libraries, then (on Excel's thread) adds them and saves. Excel is free during
    ///     the fetch, so the workbook and the cell are checked again before anything changes.
    /// </summary>
    private static void LoadThenSave(
        dynamic sheet, string address, string original, StateEngine engine, StateEngineChange change, List<string> folders,
        string? statePrefix)
    {
        Task.Run(async () =>
        {
            try
            {
                var provider = ShowLambdaPopupCommand.CreateProvider();
                var libraries = await provider.GetLibrariesAsync();
                var loads = new List<(IReadOnlyList<(string Name, string Formula)> Lambdas, string Comment)>();

                foreach (var folder in folders)
                {
                    var info = libraries.FirstOrDefault(l =>
                        string.Equals(l.FolderName, folder, StringComparison.OrdinalIgnoreCase));
                    if (info == null)
                    {
                        ShowError($"The {folder} library isn't in your configured repos, so nothing was written.");
                        return;
                    }

                    var loadPrefix = StateScaffold.LoadPrefix(info.FolderName, statePrefix, info.DefaultPrefix);
                    var lambdas = info.IsLocal
                        ? provider.LoadLocalLibrary(info.LocalSourceConfig!, info.FolderName, loadPrefix)
                        : await provider.LoadLibraryAsync(info.RepoConfig, info.FolderName, loadPrefix);
                    var source = info.IsLocal ? info.LocalSourceConfig!.Path : info.RepoConfig.Url;
                    loads.Add((lambdas, LambdaLoader.BuildComment(source, info.FolderName, loadPrefix)));
                }

                ExcelAsyncUtil.QueueAsMacro(() =>
                {
                    try
                    {
                        dynamic app = ExcelDnaUtil.Application;
                        if ((string)app.ActiveWorkbook.Name != (string)sheet.Parent.Name)
                        {
                            ShowError("The workbook changed while the library loaded, so nothing was written.");
                            return;
                        }

                        if ((Convert.ToString(sheet.Range[address].Formula2) ?? "") != original)
                        {
                            ShowError($"{address} changed while the library loaded, so nothing was written.");
                            return;
                        }

                        foreach (var (lambdas, comment) in loads)
                            foreach (var (name, lambda) in lambdas)
                                LambdaLoader.InjectLambda(name, lambda, comment);

                        Dictionary<string, string> names = ConvertLetToLambdaCommand.GetExistingNames(app.ActiveWorkbook);

                        // A new engine is written with the prefix the library actually loaded under.
                        var saved = engine;
                        if (engine.IsNew && StateScaffold.FindPrefix(names) is { } loaded)
                            saved = StateEngine.New(loaded);

                        Save(sheet, address, original, saved, change, names);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("State/Inject", ex);
                        ShowError($"Couldn't load the library, so nothing was written: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error("State/Fetch", ex);
                ShowError($"Couldn't load the library, so nothing was written: {ex.Message}");
            }
        });
    }

    private static void ShowError(string message)
    {
        try
        {
            MessageBox.Show(message, "Lambda Boss", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            Logger.Info($"ShowError: {message}");
        }
    }
}
