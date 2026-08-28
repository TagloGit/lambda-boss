using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Spec 0010 PRs 3 and 4 — spill slices end-to-end. A reference landing
///     wholly inside a spill becomes its own binding row holding a slice of the
///     anchor's array: <c>INDEX(anchor, r, c)</c> for the single-cell cases
///     (PR 3 — a spill <em>child</em> such as <c>B2</c>, which has no formula
///     of its own, and a scalar reference to the spilling <em>anchor</em>,
///     which Excel reads as the top-left value), and <c>TAKE</c>/<c>DROP</c>
///     for a range covering a sub-block (PR 4). A range spanning the whole
///     spill needs no row — it rewrites to the anchor's own binding name.
///     The straddling-range warning is PR 5; the dialog's indentation and
///     fixed-position note are PR 8.
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

    // --- PR 4: range slices ---------------------------------------------

    /// <summary>
    ///     A range exactly equal to the spill and spanning 2+ cells IS the
    ///     array: it rewrites to the anchor's binding name and earns no row of
    ///     its own, because a row would only re-alias what the anchor already
    ///     binds.
    /// </summary>
    [Fact]
    public void Gather_RangeExactlyEqualToSpill_RewritesToAnchorNameWithNoNewRow()
    {
        var source = GridSource("=SUM(A2:C5)");

        var result = GatherEngine.Gather(source.Ref("D8"), source)!;

        var binding = Assert.Single(result.Bindings);
        Assert.Equal("grid", binding.Name);
        Assert.Equal("A2#", binding.Rhs);
        Assert.Null(binding.SliceOf);
        Assert.Equal("SUM(grid)", LetParser.Parse(result.SynthesisedLet).Body);
    }

    /// <summary>
    ///     Row band starting at the top: the column axis spans the full spill
    ///     so it contributes nothing, and its argument is a trailing omission
    ///     — <c>TAKE(grid,2)</c>, not <c>TAKE(grid,2,3)</c>.
    /// </summary>
    [Fact]
    public void Gather_RowBandInsideSpill_EmitsPositiveTakeWithColumnArgOmitted()
    {
        var source = GridSource("=SUM(A2:C3)");

        var result = GatherEngine.Gather(source.Ref("D8"), source)!;

        Assert.Equal(2, result.Bindings.Count);
        Assert.Equal("A2#", result.Bindings[0].Rhs);
        var slice = result.Bindings[1];
        Assert.Equal("TAKE(grid,2)", slice.Rhs);
        Assert.Equal("A2:C3", slice.Source.A1Address);
        Assert.Equal(result.Bindings[0].Source, slice.SliceOf);
        Assert.Equal($"SUM({slice.Name})", LetParser.Parse(result.SynthesisedLet).Body);
    }

    /// <summary>
    ///     A column band flush to the spill's last column is edge-relative:
    ///     <c>TAKE(grid,,-1)</c> — a negative take with the row argument
    ///     rendered as a bare interior comma. Explicitly not a counted
    ///     <c>CHOOSECOLS(grid,3)</c>.
    /// </summary>
    [Fact]
    public void Gather_ColumnBandFlushToEnd_EmitsNegativeTakeNotCountedChooseCols()
    {
        var source = GridSource("=SUM(C2:C5)").WithLabel("C1", "Last");

        var result = GatherEngine.Gather(source.Ref("D8"), source)!;

        Assert.Equal(2, result.Bindings.Count);
        var slice = result.Bindings[1];
        Assert.Equal("last", slice.Name);
        Assert.Equal("TAKE(grid,,-1)", slice.Rhs);
        Assert.DoesNotContain("CHOOSECOLS", result.SynthesisedLet, StringComparison.Ordinal);
        Assert.Equal("SUM(last)", LetParser.Parse(result.SynthesisedLet).Body);
    }

    /// <summary>
    ///     An interior block constrained on both axes composes into exactly
    ///     one <c>DROP</c> and one <c>TAKE</c>. Here the row axis is interior
    ///     (drop 1, take 2) while the column axis is flush to the last column
    ///     (take -2, no drop), so the <c>DROP</c>'s column argument is a
    ///     trailing omission and the negative take composes across axes.
    /// </summary>
    [Fact]
    public void Gather_InteriorBlockInsideSpill_EmitsOneDropAndOneTake()
    {
        var source = GridSource("=SUM(B3:C4)");

        var result = GatherEngine.Gather(source.Ref("D8"), source)!;

        Assert.Equal(2, result.Bindings.Count);
        var slice = result.Bindings[1];
        Assert.Equal("TAKE(DROP(grid,1),2,-2)", slice.Rhs);
        Assert.Equal(1, CountOccurrences(slice.Rhs, "DROP("));
        Assert.Equal(1, CountOccurrences(slice.Rhs, "TAKE("));
        // Row-major fallback name: (2-1)*3 + 2 = 5.
        Assert.Equal("grid_5", slice.Name);
    }

    /// <summary>
    ///     A one-cell range inside a multi-cell spill is a scalar in Excel, so
    ///     it takes the <c>INDEX</c> path rather than a 1×1 <c>TAKE</c>.
    /// </summary>
    [Fact]
    public void Gather_DegenerateRangeInsideMultiCellSpill_TakesScalarIndexPath()
    {
        var source = GridSource("=SUM(B3:B3)");

        var result = GatherEngine.Gather(source.Ref("D8"), source)!;

        Assert.Equal(2, result.Bindings.Count);
        Assert.Equal("INDEX(grid,2,2)", result.Bindings[1].Rhs);
        Assert.DoesNotContain("TAKE(", result.SynthesisedLet, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The 1×1 boundary case, stated as one formula: <c>A2:A2</c> is
    ///     simultaneously one cell and the whole spill, and the single-cell
    ///     rule wins — so it indexes, while <c>A2#</c> alongside it still
    ///     binds the array. Emitting <c>arr</c> for the range would hand the
    ///     step a 1×1 array, which is not a scalar.
    /// </summary>
    [Fact]
    public void Gather_OneByOneSpill_DegenerateRangeIndexesWhileSpillRefBindsArray()
    {
        var source = OneByOneSource("=SUM(A2:A2)+SUM(A2#)");

        var result = GatherEngine.Gather(source.Ref("B2"), source)!;

        Assert.Equal(2, result.Bindings.Count);
        Assert.Equal("A2#", result.Bindings[0].Rhs);
        Assert.Equal("INDEX(arr,1,1)", result.Bindings[1].Rhs);
        Assert.Equal("SUM(arr_2)+SUM(arr)", LetParser.Parse(result.SynthesisedLet).Body);
    }

    /// <summary>
    ///     Range-promotion precedence: a range wholly inside a spill never
    ///     becomes a range input, so no binding row carries a range
    ///     <see cref="BindingRow.Source" /> and the literal <c>A2:C3</c> text
    ///     never reaches the LET.
    /// </summary>
    [Fact]
    public void Gather_RangeWhollyInsideSpill_NeverPromotesToRangeInput()
    {
        var source = GridSource("=SUM(A2:C3)+SUM(B3:C4)");

        var result = GatherEngine.Gather(source.Ref("D8"), source)!;

        Assert.All(result.Bindings, b => Assert.False(b.Source.IsRange && b.SliceOf == null));
        Assert.DoesNotContain(":", result.SynthesisedLet, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A range straddling the spill's boundary is inexpressible as a slice
    ///     and promotes to a literal range input exactly as before. The
    ///     warning marker on that row is PR 5.
    /// </summary>
    [Fact]
    public void Gather_RangeStraddlingSpillBoundary_StillPromotesToRangeInput()
    {
        var source = GridSource("=SUM(A2:C6)");

        var result = GatherEngine.Gather(source.Ref("D8"), source)!;

        var binding = Assert.Single(result.Bindings);
        Assert.True(binding.Source.IsRange);
        Assert.Equal("A2:C6", binding.Rhs);
        Assert.Null(binding.SliceOf);
    }

    /// <summary>
    ///     Spec 0005 drops walked cells that fall inside a promoted range;
    ///     spec 0010 exempts spill anchors, because the anchor is the array
    ///     every slice of it indexes into. Here a straddling range covers the
    ///     anchor, and the anchor's own <c>A2#</c> row survives alongside it.
    /// </summary>
    [Fact]
    public void Gather_PromotedRangeCoveringAnchor_DoesNotDropTheAnchorRow()
    {
        var source = GridSource("=SUM(A2:C6)+E2")
            .WithLabel("E1", "Doubled")
            .WithFormula("E2", "=A2#*2");

        var result = GatherEngine.Gather(source.Ref("D8"), source)!;

        var anchor = result.Bindings.Single(b => b.Source.IsSpilled);
        Assert.Equal("A2", anchor.Source.Start.A1Address);
        Assert.Equal("A2#", anchor.Rhs);

        var range = result.Bindings.Single(b => b.Source.IsRange);
        Assert.Equal("A2:C6", range.Rhs);

        Assert.Equal($"{anchor.Name}*2",
            result.Bindings.Single(b => b.Name == "doubled").Rhs);
    }

    /// <summary>
    ///     Anchor discovery through a range: the sink's only reference into
    ///     the spill is a sub-block, and the walker still pulls the anchor in
    ///     — otherwise there would be no array for the block to slice.
    /// </summary>
    [Fact]
    public void Walk_RangeInsideSpill_PullsAnchorIn()
    {
        var source = GridSource("=SUM(B3:C4)");

        var walked = CellGraphWalker.Walk(source.Ref("D8"), source).Cells!;

        Assert.Equal(new[] { "A2", "D8" }, walked.Select(w => w.Ref.A1Address));
        Assert.True(walked.Single(w => w.Ref.A1Address == "A2").HasSpill);
    }

    /// <summary>
    ///     A range that touches no spill is still opaque — the walker records
    ///     it as a block precedent and never recurses into its cells, so the
    ///     engine promotes it as it always has.
    /// </summary>
    [Fact]
    public void Walk_RangeOutsideAnySpill_StaysOpaque()
    {
        var source = new StubCellSource()
            .WithFormula("A2", "=SEQUENCE(4,3)")
            .WithSpill("A2", 4, 3)
            .WithFormula("F1", "=1")
            .WithFormula("D8", "=SUM(F1:F3)");

        var walked = CellGraphWalker.Walk(source.Ref("D8"), source).Cells!;

        Assert.Equal(new[] { "D8" }, walked.Select(w => w.Ref.A1Address));
    }

    /// <summary>
    ///     Regression pin for the topological pass's degenerate-range edge.
    ///     <see cref="CellGraphWalker" />'s discovery leaves a one-cell range
    ///     outside any spill unwalked, but the topological pass still follows
    ///     it — and that asymmetry is the only thing that catches a circular
    ///     reference written as a self-covering degenerate range. PR 4
    ///     rewrote the guard around it; this test stops a future tidy-up from
    ///     trading the refusal for a silently self-referential LET.
    /// </summary>
    [Fact]
    public void Gather_DegenerateRangeClosingALoop_StillRefusesWithCycle()
    {
        var source = new StubCellSource()
            .WithFormula("B1", "=SUM(C1:C1)+1")
            .WithFormula("C1", "=B1*2");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        Assert.NotNull(result.Diagnostic);
        Assert.Equal(GatherDiagnosticKind.Cycle, result.Diagnostic!.Kind);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            count++;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    ///     A 4×3 spill anchored at <c>A2</c> (so <c>A2:C5</c>), labelled
    ///     <c>Grid</c> from <c>A1</c>, with the sink at <c>D8</c> well clear of
    ///     the spill rectangle.
    /// </summary>
    private static StubCellSource GridSource(string sinkFormula)
    {
        return new StubCellSource()
            .WithLabel("A1", "Grid")
            .WithFormula("A2", "=SEQUENCE(4,3)")
            .WithSpill("A2", 4, 3)
            .WithFormula("D8", sinkFormula);
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
