using System.Windows;
using System.Windows.Interop;

using ExcelDna.Integration;

using LambdaBoss.UI;

using Taglo.Excel.Common;

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
            string? formula = activeCell?.Formula as string;

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

            ShowLambdaPopupCommand.InvokeOnWindowThread(dispatcher =>
            {
                LambdaGenerationRequest? result = null;

                dispatcher.Invoke(() =>
                {
                    var window = new LetToLambdaWindow(
                        parsed,
                        name => existingNames.Contains(name));

                    var wpfHwnd = new WindowInteropHelper(window).EnsureHandle();
                    WindowPositioner.CenterOnExcel(excelHwnd, wpfHwnd);

                    if (window.ShowDialog() == true)
                        result = window.Result;
                });

                if (result == null) return;

                try
                {
                    var generatedFormula = LetToLambdaBuilder.Build(result);
                    LambdaLoader.InjectLambda(result.LambdaName, generatedFormula);
                    Logger.Info($"ConvertLetToLambda: Created '{result.LambdaName}'");
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

    private static HashSet<string> GetExistingNames(dynamic workbook)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (dynamic n in workbook.Names)
            {
                try { names.Add((string)n.Name); }
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
