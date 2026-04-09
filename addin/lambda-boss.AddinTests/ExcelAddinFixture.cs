using System.Diagnostics;
using System.Runtime.InteropServices;

using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LambdaBoss.AddinTests;

/// <summary>
///     Manages the lifecycle of an Excel instance with the Lambda Boss add-in loaded.
///     Launches a hidden Excel, registers the XLL, and cleans up on dispose.
/// </summary>
public sealed class ExcelAddinFixture : IDisposable
{
    private readonly int _excelPid;
    private bool _disposed;

    public ExcelAddinFixture()
    {
        var excelType = Type.GetTypeFromProgID("Excel.Application")
                        ?? throw new InvalidOperationException("Excel is not installed or not registered.");

        var pidsBefore = new HashSet<int>(
            Process.GetProcessesByName("EXCEL").Select(p => p.Id));

        Application = Activator.CreateInstance(excelType)
                      ?? throw new InvalidOperationException("Failed to create Excel.Application instance.");

        Application.Visible = false;
        Application.DisplayAlerts = false;

        _excelPid = FindNewExcelPid(pidsBefore);

        var xllPath = FindXllPath();
        bool registered = Application.RegisterXLL(xllPath);
        if (!registered)
        {
            Application.Quit();
            Marshal.ReleaseComObject(Application);
            throw new InvalidOperationException($"Failed to register XLL: {xllPath}");
        }

        Workbook = Application.Workbooks.Add();

        // Brief pause for AutoOpen to complete
        Thread.Sleep(2000);
    }

    public dynamic Application { get; }
    public dynamic Workbook { get; }
    public dynamic Worksheet => Workbook.Worksheets[1];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Workbook.Close(false);
            Marshal.ReleaseComObject(Workbook);
        }
        catch
        {
            // Ignore cleanup errors
        }

        try
        {
            Application.Quit();
            Marshal.ReleaseComObject(Application);
        }
        catch
        {
            // Ignore cleanup errors
        }

        KillSpawnedExcel();
    }

    public dynamic AddWorksheet() => Workbook.Worksheets.Add();

    private static string FindXllPath()
    {
        var testDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", ".."));

        foreach (var config in new[] { "Debug", "Release" })
        {
            var xllPath = Path.Combine(repoRoot, "lambda-boss", "bin", config,
                "net6.0-windows", "lambda-boss64.xll");
            if (File.Exists(xllPath))
            {
                return xllPath;
            }
        }

        throw new FileNotFoundException(
            $"Could not find lambda-boss64.xll. Build the lambda-boss project first. Searched from: {repoRoot}");
    }

    private static int FindNewExcelPid(HashSet<int> pidsBefore, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var proc in Process.GetProcessesByName("EXCEL"))
            {
                if (!pidsBefore.Contains(proc.Id))
                {
                    return proc.Id;
                }
            }

            Thread.Sleep(100);
        }

        return -1;
    }

    private void KillSpawnedExcel()
    {
        if (_excelPid <= 0)
        {
            return;
        }

        try
        {
            var proc = Process.GetProcessById(_excelPid);
            if (proc is { HasExited: false, ProcessName: "EXCEL" })
            {
                proc.Kill();
            }
        }
        catch
        {
            // Process already exited or access denied
        }
    }
}

[CollectionDefinition("Excel Addin")]
public class ExcelAddinCollection : ICollectionFixture<ExcelAddinFixture>
{
}
