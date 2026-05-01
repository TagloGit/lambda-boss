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

        // A2 is an input named "numbers" (cell-above A1 is "Numbers", which
        // sanitises to lowercase initial); B2 is a step named step_1 (cell-
        // above B1 is empty, no cell-left either).
        Assert.Equal(2, result.Bindings.Count);

        var aRow = result.Bindings.Single(b => b.Source.A1Address == "A2");
        Assert.Equal(BindingRole.Input, aRow.Role);
        Assert.Equal("numbers", aRow.Name);
        Assert.Equal("A2", aRow.Rhs);

        var bRow = result.Bindings.Single(b => b.Source.A1Address == "B2");
        Assert.Equal(BindingRole.Step, bRow.Role);
        Assert.Equal("step_1", bRow.Name);
        Assert.Equal("numbers*2", bRow.Rhs);

        // Inputs come before steps.
        var bindings = result.Bindings.ToList();
        Assert.True(bindings.IndexOf(aRow) < bindings.IndexOf(bRow));

        // The synthesised LET round-trips through LetParser.
        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal(2, parsed.Bindings.Count);
        Assert.Equal("numbers", parsed.Bindings[0].Name);
        Assert.Equal("A2", parsed.Bindings[0].RhsText);
        Assert.Equal("step_1", parsed.Bindings[1].Name);
        Assert.Equal("numbers*2", parsed.Bindings[1].RhsText);
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
        var addrs = result.Bindings.Select(b => b.Source.A1Address).ToList();
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
    public void Gather_LabelWithSpaces_SanitizedToCamelCase()
    {
        // "Customer ID" sanitises to camelCase rather than falling through
        // to step_N (PR 1 fell through; PR 2 wires the sanitizer in).
        var source = new StubCellSource()
            .WithLabel("A1", "Customer ID")
            .WithFormula("B2", "=A2*2");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        Assert.Single(result.Bindings);
        Assert.Equal("customerId", result.Bindings[0].Name);
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

        var aRow = result.Bindings.Single(b => b.Source.A1Address == "A1");
        var bRow = result.Bindings.Single(b => b.Source.A1Address == "B1");
        Assert.Equal("step_1", aRow.Name);
        Assert.Equal("step_2", bRow.Name);
    }

    [Fact]
    public void Gather_LabelStartingWithDigit_PrefixedWithUnderscore()
    {
        // A leading digit can't begin an Excel name; the sanitizer prefixes
        // with `_` so the result is still usable.
        var source = new StubCellSource()
            .WithLabel("A1", "30")
            .WithFormula("B2", "=A2*2");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        Assert.Equal("_30", result.Bindings[0].Name);
    }

    [Fact]
    public void Gather_LabelWithOnlyPunctuation_FallsBackToStepN()
    {
        // No identifier characters at all — sanitizer returns null, so the
        // engine falls through to the next naming level (cell-left here is
        // also empty) and finally to step_N.
        var source = new StubCellSource()
            .WithLabel("A1", "!!!")
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

        var bRow = result.Bindings.Single(b => b.Source.A1Address == "B1");
        Assert.Equal(BindingRole.Input, bRow.Role);
        Assert.Equal("B1", bRow.Rhs);
    }

    [Fact]
    public void Gather_CellLeftLabel_UsedWhenCellAboveEmpty()
    {
        // A2 holds the label "Numbers"; B2 is the input (no cell-above text).
        // The naming chain falls through to cell-left.
        var source = new StubCellSource()
            .WithLabel("A2", "Numbers")
            .WithFormula("C2", "=B2*2");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        var bRow = result.Bindings.Single(b => b.Source.A1Address == "B2");
        Assert.Equal("numbers", bRow.Name);
    }

    [Fact]
    public void Gather_CellAbovePreferredOverCellLeft()
    {
        // Both neighbours have a label; cell-above wins.
        var source = new StubCellSource()
            .WithLabel("B1", "Above")
            .WithLabel("A2", "Left")
            .WithFormula("C2", "=B2*2");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        var bRow = result.Bindings.Single(b => b.Source.A1Address == "B2");
        Assert.Equal("above", bRow.Name);
    }

    [Fact]
    public void Gather_CellAbovePunctuationOnly_FallsThroughToCellLeft()
    {
        // Cell-above sanitises to null (no identifier chars), so cell-left
        // gets a turn before step_N.
        var source = new StubCellSource()
            .WithLabel("B1", "!!!")
            .WithLabel("A2", "Numbers")
            .WithFormula("C2", "=B2*2");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        var bRow = result.Bindings.Single(b => b.Source.A1Address == "B2");
        Assert.Equal("numbers", bRow.Name);
    }

    [Fact]
    public void Gather_CollidingSanitizedNames_DisambiguatedWithSuffix()
    {
        // A1 and A2 both labelled "Sales"; their cells (B1, B2) both want
        // the binding name `sales`. Topological order picks B1 first, so
        // B2 gets `sales_2`.
        var source = new StubCellSource()
            .WithLabel("A1", "Sales")
            .WithLabel("A2", "Sales")
            .WithFormula("C1", "=B1+B2");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        var b1 = result.Bindings.Single(b => b.Source.A1Address == "B1");
        var b2 = result.Bindings.Single(b => b.Source.A1Address == "B2");
        var names = new[] { b1.Name, b2.Name }.OrderBy(n => n).ToList();
        Assert.Equal(new[] { "sales", "sales_2" }, names);
    }

    [Fact]
    public void Gather_ThreeWayCollision_SuffixesIncrement()
    {
        // Three cells whose labels all sanitise to `x` should produce
        // `x`, `x_2`, `x_3` in topological order.
        var source = new StubCellSource()
            .WithLabel("A1", "X")
            .WithLabel("A2", "X")
            .WithLabel("A3", "X")
            .WithFormula("D1", "=B1+B2+B3");

        var result = GatherEngine.Gather(source.Ref("D1"), source)!;

        var names = new[] { "B1", "B2", "B3" }
            .Select(addr => result.Bindings.Single(b => b.Source.A1Address == addr).Name)
            .OrderBy(n => n)
            .ToList();
        Assert.Equal(new[] { "x", "x_2", "x_3" }, names);
    }

    [Fact]
    public void Gather_FallbackSkipsNamesAlreadyUsedByLabels()
    {
        // A label sanitises to "step_1"; the next unlabelled cell that
        // would otherwise grab `step_1` should skip to `step_2`.
        var source = new StubCellSource()
            .WithLabel("A1", "step_1")
            .WithFormula("C1", "=B1+B2");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        var b1 = result.Bindings.Single(b => b.Source.A1Address == "B1");
        var b2 = result.Bindings.Single(b => b.Source.A1Address == "B2");
        Assert.Equal("step_1", b1.Name);
        Assert.Equal("step_2", b2.Name);
    }

    [Fact]
    public void Gather_CrossSheetSink_RhsUsesSheetQualifiedAddress()
    {
        // Sheet2!C1 = =Sheet1!A1*2. The Sheet1!A1 binding must keep the
        // sheet qualifier in its RHS since the LET lives on Sheet2.
        var source = new StubCellSource("Sheet2")
            .WithFormula("Sheet2!C1", "=Sheet1!A1*2");

        var result = GatherEngine.Gather(source.Ref("Sheet2!C1"), source)!;

        var aRow = result.Bindings.Single(b => b.Source.A1Address == "A1");
        Assert.Equal(BindingRole.Input, aRow.Role);
        Assert.Equal("Sheet1!A1", aRow.Rhs);

        // Body refers to the binding name, not the cross-sheet ref.
        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Single(parsed.Bindings);
        Assert.Equal("Sheet1!A1", parsed.Bindings[0].RhsText);
        Assert.Equal($"{aRow.Name}*2", parsed.Body);
    }

    [Fact]
    public void Gather_CrossSheetStep_RewritesRefToBindingName()
    {
        // Sheet2!C1 = =Sheet1!B1+1; Sheet1!B1 = =A1*2 (Sheet1's A1).
        // The step's RHS should rewrite both Sheet1!B1 (in C1) and A1 (in
        // B1) — the latter uses Sheet1 as default since the formula lives
        // on Sheet1.
        var source = new StubCellSource("Sheet2")
            .WithLabel("Sheet1!A1", "Numbers")
            .WithFormula("Sheet1!B1", "=A1*2")
            .WithFormula("Sheet2!C1", "=Sheet1!B1+1");

        var result = GatherEngine.Gather(source.Ref("Sheet2!C1"), source)!;

        var bRow = result.Bindings.Single(b => b.Source.A1Address == "B1");
        Assert.Equal(BindingRole.Step, bRow.Role);
        // The step's RHS uses the leaf binding name, not the cross-sheet
        // form `Sheet1!A1` it had in the source formula.
        var aRow = result.Bindings.Single(b => b.Source.A1Address == "A1");
        Assert.Equal($"{aRow.Name}*2", bRow.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal($"{bRow.Name}+1", parsed.Body);
    }

    [Fact]
    public void Gather_QuotedSheetName_PreservedInBindingRhs()
    {
        // Sheet name with a space forces quoting in the synthesised LET.
        var source = new StubCellSource("Sheet2")
            .WithFormula("Sheet2!B1", "='My Sheet'!A1*2");

        var result = GatherEngine.Gather(source.Ref("Sheet2!B1"), source)!;

        var aRow = result.Bindings.Single(b => b.Source.A1Address == "A1");
        Assert.Equal("My Sheet", aRow.Source.Sheet);
        Assert.Equal("'My Sheet'!A1", aRow.Rhs);

        // Round-trips through LetParser despite the spaced sheet name —
        // the parser's bracket-aware comma splitter ignores the bang and
        // single-quote pair inside the RHS.
        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("'My Sheet'!A1", parsed.Bindings[0].RhsText);
    }

    [Fact]
    public void Gather_ExternalRef_LeftAsLeafInputWithWorkbookTag()
    {
        // External-workbook ref shows up as an input — we never reach into
        // the other workbook — and its RHS is the workbook-qualified
        // address, ready to evaluate when Excel rebinds it.
        var source = new StubCellSource()
            .WithFormula("Sheet1!B1", "=[Other.xlsx]Sheet1!A1+1");

        var result = GatherEngine.Gather(source.Ref("Sheet1!B1"), source)!;

        var ext = result.Bindings.Single(b => b.Source.IsExternal);
        Assert.Equal(BindingRole.Input, ext.Role);
        Assert.Equal("[Other.xlsx]Sheet1!A1", ext.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        // The body still references the binding name, not the original
        // external ref text — the external sub-tree is just an input.
        Assert.Equal($"{ext.Name}+1", parsed.Body);
    }

    [Fact]
    public void Gather_QuotedExternalRef_RhsRoundTripsAsQualifiedForm()
    {
        // External + spaced sheet → quoted entire qualifier on emit.
        var source = new StubCellSource()
            .WithFormula("Sheet1!B1", "='[Other.xlsx]My Sheet'!A1+1");

        var result = GatherEngine.Gather(source.Ref("Sheet1!B1"), source)!;

        var ext = result.Bindings.Single(b => b.Source.IsExternal);
        Assert.Equal("'[Other.xlsx]My Sheet'!A1", ext.Rhs);
    }

    [Fact]
    public void Gather_NamingMatchesIssueManualTestScenario()
    {
        // From issue 132 acceptance — combines sanitizer, cell-left, and
        // collision suffixing in one walk:
        //   A1=Sales,    B1=100
        //   A2=Sales,    B2=200
        //   A3=Tax Rate, B3=0.1
        //   A4=Total,    B4=B1*B3 + B2*B3
        //   C4 = B4+1                       ← sink
        var source = new StubCellSource()
            .WithLabel("A1", "Sales")
            .WithLabel("A2", "Sales")
            .WithLabel("A3", "Tax Rate")
            .WithLabel("A4", "Total")
            .WithFormula("B4", "=B1*B3 + B2*B3")
            .WithFormula("C4", "=B4+1");

        var result = GatherEngine.Gather(source.Ref("C4"), source)!;

        Assert.Equal("sales", result.Bindings.Single(b => b.Source.A1Address == "B1").Name);
        Assert.Equal("sales_2", result.Bindings.Single(b => b.Source.A1Address == "B2").Name);
        Assert.Equal("taxRate", result.Bindings.Single(b => b.Source.A1Address == "B3").Name);
        Assert.Equal("total", result.Bindings.Single(b => b.Source.A1Address == "B4").Name);

        // Sanity: synthesised LET round-trips and the body uses the step
        // binding name rather than the cell ref.
        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("total+1", parsed.Body);
    }

    [Fact]
    public void Gather_RangeRef_PromotedToSingleInputBinding()
    {
        // PR 4 acceptance: =SUM(A1:A3) with A1/A2/A3 having formulas →
        // range A1:A3 becomes the single binding; A1/A2/A3 are NOT
        // walked (only the range references them) so they don't appear
        // as separate bindings.
        var source = new StubCellSource()
            .WithFormula("A1", "=RAND()")
            .WithFormula("A2", "=RAND()")
            .WithFormula("A3", "=RAND()")
            .WithFormula("B1", "=SUM(A1:A3)");

        var result = GatherEngine.Gather(source.Ref("B1"), source)!;

        Assert.Single(result.Bindings);
        var rangeRow = result.Bindings[0];
        Assert.Equal(BindingRole.Input, rangeRow.Role);
        Assert.True(rangeRow.Source.IsRange);
        Assert.Equal("A1:A3", rangeRow.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Single(parsed.Bindings);
        Assert.Equal("A1:A3", parsed.Bindings[0].RhsText);
        Assert.Equal($"SUM({rangeRow.Name})", parsed.Body);
    }

    [Fact]
    public void Gather_RangeAndIndividualCell_BothBecomeBindings()
    {
        // PR 4 acceptance: =SUM(A1:A3) + A4 walks A4 separately while
        // still promoting the range. A4 lands as its own input binding;
        // the range stays as a single leaf input.
        var source = new StubCellSource()
            .WithFormula("B1", "=SUM(A1:A3) + A4");

        var result = GatherEngine.Gather(source.Ref("B1"), source)!;

        Assert.Equal(2, result.Bindings.Count);
        var rangeRow = result.Bindings.Single(b => b.Source.IsRange);
        var a4Row = result.Bindings.Single(b => !b.Source.IsRange);
        Assert.Equal(BindingRole.Input, rangeRow.Role);
        Assert.Equal("A1:A3", rangeRow.Rhs);
        Assert.Equal(BindingRole.Input, a4Row.Role);
        Assert.Equal("A4", a4Row.Source.A1Address);
        Assert.Equal("A4", a4Row.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal(2, parsed.Bindings.Count);
        // Body refers to the bindings, not the source refs. Whitespace
        // between operands is preserved by the rewriter.
        Assert.Equal($"SUM({rangeRow.Name}) + {a4Row.Name}", parsed.Body);
    }

    [Fact]
    public void Gather_RangeFullCoverage_DropsEveryCoveredCell()
    {
        // Full coverage: A1/A2/A3 are each in-scope (referenced directly
        // by B1) AND covered by the sink's range. All three drop; B1
        // loses every precedent and reverts to an input.
        var source = new StubCellSource()
            .WithFormula("B1", "=A1+A2+A3")
            .WithFormula("C1", "=SUM(A1:A3) + B1");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        var addresses = result.Bindings.Select(b => b.Source.A1Address).ToList();
        Assert.DoesNotContain("A1", addresses);
        Assert.DoesNotContain("A2", addresses);
        Assert.DoesNotContain("A3", addresses);
        Assert.Contains("A1:A3", addresses);
        Assert.Contains("B1", addresses);

        var b1Row = result.Bindings.Single(b => b.Source.A1Address == "B1");
        Assert.Equal(BindingRole.Input, b1Row.Role);
    }

    [Fact]
    public void Gather_RangePartialCoverage_DropsCoveredCellFromBindings()
    {
        // Partial coverage: B1 references A2 directly (so A2 is in-scope),
        // and the sink references A1:A3 (which covers A2). The covered A2
        // must drop from the bindings; B1 keeps its place but loses its
        // only in-scope precedent and reverts to an input.
        var source = new StubCellSource()
            .WithFormula("B1", "=A2*5")
            .WithFormula("C1", "=SUM(A1:A3) + B1");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        var addresses = result.Bindings.Select(b => b.Source.A1Address).ToList();
        Assert.DoesNotContain("A2", addresses);
        Assert.Contains("A1:A3", addresses);
        Assert.Contains("B1", addresses);

        var rangeRow = result.Bindings.Single(b => b.Source.IsRange);
        Assert.Equal(BindingRole.Input, rangeRow.Role);

        var b1Row = result.Bindings.Single(b => !b.Source.IsRange);
        // B1's only precedent (A2) was dropped, so it has no in-scope
        // precedents and reverts to an input bound to its cell address.
        Assert.Equal(BindingRole.Input, b1Row.Role);
        Assert.Equal("B1", b1Row.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal($"SUM({rangeRow.Name}) + {b1Row.Name}", parsed.Body);
    }

    [Fact]
    public void Gather_MultiRowRange_RecognisedAsSingleBinding()
    {
        // Two-dimensional range A1:C3 still promotes to one binding, with
        // the literal range text in the RHS so Excel evaluates it as the
        // same array.
        var source = new StubCellSource()
            .WithFormula("D1", "=SUM(A1:C3)");

        var result = GatherEngine.Gather(source.Ref("D1"), source)!;

        Assert.Single(result.Bindings);
        Assert.True(result.Bindings[0].Source.IsRange);
        Assert.Equal("A1:C3", result.Bindings[0].Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("A1:C3", parsed.Bindings[0].RhsText);
    }

    [Fact]
    public void Gather_CrossSheetRange_RhsKeepsSheetQualifier()
    {
        // Sheet2!B1 = =SUM(Sheet1!A1:A3) — the binding RHS must keep the
        // sheet qualifier so the LET works when written into Sheet2.
        var source = new StubCellSource("Sheet2")
            .WithFormula("Sheet2!B1", "=SUM(Sheet1!A1:A3)");

        var result = GatherEngine.Gather(source.Ref("Sheet2!B1"), source)!;

        Assert.Single(result.Bindings);
        var rangeRow = result.Bindings[0];
        Assert.True(rangeRow.Source.IsRange);
        Assert.Equal("Sheet1", rangeRow.Source.Sheet);
        Assert.Equal("Sheet1!A1:A3", rangeRow.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("Sheet1!A1:A3", parsed.Bindings[0].RhsText);
        Assert.Equal($"SUM({rangeRow.Name})", parsed.Body);
    }

    [Fact]
    public void Gather_RangeWithLabelAbove_NamedFromHeader()
    {
        // The range's label is the cell directly above its top-left
        // corner — common in real spreadsheets where the header sits one
        // row above the data.
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("B1", "=SUM(A2:A4)");

        var result = GatherEngine.Gather(source.Ref("B1"), source)!;

        Assert.Single(result.Bindings);
        var row = result.Bindings[0];
        Assert.True(row.Source.IsRange);
        Assert.Equal("numbers", row.Name);
    }
}