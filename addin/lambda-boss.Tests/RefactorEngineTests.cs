using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Spec 0008 / PR 1 — the tracer slice's engine tests. Covers the
///     happy path (refs, ranges, spill-distinct, sheet-qualifier
///     dedupe), the rename path, the Include-drop path, and round-trip
///     safety via <see cref="LetParser" /> + <see cref="LetToLambdaBuilder" />.
/// </summary>
public class RefactorEngineTests
{
    private const string Sheet = "Sheet1";

    [Fact]
    public void Refactor_SingleCellRef_BindsToInput1()
    {
        var result = RefactorEngine.Refactor("=A1*2", Sheet);

        Assert.Null(result.Diagnostic);
        var row = Assert.Single(result.Inputs);
        Assert.Equal("input1", row.Name);
        Assert.Equal("A1", row.Rhs);
        Assert.Contains("input1*2", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_MultipleRefsDeduped_OneBindingPerUniqueRef()
    {
        // A1 appears twice, B2 once → two bindings, two rewrites of A1.
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
        // Spec 0008 dedupe rule: A1 and A1# get distinct bindings.
        var result = RefactorEngine.Refactor("=A1+SUM(A1#)", Sheet);

        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal("A1", result.Inputs[0].Rhs);
        Assert.Equal("A1#", result.Inputs[1].Rhs);
        Assert.Contains("input1+SUM(input2)", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_SheetQualifiedRefMatchingActiveSheet_CollapsesToBareForm()
    {
        // Sheet1!A1 with active sheet Sheet1 should dedupe with A1.
        var result = RefactorEngine.Refactor("=A1+Sheet1!A1", Sheet);

        var row = Assert.Single(result.Inputs);
        Assert.Equal("A1", row.Rhs);
        Assert.Contains("input1+input1", result.SynthesisedLet);
    }

    [Fact]
    public void Refactor_CrossSheetRef_StaysDistinct()
    {
        // Sheet2!A1 from Sheet1 stays cross-sheet; the binding RHS is
        // sheet-qualified so the LET resolves correctly when the cell
        // lives on Sheet1.
        var result = RefactorEngine.Refactor("=A1+Sheet2!A1", Sheet);

        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal("A1", result.Inputs[0].Rhs);
        Assert.Equal("Sheet2!A1", result.Inputs[1].Rhs);
    }

    [Fact]
    public void Refactor_ExistingLet_Refused()
    {
        var result = RefactorEngine.Refactor("=LET(x, A1, x + 1)", Sheet);

        Assert.NotNull(result.Diagnostic);
        Assert.Equal(RefactorDiagnosticKind.ExistingLet, result.Diagnostic.Kind);
        Assert.Empty(result.Inputs);
        Assert.Equal("=LET(x, A1, x + 1)", result.SynthesisedLet);
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

        var renamed = new[] { new RefactorRowState(row.Source, "count") };
        var result = RefactorEngine.Recompute("=IF(A1<10, A1*2, 0)", Sheet, renamed);

        Assert.Single(result.Inputs);
        Assert.Equal("count", result.Inputs[0].Name);
        Assert.Contains("IF(count<10, count*2, 0)", result.SynthesisedLet);
    }

    [Fact]
    public void Recompute_DroppedRow_LeavesTokenInBody()
    {
        // Worked example from the spec: drop A1 → "IF(A1<10, ...)" stays.
        var initial = RefactorEngine.Refactor("=IF(A1<10, A1*2, B1)", Sheet);
        var rows = initial.Inputs
            .Select(r => new RefactorRowState(
                r.Source,
                r.Name,
                Include: r.Rhs != "A1"))
            .ToArray();

        var result = RefactorEngine.Recompute(
            "=IF(A1<10, A1*2, B1)", Sheet, rows);

        Assert.Single(result.Inputs);
        Assert.Equal("B1", result.Inputs[0].Rhs);
        // A1 stays as A1 because its binding was dropped.
        Assert.Contains("IF(A1<10, A1*2, input2)", result.SynthesisedLet);
    }

    [Fact]
    public void Recompute_Reordered_BindingOrderMatchesRowOrder()
    {
        var initial = RefactorEngine.Refactor("=A1+B2", Sheet);
        // Swap order: B2 first, A1 second.
        var swapped = new[]
        {
            new RefactorRowState(initial.Inputs[1].Source, initial.Inputs[1].Name),
            new RefactorRowState(initial.Inputs[0].Source, initial.Inputs[0].Name),
        };

        var result = RefactorEngine.Recompute("=A1+B2", Sheet, swapped);

        Assert.Equal("B2", result.Inputs[0].Rhs);
        Assert.Equal("A1", result.Inputs[1].Rhs);
        // Synthesised LET emits bindings in dialog order — B2 first.
        var b2Pos = result.SynthesisedLet.IndexOf("B2", StringComparison.Ordinal);
        var a1Pos = result.SynthesisedLet.IndexOf(" A1,", StringComparison.Ordinal);
        Assert.True(b2Pos < a1Pos, "B2 binding should precede A1 binding");
    }

    [Fact]
    public void Refactor_SpecWorkedExample_ProducesRoundTrippableLet()
    {
        // The worked example from the spec.
        var formula = "=IF(A1<10, IF(A1>2, SUM(B1:B5), 0), SUM(B2:B6))";
        var result = RefactorEngine.Refactor(formula, Sheet);

        Assert.Null(result.Diagnostic);
        Assert.Equal(3, result.Inputs.Count);
        Assert.Equal("A1", result.Inputs[0].Rhs);
        Assert.Equal("B1:B5", result.Inputs[1].Rhs);
        Assert.Equal("B2:B6", result.Inputs[2].Rhs);

        // Round-trip safety.
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

    /// <summary>
    ///     Confirms the synthesised LET parses via <see cref="LetParser.Parse" />
    ///     AND feeds cleanly through <see cref="LetToLambdaBuilder.Build" />
    ///     with a default request keeping all inputs. Catches both
    ///     malformed-LET output and LET-that-LetToLambdaBuilder-rejects
    ///     output early.
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
}
