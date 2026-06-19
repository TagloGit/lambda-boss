using Xunit;

namespace LambdaBoss.Tests;

public class PrefixRewriterTests
{
    [Fact]
    public void Apply_BasicReplacement_PrefixesFunctionCalls()
    {
        var formula = "=LAMBDA(x, Double(x))";
        var names = new[] { "Double" };

        var result = PrefixRewriter.Apply(formula, "tst", names);

        Assert.Equal("=LAMBDA(x, tst.Double(x))", result);
    }

    [Fact]
    public void Apply_MultipleNames_PrefixesAll()
    {
        var formula = "=LAMBDA(x, Double(Triple(x)))";
        var names = new[] { "Double", "Triple" };

        var result = PrefixRewriter.Apply(formula, "lib", names);

        Assert.Equal("=LAMBDA(x, lib.Double(lib.Triple(x)))", result);
    }

    [Fact]
    public void Apply_StringLiteral_NotRewritten()
    {
        var formula = "=LAMBDA(x, IF(x, Double(x), \"Double(x)\"))";
        var names = new[] { "Double" };

        var result = PrefixRewriter.Apply(formula, "tst", names);

        Assert.Contains("tst.Double(x)", result);
        Assert.Contains("\"Double(x)\"", result);
    }

    [Fact]
    public void Apply_DoubledQuotesInString_PreservesEscaping()
    {
        var formula = "=LAMBDA(x, \"She said \"\"Double(x)\"\" to me\")";
        var names = new[] { "Double" };

        var result = PrefixRewriter.Apply(formula, "tst", names);

        Assert.Contains("\"She said \"\"Double(x)\"\" to me\"", result);
    }

    [Fact]
    public void Apply_EmptyPrefix_ReturnsUnchanged()
    {
        var formula = "=LAMBDA(x, Double(x))";
        var names = new[] { "Double" };

        var result = PrefixRewriter.Apply(formula, "", names);

        Assert.Equal(formula, result);
    }

    [Fact]
    public void Apply_NoMatchingNames_ReturnsUnchanged()
    {
        var formula = "=LAMBDA(x, SUM(x, 1))";
        var names = new[] { "Double" };

        var result = PrefixRewriter.Apply(formula, "tst", names);

        Assert.Equal(formula, result);
    }

    [Fact]
    public void Apply_EmptyNamesList_ReturnsUnchanged()
    {
        var formula = "=LAMBDA(x, Double(x))";
        var names = Array.Empty<string>();

        var result = PrefixRewriter.Apply(formula, "tst", names);

        Assert.Equal(formula, result);
    }

    [Fact]
    public void Apply_PartialNameMatch_DoesNotPrefix()
    {
        // "Doubled" should not match "Double"
        var formula = "=LAMBDA(x, Doubled(x))";
        var names = new[] { "Double" };

        var result = PrefixRewriter.Apply(formula, "tst", names);

        Assert.Equal(formula, result);
    }

    [Fact]
    public void Apply_CaseInsensitive_MatchesDifferentCase()
    {
        var formula = "=LAMBDA(x, double(x))";
        var names = new[] { "Double" };

        var result = PrefixRewriter.Apply(formula, "tst", names);

        Assert.Equal("=LAMBDA(x, tst.double(x))", result);
    }

    [Fact]
    public void Apply_NameWithoutParens_NotPrefixed()
    {
        // "Double" used as a variable reference (no parens) should not be prefixed
        var formula = "=LAMBDA(x, Double + 1)";
        var names = new[] { "Double" };

        var result = PrefixRewriter.Apply(formula, "tst", names);

        Assert.Equal(formula, result);
    }

    [Fact]
    public void Apply_SpaceBeforeParen_StillPrefixed()
    {
        var formula = "=LAMBDA(x, Double (x))";
        var names = new[] { "Double" };

        var result = PrefixRewriter.Apply(formula, "tst", names);

        Assert.Equal("=LAMBDA(x, tst.Double (x))", result);
    }

    // --- Constant name prefixing (bare word boundary, no parens required) ---

    [Fact]
    public void Apply_ConstantReference_NoParens_IsPrefixed()
    {
        // A constant is referenced without parens — it must still be prefixed.
        var formula = "=LAMBDA(x, INDEX(DEFAULTARROWS, x))";
        var result = PrefixRewriter.Apply(
            formula, "maps",
            lambdaNames: Array.Empty<string>(),
            constNames: new[] { "DEFAULTARROWS" });

        Assert.Equal("=LAMBDA(x, INDEX(maps.DEFAULTARROWS, x))", result);
    }

    [Fact]
    public void Apply_LambdaAndConstantTogether_BothPrefixed()
    {
        var formula = "=LAMBDA(HSTACK(DEFAULTARROWS, DEFAULTMOVEDELTAS, Helper(x)))";
        var result = PrefixRewriter.Apply(
            formula, "maps",
            lambdaNames: new[] { "Helper" },
            constNames: new[] { "DEFAULTARROWS", "DEFAULTMOVEDELTAS" });

        Assert.Equal(
            "=LAMBDA(HSTACK(maps.DEFAULTARROWS, maps.DEFAULTMOVEDELTAS, maps.Helper(x)))",
            result);
    }

    [Fact]
    public void Apply_ConstantPartialName_NotPrefixed()
    {
        // "DEFAULTARROWSX" should not match constant "DEFAULTARROWS".
        var formula = "=LAMBDA(x, DEFAULTARROWSX)";
        var result = PrefixRewriter.Apply(
            formula, "maps",
            lambdaNames: Array.Empty<string>(),
            constNames: new[] { "DEFAULTARROWS" });

        Assert.Equal(formula, result);
    }

    [Fact]
    public void Apply_ConstantInsideStringLiteral_NotPrefixed()
    {
        var formula = "=LAMBDA(x, IF(x, DEFAULTARROWS, \"DEFAULTARROWS\"))";
        var result = PrefixRewriter.Apply(
            formula, "maps",
            lambdaNames: Array.Empty<string>(),
            constNames: new[] { "DEFAULTARROWS" });

        Assert.Contains("maps.DEFAULTARROWS,", result);
        Assert.Contains("\"DEFAULTARROWS\"", result);
    }

    [Fact]
    public void Apply_LambdaNameWithoutParens_NotPrefixed_EvenWithConstants()
    {
        // A LAMBDA name used without parens stays unprefixed; only constants match bare.
        var formula = "=LAMBDA(x, Double + DEFAULTARROWS)";
        var result = PrefixRewriter.Apply(
            formula, "lib",
            lambdaNames: new[] { "Double" },
            constNames: new[] { "DEFAULTARROWS" });

        Assert.Equal("=LAMBDA(x, Double + lib.DEFAULTARROWS)", result);
    }

    [Fact]
    public void Apply_OnlyConstants_EmptyPrefix_ReturnsUnchanged()
    {
        var formula = "=LAMBDA(x, DEFAULTARROWS)";
        var result = PrefixRewriter.Apply(
            formula, "",
            lambdaNames: Array.Empty<string>(),
            constNames: new[] { "DEFAULTARROWS" });

        Assert.Equal(formula, result);
    }
}
