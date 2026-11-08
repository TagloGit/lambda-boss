using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

using ExcelDna.Integration;

using LambdaBoss.Common;
using LambdaBoss.UI;

namespace LambdaBoss.Commands;

/// <summary>
///     Slash-command handler for <c>/State</c> (spec 0011, part 1): scaffolds a new state step
///     from a 2×k label/value selection or typed names, and writes it to an empty cell.
/// </summary>
internal static class StateStepCommand
{
    private const int MaxPrefillColumns = 10;
    private const int MaxRowsToSearch = 1000;

    /// <summary>The prefix used when the library isn't in the workbook yet, for the preview.</summary>
    private const string PreviewPrefix = StateScaffold.LibraryFolder + ".";

    public static void Run()
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            dynamic workbook = app.ActiveWorkbook;
            if (workbook == null)
                return;

            dynamic sheet = app.ActiveCell.Worksheet;

            if (Labs.IsOn(Labs.StateEdit))
            {
                string formula = Convert.ToString(app.ActiveCell.Formula2);
                StateEditView? view = StateEdit.Read(formula);
                if (view != null)
                {
                    RunEdit(app, sheet, formula, view);
                    return;
                }
            }

            // Typed, because a dynamic here would make the dialog call below dynamic too.
            string? prefix = StateScaffold.FindPrefix(ConvertLetToLambdaCommand.GetExistingNames(workbook));
            StateStepDraft draft = InitialDraft(app);

            var filled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!IsEmpty(sheet.Range[draft.Target]))
                filled.Add(StateScaffold.NormalAddress(draft.Target));

            var excelHwnd = new IntPtr(app.Hwnd);

            ShowLambdaPopupCommand.InvokeOnWindowThread(dispatcher =>
            {
                string? message = null;
                while (true)
                {
                    (StateStepDraft? Draft, string? Formula) saved =
                        ShowDialog(dispatcher, excelHwnd, draft, prefix, filled, message);
                    if (saved.Draft == null) return;

                    draft = saved.Draft;
                    if (!IsEmpty(sheet.Range[draft.Target]))
                    {
                        filled.Add(StateScaffold.NormalAddress(draft.Target));
                        message = null;
                        continue;
                    }

                    if (prefix == null)
                    {
                        LoadLibraryThenWrite(sheet, draft);
                        return;
                    }

                    message = TryWrite(sheet, draft.Target, saved.Formula!);
                    if (message == null) return;
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("StateStep", ex);
            ShowError($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Edit mode (spec 0011, part 2): the active cell is a state step, so the dialog lists its
    ///     variables and Save rewrites the cell. It re-opens with the edits if Excel refuses.
    /// </summary>
    private static void RunEdit(dynamic app, dynamic sheet, string formula, StateEditView view)
    {
        if (view.Refusal != null)
        {
            ShowError(view.Refusal);
            return;
        }

        var address = StateScaffold.Address((int)app.ActiveCell.Row, (int)app.ActiveCell.Column);
        var excelHwnd = new IntPtr(app.Hwnd);

        ShowLambdaPopupCommand.InvokeOnWindowThread(dispatcher =>
        {
            var rows = view.Rows;
            string? message = null;
            while (true)
            {
                string? edited = null;
                var currentRows = rows;
                var currentMessage = message;
                dispatcher.Invoke(() =>
                {
                    var window = new StateEditWindow(formula, currentRows, currentMessage);
                    var wpfHwnd = new WindowInteropHelper(window).EnsureHandle();
                    WindowPositioner.CenterOnExcel(excelHwnd, wpfHwnd);

                    if (window.ShowDialog() == true)
                    {
                        edited = window.Formula;
                        currentRows = window.CurrentRows;
                    }
                });
                if (edited == null) return;

                rows = currentRows;
                message = TryWrite(sheet, address, edited);
                if (message == null) return;
            }
        });
    }

    private static (StateStepDraft? Draft, string? Formula) ShowDialog(
        Dispatcher dispatcher,
        IntPtr excelHwnd,
        StateStepDraft draft,
        string? prefix,
        ISet<string> filled,
        string? message)
    {
        StateStepDraft? result = null;
        string? formula = null;
        var note = prefix == null
            ? $"Loops library not loaded. Save loads it with the prefix {StateScaffold.LibraryFolder}."
            : null;

        dispatcher.Invoke(() =>
        {
            var window = new StateStepWindow(draft, prefix ?? PreviewPrefix, filled, note, message);
            var wpfHwnd = new WindowInteropHelper(window).EnsureHandle();
            WindowPositioner.CenterOnExcel(excelHwnd, wpfHwnd);

            if (window.ShowDialog() == true)
            {
                result = window.Result;
                formula = window.Formula;
            }
        });

        return (result, formula);
    }

    /// <summary>
    ///     The fast path: a 2×k block with text labels on top prefills the State field and writes
    ///     below the block. Anything else starts from the active cell with an empty State.
    /// </summary>
    private static StateStepDraft InitialDraft(dynamic app)
    {
        dynamic activeCell = app.ActiveCell;
        var activeAddress = StateScaffold.Address((int)activeCell.Row, (int)activeCell.Column);

        try
        {
            dynamic selection = app.Selection;
            int columns = selection.Columns.Count;
            if ((int)selection.Areas.Count == 1 && (int)selection.Rows.Count == 2 && columns <= MaxPrefillColumns)
            {
                int row = selection.Row;
                int column = selection.Column;

                var labels = new List<object?>();
                var valueRefs = new List<string>();
                for (var i = 0; i < columns; i++)
                {
                    labels.Add(selection.Cells.Item(1, i + 1).Value2);
                    valueRefs.Add(StateScaffold.Address(row + 1, column + i));
                }

                var state = StateScaffold.PrefillState(labels, valueRefs);
                if (state != null)
                    return new StateStepDraft(state, "", FirstEmptyBelow(app.ActiveCell.Worksheet, row + 2, column));
            }
        }
        catch (Exception ex)
        {
            // The selection may not be cells at all (a shape, a chart).
            Logger.Error("StateStep/Selection", ex);
        }

        return new StateStepDraft("", "", activeAddress);
    }

    private static string FirstEmptyBelow(dynamic sheet, int row, int column)
    {
        for (var r = row; r < row + MaxRowsToSearch; r++)
            if (IsEmpty(sheet.Cells.Item(r, column)))
                return StateScaffold.Address(r, column);

        return StateScaffold.Address(row, column);
    }

    // A spill child reports no formula of its own, so the value counts too.
    private static bool IsEmpty(dynamic cell) =>
        string.IsNullOrEmpty(Convert.ToString(cell.Formula2)) && cell.Value2 == null;

    /// <summary>Writes the step. Returns why not, or null when it's written.</summary>
    private static string? TryWrite(dynamic sheet, string address, string formula)
    {
        try
        {
            dynamic target = sheet.Range[address];
            target.Formula2 = formula;
            Logger.Info($"StateStep: Written to {address}");

            try
            {
                target.Select();
            }
            catch (Exception ex)
            {
                // Written either way. Select fails when Tim has moved to another sheet.
                Logger.Error("StateStep/Select", ex);
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Error("StateStep/Write", ex);
            return $"Excel refused the step, so nothing was written. Check the values. ({ex.Message})";
        }
    }

    /// <summary>
    ///     Fetches the Loops names, then adds them to the workbook and writes the step on Excel's
    ///     thread. If the library can't be loaded, nothing is written. Excel is free during the
    ///     fetch, so the workbook and target are checked again before anything changes.
    /// </summary>
    private static void LoadLibraryThenWrite(dynamic sheet, StateStepDraft draft)
    {
        Task.Run(async () =>
        {
            try
            {
                var provider = ShowLambdaPopupCommand.CreateProvider();
                var libraries = await provider.GetLibrariesAsync();
                var info = libraries.FirstOrDefault(l => string.Equals(
                    l.FolderName, StateScaffold.LibraryFolder, StringComparison.OrdinalIgnoreCase));

                if (info == null)
                {
                    ShowError("The Loops library isn't in your configured repos, so nothing was written.");
                    return;
                }

                var lambdas = info.IsLocal
                    ? provider.LoadLocalLibrary(info.LocalSourceConfig!, info.FolderName, info.DefaultPrefix)
                    : await provider.LoadLibraryAsync(info.RepoConfig, info.FolderName, info.DefaultPrefix);

                var needed = lambdas
                    .Where(l => StateScaffold.LibraryNames.Any(n => StateStepParser.IsNamed(l.Name, n)))
                    .ToList();

                var source = info.IsLocal ? info.LocalSourceConfig!.Path : info.RepoConfig.Url;
                var comment = LambdaLoader.BuildComment(source, info.FolderName, info.DefaultPrefix);
                var prefix = info.DefaultPrefix.Length == 0 ? "" : info.DefaultPrefix + ".";
                var formula = StateScaffold.Build(draft.State, draft.Input, prefix).Formula!;

                ExcelAsyncUtil.QueueAsMacro(() =>
                {
                    try
                    {
                        dynamic app = ExcelDnaUtil.Application;
                        if ((string)app.ActiveWorkbook.Name != (string)sheet.Parent.Name)
                        {
                            ShowError("The workbook changed while the Loops library loaded, so nothing was written.");
                            return;
                        }

                        if (!IsEmpty(sheet.Range[draft.Target]))
                        {
                            ShowError($"{draft.Target} filled up while the Loops library loaded, so nothing was written.");
                            return;
                        }

                        foreach (var (name, lambda) in needed)
                            LambdaLoader.InjectLambda(name, lambda, comment);

                        var refused = TryWrite(sheet, draft.Target, formula);
                        if (refused != null) ShowError(refused);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("StateStep/Inject", ex);
                        ShowError($"Couldn't load the Loops library, so nothing was written: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error("StateStep/Fetch", ex);
                ShowError($"Couldn't load the Loops library, so nothing was written: {ex.Message}");
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
