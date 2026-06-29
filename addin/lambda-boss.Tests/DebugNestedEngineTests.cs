using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Spec 0010 (spike) / issue #279 — <see cref="DebugNestedEngine" /> tests.
///     Covers lambda-scope discovery (single + nested, with host / params /
///     enclosing-params / depth), the no-lambda and malformed refusals, default
///     pin suggestion for recognised iterators vs. blanks for custom hosts, and
///     <see cref="DebugNestedEngine.BuildWatch" /> assembling self-contained
///     evaluable <c>=LET(...)</c> formulas (pins + leaf-first steps), including
///     blank-pin omission and the no-step body.
/// </summary>
public class DebugNestedEngineTests
{
    // A doubly-dynamic formula in the shape of the #279 trip example: an outer
    // BYROW binds `r` per row, an inner custom PAIROP binds `a`/`b` per pair.
    private const string Nested =
        "=MIN(BYROW(data, LAMBDA(r, SUM(PAIROP(r, LAMBDA(a, b, a + b), 1)))))";

    private const string Simple =
        "=SUM(BYROW(A1:B3, LAMBDA(r, SUM(r)*2)))";

    // ---------------- discovery ----------------

    [Fact]
    public void Discover_SingleLambda_CapturesHostParamsAndBody()
    {
        var d = DebugNestedEngine.Discover(Simple);

        Assert.Null(d.Diagnostic);
        var scope = Assert.Single(d.Scopes);
        Assert.Equal(0, scope.Depth);
        Assert.Equal("BYROW", scope.HostFunction);
        Assert.Equal(new[] { "r" }, scope.Params);
        Assert.Empty(scope.EnclosingParams);
        Assert.Equal("SUM(r)*2", scope.BodyText);
        Assert.Equal("LAMBDA(r) — arg of BYROW", scope.Label);
    }

    [Fact]
    public void Discover_NestedLambdas_OrdersOuterFirstWithEnclosingParams()
    {
        var d = DebugNestedEngine.Discover(Nested);

        Assert.Null(d.Diagnostic);
        Assert.Equal(2, d.Scopes.Count);

        var outer = d.Scopes[0];
        Assert.Equal(0, outer.Depth);
        Assert.Equal("BYROW", outer.HostFunction);
        Assert.Equal(new[] { "r" }, outer.Params);
        Assert.Empty(outer.EnclosingParams);

        var inner = d.Scopes[1];
        Assert.Equal(1, inner.Depth);
        Assert.Equal("PAIROP", inner.HostFunction);
        Assert.Equal(new[] { "a", "b" }, inner.Params);
        Assert.Equal(new[] { "r" }, inner.EnclosingParams);
        Assert.Equal("a + b", inner.BodyText);
    }

    [Fact]
    public void Discover_NoLambda_Refuses()
    {
        var d = DebugNestedEngine.Discover("=SUM(A1:A3) + 1");

        Assert.Empty(d.Scopes);
        Assert.NotNull(d.Diagnostic);
        Assert.Equal(DebugDiagnosticKind.NoLambda, d.Diagnostic!.Kind);
    }

    [Fact]
    public void Discover_MalformedFormula_Refuses()
    {
        var d = DebugNestedEngine.Discover("=SUM(");

        Assert.Empty(d.Scopes);
        Assert.NotNull(d.Diagnostic);
        Assert.Equal(DebugDiagnosticKind.MalformedFormula, d.Diagnostic!.Kind);
    }

    // ---------------- pin suggestion ----------------

    [Fact]
    public void SuggestPins_ByRowHost_SlicesSourceWithChooseRows()
    {
        var pins = DebugNestedEngine.SuggestPins(Simple, "scope0");

        var pin = Assert.Single(pins);
        Assert.Equal("r", pin.Param);
        Assert.Equal("CHOOSEROWS(A1:B3, 1)", pin.Expression);
    }

    [Fact]
    public void SuggestPins_InnerScope_IncludesEnclosingPinsThenBlanksForCustomHost()
    {
        var pins = DebugNestedEngine.SuggestPins(Nested, "scope1");

        Assert.Collection(pins,
            p =>
            {
                Assert.Equal("r", p.Param);
                Assert.Equal("CHOOSEROWS(data, 1)", p.Expression);
            },
            p =>
            {
                Assert.Equal("a", p.Param);
                Assert.Equal("", p.Expression);
            },
            p =>
            {
                Assert.Equal("b", p.Param);
                Assert.Equal("", p.Expression);
            });
    }

    [Fact]
    public void SuggestPins_HonoursExampleIndex()
    {
        var pins = DebugNestedEngine.SuggestPins(Simple, "scope0", index: 3);

        Assert.Equal("CHOOSEROWS(A1:B3, 3)", pins[0].Expression);
    }

    // ---------------- watch / evaluable formulas ----------------

    [Fact]
    public void BuildWatch_OuterScope_BuildsPinnedStepAndFinalFormulas()
    {
        var pins = new[] { new DebugPin("r", "CHOOSEROWS(A1:B3, 1)") };

        var watch = DebugNestedEngine.BuildWatch(Simple, "scope0", pins);

        Assert.Null(watch.Diagnostic);

        // `sum1` reads as a valid cell address, so the auto-namer underscores
        // it to the legal `sum_1` (see UnnestEngine.AllocateName).
        var step = Assert.Single(watch.Steps);
        Assert.Equal("sum_1", step.Name);
        Assert.Equal("SUM(r)", step.Rhs);
        Assert.Equal("=LET(r, CHOOSEROWS(A1:B3, 1), sum_1, SUM(r), sum_1)", step.EvaluableFormula);

        Assert.Equal(
            "=LET(r, CHOOSEROWS(A1:B3, 1), sum_1, SUM(r), sum_1 * 2)",
            watch.FinalEvaluableFormula);
    }

    [Fact]
    public void BuildWatch_BlankPinOmittedAndNoStepBody()
    {
        // Inner scope `a + b`: a/b supplied, the unused enclosing `r` left blank.
        var pins = new[]
        {
            new DebugPin("r", ""),
            new DebugPin("a", "1"),
            new DebugPin("b", "2")
        };

        var watch = DebugNestedEngine.BuildWatch(Nested, "scope1", pins);

        Assert.Null(watch.Diagnostic);
        Assert.Empty(watch.Steps); // `a + b` root is the body, not a step.
        Assert.Equal("=LET(a, 1, b, 2, a + b)", watch.FinalEvaluableFormula);
    }

    [Fact]
    public void BuildWatch_InnerLambdaKeptInlineInOuterStep()
    {
        var pins = new[] { new DebugPin("r", "CHOOSEROWS(data, 1)") };

        var watch = DebugNestedEngine.BuildWatch(Nested, "scope0", pins);

        Assert.Null(watch.Diagnostic);
        // The outer body SUM(PAIROP(r, LAMBDA(a, b, a + b), 1)) decomposes its
        // non-lambda structure; the inner LAMBDA stays inline in the PAIROP step.
        var pairop = Assert.Single(watch.Steps, s => s.Name == "pairop1");
        Assert.Contains("LAMBDA(a, b, a + b)", pairop.Rhs);
        Assert.StartsWith("=LET(r, CHOOSEROWS(data, 1),", pairop.EvaluableFormula);
    }
}
