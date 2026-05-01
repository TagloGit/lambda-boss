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

    [Fact]
    public void Gather_SpillAnchorReferencedViaHash_RhsKeepsHash()
    {
        // PR 5 acceptance: A2=SEQUENCE(10) labelled "Numbers" (header at A1),
        // B2=SUM(A2#) sink → =LET(numbers, A2#, SUM(numbers)). Anchor's
        // RHS keeps the `#`; the body rewrites `A2#` to the bare binding
        // name (no trailing `#` — the binding IS the array).
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("A2", "=SEQUENCE(10)")
            .WithSpill("A2")
            .WithFormula("B2", "=SUM(A2#)");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        Assert.Single(result.Bindings);
        var aRow = result.Bindings[0];
        Assert.Equal(BindingRole.Input, aRow.Role);
        Assert.Equal("A2", aRow.Source.A1Address);
        Assert.Equal("numbers", aRow.Name);
        Assert.Equal("A2#", aRow.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Single(parsed.Bindings);
        Assert.Equal("A2#", parsed.Bindings[0].RhsText);
        Assert.Equal("SUM(numbers)", parsed.Body);
    }

    [Fact]
    public void Gather_SpillAnchorInChain_StillBindsAsInputWithHash()
    {
        // Chain: A2 spills, B2 = SUM(A2#), C2 = B2 + 1 (sink). A2 is the
        // anchor leaf input (RHS A2#), B2 is a step that references the
        // anchor's binding name (no `#`).
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("A2", "=SEQUENCE(10)")
            .WithSpill("A2")
            .WithFormula("B2", "=SUM(A2#)")
            .WithFormula("C2", "=B2+1");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        var aRow = result.Bindings.Single(b => b.Source.A1Address == "A2");
        Assert.Equal(BindingRole.Input, aRow.Role);
        Assert.Equal("A2#", aRow.Rhs);
        Assert.Equal("numbers", aRow.Name);

        var bRow = result.Bindings.Single(b => b.Source.A1Address == "B2");
        Assert.Equal(BindingRole.Step, bRow.Role);
        Assert.Equal($"SUM({aRow.Name})", bRow.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal($"{bRow.Name}+1", parsed.Body);
    }

    [Fact]
    public void Gather_SpillAnchorWithInScopePrecedent_ClassifiedAsStep()
    {
        // A spilling cell whose formula references in-scope cells is a
        // step like any other formula-with-precedents cell. Its RHS is
        // the rewritten formula — the array semantics flow through the
        // LET because the inner expression (e.g. SEQUENCE) still returns
        // an array regardless of how it's bound. The cell's HasSpill flag
        // only matters for inputs, where it suffixes `#` on the RHS.
        var source = new StubCellSource()
            .WithLabel("A1", "Count")
            .WithFormula("A2", "=10")
            .WithLabel("B1", "Numbers")
            .WithFormula("B2", "=SEQUENCE(A2)")
            .WithSpill("B2")
            .WithFormula("C2", "=SUM(B2#)");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        var aRow = result.Bindings.Single(b => b.Source.A1Address == "A2");
        Assert.Equal(BindingRole.Input, aRow.Role);
        Assert.Equal("count", aRow.Name);
        Assert.Equal("A2", aRow.Rhs);
        Assert.DoesNotContain("#", aRow.Rhs);

        var bRow = result.Bindings.Single(b => b.Source.A1Address == "B2");
        Assert.Equal(BindingRole.Step, bRow.Role);
        Assert.Equal("numbers", bRow.Name);
        Assert.Equal($"SEQUENCE({aRow.Name})", bRow.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal($"SUM({bRow.Name})", parsed.Body);
    }

    [Fact]
    public void Gather_NonSpillingCell_RhsHasNoHash()
    {
        // Regression guard: a plain leaf input must NOT pick up a stray `#`.
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("B2", "=A2*2");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        var aRow = result.Bindings.Single(b => b.Source.A1Address == "A2");
        Assert.Equal("A2", aRow.Rhs);
        Assert.DoesNotContain("#", aRow.Rhs);
    }

    [Fact]
    public void Gather_NestedLetWithoutCollision_BindingsSplicedInOrder()
    {
        // PR 6 acceptance #1: a step whose formula is =LET(a, A1+1, a*2)
        // and no outer name `a` produces an inner binding `a, A1_rewritten+1`
        // ahead of the step row, with the step's RHS being the inner body
        // `a*2`. A1 is the only outer leaf and gets the auto-name step_1.
        var source = new StubCellSource()
            .WithFormula("B1", "=LET(a, A1+1, a*2)")
            .WithFormula("C1", "=B1+5");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        var inner = result.Bindings.Single(b => b.Name == "a");
        Assert.Equal(BindingRole.Step, inner.Role);
        Assert.Equal("step_1+1", inner.Rhs);

        var stepB1 = result.Bindings.Single(b => b.Source.A1Address == "B1" && b.Name != "a");
        Assert.Equal(BindingRole.Step, stepB1.Role);
        Assert.Equal("a*2", stepB1.Rhs);

        // Inner binding sits between the outer input and the outer step.
        var bindings = result.Bindings.ToList();
        var aIdx = bindings.FindIndex(b => b.Source.A1Address == "A1");
        var innerIdx = bindings.IndexOf(inner);
        var stepIdx = bindings.IndexOf(stepB1);
        Assert.True(aIdx < innerIdx && innerIdx < stepIdx);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal(3, parsed.Bindings.Count);
        Assert.Equal("step_1", parsed.Bindings[0].Name);
        Assert.Equal("a", parsed.Bindings[1].Name);
        Assert.Equal("step_1+1", parsed.Bindings[1].RhsText);
        Assert.Equal("a*2", parsed.Bindings[2].RhsText);
    }

    [Fact]
    public void Gather_NestedLetWithCollidingName_AutoSuffixedAndRefsRewritten()
    {
        // PR 6 acceptance #2: inner binding `x` collides with outer `x`
        // (A2 labelled "x" via the cell above at A1) and is silently
        // renamed to `x_2`. Inside the inner LET, references to `x` (in
        // the body) become `x_2`; the inner binding's RHS rewrites the
        // outer cell ref `A2` to its outer binding name `x` (which is
        // unaffected by the inner-rename pass because cell-ref rewriting
        // runs second).
        var source = new StubCellSource()
            .WithLabel("A1", "x")
            .WithFormula("B2", "=LET(x, A2*2, x+5)")
            .WithFormula("C2", "=B2*2");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        var outerX = result.Bindings.Single(b => b.Source.A1Address == "A2");
        Assert.Equal("x", outerX.Name);

        var innerX2 = result.Bindings.Single(b => b.Name == "x_2");
        Assert.Equal("x*2", innerX2.Rhs);

        var stepB2 = result.Bindings.Single(b => b.Source.A1Address == "B2" && b.Name != "x_2");
        Assert.Equal("step_1", stepB2.Name);
        Assert.Equal("x_2+5", stepB2.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal(3, parsed.Bindings.Count);
        Assert.Equal("x", parsed.Bindings[0].Name);
        Assert.Equal("A2", parsed.Bindings[0].RhsText);
        Assert.Equal("x_2", parsed.Bindings[1].Name);
        Assert.Equal("x*2", parsed.Bindings[1].RhsText);
        Assert.Equal("step_1", parsed.Bindings[2].Name);
        Assert.Equal("x_2+5", parsed.Bindings[2].RhsText);
        Assert.Equal("step_1*2", parsed.Body);
    }

    [Fact]
    public void Gather_TwoStepsWithSameInnerName_SecondGetsSuffix()
    {
        // PR 6 acceptance #3: two steps each with =LET(x, ..., x+1). The
        // first expansion claims `x`; the second collides against `x`
        // already in `used` and becomes `x_2`. The outer `B1`/`C1` are
        // step-classified because each references an in-scope precedent.
        var source = new StubCellSource()
            .WithFormula("B1", "=LET(x, A1+1, x+1)")
            .WithFormula("C1", "=LET(x, B1+1, x+1)")
            .WithFormula("D1", "=C1+1");

        var result = GatherEngine.Gather(source.Ref("D1"), source)!;

        var firstInner = result.Bindings.Single(b => b.Name == "x");
        var secondInner = result.Bindings.Single(b => b.Name == "x_2");

        Assert.Equal("B1", firstInner.Source.A1Address);
        Assert.Equal("C1", secondInner.Source.A1Address);

        var stepB1 = result.Bindings.Single(b => b.Source.A1Address == "B1" && b.Name != "x");
        Assert.Equal("x+1", stepB1.Rhs);

        var stepC1 = result.Bindings.Single(b => b.Source.A1Address == "C1" && b.Name != "x_2");
        Assert.Equal("x_2+1", stepC1.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        var names = parsed.Bindings.Select(b => b.Name).ToList();
        Assert.Contains("x", names);
        Assert.Contains("x_2", names);
        Assert.True(names.IndexOf("x") < names.IndexOf("x_2"));
    }

    [Fact]
    public void Gather_NestedLetReferencingOuterCell_RewritesToBindingName()
    {
        // PR 6 acceptance #4: inner LET binding RHS references an outer
        // cell `A2`; after expansion the ref is rewritten to A2's outer
        // binding name. Body references inside the inner LET keep the
        // binding's (un-renamed) name. (The label sits at A1 so cell-above
        // for A2 picks it up — A1 itself has no cell-above and so couldn't
        // own a label-derived name.)
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("B2", "=LET(doubled, A2*2, doubled+10)")
            .WithFormula("C2", "=B2+5");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        var inner = result.Bindings.Single(b => b.Name == "doubled");
        // The outer A2 binding is `numbers`; the inner binding's RHS
        // rewrites A2 to that name, not the cell address.
        Assert.Equal("numbers*2", inner.Rhs);

        var stepB2 = result.Bindings.Single(b => b.Source.A1Address == "B2" && b.Name != "doubled");
        Assert.Equal("doubled+10", stepB2.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("numbers*2", parsed.Bindings[1].RhsText);
        Assert.Equal("doubled+10", parsed.Bindings[2].RhsText);
    }

    [Fact]
    public void Gather_NestedLetMatchingIssueManualScenario_ProducesExpectedShape()
    {
        // Adapted from issue 136's manual-test scenario, shifted down one
        // row so the label-above lookup actually has a cell to read (A1
        // can't have a label-above; the issue's "A0=x" is a thought
        // exercise). Outer A2 has label "x" → outer binding `x`; B2 holds
        // the nested LET; C2 is the sink. Expected synthesised LET shape:
        //   =LET(x, A2, x_2, x*2, step_1, x_2+5, step_1*2)
        var source = new StubCellSource()
            .WithLabel("A1", "x")
            .WithFormula("B2", "=LET(x, A2*2, x+5)")
            .WithFormula("C2", "=B2*2");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal(3, parsed.Bindings.Count);
        Assert.Equal(("x", "A2"), (parsed.Bindings[0].Name, parsed.Bindings[0].RhsText));
        Assert.Equal(("x_2", "x*2"), (parsed.Bindings[1].Name, parsed.Bindings[1].RhsText));
        Assert.Equal(("step_1", "x_2+5"), (parsed.Bindings[2].Name, parsed.Bindings[2].RhsText));
        Assert.Equal("step_1*2", parsed.Body);
    }

    [Fact]
    public void Gather_NestedLetWithMultipleInnerBindings_AllSpliced()
    {
        // Inner LET has two bindings; both are spliced in order. The
        // second's RHS references the first inner name, which we leave
        // alone (no renames needed).
        var source = new StubCellSource()
            .WithFormula("B1", "=LET(a, A1+1, b, a*2, b+1)")
            .WithFormula("C1", "=B1+1");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        var aRow = result.Bindings.Single(b => b.Name == "a");
        var bRow = result.Bindings.Single(b => b.Name == "b");
        var stepB1 = result.Bindings.Single(b => b.Source.A1Address == "B1" && b.Name != "a" && b.Name != "b");

        Assert.Equal("step_1+1", aRow.Rhs);
        Assert.Equal("a*2", bRow.Rhs);
        Assert.Equal("b+1", stepB1.Rhs);

        var bindings = result.Bindings.ToList();
        Assert.True(bindings.IndexOf(aRow) < bindings.IndexOf(bRow));
        Assert.True(bindings.IndexOf(bRow) < bindings.IndexOf(stepB1));
    }

    [Fact]
    public void Gather_NestedLetSecondBindingRefersToCollidingFirst_RenameCascades()
    {
        // Outer A2 has binding name `a` (label sits at A1). Inner LET's
        // first binding `a` collides → renamed to `a_2`. Inner LET's
        // second binding `b` doesn't collide; its RHS references the
        // (renamed) `a`, which must become `a_2`.
        var source = new StubCellSource()
            .WithLabel("A1", "a")
            .WithFormula("B2", "=LET(a, A2+1, b, a*3, b+1)")
            .WithFormula("C2", "=B2+1");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        var outerA = result.Bindings.Single(b => b.Source.A1Address == "A2");
        Assert.Equal("a", outerA.Name);

        var innerA2 = result.Bindings.Single(b => b.Name == "a_2");
        Assert.Equal("a+1", innerA2.Rhs);

        var innerB = result.Bindings.Single(b => b.Name == "b");
        // The inner-rename pass turns the original `a*3` into `a_2*3`
        // BEFORE the cell-ref rewrite runs (so a hypothetical outer cell
        // ref producing `a` wouldn't get caught).
        Assert.Equal("a_2*3", innerB.Rhs);

        var stepB2 = result.Bindings.Single(
            b => b.Source.A1Address == "B2" && b.Name != "a_2" && b.Name != "b");
        Assert.Equal("b+1", stepB2.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal(4, parsed.Bindings.Count);
    }

    [Fact]
    public void Gather_FormulaIsLetButHasTrailingExpression_NotExpanded()
    {
        // Guard against IsLetFormula's prefix-only check: `=LET(...) + A1`
        // must NOT be expanded — its trailing `+ A1` would be silently
        // dropped on naive expansion. The cell stays a regular step whose
        // RHS is the rewritten formula.
        var source = new StubCellSource()
            .WithFormula("B1", "=LET(x, 1, x+1) + A1")
            .WithFormula("C1", "=B1+5");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        // No `x` binding in the result — expansion was suppressed.
        Assert.DoesNotContain(result.Bindings, b => b.Name == "x");

        var stepB1 = result.Bindings.Single(b => b.Source.A1Address == "B1");
        Assert.Equal(BindingRole.Step, stepB1.Role);
        // The rewriter substitutes `A1` with its binding name; the LET
        // expression itself is preserved verbatim.
        Assert.Equal("LET(x, 1, x+1) + step_1", stepB1.Rhs);
    }

    [Fact]
    public void Gather_NestedLet_RoundTripsThroughLetParser()
    {
        // PR 6 acceptance: round-trip safety. Synthesised LET parses
        // cleanly even for the complex collision-and-rewrite case.
        var source = new StubCellSource()
            .WithLabel("A1", "x")
            .WithFormula("B1", "=LET(x, A1*2, x+5)")
            .WithFormula("C1", "=B1*2");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.NotNull(parsed);
        Assert.NotEmpty(parsed.Bindings);
    }
}