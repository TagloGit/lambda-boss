using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using ExcelDna.Integration;

using LambdaBoss.Common;
using LambdaBoss.UI;

namespace LambdaBoss.Commands;

/// <summary>
///     Check Formula (spec 0014, issue #419, Labs switch <c>check-formula</c>). Ctrl+Shift+K or the
///     ribbon button checks the active cell's formula; Ctrl+Shift+K while a formula is being edited
///     (including after Excel refuses it) checks the text being edited. The edit-mode path is a
///     keyboard hook on Excel's main thread (<see cref="FormulaEditHook"/>), because Excel runs no
///     macros, ribbon or OnKey commands during cell edit. Nothing runs until the key is pressed,
///     apart from a light refresh of the workbook-name snapshot every few seconds (see
///     <see cref="WorkbookNames"/>).
/// </summary>
public static class CheckFormulaCommand
{
    internal const string OnKeyShortcut = "^+K";
    internal const string ShortcutLabel = "Ctrl+Shift+K";

    private static bool _started;
    private static Timer? _refreshTimer;

    internal static bool Enabled => Labs.IsOn(Labs.CheckFormula);

    /// <summary>The ribbon and OnKey entry point: checks the active cell.</summary>
    [ExcelCommand]
    public static void CheckFormula()
    {
        try
        {
            if (!Enabled)
            {
                CheckFormulaWindow.ShowMessage(
                    "Check Formula is a Labs feature. Turn it on in Lambda Boss settings (the ribbon's channel button).");
                return;
            }

            dynamic app = ExcelDnaUtil.Application;
            var excelHwnd = new IntPtr(app.Hwnd);
            var cell = app.ActiveCell;
            if (cell == null)
                return;

            string? formula = null;
            if ((bool)cell.HasFormula)
                formula = (string)cell.Formula2;
            else if (cell.Value2 is string s && s.StartsWith("=", StringComparison.Ordinal))
                formula = s; // a formula entered as text ('=...) to get past Excel's refusal

            var address = SafeAddress(cell);
            if (formula is null)
            {
                CheckFormulaWindow.Show(excelHwnd, null, $"{address} has no formula to check.", null);
                return;
            }

            var names = WorkbookNames.Capture(app);
            var result = FormulaChecker.Check(formula, names);
            result = ConfirmWithExcel(app, result, names);
            CheckFormulaWindow.Show(excelHwnd, result, address, null);
        }
        catch (Exception ex)
        {
            Logger.Error("CheckFormula", ex);
        }
    }

    /// <summary>Starts the hook, shortcut and snapshot timer if the switch is on. Macro context.</summary>
    internal static void StartIfEnabled()
    {
        if (!Enabled || _started)
            return;

        try
        {
            XlCall.Excel(XlCall.xlcOnKey, OnKeyShortcut, nameof(CheckFormula));
            FormulaEditHook.Install(OnEditedFormula);
            _refreshTimer = new Timer(_ => ExcelAsyncUtil.QueueAsMacro(WorkbookNames.RefreshIfChanged),
                null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
            _started = true;
            Logger.Info("CheckFormula: started");

            // Read the workbook's names once now, so a check made mid-edit has them.
            ExcelAsyncUtil.QueueAsMacro(WorkbookNames.RefreshIfChanged);

            // Library names for "not loaded" messages, the way the popup loads them.
            Task.Run(async () =>
            {
                try
                {
                    await ShowLambdaPopupCommand.CreateProvider().GetAllLambdasAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error("CheckFormula: library load", ex);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("CheckFormula.Start", ex);
        }
    }

    /// <summary>Stops everything <see cref="StartIfEnabled"/> started. Macro context.</summary>
    internal static void Stop(bool excelShuttingDown = false)
    {
        if (!_started)
            return;

        _refreshTimer?.Dispose();
        _refreshTimer = null;
        FormulaEditHook.Uninstall();
        if (!excelShuttingDown)
        {
            try
            {
                XlCall.Excel(XlCall.xlcOnKey, OnKeyShortcut);
            }
            catch (Exception ex)
            {
                Logger.Error("CheckFormula.Stop/OnKey", ex);
            }
        }

        _started = false;
        Logger.Info("CheckFormula: stopped");
    }

    /// <summary>Applies the switch after the settings window saves. Safe from any thread.</summary>
    internal static void ApplySwitch() =>
        ExcelAsyncUtil.QueueAsMacro(() =>
        {
            if (Enabled)
                StartIfEnabled();
            else
                Stop();
        });

    /// <summary>
    ///     Called by the hook on Excel's main thread, mid-edit. Must return fast and make no COM
    ///     calls (Excel refuses them while editing): it hands the text to the window thread.
    /// </summary>
    private static void OnEditedFormula(string text, IntPtr excelHwnd)
    {
        // A copy: the main thread keeps adding to the snapshot while the window thread reads it.
        var names = WorkbookNames.Last.Clone();

        // Off Excel's thread at once: starting the window thread the first time takes a moment.
        Task.Run(() => ShowLambdaPopupCommand.InvokeOnWindowThread(dispatcher =>
            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var result = FormulaChecker.Check(text, names);
                    CheckFormulaWindow.ShowOnWindowThread(excelHwnd, result, "Formula being edited", names.TakenUtc);
                }
                catch (Exception ex)
                {
                    Logger.Error("CheckFormula/edit", ex);
                }
            }))));
    }

    private static readonly Regex PlainName = new(@"^[A-Za-z_\\][A-Za-z0-9_.?\\]*$", RegexOptions.CultureInvariant);

    /// <summary>
    ///     Asks Excel about each unknown name before reporting it: an add-in function or a name
    ///     the snapshot missed evaluates to something other than #NAME?. Excel's answer is kept in
    ///     the snapshot for checks made mid-edit.
    /// </summary>
    private static FormulaCheckResult ConfirmWithExcel(dynamic app, FormulaCheckResult result, FormulaNameContext names)
    {
        const int xlErrName = -2146826259;
        var kept = new List<FormulaProblem>();
        foreach (var p in result.Problems)
        {
            if (p.Kind != FormulaProblemKind.UnknownName || p.RelatedStart >= 0)
            {
                kept.Add(p);
                continue;
            }

            var name = result.Formula.Substring(p.Start, p.Length);
            if (!PlainName.IsMatch(name))
            {
                kept.Add(p);
                continue;
            }

            try
            {
                object value = app.Evaluate(name);
                if (value is int code && code == xlErrName)
                {
                    kept.Add(p);
                }
                else
                {
                    names.OtherKnownNames.Add(name);
                    Logger.Info($"CheckFormula: Excel resolves {name}; not reported");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("CheckFormula/Evaluate", ex);
                kept.Add(p);
            }
        }

        return kept.Count == result.Problems.Count ? result : result.WithProblems(kept);
    }

    private static string SafeAddress(dynamic cell)
    {
        try
        {
            return $"{cell.Worksheet.Name}!{cell.Address(false, false)}";
        }
        catch
        {
            return "The active cell";
        }
    }
}

/// <summary>
///     The workbook names Check Formula resolves against. Captured on every check of the active
///     cell, and refreshed every few seconds when the workbook's shape changes, so a check made
///     mid-edit (when Excel takes no COM calls) has a recent copy.
/// </summary>
internal static class WorkbookNames
{
    private static FormulaNameContext _last = new() { NamesRead = false };
    private static string? _lastKey;

    public static FormulaNameContext Last
    {
        get
        {
            var ctx = _last;
            AddLibraryNames(ctx);
            return ctx;
        }
    }

    /// <summary>Macro context only.</summary>
    public static FormulaNameContext Capture(dynamic app)
    {
        var ctx = new FormulaNameContext { TakenUtc = DateTime.UtcNow };
        try
        {
            var wb = app.ActiveWorkbook;
            if (wb != null)
            {
                foreach (var n in wb.Names)
                {
                    try
                    {
                        string full = n.Name;
                        ctx.DefinedNames.Add(full);
                        var bang = full.LastIndexOf('!');
                        if (bang >= 0)
                            ctx.DefinedNames.Add(full[(bang + 1)..]);
                    }
                    catch
                    {
                        // A broken name: skip it.
                    }
                }

                foreach (var ws in wb.Worksheets)
                {
                    ctx.Sheets.Add((string)ws.Name);
                    foreach (var lo in ws.ListObjects)
                    {
                        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var lc in lo.ListColumns)
                            cols.Add((string)lc.Name);
                        ctx.Tables[(string)lo.Name] = cols;
                    }
                }

                _lastKey = KeyOf(wb);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("CheckFormula/Capture", ex);
        }

        // Keep what Excel confirmed earlier in this workbook.
        foreach (var k in _last.OtherKnownNames)
            ctx.OtherKnownNames.Add(k);
        AddLibraryNames(ctx);
        _last = ctx;
        return ctx;
    }

    /// <summary>Timer tick, queued as a macro: re-captures only when the workbook's shape changed.</summary>
    public static void RefreshIfChanged()
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            var wb = app.ActiveWorkbook;
            if (wb == null)
                return;
            string key = KeyOf(wb);
            var taken = _last.TakenUtc;
            if (key != _lastKey || taken is null || DateTime.UtcNow - taken.Value > TimeSpan.FromMinutes(1))
                Capture(app);
        }
        catch (Exception ex)
        {
            Logger.Error("CheckFormula/Refresh", ex);
        }
    }

    private static string KeyOf(dynamic wb)
    {
        var tables = 0;
        foreach (var ws in wb.Worksheets)
            tables += (int)ws.ListObjects.Count;
        return $"{wb.FullName}|{wb.Names.Count}|{wb.Worksheets.Count}|{tables}";
    }

    private static void AddLibraryNames(FormulaNameContext ctx)
    {
        foreach (var n in LambdaHints.Catalog.Names())
            ctx.LibraryNames.Add(n);
        foreach (var (n, range) in LambdaHints.Catalog.ArgumentCounts())
            ctx.LibraryArgumentCounts[n] = range;
    }
}

/// <summary>
///     A keyboard hook on Excel's main thread that sees Ctrl+Shift+K while a formula is being
///     edited and reads the text with Excel's own <c>xlGetFmlaInfo</c> (the call
///     ExcelDna.IntelliSense uses). Outside edit mode it lets the key through to the OnKey command.
/// </summary>
internal static class FormulaEditHook
{
    private const int WhKeyboard = 2;
    private const int VkK = 0x4B;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private static HookProc? _proc; // kept alive while the hook is installed
    private static IntPtr _hook;
    private static Action<string, IntPtr>? _handler;
    private static IntPtr _excelHwnd;

    /// <summary>Main thread only.</summary>
    public static void Install(Action<string, IntPtr> handler)
    {
        if (_hook != IntPtr.Zero)
            return;

        dynamic app = ExcelDnaUtil.Application;
        _excelHwnd = new IntPtr(app.Hwnd);
        _handler = handler;
        _proc = Proc;
        _hook = SetWindowsHookEx(WhKeyboard, _proc, IntPtr.Zero, GetCurrentThreadId());
        if (_hook == IntPtr.Zero)
            Logger.Info($"CheckFormula: keyboard hook not installed (error {Marshal.GetLastWin32Error()}); edit-mode checks are off");
    }

    public static void Uninstall()
    {
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        _handler = null;
    }

    private static IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // Key down (bit 31 clear), not an auto-repeat (bit 30 clear).
            if (code == 0 && wParam.ToInt64() == VkK && (lParam.ToInt64() & 0xC0000000L) == 0
                && IsDown(VkControl) && IsDown(VkShift) && !IsDown(VkMenu)
                && TryGetEditedFormula(out var text))
            {
                _handler?.Invoke(text, _excelHwnd);
                return new IntPtr(1); // swallow: don't type a K into the formula
            }
        }
        catch (Exception ex)
        {
            Logger.Error("CheckFormula/hook", ex);
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>The formula being edited, or false when Excel isn't editing a cell.</summary>
    internal static bool TryGetEditedFormula(out string text)
    {
        text = "";
        var info = new XlCall.FmlaInfo();
        var rc = XlCall.PenHelper(XlCall.xlGetFmlaInfo, ref info);
        if (rc != 0 || info.wPointMode == 0 || info.lpch == IntPtr.Zero || info.cch <= 0)
            return false;

        text = Marshal.PtrToStringUni(info.lpch, info.cch) ?? "";

        // Only formulas: in a cell holding text, Ctrl+Shift+K goes through untouched.
        return text.Length > 0 && text[0] is '=' or '+' or '-';
    }

    private static bool IsDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
