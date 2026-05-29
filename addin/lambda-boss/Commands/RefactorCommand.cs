using ExcelDna.Integration;
using LambdaBoss.Common;
using LambdaBoss.UI;
using System.Windows;
using System.Windows.Interop;

namespace LambdaBoss.Commands;

/// <summary>
///     Spec 0008 / PR 1 — slash-command handler for <c>/Refactor</c>.
///     Reads the active cell's formula, runs <see cref="RefactorEngine" />,
///     opens <see cref="RefactorToLetWindow" /> on the popup's UI thread,
///     and on Save writes the synthesised LET back via
///     <c>activeCell.Formula2</c>. Existing-LET diagnostics surface via
///     <see cref="MessageBox" />; empty/literal active cells close the
///     popup silently (mirrors <c>/Gather</c>'s pattern).
/// </summary>
internal static class RefactorCommand
{
    public static void Run()
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            var workbook = app.ActiveWorkbook;
            if (workbook == null)
                return;

            var activeCell = app.ActiveCell;
            if (activeCell == null)
                return;

            var worksheet = activeCell.Worksheet;
            var sheetName = (string)worksheet.Name;

            var hasFormula = (bool)activeCell.HasFormula;
            if (!hasFormula)
                return;

            // Formula2 is the dynamic-array-aware reader — the legacy
            // Formula property returns `@`-prefixed text for spill-shaped
            // cells, which would corrupt our refactor.
            var formula = (string)activeCell.Formula2;

            // PR 3 — snapshot the workbook + active-sheet defined-name
            // catalogue once when the dialog opens. The engine consults
            // this on every Recompute (no further COM round-trips).
            var context = LiveWorkbookContext.Snapshot(workbook, worksheet);

            RefactorResult result;
            try
            {
                result = RefactorEngine.Refactor(formula, sheetName, context);
            }
            catch (Exception ex)
            {
                Logger.Error("Refactor/Engine", ex);
                ShowError($"Failed to refactor: {ex.Message}");
                return;
            }

            if (result.Diagnostic != null)
            {
                Logger.Info($"Refactor: refused — {result.Diagnostic.Kind}");
                ShowError(result.Diagnostic.Message);
                return;
            }

            var excelHwnd = new IntPtr(app.Hwnd);

            ShowLambdaPopupCommand.InvokeOnWindowThread(dispatcher =>
            {
                string? saved = null;

                dispatcher.Invoke(() =>
                {
                    RefactorResult Recompute(IReadOnlyList<RefactorRowState> rows)
                    {
                        try
                        {
                            return RefactorEngine.Recompute(formula, sheetName, rows, context);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Refactor/Recompute", ex);
                            // Return the original result so the dialog can
                            // keep its last-known-good preview rather than
                            // tearing itself down on a transient error.
                            return result;
                        }
                    }

                    var window = new RefactorToLetWindow(result, Recompute);
                    var wpfHwnd = new WindowInteropHelper(window).EnsureHandle();
                    WindowPositioner.CenterOnExcel(excelHwnd, wpfHwnd);

                    if (window.ShowDialog() == true)
                        saved = window.SavedFormula;
                });

                if (saved == null) return;

                try
                {
                    activeCell.Formula2 = saved;
                    Logger.Info("Refactor: Wrote LET into active cell");
                }
                catch (Exception ex)
                {
                    Logger.Error("Refactor/SetFormula", ex);
                    ShowError($"Failed to update cell: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Refactor", ex);
            ShowError($"Unexpected error: {ex.Message}");
        }
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

    /// <summary>
    ///     Live adapter for <see cref="IWorkbookContext" />. Snapshots
    ///     workbook-scoped names AND the active sheet's worksheet-scoped
    ///     names once when the dialog opens (per spec 0008 / PR 3 — the
    ///     dialog reads the catalogue once, then no more COM traffic until
    ///     Save). Hidden names and worksheet-scoped names whose RefersTo
    ///     can't be read (closed external sources, etc.) are skipped
    ///     defensively rather than aborted on.
    /// </summary>
    private sealed class LiveWorkbookContext : IWorkbookContext
    {
        private LiveWorkbookContext(IReadOnlyDictionary<string, string> names)
        {
            WorkbookNames = names;
        }

        public IReadOnlyDictionary<string, string> WorkbookNames { get; }

        public static LiveWorkbookContext Snapshot(dynamic workbook, dynamic worksheet)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            TryHarvestNames(dict, workbook.Names, "workbook");

            try
            {
                TryHarvestNames(dict, worksheet.Names, "worksheet");
            }
            catch (Exception ex)
            {
                // worksheet.Names doesn't exist in older Excel object models;
                // workbook-scoped names alone are still useful.
                Logger.Error("Refactor/Snapshot/Worksheet", ex);
            }

            return new LiveWorkbookContext(dict);
        }

        private static void TryHarvestNames(
            Dictionary<string, string> sink, dynamic? names, string scope)
        {
            if (names is null) return;
            try
            {
                var count = (int)names.Count;
                for (var i = 1; i <= count; i++)
                {
                    string? key = null;
                    try
                    {
                        var item = names[i];
                        key = item.Name as string;
                        if (string.IsNullOrEmpty(key)) continue;
                        // Strip the sheet qualifier from worksheet-scoped
                        // names so identifier lookup matches the bare
                        // identifier as written in the formula.
                        var bang = key!.LastIndexOf('!');
                        if (bang >= 0 && bang + 1 < key.Length)
                            key = key[(bang + 1)..];
                        var refersTo = item.RefersTo as string ?? string.Empty;
                        // Last-write-wins when a worksheet-scoped name
                        // shadows a workbook-scoped one of the same
                        // identifier. Excel's own resolution prefers
                        // worksheet scope for the active sheet, and the
                        // worksheet pass runs second.
                        sink[key] = refersTo;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Refactor/Snapshot/{scope}#{i}({key ?? "?"})", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Refactor/Snapshot/{scope}", ex);
            }
        }
    }
}