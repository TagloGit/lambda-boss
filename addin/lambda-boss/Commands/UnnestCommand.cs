using ExcelDna.Integration;
using LambdaBoss.Common;
using LambdaBoss.UI;
using System.Windows;
using System.Windows.Interop;

namespace LambdaBoss.Commands;

/// <summary>
///     Spec 0009 / issue #272 — slash-command handler for <c>/Unnest</c>.
///     Reads the active cell's formula, runs <see cref="UnnestEngine" /> to
///     explode each nested function-call / operator node into a named LET
///     step, opens <see cref="UnnestToLetWindow" /> on the popup's UI thread,
///     and on Save writes the synthesised LET back via
///     <c>activeCell.Formula2</c>. An already-<c>=LET(...)</c> cell is exploded
///     binding-by-binding (issue #273). Engine diagnostics (a malformed formula
///     or a malformed LET) surface via <see cref="MessageBox" />; empty/literal
///     active cells close the popup silently (mirrors <c>/Refactor</c>'s pattern).
/// </summary>
internal static class UnnestCommand
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

            var hasFormula = (bool)activeCell.HasFormula;
            if (!hasFormula)
                return;

            // Formula2 is the dynamic-array-aware reader — the legacy
            // Formula property returns `@`-prefixed text for spill-shaped
            // cells, which would corrupt the decomposition.
            var formula = (string)activeCell.Formula2;

            // Snapshot the workbook + active-sheet defined-name catalogue once
            // when the dialog opens so the auto-namer avoids colliding with
            // workbook names. The engine consults this on every Recompute (no
            // further COM round-trips).
            var worksheet = activeCell.Worksheet;
            var definedNames = SnapshotDefinedNames(workbook, worksheet);

            UnnestResult result;
            try
            {
                result = UnnestEngine.Unnest(formula, definedNames);
            }
            catch (Exception ex)
            {
                Logger.Error("Unnest/Engine", ex);
                ShowError($"Failed to unnest: {ex.Message}");
                return;
            }

            if (result.Diagnostic != null)
            {
                Logger.Info($"Unnest: refused — {result.Diagnostic.Kind}");
                ShowError(result.Diagnostic.Message);
                return;
            }

            var excelHwnd = new IntPtr(app.Hwnd);

            ShowLambdaPopupCommand.InvokeOnWindowThread(dispatcher =>
            {
                string? saved = null;

                dispatcher.Invoke(() =>
                {
                    UnnestResult Recompute(IReadOnlyList<UnnestRowState> rows)
                    {
                        try
                        {
                            return UnnestEngine.Recompute(formula, rows, definedNames);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Unnest/Recompute", ex);
                            // Return the original result so the dialog can keep
                            // its last-known-good preview rather than tearing
                            // itself down on a transient error.
                            return result;
                        }
                    }

                    var window = new UnnestToLetWindow(result, Recompute);
                    var wpfHwnd = new WindowInteropHelper(window).EnsureHandle();
                    WindowPositioner.CenterOnExcel(excelHwnd, wpfHwnd);

                    if (window.ShowDialog() == true)
                        saved = window.SavedFormula;
                });

                if (saved == null) return;

                try
                {
                    activeCell.Formula2 = saved;
                    Logger.Info("Unnest: Wrote LET into active cell");
                }
                catch (Exception ex)
                {
                    Logger.Error("Unnest/SetFormula", ex);
                    ShowError($"Failed to update cell: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Unnest", ex);
            ShowError($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Harvests the workbook-scoped and active-sheet worksheet-scoped
    ///     defined names so the auto-namer never allocates a step name that
    ///     shadows an existing defined name. Sheet qualifiers are stripped so
    ///     the bare identifier (as it would appear in a formula) is compared.
    ///     Failures are swallowed defensively — a missing catalogue just means
    ///     the auto-namer has fewer names to avoid.
    /// </summary>
    private static HashSet<string> SnapshotDefinedNames(dynamic workbook, dynamic worksheet)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        HarvestNames(names, SafeNames(() => workbook.Names), "workbook");
        HarvestNames(names, SafeNames(() => worksheet.Names), "worksheet");

        return names;
    }

    private static dynamic? SafeNames(Func<dynamic?> get)
    {
        try
        {
            return get();
        }
        catch (Exception ex)
        {
            Logger.Error("Unnest/Snapshot/Names", ex);
            return null;
        }
    }

    private static void HarvestNames(HashSet<string> sink, dynamic? names, string scope)
    {
        if (names is null) return;
        try
        {
            var count = (int)names.Count;
            for (var i = 1; i <= count; i++)
            {
                try
                {
                    var item = names[i];
                    if (item.Name is not string key || string.IsNullOrEmpty(key))
                        continue;
                    var bang = key.LastIndexOf('!');
                    if (bang >= 0 && bang + 1 < key.Length)
                        key = key[(bang + 1)..];
                    sink.Add(key);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Unnest/Snapshot/{scope}#{i}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Unnest/Snapshot/{scope}", ex);
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
