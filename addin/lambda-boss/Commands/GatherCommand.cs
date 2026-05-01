using System.Windows;
using System.Windows.Interop;

using ExcelDna.Integration;

using LambdaBoss.UI;

using Taglo.Excel.Common;

namespace LambdaBoss.Commands;

/// <summary>
///     Slash-command handler for <c>/Gather</c>. Reads the active cell,
///     synthesises a single <c>=LET(...)</c> formula equivalent to the
///     calculation graph rooted there, and on Save writes the LET back to
///     the sink. PR 1 scope: silent no-op when the active cell has no
///     formula; chains and branched DAGs of formula cells on the sink's
///     sheet supported.
/// </summary>
internal static class GatherCommand
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
            int sinkColumn = (int)activeCell.Column;
            int sinkRow = (int)activeCell.Row;

            bool hasFormula = (bool)activeCell.HasFormula;
            if (!hasFormula)
                return;

            var sink = new CellRef(sheetName, sinkColumn, sinkRow);
            var source = new LiveCellSource(worksheet, sheetName);

            GatherResult? result;
            try
            {
                result = GatherEngine.Gather(sink, source);
            }
            catch (Exception ex)
            {
                Logger.Error("Gather/Engine", ex);
                ShowError($"Failed to gather: {ex.Message}");
                return;
            }

            if (result == null)
                return;

            var excelHwnd = new IntPtr(app.Hwnd);

            ShowLambdaPopupCommand.InvokeOnWindowThread(dispatcher =>
            {
                string? saved = null;

                dispatcher.Invoke(() =>
                {
                    var window = new GatherWindow(result);
                    var wpfHwnd = new WindowInteropHelper(window).EnsureHandle();
                    WindowPositioner.CenterOnExcel(excelHwnd, wpfHwnd);

                    if (window.ShowDialog() == true)
                        saved = window.SavedFormula;
                });

                if (saved == null) return;

                try
                {
                    activeCell.Formula = saved;
                    Logger.Info($"Gather: Wrote LET into {sink.A1Address}");
                }
                catch (Exception ex)
                {
                    Logger.Error("Gather/SetFormula", ex);
                    ShowError($"Failed to update cell: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Gather", ex);
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
    ///     Live adapter over a single worksheet. PR 1 reads only from the
    ///     sink's sheet — cross-sheet support lands in PR 3, where this
    ///     adapter will need a workbook-scoped variant.
    /// </summary>
    private sealed class LiveCellSource : ICellSource
    {
        private readonly dynamic _worksheet;

        public LiveCellSource(dynamic worksheet, string sinkSheet)
        {
            _worksheet = worksheet;
            SinkSheet = sinkSheet;
        }

        public string SinkSheet { get; }

        public string? GetFormula(CellRef cell)
        {
            try
            {
                dynamic range = _worksheet.Cells[cell.Row, cell.Column];
                if ((bool)range.HasFormula)
                    return (string)range.Formula;
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Gather/GetFormula({cell.A1Address})", ex);
                return null;
            }
        }

        public string? GetCellAboveText(CellRef cell)
        {
            if (cell.Row <= 1)
                return null;
            return ReadStringValue(cell.Row - 1, cell.Column, $"GetCellAboveText({cell.A1Address})");
        }

        public string? GetCellLeftText(CellRef cell)
        {
            if (cell.Column <= 1)
                return null;
            return ReadStringValue(cell.Row, cell.Column - 1, $"GetCellLeftText({cell.A1Address})");
        }

        private string? ReadStringValue(int row, int column, string context)
        {
            try
            {
                dynamic range = _worksheet.Cells[row, column];
                var value = range.Value2;
                if (value is string s && !string.IsNullOrEmpty(s))
                    return s;
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Gather/{context}", ex);
                return null;
            }
        }
    }
}
