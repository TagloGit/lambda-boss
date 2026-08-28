using System.Diagnostics;
using System.Runtime.InteropServices;

using Xunit;
using Xunit.Abstractions;

namespace LambdaBoss.AddinTests;

/// <summary>
///     Spec 0010 PR 1 spike: pins the COM semantics of dynamic-array spills
///     that <c>LiveCellSource.GetSpill</c> is built on. Records
///     <c>HasSpill</c>, <c>HasFormula</c>, <c>Formula2</c>, <c>SpillParent</c>
///     and <c>SpillingToRange</c> for a spill anchor, a spill child, a plain
///     formula cell and a plain literal cell, and measures the cost of the
///     geometry probe. Findings are written up in
///     <c>specs/0010-gather-spill-aware-references.md</c> (Open Questions);
///     the assertions here are the regression guard on that contract.
/// </summary>
[Collection("Excel Addin")]
public class SpillComSemanticsTests
{
    private readonly ExcelAddinFixture _excel;
    private readonly ITestOutputHelper _output;

    public SpillComSemanticsTests(ExcelAddinFixture excel, ITestOutputHelper output)
    {
        _excel = excel;
        _output = output;
    }

    [Fact]
    public void SpillProperties_AcrossCellKinds_RecordedForSpec()
    {
        var ws = _excel.AddWorksheet();
        try
        {
            // A1 spills a 2x3 array into A1:C2, so we get an anchor (A1),
            // an interior child (B1) and the far corner child (C2).
            ws.Range["A1"].Formula2 = "=SEQUENCE(2,3)";
            // A plain (non-spilling) formula cell reading the anchor.
            ws.Range["E1"].Formula2 = "=A1*2";
            // A plain literal.
            ws.Range["E2"].Value2 = 42;
            _excel.Application.Calculate();

            var probes = new Dictionary<string, Probe>();
            foreach (var addr in new[] { "A1", "B1", "C2", "E1", "E2" })
            {
                var probe = Read(ws, addr);
                probes[addr] = probe;
                _output.WriteLine($"--- {addr} ---");
                _output.WriteLine(probe.ToString());
            }

            MeasureProbeCost(ws);

            // --- The contract GetSpill is built on -------------------------
            // Anchor: spills, owns the formula, and is its own SpillParent.
            Assert.True(probes["A1"].HasSpill);
            Assert.True(probes["A1"].HasFormula);
            Assert.Equal("=SEQUENCE(2,3)", probes["A1"].Formula2);
            Assert.Equal("A1", probes["A1"].SpillParent);
            Assert.Equal("A1:C2", probes["A1"].SpillingToRange);
            Assert.Equal(2, probes["A1"].SpillRows);
            Assert.Equal(3, probes["A1"].SpillColumns);

            // Children: HasSpill is true for them too (so today's `#` suffix
            // lands on a non-anchor cell), HasFormula is FALSE and Formula2
            // is empty (so the walker sees them as leaf inputs), SpillParent
            // resolves the anchor, and SpillingToRange is null -- the anchor
            // hop is mandatory, not a fallback.
            foreach (var child in new[] { "B1", "C2" })
            {
                Assert.True(probes[child].HasSpill);
                Assert.False(probes[child].HasFormula);
                Assert.Equal("", probes[child].Formula2);
                Assert.Equal("A1", probes[child].SpillParent);
                Assert.Null(probes[child].SpillingToRange);
            }

            // Plain cells: no spill, and SpillParent returns null rather than
            // throwing -- GetSpill can branch on null instead of on a catch.
            foreach (var plain in new[] { "E1", "E2" })
            {
                Assert.False(probes[plain].HasSpill);
                Assert.Null(probes[plain].SpillParent);
                Assert.Null(probes[plain].SpillingToRange);
            }

            Assert.True(probes["E1"].HasFormula);
            Assert.False(probes["E2"].HasFormula);
        }
        finally
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
    }

    private static Probe Read(dynamic ws, string addr)
    {
        dynamic cell = ws.Range[addr];
        try
        {
            var probe = new Probe
            {
                HasSpill = (bool)cell.HasSpill,
                HasFormula = (bool)cell.HasFormula,
                Formula2 = (string)cell.Formula2,
                HasArray = (bool)cell.HasArray
            };

            dynamic? parent = cell.SpillParent;
            if (parent != null)
                probe.SpillParent = (string)parent.Address[false, false];

            dynamic? rect = cell.SpillingToRange;
            if (rect != null)
            {
                probe.SpillingToRange = (string)rect.Address[false, false];
                probe.SpillRows = (int)rect.Rows.Count;
                probe.SpillColumns = (int)rect.Columns.Count;
            }

            return probe;
        }
        finally
        {
            Marshal.ReleaseComObject(cell);
        }
    }

    /// <summary>
    ///     Times the geometry probe on a spill child (SpillParent, then
    ///     SpillingToRange off the anchor -- the child's own SpillingToRange
    ///     is null), against the anchor's own single-range read and against
    ///     a plain cell's null SpillParent. Decides whether an anchor-keyed
    ///     memo is warranted.
    /// </summary>
    private void MeasureProbeCost(dynamic ws)
    {
        const int iterations = 200;

        var child = Time(iterations, () =>
        {
            dynamic cell = ws.Range["B1"];
            dynamic parent = cell.SpillParent;
            dynamic rect = parent.SpillingToRange;
            var _ = (int)rect.Rows.Count + (int)rect.Columns.Count + (int)parent.Row + (int)parent.Column;
        });

        var anchor = Time(iterations, () =>
        {
            dynamic cell = ws.Range["A1"];
            dynamic parent = cell.SpillParent;
            dynamic rect = cell.SpillingToRange;
            var _ = (int)rect.Rows.Count + (int)rect.Columns.Count + (int)parent.Row + (int)parent.Column;
        });

        // What a child probe would cost if the anchor's rectangle were memoed:
        // the anchor lookup still happens per cell, the geometry read doesn't.
        var childMemoed = Time(iterations, () =>
        {
            dynamic cell = ws.Range["B1"];
            dynamic parent = cell.SpillParent;
            var _ = (int)parent.Row + (int)parent.Column;
        });

        var plain = Time(iterations, () =>
        {
            dynamic cell = ws.Range["E2"];
            dynamic? parent = cell.SpillParent;
            var _ = parent == null;
        });

        var hasSpillOnly = Time(iterations, () =>
        {
            dynamic cell = ws.Range["B1"];
            var _ = (bool)cell.HasSpill;
        });

        _output.WriteLine("--- cost ---");
        _output.WriteLine($"child (anchor hop) = {child:F3} ms/op ({iterations} iterations)");
        _output.WriteLine($"anchor             = {anchor:F3} ms/op ({iterations} iterations)");
        _output.WriteLine($"child (memo hit)   = {childMemoed:F3} ms/op ({iterations} iterations)");
        _output.WriteLine($"plain cell         = {plain:F3} ms/op ({iterations} iterations)");
        _output.WriteLine($"HasSpill only      = {hasSpillOnly:F3} ms/op ({iterations} iterations)");
    }

    private static double Time(int iterations, Action body)
    {
        body(); // warm up
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            body();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iterations;
    }

    private sealed class Probe
    {
        public bool HasSpill { get; set; }
        public bool HasFormula { get; set; }
        public string Formula2 { get; set; } = "";
        public bool HasArray { get; set; }
        public string? SpillParent { get; set; }
        public string? SpillingToRange { get; set; }
        public int SpillRows { get; set; }
        public int SpillColumns { get; set; }

        public override string ToString()
        {
            return $"HasSpill        = {HasSpill}\n"
                   + $"HasFormula      = {HasFormula}\n"
                   + $"Formula2        = '{Formula2}'\n"
                   + $"HasArray        = {HasArray}\n"
                   + $"SpillParent     = {SpillParent ?? "<null>"}\n"
                   + $"SpillingToRange = {SpillingToRange ?? "<null>"}"
                   + (SpillingToRange == null ? "" : $" ({SpillRows}x{SpillColumns})");
        }
    }
}
