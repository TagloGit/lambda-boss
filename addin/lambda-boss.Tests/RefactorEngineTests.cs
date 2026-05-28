using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Spec 0008 — <see cref="RefactorEngine" /> tests. Covers the non-LET
///     happy path (refs, ranges, spill-distinct, sheet-qualifier dedupe),
///     the rename / drop / reorder paths, the existing-LET path (merge,
///     extract from calc bindings and body, ISOMITTED survival, drop
///     inlines RHS, auto-name collision), and round-trip safety via
///     <see cref="LetParser" /> + <see cref="LetToLambdaBuilder" />.
/// </summary>
public class RefactorEngineTests
{
    private const string Sheet = "Sheet1";

    // ---------- non-LET path ----------

    [Fact]
    public void Refactor_SingleCellRef_BindsToInput1()
    {
        var result = RefactorEngine.Refactor("=A1*2", Sheet);

        Assert.Null(result.Diagnostic);
        var row = Assert.Single(result.Inputs);
        Assert.Equal("input1", row.Name);
        Assert.Equal("A1", row.Rhs);
        Assert.Equal(RefactorRowOrigin.Extracted, row.Origin);
        Assert.Contains("input1*2", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_MultipleRefsDeduped_OneBindingPerUniqueRef()
    {
        var result = RefactorEngine.Refactor("=A1+B2+A1", Sheet);

        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal("input1", result.Inputs[0].Name);
        Assert.Equal("A1", result.Inputs[0].Rhs);
        Assert.Equal("input2", result.Inputs[1].Name);
        Assert.Equal("B2", result.Inputs[1].Rhs);
        Assert.Contains("input1+input2+input1", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_Range_BindsAsSingleInput()
    {
        var result = RefactorEngine.Refactor("=SUM(A1:B5)", Sheet);

        var row = Assert.Single(result.Inputs);
        Assert.Equal("input1", row.Name);
        Assert.Equal("A1:B5", row.Rhs);
        Assert.Contains("SUM(input1)", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_SpillRefAndAnchor_BindAsTwoDistinctInputs()
    {
        var result = RefactorEngine.Refactor("=A1+SUM(A1#)", Sheet);

        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal("A1", result.Inputs[0].Rhs);
        Assert.Equal("A1#", result.Inputs[1].Rhs);
        Assert.Contains("input1+SUM(input2)", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_SheetQualifiedRefMatchingActiveSheet_CollapsesToBareForm()
    {
        var result = RefactorEngine.Refactor("=A1+Sheet1!A1", Sheet);

        var row = Assert.Single(result.Inputs);
        Assert.Equal("A1", row.Rhs);
        Assert.Contains("input1+input1", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_CrossSheetRef_StaysDistinct()
    {
        var result = RefactorEngine.Refactor("=A1+Sheet2!A1", Sheet);

        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal("A1", result.Inputs[0].Rhs);
        Assert.Equal("Sheet2!A1", result.Inputs[1].Rhs);
    }

    [Fact]
    public void Refactor_NoRefs_LiteralFormulaReturnedVerbatim()
    {
        var result = RefactorEngine.Refactor("=1+2", Sheet);

        Assert.Empty(result.Inputs);
        Assert.Equal("=1+2", result.SynthesisedLet);
    }

    [Fact]
    public void Recompute_Renamed_RewritesBodyWithNewName()
    {
        var initial = RefactorEngine.Refactor("=IF(A1<10, A1*2, 0)", Sheet);
        var row = initial.Inputs[0];

        var renamed = new[] { new RefactorRowState(row.Key, "count") };
        var result = RefactorEngine.Recompute("=IF(A1<10, A1*2, 0)", Sheet, renamed);

        Assert.Single(result.Inputs);
        Assert.Equal("count", result.Inputs[0].Name);
        Assert.Contains("IF(count<10, count*2, 0)", result.SynthesisedLet);
    }

    [Fact]
    public void Recompute_DroppedRow_LeavesTokenInBody()
    {
        var initial = RefactorEngine.Refactor("=IF(A1<10, A1*2, B1)", Sheet);
        var rows = initial.Inputs
            .Select(r => new RefactorRowState(
                r.Key,
                r.Name,
                Include: r.Rhs != "A1"))
            .ToArray();

        var result = RefactorEngine.Recompute(
            "=IF(A1<10, A1*2, B1)", Sheet, rows);

        Assert.Single(result.Inputs);
        Assert.Equal("B1", result.Inputs[0].Rhs);
        Assert.Contains("IF(A1<10, A1*2, input2)", result.SynthesisedLet);
    }

    [Fact]
    public void Recompute_Reordered_BindingOrderMatchesRowOrder()
    {
        var initial = RefactorEngine.Refactor("=A1+B2", Sheet);
        var swapped = new[]
        {
            new RefactorRowState(initial.Inputs[1].Key, initial.Inputs[1].Name),
            new RefactorRowState(initial.Inputs[0].Key, initial.Inputs[0].Name),
        };

        var result = RefactorEngine.Recompute("=A1+B2", Sheet, swapped);

        Assert.Equal("B2", result.Inputs[0].Rhs);
        Assert.Equal("A1", result.Inputs[1].Rhs);
        var b2Pos = result.SynthesisedLet.IndexOf("B2", StringComparison.Ordinal);
        var a1Pos = result.SynthesisedLet.IndexOf(" A1,", StringComparison.Ordinal);
        Assert.True(b2Pos < a1Pos, "B2 binding should precede A1 binding");
    }

    [Fact]
    public void Refactor_SpecWorkedExample_ProducesRoundTrippableLet()
    {
        var formula = "=IF(A1<10, IF(A1>2, SUM(B1:B5), 0), SUM(B2:B6))";
        var result = RefactorEngine.Refactor(formula, Sheet);

        Assert.Null(result.Diagnostic);
        Assert.Equal(3, result.Inputs.Count);
        Assert.Equal("A1", result.Inputs[0].Rhs);
        Assert.Equal("B1:B5", result.Inputs[1].Rhs);
        Assert.Equal("B2:B6", result.Inputs[2].Rhs);

        AssertRoundTrips(result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_RoundTripsViaLetParserAndBuilder_ForCommonShapes()
    {
        var formulas = new[]
        {
            "=A1*2",
            "=A1+B2+C3",
            "=SUM(A1:A10)*B1",
            "=IF(A1=0, B1, C1/A1)",
            "=A1+SUM(A1#)",
            "=Sheet2!A1 + A1",
        };

        foreach (var formula in formulas)
        {
            var result = RefactorEngine.Refactor(formula, Sheet);
            Assert.Null(result.Diagnostic);
            AssertRoundTrips(result.SynthesisedLet);
        }
    }

    // ---------- existing-LET path ----------

    [Fact]
    public void Refactor_ExistingLet_TreatsValueBindingsAsInputs()
    {
        var result = RefactorEngine.Refactor("=LET(a, A1, b, B1, a + b)", Sheet);

        Assert.Null(result.Diagnostic);
        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal("a", result.Inputs[0].Name);
        Assert.Equal("A1", result.Inputs[0].Rhs);
        Assert.Equal(RefactorRowOrigin.ExistingLetValue, result.Inputs[0].Origin);
        Assert.Equal("b", result.Inputs[1].Name);
        Assert.Equal("B1", result.Inputs[1].Rhs);
        Assert.Empty(result.CalcBindings);
    }

    [Fact]
    public void Refactor_ExistingLet_MergesDuplicateValueBindings_FirstWins()
    {
        // a, A1 + b, A1 → merge b into a; body 'a + b' rewrites to 'a + a'.
        var result = RefactorEngine.Refactor("=LET(a, A1, b, A1, a + b)", Sheet);

        Assert.Null(result.Diagnostic);
        var row = Assert.Single(result.Inputs);
        Assert.Equal("a", row.Name);
        Assert.Equal("A1", row.Rhs);
        Assert.NotNull(row.MergedFrom);
        Assert.Equal(new[] { "b" }, row.MergedFrom);
        Assert.Contains("a + a", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_ExistingLet_MergesLiteralRhs()
    {
        // Bindings whose RHS isn't a single ref fall back to literal string
        // compare for merge — two `"hello"` literals collapse.
        var result = RefactorEngine.Refactor(
            "=LET(greeting, \"hello\", salutation, \"hello\", greeting & salutation)", Sheet);

        var row = Assert.Single(result.Inputs);
        Assert.Equal("greeting", row.Name);
        Assert.Equal("\"hello\"", row.Rhs);
        Assert.Equal(new[] { "salutation" }, row.MergedFrom);
        Assert.Contains("greeting & greeting", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_ExistingLet_SpecWorkedExample()
    {
        // Spec's messy-LET worked example: a/b dedupe + B1:B5 hoisted from
        // calc binding.
        var formula = "=LET(a, A1, b, A1, getMax, MAX(B1:B5), IF(a<10, getMax, b))";
        var result = RefactorEngine.Refactor(formula, Sheet);

        Assert.Null(result.Diagnostic);
        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal("a", result.Inputs[0].Name);
        Assert.Equal("A1", result.Inputs[0].Rhs);
        Assert.Equal(new[] { "b" }, result.Inputs[0].MergedFrom);
        Assert.Equal("input1", result.Inputs[1].Name);
        Assert.Equal("B1:B5", result.Inputs[1].Rhs);

        var calc = Assert.Single(result.CalcBindings);
        Assert.Equal("getMax", calc.Name);
        Assert.Equal("MAX(input1)", calc.RewrittenRhs);

        // Body: IF(a<10, getMax, b)  →  IF(a<10, getMax, a)
        Assert.Contains("IF(a<10, getMax, a)", result.SynthesisedLet);
        AssertRoundTrips(result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_ExistingLet_ExtractsRefsFromBody()
    {
        var result = RefactorEngine.Refactor("=LET(a, A1, a + B1)", Sheet);

        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal("a", result.Inputs[0].Name);
        Assert.Equal("input1", result.Inputs[1].Name);
        Assert.Equal("B1", result.Inputs[1].Rhs);
        Assert.Contains("a + input1", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_ExistingLet_AutoNameSkipsExistingBindingNames()
    {
        // Existing LET uses 'input1' as a binding name; new auto-name
        // allocator must skip 1 and use input2 onward.
        var result = RefactorEngine.Refactor("=LET(input1, A1, input1 + B1)", Sheet);

        var newRow = result.Inputs.Single(r => r.Origin == RefactorRowOrigin.Extracted);
        Assert.Equal("input2", newRow.Name);
        Assert.Equal("B1", newRow.Rhs);
    }

    [Fact]
    public void Refactor_ExistingLet_IsomittedWrapper_Survives_AndRefsInDefaultExtracted()
    {
        // Mirrors the shape produced by /EditLambda on a LAMBDA with an
        // optional `factor` param: `factor` becomes a calc binding whose
        // RHS wraps the default expression in IF(ISOMITTED(factor), ...).
        // The cell ref inside the default expression should extract; the
        // ISOMITTED wrapper should survive verbatim.
        var formula =
            "=LET(x, A1, factor, IF(ISOMITTED(factor), B1*0.1, factor), x*factor)";
        var result = RefactorEngine.Refactor(formula, Sheet);

        Assert.Null(result.Diagnostic);
        // x stays as a value binding; B1 hoists out of the calc binding.
        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal("x", result.Inputs[0].Name);
        Assert.Equal("A1", result.Inputs[0].Rhs);
        Assert.Equal("input1", result.Inputs[1].Name);
        Assert.Equal("B1", result.Inputs[1].Rhs);

        var calc = Assert.Single(result.CalcBindings);
        Assert.Equal("factor", calc.Name);
        // ISOMITTED(factor) survives untouched; B1 inside the default
        // becomes input1.
        Assert.Equal("IF(ISOMITTED(factor), input1*0.1, factor)", calc.RewrittenRhs);
    }

    [Fact]
    public void Refactor_ExistingLet_AlreadyTidyLet_NoOpRewrite()
    {
        // Tidy LET: no merge, no extractable refs in calc/body.
        var result = RefactorEngine.Refactor("=LET(a, A1, b, B1, a + b)", Sheet);

        Assert.Null(result.Diagnostic);
        Assert.Equal(2, result.Inputs.Count);
        Assert.Empty(result.CalcBindings);
        // Output should be the same LET, modulo whitespace.
        var stripped = StripWhitespace(result.SynthesisedLet);
        Assert.Equal("=LET(a,A1,b,B1,a+b)", stripped);
    }

    [Fact]
    public void Refactor_MalformedLet_ReturnsDiagnostic()
    {
        // Missing close paren — LetParser throws FormatException; the
        // engine wraps it in a MalformedLet diagnostic.
        var result = RefactorEngine.Refactor("=LET(a, A1, a + 1", Sheet);

        Assert.NotNull(result.Diagnostic);
        Assert.Equal(RefactorDiagnosticKind.MalformedLet, result.Diagnostic!.Kind);
        Assert.Empty(result.Inputs);
        Assert.Empty(result.CalcBindings);
        Assert.Equal("=LET(a, A1, a + 1", result.SynthesisedLet);
    }

    [Fact]
    public void Recompute_ExistingLet_DropValueBinding_InlinesOriginalRhs()
    {
        // Drop the value binding `a, A1`. References to `a` everywhere
        // collapse back to `A1`.
        var formula = "=LET(a, A1, calc, a + 5, a * calc)";
        var initial = RefactorEngine.Refactor(formula, Sheet);
        var rowStates = initial.Inputs
            .Select(r => new RefactorRowState(r.Key, r.Name, Include: false))
            .ToList();

        var result = RefactorEngine.Recompute(formula, Sheet, rowStates);

        Assert.Empty(result.Inputs);
        var calc = Assert.Single(result.CalcBindings);
        Assert.Equal("calc", calc.Name);
        Assert.Equal("A1 + 5", calc.RewrittenRhs);
        // Body: a * calc → A1 * calc.
        Assert.Contains("A1 * calc", result.SynthesisedLet);
        AssertRoundTrips(result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_ExistingLet_PreservesCalcBindingsInSourceOrder()
    {
        var formula = "=LET(a, A1, step1, a + 1, step2, step1 * 2, step2 - a)";
        var result = RefactorEngine.Refactor(formula, Sheet);

        Assert.Single(result.Inputs);
        Assert.Equal(2, result.CalcBindings.Count);
        Assert.Equal("step1", result.CalcBindings[0].Name);
        Assert.Equal("step2", result.CalcBindings[1].Name);
        var firstPos = result.SynthesisedLet.IndexOf("step1,", StringComparison.Ordinal);
        var secondPos = result.SynthesisedLet.IndexOf("step2,", StringComparison.Ordinal);
        Assert.True(firstPos < secondPos, "step1 should precede step2 in source-order emit");
    }

    [Fact]
    public void Recompute_ExistingLet_ReorderInputs_DialogOrderWins()
    {
        var formula = "=LET(a, A1, b, B1, a + b)";
        var initial = RefactorEngine.Refactor(formula, Sheet);
        var swapped = new[]
        {
            new RefactorRowState(initial.Inputs[1].Key, initial.Inputs[1].Name),
            new RefactorRowState(initial.Inputs[0].Key, initial.Inputs[0].Name),
        };

        var result = RefactorEngine.Recompute(formula, Sheet, swapped);

        Assert.Equal("b", result.Inputs[0].Name);
        Assert.Equal("a", result.Inputs[1].Name);
        var bPos = result.SynthesisedLet.IndexOf("b, B1", StringComparison.Ordinal);
        var aPos = result.SynthesisedLet.IndexOf("a, A1", StringComparison.Ordinal);
        Assert.True(bPos < aPos, "b binding should precede a binding");
    }

    [Fact]
    public void Recompute_ExistingLet_RenameValueBinding_RewritesEverywhere()
    {
        var formula = "=LET(a, A1, b, A1, calc, a + b, a * calc + b)";
        var initial = RefactorEngine.Refactor(formula, Sheet);

        // a is the survivor; rename it to 'first'. b (merged-away) should
        // also resolve to 'first' in body + calc bindings.
        var rowStates = initial.Inputs
            .Select(r => new RefactorRowState(r.Key, "first"))
            .ToList();

        var result = RefactorEngine.Recompute(formula, Sheet, rowStates);

        Assert.Single(result.Inputs);
        Assert.Equal("first", result.Inputs[0].Name);
        var calc = Assert.Single(result.CalcBindings);
        Assert.Equal("first + first", calc.RewrittenRhs);
        Assert.Contains("first * calc + first", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_ExistingLet_RoundTripsViaLetParserAndBuilder()
    {
        var formulas = new[]
        {
            "=LET(a, A1, a + 1)",
            "=LET(a, A1, b, A1, a + b)",
            "=LET(a, A1, getMax, MAX(B1:B5), IF(a<10, getMax, a))",
            "=LET(x, A1, factor, IF(ISOMITTED(factor), B1*0.1, factor), x*factor)",
            "=LET(a, A1, step1, a + 1, step2, step1 * 2, step2 - a)",
        };

        foreach (var formula in formulas)
        {
            var result = RefactorEngine.Refactor(formula, Sheet);
            Assert.Null(result.Diagnostic);
            AssertRoundTrips(result.SynthesisedLet);
        }
    }

    /// <summary>
    ///     Confirms the synthesised LET parses via <see cref="LetParser.Parse" />
    ///     AND feeds cleanly through <see cref="LetToLambdaBuilder.Build" />
    ///     with a default request keeping all inputs.
    /// </summary>
    private static void AssertRoundTrips(string synthesisedLet)
    {
        var parsed = LetParser.Parse(synthesisedLet);
        var inputs = parsed.Bindings
            .Where(b => !b.IsCalculation)
            .Select(b => new InputChoice(b.Name, b.Name, Keep: true))
            .ToList();
        var request = new LambdaGenerationRequest("RoundTrip", parsed, inputs);
        var lambdaText = LetToLambdaBuilder.Build(request);
        Assert.StartsWith("=LAMBDA(", lambdaText);
    }

    private static string StripWhitespace(string s)
    {
        return new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }
}
