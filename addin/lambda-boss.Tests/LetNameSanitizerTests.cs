using Xunit;

namespace LambdaBoss.Tests;

public class LetNameSanitizerTests
{
    [Theory]
    [InlineData("Customer ID", "customerId")]
    [InlineData("Tax Rate", "taxRate")]
    [InlineData("Sales", "sales")]
    [InlineData("Total", "total")]
    [InlineData("customer", "customer")]
    [InlineData("customerName", "customerName")]
    [InlineData("CustomerName", "customerName")]
    [InlineData("My Name Field", "myNameField")]
    [InlineData("My ABC Field", "myAbcField")]
    [InlineData("Customer-ID", "customerId")]
    [InlineData("Customer_ID", "customer_ID")]
    public void Sanitize_CommonLabels_ProducesCamelCase(string input, string expected)
    {
        Assert.Equal(expected, LetNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("  Customer ID  ", "customerId")]
    [InlineData("\tSales\n", "sales")]
    public void Sanitize_TrimsLeadingAndTrailingWhitespace(string input, string expected)
    {
        Assert.Equal(expected, LetNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("30", "_30")]
    [InlineData("42abc", "_42abc")]
    [InlineData("4", "_4")]
    public void Sanitize_LeadingDigit_PrefixedWithUnderscore(string input, string expected)
    {
        Assert.Equal(expected, LetNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_LeadingUnderscoreThenDigits_NotPrefixed()
    {
        Assert.Equal("_42abc", LetNameSanitizer.Sanitize("_42abc"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!@#")]
    [InlineData("---")]
    [InlineData(" \t\n ")]
    public void Sanitize_NoIdentifierCharacters_ReturnsNull(string? input)
    {
        Assert.Null(LetNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_AllUnderscores_KeptAsSingleWord()
    {
        Assert.Equal("___", LetNameSanitizer.Sanitize("___"));
    }

    [Fact]
    public void Sanitize_MultipleNonIdentifierRuns_TreatedAsSingleSeparator()
    {
        Assert.Equal("aBC", LetNameSanitizer.Sanitize("a   ---   B   !!!   C"));
    }
}
