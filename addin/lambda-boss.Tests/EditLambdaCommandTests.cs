using LambdaBoss.Commands;

using Xunit;

namespace LambdaBoss.Tests;

public class EditLambdaCommandTests
{
    [Fact]
    public void TryParseLambdaCall_SimpleCall_ReturnsNameAndArgs()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc(A1, B1)");

        Assert.NotNull(call);
        Assert.Equal("MyCalc", call!.Name);
        Assert.Equal(new[] { "A1", "B1" }, call.Arguments);
    }

    [Fact]
    public void TryParseLambdaCall_ArgsWithExpressions_PreservesInnerText()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc(A1, B1 + 2)");

        Assert.NotNull(call);
        Assert.Equal(new[] { "A1", "B1 + 2" }, call!.Arguments);
    }

    [Fact]
    public void TryParseLambdaCall_NestedCallAsArg_TreatedAsSingleArg()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc(SUM(A1, B1), C1)");

        Assert.NotNull(call);
        Assert.Equal(new[] { "SUM(A1, B1)", "C1" }, call!.Arguments);
    }

    [Fact]
    public void TryParseLambdaCall_ZeroArgs_ReturnsEmptyList()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc()");

        Assert.NotNull(call);
        Assert.Equal("MyCalc", call!.Name);
        Assert.Empty(call.Arguments);
    }

    [Fact]
    public void TryParseLambdaCall_WhitespaceOnlyInside_ReturnsEmptyArgs()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=MyCalc(  )");

        Assert.NotNull(call);
        Assert.Empty(call!.Arguments);
    }

    [Fact]
    public void TryParseLambdaCall_DottedName_Matches()
    {
        var call = EditLambdaCommand.TryParseLambdaCall("=tst.Double(A1)");

        Assert.NotNull(call);
        Assert.Equal("tst.Double", call!.Name);
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
    public void BuildExpandedLet_FullArgs_EmitsLet()
    {
        var sig = new LambdaSignature(new[] { "x", "y" }, "x * y + 1");
        var result = EditLambdaCommand.BuildExpandedLet(sig, new[] { "A1", "B1 + 2" });

        Assert.Equal("=LET(x, A1, y, B1 + 2, x * y + 1)", result);
    }

    [Fact]
    public void BuildExpandedLet_ZeroParamsZeroArgs_ReturnsBareBody()
    {
        var sig = new LambdaSignature(Array.Empty<string>(), "1 + 1");
        var result = EditLambdaCommand.BuildExpandedLet(sig, Array.Empty<string>());

        Assert.Equal("=1 + 1", result);
    }

    [Fact]
    public void BuildExpandedLet_FewerArgsThanParams_LeavesTrailingUnbound()
    {
        var sig = new LambdaSignature(new[] { "x", "y", "z" }, "x + y + z");
        var result = EditLambdaCommand.BuildExpandedLet(sig, new[] { "A1" });

        Assert.Equal("=LET(x, A1, x + y + z)", result);
    }

    [Fact]
    public void BuildExpandedLet_ZeroArgsWithParams_OmitsLetWrapper()
    {
        var sig = new LambdaSignature(new[] { "x" }, "x + 1");
        var result = EditLambdaCommand.BuildExpandedLet(sig, Array.Empty<string>());

        Assert.Equal("=x + 1", result);
    }

    [Fact]
    public void BuildExpandedLet_TooManyArgs_Throws()
    {
        var sig = new LambdaSignature(new[] { "x" }, "x + 1");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EditLambdaCommand.BuildExpandedLet(sig, new[] { "A1", "B1" }));
        Assert.Contains("1 parameter", ex.Message);
        Assert.Contains("2 were provided", ex.Message);
    }

    [Fact]
    public void BuildExpandedLet_OptionalParamsStripped_GeneratesBareNamesInLet()
    {
        // LAMBDA with an optional [y] still expands into a LET using the bare
        // parameter name `y`.
        var sig = LambdaSignatureParser.Parse("=LAMBDA(x, [y], x + y)");
        var result = EditLambdaCommand.BuildExpandedLet(sig, new[] { "A1", "B1" });

        Assert.Equal("=LET(x, A1, y, B1, x + y)", result);
    }

    [Fact]
    public void EndToEnd_SpecExample_ProducesExpectedLet()
    {
        var formula = "=MyCalc(A1, B1 + 2)";
        var refersTo = "=LAMBDA(x, y, x * y + 1)";

        var call = EditLambdaCommand.TryParseLambdaCall(formula);
        Assert.NotNull(call);
        Assert.Equal("MyCalc", call!.Name);

        var sig = LambdaSignatureParser.Parse(refersTo);
        var letFormula = EditLambdaCommand.BuildExpandedLet(sig, call.Arguments);

        Assert.Equal("=LET(x, A1, y, B1 + 2, x * y + 1)", letFormula);
    }
}
