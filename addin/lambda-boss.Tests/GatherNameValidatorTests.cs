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

    [Fact]
    public void Validate_AllUniqueCanonical_AllValid()
    {
        var result = GatherNameValidator.Validate(new[] { "numbers", "doubled", "step_1" });

        Assert.Equal(new[] { true, true, true }, result);
    }

    [Fact]
    public void Validate_TwoRowsWithSameName_BothMarkedInvalid()
    {
        // Both colliding rows light up so the user sees which two
        // names need disambiguating.
        var result = GatherNameValidator.Validate(new[] { "x", "y", "x" });

        Assert.Equal(new[] { false, true, false }, result);
    }

    [Fact]
    public void Validate_CollisionIsCaseInsensitive()
    {
        // Excel's name resolution is case-insensitive; Validate
        // mirrors that so "Foo" and "foo" collide too.
        var result = GatherNameValidator.Validate(new[] { "foo", "Foo" });

        // "Foo" is also non-canonical (initial uppercase), so even
        // without the collision rule it would be invalid. The
        // collision check applies regardless — both entries fail.
        Assert.Equal(new[] { false, false }, result);
    }

    [Fact]
    public void Validate_NonCanonicalDoesNotPoisonOtherRowsCount()
    {
        // A non-canonical name gets dropped from the count map so it
        // can't accidentally collide with itself or with a canonical
        // name. Other rows keep their natural validity.
        var result = GatherNameValidator.Validate(new[] { "Hello", "world" });

        Assert.Equal(new[] { false, true }, result);
    }

    [Fact]
    public void Validate_EmptyEntry_MarkedInvalidWithoutAffectingOthers()
    {
        // An empty string isn't counted toward collisions (we drop
        // empties from the count map). Other rows stay independently
        // valid.
        var result = GatherNameValidator.Validate(new[] { "numbers", "" });

        Assert.Equal(new[] { true, false }, result);
    }
}
