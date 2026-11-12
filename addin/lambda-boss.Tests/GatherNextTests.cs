using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Spec 0019, the <c>gather-next</c> Labs switch: ranges over formula cells are walked,
///     names come from the nearest label, and a gather can be written as a named LAMBDA.
///     The engine takes the switch as <see cref="GatherOptions" />, so none of this reads Settings.
/// </summary>
public class GatherNextTests
{
    private static readonly GatherOptions On = new(true);

    private static GatherResult GatherOn(StubCellSource source, string sink) =>
        GatherEngine.Gather(source.Ref(sink), new[] { source.Ref(sink) }, source, On)!;

    // -- Labs switch --------------------------------------------------------------------

    [Fact]
    public void LabsSwitch_IsListedUnderItsStableId()
    {
        Assert.Contains(Labs.GatherNext, Labs.All);
        Assert.Equal("gather-next", Labs.GatherNext.Id);
        Assert.Equal("Gather next", Labs.GatherNext.Title);
    }

    // -- §1 ranges over formula cells ----------------------------------------------------

    [Fact]
    public void Range_OverFormulaColumn_IsWalkedAndStackedWithVstack()
    {
        // A1 =B1*2, A2 =B1*3, C1 =MAX(A1:A2) (sink); B1 is a literal.
        var source = new StubCellSource()
            .WithLiteral("B1")
            .WithFormula("A1", "=B1*2")
            .WithFormula("A2", "=B1*3")
            .WithFormula("C1", "=MAX(A1:A2)");

        var result = GatherOn(source, "C1");

        Assert.Equal(
            new[] { "input_1", "step_1", "step_2", "step_1_s" },
            result.Bindings.Select(b => b.Name).ToArray());
        Assert.Equal(BindingRole.Input, result.Bindings[0].Role);
        Assert.Equal("B1", result.Bindings[0].Rhs);
        Assert.Equal("input_1*2", result.Bindings[1].Rhs);
        Assert.Equal("input_1*3", result.Bindings[2].Rhs);
        Assert.Equal(BindingRole.Step, result.Bindings[3].Role);
        Assert.Equal("VSTACK(step_1, step_2)", result.Bindings[3].Rhs);
        Assert.Equal("MAX(step_1_s)", result.Body);

        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal("MAX(step_1_s)", parsed.Body);
        Assert.Equal(4, parsed.Bindings.Count);
    }

    [Fact]
    public void Range_OverFormulaRow_IsStackedWithHstack()
    {
        var source = new StubCellSource()
            .WithLiteral("C2")
            .WithFormula("A1", "=C2*2")
            .WithFormula("B1", "=C2*3")
            .WithFormula("D1", "=SUM(A1:B1)");

        var result = GatherOn(source, "D1");

        var stack = result.Bindings.Single(b => b.Source.IsRange);
        Assert.Equal("HSTACK(step_1, step_2)", stack.Rhs);
        Assert.Equal("SUM(step_1_s)", result.Body);
    }

    [Fact]
    public void Range_OverFormulaBlock_IsStackedAsVstackOfHstackRows()
    {
        var source = new StubCellSource()
            .WithLiteral("E1")
            .WithFormula("A1", "=E1+1")
            .WithFormula("B1", "=E1+2")
            .WithFormula("A2", "=E1+3")
            .WithFormula("B2", "=E1+4")
            .WithFormula("D4", "=SUM(A1:B2)");

        var result = GatherOn(source, "D4");

        var stack = result.Bindings.Single(b => b.Source.IsRange);
        Assert.Equal("VSTACK(HSTACK(step_1, step_2), HSTACK(step_3, step_4))", stack.Rhs);
    }

    [Fact]
    public void Range_StackRowSitsBeforeTheStepThatReadsIt()
    {
        // B1 =MAX(A1:A2) is a step over the stack; C1 =B1+1 is the sink.
        var source = new StubCellSource()
            .WithLiteral("D1")
            .WithFormula("A1", "=D1*2")
            .WithFormula("A2", "=D1*3")
            .WithFormula("B1", "=MAX(A1:A2)")
            .WithFormula("C1", "=B1+1");

        var result = GatherOn(source, "C1");

        var names = result.Bindings.Select(b => b.Name).ToList();
        Assert.True(names.IndexOf("step_1_s") > names.IndexOf("step_2"));
        Assert.True(names.IndexOf("step_1_s") < names.IndexOf("step_3"));
        var reader = result.Bindings.Single(b => b.Name == "step_3");
        Assert.Equal("MAX(step_1_s)", reader.Rhs);
    }

    [Fact]
    public void Range_WithTheSwitchOff_StillPromotesToOneInput()
    {
        var source = new StubCellSource()
            .WithLiteral("B1")
            .WithFormula("A1", "=B1*2")
            .WithFormula("A2", "=B1*3")
            .WithFormula("C1", "=MAX(A1:A2)");

        var result = GatherEngine.Gather(source.Ref("C1"), source)!;

        var only = Assert.Single(result.Bindings);
        Assert.Equal("step_1", only.Name);
        Assert.Equal("A1:A2", only.Rhs);
        Assert.Equal(BindingRole.Input, only.Role);
    }

    [Fact]
    public void Range_OfLiteralsOnly_PromotesAsBefore()
    {
        var source = new StubCellSource()
            .WithLiteral("A1")
            .WithLiteral("A2")
            .WithFormula("C1", "=SUM(A1:A2)");

        var result = GatherOn(source, "C1");

        var only = Assert.Single(result.Bindings);
        Assert.Equal("A1:A2", only.Rhs);
        Assert.Equal(BindingRole.Input, only.Role);
        Assert.Equal("input_1", only.Name);
    }

    [Fact]
    public void Range_OverTheCellLimit_PromotesAsBefore()
    {
        var source = new StubCellSource()
            .WithFormula("A1", "=B1")
            .WithLiteral("B1")
            .WithFormula("C1", "=SUM(A1:A65)");

        var result = GatherOn(source, "C1");

        var only = Assert.Single(result.Bindings);
        Assert.Equal("A1:A65", only.Rhs);
        Assert.DoesNotContain("STACK", result.SynthesisedLet);
    }

    [Fact]
    public void Range_ExactlyAtTheCellLimit_IsWalked()
    {
        var source = new StubCellSource()
            .WithLiteral("B1")
            .WithFormula("A1", "=B1")
            .WithFormula("C1", "=SUM(A1:A64)");

        var result = GatherOn(source, "C1");

        Assert.Contains(result.Bindings, b => b.Source.IsRange && b.Rhs.StartsWith("VSTACK("));
    }

    [Fact]
    public void Range_OverASpill_IsNotWalked()
    {
        var source = new StubCellSource()
            .WithFormula("A1", "=SEQUENCE(3)")
            .WithSpill("A1", 3, 1)
            .WithFormula("C1", "=SUM(A1:A3)");

        var result = GatherOn(source, "C1");

        Assert.DoesNotContain("STACK", result.SynthesisedLet);
        var only = Assert.Single(result.Bindings);
        Assert.Equal("A1#", only.Rhs);
    }

    [Fact]
    public void Range_StraddlingASpill_PromotesAsBefore()
    {
        var source = new StubCellSource()
            .WithFormula("A1", "=SEQUENCE(2)")
            .WithSpill("A1", 2, 1)
            .WithFormula("C1", "=SUM(A1:A3)");

        var result = GatherOn(source, "C1");

        Assert.DoesNotContain("STACK", result.SynthesisedLet);
        Assert.Contains(result.Bindings, b => b.Source.IsRange && b.Rhs == "A1:A3");
    }

    [Fact]
    public void Range_ExcludedStackRow_RevertsToTheLiteralRange()
    {
        var source = new StubCellSource()
            .WithLiteral("B1")
            .WithFormula("A1", "=B1*2")
            .WithFormula("A2", "=B1*3")
            .WithFormula("C1", "=MAX(A1:A2)");
        var first = GatherOn(source, "C1");
        var stackSource = first.Bindings.Single(b => b.Source.IsRange).Source;

        var result = GatherEngine.Recompute(
            source.Ref("C1"),
            new[] { source.Ref("C1") },
            source,
            new[] { new RowState(stackSource, Include: false) },
            On)!;

        Assert.DoesNotContain("STACK", result.SynthesisedLet);
        Assert.Equal("MAX(A1:A2)", result.Body);
    }

    // -- §2 names -----------------------------------------------------------------------

    [Fact]
    public void Label_TwoRowsUpOverABlankRow_NamesTheInput()
    {
        var source = new StubCellSource()
            .WithLabel("G81", "Trump Suit")
            .WithLiteral("G83")
            .WithFormula("H83", "=G83&\"!\"");

        var result = GatherOn(source, "H83");

        Assert.Equal("trumpSuit", Assert.Single(result.Bindings).Name);
    }

    [Fact]
    public void Label_ThreeRowsUp_IsTheLimit()
    {
        var withinReach = new StubCellSource()
            .WithLabel("G80", "Reach")
            .WithLiteral("G83")
            .WithFormula("H83", "=G83&\"!\"");
        var beyondReach = new StubCellSource()
            .WithLabel("G79", "Reach")
            .WithLiteral("G83")
            .WithFormula("H83", "=G83&\"!\"");

        Assert.Equal("reach", Assert.Single(GatherOn(withinReach, "H83").Bindings).Name);
        Assert.Equal("input_1", Assert.Single(GatherOn(beyondReach, "H83").Bindings).Name);
    }

    [Fact]
    public void Label_IsNotReadPastANonBlankValue()
    {
        var source = new StubCellSource()
            .WithLabel("G81", "Trump Suit")
            .WithLiteral("G82")
            .WithLiteral("G83")
            .WithFormula("H83", "=G83&\"!\"");

        var result = GatherOn(source, "H83");

        Assert.Equal("input_1", Assert.Single(result.Bindings).Name);
    }

    [Fact]
    public void Label_ToTheLeft_IsUsedWhenNothingAbove()
    {
        var source = new StubCellSource()
            .WithLabel("A2", "Rate")
            .WithLiteral("B2")
            .WithFormula("C2", "=B2*2");

        var result = GatherOn(source, "C2");

        Assert.Equal("rate", Assert.Single(result.Bindings).Name);
    }

    [Fact]
    public void Label_ThatIsPartOfTheCalculation_IsRejected()
    {
        // A1 ="x" (shows text "x") feeds A2 =A1&"!", which feeds the sink A3. A2's cell
        // above is A1, a walked cell, so its text is a value and not a label.
        var source = new StubCellSource()
            .WithFormula("A1", "=\"x\"")
            .WithLabel("A1", "x")
            .WithFormula("A2", "=A1&\"!\"")
            .WithFormula("A3", "=A2&\"?\"");

        var result = GatherOn(source, "A3");

        Assert.Equal(
            new[] { "input_1", "step_1" },
            result.Bindings.Select(b => b.Name).ToArray());
    }

    [Fact]
    public void Fallbacks_AreNumberedSeparatelyForInputsAndSteps()
    {
        var source = new StubCellSource()
            .WithLiteral("A1")
            .WithLiteral("B1")
            .WithFormula("A2", "=A1+B1")
            .WithFormula("A3", "=A2*2")
            .WithFormula("A4", "=A3+1");

        var result = GatherOn(source, "A4");

        Assert.Equal(
            new[] { "input_1", "input_2", "step_1", "step_2" },
            result.Bindings.Select(b => b.Name).ToArray());
    }

    [Fact]
    public void Slice_WhoseLeftNeighbourIsTheSpillsOwnData_IsNotNamedFromIt()
    {
        // AE83# spills 10 rows x 2 columns of cards; AE83 shows "KH". AF83:AF92 is the
        // second column, and the cell to its left is spill data, not a label.
        var source = new StubCellSource()
            .WithLabel("AE82", "Deal")
            .WithFormula("AE83", "=DEAL()")
            .WithLabel("AE83", "KH")
            .WithSpill("AE83", 10, 2)
            .WithFormula("AH83", "=TEXTJOIN(\",\",,AF83:AF92)");

        var result = GatherOn(source, "AH83");

        Assert.DoesNotContain(result.Bindings, b => b.Name == "kH");
        var slice = Assert.Single(result.Bindings, b => b.SliceOf != null);
        Assert.Equal("deal_2", slice.Name);
    }

    [Fact]
    public void Slice_UnderItsAnchorsLabel_TakesTheAnchorDerivedName()
    {
        // X57# spills X57:Y57 under the label "coords" in X56.
        var source = new StubCellSource()
            .WithLabel("X56", "coords")
            .WithFormula("X57", "=PARSE()")
            .WithSpill("X57", 1, 2)
            .WithFormula("AA57", "=X57&Y57");

        var result = GatherOn(source, "AA57");

        Assert.Equal(
            new[] { "coords", "coords_1", "coords_2" },
            result.Bindings.Select(b => b.Name).ToArray());
    }

    // -- input order (parameter order) ---------------------------------------------------

    [Fact]
    public void Recompute_FollowsTheRowOrderForInputs()
    {
        var source = new StubCellSource()
            .WithLiteral("A1")
            .WithLiteral("B1")
            .WithFormula("C1", "=A1-B1");
        var first = GatherOn(source, "C1");
        Assert.Equal(new[] { "A1", "B1" }, first.Bindings.Select(b => b.Source.A1Address).ToArray());

        var reversed = first.Bindings.Reverse().Select(b => new RowState(b.Source)).ToList();
        var result = GatherEngine.Recompute(
            source.Ref("C1"), new[] { source.Ref("C1") }, source, reversed, On)!;

        Assert.Equal(new[] { "B1", "A1" }, result.Bindings.Select(b => b.Source.A1Address).ToArray());
        // Names belong to the cells, not the position.
        Assert.Equal("input_2", result.Bindings[0].Name);
    }

    [Fact]
    public void Recompute_KeepsASliceUnderItsAnchorWhenInputsMove()
    {
        var source = new StubCellSource()
            .WithLiteral("A1")
            .WithFormula("C1", "=SEQUENCE(2)")
            .WithSpill("C1", 2, 1)
            .WithFormula("E1", "=A1+C1+C2");
        var first = GatherOn(source, "E1");

        var anchor = first.Bindings.Single(b => b.Source.IsSpilled);
        var plain = first.Bindings.Single(b => b.Source.A1Address == "A1");
        var rows = new[] { anchor, plain }.Select(b => new RowState(b.Source)).ToList();

        var result = GatherEngine.Recompute(
            source.Ref("E1"), new[] { source.Ref("E1") }, source, rows, On)!;

        var order = result.Bindings.Select(b => b.Source.A1Address + (b.SliceOf != null ? "/slice" : "")).ToList();
        Assert.True(order.IndexOf("C1") < order.IndexOf("A1"));
        // Every slice row of the anchor comes directly after it.
        var anchorIndex = order.IndexOf("C1");
        Assert.EndsWith("/slice", order[anchorIndex + 1]);
    }

    // -- §3 the LAMBDA text --------------------------------------------------------------

    private static BindingRow Input(string name, string rhs) =>
        new(new FormulaRef(new CellRef("Sheet1", 1, 1)), BindingRole.Input, name, rhs);

    private static BindingRow Step(string name, string rhs) =>
        new(new FormulaRef(new CellRef("Sheet1", 2, 1)), BindingRole.Step, name, rhs);

    [Fact]
    public void LambdaBuilder_InputsBecomeParametersAndStepsStayInTheLet()
    {
        var bindings = new[] { Input("input_1", "B1"), Step("step_1", "input_1*2") };

        var lambda = GatherLambdaBuilder.Build("calc", bindings, "step_1+1")!;

        Assert.Equal(
            "=LAMBDA(\n    input_1,\n    LET(\n        step_1, input_1*2,\n        step_1+1\n    )\n)",
            lambda.Formula);
        Assert.Equal("=calc(B1)", lambda.Call);
        Assert.Equal("calc", lambda.Name);
    }

    [Fact]
    public void LambdaBuilder_ALetWithNoStepsLeftCollapsesToTheBody()
    {
        var bindings = new[] { Input("input_1", "B1") };

        var lambda = GatherLambdaBuilder.Build("dbl", bindings, "input_1*2")!;

        Assert.Equal("=LAMBDA(\n    input_1,\n    input_1*2\n)", lambda.Formula);
    }

    [Fact]
    public void LambdaBuilder_ParametersAndArgumentsFollowRowOrder_RefsKeepTheirWrittenForm()
    {
        var a = new BindingRow(new FormulaRef(new CellRef("Sheet1", 11, 287), IsSpilled: true),
            BindingRole.Input, "deck", "K287#");
        var b = new BindingRow(
            new FormulaRef(new CellRef("Sheet1", 17, 57), new CellRef("Sheet1", 20, 60)),
            BindingRole.Input, "grid", "Q57:T60");
        var c = Input("rate", "A1");

        var lambda = GatherLambdaBuilder.Build("play", new[] { b, c, a }, "grid+rate+deck")!;

        Assert.Equal("=play(Q57:T60, A1, K287#)", lambda.Call);
        Assert.StartsWith("=LAMBDA(\n    grid,\n    rate,\n    deck,\n", lambda.Formula);
    }

    [Fact]
    public void LambdaBuilder_SlicesAndInnerLetBindingsAreNotParameters()
    {
        var anchor = new BindingRow(
            new FormulaRef(new CellRef("Sheet1", 1, 5), IsSpilled: true),
            BindingRole.Input, "coords", "A5#");
        var slice = new BindingRow(
            new FormulaRef(new CellRef("Sheet1", 1, 5)),
            BindingRole.Input, "coords_1", "INDEX(coords,1,1)",
            SliceOf: anchor.Source);
        var inner = new BindingRow(
            new FormulaRef(new CellRef("Sheet1", 3, 3)),
            BindingRole.Input, "k", "5", IsExpansion: true);

        var lambda = GatherLambdaBuilder.Build("f", new[] { anchor, slice, inner }, "coords_1+k")!;

        Assert.Equal("=f(A5#)", lambda.Call);
        Assert.Contains("coords_1, INDEX(coords,1,1),", lambda.Formula);
        Assert.Contains("k, 5,", lambda.Formula);
        Assert.StartsWith("=LAMBDA(\n    coords,\n    LET(", lambda.Formula);
    }

    [Fact]
    public void LambdaBuilder_ALeftoverCellReference_IsMadeAbsolute()
    {
        var bindings = new[] { Input("input_1", "B1"), Step("step_1", "input_1+C3") };

        var lambda = GatherLambdaBuilder.Build("f", bindings, "step_1")!;

        Assert.Contains("step_1, input_1+$C$3,", lambda.Formula);
    }

    [Fact]
    public void LambdaBuilder_NoInputs_ReturnsNull()
    {
        Assert.Null(GatherLambdaBuilder.Build("f", new[] { Step("step_1", "1+1") }, "step_1"));
    }

    [Fact]
    public void LambdaBuilder_OverAGatherResult_RegistersTheStackedCalculation()
    {
        var source = new StubCellSource()
            .WithLiteral("B1")
            .WithFormula("A1", "=B1*2")
            .WithFormula("A2", "=B1*3")
            .WithFormula("C1", "=MAX(A1:A2)");
        var result = GatherOn(source, "C1");

        var lambda = GatherLambdaBuilder.Build("peak", result.Bindings, result.Body!)!;

        Assert.Equal("=peak(B1)", lambda.Call);
        Assert.Contains("step_1_s, VSTACK(step_1, step_2),", lambda.Formula);
        Assert.Contains("MAX(step_1_s)", lambda.Formula);
        Assert.StartsWith("=LAMBDA(\n    input_1,\n", lambda.Formula);
    }
}
