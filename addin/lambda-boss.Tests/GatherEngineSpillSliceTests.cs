using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Spec 0010 PR 3 — single-cell slices end-to-end. A single-cell
///     reference landing inside a spill becomes its own binding row holding
///     <c>INDEX(anchor, r, c)</c>. Both single-cell cases are one code path:
///     a spill <em>child</em> (<c>B2</c>, which has no formula of its own) and
///     a scalar reference to the spilling <em>anchor</em> (<c>A2</c>, which
///     Excel reads as the top-left value). Range slices spanning more than one
///     cell are PR 4; the dialog's indentation and fixed-position note are PR 8.
/// </summary>
public class GatherEngineSpillSliceTests
{
    /// <summary>
    ///     The spec's canonical case, laid out with labels so the binding
    ///     names read the way the sheet reads:
    ///     <code>
    ///     A1 Extracted   B1 Second
    ///     A2 =REGEXEXTRACT(...)  spills A2:B2
    ///     C4 Doubled     D4 =A2*2
    ///     C5 Suffixed    D5 =B2&amp;"x"
    ///     C6 Joined      D6 =D4&amp;D5      &lt;- sink
    ///     </code>
    ///     One array binding plus two named slices, not an array binding plus
    ///     an unrelated cell-reference input.
    /// </summary>
    [Fact]
    public void Gather_CanonicalRegexExtractCase_EmitsAnchorPlusTwoSlices()
    {
        var source = CanonicalSource();

        var result = GatherEngine.Gather(source.Ref("D6"), source)!;

        var names = result.Bindings.Select(b => b.Name).ToList();
        Assert.Equal(
            new[] { "extracted", "extracted_2", "second", "doubled", "suffixed" },
            names);

        var anchor = result.Bindings[0];
        Assert.Equal(BindingRole.Input, anchor.Role);
        Assert.Equal("A2#", anchor.Rhs);
        Assert.Null(anchor.SliceOf);

        Assert.Equal("INDEX(extracted,1,1)", result.Bindings[1].Rhs);
        Assert.Equal("INDEX(extracted,1,2)", result.Bindings[2].Rhs);
        Assert.Equal("extracted_2*2", result.Bindings[3].Rhs);
        Assert.Equal("suffixed", result.Bindings[4].Name);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("doubled&suffixed", parsed.Body);
        Assert.Equal(5, parsed.Bindings.Count);
    }

    [Fact]
    public void Gather_CanonicalCase_SliceRowsCarrySliceOfAndNoRoleToggle()
    {
        var source = CanonicalSource();

        var result = GatherEngine.Gather(source.Ref("D6"), source)!;

        var anchorRowRef = result.Bindings[0].Source;
        // The anchor's binding IS the array, so its row is keyed on the
        // spilled ref — which is what leaves the bare `A2` key free to
        // identify the scalar slice of the same cell.
        Assert.True(anchorRowRef.IsSpilled);

        foreach (var slice in result.Bindings.Where(b => b.SliceOf != null))
        {
            Assert.Equal(anchorRowRef, slice.SliceOf);
            Assert.Equal(BindingRole.Input, slice.Role);
            Assert.False(slice.CanToggleRole);
        }

        var sliceSources = result.Bindings
            .Where(b => b.SliceOf != null)
            .Select(b => b.Source.A1Address)
            .ToList();
        Assert.Equal(new[] { "A2", "B2" }, sliceSources);
        Assert.All(
            result.Bindings.Where(b => b.SliceOf != null),
            b => Assert.False(b.Source.IsSpilled));
    }

    /// <summary>
    ///     Spec 0010: "Step formulas continue to receive only bare
    ///     identifiers from <c>CellRefExtractor.Rewrite</c>." Slice
    ///     expressions live solely on a slice row's own RHS, so there are no
    ///     operator-precedence concerns in a rewritten step formula.
    /// </summary>
    [Fact]
    public void Gather_CanonicalCase_StepFormulasCarryNoSliceExpressions()
    {
        var source = CanonicalSource();

        var result = GatherEngine.Gather(source.Ref("D6"), source)!;

        foreach (var step in result.Bindings.Where(b => b.Role == BindingRole.Step))
            Assert.DoesNotContain("INDEX(", step.Rhs, StringComparison.Ordinal);
        Assert.DoesNotContain("INDEX(", LetParser.Parse(result.SynthesisedLet).Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Gather_SliceReferencedFromSeveralSteps_ProducesOneRow()
    {
        var source = new StubCellSource()
            .WithLabel("A1", "Extracted")
            .WithLabel("B1", "Second")
            .WithFormula("A2", "=SEQUENCE(1,2)")
            .WithSpill("A2", 1, 2)
            .WithLabel("C4", "Doubled")
            .WithFormula("D4", "=B2*2")
            .WithLabel("C5", "Suffixed")
            .WithFormula("D5", "=B2&\"x\"")
            .WithLabel("C6", "Joined")
            .WithFormula("D6", "=D4&D5");

        var result = GatherEngine.Gather(source.Ref("D6"), source)!;

        var sliceRows = result.Bindings.Where(b => b.SliceOf != null).ToList();
        var slice = Assert.Single(sliceRows);
        Assert.Equal("second", slice.Name);
        Assert.Equal("INDEX(extracted,1,2)", slice.Rhs);

        Assert.Equal("second*2", result.Bindings.Single(b => b.Name == "doubled").Rhs);
        Assert.Equal("second&\"x\"", result.Bindings.Single(b => b.Name == "suffixed").Rhs);
    }

    /// <summary>
    ///     Anchor discovery: the sink references a spill child and nothing
    ///     references the anchor, so the walker pulls the anchor in as a
    ///     precedent — otherwise there is no array to slice.
    /// </summary>
    [Fact]
    public void Gather_SpillChildWithUnreferencedAnchor_PullsAnchorIn()
    {
        var source = new StubCellSource()
            .WithLabel("A1", "Extracted")
            .WithLabel("B1", "Second")
            .WithFormula("A2", "=SEQUENCE(1,2)")
            .WithSpill("A2", 1, 2)
            .WithFormula("D4", "=B2*2");

        var result = GatherEngine.Gather(source.Ref("D4"), source)!;

        Assert.Equal(2, result.Bindings.Count);
        var anchor = result.Bindings[0];
        Assert.Equal("A2", anchor.Source.A1Address);
        Assert.Equal("extracted", anchor.Name);
        Assert.Equal("A2#", anchor.Rhs);

        var slice = result.Bindings[1];
        Assert.Equal("B2", slice.Source.A1Address);
        Assert.Equal("INDEX(extracted,1,2)", slice.Rhs);

        Assert.Equal("second*2", LetParser.Parse(result.SynthesisedLet).Body);
    }

    /// <summary>
    ///     The walker recurses into the anchor, never a child — a child has no
    ///     formula of its own, so walking it would produce a stray leaf input
    ///     whose <c>#</c>-suffixed RHS (<c>B2#</c>) is <c>#REF!</c> in Excel.
    /// </summary>
    [Fact]
    public void Walk_SpillChildPrecedent_WalksAnchorNeverChild()
    {
        var source = new StubCellSource()
            .WithFormula("A2", "=SEQUENCE(1,2)")
            .WithSpill("A2", 1, 2)
            .WithFormula("D4", "=B2*2");

        var walked = CellGraphWalker.Walk(source.Ref("D4"), source).Cells!;

        Assert.Equal(new[] { "A2", "D4" }, walked.Select(w => w.Ref.A1Address));
        Assert.True(walked.Single(w => w.Ref.A1Address == "A2").HasSpill);
    }

    /// <summary>
    ///     <c>NormaliseSpillFlag</c> is gone: <c>A2</c> and <c>A2#</c> map to
    ///     different replacements (<c>INDEX(arr,1,1)</c> versus <c>arr</c>),
    ///     so they must survive the walk as distinct precedents.
    /// </summary>
    [Fact]
    public void Walk_SpilledAndBareRefsToSameCell_StayDistinctPrecedents()
    {
        var source = new StubCellSource()
            .WithFormula("A2", "=SEQUENCE(1,2)")
            .WithSpill("A2", 1, 2)
            .WithFormula("D4", "=SUM(A2#)+A2");

        var walked = CellGraphWalker.Walk(source.Ref("D4"), source).Cells!;

        var sink = walked.Single(w => w.Ref.A1Address == "D4");
        Assert.Equal(2, sink.Precedents.Count);
        Assert.Contains(sink.Precedents, p => p.IsSpilled);
        Assert.Contains(sink.Precedents, p => !p.IsSpilled);
        Assert.All(sink.Precedents, p => Assert.Equal("A2", p.Start.A1Address));
    }

    /// <summary>
    ///     Both shapes in one LET: <c>A2#</c> is the anchor's binding,
    ///     <c>A2</c> is a slice of it.
    /// </summary>
    [Fact]
    public void Gather_SpilledAndBareRefsToSameCell_BindArrayAndSliceSeparately()
    {
        var source = new StubCellSource()
            .WithLabel("A1", "Extracted")
            .WithFormula("A2", "=SEQUENCE(1,2)")
            .WithSpill("A2", 1, 2)
            .WithFormula("D4", "=SUM(A2#)+A2");

        var result = GatherEngine.Gather(source.Ref("D4"), source)!;

        Assert.Equal("A2#", result.Bindings.Single(b => b.Name == "extracted").Rhs);
        Assert.Equal("INDEX(extracted,1,1)",
            result.Bindings.Single(b => b.Name == "extracted_2").Rhs);
        Assert.Equal("SUM(extracted)+extracted_2",
            LetParser.Parse(result.SynthesisedLet).Body);
    }

    // --- The pre-existing scalar-widening bug, now fixed -----------------

    /// <summary>
    ///     Regression pin for spec 0010's second listed defect: <c>=A2*2</c>
    ///     on a spilling anchor used to bind <c>A2#</c> and rewrite the bare
    ///     <c>A2</c> token to that binding name, silently handing the step the
    ///     whole array. <c>INDEX(arr,1,1)</c> is the faithful rewrite.
    /// </summary>
    [Fact]
    public void Gather_ScalarRefToSpillingAnchor_NoLongerWidensToWholeArray()
    {
        var source = new StubCellSource()
            .WithLabel("A1", "Numbers")
            .WithFormula("A2", "=SEQUENCE(10)")
            .WithSpill("A2", 10, 1)
            .WithFormula("B2", "=A2*2");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("numbers_2*2", parsed.Body);
        Assert.Equal(2, parsed.Bindings.Count);
        Assert.Equal("A2#", parsed.Bindings[0].RhsText);
        Assert.Equal("INDEX(numbers,1,1)", parsed.Bindings[1].RhsText);
    }

    // --- 1x1 spills: the reference's shape decides, not the result's -----

    [Fact]
    public void Gather_OneByOneSpill_SpillRefBindsWholeArray()
    {
        var source = OneByOneSource("=SUM(A2#)");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        var binding = Assert.Single(result.Bindings);
        Assert.Equal("arr", binding.Name);
        Assert.Equal("A2#", binding.Rhs);
        Assert.Null(binding.SliceOf);
        Assert.Equal("SUM(arr)", LetParser.Parse(result.SynthesisedLet).Body);
    }

    [Fact]
    public void Gather_OneByOneSpill_BareCellRefIndexesTopLeft()
    {
        var source = OneByOneSource("=A2*2");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        Assert.Equal(2, result.Bindings.Count);
        Assert.Equal("A2#", result.Bindings[0].Rhs);
        Assert.Equal("INDEX(arr,1,1)", result.Bindings[1].Rhs);
        Assert.Equal("arr_2*2", LetParser.Parse(result.SynthesisedLet).Body);
    }

    /// <summary>
    ///     A degenerate one-cell range is a scalar in Excel (<c>=A2:A2</c>
    ///     yields the cell's value), so it takes the single-cell path even
    ///     though on a 1×1 spill it is simultaneously the whole array — and
    ///     it never promotes to a range input.
    /// </summary>
    [Fact]
    public void Gather_OneByOneSpill_DegenerateRangeIndexesTopLeft()
    {
        var source = OneByOneSource("=SUM(A2:A2)");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        Assert.Equal(2, result.Bindings.Count);
        Assert.Equal("A2#", result.Bindings[0].Rhs);
        var slice = result.Bindings[1];
        Assert.NotNull(slice.SliceOf);
        Assert.Equal("A2:A2", slice.Source.A1Address);
        Assert.Equal("INDEX(arr,1,1)", slice.Rhs);
        Assert.Equal("SUM(arr_2)", LetParser.Parse(result.SynthesisedLet).Body);
    }

    // --- Naming ---------------------------------------------------------

    /// <summary>
    ///     With no label above or to the left of the sliced cell, the slice
    ///     falls back to <c>&lt;anchorName&gt;_&lt;rowMajorIndex&gt;</c>
    ///     (<c>A2</c>→1, <c>B2</c>→2 for a 1×2 spill) rather than the generic
    ///     <c>step_N</c>.
    /// </summary>
    [Fact]
    public void Gather_SliceWithNoLabel_NamesFromAnchorAndRowMajorIndex()
    {
        var source = new StubCellSource()
            .WithLabel("A1", "Extracted")
            .WithFormula("A2", "=SEQUENCE(1,2)")
            .WithSpill("A2", 1, 2)
            .WithFormula("D4", "=B2*2");

        var result = GatherEngine.Gather(source.Ref("D4"), source)!;

        Assert.Equal("extracted_2", result.Bindings[1].Name);
        Assert.Equal("INDEX(extracted,1,2)", result.Bindings[1].Rhs);
    }

    [Fact]
    public void Gather_SliceWithLabelAbove_NamesFromLabel()
    {
        var source = new StubCellSource()
            .WithLabel("A1", "Extracted")
            .WithLabel("B1", "Capture Two")
            .WithFormula("A2", "=SEQUENCE(1,2)")
            .WithSpill("A2", 1, 2)
            .WithFormula("D4", "=B2*2");

        var result = GatherEngine.Gather(source.Ref("D4"), source)!;

        Assert.Equal("captureTwo", result.Bindings[1].Name);
    }

    /// <summary>
    ///     Slice rows are ordered immediately after their anchor's row, even
    ///     when the anchor is a step (a spilling cell with in-scope
    ///     precedents) rather than a leaf input.
    /// </summary>
    [Fact]
    public void Gather_AnchorIsAStep_SliceRowFollowsItImmediately()
    {
        var source = new StubCellSource()
            .WithLabel("A1", "Count")
            .WithFormula("A2", "=2")
            .WithLabel("B1", "Extracted")
            .WithFormula("B2", "=SEQUENCE(1,A2)")
            .WithSpill("B2", 1, 2)
            .WithFormula("D4", "=C2*3");

        var result = GatherEngine.Gather(source.Ref("D4"), source)!;

        var names = result.Bindings.Select(b => b.Name).ToList();
        Assert.Equal(new[] { "count", "extracted", "extracted_2" }, names);
        Assert.Equal("SEQUENCE(1,count)", result.Bindings[1].Rhs);
        Assert.Equal("INDEX(extracted,1,2)", result.Bindings[2].Rhs);
        Assert.Equal("extracted_2*3", LetParser.Parse(result.SynthesisedLet).Body);
    }

    private static StubCellSource CanonicalSource()
    {
        return new StubCellSource()
            .WithLabel("A1", "Extracted")
            .WithLabel("B1", "Second")
            .WithFormula("A2", "=REGEXEXTRACT(\"a-b\",\"(.)-(.)\",1)")
            .WithSpill("A2", 1, 2)
            .WithLabel("C4", "Doubled")
            .WithFormula("D4", "=A2*2")
            .WithLabel("C5", "Suffixed")
            .WithFormula("D5", "=B2&\"x\"")
            .WithLabel("C6", "Joined")
            .WithFormula("D6", "=D4&D5");
    }

    private static StubCellSource OneByOneSource(string sinkFormula)
    {
        return new StubCellSource()
            .WithLabel("A1", "Arr")
            .WithFormula("A2", "=SEQUENCE(1,1)")
            .WithSpill("A2", 1, 1)
            .WithFormula("B2", sinkFormula);
    }
}
