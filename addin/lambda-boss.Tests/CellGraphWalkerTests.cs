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

        var walked = CellGraphWalker.Walk(source.Ref("C1"), source);
        var order = walked.Select(w => w.Ref.A1Address).ToList();

        Assert.Equal(new[] { "A1", "B1", "C1" }, order);
    }

    [Fact]
    public void Walk_LeafCellHasNullFormula_StillIncluded()
    {
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2");

        var walked = CellGraphWalker.Walk(source.Ref("B1"), source);

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

        var walked = CellGraphWalker.Walk(source.Ref("C1"), source);
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

        var walked = CellGraphWalker.Walk(source.Ref("D1"), source);
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

        var walked = CellGraphWalker.Walk(source.Ref("A1"), source);

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

        var walked = CellGraphWalker.Walk(source.Ref("Sheet2!B1"), source);

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

        var walked = CellGraphWalker.Walk(source.Ref("Sheet2!C1"), source);

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

        var walked = CellGraphWalker.Walk(source.Ref("Sheet1!B1"), source);

        var external = walked.Single(w => w.Ref.IsExternal);
        Assert.Equal("Other.xlsx", external.Ref.ExternalWorkbook);
        Assert.Equal("Sheet1", external.Ref.Sheet);
        Assert.Equal("A1", external.Ref.A1Address);
        Assert.Null(external.Formula);
        Assert.Empty(external.Precedents);
    }
}

internal static class WalkedCellExtensions
{
    public static string Sheet(this WalkedCell cell)
    {
        return cell.Ref.Sheet;
    }
}