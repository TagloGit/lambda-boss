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

    [Fact]
    public void AllInputsKept_NoInternalLet()
    {
        var result = Build("=LET(x, 1, y, 2, SUM(x, y))", "Adder",
            ("x", "x", true), ("y", "y", true));

        Assert.Equal("=LAMBDA(x, y, SUM(x, y))", result);
    }

    [Fact]
    public void RenamedParam_SubstitutesThroughBody()
    {
        var result = Build("=LET(x, 1, y, 2, SUM(x, y))", "Adder",
            ("x", "a", true), ("y", "b", true));

        Assert.Equal("=LAMBDA(a, b, SUM(a, b))", result);
    }

    [Fact]
    public void MixedKeepAndCalc_WrapsInLet()
    {
        var result = Build("=LET(someRange, A1:A10, getMax, MAX(someRange), getMax)", "MyMax",
            ("someRange", "someRange", true));

        Assert.Equal("=LAMBDA(someRange, LET(getMax, MAX(someRange), getMax))", result);
    }

    [Fact]
    public void RenamedInput_RenamesInsideInternalBindingRhs()
    {
        var result = Build("=LET(a, A1:A10, b, MAX(a), b)", "MaxLambda",
            ("a", "myRange", true));

        Assert.Equal("=LAMBDA(myRange, LET(b, MAX(myRange), b))", result);
    }

    [Fact]
    public void RemovedInput_StaysAsInternalBinding()
    {
        var result = Build("=LET(x, 1, y, 2, x + y)", "Test",
            ("x", "x", false), ("y", "y", true));

        Assert.Equal("=LAMBDA(y, LET(x, 1, x + y))", result);
    }

    [Fact]
    public void NoInputsKept_LambdaHasNoParams()
    {
        var result = Build("=LET(x, 1, y, 2, x + y)", "Zero",
            ("x", "x", false), ("y", "y", false));

        Assert.Equal("=LAMBDA(LET(x, 1, y, 2, x + y))", result);
    }

    [Fact]
    public void StringLiteralContent_NotRenamed()
    {
        var result = Build("=LET(x, 1, CONCAT(\"x is \", x))", "WithString",
            ("x", "value", true));

        Assert.Equal("=LAMBDA(value, CONCAT(\"x is \", value))", result);
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

        Assert.Equal("=LAMBDA(LET(m, MAX(A1:A10), m + 1))", result);
    }

    [Fact]
    public void KeptInputs_FollowInputListOrder()
    {
        // User reorders kept inputs: y first, then x.
        var result = Build("=LET(x, 1, y, 2, SUM(x, y))", "Adder",
            ("y", "y", true), ("x", "x", true));

        Assert.Equal("=LAMBDA(y, x, SUM(x, y))", result);
    }

    [Fact]
    public void ReorderedKeptInputs_InternalBindingsStayInSourceOrder()
    {
        // x and z kept and reordered; y is an internal calculation that must
        // stay in its source position relative to other internal bindings.
        var result = Build("=LET(x, 1, y, MAX(x), z, 3, x + y + z)", "Mix",
            ("z", "z", true), ("x", "x", true));

        Assert.Equal("=LAMBDA(z, x, LET(y, MAX(x), x + y + z))", result);
    }

    [Fact]
    public void ReorderWithRename_RenamesThroughBody()
    {
        var result = Build("=LET(x, 1, y, 2, SUM(x, y))", "Adder",
            ("y", "second", true), ("x", "first", true));

        Assert.Equal("=LAMBDA(second, first, SUM(first, second))", result);
    }

    [Fact]
    public void UncheckedInputs_PositionInInputListIgnoredForSignature()
    {
        // z appears first in request.Inputs but is unchecked, so it should
        // not influence the LAMBDA signature; kept order is still y, x.
        var result = Build("=LET(x, 1, y, 2, z, 3, x + y + z)", "Skip",
            ("z", "z", false), ("y", "y", true), ("x", "x", true));

        Assert.Equal("=LAMBDA(y, x, LET(z, 3, x + y + z))", result);
    }
}
