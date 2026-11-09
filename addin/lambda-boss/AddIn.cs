using System.Net;

using ExcelDna.Integration;

using LambdaBoss.Commands;

using LambdaBoss.Common;

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
                XlCall.Excel(XlCall.xlcOnKey, Settings.Current.KeyboardShortcut);
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        CheckFormulaCommand.Stop(excelShuttingDown: _isExcelShutdown);
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

        // Excel is a native host and the add-in ships no .xll.config, so the add-in AppDomain
        // gets .NET Framework 4.0-era quirks and HttpClient offers TLS 1.0 only. GitHub requires
        // TLS 1.2+, so every fetch failed with "Could not create SSL/TLS secure channel" (#372).
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

        // The same 4.0-era quirks leave WPF's DoNotScaleForDpiChanges on, so WPF ignores the
        // per-monitor DPI the window thread asks for and lays windows out at the system DPI
        // the Excel process started with. After a Remote Desktop session (or on a monitor
        // with other scaling) dialogs opened tiny until Excel restarted (#379). Must run
        // before any WPF type loads: WPF reads the switch once.
        AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);

        try
        {
            ExcelComAddInHelper.LoadComAddIn(new ShutdownMonitor());

            ExcelAsyncUtil.QueueAsMacro(DeferredInit);

            // Argument hints (Labs, spec 0013). AutoOpen is a macro context, which Install needs.
            LambdaHints.StartIfEnabled();
            ShowLambdaPopupCommand.SettingsSaved += LambdaHints.ApplySwitch;

            // Check Formula (Labs, spec 0014): shortcut, edit-mode hook, name snapshot.
            CheckFormulaCommand.StartIfEnabled();
            ShowLambdaPopupCommand.SettingsSaved += CheckFormulaCommand.ApplySwitch;

            var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                                  ?? new Version(0, 0, 0);
            UpdateChecker.Initialize(
                "https://api.github.com/repos/TagloGit/lambda-boss/releases/latest",
                $"LambdaBoss/{assemblyVersion}");
            if (Settings.Current.LibraryChannel == LibraryChannel.Labs)
                UpdateChecker.CheckForDevBuildAsync(
                    "https://api.github.com/repos/TagloGit/lambda-boss/releases/tags/dev-latest",
                    BuildInfo.Commit);
            else
                UpdateChecker.CheckForUpdateAsync(assemblyVersion);

            Logger.Info("Lambda Boss add-in loaded successfully");
        }
        catch (Exception ex)
        {
            Logger.Error("AddIn.AutoOpen", ex);
        }
    }

    public void AutoClose()
    {
        // The IntelliSense server must be uninstalled before the add-in unloads, or Excel
        // crashes. Its file stays for the next session.
        ShowLambdaPopupCommand.SettingsSaved -= LambdaHints.ApplySwitch;
        LambdaHints.Stop(removeFile: false);
        ShowLambdaPopupCommand.SettingsSaved -= CheckFormulaCommand.ApplySwitch;
        CheckFormulaCommand.Stop();
        Dispose();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("Unobserved task", e.Exception);
        e.SetObserved();
    }

    private void DeferredInit()
    {
        try
        {
            // Register keyboard shortcut from settings (default: Ctrl+Shift+L)
            XlCall.Excel(XlCall.xlcOnKey, Settings.Current.KeyboardShortcut, "ShowLambdaPopup");

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
