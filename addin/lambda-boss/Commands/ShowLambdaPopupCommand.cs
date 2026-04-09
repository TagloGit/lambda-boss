using System.Windows.Interop;
using System.Windows.Threading;

using ExcelDna.Integration;

using LambdaBoss.UI;

using Taglo.Excel.Common;

namespace LambdaBoss.Commands;

/// <summary>
///     Command handler for showing the Lambda popup.
///     Triggered by Ctrl+Shift+L keyboard shortcut.
/// </summary>
public static class ShowLambdaPopupCommand
{
    private static LambdaPopup? _window;
    private static Dispatcher? _windowDispatcher;
    private static Thread? _windowThread;
    private static bool _hasBeenPositioned;

    public static void Cleanup()
    {
        _windowDispatcher?.InvokeShutdown();

        if (_windowThread is { IsAlive: true })
        {
            _windowThread.Join(TimeSpan.FromSeconds(2));
        }

        _windowDispatcher = null;
        _windowThread = null;
        _window = null;
        _hasBeenPositioned = false;
    }

    [ExcelCommand]
    public static void ShowLambdaPopup()
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            var excelHwnd = new IntPtr(app.Hwnd);

            EnsureWindowThread();

            _windowDispatcher?.Invoke(() =>
            {
                if (_window == null)
                {
                    return;
                }

                if (_window.IsVisible)
                {
                    _window.Hide();
                }
                else
                {
                    if (!_hasBeenPositioned)
                    {
                        var wpfHwnd = new WindowInteropHelper(_window).EnsureHandle();
                        WindowPositioner.CenterOnExcel(excelHwnd, wpfHwnd);
                        _hasBeenPositioned = true;
                    }

                    _window.ResetAndShow();
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("ShowLambdaPopup", ex);
        }
    }

    private static void EnsureWindowThread()
    {
        if (_windowThread != null && _windowThread.IsAlive)
        {
            return;
        }

        var readyEvent = new ManualResetEventSlim(false);

        _windowThread = new Thread(() =>
        {
            NativeMethods.SetThreadDpiAwarenessContext(
                NativeMethods.DpiAwarenessContextPerMonitorAwareV2);

            _window = new LambdaPopup();
            _window.LambdaSelected += OnLambdaSelected;
            _windowDispatcher = Dispatcher.CurrentDispatcher;

            _windowDispatcher.UnhandledException += (_, e) =>
            {
                Logger.Error("WPF dispatcher", e.Exception);
                e.Handled = true;
            };

            readyEvent.Set();

            Dispatcher.Run();
        });

        _windowThread.SetApartmentState(ApartmentState.STA);
        _windowThread.IsBackground = true;
        _windowThread.Start();

        readyEvent.Wait();
    }

    private static void OnLambdaSelected(object? sender, (string Name, string Formula) lambda)
    {
        ExcelAsyncUtil.QueueAsMacro(() =>
        {
            try
            {
                LambdaLoader.InjectLambda(lambda.Name, lambda.Formula);
            }
            catch (Exception ex)
            {
                Logger.Error("OnLambdaSelected", ex);
            }
        });
    }
}
