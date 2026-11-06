using Xunit;

namespace LambdaBoss.Tests;

public class LetToLambdaBuilderTests
{
    private static string Build(string formula, string lambdaName,
        params (string name, string paramName, bool keep)[] choices)
    {
        var parsed = LetParser.Parse(formula);
        var inputs = choices.Select(c => new InputChoice(c.name, c.paramName, c.keep)).ToList();
        return LetToLambdaBuilder.Build(new LambdaGenerationRequest(lambdaName, parsed, inputs));
    }

    private static string BuildWithOptional(string formula, string lambdaName,
        params (string name, string paramName, bool keep, bool isOptional)[] choices)
    {
        var parsed = LetParser.Parse(formula);
        var inputs = choices
            .Select(c => new InputChoice(c.name, c.paramName, c.keep, c.isOptional))
            .ToList();
        return LetToLambdaBuilder.Build(new LambdaGenerationRequest(lambdaName, parsed, inputs));
    }

    private static string BuildWithDefault(string formula, string lambdaName,
        params (string name, string paramName, bool keep, bool isOptional, string? defaultExpr)[] choices)
    {
        var parsed = LetParser.Parse(formula);
        var inputs = choices
            .Select(c => new InputChoice(c.name, c.paramName, c.keep, c.isOptional, c.defaultExpr))
            .ToList();
        return LetToLambdaBuilder.Build(new LambdaGenerationRequest(lambdaName, parsed, inputs));
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void AllInputsKept_NoInternalLet()
    {
        var result = Build("=LET(x, 1, y, 2, SUM(x, y))", "Adder",
            ("x", "x", true), ("y", "y", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    x,",
            "    y,",
            "    SUM(x, y)",
            ")"), result);
    }

    [Fact]
    public void RenamedParam_SubstitutesThroughBody()
    {
        var result = Build("=LET(x, 1, y, 2, SUM(x, y))", "Adder",
            ("x", "a", true), ("y", "b", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    a,",
            "    b,",
            "    SUM(a, b)",
            ")"), result);
    }

    [Fact]
    public void RenamedKey_DoesNotMatchInsideLongerIdentifier()
    {
        // Regression: when a rename key appears as the first letter of an
        // identifier in the body (e.g. `a` inside `ABS`), the word-boundary
        // lookarounds must still guard the match. A previous bug interpolated
        // the alternation without grouping, so `|` split the regex and the
        // first alternative was only left-guarded, causing `ABS` → `add_1BS`.
        var result = Build(
            "=LET(a, A1, b, B1, c, INDIRECT(a), d, INDIRECT(b), " +
            "SUM(ABS(ROW(d)-ROW(c))+ABS(COLUMN(d)-COLUMN(c))))",
            "Distance",
            ("a", "add_1", true), ("b", "add_2", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    add_1,",
            "    add_2,",
            "    LET(",
            "        c, INDIRECT(add_1),",
            "        d, INDIRECT(add_2),",
            "        SUM(ABS(ROW(d)-ROW(c))+ABS(COLUMN(d)-COLUMN(c)))",
            "    )",
            ")"), result);
    }

    [Fact]
    public void MixedKeepAndCalc_WrapsInLet()
    {
        var result = Build("=LET(someRange, A1:A10, getMax, MAX(someRange), getMax)", "MyMax",
            ("someRange", "someRange", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    someRange,",
            "    LET(",
            "        getMax, MAX(someRange),",
            "        getMax",
            "    )",
            ")"), result);
    }

    [Fact]
    public void RenamedInput_RenamesInsideInternalBindingRhs()
    {
        var result = Build("=LET(a, A1:A10, b, MAX(a), b)", "MaxLambda",
            ("a", "myRange", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    myRange,",
            "    LET(",
            "        b, MAX(myRange),",
            "        b",
            "    )",
            ")"), result);
    }

    [Fact]
    public void RemovedInput_StaysAsInternalBinding()
    {
        var result = Build("=LET(x, 1, y, 2, x + y)", "Test",
            ("x", "x", false), ("y", "y", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    y,",
            "    LET(",
            "        x, 1,",
            "        x + y",
            "    )",
            ")"), result);
    }

    [Fact]
    public void NoInputsKept_LambdaHasNoParams()
    {
        var result = Build("=LET(x, 1, y, 2, x + y)", "Zero",
            ("x", "x", false), ("y", "y", false));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    LET(",
            "        x, 1,",
            "        y, 2,",
            "        x + y",
            "    )",
            ")"), result);
    }

    [Fact]
    public void StringLiteralContent_NotRenamed()
    {
        var result = Build("=LET(x, 1, CONCAT(\"x is \", x))", "WithString",
            ("x", "value", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    value,",
            "    CONCAT(\"x is \", value)",
            ")"), result);
    }

    [Fact]
    public void DuplicateParamName_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Build("=LET(x, 1, y, 2, x + y)", "Dup",
                ("x", "a", true), ("y", "a", true)));
    }

    [Fact]
    public void ParamNameCollidesWithInternalBinding_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Build("=LET(x, 1, y, MAX(x), y)", "Bad",
                ("x", "y", true)));
    }

    [Fact]
    public void EmptyParamName_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Build("=LET(x, 1, x)", "Bad",
                ("x", "", true)));
    }

    [Fact]
    public void CalculationBindingOnly_NoInputs()
    {
        var result = Build("=LET(m, MAX(A1:A10), m + 1)", "MaxPlus1");

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    LET(",
            "        m, MAX(A1:A10),",
            "        m + 1",
            "    )",
            ")"), result);
    }

    [Fact]
    public void KeptInputs_FollowInputListOrder()
    {
        // User reorders kept inputs: y first, then x.
        var result = Build("=LET(x, 1, y, 2, SUM(x, y))", "Adder",
            ("y", "y", true), ("x", "x", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    y,",
            "    x,",
            "    SUM(x, y)",
            ")"), result);
    }

    [Fact]
    public void ReorderedKeptInputs_InternalBindingsStayInSourceOrder()
    {
        // x and z kept and reordered; y is an internal calculation that must
        // stay in its source position relative to other internal bindings.
        var result = Build("=LET(x, 1, y, MAX(x), z, 3, x + y + z)", "Mix",
            ("z", "z", true), ("x", "x", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    z,",
            "    x,",
            "    LET(",
            "        y, MAX(x),",
            "        x + y + z",
            "    )",
            ")"), result);
    }

    [Fact]
    public void ReorderWithRename_RenamesThroughBody()
    {
        var result = Build("=LET(x, 1, y, 2, SUM(x, y))", "Adder",
            ("y", "second", true), ("x", "first", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    second,",
            "    first,",
            "    SUM(first, second)",
            ")"), result);
    }

    [Fact]
    public void UncheckedInputs_PositionInInputListIgnoredForSignature()
    {
        // z appears first in request.Inputs but is unchecked, so it should
        // not influence the LAMBDA signature; kept order is still y, x.
        var result = Build("=LET(x, 1, y, 2, z, 3, x + y + z)", "Skip",
            ("z", "z", false), ("y", "y", true), ("x", "x", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    y,",
            "    x,",
            "    LET(",
            "        z, 3,",
            "        x + y + z",
            "    )",
            ")"), result);
    }

    [Fact]
    public void OptionalSingleParam_WrapsWithIsOmitted()
    {
        // Cell refs in optional defaults are absolute-ized to avoid shifting
        // when the registered LAMBDA is invoked from any cell.
        var result = BuildWithOptional("=LET(x, 10, y, A1, x + y)", "Adder",
            ("x", "x", true, false), ("y", "offset", true, true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    x,",
            "    [offset],",
            "    LET(",
            "        offset, IF(ISOMITTED(offset), $A$1, offset),",
            "        x + offset",
            "    )",
            ")"), result);
    }

    [Fact]
    public void OptionalAllParams_EmitsWrapperBindingsInOrder()
    {
        var result = BuildWithOptional("=LET(x, 10, y, A1, x + y)", "Adder",
            ("x", "x", true, true), ("y", "offset", true, true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    [x],",
            "    [offset],",
            "    LET(",
            "        x, IF(ISOMITTED(x), 10, x),",
            "        offset, IF(ISOMITTED(offset), $A$1, offset),",
            "        x + offset",
            "    )",
            ")"), result);
    }

    [Fact]
    public void OptionalWithInternalBinding_WrapperBindingsAppearFirst()
    {
        // y is a calculation (uses operator), so it stays as an internal
        // binding. The optional x wrapper must appear first so y's RHS can
        // reference the defaulted x.
        var result = BuildWithOptional("=LET(x, 10, y, x + 1, x + y)", "Calc",
            ("x", "x", true, true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    [x],",
            "    LET(",
            "        x, IF(ISOMITTED(x), 10, x),",
            "        y, x + 1,",
            "        x + y",
            "    )",
            ")"), result);
    }

    [Fact]
    public void NoOptionalParams_OutputIsUnchanged()
    {
        // Matches today's output exactly when IsOptional is false for all.
        var result = BuildWithOptional("=LET(x, 1, y, 2, SUM(x, y))", "Adder",
            ("x", "x", true, false), ("y", "y", true, false));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    x,",
            "    y,",
            "    SUM(x, y)",
            ")"), result);
    }

    [Fact]
    public void OptionalWithRename_DefaultRhsUsesRenamedReferences()
    {
        // y's RHS is a bare reference to x (not a calculation, so y remains
        // an input). When x is renamed to a, the default expression for y
        // should read "a", not "x".
        var result = BuildWithOptional("=LET(x, 5, y, x, x + y)", "Calc",
            ("x", "a", true, false), ("y", "y", true, true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    a,",
            "    [y],",
            "    LET(",
            "        y, IF(ISOMITTED(y), a, y),",
            "        a + y",
            "    )",
            ")"), result);
    }

    [Fact]
    public void OptionalWithReorderAndRename_WorksTogether()
    {
        var result = BuildWithOptional("=LET(x, 10, y, A1, x + y)", "Adder",
            ("y", "offset", true, true), ("x", "base", true, false));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    [offset],",
            "    base,",
            "    LET(",
            "        offset, IF(ISOMITTED(offset), $A$1, offset),",
            "        base + offset",
            "    )",
            ")"), result);
    }

    [Fact]
    public void OptionalWithCustomDefault_UsesTypedDefault()
    {
        // The original RHS is A1, but the user overrides the default to 0.
        var result = BuildWithDefault("=LET(x, 10, y, A1, x + y)", "Adder",
            ("x", "x", true, false, null), ("y", "offset", true, true, "0"));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    x,",
            "    [offset],",
            "    LET(",
            "        offset, IF(ISOMITTED(offset), 0, offset),",
            "        x + offset",
            "    )",
            ")"), result);
    }

    [Fact]
    public void OptionalWithCustomDefault_CellRefAbsolutized()
    {
        // A cell ref typed as a custom default is absolutized like any other.
        var result = BuildWithDefault("=LET(x, 10, y, A1, x + y)", "Adder",
            ("x", "x", true, false, null), ("y", "offset", true, true, "B2"));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    x,",
            "    [offset],",
            "    LET(",
            "        offset, IF(ISOMITTED(offset), $B$2, offset),",
            "        x + offset",
            "    )",
            ")"), result);
    }

    [Fact]
    public void OptionalWithBlankCustomDefault_FallsBackToOriginalRhs()
    {
        // A null/blank custom default preserves the pre-edit behaviour.
        var result = BuildWithDefault("=LET(x, 10, y, A1, x + y)", "Adder",
            ("x", "x", true, false, null), ("y", "offset", true, true, "   "));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    x,",
            "    [offset],",
            "    LET(",
            "        offset, IF(ISOMITTED(offset), $A$1, offset),",
            "        x + offset",
            "    )",
            ")"), result);
    }

    [Fact]
    public void OptionalWithCustomDefault_AppliesRenames()
    {
        // The typed default references binding x, which is renamed to base.
        var result = BuildWithDefault("=LET(x, 5, y, 0, x + y)", "Calc",
            ("x", "base", true, false, null), ("y", "offset", true, true, "x + 1"));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    base,",
            "    [offset],",
            "    LET(",
            "        offset, IF(ISOMITTED(offset), base + 1, offset),",
            "        base + offset",
            "    )",
            ")"), result);
    }

    [Fact]
    public void OptionalOnUncheckedRow_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BuildWithOptional("=LET(x, 1, y, 2, x + y)", "Bad",
                ("x", "x", false, true), ("y", "y", true, false)));
    }

    [Theory]
    [InlineData("A1", "$A$1")]
    [InlineData("$A1", "$A$1")]
    [InlineData("A$1", "$A$1")]
    [InlineData("$A$1", "$A$1")]
    [InlineData("A1:B5", "$A$1:$B$5")]
    [InlineData("Sheet1!A1", "Sheet1!$A$1")]
    [InlineData("Sheet1!A1:B5", "Sheet1!$A$1:$B$5")]
    [InlineData("SUM(A1, B2)", "SUM($A$1, $B$2)")]
    [InlineData("\"A1 is a ref\"", "\"A1 is a ref\"")]
    [InlineData("IF(ISOMITTED(x), 5, x)", "IF(ISOMITTED(x), 5, x)")]
    [InlineData("offset", "offset")]
    [InlineData("42", "42")]
    public void AbsolutizeCellRefs_HandlesCommonShapes(string input, string expected)
    {
        Assert.Equal(expected, LetToLambdaBuilder.AbsolutizeCellRefs(input));
    }

    [Fact]
    public void OptionalOnlyParam_StillProducesLet()
    {
        // No internal bindings, but an optional param still needs a LET
        // wrapper to introduce the defaulted binding.
        var result = BuildWithOptional("=LET(x, 5, x + 1)", "Inc",
            ("x", "x", true, true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    [x],",
            "    LET(",
            "        x, IF(ISOMITTED(x), 5, x),",
            "        x + 1",
            "    )",
            ")"), result);
    }

    [Fact]
    public void ExcludedInput_CellRefsInRhsAreAbsolutized()
    {
        // y is excluded, so its RHS is baked into the LAMBDA body. Its cell
        // ref must be forced absolute for the same reason as optional
        // defaults: a registered LAMBDA invoked from another cell would
        // otherwise shift the reference.
        var result = Build("=LET(x, 1, y, A1, x + y)", "Calc",
            ("x", "x", true), ("y", "y", false));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    x,",
            "    LET(",
            "        y, $A$1,",
            "        x + y",
            "    )",
            ")"), result);
    }

    [Fact]
    public void ExcludedInput_RangeRefAbsolutized()
    {
        var result = Build("=LET(data, A1:B10, x, 1, SUM(data) + x)", "Calc",
            ("data", "data", false), ("x", "x", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    x,",
            "    LET(",
            "        data, $A$1:$B$10,",
            "        SUM(data) + x",
            "    )",
            ")"), result);
    }

    [Fact]
    public void CalculationBinding_CellRefsNotAbsolutized()
    {
        // Calculation bindings (RHS contains an operator/call that doesn't
        // resolve to a simple value) are the author's original derived
        // expressions and are left verbatim, unlike excluded value inputs.
        var result = Build("=LET(x, 1, m, MAX(A1:A10), x + m)", "Calc",
            ("x", "x", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    x,",
            "    LET(",
            "        m, MAX(A1:A10),",
            "        x + m",
            "    )",
            ")"), result);
    }

    [Fact]
    public void ExcludedInput_SheetQualifiedRefAbsolutized()
    {
        var result = Build("=LET(y, Sheet1!B2, x, 1, x + y)", "Calc",
            ("y", "y", false), ("x", "x", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    x,",
            "    LET(",
            "        y, Sheet1!$B$2,",
            "        x + y",
            "    )",
            ")"), result);
    }

    [Fact]
    public void ExcludedInput_StringLiteralCellRefNotAbsolutized()
    {
        // Tokens inside string literals must not be touched even when the
        // surrounding RHS is from an excluded input.
        var result = Build("=LET(msg, \"see A1\", x, 1, msg)", "Calc",
            ("msg", "msg", false), ("x", "x", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    x,",
            "    LET(",
            "        msg, \"see A1\",",
            "        msg",
            "    )",
            ")"), result);
    }

    [Fact]
    public void GeneratedLambda_ParsesBackViaLambdaSignatureParser()
    {
        // The formatted output must round-trip cleanly through the parser
        // so Edit Lambda can expand it back into a LET.
        var refersTo = Build("=LET(x, 1, y, 2, SUM(x, y))", "Adder",
            ("x", "x", true), ("y", "y", true));

        Assert.True(LambdaSignatureParser.IsLambdaFormula(refersTo));
        var sig = LambdaSignatureParser.Parse(refersTo);
        Assert.Equal(new[] { "x", "y" }, sig.Parameters);
        Assert.Equal("SUM(x, y)", sig.Body);
    }

    // --- State steps (spec 0011, part 3) ---

    private const string RobotStep =
        "=LET(a, SetVars(\"p\", I266, \"b\", I267), v, I268, " +
        "p, GetVar(a, \"p\"), b, GetVar(a, \"b\"), " +
        "nextP, p + IF(b > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)), " +
        "nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)), " +
        "SetVars(\"p\", nextP, \"b\", nextB))";

    private static string BuildStep(string formula, string state, string? input,
        params (string name, string paramName, bool keep)[] choices)
    {
        var parsed = LetParser.Parse(formula);
        var inputs = choices.Select(c => new InputChoice(c.name, c.paramName, c.keep)).ToList();
        return LetToLambdaBuilder.Build(
            new LambdaGenerationRequest("step", parsed, inputs, new StateStepRoles(state, input)));
    }

    [Fact]
    public void StateStep_RobotGivesTheSpecLambda()
    {
        var result = BuildStep(RobotStep, "a", "v", ("a", "a", true), ("v", "v", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    a,",
            "    v,",
            "    LET(",
            "        p, GetVar(a, \"p\"),",
            "        b, GetVar(a, \"b\"),",
            "        nextP, p + IF(b > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)),",
            "        nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)),",
            "        SetVars(\"p\", nextP, \"b\", nextB)",
            "    )",
            ")"), result);
    }

    [Fact]
    public void StateStep_ParameterOrderIsStateThenInputWhateverTheLetOrder()
    {
        var result = BuildStep(
            "=LET(v, H1, a, SetVars(\"p\", 0), p, GetVar(a, \"p\"), SetVars(\"p\", p + v))",
            "a", "v", ("a", "a", true), ("v", "v", true));

        Assert.StartsWith(Lines("=LAMBDA(", "    a,", "    v,"), result);
    }

    [Fact]
    public void StateStep_CalculationStateRowNeedsNoRename()
    {
        // The state's own name isn't "retained" once it's a parameter.
        var result = BuildStep(
            "=LET(s, SetVars(\"p\", 0), v, H1, p, GetVar(s, \"p\"), SetVars(\"p\", p + v))",
            "s", "v", ("s", "state", true), ("v", "input", true));

        Assert.Contains("p, GetVar(state, \"p\")", result);
        Assert.Contains("SetVars(\"p\", p + input)", result);
        Assert.DoesNotContain("s, SetVars", result);
    }

    [Fact]
    public void StateStep_CalculationInputCanBeAParameter()
    {
        var result = BuildStep(
            "=LET(a, SetVars(\"p\", 0), v, INDEX(G1:G9, 1), p, GetVar(a, \"p\"), SetVars(\"p\", p + v))",
            "a", "v", ("a", "a", true), ("v", "v", true));

        Assert.StartsWith(Lines("=LAMBDA(", "    a,", "    v,"), result);
        Assert.DoesNotContain("INDEX", result);
    }

    [Fact]
    public void StateStep_OtherValueKeptOffIsHardcodedAbsolute()
    {
        var result = BuildStep(
            "=LET(a, SetVars(\"b\", 4), v, H1, maxB, K2, b, GetVar(a, \"b\"), " +
            "nextB, MIN(maxB, b + v), SetVars(\"b\", nextB))",
            "a", "v", ("a", "a", true), ("v", "v", true), ("maxB", "maxB", false));

        Assert.Contains("maxB, $K$2,", result);
    }

    [Fact]
    public void StateStep_CommentBindingStays()
    {
        var result = BuildStep(
            "=LET(_note, \"caps at 6\", a, SetVars(\"b\", 4), v, H1, b, GetVar(a, \"b\"), SetVars(\"b\", b + v))",
            "a", "v", ("_note", "_note", false), ("a", "a", true), ("v", "v", true));

        Assert.Contains("_note, \"caps at 6\",", result);
    }

    [Fact]
    public void StateStep_SamplingOnlyBindingsAreDropped()
    {
        // The part 4 stepper: k feeds only the sample state and input.
        var result = BuildStep(
            "=LET(k, 1, " +
            "a, IF(k = 1, SetVars(\"p\", 0), INDEX(SCAN(SetVars(\"p\", 0), EXPLODE(G270), moveRobot), k - 1)), " +
            "v, INDEX(EXPLODE(G270), k), p, GetVar(a, \"p\"), nextP, p + v, SetVars(\"p\", nextP))",
            "a", "v", ("k", "k", false), ("a", "a", true), ("v", "v", true));

        Assert.Equal(Lines(
            "=LAMBDA(",
            "    a,",
            "    v,",
            "    LET(",
            "        p, GetVar(a, \"p\"),",
            "        nextP, p + v,",
            "        SetVars(\"p\", nextP)",
            "    )",
            ")"), result);
    }

    [Fact]
    public void StateStep_ChainsOfSamplingBindingsAreDropped()
    {
        var result = BuildStep(
            "=LET(x, H1, y, x + 1, a, HSTACK(y, 0), v, H2, p, INDEX(a, 1), HSTACK(p + v, 0))",
            "a", "v", ("x", "x", false), ("a", "a", true), ("v", "v", true));

        Assert.DoesNotContain("x,", result);
        Assert.DoesNotContain("y,", result);
    }

    [Fact]
    public void StateStep_BindingAlsoReadByTheStepStays()
    {
        var result = BuildStep(
            "=LET(k, 5, a, SetVars(\"p\", k), v, H1, p, GetVar(a, \"p\"), SetVars(\"p\", p + v + k))",
            "a", "v", ("k", "k", false), ("a", "a", true), ("v", "v", true));

        Assert.Contains("k, 5,", result);
    }

    [Fact]
    public void StateStep_BindingReadThroughARetainedLineStays()
    {
        var result = BuildStep(
            "=LET(k, 5, a, SetVars(\"p\", k), v, H1, twice, k * 2, p, GetVar(a, \"p\"), SetVars(\"p\", p + v + twice))",
            "a", "v", ("k", "k", false), ("a", "a", true), ("v", "v", true));

        Assert.Contains("k, 5,", result);
        Assert.Contains("twice, k * 2,", result);
    }

    [Fact]
    public void StateStep_KeptBindingIsNotDropped()
    {
        var result = BuildStep(
            "=LET(k, 5, a, SetVars(\"p\", k), v, H1, p, GetVar(a, \"p\"), SetVars(\"p\", p + v))",
            "a", "v", ("a", "a", true), ("v", "v", true), ("k", "k", true));

        Assert.Contains("    k,", result);
    }

    [Fact]
    public void StateStep_StateKeptOffStaysInsideAbsolute()
    {
        var result = BuildStep(
            "=LET(a, SetVars(\"p\", H1), v, H2, p, GetVar(a, \"p\"), SetVars(\"p\", p + v))",
            "a", "v", ("a", "a", false), ("v", "v", true));

        Assert.Contains("a, SetVars(\"p\", $H$1),", result);
    }

    [Fact]
    public void StateStep_OptionalDefaultKeepsTheBindingItNeeds()
    {
        var parsed = LetParser.Parse(
            "=LET(k, 5, a, SetVars(\"p\", k), v, H1, p, GetVar(a, \"p\"), SetVars(\"p\", p + v))");
        var inputs = new[]
        {
            new InputChoice("a", "a", true, IsOptional: true),
            new InputChoice("v", "v", true),
            new InputChoice("k", "k", false)
        };

        var result = LetToLambdaBuilder.Build(
            new LambdaGenerationRequest("step", parsed, inputs, new StateStepRoles("a", "v")));

        Assert.Contains("k, 5,", result);
        Assert.Contains("IF(ISOMITTED(a), SetVars(\"p\", k), a)", result);
    }

    [Fact]
    public void StateStep_CalculationRowCanBeKeptAfterTheBadgeMoved()
    {
        var result = BuildStep(
            "=LET(a, SetVars(\"p\", 0), cap, 6, v, INDEX(G1:G9, 1), p, GetVar(a, \"p\"), SetVars(\"p\", MIN(cap, p + v)))",
            "a", "cap", ("a", "a", true), ("cap", "cap", true), ("v", "v", true));

        Assert.StartsWith(Lines("=LAMBDA(", "    a,", "    cap,", "    v,"), result);
    }

    [Fact]
    public void StateStep_CalculationRowKeptOffIsAbsolute()
    {
        var result = BuildStep(
            "=LET(a, SetVars(\"p\", 0), cap, 6, v, INDEX(G1:G9, 1), p, GetVar(a, \"p\"), SetVars(\"p\", MIN(cap, p + v)))",
            "a", "cap", ("a", "a", true), ("cap", "cap", true), ("v", "v", false));

        Assert.Contains("v, INDEX($G$1:$G$9, 1),", result);
    }

    [Fact]
    public void StateStep_NoInputBinding()
    {
        var result = BuildStep(
            "=LET(a, SetVars(\"p\", 0), p, GetVar(a, \"p\"), SetVars(\"p\", p + 1))",
            "a", null, ("a", "a", true));

        Assert.StartsWith(Lines("=LAMBDA(", "    a,", "    LET("), result);
    }

    [Fact]
    public void NoStateRoles_CalculationChoiceStillThrows()
    {
        var parsed = LetParser.Parse("=LET(x, 1, y, x + 1, y)");
        var inputs = new[] { new InputChoice("x", "x", true), new InputChoice("y", "y", true) };

        Assert.ThrowsAny<Exception>(() =>
            LetToLambdaBuilder.Build(new LambdaGenerationRequest("n", parsed, inputs)));
    }
}
