using Xunit;

namespace LambdaBoss.Tests;

public class LetParserTests
{
    [Fact]
    public void IsLetFormula_DetectsLet()
    {
        Assert.True(LetParser.IsLetFormula("=LET(x, 1, x)"));
        Assert.True(LetParser.IsLetFormula("=let(x, 1, x)"));
        Assert.True(LetParser.IsLetFormula("=LET ( x , 1 , x )"));
    }

    [Fact]
    public void IsLetFormula_RejectsNonLet()
    {
        Assert.False(LetParser.IsLetFormula("=SUM(A1:A10)"));
        Assert.False(LetParser.IsLetFormula("=IF(A1, LET(x, 1, x), 0)"));
        Assert.False(LetParser.IsLetFormula(" =LET(x, 1, x)"));
        Assert.False(LetParser.IsLetFormula(""));
        Assert.False(LetParser.IsLetFormula(null));
    }

    [Fact]
    public void Parse_SingleBinding_ReturnsBindingAndBody()
    {
        var parsed = LetParser.Parse("=LET(x, 1, x + 1)");

        Assert.Single(parsed.Bindings);
        Assert.Equal("x", parsed.Bindings[0].Name);
        Assert.Equal("1", parsed.Bindings[0].RhsText);
        Assert.False(parsed.Bindings[0].IsCalculation);
        Assert.Equal("x + 1", parsed.Body);
    }

    [Fact]
    public void Parse_MultipleBindings_PreservesOrder()
    {
        var parsed = LetParser.Parse("=LET(a, 1, b, 2, c, 3, a + b + c)");

        Assert.Equal(3, parsed.Bindings.Count);
        Assert.Equal(new[] { "a", "b", "c" }, parsed.Bindings.Select(b => b.Name));
        Assert.Equal("a + b + c", parsed.Body);
    }

    [Fact]
    public void Parse_ValueRhs_NotClassifiedAsCalculation()
    {
        var parsed = LetParser.Parse(
            "=LET(n, 1, s, \"hello\", c, A1, r, A1:B10, q, Sheet1!A1, nm, myRange, -1)");

        Assert.All(parsed.Bindings, b => Assert.False(b.IsCalculation,
            $"Binding '{b.Name}' RHS '{b.RhsText}' was classified as calculation."));
    }

    [Fact]
    public void Parse_QuotedSheetReference_IsValue()
    {
        var parsed = LetParser.Parse("=LET(r, 'My Sheet'!A1:B2, r)");
        Assert.False(parsed.Bindings[0].IsCalculation);
    }

    [Fact]
    public void Parse_CalculationRhs_ClassifiedAsCalculation()
    {
        var parsed = LetParser.Parse(
            "=LET(m, MAX(A1:A10), s, 1 + 2, f, SUM(x, y), ix, A1:A10 B1:B10, n, -SUM(x), m)");

        Assert.All(parsed.Bindings, b => Assert.True(b.IsCalculation,
            $"Binding '{b.Name}' RHS '{b.RhsText}' was classified as value."));
    }

    [Fact]
    public void Parse_StringLiteralWithCommaAndParen_PreservedIntact()
    {
        var parsed = LetParser.Parse("=LET(greeting, \"Hello, (world)\", greeting)");

        Assert.Single(parsed.Bindings);
        Assert.Equal("\"Hello, (world)\"", parsed.Bindings[0].RhsText);
        Assert.False(parsed.Bindings[0].IsCalculation);
    }

    [Fact]
    public void Parse_NestedLetAsRhs_ClassifiedAsCalculation()
    {
        var parsed = LetParser.Parse("=LET(inner, LET(x, 1, x + 1), inner * 2)");

        Assert.True(parsed.Bindings[0].IsCalculation);
        Assert.Equal("LET(x, 1, x + 1)", parsed.Bindings[0].RhsText);
    }

    [Fact]
    public void Parse_NonLetFormula_Throws()
    {
        Assert.Throws<FormatException>(() => LetParser.Parse("=SUM(A1:A10)"));
    }

    [Fact]
    public void Parse_EvenArgCount_Throws()
    {
        Assert.Throws<FormatException>(() => LetParser.Parse("=LET(x, 1)"));
        Assert.Throws<FormatException>(() => LetParser.Parse("=LET(x, 1, y, 2)"));
    }

    [Fact]
    public void Parse_RealWorldExample_FromIssue()
    {
        var parsed = LetParser.Parse("=LET(someRange, A1:A10, getMax, MAX(someRange), getMax)");

        Assert.Equal(2, parsed.Bindings.Count);
        Assert.Equal("someRange", parsed.Bindings[0].Name);
        Assert.False(parsed.Bindings[0].IsCalculation);
        Assert.Equal("getMax", parsed.Bindings[1].Name);
        Assert.True(parsed.Bindings[1].IsCalculation);
        Assert.Equal("getMax", parsed.Body);
    }

    [Fact]
    public void Parse_BindingNameWithTrailingQuestionMark_Accepted()
    {
        // Issue 152: predicate-style names like 'Help?' are valid Excel
        // names (per ExcelNameValidator) and the LAMBDA library uses
        // them throughout. The parser must accept them as binding names.
        var parsed = LetParser.Parse("=LET(Help?, 1, Help? + 1)");

        Assert.Single(parsed.Bindings);
        Assert.Equal("Help?", parsed.Bindings[0].Name);
        Assert.Equal("Help? + 1", parsed.Body);
    }

    [Fact]
    public void Parse_QuestionMarkInsideName_Accepted()
    {
        // '?' anywhere except the first character is valid; mid-name
        // occurrences round-trip just like trailing ones.
        var parsed = LetParser.Parse("=LET(is?valid, TRUE, is?valid)");

        Assert.Equal("is?valid", parsed.Bindings[0].Name);
        Assert.Equal("is?valid", parsed.Body);
    }

    [Fact]
    public void Parse_BindingNameStartingWithQuestionMark_Throws()
    {
        // '?' at the start is NOT a valid Excel name — names must begin
        // with a letter or underscore. Guard against accidentally
        // accepting it via the relaxed pattern.
        Assert.Throws<FormatException>(() =>
            LetParser.Parse("=LET(?help, 1, ?help)"));
    }
}
