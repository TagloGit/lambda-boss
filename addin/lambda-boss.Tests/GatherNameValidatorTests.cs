using Xunit;

namespace LambdaBoss.Tests;

public class GatherNameValidatorTests
{
    [Theory]
    [InlineData("myName")]
    [InlineData("step_1")]
    [InlineData("count")]
    [InlineData("_count")]
    [InlineData("count_2")]
    [InlineData("a")]
    public void IsCanonical_ValidIdentifier_ReturnsTrue(string name)
    {
        // Names that the sanitizer leaves untouched are canonical.
        // Round-tripping through Sanitize must be idempotent for them.
        Assert.True(GatherNameValidator.IsCanonical(name));
    }

    [Theory]
    [InlineData("Hello")]            // initial uppercase → sanitised to "hello"
    [InlineData("Hello World")]      // multi-word → camel-cased
    [InlineData("Customer ID")]      // multi-word → camel-cased
    [InlineData("30")]               // leading digit → "_30"
    [InlineData("?help")]            // leading ? → "_?help"
    public void IsCanonical_NameThatSanitizesToSomethingElse_ReturnsFalse(string name)
    {
        // Surfacing the difference (rather than auto-correcting) keeps
        // the user's typed text and the eventual binding name in sync.
        Assert.False(GatherNameValidator.IsCanonical(name));
    }

    [Theory]
    [InlineData("true")]   // Excel reserved name
    [InlineData("false")]  // Excel reserved name
    [InlineData("r")]      // Excel reserved name (R1C1 row marker)
    [InlineData("c")]      // Excel reserved name (R1C1 column marker)
    [InlineData("a1")]     // cell-ref shape
    [InlineData("bc27")]   // cell-ref shape (3-letter column)
    public void IsCanonical_ExcelReservedOrCellRefShape_ReturnsFalse(string name)
    {
        // These names round-trip through the sanitizer unchanged but
        // Excel refuses to use them as defined names — emitting them
        // as LET bindings would produce a formula Excel rejects on
        // write.
        Assert.False(GatherNameValidator.IsCanonical(name));
    }

    [Fact]
    public void IsCanonical_Empty_ReturnsFalse()
    {
        Assert.False(GatherNameValidator.IsCanonical(""));
    }

    [Fact]
    public void IsCanonical_Whitespace_ReturnsFalse()
    {
        // Sanitize returns null for whitespace-only input — that's the
        // "empty after sanitization" case from the issue's acceptance
        // criteria.
        Assert.False(GatherNameValidator.IsCanonical("   "));
    }

    [Fact]
    public void IsCanonical_Null_ReturnsFalse()
    {
        Assert.False(GatherNameValidator.IsCanonical(null));
    }
}
