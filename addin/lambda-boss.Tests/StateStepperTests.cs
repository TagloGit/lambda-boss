using Xunit;

namespace LambdaBoss.Tests;

public class StateStepperTests
{
    private const string Robot =
        "=LAMBDA(a, v, LET(p, GetVar(a, \"p\"), b, GetVar(a, \"b\"), "
        + "nextP, p + IF(b > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)), "
        + "nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)), "
        + "SetVars(\"p\", nextP, \"b\", nextB)))";

    private const string RobotPrototype =
        "=LET(\n"
        + "    k, 1,\n"
        + "    a, IF(k = 1, SetVars(\"p\", 0, \"b\", 4), INDEX(SCAN(SetVars(\"p\", 0, \"b\", 4), EXPLODE(G270), moveRobot), k - 1)),\n"
        + "    v, INDEX(EXPLODE(G270), k),\n"
        + "    p, GetVar(a, \"p\"),\n"
        + "    b, GetVar(a, \"b\"),\n"
        + "    nextP, p + IF(b > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)),\n"
        + "    nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)),\n"
        + "    SetVars(\"p\", nextP, \"b\", nextB)\n"
        + ")";

    private const string RobotFold = "REDUCE(SetVars(\"p\", 0, \"b\", 4), EXPLODE(G270), moveRobot)";

    private static string? Names(string name) =>
        string.Equals(name, "moveRobot", StringComparison.OrdinalIgnoreCase) ? Robot : null;

    private static Func<string, string?> Defined(params (string Name, string RefersTo)[] names) =>
        name => names.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)).RefersTo;

    private static string Prototype(string formula, Func<string, string?>? names = null)
    {
        var outcome = StateStepper.Build(formula, names ?? Names);
        Assert.NotNull(outcome);
        Assert.Null(outcome!.Refusal);
        return outcome.Formula!;
    }

    private static string Refusal(string formula, Func<string, string?>? names = null)
    {
        var outcome = StateStepper.Build(formula, names ?? Names);
        Assert.NotNull(outcome);
        Assert.Null(outcome!.Formula);
        return outcome.Refusal!;
    }

    [Fact]
    public void RobotFold_GivesTheSpecsPrototype()
    {
        Assert.Equal(RobotPrototype, Prototype("=" + RobotFold));
    }

    [Theory]
    [InlineData("=\"P\"&TEXTJOIN(\" B\",,{0})")]
    [InlineData("=GetVar({0}, \"b\")")]
    [InlineData("=SUM(1, IF(TRUE, {0}))")]
    public void AFoldAnywhereInTheFormula_IsFound(string wrapper)
    {
        Assert.Equal(RobotPrototype, Prototype(string.Format(wrapper, RobotFold)));
    }

    [Fact]
    public void Scan_IsAFoldToo()
    {
        Assert.Equal(RobotPrototype, Prototype("=SCAN(SetVars(\"p\", 0, \"b\", 4), EXPLODE(G270), moveRobot)"));
    }

    [Fact]
    public void PrefixedLibraryNames_AreKept()
    {
        var step = "=LAMBDA(a, v, LET(p, loops.GetVar(a, \"p\"), loops.SetVars(\"p\", p + v)))";

        var result = Prototype("=REDUCE(loops.SetVars(\"p\", 0), G1:G9, st.go)", Defined(("st.go", step)));

        Assert.Contains("k, 1,", result);
        Assert.Contains("SCAN(loops.SetVars(\"p\", 0), G1:G9, st.go)", result);
    }

    [Theory]
    [InlineData("=moveRobot(A1, B1)")]
    [InlineData("=SUM(A1:A3)")]
    [InlineData("=REDUCE(0, A1:A3, LAMBDA(a, v, a + v))")]
    [InlineData("=REDUCE(0, A1:A3, unknown)")]
    [InlineData("=REDUCE(A1:A3, moveRobot)")]
    [InlineData("=LET(moveRobot, LAMBDA(a, v, a + v), REDUCE(0, A1:A3, moveRobot))")]
    [InlineData("=REDUCE(0, A1:A3, Sheet1!moveRobot)")]
    [InlineData("=FOLDROWS(A1:A3, 0, moveRobot)")]
    [InlineData("=SUM(")]
    [InlineData("")]
    public void NotAFold_ReturnsNull(string formula)
    {
        Assert.Null(StateStepper.Build(formula, Names));
    }

    [Fact]
    public void AStepWithoutTwoParameters_IsNotAFold()
    {
        var names = Defined(("one", "=LAMBDA(a, a + 1)"), ("three", "=LAMBDA(a, v, x, a + v + x)"), ("n", "=A1"));

        Assert.Null(StateStepper.Build("=REDUCE(0, A1:A3, one)", names));
        Assert.Null(StateStepper.Build("=REDUCE(0, A1:A3, three)", names));
        Assert.Null(StateStepper.Build("=REDUCE(0, A1:A3, n)", names));
    }

    [Theory]
    [InlineData("=SUM(SCAN(0, A1:A3, moveRobot), REDUCE(0, A1:A3, moveRobot))")]
    [InlineData("=REDUCE(REDUCE(0, A1:A3, moveRobot), A1:A3, moveRobot)")]
    public void TwoNamedFolds_AreRefused(string formula)
    {
        Assert.Equal(
            "Edit Lambda found more than one REDUCE/SCAN with a named step in this cell; "
            + "put the one to edit in its own cell.",
            Refusal(formula));
    }

    [Fact]
    public void AnInlineFoldBesideANamedOne_StillFindsTheNamedOne()
    {
        var result = Prototype("=SUM(REDUCE(0, A1:A3, LAMBDA(a, v, a + v)), " + RobotFold + ")");

        Assert.Equal(RobotPrototype, result);
    }

    [Fact]
    public void ArrayStep_BindsTheSeedAndFirstInput_WithNoStepper()
    {
        var step = "=LAMBDA(a, v, LET(p, INDEX(a, 1), b, INDEX(a, 2), HSTACK(p + v, b - 1)))";

        var result = Prototype("=REDUCE(HSTACK(0, 4), G1:G9, go)", Defined(("go", step)));

        Assert.Equal(
            "=LET(\n"
            + "    a, HSTACK(0, 4),\n"
            + "    v, INDEX(G1:G9, 1),\n"
            + "    p, INDEX(a, 1),\n"
            + "    b, INDEX(a, 2),\n"
            + "    HSTACK(p + v, b - 1)\n"
            + ")",
            result);
    }

    [Theory]
    [InlineData("=LAMBDA(a, v, LET(p, GetVar(a, \"p\"), SINGLE(IF(p > 1, a, SetVars(\"p\", p + v)))))", "SetVars(\"p\", 0)", true)]
    [InlineData("=LAMBDA(a, v, a + v)", "0", true)]
    [InlineData("=LAMBDA(a, v, IF(v > 0, a + v, a))", "HSTACK(0, 1)", false)]
    [InlineData("=LAMBDA(a, v, IF(v > 0, a + v, a))", "{0,1}", false)]
    public void AnUnrecognisedStep_GoesByItsSeed(string step, string seed, bool stepper)
    {
        var result = Prototype($"=REDUCE({seed}, G1:G9, go)", Defined(("go", step)));

        Assert.Equal(stepper, result.Contains("k, 1,"));
    }

    [Fact]
    public void ASeedPastTenVariables_ComesThroughWhole()
    {
        var pairs = string.Join(", ", Enumerable.Range(1, 10).Select(i => $"\"v{i}\", {i}"));
        var seed = $"SetVar(SetVars({pairs}), \"k\", 11)";

        var result = Prototype($"=REDUCE({seed}, G1:G9, moveRobot)");

        Assert.Contains($"a, IF(k = 1, {seed}, INDEX(SCAN({seed}, G1:G9, moveRobot), k - 1)),", result);
    }

    [Fact]
    public void AStepNameThatWouldShadowACellTheFoldReads_GetsASuffix()
    {
        var step = "=LAMBDA(a, v, LET(h1, GetVar(a, \"h1\"), next, h1 + v, SetVars(\"h1\", next)))";

        var result = Prototype("=REDUCE(SetVars(\"h1\", H1), G1:G9, go)", Defined(("go", step)));

        Assert.Equal(
            "=LET(\n"
            + "    k, 1,\n"
            + "    a, IF(k = 1, SetVars(\"h1\", H1), INDEX(SCAN(SetVars(\"h1\", H1), G1:G9, go), k - 1)),\n"
            + "    v, INDEX(G1:G9, k),\n"
            + "    h1_, GetVar(a, \"h1\"),\n"
            + "    next, h1_ + v,\n"
            + "    SetVars(\"h1\", next)\n"
            + ")",
            result);
    }

    [Fact]
    public void AParameterThatWouldShadowACellTheFoldReads_GetsASuffix()
    {
        var step = "=LAMBDA(g1, v, LET(p, GetVar(g1, \"p\"), SetVars(\"p\", p + v)))";

        var result = Prototype("=REDUCE(SetVars(\"p\", 0), G1:G9, go)", Defined(("go", step)));

        Assert.Contains("    g1_, IF(k = 1,", result);
        Assert.Contains("    p, GetVar(g1_, \"p\"),", result);
    }

    [Fact]
    public void AnAbsoluteOrQualifiedCell_IsLeftAlone()
    {
        var step = "=LAMBDA(a, v, LET(h1, GetVar(a, \"h1\"), SetVars(\"h1\", h1 + v)))";
        var names = Defined(("go", step));

        Assert.Contains("    h1, GetVar(a", Prototype("=REDUCE(SetVars(\"h1\", $H$1), Sheet1!H1:H9, go)", names));
    }

    [Theory]
    [InlineData("=LAMBDA(k, v, LET(p, GetVar(k, \"p\"), SetVars(\"p\", p + v)))")]
    [InlineData("=LAMBDA(a, v, LET(k, 2, p, GetVar(a, \"p\"), SetVars(\"p\", p + v * k)))")]
    [InlineData("=LAMBDA(a, v, LET(p, GetVar(a, \"p\"), SetVars(\"p\", p + v + SUM(K:K))))")]
    public void AStepUsingK_TakesAnotherStepperName(string step)
    {
        var result = Prototype("=REDUCE(SetVars(\"p\", 0), G1:G9, go)", Defined(("go", step)));

        Assert.StartsWith("=LET(\n    k_, 1,\n", result);
        Assert.Contains("v, INDEX(G1:G9, k_),", result);
    }

    [Fact]
    public void AFoldThatReadsK_TakesAnotherStepperName()
    {
        var result = Prototype("=REDUCE(SetVars(\"p\", 0), K:K, go)", Defined(("go", Robot)));

        Assert.StartsWith("=LET(\n    k_, 1,\n", result);
    }

    [Fact]
    public void AKeyNamedK_IsNotAName()
    {
        var step = "=LAMBDA(a, v, LET(p, GetVar(a, \"k\"), SetVars(\"k\", p + v)))";

        var result = Prototype("=REDUCE(SetVars(\"k\", 0), G1:G9, go)", Defined(("go", step)));

        Assert.StartsWith("=LET(\n    k, 1,\n", result);
    }

    [Fact]
    public void TheLetsAroundTheFold_AreWrittenInPlace()
    {
        var result = Prototype(
            "=LET(start, H1, seed, SetVars(\"p\", start, \"b\", 4), xs, EXPLODE(G270), "
            + "r, REDUCE(seed, xs, moveRobot), GetVar(r, \"p\"))");

        Assert.Contains("a, IF(k = 1, SetVars(\"p\", H1, \"b\", 4), INDEX(SCAN(SetVars(\"p\", H1, \"b\", 4), EXPLODE(G270), moveRobot), k - 1)),", result);
        Assert.Contains("v, INDEX(EXPLODE(G270), k),", result);
    }

    [Fact]
    public void ALetBindingAfterTheFold_IsNotInScope()
    {
        var result = Prototype("=LET(r, REDUCE(SetVars(\"p\", 0), xs, moveRobot), xs, 5, r)");

        Assert.Contains("v, INDEX(xs, k),", result);
    }

    [Fact]
    public void AFoldReadingAnOuterLambdaParameter_IsRefused()
    {
        var message = Refusal("=MAP(G1:G9, LAMBDA(x, REDUCE(SetVars(\"p\", 0, \"b\", 4), EXPLODE(x), moveRobot)))");

        Assert.Contains("reads x, a parameter of the LAMBDA around it", message);
    }

    [Fact]
    public void AnOuterLambdaParameterReadThroughALet_IsRefused()
    {
        var message = Refusal(
            "=MAP(G1:G9, LAMBDA(x, LET(xs, EXPLODE(x), REDUCE(SetVars(\"p\", 0, \"b\", 4), xs, moveRobot))))");

        Assert.Contains("reads x,", message);
    }

    [Fact]
    public void AFoldInsideALambdaThatIgnoresItsParameter_Works()
    {
        Assert.Equal(RobotPrototype, Prototype("=MAP(G1:G9, LAMBDA(x, GetVar(" + RobotFold + ", \"p\") + x))"));
    }

    [Theory]
    [InlineData("=REDUCE(, G1:G9, moveRobot)")]
    [InlineData("=REDUCE(0, , moveRobot)")]
    public void AnEmptySeedOrInputs_IsRefused(string formula)
    {
        Assert.Contains("seed and inputs", Refusal(formula));
    }

    [Fact]
    public void AStepThatRebindsItsParameter_IsRefused()
    {
        var step = "=LAMBDA(a, v, LET(a, a + 1, SetVars(\"p\", a + v)))";

        Assert.Contains("binds one of its own parameters", Refusal("=REDUCE(0, G1:G9, go)", Defined(("go", step))));
    }

    [Fact]
    public void AnOptionalParameter_TakesItsArgument()
    {
        var step = "=LAMBDA(a, [v], LET(v, IF(ISOMITTED(v), 1, v), p, GetVar(a, \"p\"), SetVars(\"p\", p + v)))";

        var result = Prototype("=REDUCE(SetVars(\"p\", 0), G1:G9, go)", Defined(("go", step)));

        Assert.Contains("v, INDEX(G1:G9, k),", result);
        Assert.DoesNotContain("ISOMITTED", result);
    }

    [Fact]
    public void APrototypePastExcelsLimit_IsRefused()
    {
        var seed = "SetVars(\"p\", \"" + new string('x', 4500) + "\")";

        Assert.Contains("limit", Refusal($"=REDUCE({seed}, G1:G9, moveRobot)"));
    }

    [Fact]
    public void LetToLambda_TurnsThePrototypeBackIntoTheSameLambda()
    {
        var parsed = LetParser.Parse(RobotPrototype);
        var step = StateStepParser.TryParse(parsed);

        Assert.NotNull(step);
        Assert.Equal("a", step!.StateBinding.Name);
        Assert.Equal("v", step.InputBinding!.Name);

        var rebuilt = LetToLambdaBuilder.Build(new LambdaGenerationRequest(
            "moveRobot",
            parsed,
            new[] { new InputChoice("a", "a", true), new InputChoice("v", "v", true), new InputChoice("k", "k", false) },
            new StateStepRoles("a", "v")));

        Assert.Equal(Squeeze(Robot), Squeeze(rebuilt));
    }

    private static string Squeeze(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    [Fact]
    public void Target_IsTheFirstEmptyCellToTheRight()
    {
        Assert.Equal((5, 4), StateStepper.FindTarget(5, 2, (r, c) => c == 4 || c == 5));
    }

    [Fact]
    public void Target_LooksTenColumnsRight_ThenTenRowsBelow()
    {
        Assert.Equal((5, 12), StateStepper.FindTarget(5, 2, (r, c) => c == 12));
        Assert.Equal((15, 2), StateStepper.FindTarget(5, 2, (r, c) => c == 13 || (c == 2 && r == 15)));
        Assert.Null(StateStepper.FindTarget(5, 2, (r, c) => c == 13 || (c == 2 && r == 16)));
    }

    [Fact]
    public void Target_StopsAtTheEdgeOfTheSheet()
    {
        var asked = new List<(int, int)>();

        Assert.Null(StateStepper.FindTarget(1048576, 16384, (r, c) =>
        {
            asked.Add((r, c));
            return false;
        }));
        Assert.Empty(asked);
    }
}
