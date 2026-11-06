using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Interop;

using ExcelDna.Integration;

using LambdaBoss.Common;
using LambdaBoss.UI;

namespace LambdaBoss.Commands;

/// <summary>
///     Spec 0010 (spike) / issue #279 — slash-command handler for
///     <c>/Debug Nested</c>. Reads the active cell's formula, finds its
///     <c>LAMBDA(...)</c> scopes (<see cref="DebugNestedEngine" />), and opens
///     <see cref="DebugNestedWindow" /> so the user can pin one concrete example
///     and watch each step of a chosen lambda body compute a live value.
///
///     <para>
///     Unlike <c>/Unnest</c>'s modal dialog (which blocks Excel's main thread
///     while open), this window is <em>modeless</em>: the debugger must evaluate
///     probe formulas against the live grid while it's open, which requires
///     Excel to stay responsive. Each evaluation is marshalled back onto the
///     Excel macro thread via <see cref="ExcelAsyncUtil.QueueAsMacro" /> — the
///     window calls the supplied delegate from a background task and blocks only
///     its own UI thread until the macro completes. The probe is written to a
///     scratch cell beyond the sheet's used range, read back, and cleared
///     (mechanism C). Nothing is written to the active cell — this is a read-only
///     debugging view, not a refactor.
///     </para>
/// </summary>
internal static class DebugNestedCommand
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

            if (!(bool)activeCell.HasFormula)
                return;

            var formula = (string)activeCell.Formula2;

            var discovery = DebugNestedEngine.Discover(formula);
            if (discovery.Diagnostic != null)
            {
                Logger.Info($"DebugNested: refused — {discovery.Diagnostic.Kind}");
                ShowError(discovery.Diagnostic.Message);
                return;
            }

            var worksheet = activeCell.Worksheet;
            var evaluator = ScratchEvaluator.Create(app, worksheet);

            // A blocking evaluate delegate that is safe to call from the WPF
            // window thread: it hops onto the Excel macro thread (free, because
            // the window is modeless) and blocks the caller until the batch is
            // computed against the live grid.
            IReadOnlyList<DebugValue> Evaluate(IReadOnlyList<string> formulas)
            {
                IReadOnlyList<DebugValue> result =
                    formulas.Select(_ => new DebugValue("(not computed)", true)).ToArray();
                var done = new ManualResetEventSlim(false);

                ExcelAsyncUtil.QueueAsMacro(() =>
                {
                    try
                    {
                        result = evaluator.EvaluateBatch(formulas);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("DebugNested/Evaluate", ex);
                    }
                    finally
                    {
                        done.Set();
                    }
                });

                done.Wait();
                return result;
            }

            var excelHwnd = new IntPtr(app.Hwnd);

            ShowLambdaPopupCommand.InvokeOnWindowThread(dispatcher =>
            {
                dispatcher.Invoke(() =>
                {
                    var window = new DebugNestedWindow(formula, discovery, Evaluate);
                    var wpfHwnd = new WindowInteropHelper(window).EnsureHandle();
                    WindowPositioner.CenterOnExcel(excelHwnd, wpfHwnd);
                    window.Show();
                });
            });
        }
        catch (Exception ex)
        {
            Logger.Error("DebugNested", ex);
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
    ///     Evaluates probe formulas (mechanism C) by writing each to a scratch
    ///     cell beyond the active sheet's used range, reading the result back,
    ///     and clearing the cell. The scratch cell is on the <em>same sheet</em>
    ///     as the active cell so that sheet-local A1 references in the original
    ///     formula resolve to the same targets. All members must run on the Excel
    ///     main thread (the command only ever calls them inside QueueAsMacro).
    /// </summary>
    private sealed class ScratchEvaluator
    {
        private const int MaxPreviewCells = 12;

        private readonly dynamic _app;
        private readonly dynamic _sheet;
        private readonly int _row;
        private readonly int _col;

        private ScratchEvaluator(dynamic app, dynamic sheet, int row, int col)
        {
            _app = app;
            _sheet = sheet;
            _row = row;
            _col = col;
        }

        public static ScratchEvaluator Create(dynamic app, dynamic sheet)
        {
            // Park the scratch cell a few rows/cols beyond the used range so a
            // spilled probe has room and never clobbers real data.
            var row = 1;
            var col = 1;
            try
            {
                dynamic used = sheet.UsedRange;
                row = (int)used.Row + (int)used.Rows.Count + 2;
                col = (int)used.Column + (int)used.Columns.Count + 2;
            }
            catch (Exception ex)
            {
                Logger.Error("DebugNested/Scratch/UsedRange", ex);
            }

            return new ScratchEvaluator(app, sheet, row, col);
        }

        public IReadOnlyList<DebugValue> EvaluateBatch(IReadOnlyList<string> formulas)
        {
            object? priorScreenUpdating = null;
            try
            {
                priorScreenUpdating = _app.ScreenUpdating;
                _app.ScreenUpdating = false;
            }
            catch
            {
                // Non-fatal cosmetics — proceed without toggling.
            }

            try
            {
                return formulas.Select(EvaluateOne).ToArray();
            }
            finally
            {
                try
                {
                    if (priorScreenUpdating is bool b)
                        _app.ScreenUpdating = b;
                }
                catch
                {
                    // Ignore — leave ScreenUpdating in whatever state we managed.
                }
            }
        }

        private DebugValue EvaluateOne(string formula)
        {
            dynamic cell = _sheet.Cells[_row, _col];
            try
            {
                cell.Formula2 = formula;

                var spill = false;
                try
                {
                    spill = (bool)cell.HasSpill;
                }
                catch
                {
                    // Range.HasSpill is Excel-365 only; treat as scalar otherwise.
                }

                if (spill)
                    return FormatSpill(cell);

                var text = (string)cell.Text;
                return new DebugValue(text, IsErrorText(text));
            }
            catch (Exception ex)
            {
                Logger.Error("DebugNested/EvaluateOne", ex);
                return new DebugValue("(uncomputable)", true);
            }
            finally
            {
                try
                {
                    cell.ClearContents();
                }
                catch (Exception ex)
                {
                    Logger.Error("DebugNested/ClearScratch", ex);
                }
            }
        }

        private static DebugValue FormatSpill(dynamic anchor)
        {
            try
            {
                dynamic range = anchor.SpillingToRange;
                var rows = (int)range.Rows.Count;
                var cols = (int)range.Columns.Count;

                var values = range.Value2;
                var sb = new StringBuilder();
                sb.Append('{').Append(rows).Append('×').Append(cols).Append("} ");

                var shown = 0;
                var truncated = false;
                if (rows == 1 && cols == 1)
                {
                    sb.Append(Stringify(values));
                }
                else
                {
                    for (var r = 1; r <= rows && !truncated; r++)
                    for (var c = 1; c <= cols; c++)
                    {
                        if (shown >= MaxPreviewCells)
                        {
                            truncated = true;
                            break;
                        }

                        if (shown > 0) sb.Append(", ");
                        sb.Append(Stringify(values[r, c]));
                        shown++;
                    }

                    if (truncated) sb.Append(", …");
                }

                return new DebugValue(sb.ToString(), false);
            }
            catch (Exception ex)
            {
                Logger.Error("DebugNested/FormatSpill", ex);
                return new DebugValue("(array)", false);
            }
        }

        private static string Stringify(object? v)
        {
            return v switch
            {
                null => "",
                double d => d.ToString("0.######", CultureInfo.InvariantCulture),
                bool b => b ? "TRUE" : "FALSE",
                _ => v.ToString() ?? ""
            };
        }

        private static bool IsErrorText(string text)
        {
            return !string.IsNullOrEmpty(text) && text[0] == '#';
        }
    }
}
