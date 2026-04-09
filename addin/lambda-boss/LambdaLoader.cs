using ExcelDna.Integration;

using Taglo.Excel.Common;

namespace LambdaBoss;

/// <summary>
///     Injects LAMBDA definitions into the active workbook's Name Manager via COM interop.
/// </summary>
public static class LambdaLoader
{
    /// <summary>
    ///     Adds or updates a named LAMBDA formula in the active workbook's Name Manager.
    /// </summary>
    /// <param name="name">The name to register (e.g. "DOUBLE").</param>
    /// <param name="formula">The LAMBDA formula including the = prefix (e.g. "=LAMBDA(x, x*2)").</param>
    public static void InjectLambda(string name, string formula)
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            dynamic workbook = app.ActiveWorkbook;

            if (workbook == null)
            {
                Logger.Info("InjectLambda: No active workbook");
                return;
            }

            // Try to update existing name first, fall back to adding new
            try
            {
                dynamic existing = workbook.Names.Item(name);
                existing.RefersTo = formula;
                Logger.Info($"InjectLambda: Updated '{name}'");
            }
            catch
            {
                // Name doesn't exist — add it
                workbook.Names.Add(name, formula);
                Logger.Info($"InjectLambda: Added '{name}'");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("InjectLambda", ex);
            throw;
        }
    }

    /// <summary>
    ///     Returns the hardcoded tracer-bullet LAMBDAs for the popup.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Formula)> GetTracerBulletLambdas()
    {
        return new[]
        {
            ("DOUBLE", "=LAMBDA(x, x*2)"),
            ("TRIPLE", "=LAMBDA(x, x*3)"),
            ("ADDN", "=LAMBDA(x, n, x+n)")
        };
    }
}
