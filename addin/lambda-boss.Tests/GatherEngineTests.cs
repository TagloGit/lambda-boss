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

        var stepB2 = result.Bindings.Single(b => b.Source.A1Address == "B2" && b.Name != "a_2" && b.Name != "b");
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
    public void Gather_SinkIsLet_InlinedRatherThanNested()
    {
        // The sink itself is a `=LET(...)`. The synthesised outer LET
        // must splice the inner bindings in and use the inner body —
        // NOT emit the sink's LET nested inside the outer body. (Naive
        // implementations leave the sink LET verbatim because the body
        // path only does cell-ref rewriting.)
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("B1", "=LET(doubled, A2*2, doubled+1)");

        var result = GatherEngine.Gather(source.Ref("B1"), source)!;

        // The body of the synthesised LET must be `doubled+1`, not the
        // original nested `LET(doubled, ...)` text.
        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("doubled+1", parsed.Body);

        // Bindings: numbers (input from A2), doubled (inner step from
        // sink LET, with A2 rewritten to `numbers`).
        Assert.Equal(2, parsed.Bindings.Count);
        Assert.Equal("numbers", parsed.Bindings[0].Name);
        Assert.Equal("A2", parsed.Bindings[0].RhsText);
        Assert.Equal("doubled", parsed.Bindings[1].Name);
        Assert.Equal("numbers*2", parsed.Bindings[1].RhsText);
    }

    [Fact]
    public void Gather_SinkLetWithCollidingInnerName_AutoSuffixed()
    {
        // Sink LET's inner binding `numbers` collides with outer A2's
        // `numbers` and renames to `numbers_2`.
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("B1", "=LET(numbers, A2*2, numbers+1)");

        var result = GatherEngine.Gather(source.Ref("B1"), source)!;

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("numbers", parsed.Bindings[0].Name);
        Assert.Equal("numbers_2", parsed.Bindings[1].Name);
        Assert.Equal("numbers*2", parsed.Bindings[1].RhsText);
        Assert.Equal("numbers_2+1", parsed.Body);
    }

    [Fact]
    public void Gather_NestedLetBindingIsBareCellRef_NoOpAliasEliminated()
    {
        // Inner LET `=LET(in, A2, in*2)` aliases `in` to A2. After
        // rewriting `A2` to its outer binding name `numbers`, the inner
        // `in` row would be `in, numbers` — a no-op rebind. The engine
        // drops the row and propagates `in` → `numbers` through the
        // body so the synthesised LET stays terse.
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("B2", "=LET(in, A2, in*2)")
            .WithFormula("C2", "=B2+1");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        // No `in` row in the result.
        Assert.DoesNotContain(result.Bindings, b => b.Name == "in");

        // The step row for B2 inlines `numbers*2` directly.
        var stepB2 = result.Bindings.Single(b => b.Source.A1Address == "B2");
        Assert.Equal("numbers*2", stepB2.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.DoesNotContain(parsed.Bindings, b => b.Name == "in");
    }

    [Fact]
    public void Gather_StepFormulaIsBareCellRef_NoOpAliasEliminated()
    {
        // Outer step cell whose formula is just `=A2` — a bare cell-ref
        // alias. After rewriting, the step's RHS is `numbers` (A2's
        // outer binding), making the row a no-op rebind. Drop the row
        // and redirect downstream references to `numbers` directly.
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithLabel("B1", "Alias")
            .WithFormula("B2", "=A2")
            .WithFormula("C2", "=B2+10");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        // No `alias` row — eliminated.
        Assert.DoesNotContain(result.Bindings, b => b.Name == "alias");

        // Body references `numbers` directly, skipping the indirection.
        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("numbers+10", parsed.Body);
    }

    [Fact]
    public void Gather_ChainedAliasSteps_AllCollapseToOriginalBinding()
    {
        // A chain of pure-alias steps should all collapse: B2=A2,
        // C2=B2, D2=C2+1 (sink). Both intermediate aliases drop and
        // the body rewrites D2's reference to C2 straight to `numbers`.
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithLabel("B1", "First")
            .WithLabel("C1", "Second")
            .WithFormula("B2", "=A2")
            .WithFormula("C2", "=B2")
            .WithFormula("D2", "=C2+1");

        var result = GatherEngine.Gather(source.Ref("D2"), source)!;

        // Only the `numbers` input remains; `first` and `second` were
        // alias-eliminated.
        Assert.DoesNotContain(result.Bindings, b => b.Name == "first");
        Assert.DoesNotContain(result.Bindings, b => b.Name == "second");

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Single(parsed.Bindings);
        Assert.Equal("numbers", parsed.Bindings[0].Name);
        Assert.Equal("numbers+1", parsed.Body);
    }

    [Fact]
    public void Gather_StepLetCellAliasesItsLastBinding_NameLabelDoesNotForceInnerSuffix()
    {
        // The cell label gives K6 the outer name `y`, but K6's formula
        // `=LET(x, J6, y, x+5, y)` returns its last binding `y` directly
        // — the cell aliases its inner `y`, so K6's outer name is never
        // emitted as a binding row. Reserving `y` upfront would force
        // the inner `y` to suffix to `y_2` for no benefit. Detect the
        // bare-identifier body, free the outer name before expansion,
        // and let the inner binding keep `y`. Adapted from issue #136
        // follow-up feedback.
        var source = new StubCellSource()
            .WithLabel("J5", "x")
            .WithLabel("K5", "y")
            .WithFormula("K6", "=LET(x, J6, y, x+5, y)")
            .WithFormula("L6", "=K6");

        var result = GatherEngine.Gather(source.Ref("L6"), source)!;

        // No `y_2` — the inner `y` kept the unsuffixed name.
        Assert.DoesNotContain(result.Bindings, b => b.Name == "y_2");

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal(2, parsed.Bindings.Count);
        Assert.Equal("x", parsed.Bindings[0].Name);
        Assert.Equal("J6", parsed.Bindings[0].RhsText);
        Assert.Equal("y", parsed.Bindings[1].Name);
        Assert.Equal("x+5", parsed.Bindings[1].RhsText);
        Assert.Equal("y", parsed.Body);
    }

    [Fact]
    public void Gather_StepLetCellWithCalcBody_KeepsLabelOnOuter()
    {
        // Counterpart to the alias case: when the cell's LET body is a
        // calculation (`doubled+1`), the outer step row IS emitted, so
        // the cell's label-derived name `doubled` should win and the
        // inner colliding `doubled` suffixes to `doubled_2`. Guards
        // against regressing the original PR 6 behaviour.
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithLabel("B1", "Doubled")
            .WithFormula("B2", "=LET(doubled, A2*2, doubled+1)")
            .WithFormula("C2", "=B2+5");

        var result = GatherEngine.Gather(source.Ref("C2"), source)!;

        // Outer `doubled` row exists with the cell's preferred name;
        // the inner colliding binding got `doubled_2`.
        var outerDoubled = result.Bindings.Single(b => b.Source.A1Address == "B2" && b.Name == "doubled");
        Assert.Equal("doubled_2+1", outerDoubled.Rhs);

        var innerDoubled2 = result.Bindings.Single(b => b.Name == "doubled_2");
        Assert.Equal("numbers*2", innerDoubled2.Rhs);
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

    [Fact]
    public void Gather_TwoCycle_ReturnsCycleDiagnostic()
    {
        // PR 7 acceptance: A1 ↔ B1 cycle. Engine returns a result with
        // Diagnostic.Kind = Cycle, an empty bindings list, and an empty
        // synthesised LET. The diagnostic's Cells list contains both
        // cycle members so the caller (or future tests) can introspect.
        var source = new StubCellSource()
            .WithFormula("A1", "=B1+1")
            .WithFormula("B1", "=A1+1");

        var result = GatherEngine.Gather(source.Ref("A1"), source);

        Assert.NotNull(result);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(GatherDiagnosticKind.Cycle, result.Diagnostic!.Kind);
        Assert.Empty(result.Bindings);
        Assert.Empty(result.SynthesisedLet);
        var addresses = result.Diagnostic.Cells.Select(c => c.A1Address).ToHashSet();
        Assert.Equal(["A1", "B1"], addresses);
    }

    [Fact]
    public void Gather_TwoCycle_DiagnosticMessageNamesBothCells()
    {
        // The MessageBox text the user sees must list both cells with
        // sheet qualifiers so cross-sheet cycles remain unambiguous.
        var source = new StubCellSource()
            .WithFormula("A1", "=B1+1")
            .WithFormula("B1", "=A1+1");

        var result = GatherEngine.Gather(source.Ref("A1"), source)!;

        Assert.NotNull(result.Diagnostic);
        Assert.Contains("A1", result.Diagnostic!.Message);
        Assert.Contains("B1", result.Diagnostic.Message);
        // Closes the cycle path with the back-edge target repeated at
        // the end so the loop is visible — exact format isn't pinned
        // here but the arrow separator must be present.
        Assert.Contains("→", result.Diagnostic.Message);
    }

    [Fact]
    public void Gather_ThreeCycle_DiagnosticListsAllThreeCells()
    {
        // PR 7 acceptance: 3-cycle. Diagnostic.Cells contains all three
        // members in path order; the message names each one.
        var source = new StubCellSource()
            .WithFormula("A1", "=B1+1")
            .WithFormula("B1", "=C1+1")
            .WithFormula("C1", "=A1+1");

        var result = GatherEngine.Gather(source.Ref("A1"), source)!;

        Assert.Equal(GatherDiagnosticKind.Cycle, result.Diagnostic!.Kind);
        var addresses = result.Diagnostic.Cells.Select(c => c.A1Address).ToList();
        Assert.Equal(3, addresses.Count);
        Assert.Contains("A1", addresses);
        Assert.Contains("B1", addresses);
        Assert.Contains("C1", addresses);
    }

    [Fact]
    public void Gather_CycleReachableFromSink_StillSurfaces()
    {
        // The cycle doesn't include the sink itself; engine still
        // surfaces it because the walker's DFS reaches into it.
        var source = new StubCellSource()
            .WithFormula("A1", "=B1+1")
            .WithFormula("B1", "=A1+1")
            .WithFormula("C1", "=B1+1");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        Assert.Equal(GatherDiagnosticKind.Cycle, result.Diagnostic!.Kind);
    }

    [Fact]
    public void Gather_NoCycle_DiagnosticIsNull()
    {
        // Regression guard: an acyclic walk produces no diagnostic.
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=B1+1");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        Assert.Null(result.Diagnostic);
        Assert.NotEmpty(result.Bindings);
    }

    [Fact]
    public void Gather_NoFormulaSink_StillReturnsNullEvenWithSelection()
    {
        // The silent-no-op contract is preserved when the new selection
        // overload is called: a non-formula sink still yields null,
        // never a diagnostic — the slash command treats it as a quiet
        // close, not an error worth a MessageBox.
        var source = new StubCellSource();

        var result = GatherEngine.Gather(
            source.Ref("A1"),
            new[] { source.Ref("A1"), source.Ref("B1") },
            source);

        Assert.Null(result);
    }

    [Fact]
    public void Gather_MultiSinkSelection_ReturnsMultipleSinksDiagnostic()
    {
        // PR 7 acceptance: A1=10 (literal), B1=A1*2, C1=A1+1 — B1 and
        // C1 are independent sinks (neither references the other).
        // Multi-selecting both must refuse with MultipleSinks. The sink
        // passed in is whichever cell was active at trigger time; the
        // diagnostic's Cells is empty per the spec ("no list — situation
        // is obvious to the author").
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=A1+1");

        var result = GatherEngine.Gather(
            source.Ref("B1"),
            new[] { source.Ref("B1"), source.Ref("C1") },
            source);

        Assert.NotNull(result);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(GatherDiagnosticKind.MultipleSinks, result.Diagnostic!.Kind);
        Assert.Empty(result.Bindings);
        Assert.Empty(result.SynthesisedLet);
        Assert.Empty(result.Diagnostic.Cells);
    }

    [Fact]
    public void Gather_MultiSelectionWithDependency_AllowedSingleSink()
    {
        // Multi-selection is allowed when one of the selected cells
        // transitively reaches every other selected cell — there's only
        // one true sink. Walk proceeds normally; no diagnostic.
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=B1+1");

        var result = GatherEngine.Gather(
            source.Ref("C1"),
            new[] { source.Ref("A1"), source.Ref("B1"), source.Ref("C1") },
            source);

        Assert.NotNull(result);
        Assert.Null(result.Diagnostic);
        Assert.NotEmpty(result.Bindings);
    }

    [Fact]
    public void Gather_SingleCellSelection_BypassesMultiSinkCheck()
    {
        // PR 7 acceptance: the multi-sink check is skipped when the
        // selection is a single cell — the spec says "single-cell
        // selection (always allowed)".
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2");

        var result = GatherEngine.Gather(
            source.Ref("B1"),
            new[] { source.Ref("B1") },
            source);

        Assert.NotNull(result);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public void Gather_ThreeIndependentSinksInSelection_RefusedAsMultiSink()
    {
        // Three disconnected sinks (B1, C1, D1 all refer to A1 only)
        // — three sink candidates, two would already trigger refusal.
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=A1+1")
            .WithFormula("D1", "=A1-1");

        var result = GatherEngine.Gather(
            source.Ref("B1"),
            new[] { source.Ref("B1"), source.Ref("C1"), source.Ref("D1") },
            source);

        Assert.Equal(GatherDiagnosticKind.MultipleSinks, result!.Diagnostic!.Kind);
    }

    [Fact]
    public void Gather_CycleWithMultiSelection_CycleDiagnosticWins()
    {
        // Cycles are an unconditional refusal regardless of selection
        // shape — even a multi-sink-shaped selection should surface
        // the cycle first because it's the more fundamental error and
        // the multi-sink check itself walks the graph.
        var source = new StubCellSource()
            .WithFormula("A1", "=B1+1")
            .WithFormula("B1", "=A1+1");

        var result = GatherEngine.Gather(
            source.Ref("A1"),
            new[] { source.Ref("A1"), source.Ref("B1") },
            source);

        Assert.Equal(GatherDiagnosticKind.Cycle, result!.Diagnostic!.Kind);
    }

    [Fact]
    public void Gather_PureLambdaCallSink_ReturnsLambdaCallSinkDiagnostic()
    {
        // PR 8 acceptance #1: sink formula is exactly =Foo(A1, B1) with
        // Foo registered as a LAMBDA. Engine refuses with a diagnostic
        // pointing at /EditLambda and emits no bindings or LET.
        var source = new StubCellSource()
            .WithFormula("C1", "=Foo(A1, B1)")
            .WithLambdaName("Foo");

        var result = GatherEngine.Gather(source.Ref("C1"), source);

        Assert.NotNull(result);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(GatherDiagnosticKind.LambdaCallSink, result.Diagnostic!.Kind);
        Assert.Empty(result.Bindings);
        Assert.Empty(result.SynthesisedLet);
        Assert.Contains("/EditLambda", result.Diagnostic.Message);
    }

    [Fact]
    public void Gather_LambdaCallWrappedInExpression_WalksNormally()
    {
        // PR 8 acceptance #2: =Foo(A1) + 1 is NOT a pure LAMBDA call —
        // the trailing `+ 1` makes it an ordinary expression. The walk
        // proceeds normally and Foo(A1) is left untouched in the
        // rewritten step body (CellRefExtractor doesn't match function
        // names, only cell-shaped tokens).
        var source = new StubCellSource()
            .WithFormula("D1", "=Foo(A1) + 1")
            .WithLambdaName("Foo");

        var result = GatherEngine.Gather(source.Ref("D1"), source);

        Assert.NotNull(result);
        Assert.Null(result.Diagnostic);
        // A1 is the only in-scope precedent.
        var aRow = result.Bindings.Single(b => b.Source.A1Address == "A1");
        Assert.Equal(BindingRole.Input, aRow.Role);

        // The synthesised LET body keeps the LAMBDA call verbatim and
        // rewrites the cell ref inside it to the binding name.
        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal($"Foo({aRow.Name}) + 1", parsed.Body);
    }

    [Fact]
    public void Gather_NonLambdaCallSink_WalksNormally()
    {
        // PR 8 acceptance #3: a sink with no LAMBDA call at all must
        // not be touched by the new check. Regression guard against
        // accidentally refusing plain arithmetic sinks.
        var source = new StubCellSource()
            .WithFormula("B1", "=A1 + 1");

        var result = GatherEngine.Gather(source.Ref("B1"), source);

        Assert.NotNull(result);
        Assert.Null(result.Diagnostic);
        Assert.NotEmpty(result.Bindings);
    }

    [Fact]
    public void Gather_BuiltInFunctionCallSink_NotRefused()
    {
        // =SUM(A1, B1) matches the call-shape regex but SUM isn't a
        // workbook-scoped LAMBDA, so the engine must walk it normally
        // rather than refuse. This guards against the LAMBDA-call check
        // accidentally catching every single-call formula.
        var source = new StubCellSource()
            .WithFormula("C1", "=SUM(A1, B1)");

        var result = GatherEngine.Gather(source.Ref("C1"), source);

        Assert.NotNull(result);
        Assert.Null(result.Diagnostic);
        Assert.Equal(2, result.Bindings.Count);
    }

    [Fact]
    public void Gather_NestedLetWithQuestionMarkBinding_ParsesAndPreservesName()
    {
        // Issue 152: a step's nested LET uses 'Help?' as a binding name —
        // the engine must parse it (LetParser allows '?' in body chars)
        // and preserve the name in both the spliced inner row and any
        // body references. Without the fix this throws "Invalid LET
        // binding name: 'Help?'" before any walking happens.
        var source = new StubCellSource()
            .WithFormula("B1", "=LET(Help?, A1+1, Help? * 2)")
            .WithFormula("C1", "=B1+5");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        Assert.Null(result.Diagnostic);
        var inner = result.Bindings.Single(b => b.Name == "Help?");
        Assert.Equal("step_1+1", inner.Rhs);

        var stepB1 = result.Bindings.Single(b => b.Source.A1Address == "B1" && b.Name != "Help?");
        // The inner body 'Help? * 2' becomes the step's RHS — the
        // tokenizer must recognise 'Help?' as one identifier so the
        // (no-op) inner-rename pass doesn't truncate it.
        Assert.Equal("Help? * 2", stepB1.Rhs);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Contains(parsed.Bindings, b => b.Name == "Help?");
    }

    [Fact]
    public void Gather_PureLetSink_StillExpandedNotRefused()
    {
        // =LET(...) matches the call-shape regex with name "LET" but
        // LET isn't a registered LAMBDA, so the engine takes its
        // existing pure-LET sink path (inline expansion) rather than
        // refusing. Regression guard against the LAMBDA-call check
        // catching the engine's own LET inlining flow.
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("B1", "=LET(doubled, A2*2, doubled+1)");

        var result = GatherEngine.Gather(source.Ref("B1"), source);

        Assert.NotNull(result);
        Assert.Null(result.Diagnostic);
        // Sink LET was inlined (body is `doubled+1`, not nested).
        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("doubled+1", parsed.Body);
    }

    [Fact]
    public void Gather_SingleCellSelection_ReportsFreeWalkCounts()
    {
        // PR 9: single-cell selection means free walk. M and N both equal
        // the count of cells the walker visited (sink + every reachable
        // precedent). The dialog will render this as
        // "Walking 3 cells from C1".
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=B1+1");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        Assert.Equal(3, result.FreeWalkCount);
        Assert.Equal(3, result.WalkedCount);
    }

    [Fact]
    public void Gather_MultiSelectionFullCover_ReportsEqualCounts()
    {
        // PR 9: a multi-selection that covers every walked cell restricts
        // nothing, so M == N. The dialog suppresses the "restricted by
        // selection" hint when the selection didn't actually narrow the
        // walk — this matches the issue's "behaves like a free walk".
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=B1+1");

        var result = GatherEngine.Gather(
            source.Ref("C1"),
            new[] { source.Ref("A1"), source.Ref("B1"), source.Ref("C1") },
            source)!;

        Assert.Equal(3, result.FreeWalkCount);
        Assert.Equal(3, result.WalkedCount);
    }

    [Fact]
    public void Gather_MultiSelectionPartialCover_DemotesPrecedentToCellRefInput()
    {
        // PR 9 acceptance scenario: A1=10 literal, B1=A1*2, C1=B1+1
        // (sink). Selecting {B1, C1} restricts the walk so A1 isn't a
        // step candidate, but its cell-ref still appears as an input
        // binding on the boundary. A1 happens to be a literal here, so
        // the binding shape is the same as the free-walk case — what
        // changes is the count: the dialog header shows "2 of 3".
        var source = new StubCellSource()
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=B1+1");

        var result = GatherEngine.Gather(
            source.Ref("C1"),
            new[] { source.Ref("B1"), source.Ref("C1") },
            source)!;

        Assert.Null(result.Diagnostic);
        Assert.Equal(3, result.FreeWalkCount);
        Assert.Equal(2, result.WalkedCount);

        var aRow = result.Bindings.Single(b => b.Source.A1Address == "A1");
        Assert.Equal(BindingRole.Input, aRow.Role);
        Assert.Equal("A1", aRow.Rhs);

        var bRow = result.Bindings.Single(b => b.Source.A1Address == "B1");
        Assert.Equal(BindingRole.Step, bRow.Role);
    }

    [Fact]
    public void Gather_MultiSelectionPartialCover_DropsOutOfSelectionPrecedentSubTree()
    {
        // A1 (formula referencing X1) → B1 → C1 → D1 (sink). Selection
        // {C1, D1} restricts B1 to a leaf-input (cell-ref RHS); A1 and
        // X1 are never reached because B1's sub-tree is pruned. Without
        // restriction the walk would visit 4 cells (A1 has no formula, so
        // X1 isn't reached even in the free walk — it would only matter
        // if A1 had a formula).
        var source = new StubCellSource()
            .WithFormula("A1", "=X1+1")
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=B1+1")
            .WithFormula("D1", "=C1*3");

        var freeResult = GatherEngine.Gather(source.Ref("D1"), source)!;
        Assert.Equal(5, freeResult.FreeWalkCount);
        Assert.Equal(5, freeResult.WalkedCount);

        var restrictedResult = GatherEngine.Gather(
            source.Ref("D1"),
            new[] { source.Ref("C1"), source.Ref("D1") },
            source)!;

        Assert.Equal(5, restrictedResult.FreeWalkCount);
        Assert.Equal(2, restrictedResult.WalkedCount);

        var addresses = restrictedResult.Bindings
            .Select(b => b.Source.A1Address)
            .ToHashSet();
        Assert.Contains("B1", addresses);
        Assert.Contains("C1", addresses);
        Assert.DoesNotContain("A1", addresses);
        Assert.DoesNotContain("X1", addresses);

        // B1 is the boundary leaf — input role with cell-ref RHS, no
        // matter that its formula is "=A1*2" in the workbook.
        var bRow = restrictedResult.Bindings.Single(b => b.Source.A1Address == "B1");
        Assert.Equal(BindingRole.Input, bRow.Role);
        Assert.Equal("B1", bRow.Rhs);
    }

    [Fact]
    public void Gather_RestrictedWalk_LetRoundTripsThroughLetParser()
    {
        // Round-trip safety on the restricted-walk branch: synthesised
        // LET still parses cleanly even though some bindings are leafs
        // demoted from steps.
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("B1", "=A1*2")
            .WithFormula("C1", "=B1+1");

        var result = GatherEngine.Gather(
            source.Ref("C1"),
            new[] { source.Ref("B1"), source.Ref("C1") },
            source)!;

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.NotEmpty(parsed.Bindings);
        Assert.NotEmpty(parsed.Body);
    }
}