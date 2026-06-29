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

    // ---------------- scratch-sheet extraction (new approach) ----------------

    // A LET-wrapped formula like Tim's: `convert` and `both` are parent-LET
    // bindings; the inner lambda body references `r` (its param) and `convert`.
    private const string LetWrapped =
        "=LET(convert, data[Amount], both, VSTACK(convert, convert), " +
        "BYROW(both, LAMBDA(r, LET(m, MAX(r), IF(m > AVERAGE(convert), m, 0)))))";

    [Fact]
    public void AnalyzeInputs_ClassifiesParamAndEnclosingLetBinding()
    {
        var inputs = DebugNestedEngine.AnalyzeInputs(LetWrapped, "scope0");

        // `r` is the lambda param; `convert` is an enclosing-LET binding whose
        // definition we can rebuild. `both` (unreferenced by the body) and `m`
        // (bound inside the body's own LET) are correctly excluded.
        Assert.Collection(inputs,
            i =>
            {
                Assert.Equal("r", i.Name);
                Assert.Equal(DebugInputKind.Param, i.Kind);
                Assert.Null(i.Definition);
            },
            i =>
            {
                Assert.Equal("convert", i.Name);
                Assert.Equal(DebugInputKind.LetBinding, i.Kind);
                Assert.Equal("data[Amount]", i.Definition);
            });
    }

    [Fact]
    public void AnalyzeInputs_UnknownName_ClassifiesAsExternal()
    {
        var inputs = DebugNestedEngine.AnalyzeInputs(
            "=BYROW(rng, LAMBDA(r, r + factor))", "scope0");

        Assert.Collection(inputs,
            i => Assert.Equal(DebugInputKind.Param, i.Kind),
            i =>
            {
                Assert.Equal("factor", i.Name);
                Assert.Equal(DebugInputKind.External, i.Kind);
            });
    }

    [Fact]
    public void AnalyzeInputs_InnerScope_ParamAndEnclosingParam()
    {
        var inputs = DebugNestedEngine.AnalyzeInputs(Nested, "scope1");

        // Inner LAMBDA(a, b) body `a + b`: both are its own params.
        Assert.All(inputs, i => Assert.Equal(DebugInputKind.Param, i.Kind));
        Assert.Equal(new[] { "a", "b" }, inputs.Select(i => i.Name));
    }

    [Fact]
    public void BuildDebugLet_SeedsParamFirstThenUnnestedBody()
    {
        var seeds = new[] { new DebugPin("r", "CHOOSEROWS(both, 1)") };

        var let = DebugNestedEngine.BuildDebugLet(LetWrapped, "scope0", seeds);

        Assert.StartsWith("=LET(", let);

        // Round-trips through LetParser, param seeded first, enclosing-LET name
        // `convert` left free (rebuilt on the sheet, not a binding), body intact.
        var parsed = LetParser.Parse(let);
        Assert.Equal("r", parsed.Bindings[0].Name);
        Assert.Equal("CHOOSEROWS(both, 1)", parsed.Bindings[0].RhsText);
        Assert.Contains(parsed.Bindings, b => b.RhsText == "AVERAGE(convert)");
        Assert.DoesNotContain(parsed.Bindings, b => b.Name == "convert");
        Assert.Equal("IF(calc1, m, 0)", parsed.Body);
    }

    [Fact]
    public void BuildCaptureFormula_WrapsExpressionInEnclosingLetContext()
    {
        // A param slice and an enclosing-LET binding both evaluate correctly once
        // wrapped in the parent LET's bindings.
        Assert.Equal(
            "=LET(convert, data[Amount], both, VSTACK(convert, convert), CHOOSEROWS(both, 1))",
            DebugNestedEngine.BuildCaptureFormula(LetWrapped, "scope0", "CHOOSEROWS(both, 1)"));

        Assert.Equal(
            "=LET(convert, data[Amount], both, VSTACK(convert, convert), convert)",
            DebugNestedEngine.BuildCaptureFormula(LetWrapped, "scope0", "convert"));
    }

    [Fact]
    public void BuildCaptureFormula_NoEnclosingLet_JustPrefixesEquals()
    {
        Assert.Equal(
            "=CHOOSEROWS(rng, 1)",
            DebugNestedEngine.BuildCaptureFormula(
                "=BYROW(rng, LAMBDA(r, r + 1))", "scope0", "CHOOSEROWS(rng, 1)"));
    }
}
