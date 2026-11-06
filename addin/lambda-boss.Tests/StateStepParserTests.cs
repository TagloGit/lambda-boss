using Xunit;

namespace LambdaBoss.Tests;

public class StateStepParserTests
{
    private const string RobotStep =
        "=LET(a, SetVars(\"p\", I266, \"b\", I267), v, I268, " +
        "p, GetVar(a, \"p\"), b, GetVar(a, \"b\"), " +
        "nextP, p + IF(b > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)), " +
        "nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)), " +
        "SetVars(\"p\", nextP, \"b\", nextB))";

    private static StateStep? Parse(string formula) => StateStepParser.TryParse(LetParser.Parse(formula));

    private static StateStep Step(string formula) =>
        Parse(formula) ?? throw new InvalidOperationException("Not recognised as a state step.");

    [Fact]
    public void RobotStep_ReturnsEveryRole()
    {
        var step = Step(RobotStep);

        Assert.Equal(StateFamily.SetVars, step.Family);
        Assert.Equal("a", step.StateBinding.Name);
        Assert.Equal("v", step.InputBinding!.Name);
        Assert.Equal("SetVars(\"p\", nextP, \"b\", nextB)", step.Output);

        Assert.Collection(step.Variables,
            p =>
            {
                Assert.Equal("p", p.Key);
                Assert.Equal("p", p.BindingName);
                Assert.Equal("GetVar(a, \"p\")", p.GetVarLine!.RhsText);
                Assert.Equal("nextP", p.NextLine!.Name);
            },
            b =>
            {
                Assert.Equal("b", b.Key);
                Assert.Equal("b", b.BindingName);
                Assert.Equal("nextB", b.NextLine!.Name);
            });
    }

    [Fact]
    public void BindingNameCanDifferFromKey()
    {
        var step = Step("=LET(s, A1, v, B1, c_, GetVar(s, \"c\"), nextC, c_ + v, SetVars(\"c\", nextC))");

        Assert.Equal("s", step.StateBinding.Name);
        Assert.Equal("c", step.Variables.Single().Key);
        Assert.Equal("c_", step.Variables.Single().BindingName);
    }

    [Fact]
    public void CellBoundState_IsRecognised()
    {
        var step = Step("=LET(a, I266, v, H270, p, GetVar(a, \"p\"), nextP, p + v, SetVars(\"p\", nextP))");

        Assert.Equal("a", step.StateBinding.Name);
        Assert.False(step.StateBinding.IsCalculation);
        Assert.Equal("v", step.InputBinding!.Name);
    }

    [Fact]
    public void InputAfterGetVarLines_IsFound()
    {
        var step = Step(
            "=LET(a, SetVars(\"p\", 0), p, GetVar(a, \"p\"), v, H270, nextP, p + v, SetVars(\"p\", nextP))");

        Assert.Equal("v", step.InputBinding!.Name);
    }

    [Fact]
    public void InputNotNamedV_PicksSingleCellOverConstantAndLiteral()
    {
        var step = Step(
            "=LET(a, SetVars(\"p\", 0), maxB, $K$2, cap, 6, inp, H270, p, GetVar(a, \"p\"), " +
            "nextP, MIN(cap, maxB, p + inp), SetVars(\"p\", nextP))");

        Assert.Equal("inp", step.InputBinding!.Name);
    }

    [Fact]
    public void InputNotNamedV_IndexCounts()
    {
        var step = Step(
            "=LET(a, SetVars(\"p\", 0), cap, 6, inp, INDEX(G1:G9, 1), p, GetVar(a, \"p\"), " +
            "nextP, MIN(cap, p + inp), SetVars(\"p\", nextP))");

        Assert.Equal("inp", step.InputBinding!.Name);
    }

    [Fact]
    public void InputNotNamedV_FallsBackToFirstCandidate()
    {
        var step = Step(
            "=LET(a, SetVars(\"p\", 0), one, 1, two, 2, p, GetVar(a, \"p\"), " +
            "nextP, p + two + one, SetVars(\"p\", nextP))");

        Assert.Equal("one", step.InputBinding!.Name);
    }

    [Fact]
    public void InputReadOnlyThroughHelperLine_IsStillFound()
    {
        var step = Step(
            "=LET(a, SetVars(\"p\", 0), inp, H270, dir, IF(inp = \"F\", 1, -1), p, GetVar(a, \"p\"), " +
            "nextP, p + dir, SetVars(\"p\", nextP))");

        Assert.Equal("inp", step.InputBinding!.Name);
    }

    [Fact]
    public void InputReadDirectlyByOutputExpression_IsFound()
    {
        var step = Step(
            "=LET(a, SetVars(\"p\", 0), inp, H270, p, GetVar(a, \"p\"), SetVars(\"p\", p + inp))");

        Assert.Equal("inp", step.InputBinding!.Name);
        Assert.Null(step.Variables.Single().NextLine);
    }

    [Fact]
    public void NoInputCandidate_LeavesInputNull()
    {
        var step = Step("=LET(a, SetVars(\"p\", 0), p, GetVar(a, \"p\"), nextP, p + 1, SetVars(\"p\", nextP))");

        Assert.Null(step.InputBinding);
    }

    [Fact]
    public void BindingThatReadsTheStateThroughALine_IsNotTheInput()
    {
        var step = Step(
            "=LET(a, SetVars(\"p\", 0), p, GetVar(a, \"p\"), twice, p * 2, nextP, twice + 1, SetVars(\"p\", nextP))");

        Assert.Null(step.InputBinding);
    }

    [Fact]
    public void BindingNamedVThatIsAGetVarLine_IsNotTheInput()
    {
        var step = Step(
            "=LET(a, SetVars(\"v\", 0), v, GetVar(a, \"v\"), inp, H1, nextV, v + inp, SetVars(\"v\", nextV))");

        Assert.Equal("inp", step.InputBinding!.Name);
    }

    [Theory]
    [InlineData("v, p + inp")]
    [InlineData("v, GetVar(a, \"p\") + inp")]
    public void CalculationNamedVThatReadsTheState_IsNotTheInput(string line)
    {
        var step = Step($"=LET(a, A1, inp, B1, p, GetVar(a, \"p\"), {line}, SetVars(\"p\", v))");

        Assert.Equal("inp", step.InputBinding!.Name);
    }

    [Fact]
    public void InputAlsoStoredInTheState_IsStillTheInput()
    {
        var step = Step(
            "=LET(a, SetVars(\"p\", 0), cap, 6, inp, H270, p, GetVar(a, \"p\"), nextP, MIN(cap, p + (inp = \"F\")), " +
            "SetVars(\"p\", nextP, \"last\", inp))");

        Assert.Equal("inp", step.InputBinding!.Name);
    }

    [Fact]
    public void ConstantNextLine_IsNotTheInputOfLastResort()
    {
        var step = Step("=LET(a, SetVars(\"p\", 0), p, GetVar(a, \"p\"), nextQ, 0, SetVars(\"p\", p + 1, \"q\", nextQ))");

        Assert.Null(step.InputBinding);
    }

    [Fact]
    public void ExtraConstantAndCommentBindings_AreNotRoles()
    {
        var step = Step(
            "=LET(_note, \"battery caps at 6\", a, SetVars(\"b\", 4), v, H270, maxB, $K$2, " +
            "b, GetVar(a, \"b\"), nextB, MIN(maxB, b + v), SetVars(\"b\", nextB))");

        Assert.Equal("a", step.StateBinding.Name);
        Assert.Equal("v", step.InputBinding!.Name);
        Assert.Equal(new[] { "b" }, step.Variables.Select(x => x.Key));
    }

    [Fact]
    public void NonCanonicalLines_CountAsReadsButNotAsGetVarLines()
    {
        var step = Step(
            "=LET(a, SetVars(\"p\", 0, \"q\", 1), v, H1, p, GetVar(a, \"p\") + 0, q, GetVar(a, \"q\"), " +
            "nextP, p + v, nextQ, q + GetVar(a, \"p\"), SetVars(\"p\", nextP, \"q\", nextQ))");

        var p = step.Variables.Single(x => x.Key == "p");
        var q = step.Variables.Single(x => x.Key == "q");
        Assert.Null(p.GetVarLine);
        Assert.Equal("nextP", p.NextLine!.Name);
        Assert.Equal("q", q.GetVarLine!.Name);
    }

    [Fact]
    public void NestedLetInNextLine_IsRecognised()
    {
        var step = Step(
            "=LET(a, SetVars(\"p\", 0), v, H1, p, GetVar(a, \"p\"), " +
            "nextP, LET(x, p + 1, y, x * v, y), SetVars(\"p\", nextP))");

        Assert.Equal("v", step.InputBinding!.Name);
        Assert.Equal("nextP", step.Variables.Single().NextLine!.Name);
    }

    [Fact]
    public void KeyInOutputButNeverRead_IsListedLast()
    {
        var step = Step(
            "=LET(a, SetVars(\"p\", 0), v, H1, p, GetVar(a, \"p\"), SetVars(\"seen\", v, \"p\", p + 1))");

        Assert.Equal(new[] { "p", "seen" }, step.Variables.Select(x => x.Key));
    }

    [Fact]
    public void SetVarChain_IsRecognised()
    {
        var step = Step(
            "=LET(a, A1, v, B1, p, GetVar(a, \"p\"), b, GetVar(a, \"b\"), nextP, p + v, nextB, b - 1, " +
            "SetVar(SetVar(a, \"p\", nextP), \"b\", nextB))");

        Assert.Equal(StateFamily.SetVars, step.Family);
        Assert.Equal("nextP", step.Variables.Single(x => x.Key == "p").NextLine!.Name);
        Assert.Equal("nextB", step.Variables.Single(x => x.Key == "b").NextLine!.Name);
    }

    [Fact]
    public void SetVarChainOnSetVars_IsRecognised()
    {
        // Part 1 writes this past SetVars' ten pairs.
        var step = Step(
            "=LET(a, A1, v, B1, p, GetVar(a, \"p\"), k, GetVar(a, \"k\"), nextP, p + v, nextK, k + 1, " +
            "SetVar(SetVars(\"p\", nextP), \"k\", nextK))");

        Assert.Equal("nextK", step.Variables.Single(x => x.Key == "k").NextLine!.Name);
    }

    [Fact]
    public void SetVarChainRootedElsewhere_IsNotAStep()
    {
        Assert.Null(Parse(
            "=LET(a, A1, other, B1, p, GetVar(a, \"p\"), SetVar(other, \"p\", p + 1))"));
    }

    [Fact]
    public void Prefixes_AreAccepted()
    {
        var step = Step(
            "=LET(a, loops.SetVars(\"p\", 0), v, H1, p, loops.GetVar(a, \"p\"), nextP, p + v, " +
            "loops.SetVars(\"p\", nextP))");

        Assert.Equal("a", step.StateBinding.Name);
    }

    [Theory]
    [InlineData("=LET(a, A1, v, B1, p, MyGetVar(a, \"p\"), SetVars(\"p\", p))")]
    [InlineData("=LET(a, A1, v, B1, p, GetVar(a, \"p\"), MySetVars(\"p\", p))")]
    [InlineData("=LET(a, A1, v, B1, p, GetVar(a, \"p\"), SetVars(name, p))")]
    [InlineData("=LET(a, A1, v, B1, p, GetVar(a, name), SetVars(\"p\", p))")]
    [InlineData("=LET(a, A1, v, B1, p, GetVar(a, \"p\"), p + 1)")]
    [InlineData("=LET(a, A1, b, a + 1, SetVars(\"p\", b))")]
    [InlineData("=LET(x, 1, y, 2, SUM(x, y))")]
    [InlineData("=LET(p, GetVar(A1, \"p\"), SetVars(\"p\", p))")]
    public void NonSteps_AreRejected(string formula)
    {
        Assert.Null(Parse(formula));
    }

    [Fact]
    public void MostReadBindingIsTheState_TiesGoToTheEarliest()
    {
        var mostRead = Step(
            "=LET(s, A1, t, B1, x, GetVar(s, \"x\"), y, GetVar(t, \"y\"), z, GetVar(t, \"z\"), " +
            "SetVars(\"x\", x, \"y\", y, \"z\", z))");
        var tie = Step(
            "=LET(s, A1, t, B1, x, GetVar(t, \"x\"), y, GetVar(s, \"y\"), SetVars(\"x\", x, \"y\", y))");

        Assert.Equal("t", mostRead.StateBinding.Name);
        Assert.Equal("s", tie.StateBinding.Name);
    }

    [Fact]
    public void ArrayFamily_IndexState()
    {
        var step = Step(
            "=LET(a, HSTACK(H268, I268), v, H270, p, INDEX(a, 1), b, INDEX(a, 2), " +
            "nextP, p + v, nextB, b - 1, HSTACK(nextP, nextB))");

        Assert.Equal(StateFamily.Array, step.Family);
        Assert.Equal("a", step.StateBinding.Name);
        Assert.Equal("v", step.InputBinding!.Name);
        Assert.Equal(new[] { "1", "2" }, step.Variables.Select(x => x.Key));
        Assert.Equal("nextB", step.Variables[1].NextLine!.Name);
    }

    [Theory]
    [InlineData("NTH(a, 1)", "NTH(a, 2)")]
    [InlineData("CHOOSECOLS(a, 1)", "CHOOSECOLS(a, 2)")]
    [InlineData("INDEX(a, 1, 1)", "INDEX(a, 1, 2)")]
    [InlineData("INDEX(a, , 1)", "INDEX(a, , 2)")]
    [InlineData("INDEX(a, 1, 1)", "INDEX(a, 2, 1)")]
    public void ArrayFamily_OtherReads(string first, string second)
    {
        var step = Step(
            $"=LET(a, VSTACK(0, 4), v, H1, p, {first}, b, {second}, nextP, p + v, nextB, b - 1, " +
            "VSTACK(nextP, nextB))");

        Assert.Equal(new[] { "1", "2" }, step.Variables.Select(x => x.Key));
        Assert.All(step.Variables, x => Assert.NotNull(x.GetVarLine));
    }

    [Theory]
    [InlineData("=LET(a, A1:A5, x, INDEX(a, 7), HSTACK(x, x + 1))")]
    [InlineData("=LET(a, A1:A5, x, INDEX(a, n), HSTACK(x, x + 1))")]
    [InlineData("=LET(a, A1:A5, x, INDEX(a, 1), HSTACK(x))")]
    [InlineData("=LET(a, A1:A5, x, INDEX(a, 2, 3), HSTACK(x, x + 1))")]
    [InlineData("=LET(d, A1:C10, x, INDEX(d, 2), y, INDEX(d, 3), HSTACK(x, y))")]
    [InlineData("=LET(t, SORT(A1:A9), lo, INDEX(t, 1), hi, INDEX(t, 2), med, MEDIAN(t), VSTACK(lo, hi, med))")]
    [InlineData("=LET(t, Table1[Name], first, NTH(t, 1), HSTACK(first, COUNTA(t)))")]
    public void ArrayFamily_NonSteps(string formula)
    {
        Assert.Null(Parse(formula));
    }

    [Fact]
    public void GetVarInAnArrayBody_DoesNotMixFamilies()
    {
        Assert.Null(Parse("=LET(a, A1, p, GetVar(a, \"p\"), HSTACK(p, p + 1))"));
    }

    [Fact]
    public void UnparsableRhs_IsNotAStep()
    {
        var parsed = new ParsedLet(
            new[] { new LetBinding("a", "A1", false), new LetBinding("p", "1 +", true) },
            "SetVars(\"p\", p)");

        Assert.Null(StateStepParser.TryParse(parsed));
    }

    [Fact]
    public void DialogBindings_StateThenInputThenOtherValues()
    {
        var parsed = LetParser.Parse(
            "=LET(cap, 6, _note, \"x\", p, GetVar(a2, \"p\"), a2, SetVars(\"p\", 0), v, H1, nextP, MIN(cap, p + v), " +
            "SetVars(\"p\", nextP))");

        var rows = StateStepParser.DialogBindings(parsed, StateStepParser.TryParse(parsed)!);

        Assert.Equal(new[] { "a2", "v", "cap", "_note" }, rows.Select(b => b.Name));
    }

    [Fact]
    public void DialogBindings_CalculationInputIsListed()
    {
        var parsed = LetParser.Parse(
            "=LET(a, SetVars(\"p\", 0), v, INDEX(G1:G9, 1), p, GetVar(a, \"p\"), nextP, p + v, SetVars(\"p\", nextP))");

        var rows = StateStepParser.DialogBindings(parsed, StateStepParser.TryParse(parsed)!);

        Assert.Equal(new[] { "a", "v" }, rows.Select(b => b.Name));
    }
}

public class StateFoldTests
{
    private static string Seed(string formula)
    {
        var parsed = LetParser.Parse(formula);
        return StateFold.Seed(parsed, StateStepParser.TryParse(parsed)!);
    }

    [Fact]
    public void Seed_AbsolutisesSetVarsReferences()
    {
        Assert.Equal(
            "SetVars(\"p\", $H$268, \"b\", $I$268)",
            Seed("=LET(a, SetVars(\"p\", H268, \"b\", I268), v, H270, p, GetVar(a, \"p\"), SetVars(\"p\", p + v))"));
    }

    [Fact]
    public void Seed_KeepsAKeyThatLooksLikeACell()
    {
        Assert.Equal(
            "SetVars(\"b4\", $C$3)",
            Seed("=LET(a, SetVars(\"b4\", C3), v, H1, p, GetVar(a, \"b4\"), SetVars(\"b4\", p + v))"));
    }

    [Fact]
    public void Seed_ArrayState()
    {
        Assert.Equal(
            "HSTACK($H$268, 4)",
            Seed("=LET(a, HSTACK(H268, 4), v, H1, p, INDEX(a, 1), HSTACK(p + v, 0))"));
    }

    [Fact]
    public void Seed_EmptyForACellState()
    {
        Assert.Equal("", Seed("=LET(a, I266, v, H1, p, GetVar(a, \"p\"), SetVars(\"p\", p + v))"));
    }

    [Fact]
    public void Seed_EmptyForAnIndexOfScan()
    {
        Assert.Equal("", Seed(
            "=LET(a, INDEX(SCAN(\"\", G1:G9, f), 2), v, H1, p, GetVar(a, \"p\"), SetVars(\"p\", p + v))"));
    }

    [Fact]
    public void Seed_WritesOtherBindingsInPlace()
    {
        // The input1/input2 detour: the fold's cell can't see the LET's names.
        Assert.Equal(
            "HSTACK($H$268, ($I$268+1))",
            Seed("=LET(input1, H268, input2, I268+1, a, HSTACK(input1, input2), v, H1, " +
                 "p, INDEX(a, 1), HSTACK(p + v, 0))"));
    }

    [Fact]
    public void Seed_WritesChainsOfBindingsInPlace()
    {
        Assert.Equal(
            "SetVars(\"p\", SUM($H$1, 1))",
            Seed("=LET(x, H1, y, SUM(x, 1), a, SetVars(\"p\", y), v, H2, p, GetVar(a, \"p\"), SetVars(\"p\", p + v))"));
    }

    [Fact]
    public void Seed_IsOneLine()
    {
        Assert.Equal(
            "SetVars( \"p\", 0, \"b\", 4 )",
            Seed("=LET(a, SetVars(\n    \"p\", 0,\n    \"b\", 4\n), v, H1, p, GetVar(a, \"p\"), SetVars(\"p\", p + v))"));
    }

    [Fact]
    public void Seed_LeavesFunctionsSheetsAndTablesAlone()
    {
        Assert.Equal(
            "SetVars(\"p\", LOG10($H$1), \"q\", 'L12'!$H$1, \"r\", t[b])",
            Seed("=LET(a, SetVars(\"p\", LOG10(H1), \"q\", 'L12'!H1, \"r\", t[b]), v, H2, b, GetVar(a, \"p\"), " +
                 "SetVars(\"p\", b + v))"));
    }

    [Fact]
    public void Seed_AlreadyAbsoluteStaysSingleDollared()
    {
        Assert.Equal(
            "SetVars(\"p\", $H$1, \"q\", $I$1)",
            Seed("=LET(i, H1, a, SetVars(\"p\", i, \"q\", $I$1), v, H2, p, GetVar(a, \"p\"), SetVars(\"p\", p + v))"));
    }

    [Fact]
    public void Seed_KeepsLineBreaksInsideStrings()
    {
        Assert.Equal(
            "SetVars(\"p\", \"x\n  y\")",
            Seed("=LET(a, SetVars(\"p\", \"x\n  y\"), v, H2, p, GetVar(a, \"p\"), SetVars(\"p\", p & v))"));
    }

    [Fact]
    public void Build_LeavesTheInputsSlotEmpty()
    {
        Assert.Equal("=REDUCE(SetVars(\"p\", 0), , moveRobot)", StateFold.Build("SetVars(\"p\", 0)", "moveRobot"));
    }

    [Fact]
    public void InputsCaret_SitsInTheEmptySlot()
    {
        var fold = StateFold.Build("seed", "step");

        Assert.Equal("=REDUCE(seed, ".Length, StateFold.InputsCaret(fold));
    }

    [Fact]
    public void InputsCaret_MinusOneOnceFilled()
    {
        Assert.Equal(-1, StateFold.InputsCaret("=REDUCE(seed, G1#, step)"));
    }

    [Fact]
    public void InputsCaret_MinusOneWhenUnparsable()
    {
        Assert.Equal(-1, StateFold.InputsCaret("=REDUCE(seed, "));
    }

    [Fact]
    public void WithLambdaName_FillsAnEmptyNameAndChangesIt()
    {
        var empty = StateFold.Build("seed", "");
        var named = StateFold.WithLambdaName(empty, "", "moveRobot");

        Assert.Equal("=REDUCE(seed, , moveRobot)", named);
        Assert.Equal("=REDUCE(seed, , go)", StateFold.WithLambdaName(named, "moveRobot", "go"));
        Assert.Equal(empty, StateFold.WithLambdaName(named, "moveRobot", ""));
    }

    [Fact]
    public void WithLambdaName_KeepsTheRestOfAnEditedFold()
    {
        Assert.Equal(
            "=\"P\"&TEXTJOIN(\" \",,REDUCE(seed, G1#, go))",
            StateFold.WithLambdaName("=\"P\"&TEXTJOIN(\" \",,REDUCE(seed, G1#, step))", "step", "go"));
    }

    [Fact]
    public void WithLambdaName_FindsTheFoldThatUsesTheOldName()
    {
        Assert.Equal(
            "=SUM(SCAN(0, A1:A3, helper), REDUCE(s, G1#, go))",
            StateFold.WithLambdaName("=SUM(SCAN(0, A1:A3, helper), REDUCE(s, G1#, old))", "old", "go"));
    }

    [Theory]
    [InlineData("=REDUCE(seed, (")]
    [InlineData("=REDUCE(seed, G1#, LAMBDA(a, v, a + v))")]
    [InlineData("=REDUCE(seed, G1#, someOtherStep)")]
    public void WithLambdaName_LeavesAnythingElseAlone(string fold)
    {
        Assert.Equal(fold, StateFold.WithLambdaName(fold, "step", "go"));
    }

    [Theory]
    [InlineData("=REDUCE(seed, G1#, step)", true)]
    [InlineData("=SCAN(seed, G1#, step)", true)]
    [InlineData("=\"P\"&TEXTJOIN(\" \",,REDUCE(seed, G1#, step))", true)]
    [InlineData("=REDUCE(seed, , step)", false)]
    [InlineData("=REDUCE(, G1#, step)", false)]
    [InlineData("=REDUCE(seed, G1#, )", false)]
    [InlineData("=REDUCE(seed, (, step)", false)]
    [InlineData("=SUM(A1)", false)]
    [InlineData("REDUCE(seed, G1#, step)", false)]
    [InlineData("", false)]
    public void IsComplete_NeedsEverySlot(string fold, bool expected)
    {
        Assert.Equal(expected, StateFold.IsComplete(fold));
    }

    [Theory]
    [InlineData("H5", true)]
    [InlineData("$H$5", true)]
    [InlineData(" h5 ", true)]
    [InlineData("", false)]
    [InlineData("H0", false)]
    [InlineData("H5:H6", false)]
    [InlineData("Sheet2!H5", false)]
    [InlineData("moveRobot", false)]
    public void IsCellAddress(string text, bool expected)
    {
        Assert.Equal(expected, StateFold.IsCellAddress(text));
    }
}
