using System.Runtime.InteropServices;

using ExcelDna.Integration;

using LambdaBoss.Common;
using LambdaBoss.UI;

namespace LambdaBoss.Commands;

/// <summary>
///     Where a checked formula came from, so a fix can go back there: the active cell (written
///     through COM), or Excel's edit buffer (retyped, so nothing is committed).
/// </summary>
internal sealed record FixTarget(bool MidEdit, IntPtr ExcelHwnd, string? Workbook, string? Sheet, string? Address);

/// <summary>
///     Applies and reverts Check Formula's fixes (issue #426).
///     <list type="bullet">
///         <item>
///             A cell's formula is written through COM, which clears Excel's undo stack, so the
///             formula from before is kept and Revert writes it back.
///         </item>
///         <item>
///             Mid-edit, the add-in can't touch Excel's edit buffer. The fix cancels the edit (Esc),
///             re-opens the cell in Edit mode (F2), selects its text and pastes the fixed formula
///             (typing it lost characters to Formula AutoComplete in the VM), then moves the cursor
///             to the fix. Nothing is entered, so Excel's undo is untouched and Tim presses Enter
///             himself. The clipboard's text is put back afterwards. Revert pastes the original back.
///         </item>
///     </list>
/// </summary>
internal static class FormulaFixer
{
    /// <summary>The last fix applied: what to put back, and where.</summary>
    internal sealed record AppliedFix(FixTarget Target, string Before, string After, string RevertTo, int RevertCaret);

    private static AppliedFix? _last;

    internal static AppliedFix? Last => _last;

    /// <summary>True when <paramref name="formula"/> at <paramref name="target"/> is what the last fix wrote.</summary>
    internal static bool CanRevert(FixTarget? target, string formula) =>
        target is not null && _last is { } l && l.Target.MidEdit == target.MidEdit
        && l.Target.Address == target.Address && l.Target.Sheet == target.Sheet && l.Target.Workbook == target.Workbook
        && Normalise(l.After) == Normalise(formula);

    private static string Normalise(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>On the window thread. <paramref name="closeWindow"/> closes the Check Formula window.</summary>
    public static void Apply(FormulaFix fix, FixTarget target, Action closeWindow)
    {
        if (target.MidEdit)
        {
            _last = new AppliedFix(target, fix.Before, fix.After, fix.Before, Math.Min(fix.EditStart, fix.Before.Length));
            closeWindow();
            RunSta(() => PasteIntoEdit(target.ExcelHwnd, fix.After, fix.Caret));
            return;
        }

        ExcelAsyncUtil.QueueAsMacro(() => WriteCell(target, fix.After, fix.Before, isRevert: false));
    }

    /// <summary>On the window thread.</summary>
    public static void Revert(FixTarget target, Action closeWindow)
    {
        if (_last is not { } last)
            return;

        if (target.MidEdit)
        {
            _last = null;
            closeWindow();
            RunSta(() => PasteIntoEdit(target.ExcelHwnd, last.RevertTo, last.RevertCaret));
            return;
        }

        ExcelAsyncUtil.QueueAsMacro(() => WriteCell(target, last.RevertTo, last.After, isRevert: true));
    }

    // ------------------------------------------------------------ cell

    /// <summary>Macro context.</summary>
    private static void WriteCell(FixTarget target, string text, string expected, bool isRevert)
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            dynamic cell = app.Workbooks[target.Workbook].Worksheets[target.Sheet].Range[target.Address];

            // What the cell holds now: a formula, or text (a formula entered as '=… to get past Excel).
            var hasFormula = (bool)cell.HasFormula;
            string shown = hasFormula ? (string)cell.Formula2 : Convert.ToString(cell.Value2) ?? "";
            string current = hasFormula ? shown : "'" + shown;

            // The window may be older than the cell: never overwrite something newer.
            if (Normalise(shown) != Normalise(expected.StartsWith("'", StringComparison.Ordinal) ? expected[1..] : expected))
            {
                CheckFormulaWindow.ShowMessage("The cell has changed since it was checked, so it wasn't changed. Check it again.");
                return;
            }

            try
            {
                cell.Formula2 = text;
            }
            catch (COMException ex)
            {
                Logger.Error("FormulaFixer.WriteCell", ex);
                CheckFormulaWindow.ShowMessage("Excel refused the formula, so the cell wasn't changed.");
                return;
            }

            // Keep the formula as Excel stores it (names and functions in its own case), so the
            // re-check recognises it and offers Revert.
            string stored = (bool)cell.HasFormula ? (string)cell.Formula2 : Convert.ToString(cell.Value2) ?? text;
            if (isRevert)
                _last = null;
            else
                _last = new AppliedFix(target, current, stored, current, 0);

            // Check the cell again and show the result, with Revert.
            CheckFormulaCommand.CheckCell(app, cell, target);
        }
        catch (Exception ex)
        {
            Logger.Error("FormulaFixer.WriteCell", ex);
        }
    }

    // ------------------------------------------------------------ edit buffer

    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkEnd = 0x23;
    private const ushort VkHome = 0x24;
    private const ushort VkV = 0x56;
    private const ushort VkEscape = 0x1B;
    private const ushort VkLeft = 0x25;
    private const ushort VkF2 = 0x71;
    private const uint KeyUp = 0x0002;

    private static void RunSta(Action action)
    {
        var t = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.Error("FormulaFixer", ex);
            }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); // the clipboard needs it
        t.Start();
    }

    /// <summary>
    ///     On an STA thread: cancel Excel's edit, re-open the cell in Edit mode, replace its text
    ///     with <paramref name="text"/> by pasting, and put the cursor at <paramref name="caret"/>.
    /// </summary>
    internal static void PasteIntoEdit(IntPtr excelHwnd, string text, int caret)
    {
        // Keys still held from the click or Ctrl+Z would turn Esc into Ctrl+Esc and so on.
        for (var i = 0; i < 60 && AnyModifierDown(); i++)
            Thread.Sleep(50);
        if (AnyModifierDown())
        {
            Logger.Info("FormulaFixer: a modifier key is still down; fix not applied");
            return;
        }

        // The window has just closed and handed focus back; make sure Excel has it.
        for (var i = 0; i < 30 && GetForegroundWindow() != excelHwnd; i++)
        {
            SetForegroundWindow(excelHwnd);
            Thread.Sleep(50);
        }

        if (GetForegroundWindow() != excelHwnd)
        {
            Logger.Info("FormulaFixer: Excel didn't take focus; fix not applied");
            return;
        }

        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string? saved = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                saved = System.Windows.Clipboard.GetText();
        }
        catch (Exception ex)
        {
            Logger.Error("FormulaFixer: read clipboard", ex);
        }

        if (!SetClipboard(normalised))
            return;

        var inputs = new List<Input>();
        // Esc twice: the first may only close Formula AutoComplete's list. Then F2 re-opens the
        // cell in Edit mode, Ctrl+End, Ctrl+Shift+Home selects all of its text, Ctrl+V replaces it.
        Key(inputs, VkEscape);
        Key(inputs, VkEscape);
        Key(inputs, VkF2);
        Chord(inputs, VkEnd, VkControl);
        Chord(inputs, VkHome, VkControl, VkShift);
        Chord(inputs, VkV, VkControl);
        var lefts = Math.Max(0, normalised.Length - Math.Min(caret, normalised.Length));
        for (var i = 0; i < lefts; i++)
            Key(inputs, VkLeft);
        Send(inputs);
        Logger.Info($"FormulaFixer: pasted {normalised.Length} characters, cursor {lefts} from the end");

        // Put the clipboard's text back once Excel has pasted (unless something else changed it).
        Thread.Sleep(2000);
        try
        {
            if (saved is not null && System.Windows.Clipboard.GetText() == normalised)
                SetClipboard(saved);
        }
        catch (Exception ex)
        {
            Logger.Error("FormulaFixer: restore clipboard", ex);
        }
    }

    private static bool SetClipboard(string text)
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(50); // another program has it open
            }
        }

        Logger.Info("FormulaFixer: clipboard busy; fix not applied");
        return false;
    }

    private static bool AnyModifierDown() =>
        (GetAsyncKeyState(VkControl) & 0x8000) != 0 || (GetAsyncKeyState(VkShift) & 0x8000) != 0
        || (GetAsyncKeyState(VkMenu) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0
        || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

    private static void Chord(List<Input> inputs, ushort vk, params ushort[] modifiers)
    {
        foreach (var m in modifiers)
            inputs.Add(Vk(m, 0));
        Key(inputs, vk);
        for (var i = modifiers.Length - 1; i >= 0; i--)
            inputs.Add(Vk(modifiers[i], KeyUp));
    }

    private static void Key(List<Input> inputs, ushort vk)
    {
        inputs.Add(Vk(vk, 0));
        inputs.Add(Vk(vk, KeyUp));
    }

    private static Input Vk(ushort vk, uint flags) =>
        new() { Type = 1, U = new InputUnion { Ki = new KeyboardInput { Vk = vk, Flags = flags } } };

    private static void Send(List<Input> inputs)
    {
        const int chunk = 400;
        var size = Marshal.SizeOf(typeof(Input));
        for (var i = 0; i < inputs.Count; i += chunk)
        {
            var part = inputs.Skip(i).Take(chunk).ToArray();
            var sent = SendInput((uint)part.Length, part, size);
            if (sent != part.Length)
                Logger.Info($"FormulaFixer: SendInput sent {sent} of {part.Length} (error {Marshal.GetLastWin32Error()})");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // INPUT's union: MOUSEINPUT is its largest member, so it sets the size on 32- and 64-bit alike.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mi;
        [FieldOffset(0)] public KeyboardInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
