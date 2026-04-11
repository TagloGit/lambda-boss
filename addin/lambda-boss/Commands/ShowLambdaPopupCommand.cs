using System.Windows.Interop;
using System.Windows.Threading;

using ExcelDna.Integration;

using LambdaBoss.UI;

using Taglo.Excel.Common;

namespace LambdaBoss.Commands;

/// <summary>
///     Command handler for showing the Lambda popup and settings window.
///     Triggered by keyboard shortcut or ribbon buttons.
/// </summary>
public static class ShowLambdaPopupCommand
{
    private static LambdaPopup? _window;
    private static SettingsWindow? _settingsWindow;
    private static Dispatcher? _windowDispatcher;
    private static Thread? _windowThread;
    private static bool _hasBeenPositioned;
    private static LibraryProvider? _provider;
    private static bool _dataLoaded;

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
        _settingsWindow = null;
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

            // Scan Name Manager on the Excel main thread (COM-safe)
            var loadedKeys = ScanLoadedLibraryKeys();

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

                    // Always refresh loaded state from Name Manager
                    _window.UpdateLoadedKeys(loadedKeys);
                    _window.ResetAndShow();

                    if (!_dataLoaded)
                    {
                        LoadDataAsync(loadedKeys);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("ShowLambdaPopup", ex);
        }
    }

    /// <summary>
    ///     Opens the settings window. Called from the ribbon Settings button.
    /// </summary>
    public static void ShowSettings()
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            var excelHwnd = new IntPtr(app.Hwnd);

            EnsureWindowThread();

            _windowDispatcher?.Invoke(() =>
            {
                if (_settingsWindow == null)
                    return;

                if (_settingsWindow.IsVisible)
                {
                    _settingsWindow.Activate();
                }
                else
                {
                    var wpfHwnd = new WindowInteropHelper(_settingsWindow).EnsureHandle();
                    WindowPositioner.CenterOnExcel(excelHwnd, wpfHwnd);
                    _settingsWindow.Show();
                    _settingsWindow.Activate();
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("ShowSettings", ex);
        }
    }

    /// <summary>
    ///     Clears cached data and re-fetches from all repos. Called from the ribbon Refresh button.
    /// </summary>
    public static void RefreshData()
    {
        _provider = null;
        _dataLoaded = false;

        try
        {
            EnsureWindowThread();

            _windowDispatcher?.Invoke(() =>
            {
                _window?.SetStatus("Refreshing libraries...");
            });

            LoadDataAsync(null);
        }
        catch (Exception ex)
        {
            Logger.Error("RefreshData", ex);
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

            _settingsWindow = new SettingsWindow();
            _settingsWindow.SettingsChanged += OnSettingsChanged;

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

    private static async void LoadDataAsync(HashSet<string>? loadedKeys)
    {
        try
        {
            _windowDispatcher?.Invoke(() => _window?.SetStatus("Loading libraries..."));

            _provider ??= new LibraryProvider(Settings.Current.EnabledRepos);

            var libraries = await _provider.GetLibrariesAsync();
            var lambdas = await _provider.GetAllLambdasAsync();

            _dataLoaded = true;

            _windowDispatcher?.Invoke(() =>
            {
                _window?.SetData(libraries, lambdas, loadedKeys);
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

    private static void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Invalidate cached data so next popup open re-fetches with updated repos
        _provider = null;
        _dataLoaded = false;
    }

    /// <summary>
    ///     Scans Name Manager comments on the Excel main thread.
    /// </summary>
    private static HashSet<string> ScanLoadedLibraryKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var scanned = LambdaLoader.ScanLoadedLibraries();
            foreach (var lib in scanned)
            {
                keys.Add(LambdaPopup.MakeLoadedKey(lib.RepoUrl, lib.LibraryName));
            }
        }
        catch (Exception ex)
        {
            Logger.Error("ScanLoadedLibraryKeys", ex);
        }

        return keys;
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

                _provider ??= new LibraryProvider(Settings.Current.EnabledRepos);

                // Invalidate cache so we always get fresh data
                _provider.InvalidateCache(request.RepoConfig, request.LibraryName);

                var lambdas = await _provider.LoadLibraryAsync(
                    request.RepoConfig, request.LibraryName, request.Prefix);

                ExcelAsyncUtil.QueueAsMacro(() =>
                {
                    try
                    {
                        // Scan existing names before injecting (for diff summary)
                        ScannedLibrary? existing = null;
                        try
                        {
                            var allScanned = LambdaLoader.ScanLoadedLibraries();
                            existing = allScanned.FirstOrDefault(s =>
                                string.Equals(s.LibraryName, request.LibraryName, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(
                                    s.RepoUrl.TrimEnd('/'),
                                    request.RepoConfig.Url.TrimEnd('/'),
                                    StringComparison.OrdinalIgnoreCase));
                        }
                        catch
                        {
                            // If scan fails, proceed without diff
                        }

                        var comment = LambdaLoader.BuildComment(
                            request.RepoConfig.Url, request.LibraryName, request.Prefix);

                        var added = 0;
                        var updated = 0;
                        var unchanged = 0;

                        foreach (var (name, formula) in lambdas)
                        {
                            // Classify change
                            if (existing != null && existing.Lambdas.TryGetValue(name, out var oldFormula))
                            {
                                if (string.Equals(formula, oldFormula, StringComparison.Ordinal))
                                    unchanged++;
                                else
                                    updated++;
                            }
                            else
                            {
                                added++;
                            }

                            LambdaLoader.InjectLambda(name, formula, comment);
                        }

                        Logger.Info($"Loaded {lambdas.Count} lambdas from '{request.DisplayName}' with prefix '{request.Prefix}'");

                        // Build summary
                        var parts = new List<string>();
                        if (added > 0) parts.Add($"{added} new");
                        if (updated > 0) parts.Add($"{updated} updated");
                        if (unchanged > 0) parts.Add($"{unchanged} unchanged");
                        var summary = parts.Count > 0 ? string.Join(", ", parts) : $"{lambdas.Count} loaded";

                        _windowDispatcher?.Invoke(() =>
                            _window?.SetStatus($"{request.DisplayName}: {summary}"));
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
