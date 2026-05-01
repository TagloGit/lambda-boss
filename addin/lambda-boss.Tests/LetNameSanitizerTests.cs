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

    [Theory]
    [InlineData("Help?", "help?")]
    [InlineData("IsEmpty?", "isEmpty?")]
    [InlineData("Has Value?", "hasValue?")]
    public void Sanitize_TrailingQuestionMark_Preserved(string input, string expected)
    {
        // Issue 152: '?' is allowed inside Excel names, and predicate-style
        // labels like 'Help?' should sanitise to 'help?' rather than have
        // the '?' silently stripped.
        Assert.Equal(expected, LetNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_LeadingQuestionMark_PrefixedWithUnderscore()
    {
        // '?' isn't a valid name-start character; the sanitizer prefixes
        // with '_' so the result is still a usable name. Alternative
        // would be to return null — picking '_' is consistent with the
        // existing leading-digit handling.
        Assert.Equal("_?help", LetNameSanitizer.Sanitize("?help"));
    }
}
