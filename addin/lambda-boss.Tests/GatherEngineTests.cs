using Xunit;

namespace LambdaBoss.Tests;

public class GatherEngineTests
{
    [Fact]
    public void Gather_NoFormulaSink_ReturnsNull()
    {
        var source = new StubCellSource();

        var result = GatherEngine.Gather(source.Ref("A1"), source);

        Assert.Null(result);
    }

    [Fact]
    public void Gather_SimpleChain_BuildsLetWithLabelAndStepFallback()
    {
        // A1 = "Numbers" (label), A2 = 30 (input), B2 = =A2*2 (no label
        // because B1 is empty), C2 = =B2+1 (sink, no label).
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("B2", "=A2*2")
            .WithFormula("C2", "=B2+1");

        var result = GatherEngine.Gather(source.Ref("C2"), source);

        Assert.NotNull(result);
        Assert.Equal("=B2+1", result.OriginalFormula);

        // A2 is an input named "Numbers" (cell-above A1 is "Numbers"),
        // B2 is a step named step_1 (cell-above B1 is empty).
        Assert.Equal(2, result.Bindings.Count);

        var aRow = result.Bindings.Single(b => b.CellRef.A1Address == "A2");
        Assert.Equal(BindingRole.Input, aRow.Role);
        Assert.Equal("Numbers", aRow.Name);
        Assert.Equal("A2", aRow.Rhs);

        var bRow = result.Bindings.Single(b => b.CellRef.A1Address == "B2");
        Assert.Equal(BindingRole.Step, bRow.Role);
        Assert.Equal("step_1", bRow.Name);
        Assert.Equal("Numbers*2", bRow.Rhs);

        // Inputs come before steps.
        var bindings = result.Bindings.ToList();
        Assert.True(bindings.IndexOf(aRow) < bindings.IndexOf(bRow));

        // The synthesised LET round-trips through LetParser.
        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal(2, parsed.Bindings.Count);
        Assert.Equal("Numbers", parsed.Bindings[0].Name);
        Assert.Equal("A2", parsed.Bindings[0].RhsText);
        Assert.Equal("step_1", parsed.Bindings[1].Name);
        Assert.Equal("Numbers*2", parsed.Bindings[1].RhsText);
        Assert.Equal("step_1+1", parsed.Body);
    }

    [Fact]
    public void Gather_BranchedGraph_BothBranchesAppearAsBindings()
    {
        // A1, B1 inputs; C1 = A1+B1 sink.
        var source = new StubCellSource()
            .WithFormula("C1", "=A1+B1");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        Assert.Equal(2, result.Bindings.Count);
        Assert.All(result.Bindings, b => Assert.Equal(BindingRole.Input, b.Role));
        var addrs = result.Bindings.Select(b => b.CellRef.A1Address).ToList();
        Assert.Contains("A1", addrs);
        Assert.Contains("B1", addrs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal(2, parsed.Bindings.Count);
        // Body refers to the binding names, not the original cell refs.
        var names = result.Bindings.Select(b => b.Name).ToList();
        Assert.Equal($"{names[0]}+{names[1]}", parsed.Body);
    }

    [Fact]
    public void Gather_LabelAboveNaming_UsedWhenIdentifierShape()
    {
        // A1 = "input_value" (label), A2 input, B2 sink.
        var source = new StubCellSource()
            .WithLabel("A1", "input_value")
            .WithFormula("B2", "=A2+10");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        Assert.Single(result.Bindings);
        Assert.Equal("input_value", result.Bindings[0].Name);
    }

    [Fact]
    public void Gather_NonIdentifierLabel_FallsBackToStepN()
    {
        // Cell-above "Customer ID" has a space so doesn't match the
        // identifier regex; PR 1 falls through to step_N. Sanitizer
        // (which would convert "Customer ID" → "customerId") lands in PR 2.
        var source = new StubCellSource()
            .WithLabel("A1", "Customer ID")
            .WithFormula("B2", "=A2*2");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        Assert.Single(result.Bindings);
        Assert.Equal("step_1", result.Bindings[0].Name);
    }

    [Fact]
    public void Gather_MultipleUnlabelledCells_NumberedInTopoOrder()
    {
        // A1 → B1 → C1 (sink). All unlabelled (and row 1 has no row above
        // anyway). A1 = step_1, B1 = step_2 in topo order.
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=B1+1");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        var aRow = result.Bindings.Single(b => b.CellRef.A1Address == "A1");
        var bRow = result.Bindings.Single(b => b.CellRef.A1Address == "B1");
        Assert.Equal("step_1", aRow.Name);
        Assert.Equal("step_2", bRow.Name);
    }

    [Fact]
    public void Gather_NumericLabel_FallsBackToStepN()
    {
        // Label "30" doesn't match the identifier regex (starts with digit).
        var source = new StubCellSource()
            .WithLabel("A1", "30")
            .WithFormula("B2", "=A2*2");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        Assert.Equal("step_1", result.Bindings[0].Name);
    }

    [Fact]
    public void Gather_SynthesisedLet_StartsWithEqualsLet()
    {
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2");

        var result = GatherEngine.Gather(source.Ref("B1"), source)!;

        Assert.StartsWith("=LET(", result.SynthesisedLet);
        Assert.EndsWith(")", result.SynthesisedLet);
    }

    [Fact]
    public void Gather_FormulaWithoutInScopeRefs_TreatedAsInput()
    {
        // B1 = =SUM(1, 2) — formula with no cell refs. Per the spec table
        // ("Formula, references no in-scope cells, doesn't spill" → input,
        // RHS = A1) this becomes an input whose RHS is the cell address,
        // not the formula text.
        var source = new StubCellSource()
            .WithFormula("B1", "=SUM(1, 2)")
            .WithFormula("C1", "=B1+10");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        var bRow = result.Bindings.Single(b => b.CellRef.A1Address == "B1");
        Assert.Equal(BindingRole.Input, bRow.Role);
        Assert.Equal("B1", bRow.Rhs);
    }
}