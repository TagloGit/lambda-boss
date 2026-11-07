using System.Windows;
using System.Windows.Interop;

using ExcelDna.Integration;

using LambdaBoss.UI;

using LambdaBoss.Common;

namespace LambdaBoss.Commands;

/// <summary>
///     Ribbon handler: converts the active cell's =LET(...) formula into a
///     workbook-scoped LAMBDA registered in the Name Manager.
/// </summary>
internal static class ConvertLetToLambdaCommand
{
    public static void Run()
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            dynamic workbook = app.ActiveWorkbook;
            if (workbook == null)
            {
                ShowError("No active workbook.");
                return;
            }

            dynamic activeCell = app.ActiveCell;
            string? formula = activeCell?.Formula2 as string;

            if (!LetParser.IsLetFormula(formula))
            {
                ShowError("Active cell does not contain a =LET(...) formula.");
                return;
            }

            ParsedLet parsed;
            try
            {
                parsed = LetParser.Parse(formula!);
            }
            catch (FormatException ex)
            {
                ShowError($"Could not parse LET formula: {ex.Message}");
                return;
            }

            var existingNames = GetExistingNames(workbook);
            var excelHwnd = new IntPtr(app.Hwnd);
            var stateStep = Labs.IsOn(Labs.StateLetToLambda) ? StateStepParser.TryParse(parsed) : null;
            dynamic? sheet = stateStep == null ? null : activeCell!.Worksheet;

            ShowLambdaPopupCommand.InvokeOnWindowThread(dispatcher =>
            {
                LambdaGenerationRequest? result = null;
                FoldWrite? foldWrite = null;

                dispatcher.Invoke(() =>
                {
                    var window = new LetToLambdaWindow(
                        parsed,
                        name => existingNames.TryGetValue(name, out string? refersTo) ? refersTo : null,
                        stateStep);

                    var wpfHwnd = new WindowInteropHelper(window).EnsureHandle();
                    WindowPositioner.CenterOnExcel(excelHwnd, wpfHwnd);

                    if (window.ShowDialog() == true)
                    {
                        result = window.Result;
                        foldWrite = window.FoldToWrite;
                    }
                });

                if (result == null) return;

                try
                {
                    var generatedFormula = LetToLambdaBuilder.Build(result);
                    LambdaLoader.InjectLambda(result.LambdaName, generatedFormula);
                    Logger.Info($"ConvertLetToLambda: Created '{result.LambdaName}'");
                    if (foldWrite != null) WriteFold(sheet, foldWrite);
                }
                catch (Exception ex)
                {
                    Logger.Error("ConvertLetToLambda/Build", ex);
                    ShowError($"Failed to generate LAMBDA: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("ConvertLetToLambda", ex);
            ShowError($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Writes the fold after the LAMBDA is registered. The LAMBDA stands either way, so a
    ///     fold that can't be written is reported, not treated as a failed conversion, and a
    ///     cell that already holds something is never overwritten.
    /// </summary>
    private static void WriteFold(dynamic sheet, FoldWrite fold)
    {
        try
        {
            dynamic target = sheet.Range[fold.Target];
            if (!string.IsNullOrEmpty(Convert.ToString(target.Formula2)))
            {
                ShowError($"The LAMBDA is registered, but {fold.Target} isn't empty, so the fold wasn't written.");
                return;
            }

            target.Formula2 = fold.Formula;
            Logger.Info($"ConvertLetToLambda: Fold written to {fold.Target}");
        }
        catch (Exception ex)
        {
            Logger.Error("ConvertLetToLambda/WriteFold", ex);
            ShowError($"The LAMBDA is registered, but the fold wasn't written: {ex.Message}");
        }
    }

    internal static Dictionary<string, string> GetExistingNames(dynamic workbook)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (dynamic n in workbook.Names)
            {
                try
                {
                    var name = (string)n.Name;
                    var refersTo = (string?)n.RefersTo ?? string.Empty;
                    names[name] = refersTo;
                }
                catch { /* skip unreadable */ }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("GetExistingNames", ex);
        }
        return names;
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
