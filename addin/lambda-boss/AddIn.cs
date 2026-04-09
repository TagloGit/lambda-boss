using ExcelDna.Integration;

using LambdaBoss.Commands;

using Taglo.Excel.Common;

namespace LambdaBoss;

public sealed class AddIn : IExcelAddIn, IDisposable
{
    private static AddIn? _instance;

    private bool _disposed;
    private bool _isExcelShutdown;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Logger.Info("Lambda Boss add-in shutting down");

        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        if (!_isExcelShutdown)
        {
            try
            {
                XlCall.Excel(XlCall.xlcOnKey, "^+L");
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        ShowLambdaPopupCommand.Cleanup();

        _disposed = true;

        Logger.Info("Lambda Boss add-in shutdown complete");
    }

    public void AutoOpen()
    {
        _instance = this;

        Logger.Initialize("LambdaBoss");
        Logger.Info("Lambda Boss add-in loading");

        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            ExcelComAddInHelper.LoadComAddIn(new ShutdownMonitor());

            ExcelAsyncUtil.QueueAsMacro(DeferredInit);

            var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                                  ?? new Version(0, 0, 0);
            UpdateChecker.Initialize(
                "https://api.github.com/repos/TagloGit/lambda-boss/releases/latest",
                $"LambdaBoss/{assemblyVersion}");
            UpdateChecker.CheckForUpdateAsync(assemblyVersion);

            Logger.Info("Lambda Boss add-in loaded successfully");
        }
        catch (Exception ex)
        {
            Logger.Error("AddIn.AutoOpen", ex);
        }
    }

    public void AutoClose() => Dispose();

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("Unobserved task", e.Exception);
        e.SetObserved();
    }

    private void DeferredInit()
    {
        try
        {
            // Register keyboard shortcut: Ctrl+Shift+L
            XlCall.Excel(XlCall.xlcOnKey, "^+L", "ShowLambdaPopup");

            Logger.Info("Lambda Boss keyboard shortcut registered");
        }
        catch (Exception ex)
        {
            Logger.Error("DeferredInit", ex);
        }
    }

    private class ShutdownMonitor : ExcelComAddIn
    {
        public override void OnBeginShutdown(ref Array custom)
        {
            if (_instance != null)
            {
                _instance._isExcelShutdown = true;
                _instance.Dispose();
            }
        }
    }
}
