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
    private static LibraryProvider? _provider;
    private static bool _dataLoaded;

    /// <summary>
    ///     The default repo used until settings persistence is implemented (#6).
    /// </summary>
    private static readonly RepoConfig DefaultRepo = new()
    {
        Url = "https://github.com/TagloGit/lambda-boss"
    };

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
        _provider = null;
        _dataLoaded = false;
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

                    if (!_dataLoaded)
                    {
                        LoadDataAsync();
                    }
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
            _window.LibraryLoadRequested += OnLibraryLoadRequested;
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

    private static async void LoadDataAsync()
    {
        try
        {
            _windowDispatcher?.Invoke(() => _window?.SetStatus("Loading libraries..."));

            _provider ??= new LibraryProvider(new[] { DefaultRepo });

            var libraries = await _provider.GetLibrariesAsync();
            var lambdas = await _provider.GetAllLambdasAsync();

            _dataLoaded = true;

            _windowDispatcher?.Invoke(() =>
            {
                _window?.SetData(libraries, lambdas);
                _window?.SetStatus("↑↓ navigate · Enter load · Tab switch · Esc close");
                // Re-apply the view so data shows up
                _window?.ResetAndShow();
            });
        }
        catch (Exception ex)
        {
            Logger.Error("LoadDataAsync", ex);
            _windowDispatcher?.Invoke(() =>
                _window?.SetStatus("Failed to load libraries — check network"));
        }
    }

    private static void OnLibraryLoadRequested(object? sender, LibraryLoadRequest request)
    {
        // Fetch and inject on a background thread, then inject via QueueAsMacro
        Task.Run(async () =>
        {
            try
            {
                _windowDispatcher?.Invoke(() =>
                    _window?.SetStatus($"Loading {request.DisplayName}..."));

                _provider ??= new LibraryProvider(new[] { DefaultRepo });

                var lambdas = await _provider.LoadLibraryAsync(
                    request.RepoConfig, request.LibraryName, request.Prefix);

                ExcelAsyncUtil.QueueAsMacro(() =>
                {
                    try
                    {
                        var count = 0;
                        foreach (var (name, formula) in lambdas)
                        {
                            LambdaLoader.InjectLambda(name, formula);
                            count++;
                        }

                        Logger.Info($"Loaded {count} lambdas from '{request.DisplayName}' with prefix '{request.Prefix}'");

                        _windowDispatcher?.Invoke(() =>
                            _window?.SetStatus($"Loaded {count} lambdas from {request.DisplayName}"));
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("OnLibraryLoadRequested/Inject", ex);
                        _windowDispatcher?.Invoke(() =>
                            _window?.SetStatus($"Error loading {request.DisplayName}"));
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error("OnLibraryLoadRequested/Fetch", ex);
                _windowDispatcher?.Invoke(() =>
                    _window?.SetStatus($"Error fetching {request.DisplayName}"));
            }
        });
    }
}
