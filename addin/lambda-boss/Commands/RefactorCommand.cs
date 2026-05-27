using System.Windows;
using System.Windows.Interop;

using ExcelDna.Integration;

using LambdaBoss.UI;

using LambdaBoss.Common;

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
            dynamic workbook = app.ActiveWorkbook;
            if (workbook == null)
                return;

            dynamic activeCell = app.ActiveCell;
            if (activeCell == null)
                return;

            dynamic worksheet = activeCell.Worksheet;
            string sheetName = (string)worksheet.Name;

            bool hasFormula = (bool)activeCell.HasFormula;
            if (!hasFormula)
                return;

            // Formula2 is the dynamic-array-aware reader — the legacy
            // Formula property returns `@`-prefixed text for spill-shaped
            // cells, which would corrupt our refactor.
            string formula = (string)activeCell.Formula2;

            RefactorResult result;
            try
            {
                result = RefactorEngine.Refactor(formula, sheetName);
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
                            return RefactorEngine.Recompute(formula, sheetName, rows);
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

                    var window = new RefactorToLetWindow(result, sheetName, Recompute);
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
}
