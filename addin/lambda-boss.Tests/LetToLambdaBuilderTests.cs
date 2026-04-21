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
}
