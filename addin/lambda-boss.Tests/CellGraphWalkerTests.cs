using Xunit;

namespace LambdaBoss.Tests;

public class CellGraphWalkerTests
{
    [Fact]
    public void Walk_LinearChain_ReturnsTopoOrder()
    {
        // A1 = 30, B1 = =A1*2, C1 = =B1+1
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=B1+1");

        var walked = CellGraphWalker.Walk(source.Ref("C1"), source).Cells!;
        var order = walked.Select(w => w.Ref.A1Address).ToList();

        Assert.Equal(new[] { "A1", "B1", "C1" }, order);
    }

    [Fact]
    public void Walk_LeafCellHasNullFormula_StillIncluded()
    {
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2");

        var walked = CellGraphWalker.Walk(source.Ref("B1"), source).Cells!;

        var leaf = walked.Single(w => w.Ref.A1Address == "A1");
        Assert.Null(leaf.Formula);
        Assert.Empty(leaf.Precedents);
    }

    [Fact]
    public void Walk_BranchedGraph_BothBranchesIncluded()
    {
        // A1, B1, C1 = =A1+B1
        var source = new StubCellSource()
            .WithFormula("C1", "=A1+B1");

        var walked = CellGraphWalker.Walk(source.Ref("C1"), source).Cells!;
        var addresses = walked.Select(w => w.Ref.A1Address).ToHashSet();

        Assert.Contains("A1", addresses);
        Assert.Contains("B1", addresses);
        Assert.Contains("C1", addresses);
        // Sink last in topo order.
        Assert.Equal("C1", walked[^1].Ref.A1Address);
    }

    [Fact]
    public void Walk_DiamondGraph_DoesNotDuplicate()
    {
        // A1, B1 = =A1, C1 = =A1*2, D1 = =B1+C1 (sink)
        var source = new StubCellSource()
            .WithFormula("B1", "=A1")
            .WithFormula("C1", "=A1*2")
            .WithFormula("D1", "=B1+C1");

        var walked = CellGraphWalker.Walk(source.Ref("D1"), source).Cells!;
        var addresses = walked.Select(w => w.Ref.A1Address).ToList();

        Assert.Equal(4, addresses.Count);
        Assert.Equal(new[] { "A1", "B1", "C1", "D1" }.OrderBy(s => s),
            addresses.OrderBy(s => s));

        // A1 must come before B1 and C1, both before D1.
        Assert.True(addresses.IndexOf("A1") < addresses.IndexOf("B1"));
        Assert.True(addresses.IndexOf("A1") < addresses.IndexOf("C1"));
        Assert.True(addresses.IndexOf("B1") < addresses.IndexOf("D1"));
        Assert.True(addresses.IndexOf("C1") < addresses.IndexOf("D1"));
    }

    [Fact]
    public void Walk_SinkWithNoFormula_ReturnsSingleLeaf()
    {
        // The walker doesn't itself decide whether to refuse — the engine
        // does. Walking a leaf-only sink just returns the sink as a leaf.
        var source = new StubCellSource();

        var walked = CellGraphWalker.Walk(source.Ref("A1"), source).Cells!;

        Assert.Single(walked);
        Assert.Null(walked[0].Formula);
    }

    [Fact]
    public void Walk_CrossSheetPrecedent_WalkedNormally()
    {
        // Sheet2!B1 = =Sheet1!A1*2; Sheet1!A1 is a literal on the other
        // sheet. The walker should reach across, identify A1 as a leaf,
        // and emit it before B1.
        var source = new StubCellSource("Sheet2")
            .WithFormula("Sheet2!B1", "=Sheet1!A1*2");

        var walked = CellGraphWalker.Walk(source.Ref("Sheet2!B1"), source).Cells!;

        Assert.Equal(2, walked.Count);
        Assert.Equal("Sheet1", walked[0].Ref.Sheet);
        Assert.Equal("A1", walked[0].Ref.A1Address);
        Assert.Null(walked[0].Formula);
        Assert.Equal("Sheet2", walked[1].Ref.Sheet);
        Assert.Equal("B1", walked[1].Ref.A1Address);
    }

    [Fact]
    public void Walk_CrossSheetStep_PrecedentResolvedOnTargetSheet()
    {
        // Sheet2!C1 = =Sheet1!B1+1; Sheet1!B1 = =A1*2 (unqualified A1
        // refers to Sheet1!A1, NOT Sheet2!A1).
        var source = new StubCellSource("Sheet2")
            .WithFormula("Sheet1!B1", "=A1*2")
            .WithFormula("Sheet2!C1", "=Sheet1!B1+1");

        var walked = CellGraphWalker.Walk(source.Ref("Sheet2!C1"), source).Cells!;

        var leaf = walked.Single(w => w.Ref.A1Address == "A1");
        Assert.Equal("Sheet1", leaf.Sheet());
        var step = walked.Single(w => w.Ref.A1Address == "B1");
        Assert.Equal("Sheet1", step.Sheet());
        Assert.Equal("=A1*2", step.Formula);
    }

    [Fact]
    public void Walk_ExternalRef_TreatedAsLeaf()
    {
        // External ref reaches the walker as a CellRef with ExternalWorkbook
        // set. The stub returns null for GetFormula on externals, so it
        // surfaces as a leaf with no precedents of its own.
        var source = new StubCellSource()
            .WithFormula("Sheet1!B1", "=[Other.xlsx]Sheet1!A1+1");

        var walked = CellGraphWalker.Walk(source.Ref("Sheet1!B1"), source).Cells!;

        var external = walked.Single(w => w.Ref.IsExternal);
        Assert.Equal("Other.xlsx", external.Ref.ExternalWorkbook);
        Assert.Equal("Sheet1", external.Ref.Sheet);
        Assert.Equal("A1", external.Ref.A1Address);
        Assert.Null(external.Formula);
        Assert.Empty(external.Precedents);
    }

    [Fact]
    public void Walk_RangePrecedent_DoesNotRecurseIntoRangeCells()
    {
        // PR 4: A1, A2, A3 each have a formula but only the range
        // references them. The walker must NOT include A1/A2/A3 as walked
        // cells (they're not directly referenced) — the range precedent is
        // an opaque leaf for the engine to promote.
        var source = new StubCellSource()
            .WithFormula("A1", "=RAND()")
            .WithFormula("A2", "=RAND()")
            .WithFormula("A3", "=RAND()")
            .WithFormula("B1", "=SUM(A1:A3)");

        var walked = CellGraphWalker.Walk(source.Ref("B1"), source).Cells!;

        Assert.Single(walked);
        Assert.Equal("B1", walked[0].Ref.A1Address);

        var precedents = walked[0].Precedents;
        Assert.Single(precedents);
        Assert.True(precedents[0].IsRange);
        Assert.Equal("A1", precedents[0].Start.A1Address);
        Assert.Equal("A3", precedents[0].End!.A1Address);
    }

    [Fact]
    public void Walk_RangeAndIndividualCell_IndividualCellWalkedSeparately()
    {
        // PR 4 acceptance: =SUM(A1:A3) + A4 — A4 is an individual ref and
        // must be walked, while the range stays opaque.
        var source = new StubCellSource()
            .WithFormula("B1", "=SUM(A1:A3) + A4");

        var walked = CellGraphWalker.Walk(source.Ref("B1"), source).Cells!;
        var addresses = walked.Select(w => w.Ref.A1Address).OrderBy(a => a).ToList();

        Assert.Equal(new[] { "A4", "B1" }, addresses);

        var b1 = walked.Single(w => w.Ref.A1Address == "B1");
        Assert.Equal(2, b1.Precedents.Count);
        Assert.Contains(b1.Precedents, p => p.IsRange && p.A1Address == "A1:A3");
        Assert.Contains(b1.Precedents, p => !p.IsRange && p.Start.A1Address == "A4");
    }

    [Fact]
    public void Walk_SpillRef_WalksIntoAnchorCell()
    {
        // PR 5: A1#=SEQUENCE(10) referenced via SUM(A1#). The walker
        // continues into A1 (the anchor) — A1 surfaces as a walked cell.
        var source = new StubCellSource()
            .WithFormula("A1", "=SEQUENCE(10)")
            .WithSpill("A1")
            .WithFormula("B1", "=SUM(A1#)");

        var walked = CellGraphWalker.Walk(source.Ref("B1"), source).Cells!;
        var addresses = walked.Select(w => w.Ref.A1Address).ToList();

        Assert.Equal(new[] { "A1", "B1" }, addresses);
        var anchor = walked.Single(w => w.Ref.A1Address == "A1");
        Assert.True(anchor.HasSpill);
        Assert.Equal("=SEQUENCE(10)", anchor.Formula);
    }

    [Fact]
    public void Walk_SpillAnchorWithInScopeRef_RecursesIntoPrecedents()
    {
        // Spill anchors aren't opaque — the walker treats them like any
        // other cell. B2 = SEQUENCE(A2) (spills) referenced via B2#; A2
        // must be walked since it's an in-scope precedent of B2. The
        // engine later decides B2 is a step and inlines its formula.
        var source = new StubCellSource()
            .WithFormula("A2", "=10")
            .WithFormula("B2", "=SEQUENCE(A2)")
            .WithSpill("B2")
            .WithFormula("C2", "=SUM(B2#)");

        var walked = CellGraphWalker.Walk(source.Ref("C2"), source).Cells!;
        var addresses = walked.Select(w => w.Ref.A1Address).ToList();

        Assert.Equal(new[] { "A2", "B2", "C2" }, addresses);
        Assert.True(walked.Single(w => w.Ref.A1Address == "B2").HasSpill);
        Assert.False(walked.Single(w => w.Ref.A1Address == "A2").HasSpill);
    }

    [Fact]
    public void Walk_NonSpillingCell_HasSpillFalse()
    {
        // Sanity check: cells that aren't spill anchors carry HasSpill=false
        // through the walker so the engine doesn't accidentally `#`-suffix
        // a plain leaf input.
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2");

        var walked = CellGraphWalker.Walk(source.Ref("B1"), source).Cells!;

        Assert.All(walked, w => Assert.False(w.HasSpill));
    }

    [Fact]
    public void Walk_TwoCycle_ReturnsCycleOutcome()
    {
        // PR 7: A1 = =B1+1, B1 = =A1+1 — sink A1. The walker discovers
        // both cells and then surfaces the back-edge during topo sort
        // instead of spinning forever in the iterative DFS.
        var source = new StubCellSource()
            .WithFormula("A1", "=B1+1")
            .WithFormula("B1", "=A1+1");

        var outcome = CellGraphWalker.Walk(source.Ref("A1"), source);

        Assert.True(outcome.IsCycle);
        Assert.Null(outcome.Cells);
        var addresses = outcome.Cycle!.Select(c => c.A1Address).ToHashSet();
        Assert.Equal(new HashSet<string> { "A1", "B1" }, addresses);
    }

    [Fact]
    public void Walk_ThreeCycle_ReturnsAllThreeCellsInOrder()
    {
        // A1 → B1 → C1 → A1 — the cycle list reads in path order from
        // the back-edge target through to the cell that closed the loop.
        var source = new StubCellSource()
            .WithFormula("A1", "=B1+1")
            .WithFormula("B1", "=C1+1")
            .WithFormula("C1", "=A1+1");

        var outcome = CellGraphWalker.Walk(source.Ref("A1"), source);

        Assert.True(outcome.IsCycle);
        var addresses = outcome.Cycle!.Select(c => c.A1Address).ToList();
        Assert.Equal(3, addresses.Count);
        Assert.Equal(new[] { "A1", "B1", "C1" }, addresses);
    }

    [Fact]
    public void Walk_CycleNotIncludingSink_StillDetected()
    {
        // Sink C1 → B1 ↔ A1 (the cycle excludes the sink). The walker
        // still surfaces the cycle because phase-2 DFS walks into it
        // from C1; nothing about cycle detection requires the sink to
        // be on the cycle.
        var source = new StubCellSource()
            .WithFormula("A1", "=B1+1")
            .WithFormula("B1", "=A1+1")
            .WithFormula("C1", "=B1+1");

        var outcome = CellGraphWalker.Walk(source.Ref("C1"), source);

        Assert.True(outcome.IsCycle);
        var addresses = outcome.Cycle!.Select(c => c.A1Address).ToHashSet();
        Assert.Contains("A1", addresses);
        Assert.Contains("B1", addresses);
        Assert.DoesNotContain("C1", addresses);
    }

    [Fact]
    public void Walk_AcyclicGraph_HasNoCycle()
    {
        // Regression guard: a normal acyclic walk surfaces Cells, not
        // a cycle outcome.
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=B1+1");

        var outcome = CellGraphWalker.Walk(source.Ref("C1"), source);

        Assert.False(outcome.IsCycle);
        Assert.NotNull(outcome.Cells);
        Assert.Null(outcome.Cycle);
    }
}

internal static class WalkedCellExtensions
{
    public static string Sheet(this WalkedCell cell)
    {
        return cell.Ref.Sheet;
    }
}