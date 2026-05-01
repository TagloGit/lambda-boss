using Xunit;

namespace LambdaBoss.Tests;

public class LambdaSignatureParserTests
{
    [Fact]
    public void IsLambdaFormula_DetectsLambda()
    {
        Assert.True(LambdaSignatureParser.IsLambdaFormula("=LAMBDA(x, x)"));
        Assert.True(LambdaSignatureParser.IsLambdaFormula("=lambda(x, x)"));
        Assert.True(LambdaSignatureParser.IsLambdaFormula("=LAMBDA ( x , x )"));
    }

    [Fact]
    public void IsLambdaFormula_RejectsNonLambda()
    {
        Assert.False(LambdaSignatureParser.IsLambdaFormula("=SUM(A1:A10)"));
        Assert.False(LambdaSignatureParser.IsLambdaFormula("=LET(x, 1, x)"));
        Assert.False(LambdaSignatureParser.IsLambdaFormula("=IF(A1, LAMBDA(x, x), 0)"));
        Assert.False(LambdaSignatureParser.IsLambdaFormula(" =LAMBDA(x, x)"));
        Assert.False(LambdaSignatureParser.IsLambdaFormula(""));
        Assert.False(LambdaSignatureParser.IsLambdaFormula(null));
    }

    [Fact]
    public void Parse_SingleParam_ReturnsParamAndBody()
    {
        var sig = LambdaSignatureParser.Parse("=LAMBDA(x, x*2)");

        Assert.Equal(new[] { "x" }, sig.Parameters);
        Assert.Equal("x*2", sig.Body);
    }

    [Fact]
    public void Parse_MultipleParams_PreservesOrder()
    {
        var sig = LambdaSignatureParser.Parse("=LAMBDA(x, y, z, x + y * z)");

        Assert.Equal(new[] { "x", "y", "z" }, sig.Parameters);
        Assert.Equal("x + y * z", sig.Body);
    }

    [Fact]
    public void Parse_NestedCommasInBody_TreatedAsOneBody()
    {
        var sig = LambdaSignatureParser.Parse("=LAMBDA(x, SUM(x, 1, 2))");

        Assert.Equal(new[] { "x" }, sig.Parameters);
        Assert.Equal("SUM(x, 1, 2)", sig.Body);
    }

    [Fact]
    public void Parse_NestedLambdaInBody_PreservedAsBody()
    {
        var sig = LambdaSignatureParser.Parse("=LAMBDA(x, LAMBDA(y, x + y))");

        Assert.Equal(new[] { "x" }, sig.Parameters);
        Assert.Equal("LAMBDA(y, x + y)", sig.Body);
    }

    [Fact]
    public void Parse_OptionalParamBrackets_Stripped()
    {
        var sig = LambdaSignatureParser.Parse("=LAMBDA([x], x*2)");

        Assert.Equal(new[] { "x" }, sig.Parameters);
        Assert.Equal("x*2", sig.Body);
    }

    [Fact]
    public void Parse_MixedRequiredAndOptional_StripsBrackets()
    {
        var sig = LambdaSignatureParser.Parse("=LAMBDA(a, [b], a + b)");

        Assert.Equal(new[] { "a", "b" }, sig.Parameters);
        Assert.Equal("a + b", sig.Body);
    }

    [Fact]
    public void Parse_NoParams_ReturnsEmptyParamsAndBody()
    {
        var sig = LambdaSignatureParser.Parse("=LAMBDA(1 + 1)");

        Assert.Empty(sig.Parameters);
        Assert.Equal("1 + 1", sig.Body);
    }

    [Fact]
    public void Parse_CaseInsensitiveAndWhitespaceTolerant()
    {
        var sig = LambdaSignatureParser.Parse("=lambda( x ,  y , x + y )");

        Assert.Equal(new[] { "x", "y" }, sig.Parameters);
        Assert.Equal("x + y", sig.Body);
    }

    [Fact]
    public void Parse_StringLiteralWithCommasInBody_PreservedIntact()
    {
        var sig = LambdaSignatureParser.Parse("=LAMBDA(x, CONCAT(\"a, b\", x))");

        Assert.Equal(new[] { "x" }, sig.Parameters);
        Assert.Equal("CONCAT(\"a, b\", x)", sig.Body);
    }

    [Fact]
    public void Parse_NonLambdaFormula_Throws()
    {
        Assert.Throws<FormatException>(() => LambdaSignatureParser.Parse("=LET(x, 1, x)"));
    }

    [Fact]
    public void Parse_UnbalancedParens_Throws()
    {
        Assert.Throws<FormatException>(() => LambdaSignatureParser.Parse("=LAMBDA(x, x"));
    }

    [Fact]
    public void Parse_InvalidParamName_Throws()
    {
        Assert.Throws<FormatException>(() => LambdaSignatureParser.Parse("=LAMBDA(1x, x)"));
    }

    [Fact]
    public void Parse_RealWorldExample_FromSpec()
    {
        var sig = LambdaSignatureParser.Parse("=LAMBDA(x, y, x * y + 1)");

        Assert.Equal(new[] { "x", "y" }, sig.Parameters);
        Assert.Equal("x * y + 1", sig.Body);
    }

    [Fact]
    public void Parse_TrailingQuestionMarkParam_Accepted()
    {
        // Issue 152: predicate parameters like 'Help?' are a Lambda
        // library convention — the parser must accept them, not just
        // ExcelNameValidator.
        var sig = LambdaSignatureParser.Parse("=LAMBDA(Help?, IF(Help?, 1, 0))");

        Assert.Equal(new[] { "Help?" }, sig.Parameters);
        Assert.Equal("IF(Help?, 1, 0)", sig.Body);
    }

    [Fact]
    public void Parse_OptionalTrailingQuestionMarkParam_StripsBracketsKeepsQuestionMark()
    {
        // [Help?] is an optional predicate parameter — bracket stripping
        // must preserve the trailing '?'.
        var sig = LambdaSignatureParser.Parse("=LAMBDA(x, [Help?], x + 1)");

        Assert.Equal(new[] { "x", "Help?" }, sig.Parameters);
    }

    [Fact]
    public void Parse_QuestionMarkAtStart_Throws()
    {
        // '?' at the start is NOT a valid Excel name.
        Assert.Throws<FormatException>(() =>
            LambdaSignatureParser.Parse("=LAMBDA(?x, x)"));
    }
}
