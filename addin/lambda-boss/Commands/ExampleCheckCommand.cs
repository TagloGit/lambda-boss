using System.Diagnostics;
using System.Globalization;
using System.Reflection;

using ExcelDna.Integration;

using LambdaBoss.Common;
using LambdaBoss.UI;

using Excel = Microsoft.Office.Interop.Excel;

namespace LambdaBoss.Commands;

/// <summary>
///     The example check (spec 0020, excel-esports#80, Labs switch <c>example-check</c>). Watches
///     Excel's SheetChange: when a formula lands in more than one cell (Ctrl+D, the fill handle,
///     Ctrl+Enter, a macro's fill) or one formula spills to more than one cell, it checks the
///     formula for a value left in from the worked example and, only if it finds one, shows it in
///     the mode the settings choose. It reads the sheet and never writes to it, so Excel's undo is
///     untouched. Ordinary one-cell edits cost one property read.
/// </summary>
internal static class ExampleCheckCommand
{
    /// <summary>Columns of a multi-column fill checked (each column is its own formula).</summary>
    private const int MaxColumns = 30;

    /// <summary>Rows above a fill searched for the rest of the family (the example row the fill started from).</summary>
    private const int FamilyRowsAbove = 60;

    private const int MaxFamilyGap = 4;

    private static readonly int[] Directions = { -1, 1 };

    private static Excel.Application? _app;
    private static bool _started;

    internal static bool Enabled => Labs.IsOn(Labs.ExampleCheck);

    /// <summary>The time the last check took, in milliseconds (logged too).</summary>
    internal static double LastCheckMs { get; private set; }

    /// <summary>Subscribes to SheetChange if the switch is on. Main thread.</summary>
    internal static void StartIfEnabled()
    {
        if (!Enabled || _started)
            return;
        try
        {
            _app = (Excel.Application)ExcelDnaUtil.Application;
            _app.SheetChange += OnSheetChange;
            _started = true;
            Logger.Info($"ExampleCheck: started (mode {ExampleCheckModes.Normalize(Settings.Current.ExampleCheckMode)})");

            // JIT the checker and compile its regexes off Excel's thread, so the first fill isn't slow.
            Task.Run(() =>
            {
                try
                {
                    foreach (var type in new[]
                             {
                                 typeof(ExampleCheckCommand), typeof(ComExampleSheet), typeof(RuleWordCache), typeof(Com),
                                 typeof(ExampleCheckWindow), typeof(ExampleLiteralChecker), typeof(FormulaParser)
                             })
                        PreJit(type);
                    ExampleLiteralChecker.Check(new ExampleCheckRequest("=IF(A1>0.5,\"x\",B1:B2)", 2, 3, new[] { 3 }, new[] { 3 }),
                        new WarmUpSheet(), () => ExampleLiteralChecker.RuleWords(new[] { "Level 1 warm-up text for the example check." }));
                }
                catch (Exception ex)
                {
                    Logger.Error("ExampleCheck.WarmUp", ex);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("ExampleCheck.Start", ex);
        }
    }

    internal static void Stop()
    {
        if (!_started)
            return;
        try
        {
            if (_app != null)
                _app.SheetChange -= OnSheetChange;
        }
        catch (Exception ex)
        {
            Logger.Error("ExampleCheck.Stop", ex);
        }

        _app = null;
        _started = false;
        ExampleCheckWindow.CloseAll();
        Logger.Info("ExampleCheck: stopped");
    }

    private static void PreJit(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                                 | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var m in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            if (m.IsAbstract || m.ContainsGenericParameters)
                continue;
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
            catch
            {
                // A method that can't be prepared ahead is JIT-compiled on first use as usual.
            }
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            if (!nested.ContainsGenericParameters)
                PreJit(nested);
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

    // ------------------------------------------------------------ the trigger

    private static void OnSheetChange(object sh, Excel.Range target)
    {
        try
        {
            if (!Enabled)
                return;
            var sw = Stopwatch.StartNew();
            var findings = CheckWrite(sh, target, out var where, out var stages);
            sw.Stop();
            if (where is null)
                return; // not a fill or spill: nothing was checked
            LastCheckMs = sw.Elapsed.TotalMilliseconds;
            var fresh = findings.Where(f => Fresh(f, ((Excel.Worksheet)sh).Parent is Excel.Workbook b ? $"{b.Name}!{where.Sheet}" : where.Sheet)).ToList();
            var detail = string.Join("; ", findings.Select(f =>
                $"{f.Finding.Literal} in {ExampleLiteralChecker.Address(f.Request.Row, f.Request.Column)} = "
                + (f.Finding.FromText ? "example text" : f.Finding.SourceAddress)));
            Logger.Info($"ExampleCheck: {where.Sheet}!{where.Label} checked in {LastCheckMs:F1} ms ({stages}), "
                        + $"{findings.Count} finding(s){(findings.Count > 0 ? ": " + detail : "")}"
                        + (fresh.Count < findings.Count ? $" ({findings.Count - fresh.Count} shown in the last minute: not again)" : ""));
            if (fresh.Count > 0)
                ExampleCheckWindow.Present(where, fresh, ExampleCheckModes.Normalize(Settings.Current.ExampleCheckMode));
        }
        catch (Exception ex)
        {
            Logger.Error("ExampleCheck", ex);
        }
    }

    private static readonly Dictionary<string, DateTime> Shown = new(StringComparer.Ordinal);

    /// <summary>
    ///     False when the same finding (sheet, example cell, literal, match) was shown in the last
    ///     minute: an undo, a redo or a re-fill of the same formula doesn't show it again.
    /// </summary>
    private static bool Fresh(ExampleCheckFinding f, string where)
    {
        var key = $"{where}|{f.Request.Row}:{f.Request.Column}|{f.Finding.Literal}|{f.Finding.SourceAddress}|{f.Request.Formula.GetHashCode()}";
        var now = DateTime.UtcNow;
        foreach (var old in Shown.Where(kv => now - kv.Value > TimeSpan.FromMinutes(1)).Select(kv => kv.Key).ToList())
            Shown.Remove(old);
        if (Shown.ContainsKey(key))
            return false;
        Shown[key] = now;
        return true;
    }

    /// <summary>
    ///     The findings for one write, or none. <paramref name="where"/> is null when the write was
    ///     neither a multi-cell formula write nor a spilling formula (so nothing was checked).
    ///     <paramref name="stages"/> says where the time went, for the log.
    /// </summary>
    private static List<ExampleCheckFinding> CheckWrite(object sh, Excel.Range target, out CheckedWrite? where,
        out string stages)
    {
        where = null;
        stages = "";
        var clock = Stopwatch.StartNew();
        // Typed COM calls, not dynamic: the DLR binder costs a second on the first fill of a session.
        if (sh is not Excel.Worksheet ws)
            return new List<ExampleCheckFinding>();
        var area = target.Areas[1];
        var count = Convert.ToInt64(area.CountLarge, CultureInfo.InvariantCulture);
        var first = (Excel.Range)area.Cells[1, 1];
        if (first.HasFormula is not true)
        {
            if (count == 1)
                return new List<ExampleCheckFinding>();
            // A fill can start on a blank or constant row only if nothing is a formula: cheap out.
            if (area.HasFormula is false)
                return new List<ExampleCheckFinding>();
        }

        int r1 = area.Row, c1 = area.Column;
        int rows = area.Rows.Count, cols = area.Columns.Count;

        var requests = new List<ExampleCheckRequest>();
        string label;
        if (count == 1)
        {
            // One cell: only a formula whose result spills to more than one cell.
            if (!SpillSize(first, out int spillRows, out int spillCols) || spillRows * spillCols <= 1)
                return new List<ExampleCheckFinding>();
            var others = Enumerable.Range(r1 + 1, Math.Min(spillRows - 1, ExampleLiteralChecker.MaxCompareRows)).ToList();
            requests.Add(new ExampleCheckRequest(Com.Formula2(first), r1, c1, others,
                Enumerable.Range(c1, spillCols).ToList()));
            label = $"spill from {ExampleLiteralChecker.Address(r1, c1)}";
        }
        else
        {
            // Every written column is excluded from every column's candidates: its values are outputs.
            var written = Enumerable.Range(c1, cols).ToList();
            for (var i = 1; i <= Math.Min(cols, MaxColumns); i++)
            {
                var req = FamilyRequest(ws, r1, c1 + i - 1, rows, written);
                if (req is not null)
                    requests.Add(req);
            }

            if (requests.Count == 0)
                return new List<ExampleCheckFinding>();
            label = $"fill of {ExampleLiteralChecker.Address(r1, c1)}:{ExampleLiteralChecker.Address(r1 + rows - 1, c1 + cols - 1)}";
        }

        var tFamily = clock.Elapsed.TotalMilliseconds;
        var sheet = new ComExampleSheet(ws, requests);
        var tRead = clock.Elapsed.TotalMilliseconds;
        var wb = (Excel.Workbook)ws.Parent;
        var findings = new List<ExampleCheckFinding>();
        foreach (var req in requests)
        {
            foreach (var f in ExampleLiteralChecker.Check(req, sheet, () => RuleWordCache.For(wb)))
                findings.Add(new ExampleCheckFinding(req, f));
        }

        stages = $"family {tFamily:F0}, read {tRead - tFamily:F0}, check {clock.Elapsed.TotalMilliseconds - tRead:F0}";

        where = new CheckedWrite(label, ws.Name, r1, c1, rows, cols, new IntPtr(_app!.Hwnd));
        return findings;
    }

    private static bool SpillSize(Excel.Range cell, out int rows, out int cols)
    {
        rows = cols = 1;
        try
        {
            if (Com.Get(cell, "HasSpill") is not true)
                return false;
            var spill = (Excel.Range)Com.Get(cell, "SpillingToRange")!;
            rows = spill.Rows.Count;
            cols = spill.Columns.Count;
            return true;
        }
        catch
        {
            return false; // an Excel without dynamic arrays
        }
    }

    /// <summary>
    ///     One column of a fill: the written rows holding the same formula (R1C1) as the column's
    ///     first formula, extended upward over the rows the fill started from. Null when that's a
    ///     single row (nothing to compare with).
    /// </summary>
    private static ExampleCheckRequest? FamilyRequest(Excel.Worksheet ws, int r1, int col, int rows,
        IReadOnlyCollection<int> targetColumns)
    {
        // Only the first rows matter (50 are compared): a full-column fill reads no more than this.
        rows = Math.Min(rows, ExampleLiteralChecker.MaxCompareRows + 10);
        object written = Com.Block(ws, r1, col, r1 + rows - 1, col).FormulaR1C1;
        var texts = Column(written, rows);
        var firstIdx = Array.FindIndex(texts, t => t.StartsWith("=", StringComparison.Ordinal));
        if (firstIdx < 0)
            return null;
        var key = texts[firstIdx];
        var family = new List<int>();
        for (var i = firstIdx; i < texts.Length; i++)
            if (texts[i] == key)
                family.Add(r1 + i);

        // Up from the fill: Ctrl+D from the example row includes it, the fill handle may not.
        var top = r1 + firstIdx;
        var above = Math.Min(FamilyRowsAbove, top - 1);
        if (above > 0)
        {
            object up = Com.Block(ws, top - above, col, top - 1, col).FormulaR1C1;
            var upTexts = Column(up, above);
            var gap = 0;
            for (var i = upTexts.Length - 1; i >= 0; i--)
            {
                if (upTexts[i] == key)
                {
                    family.Insert(0, top - above + i);
                    gap = 0;
                }
                else if (upTexts[i].Length == 0 && ++gap <= MaxFamilyGap)
                {
                }
                else
                    break;
            }
        }

        if (family.Count < 2)
            return null;
        var example = family[0];
        var formula = Com.Formula2(Com.Block(ws, example, col, example, col));
        return new ExampleCheckRequest(formula, example, col,
            family.Skip(1).Take(ExampleLiteralChecker.MaxCompareRows).ToList(), targetColumns);
    }

    /// <summary>
    ///     The family the active cell belongs to: the rows above and below holding its formula
    ///     (R1C1), with gaps of up to four rows. Null when it's alone.
    /// </summary>
    private static ExampleCheckRequest? FamilyAround(Excel.Worksheet ws, int row, int col, string cellFormula)
    {
        var top = Math.Max(1, row - FamilyRowsAbove);
        var bottom = Math.Min(1048576, row + FamilyRowsAbove);
        object window = Com.Block(ws, top, col, bottom, col).FormulaR1C1;
        var texts = Column(window, bottom - top + 1);
        var at = row - top;
        var key = texts[at];
        var family = new List<int> { row };
        foreach (var step in Directions)
        {
            var gap = 0;
            for (var i = at + step; i >= 0 && i < texts.Length; i += step)
            {
                if (texts[i] == key)
                {
                    family.Add(top + i);
                    gap = 0;
                }
                else if (texts[i].Length == 0 && ++gap <= MaxFamilyGap)
                {
                }
                else
                    break;
            }
        }

        if (family.Count < 2)
            return null;
        family.Sort();
        var example = family[0];
        var formula = example == row ? cellFormula : Com.Formula2(Com.Block(ws, example, col, example, col));
        return new ExampleCheckRequest(formula, example, col,
            family.Skip(1).Take(ExampleLiteralChecker.MaxCompareRows).ToList(), new[] { col });
    }

    private static string[] Column(object values, int rows)
    {
        if (values is object[,] arr)
        {
            var lb = arr.GetLowerBound(0);
            var lc = arr.GetLowerBound(1);
            var outp = new string[rows];
            for (var i = 0; i < rows; i++)
                outp[i] = arr[lb + i, lc] as string ?? "";
            return outp;
        }

        return new[] { values as string ?? "" };
    }

    // ------------------------------------------------------------ Check Formula (Ctrl+Shift+C)

    /// <summary>
    ///     The example findings for the active cell as Check Formula problems, positioned in
    ///     <paramref name="cellFormula"/>. Empty unless the cell is part of a filled family or
    ///     spills. Macro context.
    /// </summary>
    internal static IReadOnlyList<FormulaProblem> ProblemsForCell(object cellObject, string cellFormula)
    {
        var problems = new List<FormulaProblem>();
        if (!Enabled || cellObject is not Excel.Range cell)
            return problems;
        try
        {
            var ws = cell.Worksheet;
            int row = cell.Row, col = cell.Column;
            ExampleCheckRequest? req;
            if (SpillSize(cell, out int sr, out int sc) && sr * sc > 1)
                req = new ExampleCheckRequest(cellFormula, row, col,
                    Enumerable.Range(row + 1, Math.Min(sr - 1, ExampleLiteralChecker.MaxCompareRows)).ToList(),
                    Enumerable.Range(col, sc).ToList());
            else
            {
                req = FamilyAround(ws, row, col, cellFormula);
                if (req is null)
                    return problems;
            }

            var findings = ExampleLiteralChecker.Check(req, new ComExampleSheet(ws, new[] { req }),
                () => RuleWordCache.For((Excel.Workbook)ws.Parent));
            if (findings.Count == 0)
                return problems;

            // The example cell's formula and this cell's differ only in their references: the
            // literals line up one for one.
            var exampleLits = ExampleLiteralChecker.Scan(req.Formula).Literals;
            var cellLits = ExampleLiteralChecker.Scan(cellFormula).Literals;
            foreach (var f in findings)
            {
                var idx = exampleLits.FindIndex(l => l.Start == f.Start);
                if (idx < 0 || idx >= cellLits.Count || cellLits.Count != exampleLits.Count)
                    continue;
                var at = cellLits[idx];
                var src = f.FromText ? "the level's example text" : $"{f.SourceAddress} (the example row is {req.Row})";
                problems.Add(new FormulaProblem(FormulaProblemKind.ExampleValue, at.Start, at.Length,
                    f.Message + $" Matched: {src}."));
            }
        }
        catch (Exception ex)
        {
            Logger.Error("ExampleCheck/CheckFormula", ex);
        }

        return problems;
    }
}

/// <summary>A tiny sheet for the warm-up check.</summary>
internal sealed class WarmUpSheet : IExampleSheet
{
    public object? Value(int row, int column) => row == 1 ? "Example: the threshold is 0.5 in this warm-up text." : (double)row;
    public bool IsFormula(int row, int column) => false;
    public int LastColumn => 3;
}

/// <summary>Where a check ran: the written range, for the message and the highlight.</summary>
internal sealed record CheckedWrite(string Label, string Sheet, int Row, int Column, int Rows, int Columns, IntPtr ExcelHwnd);

/// <summary>A finding with the request it came from.</summary>
internal sealed record ExampleCheckFinding(ExampleCheckRequest Request, ExampleLiteralFinding Finding);

/// <summary>The display modes, as the settings store them.</summary>
internal static class ExampleCheckModes
{
    public const string Status = "status";
    public const string Toast = "toast";
    public const string Prompt = "prompt";

    public static string Normalize(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            Status => Status,
            Prompt => Prompt,
            _ => Toast
        };
}

/// <summary>
///     Reads the sheet for the check in a few block reads: one rectangle around the example rows
///     (80 rows above for the level's text, down to the last compared row), and more blocks on
///     demand for ranges elsewhere. Only Value2 and the example rows' Formula2: never a write.
/// </summary>
internal sealed class ComExampleSheet : IExampleSheet
{
    private const int MaxColumns = 200;
    private readonly Excel.Worksheet _ws;
    private readonly List<(int R1, int C1, int R2, int C2, object[,] Values)> _blocks = new();
    private readonly Dictionary<int, HashSet<int>> _formulaColumns = new();

    public ComExampleSheet(Excel.Worksheet ws, IReadOnlyCollection<ExampleCheckRequest> requests)
    {
        _ws = ws;
        int lastCol;
        try
        {
            var used = ws.UsedRange;
            lastCol = used.Column + used.Columns.Count - 1;
        }
        catch
        {
            lastCol = 50;
        }

        LastColumn = Math.Min(MaxColumns, Math.Max(lastCol, requests.Max(r => r.Column)));
        var top = Math.Max(1, requests.Min(r => r.Row) - ExampleLiteralChecker.TextRowsAbove);
        var bottom = requests.Max(r => r.OtherRows.Count > 0 ? r.OtherRows.Max() : r.Row);
        Load(top, 1, bottom, LastColumn);

        foreach (var row in requests.Select(r => r.Row).Distinct())
        {
            var set = new HashSet<int>();
            var f = Com.Get(Com.Block(ws, row, 1, row, LastColumn), "Formula2");
            if (f is object[,] arr)
            {
                var lb = arr.GetLowerBound(0);
                var lc = arr.GetLowerBound(1);
                for (var c = 0; c < LastColumn; c++)
                    if (arr[lb, lc + c] is string s && s.StartsWith("=", StringComparison.Ordinal))
                        set.Add(c + 1);
            }

            _formulaColumns[row] = set;
        }
    }

    public int LastColumn { get; }

    public object? Value(int row, int column)
    {
        foreach (var b in _blocks)
            if (row >= b.R1 && row <= b.R2 && column >= b.C1 && column <= b.C2)
                return Norm(b.Values[b.Values.GetLowerBound(0) + row - b.R1, b.Values.GetLowerBound(1) + column - b.C1]);
        if (row < 1 || column < 1 || row > 1048576 || column > 16384)
            return null;
        Load(row, column, Math.Min(1048576, row + ExampleLiteralChecker.MaxCompareRows),
            Math.Min(16384, column + 30));
        return Value(row, column);
    }

    public bool IsFormula(int row, int column) =>
        _formulaColumns.TryGetValue(row, out var set) && set.Contains(column);

    private void Load(int r1, int c1, int r2, int c2)
    {
        object v = Com.Block(_ws, r1, c1, r2, c2).Value2;
        if (v is not object[,] arr)
        {
            arr = (object[,])Array.CreateInstance(typeof(object), new[] { 1, 1 }, new[] { 1, 1 });
            arr[1, 1] = v;
        }

        _blocks.Add((r1, c1, r2, c2, arr));
    }

    private static object? Norm(object? v) => v switch
    {
        int i => i, // an error value (Value2 gives errors as ints)
        _ => v
    };
}

/// <summary>
///     The words of each open workbook's prose outside its examples (the case's vocabulary), read
///     once per workbook: the first 400 rows and 30 columns of every sheet.
/// </summary>
internal static class RuleWordCache
{
    private static readonly Dictionary<string, HashSet<string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> For(Excel.Workbook wb)
    {
        string key;
        try
        {
            key = $"{wb.FullName}|{wb.Worksheets.Count}";
        }
        catch
        {
            return Array.Empty<string>();
        }

        if (Cache.TryGetValue(key, out var words))
            return words;

        var texts = new List<string>();
        var sw = Stopwatch.StartNew();
        try
        {
            foreach (Excel.Worksheet ws in wb.Worksheets)
            {
                object v = Com.Block(ws, 1, 1, 400, 30).Value2;
                if (v is not object[,] arr)
                    continue;
                for (var r = arr.GetLowerBound(0); r <= arr.GetUpperBound(0); r++)
                for (var c = arr.GetLowerBound(1); c <= arr.GetUpperBound(1); c++)
                    if (arr[r, c] is string s)
                        texts.Add(s);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("ExampleCheck/RuleWords", ex);
        }

        words = ExampleLiteralChecker.RuleWords(texts);
        Cache[key] = words;
        Logger.Info($"ExampleCheck: read {wb.Name}'s rules vocabulary ({words.Count} words) in {sw.ElapsedMilliseconds} ms");
        return words;
    }
}

/// <summary>
///     Typed COM helpers. Properties newer than the embedded Excel 2013 types (Formula2, HasSpill,
///     SpillingToRange) go through IDispatch by name.
/// </summary>
internal static class Com
{
    public static Excel.Range Block(Excel.Worksheet ws, int r1, int c1, int r2, int c2) =>
        ws.Range[ws.Cells[r1, c1], ws.Cells[r2, c2]];

    /// <summary>
    ///     The locale passed to Excel's IDispatch: en-US (1033), which every Excel accepts. The
    ///     invariant culture (LCID 127) fails with 0x80028018 TYPE_E_INVDATAREAD when the Windows
    ///     user locale is, say, en-GB (seen on Tim's PC, 16 Nov; reproduced in the VM).
    /// </summary>
    internal static readonly CultureInfo DispatchCulture = CultureInfo.GetCultureInfo(1033);

    public static object? Get(object o, string property) =>
        o.GetType().InvokeMember(property, BindingFlags.GetProperty, null, o, null, DispatchCulture);

    public static string Formula2(Excel.Range cell) => Get(cell, "Formula2") as string ?? "";
}
