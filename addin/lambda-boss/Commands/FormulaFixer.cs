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
///             types the fixed formula back in, switches to Edit mode (F2) and moves the cursor to the
///             fix. Nothing is entered, so Excel's undo is untouched and Tim presses Enter himself.
///             Revert types the original back the same way.
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
            _last = new AppliedFix(target, fix.Before, fix.After, fix.Before, Math.Min(fix.HighlightStart, fix.Before.Length));
            closeWindow();
            Task.Run(() => RetypeEdit(target.ExcelHwnd, fix.After, fix.Caret));
            return;
        }

        ExcelAsyncUtil.QueueAsMacro(() => WriteCell(target, fix.After, isRevert: false));
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
            Task.Run(() => RetypeEdit(target.ExcelHwnd, last.RevertTo, last.RevertCaret));
            return;
        }

        ExcelAsyncUtil.QueueAsMacro(() => WriteCell(target, last.RevertTo, isRevert: true));
    }

    // ------------------------------------------------------------ cell

    /// <summary>Macro context.</summary>
    private static void WriteCell(FixTarget target, string text, bool isRevert)
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            dynamic cell = app.Workbooks[target.Workbook].Worksheets[target.Sheet].Range[target.Address];

            // What the cell holds now: a formula, or text (a formula entered as '=… to get past Excel).
            string current = (bool)cell.HasFormula ? (string)cell.Formula2 : "'" + Convert.ToString(cell.Value2);

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

            if (isRevert)
                _last = null;
            else
                _last = new AppliedFix(target, current, text, current, 0);

            // Check the cell again and show the result, with Revert.
            CheckFormulaCommand.CheckCell(app, cell, target);
        }
        catch (Exception ex)
        {
            Logger.Error("FormulaFixer.WriteCell", ex);
        }
    }

    // ------------------------------------------------------------ edit buffer

    private const ushort VkReturn = 0x0D;
    private const ushort VkMenu = 0x12;
    private const ushort VkEscape = 0x1B;
    private const ushort VkLeft = 0x25;
    private const ushort VkF2 = 0x71;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;

    /// <summary>
    ///     Off the window thread: cancel Excel's edit, type <paramref name="text"/> as a new entry,
    ///     switch to Edit mode and put the cursor at <paramref name="caret"/>.
    /// </summary>
    internal static void RetypeEdit(IntPtr excelHwnd, string text, int caret)
    {
        try
        {
            // The window has just closed and handed focus back; make sure Excel has it.
            for (var i = 0; i < 30 && GetForegroundWindow() != excelHwnd; i++)
            {
                SetForegroundWindow(excelHwnd);
                Thread.Sleep(50);
            }

            if (GetForegroundWindow() != excelHwnd)
            {
                Logger.Info("FormulaFixer: Excel didn't take focus; fix not typed");
                return;
            }

            var inputs = new List<Input>();
            Key(inputs, VkEscape);
            var typed = 0;
            foreach (var ch in text)
            {
                if (ch == '\r')
                    continue;
                if (ch == '\n')
                {
                    // Alt+Enter: a line break inside the cell.
                    inputs.Add(Vk(VkMenu, 0));
                    Key(inputs, VkReturn);
                    inputs.Add(Vk(VkMenu, KeyUp));
                }
                else
                {
                    inputs.Add(Char(ch, 0));
                    inputs.Add(Char(ch, KeyUp));
                }

                typed++;
            }

            // F2 turns Enter mode into Edit mode, so the arrows move the cursor rather than
            // pointing at cells.
            Key(inputs, VkF2);
            var lefts = Math.Max(0, typed - Math.Min(caret, typed));
            for (var i = 0; i < lefts; i++)
                Key(inputs, VkLeft);

            Send(inputs);
            Logger.Info($"FormulaFixer: retyped {typed} characters, cursor {lefts} from the end");
        }
        catch (Exception ex)
        {
            Logger.Error("FormulaFixer.RetypeEdit", ex);
        }
    }

    private static void Key(List<Input> inputs, ushort vk)
    {
        inputs.Add(Vk(vk, 0));
        inputs.Add(Vk(vk, KeyUp));
    }

    private static Input Vk(ushort vk, uint flags) =>
        new() { Type = 1, U = new InputUnion { Ki = new KeyboardInput { Vk = vk, Flags = flags } } };

    private static Input Char(char ch, uint flags) =>
        new() { Type = 1, U = new InputUnion { Ki = new KeyboardInput { Scan = ch, Flags = Unicode | flags } } };

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
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
