using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using ExcelDna.Integration;

using Excel = Microsoft.Office.Interop.Excel;

using LambdaBoss.Commands;
using LambdaBoss.Common;

namespace LambdaBoss.UI;

/// <summary>
///     Shows the example check's findings (spec 0020) in one of three modes, none of which writes to
///     the workbook:
///     <list type="bullet">
///         <item><c>status</c>: Excel's status bar, plus a drawn outline (an overlay window, not cell
///         formatting) around the filled cells and the matching cell for a few seconds;</item>
///         <item><c>toast</c> (the default): a small panel beside the filled cells that doesn't take
///         focus, closing itself after 20 s or on ×;</item>
///         <item><c>prompt</c>: a window over Excel that takes focus until Enter or Esc.</item>
///     </list>
/// </summary>
internal static class ExampleCheckWindow
{
    private static readonly Brush Amber = Frozen(Color.FromRgb(0xE6, 0x8A, 0x00));
    private static readonly Brush Blue = Frozen(Color.FromRgb(0x1F, 0x6F, 0xEB));
    private static readonly Brush Bad = Frozen(Color.FromRgb(0xB0, 0x1E, 0x1E));
    private static readonly Brush Panel = Frozen(Color.FromRgb(0xFF, 0xF8, 0xE5));
    private static readonly FontFamily Mono = new("Consolas");

    private static readonly List<Window> Open = new();
    private static string? _statusText;

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Main thread (the SheetChange handler).</summary>
    public static void Present(CheckedWrite where, IReadOnlyList<ExampleCheckFinding> findings, string mode)
    {
        var app = (Excel.Application)ExcelDnaUtil.Application;
        var written = ScreenRect(app, where.Row, where.Column, where.Rows, where.Columns);
        var first = findings[0];
        var source = first.Finding.FromText
            ? (NativeMethods.Rect?)null
            : ScreenRect(app, first.Finding.SourceRow, first.Finding.SourceColumn, 1, 1);
        var shortcut = CheckFormulaCommand.ShortcutLabel;

        if (mode == ExampleCheckModes.Status)
        {
            var text = $"Lambda Boss: {Headline(findings)} — {Detail(first)} ({shortcut} on the cell for more)";
            _statusText = text;
            try
            {
                app.StatusBar = text;
            }
            catch (Exception ex)
            {
                Logger.Error("ExampleCheck/StatusBar", ex);
            }

            // Hand the status bar back to Excel after a while, unless something else changed it.
            var timer = new Timer(_ => ExcelAsyncUtil.QueueAsMacro(() =>
            {
                try
                {
                    var a = (Excel.Application)ExcelDnaUtil.Application;
                    if (a.StatusBar is string s && s == _statusText)
                        a.StatusBar = false;
                }
                catch
                {
                    // Excel busy or closing: leave it.
                }
            }), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
            GC.KeepAlive(timer);
            ShowLambdaPopupCommand.InvokeOnWindowThread(d => d.BeginInvoke(new Action(() =>
            {
                CloseAllOnWindowThread();
                if (written is { } w)
                    Outline(w, Amber, TimeSpan.FromSeconds(8));
                if (source is { } s)
                    Outline(s, Blue, TimeSpan.FromSeconds(8));
            })));
            return;
        }

        ShowLambdaPopupCommand.InvokeOnWindowThread(d => d.BeginInvoke(new Action(() =>
        {
            try
            {
                CloseAllOnWindowThread();
                if (mode == ExampleCheckModes.Prompt)
                    ShowPrompt(where, findings, shortcut);
                else
                    ShowToast(where, findings, shortcut, written);
            }
            catch (Exception ex)
            {
                Logger.Error("ExampleCheckWindow", ex);
            }
        })));
    }

    public static void CloseAll() =>
        ShowLambdaPopupCommand.InvokeOnWindowThread(d => d.BeginInvoke(new Action(CloseAllOnWindowThread)));

    private static void CloseAllOnWindowThread()
    {
        foreach (var w in Open.ToList())
            w.Close();
        Open.Clear();
    }

    // ------------------------------------------------------------ text

    private static string Headline(IReadOnlyList<ExampleCheckFinding> findings) =>
        findings.Count == 1
            ? "a value from the example is left in the formula"
            : $"{findings.Count} values from the example are left in the formula";

    private static string Detail(ExampleCheckFinding f) =>
        f.Finding.FromText
            ? $"{f.Finding.Literal} is in this level's example text"
            : $"{f.Finding.Literal} is {f.Finding.SourceAddress}'s value on the example row";

    private static StackPanel Body(CheckedWrite where, IReadOnlyList<ExampleCheckFinding> findings, string shortcut, bool compact)
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = char.ToUpperInvariant(Headline(findings)[0]) + Headline(findings)[1..],
            FontWeight = FontWeights.SemiBold,
            FontSize = compact ? 13 : 15,
            Foreground = Bad,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(new TextBlock
        {
            Text = $"{where.Sheet} · {where.Label}",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 1, 0, 6)
        });
        foreach (var f in findings.Take(compact ? 3 : 8))
        {
            var formula = f.Request.Formula;
            var from = Math.Max(0, f.Finding.Start - 25);
            var to = Math.Min(formula.Length, f.Finding.Start + f.Finding.Length + 25);
            var excerpt = new TextBlock { FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            if (from > 0)
                excerpt.Inlines.Add(new System.Windows.Documents.Run("…") { Foreground = Brushes.Gray });
            excerpt.Inlines.Add(new System.Windows.Documents.Run(Flat(formula[from..f.Finding.Start])));
            excerpt.Inlines.Add(new System.Windows.Documents.Run(formula.Substring(f.Finding.Start, f.Finding.Length))
            {
                Background = Frozen(Color.FromRgb(0xFF, 0xC9, 0xC9)),
                FontWeight = FontWeights.Bold
            });
            excerpt.Inlines.Add(new System.Windows.Documents.Run(Flat(formula[(f.Finding.Start + f.Finding.Length)..to])));
            if (to < formula.Length)
                excerpt.Inlines.Add(new System.Windows.Documents.Run("…") { Foreground = Brushes.Gray });
            root.Children.Add(excerpt);
            root.Children.Add(new TextBlock
            {
                Text = f.Finding.Message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 8)
            });
        }

        root.Children.Add(new TextBlock
        {
            Text = $"{shortcut} on a filled cell shows it in Check Formula. Mode: Lambda Boss settings.",
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        return root;
    }

    private static string Flat(string s) => s.Replace("\r", "").Replace("\n", " ");

    // ------------------------------------------------------------ toast

    private static void ShowToast(CheckedWrite where, IReadOnlyList<ExampleCheckFinding> findings, string shortcut,
        NativeMethods.Rect? near)
    {
        var close = new Button
        {
            Content = "×",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        var grid = new Grid();
        grid.Children.Add(Body(where, findings, shortcut, compact: true));
        grid.Children.Add(close);
        var w = new Window
        {
            Title = "Example check",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            Width = 440,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = Panel,
            BorderBrush = Amber,
            BorderThickness = new Thickness(2),
            Content = new Border { Padding = new Thickness(10, 8, 10, 8), Child = grid }
        };
        close.Click += (_, _) => w.Close();
        w.Closed += (_, _) => Open.Remove(w);
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
        _ = NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlExStyle,
            ex | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow);
        Open.Add(w);
        // Placed before it shows (WPF moves a window placed after), and again once it has its height.
        Place(hwnd, where.ExcelHwnd, near);
        w.ContentRendered += (_, _) => Place(hwnd, where.ExcelHwnd, near);
        w.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            w.Close();
        };
        timer.Start();
    }

    /// <summary>Just right of the written cells' top, or Excel's bottom-right corner; on screen.</summary>
    private static void Place(IntPtr hwnd, IntPtr excelHwnd, NativeMethods.Rect? near)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var me) || !NativeMethods.GetWindowRect(excelHwnd, out var xl))
            return;
        int left, top;
        if (near is { } n)
        {
            left = n.Right + 12;
            top = n.Top;
            if (left + me.Width > xl.Right)
                left = Math.Max(xl.Left, n.Left - me.Width - 12);
        }
        else
        {
            left = xl.Right - me.Width - 40;
            top = xl.Bottom - me.Height - 60;
        }

        var monitor = NativeMethods.MonitorFromWindow(excelHwnd, NativeMethods.MonitorDefaultToNearest);
        var info = NativeMethods.Monitorinfo.Create();
        NativeMethods.GetMonitorInfo(monitor, ref info);
        left = Math.Max(info.rcWork.Left, Math.Min(left, info.rcWork.Right - me.Width));
        top = Math.Max(info.rcWork.Top, Math.Min(top, info.rcWork.Bottom - me.Height));
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    // ------------------------------------------------------------ prompt

    private static void ShowPrompt(CheckedWrite where, IReadOnlyList<ExampleCheckFinding> findings, string shortcut)
    {
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 8, 0, 0), IsDefault = true, IsCancel = true };
        var root = Body(where, findings, shortcut, compact: false);
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        root.Children.Add(ok);
        var w = new Window
        {
            Title = "Lambda Boss — example check",
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            Width = 560,
            Topmost = true,
            ShowInTaskbar = false,
            Background = Brushes.White,
            Content = new Border { Padding = new Thickness(14), Child = root }
        };
        ok.Click += (_, _) => w.Close();
        w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter)
            {
                e.Handled = true;
                w.Close();
            }
        };
        w.Closed += (_, _) =>
        {
            Open.Remove(w);
            SetForegroundWindow(where.ExcelHwnd);
        };
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        WindowPositioner.CenterOnExcel(where.ExcelHwnd, hwnd);
        Open.Add(w);
        w.Show();
        w.Activate();
        ok.Focus();
    }

    // ------------------------------------------------------------ outline (status mode)

    private static void Outline(NativeMethods.Rect r, Brush brush, TimeSpan life)
    {
        const int pad = 2;
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = new Border { BorderBrush = brush, BorderThickness = new Thickness(3) }
        };
        w.Closed += (_, _) => Open.Remove(w);
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
        // Click-through and never focused: it's paint only.
        _ = NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlExStyle,
            ex | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow | WsExTransparent);
        Open.Add(w);
        w.Show();
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, r.Left - pad, r.Top - pad, r.Width + 2 * pad, r.Height + 2 * pad,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        var timer = new DispatcherTimer { Interval = life };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            w.Close();
        };
        timer.Start();
    }

    private const int WsExTransparent = 0x00000020;

    /// <summary>A range's rectangle on screen, in pixels, or null when Excel can't say. Main thread.</summary>
    private static NativeMethods.Rect? ScreenRect(Excel.Application app, int row, int column, int rows, int columns)
    {
        try
        {
            var win = app.ActiveWindow;
            var pane = win.ActivePane;
            if (app.ActiveSheet is not Excel.Worksheet ws)
                return null;
            var rng = Com.Block(ws, row, column, row + rows - 1, column + columns - 1);
            static double D(object o) => Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture);
            // The pane maps a cell's position in points (Range.Left/Top) to screen pixels, scroll
            // and zoom included.
            var x = new Func<double, int>(pts => pane.PointsToScreenPixelsX((int)Math.Round(pts)));
            var y = new Func<double, int>(pts => pane.PointsToScreenPixelsY((int)Math.Round(pts)));
            double l = D(rng.Left), t = D(rng.Top), w = D(rng.Width), h = D(rng.Height);
            return new NativeMethods.Rect { Left = x(l), Top = y(t), Right = x(l + w), Bottom = y(t + h) };
        }
        catch (Exception ex)
        {
            Logger.Error("ExampleCheck/ScreenRect", ex);
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
