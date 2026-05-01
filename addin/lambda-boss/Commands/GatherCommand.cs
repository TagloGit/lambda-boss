using System.Windows;
using System.Windows.Interop;

using ExcelDna.Integration;

using LambdaBoss.UI;

using Taglo.Excel.Common;

namespace LambdaBoss.Commands;

/// <summary>
///     Slash-command handler for <c>/Gather</c>. Reads the active cell and
///     the multi-selection (PR 7), synthesises a single <c>=LET(...)</c>
///     formula equivalent to the calculation graph rooted at the active
///     cell, and on Save writes the LET back to the sink. PR 7 also routes
///     refusals (cycle in the graph, multi-sink selection) into a
///     <c>MessageBox</c> rather than opening the dialog.
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
            var source = new LiveCellSource(workbook, sheetName);
            var selection = ReadSelection(app, sheetName, sink);

            GatherResult? result;
            try
            {
                result = GatherEngine.Gather(sink, selection, source);
            }
            catch (Exception ex)
            {
                Logger.Error("Gather/Engine", ex);
                ShowError($"Failed to gather: {ex.Message}");
                return;
            }

            if (result == null)
                return;

            if (result.Diagnostic != null)
            {
                Logger.Info($"Gather: refused — {result.Diagnostic.Kind}");
                ShowError(result.Diagnostic.Message);
                return;
            }

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
                    // Formula2 is the dynamic-array-aware setter — the
                    // legacy Formula property silently wraps array refs
                    // like `A1#` with the implicit-intersection `@`
                    // operator on write, which would scalarise our LET.
                    activeCell.Formula2 = saved;
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
    ///     Enumerates every cell in <c>Application.Selection</c> on the
    ///     active sheet, including each disjoint area when the user has
    ///     Ctrl-clicked a multi-area range. Falls back to the sink alone
    ///     if the selection can't be read for any reason — the engine's
    ///     multi-sink check short-circuits on a single-cell list, so this
    ///     keeps the command working even when the COM call surface
    ///     misbehaves. Selections are restricted to the active sheet; we
    ///     don't currently model multi-sheet selections (which Excel
    ///     allows via Ctrl-click on a sheet tab) — those would surface as
    ///     a single-area Selection on the active sheet here, and the
    ///     multi-sink check operates on that subset.
    /// </summary>
    private static List<CellRef> ReadSelection(dynamic app, string sheetName, CellRef sink)
    {
        try
        {
            dynamic? selection = app.Selection;
            if (selection == null)
                return new List<CellRef> { sink };

            var cells = new List<CellRef>();
            dynamic areas = selection.Areas;
            int areaCount = (int)areas.Count;
            for (var a = 1; a <= areaCount; a++)
            {
                dynamic area = areas[a];
                int firstRow = (int)area.Row;
                int firstCol = (int)area.Column;
                int rowCount = (int)area.Rows.Count;
                int colCount = (int)area.Columns.Count;
                for (var r = 0; r < rowCount; r++)
                for (var c = 0; c < colCount; c++)
                    cells.Add(new CellRef(sheetName, firstCol + c, firstRow + r));
            }

            if (cells.Count == 0)
                cells.Add(sink);
            return cells;
        }
        catch (Exception ex)
        {
            Logger.Error("Gather/ReadSelection", ex);
            return new List<CellRef> { sink };
        }
    }

    /// <summary>
    ///     Live adapter over the active workbook. Resolves
    ///     <see cref="CellRef.Sheet" /> case-insensitively against the
    ///     workbook's worksheets and returns null for external-workbook
    ///     refs (we never reach into other workbooks) and for refs into
    ///     sheets that aren't part of this workbook — both classify as
    ///     leaf inputs upstream.
    /// </summary>
    private sealed class LiveCellSource : ICellSource
    {
        private readonly dynamic _workbook;

        public LiveCellSource(dynamic workbook, string sinkSheet)
        {
            _workbook = workbook;
            SinkSheet = sinkSheet;
        }

        public string SinkSheet { get; }

        public string? GetFormula(CellRef cell)
        {
            if (cell.IsExternal)
                return null;
            var sheet = TryGetWorksheet(cell.Sheet);
            if (sheet == null)
                return null;
            try
            {
                dynamic range = sheet.Cells[cell.Row, cell.Column];
                if ((bool)range.HasFormula)
                    return (string)range.Formula2;
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Gather/GetFormula({cell.Sheet}!{cell.A1Address})", ex);
                return null;
            }
        }

        public string? GetCellAboveText(CellRef cell)
        {
            if (cell.IsExternal || cell.Row <= 1)
                return null;
            return ReadStringValue(cell.Sheet, cell.Row - 1, cell.Column,
                $"GetCellAboveText({cell.Sheet}!{cell.A1Address})");
        }

        public string? GetCellLeftText(CellRef cell)
        {
            if (cell.IsExternal || cell.Column <= 1)
                return null;
            return ReadStringValue(cell.Sheet, cell.Row, cell.Column - 1,
                $"GetCellLeftText({cell.Sheet}!{cell.A1Address})");
        }

        public bool HasSpill(CellRef cell)
        {
            if (cell.IsExternal)
                return false;
            var sheet = TryGetWorksheet(cell.Sheet);
            if (sheet == null)
                return false;
            try
            {
                dynamic range = sheet.Cells[cell.Row, cell.Column];
                return (bool)range.HasSpill;
            }
            catch (Exception ex)
            {
                // Range.HasSpill is Excel 365 only. On older builds the
                // property doesn't exist and the dynamic call throws — log
                // and treat as non-spilling. The plan documents this as
                // "modern Excel 365 only, no fallback"; this catch is the
                // graceful-degradation safety net rather than a feature.
                Logger.Error($"Gather/HasSpill({cell.Sheet}!{cell.A1Address})", ex);
                return false;
            }
        }

        private string? ReadStringValue(string sheetName, int row, int column, string context)
        {
            var sheet = TryGetWorksheet(sheetName);
            if (sheet == null)
                return null;
            try
            {
                dynamic range = sheet.Cells[row, column];
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

        // The COM Worksheets[name] indexer throws when the name is missing;
        // catching the exception is the cheapest "does this sheet exist?"
        // probe. Walking the collection by index is also valid but pays a
        // round-trip per sheet.
        private dynamic? TryGetWorksheet(string sheetName)
        {
            try
            {
                return _workbook.Worksheets[sheetName];
            }
            catch
            {
                return null;
            }
        }
    }
}
