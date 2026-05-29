using LambdaBoss.Commands;
using Xunit;

namespace LambdaBoss.Tests;

public class EditLambdaCommandTests
{
    private static string Lines(params string[] lines)
    {
        return string.Join("\n", lines);
    }

    [Fact]
    public void TryParseLambdaCall_SimpleCall_ReturnsNameAndArgs()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc(A1, B1)");

        Assert.NotNull(call);
        Assert.Equal("MyCalc", call.Name);
        Assert.Equal(["A1", "B1"], call.Arguments);
    }

    [Fact]
    public void TryParseLambdaCall_ArgsWithExpressions_PreservesInnerText()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc(A1, B1 + 2)");

        Assert.NotNull(call);
        Assert.Equal(["A1", "B1 + 2"], call.Arguments);
    }

    [Fact]
    public void TryParseLambdaCall_NestedCallAsArg_TreatedAsSingleArg()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc(SUM(A1, B1), C1)");

        Assert.NotNull(call);
        Assert.Equal(["SUM(A1, B1)", "C1"], call.Arguments);
    }

    [Fact]
    public void TryParseLambdaCall_ZeroArgs_ReturnsEmptyList()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc()");

        Assert.NotNull(call);
        Assert.Equal("MyCalc", call.Name);
        Assert.Empty(call.Arguments);
    }

    [Fact]
    public void TryParseLambdaCall_WhitespaceOnlyInside_ReturnsEmptyArgs()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc(  )");

        Assert.NotNull(call);
        Assert.Empty(call.Arguments);
    }

    [Fact]
    public void TryParseLambdaCall_DottedName_Matches()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=tst.Double(A1)");

        Assert.NotNull(call);
        Assert.Equal("tst.Double", call.Name);
    }

    [Fact]
    public void TryParseLambdaCall_WhitespaceAfterClose_Ok()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc(A1)   ");

        Assert.NotNull(call);
    }

    [Fact]
    public void TryParseLambdaCall_TrailingExpression_Rejected()
    {
        Assert.Null(EditLambdaCommand.TryParseLambdaCall("=MyCalc(A1) + 5"));
    }

    [Fact]
    public void TryParseLambdaCall_LeadingExpression_Rejected()
    {
        Assert.Null(EditLambdaCommand.TryParseLambdaCall("=5 + MyCalc(A1)"));
    }

    [Fact]
    public void TryParseLambdaCall_NotAFormula_Rejected()
    {
        Assert.Null(EditLambdaCommand.TryParseLambdaCall("MyCalc(A1)"));
        Assert.Null(EditLambdaCommand.TryParseLambdaCall("123"));
        Assert.Null(EditLambdaCommand.TryParseLambdaCall(""));
        Assert.Null(EditLambdaCommand.TryParseLambdaCall(null));
    }

    [Fact]
    public void TryParseLambdaCall_NoParens_Rejected()
    {
        Assert.Null(EditLambdaCommand.TryParseLambdaCall("=MyCalc"));
    }

    [Fact]
    public void TryParseLambdaCall_UnbalancedParens_Rejected()
    {
        Assert.Null(EditLambdaCommand.TryParseLambdaCall("=MyCalc(A1"));
    }

    [Fact]
    public void TryParseLambdaCall_NumericLeading_Rejected()
    {
        Assert.Null(EditLambdaCommand.TryParseLambdaCall("=123(A1)"));
    }

    [Fact]
    public void BuildExpandedLet_FullArgs_EmitsFormattedLet()
    {
        var sig = new LambdaSignature(["x", "y"], "x * y + 1");
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["A1", "B1 + 2"]);

        Assert.Equal(Lines(
            "=LET(",
            "    x, A1,",
            "    y, B1 + 2,",
            "    x * y + 1",
            ")"), result);
    }

    [Fact]
    public void BuildExpandedLet_ZeroParamsZeroArgs_ReturnsBareBody()
    {
        var sig = new LambdaSignature([], "1 + 1");
        var result = EditLambdaCommand.BuildExpandedLet(sig, []);

        Assert.Equal("=1 + 1", result);
    }

    [Fact]
    public void BuildExpandedLet_FewerArgsThanParams_BindsOmittedToNa()
    {
        // No ISOMITTED default to extract, so omitted params fall back to NA()
        // — a valid binding that surfaces #N/A rather than an invalid LET.
        var sig = new LambdaSignature(["x", "y", "z"], "x + y + z");
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["A1"]);

        Assert.Equal(Lines(
            "=LET(",
            "    x, A1,",
            "    y, NA(),",
            "    z, NA(),",
            "    x + y + z",
            ")"), result);
    }

    [Fact]
    public void BuildExpandedLet_ZeroArgsWithParams_BindsOmittedToNa()
    {
        var sig = new LambdaSignature(["x"], "x + 1");
        var result = EditLambdaCommand.BuildExpandedLet(sig, []);

        Assert.Equal(Lines(
            "=LET(",
            "    x, NA(),",
            "    x + 1",
            ")"), result);
    }

    [Fact]
    public void BuildExpandedLet_TooManyArgs_Throws()
    {
        var sig = new LambdaSignature(["x"], "x + 1");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EditLambdaCommand.BuildExpandedLet(sig, ["A1", "B1"]));
        Assert.Contains("1 parameter", ex.Message);
        Assert.Contains("2 were provided", ex.Message);
    }

    [Fact]
    public void BuildExpandedLet_OptionalParamsStripped_GeneratesBareNamesInLet()
    {
        var sig = LambdaSignatureParser.Parse("=LAMBDA(x, [y], x + y)");
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["A1", "B1"]);

        Assert.Equal(Lines(
            "=LET(",
            "    x, A1,",
            "    y, B1,",
            "    x + y",
            ")"), result);
    }

    [Fact]
    public void BuildExpandedLet_BodyIsLet_FoldsIntoOuterLet()
    {
        // Nested LET in body should fold into the outer LET so the result is
        // a single flat LET rather than LET-in-LET.
        var sig = LambdaSignatureParser.Parse(
            "=LAMBDA(x, y, LET(m, MAX(x), m + y))");
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["A1", "B1"]);

        Assert.Equal(Lines(
            "=LET(",
            "    x, A1,",
            "    y, B1,",
            "    m, MAX(x),",
            "    m + y",
            ")"), result);
    }

    [Fact]
    public void BuildExpandedLet_BodyIsMultiLineLet_FoldsCorrectly()
    {
        // Mirrors what Edit Lambda sees after LET to LAMBDA formatted the
        // stored LAMBDA with newlines.
        var refersTo = Lines(
            "=LAMBDA(",
            "    x,",
            "    y,",
            "    LET(",
            "        m, MAX(x),",
            "        m + y",
            "    )",
            ")");
        var sig = LambdaSignatureParser.Parse(refersTo);
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["A1", "B1"]);

        Assert.Equal(Lines(
            "=LET(",
            "    x, A1,",
            "    y, B1,",
            "    m, MAX(x),",
            "    m + y",
            ")"), result);
    }

    [Fact]
    public void BuildExpandedLet_BodyContainsLetInsideExpression_DoesNotFold()
    {
        // Body has a LET but it's embedded in a larger expression.
        var sig = new LambdaSignature(["x"], "LET(a, x, a) + 1");
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["A1"]);

        Assert.Equal(Lines(
            "=LET(",
            "    x, A1,",
            "    LET(a, x, a) + 1",
            ")"), result);
    }

    [Fact]
    public void EndToEnd_SpecExample_ProducesExpectedLet()
    {
        var formula = "=MyCalc(A1, B1 + 2)";
        var refersTo = "=LAMBDA(x, y, x * y + 1)";

        var call = EditLambdaCommand.TryParseLambdaCall(formula);
        Assert.NotNull(call);
        Assert.Equal("MyCalc", call.Name);

        var sig = LambdaSignatureParser.Parse(refersTo);
        var letFormula = EditLambdaCommand.BuildExpandedLet(sig, call.Arguments);

        Assert.Equal(Lines(
            "=LET(",
            "    x, A1,",
            "    y, B1 + 2,",
            "    x * y + 1",
            ")"), letFormula);
    }

    [Fact]
    public void RoundTrip_LetToLambdaThenEditLambda_FlattensToOriginalShape()
    {
        // Simulate the full round trip: author a LET, convert to LAMBDA, then
        // call it and expand via Edit Lambda. The expanded LET should fold
        // the internal binding back into a single flat LET.
        var parsed = LetParser.Parse("=LET(x, 5, y, MAX(x), x + y)");
        var request = new LambdaGenerationRequest(
            "MyCalc",
            parsed,
            [new InputChoice("x", "x", true)]);
        var refersTo = LetToLambdaBuilder.Build(request);

        var sig = LambdaSignatureParser.Parse(refersTo);
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc(A1)");
        Assert.NotNull(call);
        var expanded = EditLambdaCommand.BuildExpandedLet(sig, call.Arguments);

        Assert.Equal(Lines(
            "=LET(",
            "    x, A1,",
            "    y, MAX(x),",
            "    x + y",
            ")"), expanded);
    }

    [Fact]
    public void BuildExpandedLet_OmittedOptional_GeneratedLambda_MergesWrapperToDefault()
    {
        // LET to Lambda emits the optional param as `c, IF(ISOMITTED(c), 3, c)`.
        // Omitting c should merge that wrapper back to the bare default `c, 3`.
        var sig = LambdaSignatureParser.Parse(
            "=LAMBDA(a, b, [c], LET(c, IF(ISOMITTED(c), 3, c), a + b + c))");
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["1", "2"]);

        Assert.Equal(Lines(
            "=LET(",
            "    a, 1,",
            "    b, 2,",
            "    c, 3,",
            "    a + b + c",
            ")"), result);
    }

    [Fact]
    public void BuildExpandedLet_SuppliedOptional_GeneratedLambda_MergesWrapperToArg()
    {
        // Supplying c should merge the wrapper to the supplied argument, not the
        // default.
        var sig = LambdaSignatureParser.Parse(
            "=LAMBDA(a, b, [c], LET(c, IF(ISOMITTED(c), 3, c), a + b + c))");
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["1", "2", "9"]);

        Assert.Equal(Lines(
            "=LET(",
            "    a, 1,",
            "    b, 2,",
            "    c, 9,",
            "    a + b + c",
            ")"), result);
    }

    [Fact]
    public void BuildExpandedLet_OmittedOptional_HandAuthored_AddsParamBinding()
    {
        // EXPLODE-style: the wrapper's binding name (_size) differs from the
        // param (chunk_size). The param has no binding of its own, so one is
        // added with the extracted default, and the stray ISOMITTED is
        // neutralised to FALSE.
        var sig = LambdaSignatureParser.Parse(
            "=LAMBDA(text, [chunk_size], "
            + "LET(_size, IF(ISOMITTED(chunk_size), 1, chunk_size), "
            + "MID(text, 1, _size)))");
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["A1"]);

        Assert.Equal(Lines(
            "=LET(",
            "    text, A1,",
            "    chunk_size, 1,",
            "    _size, IF(FALSE, 1, chunk_size),",
            "    MID(text, 1, _size)",
            ")"), result);
    }

    [Fact]
    public void BuildExpandedLet_NeutralizesIsOmittedToFalse_ForSuppliedParam()
    {
        // `Help?, ISOMITTED(text)` references a supplied param; ISOMITTED is
        // illegal in a LET so it must become FALSE.
        var sig = LambdaSignatureParser.Parse(
            "=LAMBDA(text, LET(Help?, ISOMITTED(text), IF(Help?, 0, text)))");
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["A1"]);

        Assert.Equal(Lines(
            "=LET(",
            "    text, A1,",
            "    Help?, FALSE,",
            "    IF(Help?, 0, text)",
            ")"), result);
    }

    [Fact]
    public void BuildExpandedLet_IsOmittedInsideStringLiteral_NotReplaced()
    {
        // The literal text "ISOMITTED" inside a help string must be left alone.
        var sig = LambdaSignatureParser.Parse(
            "=LAMBDA(x, LET(note, \"uses ISOMITTED(y) internally\", "
            + "CONCAT(note, x)))");
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["A1"]);

        Assert.Contains("uses ISOMITTED(y) internally", result);
    }

    [Fact]
    public void BuildExpandedLet_OmittedNoDefault_FallsBackToNa()
    {
        var sig = LambdaSignatureParser.Parse("=LAMBDA(x, y, x + y)");
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["A1"]);

        Assert.Equal(Lines(
            "=LET(",
            "    x, A1,",
            "    y, NA(),",
            "    x + y",
            ")"), result);
    }

    [Fact]
    public void ExtractIsOmittedDefault_ParamSubstring_NotMatched()
    {
        // `size` must not match inside `chunk_size`.
        var defaultFor = EditLambdaCommand.ExtractIsOmittedDefault(
            ["IF(ISOMITTED(chunk_size), 1, chunk_size)"], "size");

        Assert.Null(defaultFor);
    }

    [Fact]
    public void ExtractIsOmittedDefault_NestedIf_ExtractsFullDefault()
    {
        var defaultFor = EditLambdaCommand.ExtractIsOmittedDefault(
            ["IF(ISOMITTED(p), IF(A1>0, 1, 2), p)"], "p");

        Assert.Equal("IF(A1>0, 1, 2)", defaultFor);
    }

    [Fact]
    public void NeutralizeIsOmitted_ReplacesOutsideStringsOnly()
    {
        var result = EditLambdaCommand.NeutralizeIsOmitted(
            "IF(ISOMITTED(a), \"ISOMITTED(b)\", a)");

        Assert.Equal("IF(FALSE, \"ISOMITTED(b)\", a)", result);
    }

    [Fact]
    public void BuildExpandedLet_FullExplode_ProducesValidLet()
    {
        // A faithful registered form of EXPLODE (no // comments, brackets kept
        // on optional params). Calling with only `text` must yield a valid LET
        // with no surviving ISOMITTED and both optional params bound.
        var refersTo =
            "=LAMBDA([text], [chunk_size], [horizontal], "
            + "LET("
            + "Help, TEXTSPLIT(\"FUNCTION: EXPLODE(text, chunk_size, horizontal)\", \"->\", \"|\"), "
            + "Help?, ISOMITTED(text), "
            + "_size, IF(ISOMITTED(chunk_size), 1, chunk_size), "
            + "_horiz, IF(ISOMITTED(horizontal), FALSE, horizontal), "
            + "_count, CEILING(LEN(text) / _size, 1), "
            + "_starts, IF(_horiz, SEQUENCE(1, _count, 1, _size), SEQUENCE(_count, 1, 1, _size)), "
            + "result, MID(text, _starts, _size), "
            + "IF(Help?, Help, result)))";

        var sig = LambdaSignatureParser.Parse(refersTo);
        var result = EditLambdaCommand.BuildExpandedLet(sig, ["O13"]);

        Assert.StartsWith("=LET(", result);
        Assert.DoesNotContain("ISOMITTED", result);
        Assert.Contains("text, O13,", result);
        Assert.Contains("chunk_size, 1,", result);
        Assert.Contains("horizontal, FALSE,", result);
        Assert.Contains("Help?, FALSE,", result);
    }
}