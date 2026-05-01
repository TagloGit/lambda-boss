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
}
