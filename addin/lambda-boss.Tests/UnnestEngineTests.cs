using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Spec 0009 / issue #271 — <see cref="UnnestEngine" /> tests for the non-LET
///     decomposition path. Covers the worked example, function-only and
///     operator-only formulas, the single-root no-op, function-derived + calcN
///     naming with collision suffixing, Include-toggle inlining + re-parenting,
///     and round-trip safety through <see cref="LetParser" />.
/// </summary>
public class UnnestEngineTests
{
    private const string WorkedExample =
        "=ROUND(SQRT(SUMSQ(XLOOKUP(H94, t[City], t[[X-Coordinates]:[Y-Coordinates]]) - $I$92:$J$92)) * 100, 0)";

    // ---------------- worked example ----------------

    [Fact]
    public void Unnest_WorkedExample_ProducesSpecLeafFirstSteps()
    {
        var result = UnnestEngine.Unnest(WorkedExample);

        Assert.Null(result.Diagnostic);
        Assert.Collection(result.Steps,
            s => AssertStep(s, "xlookup1",
                "XLOOKUP(H94, t[City], t[[X-Coordinates]:[Y-Coordinates]])", UnnestStepOrigin.Function),
            s => AssertStep(s, "calc1", "xlookup1 - $I$92:$J$92", UnnestStepOrigin.Operator),
            s => AssertStep(s, "sumsq1", "SUMSQ(calc1)", UnnestStepOrigin.Function),
            s => AssertStep(s, "sqrt1", "SQRT(sumsq1)", UnnestStepOrigin.Function),
            s => AssertStep(s, "calc2", "sqrt1 * 100", UnnestStepOrigin.Operator));
    }

    [Fact]
    public void Unnest_WorkedExample_SynthesisesExpectedLet()
    {
        var result = UnnestEngine.Unnest(WorkedExample);

        const string expected =
            "=LET(\n" +
            "    xlookup1, XLOOKUP(H94, t[City], t[[X-Coordinates]:[Y-Coordinates]]),\n" +
            "    calc1, xlookup1 - $I$92:$J$92,\n" +
            "    sumsq1, SUMSQ(calc1),\n" +
            "    sqrt1, SQRT(sumsq1),\n" +
            "    calc2, sqrt1 * 100,\n" +
            "    ROUND(calc2, 0)\n" +
            ")";

        Assert.Equal(expected, result.SynthesisedLet);
    }

    // ---------------- function-only / operator-only ----------------

    [Fact]
    public void Unnest_FunctionOnlyFormula_NamesEachNestedCall()
    {
        var result = UnnestEngine.Unnest("=ROUND(SQRT(SUM(A1:A10)), 2)");

        // SUM is a 1–3 letter base, so the plain "sum1" looks like a cell
        // reference (Excel rejects it as a name); the namer separates it as
        // "sum_1". SQRT is longer, so "sqrt1" stands.
        Assert.Collection(result.Steps,
            s => AssertStep(s, "sum_1", "SUM(A1:A10)", UnnestStepOrigin.Function),
            s => AssertStep(s, "sqrt1", "SQRT(sum_1)", UnnestStepOrigin.Function));
        Assert.Contains("ROUND(sqrt1, 2)", result.SynthesisedLet);
    }

    [Fact]
    public void Unnest_ShortFunctionNames_ProduceValidExcelNames()
    {
        // Every 1–3 letter function base + digits collides with the cell-ref
        // pattern; the namer must separate each so the dialog never flags an
        // auto-name as invalid.
        var result = UnnestEngine.Unnest("=ABS(MAX(LOG(A1), MIN(B1, C1)))");

        foreach (var step in result.Steps)
            Assert.True(
                ExcelNameValidator.Validate(step.Name).IsValid,
                $"Auto-name '{step.Name}' is not a valid Excel defined name.");
    }

    [Fact]
    public void Unnest_OperatorOnlyFormula_NamesEachOperatorCalcN()
    {
        var result = UnnestEngine.Unnest("=A1+B1*C1-D1");

        // ((A1 + (B1*C1)) - D1): the '-' is root (body); '*' then '+' are steps.
        Assert.Collection(result.Steps,
            s => AssertStep(s, "calc1", "B1 * C1", UnnestStepOrigin.Operator),
            s => AssertStep(s, "calc2", "A1 + calc1", UnnestStepOrigin.Operator));
        Assert.Contains("calc2 - D1", result.SynthesisedLet);
    }

    // ---------------- single-leaf / single-root no-op ----------------

    [Fact]
    public void Unnest_SingleRootOperator_ZeroStepsNoOp()
    {
        var result = UnnestEngine.Unnest("=A1+B1");

        Assert.Empty(result.Steps);
        Assert.Equal("=A1+B1", result.SynthesisedLet);
    }

    [Fact]
    public void Unnest_SingleLeaf_ZeroStepsNoOp()
    {
        var result = UnnestEngine.Unnest("=A1");

        Assert.Empty(result.Steps);
        Assert.Equal("=A1", result.SynthesisedLet);
    }

    [Fact]
    public void Unnest_LoneFunctionWithLeafArgs_ZeroSteps()
    {
        // The function IS the root → body; its leaf args nest nothing.
        var result = UnnestEngine.Unnest("=SUM(A1, B1)");

        Assert.Empty(result.Steps);
        Assert.Equal("=SUM(A1, B1)", result.SynthesisedLet);
    }

    [Fact]
    public void Unnest_BareRange_IsLeafNotStep()
    {
        // ':' is a reference operator — never a step.
        var result = UnnestEngine.Unnest("=A1:B5");

        Assert.Empty(result.Steps);
        Assert.Equal("=A1:B5", result.SynthesisedLet);
    }

    // ---------------- naming: function-derived, calcN, collision suffixing ----------------

    [Fact]
    public void Unnest_RepeatedFunction_EachGetsOwnSuffix()
    {
        var result = UnnestEngine.Unnest("=SQRT(A1) + SQRT(B1)");

        Assert.Collection(result.Steps,
            s => Assert.Equal("sqrt1", s.Name),
            s => Assert.Equal("sqrt2", s.Name));
        Assert.Contains("sqrt1 + sqrt2", result.SynthesisedLet);
    }

    [Fact]
    public void Unnest_NameCollidesWithWorkbookDefinedName_SuffixSkipsAhead()
    {
        var defined = new[] { "sumsq1", "calc1" };
        var result = UnnestEngine.Unnest(WorkedExample, defined);

        var sumsq = Assert.Single(result.Steps, s => s.Origin == UnnestStepOrigin.Function && s.OriginLabel == "SUMSQ");
        Assert.Equal("sumsq2", sumsq.Name);

        // Both operator steps must avoid the reserved 'calc1'.
        var calcs = result.Steps.Where(s => s.Origin == UnnestStepOrigin.Operator).Select(s => s.Name).ToList();
        Assert.Equal(new[] { "calc2", "calc3" }, calcs);
    }

    [Fact]
    public void Unnest_FunctionName_StripsXlfnPrefix()
    {
        var result = UnnestEngine.Unnest("=SUM(_xlfn.SEQUENCE(3))");

        var step = Assert.Single(result.Steps);
        Assert.Equal("sequence1", step.Name);
        Assert.Equal("_xlfn.SEQUENCE", step.OriginLabel);
    }

    // ---------------- include-toggle inlining + re-parenting ----------------

    [Fact]
    public void Recompute_UnIncludeOperatorStep_InlinesIntoParentAndReParentsChildren()
    {
        var initial = UnnestEngine.Unnest(WorkedExample);

        // Flip calc1 (the '-' operator) off; keep everything else.
        var states = initial.Steps
            .Select(s => new UnnestRowState(s.Key, s.Name, Include: s.Name != "calc1"))
            .ToList();

        var result = UnnestEngine.Recompute(WorkedExample, states);

        // calc1 row is still reported but excluded.
        var calc1 = Assert.Single(result.Steps, s => s.Name == "calc1");
        Assert.False(calc1.Include);

        // sumsq1 now inlines calc1's RHS — xlookup1 re-parents up into it.
        var sumsq = Assert.Single(result.Steps, s => s.Name == "sumsq1");
        Assert.Equal("SUMSQ(xlookup1 - $I$92:$J$92)", sumsq.Rhs);

        const string expected =
            "=LET(\n" +
            "    xlookup1, XLOOKUP(H94, t[City], t[[X-Coordinates]:[Y-Coordinates]]),\n" +
            "    sumsq1, SUMSQ(xlookup1 - $I$92:$J$92),\n" +
            "    sqrt1, SQRT(sumsq1),\n" +
            "    calc2, sqrt1 * 100,\n" +
            "    ROUND(calc2, 0)\n" +
            ")";
        Assert.Equal(expected, result.SynthesisedLet);
    }

    [Fact]
    public void Recompute_UnIncludeAllSteps_NoOpRewrite()
    {
        var initial = UnnestEngine.Unnest(WorkedExample);
        var states = initial.Steps
            .Select(s => new UnnestRowState(s.Key, s.Name, Include: false))
            .ToList();

        var result = UnnestEngine.Recompute(WorkedExample, states);

        Assert.Equal(WorkedExample, result.SynthesisedLet);
    }

    [Fact]
    public void Recompute_RenameStep_UsesCustomNameAndBodyReferencesIt()
    {
        var initial = UnnestEngine.Unnest(WorkedExample);
        var states = initial.Steps
            .Select(s => new UnnestRowState(s.Key, s.Name == "calc2" ? "dist" : s.Name))
            .ToList();

        var result = UnnestEngine.Recompute(WorkedExample, states);

        Assert.Single(result.Steps, s => s.Name == "dist");
        Assert.Contains("dist, sqrt1 * 100,", result.SynthesisedLet);
        Assert.Contains("ROUND(dist, 0)", result.SynthesisedLet);
    }

    [Fact]
    public void Recompute_RenameClashesWithAutoSuffix_AutoNamesAvoidIt()
    {
        // Rename the first SQRT step to 'sqrt2'; the second must not also
        // auto-name to 'sqrt2'.
        var initial = UnnestEngine.Unnest("=SQRT(A1) + SQRT(B1)");
        var states = new List<UnnestRowState>
        {
            new(initial.Steps[0].Key, "sqrt2"),
            new(initial.Steps[1].Key, ""), // empty → re-auto-name
        };

        var result = UnnestEngine.Recompute("=SQRT(A1) + SQRT(B1)", states);

        Assert.Equal("sqrt2", result.Steps[0].Name);
        Assert.NotEqual("sqrt2", result.Steps[1].Name);
    }

    // ---------------- round-trip through LetParser ----------------

    [Fact]
    public void Unnest_WorkedExample_RoundTripsThroughLetParser()
    {
        var result = UnnestEngine.Unnest(WorkedExample);

        var parsed = LetParser.Parse(result.SynthesisedLet);

        Assert.Equal(
            new[] { "xlookup1", "calc1", "sumsq1", "sqrt1", "calc2" },
            parsed.Bindings.Select(b => b.Name).ToArray());
        Assert.Equal("ROUND(calc2, 0)", parsed.Body);
        // Every binding RHS classifies as a calculation (function or operator).
        Assert.All(parsed.Bindings, b => Assert.True(b.IsCalculation));
    }

    // ---------------- scope: opaque LAMBDA / nested LET (#278) ----------------

    [Fact]
    public void Unnest_InnerLambda_KeptIntactAndNotDecomposed()
    {
        // BYROW's lambda binds 'r'; SUM(r) / MAX(r) inside it must NOT be
        // hoisted (they'd reference an unbound 'r' at the top level). The whole
        // lambda stays inline in byrow1's RHS.
        var result = UnnestEngine.Unnest("=ROUND(BYROW(A1:B3, LAMBDA(r, SUM(r) + MAX(r))), 2)");

        var step = Assert.Single(result.Steps);
        Assert.Equal("byrow1", step.Name);
        Assert.Equal("BYROW(A1:B3, LAMBDA(r, SUM(r) + MAX(r)))", step.Rhs);
        Assert.Contains("ROUND(byrow1, 2)", result.SynthesisedLet);

        // Round-trips, and no binding ever references the bound param 'r'.
        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.Equal(new[] { "byrow1" }, parsed.Bindings.Select(b => b.Name).ToArray());
    }

    [Fact]
    public void Unnest_StructureAroundLambda_DecomposesButLeavesLambdaIntact()
    {
        // SQRT(A1:B3) (BYROW's first arg) is outside the lambda → a step;
        // AVERAGE / BYROW outside too → steps. SUM(r) inside the lambda stays.
        var result = UnnestEngine.Unnest(
            "=ROUND(AVERAGE(BYROW(SQRT(A1:B3), LAMBDA(r, SUM(r)))), 2)");

        Assert.Equal(
            new[] { "sqrt1", "byrow1", "average1" },
            result.Steps.Select(s => s.Name).ToArray());
        Assert.Equal("BYROW(sqrt1, LAMBDA(r, SUM(r)))",
            Assert.Single(result.Steps, s => s.Name == "byrow1").Rhs);

        // SUM(r) was never extracted as its own step.
        Assert.DoesNotContain(result.Steps, s => s.Rhs == "SUM(r)");
    }

    [Fact]
    public void Unnest_NestedDoubleLambda_AllParamDependentNodesStayInline()
    {
        // Mirrors the reported case: two nested lambdas (binds r, then a/b).
        // Everything inside either lambda must stay inline.
        const string formula =
            "=ROUND(BYROW(A1:B3, LAMBDA(r, SUM(PAIROP(r, LAMBDA(a, b, SQRT(SUMSQ(VSTACK(a, b)))), , 1)))), 2)";
        var result = UnnestEngine.Unnest(formula);

        var step = Assert.Single(result.Steps);
        Assert.Equal("byrow1", step.Name);
        // The full nested-lambda payload survives verbatim in the RHS.
        Assert.Contains("LAMBDA(a, b, SQRT(SUMSQ(VSTACK(a, b))))", step.Rhs);
        // None of the lambda-interior nodes leaked out as their own step
        // (a node "leaks" only if it became a step, i.e. carries that
        // OriginLabel — its appearance inside the kept lambda's RHS is fine).
        Assert.DoesNotContain(result.Steps,
            s => s.OriginLabel is "VSTACK" or "SUMSQ" or "PAIROP" or "SQRT" or "SUM");
    }

    [Fact]
    public void Unnest_NestedLetSubExpression_IsOpaque()
    {
        // A nested LET binds 'x' inside itself; the engine treats it as opaque
        // (it is never the root here, so the top-level refusal doesn't apply).
        var result = UnnestEngine.Unnest("=ROUND(LET(x, A1, x + 1), 2)");

        Assert.Null(result.Diagnostic);
        Assert.Empty(result.Steps);
        Assert.Equal("=ROUND(LET(x, A1, x + 1), 2)", result.SynthesisedLet);
    }

    [Fact]
    public void Unnest_RootLambda_ZeroStepsNoOp()
    {
        var result = UnnestEngine.Unnest("=LAMBDA(x, x + 1)");

        Assert.Null(result.Diagnostic);
        Assert.Empty(result.Steps);
        Assert.Equal("=LAMBDA(x, x + 1)", result.SynthesisedLet);
    }

    [Fact]
    public void Unnest_ReportedFormula_DecomposesOuterNestKeepsLambdasIntact()
    {
        const string formula =
            "=ROUND(MIN(BIROW(HSTACK(IFS(SEQUENCE(6), \"Vienna\"), " +
            "PERMUTATIONS(XLOOKUP(G141:I141,t[Concat], t[City]),3)), " +
            "LAMBDA(r, SUM(PAIROP(r, LAMBDA(a,b, SQRT(SUMSQ(PAIROP(XV(VSTACK(a,b), " +
            "t[[City]:[Y-Coordinates]], {2,3}),,1)))*100),,1)))))+45,)";

        var result = UnnestEngine.Unnest(formula);

        Assert.Null(result.Diagnostic);

        // Outer (non-lambda) structure decomposes — e.g. the XLOOKUP inside HSTACK
        // becomes its own step.
        Assert.Contains(result.Steps, s => s.OriginLabel == "XLOOKUP");

        // Nothing from inside the lambdas leaks out as its own step.
        Assert.DoesNotContain(result.Steps,
            s => s.OriginLabel is "VSTACK" or "SUMSQ" or "XV" or "PAIROP" or "SQRT" or "SUM");
        // The param-dependent expression stays inline within the kept lambda.
        Assert.Contains("VSTACK(a, b)", result.SynthesisedLet);

        // The whole thing still round-trips through LetParser.
        var parsed = LetParser.Parse(result.SynthesisedLet);
        Assert.NotEmpty(parsed.Bindings);
    }

    // ---------------- guards ----------------

    [Fact]
    public void Unnest_ExistingLet_RefusesWithDiagnostic()
    {
        var result = UnnestEngine.Unnest("=LET(x, A1, x+1)");

        Assert.NotNull(result.Diagnostic);
        Assert.Equal(UnnestDiagnosticKind.ExistingLet, result.Diagnostic!.Kind);
        Assert.Empty(result.Steps);
        Assert.Equal("=LET(x, A1, x+1)", result.SynthesisedLet);
    }

    [Fact]
    public void Unnest_MalformedFormula_RefusesWithDiagnostic()
    {
        var result = UnnestEngine.Unnest("=SUM(A1,");

        Assert.NotNull(result.Diagnostic);
        Assert.Equal(UnnestDiagnosticKind.MalformedFormula, result.Diagnostic!.Kind);
    }

    [Fact]
    public void Unnest_NullFormula_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => UnnestEngine.Unnest(null!));
    }

    private static void AssertStep(
        UnnestStepRow step, string name, string rhs, UnnestStepOrigin origin)
    {
        Assert.Equal(name, step.Name);
        Assert.Equal(rhs, step.Rhs);
        Assert.Equal(origin, step.Origin);
        Assert.True(step.Include);
    }
}
