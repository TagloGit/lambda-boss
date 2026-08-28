using System.Runtime.InteropServices;

using Xunit;
using Xunit.Abstractions;

namespace LambdaBoss.AddinTests;

/// <summary>
///     Spec 0010 PR 3 smoke: a gathered LET containing slice expressions
///     evaluates, in live Excel, to the same value as the original cell graph.
///     The unit suite covers the slice ladder and the engine wiring
///     exhaustively against a stub; what it cannot establish is that Excel
///     actually accepts the text the generator emits. Two things are pinned
///     here:
///
///     <list type="bullet">
///         <item>
///             The end-to-end round trip — sheet in, <c>=LET(...)</c> out, same
///             answer — over a graph whose steps read individual cells of a
///             spill. This is also the live proof of the scalar-widening fix:
///             if <c>=A2*2</c> still bound the whole array, the synthesised LET
///             would spill a two-element result instead of the original scalar.
///         </item>
///         <item>
///             That Excel accepts the generator's <em>bare-comma</em> argument
///             omission (<c>TAKE(arr,,-1)</c>) inside a LET binding's RHS.
///             PR 3's single-cell path only ever emits <c>INDEX</c>, but PR 4's
///             band/block forms depend on this and no pure test can confirm it.
///         </item>
///     </list>
/// </summary>
[Collection("Excel Addin")]
public class GatherSpillSliceSmokeTests
{
    private readonly ExcelAddinFixture _excel;
    private readonly ITestOutputHelper _output;

    public GatherSpillSliceSmokeTests(ExcelAddinFixture excel, ITestOutputHelper output)
    {
        _excel = excel;
        _output = output;
    }

    [Fact]
    public void GatheredLet_WithSingleCellSlices_EvaluatesToTheSameValue()
    {
        var ws = _excel.AddWorksheet();
        try
        {
            var sheetName = SeedSpillGraph(ws);
            var expected = (string)ws.Range["D6"].Value2;

            var source = new LiveSheetCellSource(ws, sheetName);
            // D6 — column 4, row 6.
            var result = GatherEngine.Gather(new CellRef(sheetName, 4, 6), source);

            Assert.NotNull(result);
            Assert.Null(result!.Diagnostic);
            _output.WriteLine(result.SynthesisedLet);

            // The anchor binds the whole array; the two single-cell reads of
            // it become named INDEX slices rather than stray cell inputs.
            var anchor = Assert.Single(result.Bindings.Where(b => b.Rhs.EndsWith("#", StringComparison.Ordinal)));
            Assert.Equal("A2#", anchor.Rhs);
            var slices = result.Bindings.Where(b => b.SliceOf != null).ToList();
            Assert.Equal(2, slices.Count);
            Assert.All(slices, s => Assert.StartsWith("INDEX(", s.Rhs, StringComparison.Ordinal));

            ws.Range["F1"].Formula2 = result.SynthesisedLet;
            _excel.Application.Calculate();

            // Same scalar, not a spilled two-element array — the LET is
            // equivalent to the cell graph it replaced. G1 staying empty is
            // the direct check on the widening bug: a LET that bound the
            // whole array to `doubled` would spill into it.
            Assert.Equal(expected, (string)ws.Range["F1"].Value2);
            Assert.Null(ws.Range["G1"].Value2);
        }
        finally
        {
            Cleanup(ws);
        }
    }

    [Fact]
    public void LetBindingRhs_WithBareCommaTake_IsAcceptedByExcel()
    {
        var ws = _excel.AddWorksheet();
        try
        {
            SeedSpillGraph(ws);

            // A2 spills {10, 20}. TAKE with an omitted (bare-comma) row
            // argument and a negative column take is the shape PR 4's edge-
            // flush band selector produces.
            ws.Range["F3"].Formula2 = "=LET(arr, A2#, lastCol, TAKE(arr,,-1), lastCol)";
            _excel.Application.Calculate();

            Assert.Equal(20d, (double)ws.Range["F3"].Value2);
        }
        finally
        {
            Cleanup(ws);
        }
    }

    /// <summary>
    ///     Lays out the spec's canonical shape: an interim step that spills,
    ///     with downstream steps reading individual cells out of it.
    ///     <code>
    ///     A1 Extracted   B1 Second
    ///     A2 =SEQUENCE(1,2)*10   spills {10, 20} into A2:B2
    ///     C4 Doubled     D4 =A2*2        -> 20   (scalar read of the anchor)
    ///     C5 Suffixed    D5 =B2&amp;"x"      -> "20x" (read of a spill child)
    ///     C6 Joined      D6 =D4&amp;D5       -> "2020x"  &lt;- sink
    ///     </code>
    /// </summary>
    private string SeedSpillGraph(dynamic ws)
    {
        ws.Range["A1"].Value2 = "Extracted";
        ws.Range["B1"].Value2 = "Second";
        ws.Range["A2"].Formula2 = "=SEQUENCE(1,2)*10";
        ws.Range["C4"].Value2 = "Doubled";
        ws.Range["D4"].Formula2 = "=A2*2";
        ws.Range["C5"].Value2 = "Suffixed";
        ws.Range["D5"].Formula2 = "=B2&\"x\"";
        ws.Range["C6"].Value2 = "Joined";
        ws.Range["D6"].Formula2 = "=D4&D5";
        _excel.Application.Calculate();
        return (string)ws.Name;
    }

    private static void Cleanup(dynamic ws)
    {
        try
        {
            ws.Delete();
            Marshal.ReleaseComObject(ws);
        }
        catch
        {
            // Ignore cleanup
        }
    }

    /// <summary>
    ///     Minimal single-sheet <see cref="ICellSource" /> over a live
    ///     worksheet. Mirrors <c>GatherCommand.LiveCellSource</c> (which is
    ///     private to the add-in assembly) closely enough to exercise the
    ///     engine against real COM spill geometry; refs to any other sheet
    ///     read as unreachable, which the graph here never needs.
    /// </summary>
    private sealed class LiveSheetCellSource : ICellSource
    {
        private readonly dynamic _sheet;

        public LiveSheetCellSource(dynamic sheet, string sinkSheet)
        {
            _sheet = sheet;
            SinkSheet = sinkSheet;
        }

        public string SinkSheet { get; }

        public string? GetFormula(CellRef cell)
        {
            if (!Reachable(cell))
                return null;
            dynamic range = _sheet.Cells[cell.Row, cell.Column];
            return (bool)range.HasFormula ? (string)range.Formula2 : null;
        }

        public string? GetCellAboveText(CellRef cell)
        {
            if (!Reachable(cell) || cell.Row <= 1)
                return null;
            return ReadText(cell.Row - 1, cell.Column);
        }

        public string? GetCellLeftText(CellRef cell)
        {
            if (!Reachable(cell) || cell.Column <= 1)
                return null;
            return ReadText(cell.Row, cell.Column - 1);
        }

        public SpillInfo? GetSpill(CellRef cell)
        {
            if (!Reachable(cell))
                return null;
            dynamic range = _sheet.Cells[cell.Row, cell.Column];
            dynamic? anchorRange = range.SpillParent;
            if (anchorRange == null)
                return null;
            dynamic? rect = anchorRange.SpillingToRange;
            if (rect == null)
                return null;
            var anchor = new CellRef(cell.Sheet, (int)anchorRange.Column, (int)anchorRange.Row);
            return new SpillInfo(anchor, (int)rect.Rows.Count, (int)rect.Columns.Count);
        }

        public bool IsLambdaName(string name) => false;

        private bool Reachable(CellRef cell) =>
            !cell.IsExternal
            && string.Equals(cell.Sheet, SinkSheet, StringComparison.OrdinalIgnoreCase);

        private string? ReadText(int row, int column)
        {
            var value = _sheet.Cells[row, column].Value2;
            return value is string s && s.Length > 0 ? s : null;
        }
    }
}
