using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;

using ExcelDna.Integration;

using LambdaBoss.Common;
using LambdaBoss.UI;

namespace LambdaBoss.Commands;

/// <summary>
///     Spec 0010 (spike) / issue #279 — <c>/Debug Lambda</c>. Extracts a nested
///     <c>LAMBDA(...)</c> from the active cell onto a fresh scratch worksheet as
///     an editable multiline <c>=LET(...)</c> wired to sample inputs, so the user
///     can debug it in real cells and then convert it back with <c>/LET to LAMBDA</c>.
///
///     <para>
///     A modal picker chooses the scope + example index (no Excel work while
///     open). Then, on the macro thread: each input the body needs is
///     <em>captured</em> by evaluating it in the lambda's enclosing context on the
///     <em>source</em> sheet (so sheet-local refs and tables resolve) and the
///     resulting value snapshot is written to the scratch sheet under a
///     sheet-scoped name — the lambda's own params as <c>name_in</c> (seeded into
///     the LET so <c>/LET to LAMBDA</c> lifts exactly them), enclosing-LET bindings
///     and enclosing-lambda params under their own name (referenced freely by the
///     body). A recognised iterator's param is sliced (<c>CHOOSEROWS</c>); a param
///     under a custom higher-order function is left as a blank cell to fill in
///     (probe-capture is a follow-up). Deleting the sheet removes its scoped names.
///     </para>
/// </summary>
internal static class DebugLambdaCommand
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
            var sourceSheet = activeCell.Worksheet;

            var discovery = DebugNestedEngine.Discover(formula);
            if (discovery.Diagnostic != null)
            {
                Logger.Info($"DebugLambda: refused — {discovery.Diagnostic.Kind}");
                ShowError(discovery.Diagnostic.Message);
                return;
            }

            // Modal picker — pure UI, no Excel. Blocks the macro thread until closed.
            var excelHwnd = new IntPtr(app.Hwnd);
            string? scopeKey = null;
            var index = 1;

            ShowLambdaPopupCommand.InvokeOnWindowThread(dispatcher =>
            {
                dispatcher.Invoke(() =>
                {
                    var picker = new DebugScopePickerWindow(formula, discovery);
                    var hwnd = new WindowInteropHelper(picker).EnsureHandle();
                    WindowPositioner.CenterOnExcel(excelHwnd, hwnd);
                    if (picker.ShowDialog() == true)
                    {
                        scopeKey = picker.SelectedScopeKey;
                        index = picker.ExampleIndex;
                    }
                });
            });

            if (scopeKey is null)
                return;

            var scope = discovery.Scopes.FirstOrDefault(s => s.Key == scopeKey);
            GenerateSheet(app, workbook, sourceSheet, formula, scopeKey, scope?.Label ?? "", index);
        }
        catch (Exception ex)
        {
            Logger.Error("DebugLambda", ex);
            ShowError($"Unexpected error: {ex.Message}");
        }
    }

    private static void GenerateSheet(
        dynamic app, dynamic workbook, dynamic sourceSheet,
        string formula, string scopeKey, string scopeLabel, int index)
    {
        var inputs = DebugNestedEngine.AnalyzeInputs(formula, scopeKey);
        var seeds = DebugNestedEngine.SuggestPins(formula, scopeKey, index);
        var seedByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in seeds)
            seedByName[p.Param] = p.Expression;

        // Capture every input the body needs (on the source sheet, before we add
        // the new sheet) and collect the own-param seeds for the LET.
        var scratch = ScratchCell.Locate(sourceSheet);
        var captures = new List<CapturedInput>();
        var ownParamSeeds = new List<DebugPin>();

        object? priorScreenUpdating = null;
        try
        {
            try { priorScreenUpdating = app.ScreenUpdating; app.ScreenUpdating = false; }
            catch { /* cosmetic only */ }

            foreach (var inp in inputs)
            {
                string defName;
                string captureExpr;
                switch (inp.Kind)
                {
                    case DebugInputKind.Param:
                        defName = inp.Name + "_in";
                        ownParamSeeds.Add(new DebugPin(inp.Name, defName));
                        seedByName.TryGetValue(inp.Name, out captureExpr!);
                        break;
                    case DebugInputKind.EnclosingParam:
                        defName = inp.Name;
                        seedByName.TryGetValue(inp.Name, out captureExpr!);
                        break;
                    case DebugInputKind.LetBinding:
                        defName = inp.Name;
                        captureExpr = inp.Name; // capture the binding's value in context
                        break;
                    default:
                        continue; // External — resolves on its own
                }

                captures.Add(Capture(sourceSheet, scratch, formula, scopeKey, defName, inp.Name, captureExpr));
            }

            var letText = DebugNestedEngine.BuildDebugLet(formula, scopeKey, ownParamSeeds);
            if (string.IsNullOrEmpty(letText))
            {
                ShowError("Couldn't build a debuggable LET for this lambda.");
                return;
            }

            var sheet = CreateFreshSheet(workbook);
            WriteSheet(sheet, formula, scopeLabel, captures, letText);

            try { sheet.Activate(); } catch (Exception ex) { Logger.Error("DebugLambda/Activate", ex); }
            Logger.Info($"DebugLambda: wrote debug sheet for {scopeKey}");
        }
        finally
        {
            try
            {
                if (priorScreenUpdating is bool b) app.ScreenUpdating = b;
            }
            catch { /* leave as-is */ }
        }
    }

    private static CapturedInput Capture(
        dynamic sourceSheet, ScratchCell scratch,
        string formula, string scopeKey, string defName, string label, string captureExpr)
    {
        if (string.IsNullOrWhiteSpace(captureExpr))
            return CapturedInput.Manual(defName, label);

        var capFormula = DebugNestedEngine.BuildCaptureFormula(formula, scopeKey, captureExpr);
        if (capFormula is null)
            return CapturedInput.Manual(defName, label);

        dynamic cell = sourceSheet.Cells[scratch.Row, scratch.Col];
        try
        {
            cell.Formula2 = capFormula;

            var spill = false;
            try { spill = (bool)cell.HasSpill; }
            catch { /* HasSpill is 365-only */ }

            if (spill)
            {
                dynamic range = cell.SpillingToRange;
                var rows = (int)range.Rows.Count;
                var cols = (int)range.Columns.Count;
                var block = (object[,])range.Value2;
                return CapturedInput.FromBlock(defName, label, block, rows, cols);
            }

            return CapturedInput.FromScalar(defName, label, cell.Value2);
        }
        catch (Exception ex)
        {
            Logger.Error($"DebugLambda/Capture({label})", ex);
            return CapturedInput.Manual(defName, label);
        }
        finally
        {
            try { cell.ClearContents(); }
            catch (Exception ex) { Logger.Error("DebugLambda/ClearScratch", ex); }
        }
    }

    private static dynamic CreateFreshSheet(dynamic workbook)
    {
        dynamic sheets = workbook.Worksheets;
        var count = (int)sheets.Count;

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i <= count; i++)
            existing.Add((string)sheets[i].Name);

        var n = 1;
        string name;
        do
        {
            name = "LB Debug " + n.ToString(CultureInfo.InvariantCulture);
            n++;
        } while (existing.Contains(name));

        dynamic last = sheets[count];
        dynamic sheet = sheets.Add(Missing.Value, last); // Add(Before, After) — after the last sheet
        sheet.Name = name;
        return sheet;
    }

    private static void WriteSheet(
        dynamic sheet, string formula, string scopeLabel,
        IReadOnlyList<CapturedInput> captures, string letText)
    {
        var sheetName = (string)sheet.Name;

        SetCell(sheet, 1, 1, "Debug Lambda");
        SetCell(sheet, 2, 1, "Source");
        // The source is an informational echo, not a live formula — write it as
        // text so Excel doesn't enter it (the Value2 setter would route a leading
        // '=' through the legacy formula path, scalarising dynamic-array refs with
        // '@' and resolving any sheet-local refs against this sheet).
        SetFormulaText(sheet, 2, 2, formula);
        SetCell(sheet, 3, 1, "Lambda");
        SetCell(sheet, 3, 2, scopeLabel);

        var row = 5;
        SetCell(sheet, row, 1, "Inputs (edit freely — these are samples):");
        row++;

        foreach (var cap in captures)
        {
            SetCell(sheet, row, 1, cap.Label);
            var anchorCol = 2;

            if (cap.IsManual)
            {
                SetCell(sheet, row, anchorCol, cap.Note);
                DefineName(sheet, sheetName, cap.DefName, row, anchorCol, row, anchorCol);
                row += 2;
                continue;
            }

            if (cap.Block is not null)
            {
                var endRow = row + cap.Rows - 1;
                var endCol = anchorCol + cap.Cols - 1;
                dynamic top = sheet.Cells[row, anchorCol];
                dynamic bottom = sheet.Cells[endRow, endCol];
                dynamic range = sheet.Range[top, bottom];
                try { range.Value2 = cap.Block; }
                catch (Exception ex) { Logger.Error($"DebugLambda/WriteBlock({cap.Label})", ex); }
                DefineName(sheet, sheetName, cap.DefName, row, anchorCol, endRow, endCol);
                row = endRow + 2;
            }
            else
            {
                try { sheet.Cells[row, anchorCol].Value2 = cap.Scalar; }
                catch (Exception ex) { Logger.Error($"DebugLambda/WriteScalar({cap.Label})", ex); }
                DefineName(sheet, sheetName, cap.DefName, row, anchorCol, row, anchorCol);
                row += 2;
            }
        }

        row++;
        SetCell(sheet, row, 1, "Debug LET (play here, then /LET to LAMBDA):");
        row++;
        try { sheet.Cells[row, 2].Formula2 = letText; }
        catch (Exception ex) { Logger.Error("DebugLambda/WriteLet", ex); }

        try { sheet.Columns[1].ColumnWidth = 16; }
        catch { /* cosmetic */ }
    }

    private static void SetCell(dynamic sheet, int row, int col, string text)
    {
        try { sheet.Cells[row, col].Value2 = text; }
        catch (Exception ex) { Logger.Error($"DebugLambda/SetCell({row},{col})", ex); }
    }

    /// <summary>
    ///     Writes <paramref name="text" /> that may start with <c>=</c> as literal
    ///     text (forcing the cell to text format first) so Excel displays it
    ///     verbatim instead of entering it as a formula.
    /// </summary>
    private static void SetFormulaText(dynamic sheet, int row, int col, string text)
    {
        try
        {
            dynamic cell = sheet.Cells[row, col];
            cell.NumberFormat = "@";
            cell.Value2 = text;
        }
        catch (Exception ex) { Logger.Error($"DebugLambda/SetFormulaText({row},{col})", ex); }
    }

    private static void DefineName(
        dynamic sheet, string sheetName, string name, int r0, int c0, int r1, int c1)
    {
        try
        {
            dynamic top = sheet.Cells[r0, c0];
            dynamic bottom = sheet.Cells[r1, c1];
            dynamic range = sheet.Range[top, bottom];
            var address = (string)range.Address(true, true); // $A$1 absolute
            var refersTo = "='" + sheetName.Replace("'", "''") + "'!" + address;
            sheet.Names.Add(name, refersTo); // sheet-scoped (worksheet.Names)
        }
        catch (Exception ex)
        {
            Logger.Error($"DebugLambda/DefineName({name})", ex);
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

    /// <summary>A scratch cell on the source sheet, parked beyond the used range.</summary>
    private readonly struct ScratchCell
    {
        public int Row { get; }
        public int Col { get; }

        private ScratchCell(int row, int col)
        {
            Row = row;
            Col = col;
        }

        public static ScratchCell Locate(dynamic sheet)
        {
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
                Logger.Error("DebugLambda/ScratchCell", ex);
            }

            return new ScratchCell(row, col);
        }
    }

    /// <summary>A captured input's value snapshot (scalar, 2-D block, or a manual placeholder).</summary>
    private sealed class CapturedInput
    {
        public string DefName { get; private init; } = "";
        public string Label { get; private init; } = "";
        public object? Scalar { get; private init; }
        public object[,]? Block { get; private init; }
        public int Rows { get; private init; }
        public int Cols { get; private init; }
        public bool IsManual { get; private init; }
        public string Note { get; private init; } = "";

        public static CapturedInput FromScalar(string def, string label, object? value) =>
            new() { DefName = def, Label = label, Scalar = value };

        public static CapturedInput FromBlock(string def, string label, object[,] block, int rows, int cols) =>
            new() { DefName = def, Label = label, Block = block, Rows = rows, Cols = cols };

        public static CapturedInput Manual(string def, string label) =>
            new() { DefName = def, Label = label, IsManual = true, Note = "(fill in an example value)" };
    }
}
