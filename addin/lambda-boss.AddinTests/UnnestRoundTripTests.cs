using System.Runtime.InteropServices;

using Xunit;
using Xunit.Abstractions;

namespace LambdaBoss.AddinTests;

/// <summary>
///     Spec 0009 / issue #272 — real-Excel coverage for the <c>/Unnest</c>
///     command's output. The dialog itself is interactive (modal WPF window)
///     and can't be driven headless, so these tests exercise the same engine
///     the command runs (<see cref="UnnestEngine" />) and prove its synthesised
///     LET is accepted by Excel, round-trips through <see cref="LetParser" />,
///     preserves the original value, and composes through
///     <c>/Refactor → /LET to LAMBDA</c> into a working LAMBDA. The dialog's
///     own activation guard (empty/literal cell → silent no-op) is verified via
///     the <c>HasFormula</c> precondition the command keys on.
/// </summary>
[Collection("Excel Addin")]
public class UnnestRoundTripTests
{
    // Structurally mirrors the spec's worked example (ROUND / SQRT / SUMSQ over
    // an operator subtree) without the table dependency, so the test is
    // self-contained: each leaf is a real cell.
    private const string NestedFormula = "=ROUND(SQRT(SUMSQ(A1 - B1)) * 100, 0)";

    private readonly ExcelAddinFixture _excel;
    private readonly ITestOutputHelper _output;

    public UnnestRoundTripTests(ExcelAddinFixture excel, ITestOutputHelper output)
    {
        _excel = excel;
        _output = output;
    }

    [Fact]
    public void Unnest_WorkedExample_WritesLetThatParsesAndPreservesValue()
    {
        var ws = _excel.AddWorksheet();
        try
        {
            SeedLeaves(ws);

            // Baseline: the original nested formula's computed value.
            var original = ws.Range["D1"];
            var unnested = ws.Range["D2"];
            try
            {
                original.Formula2 = NestedFormula;
                _excel.Application.Calculate();
                var expected = Convert.ToDouble(original.Value);

                var result = UnnestEngine.Unnest(NestedFormula);
                _output.WriteLine($"Synthesised LET:\n{result.SynthesisedLet}");

                // The synthesised LET must round-trip through LetParser (the
                // canonical shape /LET to LAMBDA expects).
                Assert.True(LetParser.IsLetFormula(result.SynthesisedLet));
                var parsed = LetParser.Parse(result.SynthesisedLet);
                Assert.NotEmpty(parsed.Bindings);

                // Excel must accept the synthesised formula and compute the
                // same value as the original.
                unnested.Formula2 = result.SynthesisedLet;
                _excel.Application.Calculate();
                var actual = Convert.ToDouble(unnested.Value);

                _output.WriteLine($"Original = {expected}, unnested = {actual}");
                Assert.Equal(expected, actual);
            }
            finally
            {
                Marshal.ReleaseComObject(original);
                Marshal.ReleaseComObject(unnested);
            }
        }
        finally
        {
            CleanupSheet(ws);
        }
    }

    [Fact]
    public void Unnest_ThenRefactor_ThenLetToLambda_ProducesWorkingLambda()
    {
        var ws = _excel.AddWorksheet();
        var sheetName = (string)ws.Name;
        try
        {
            SeedLeaves(ws);

            var original = ws.Range["D1"];
            var viaLambda = ws.Range["D2"];
            try
            {
                original.Formula2 = NestedFormula;
                _excel.Application.Calculate();
                var expected = Convert.ToDouble(original.Value);

                // /Unnest — explode nested calls/operators into LET steps.
                var unnested = UnnestEngine.Unnest(NestedFormula).SynthesisedLet;

                // /Refactor — hoist the leaves (A1, B1) into input bindings.
                var refactored = RefactorEngine.Refactor(unnested, sheetName).SynthesisedLet;
                _output.WriteLine($"Refactored LET:\n{refactored}");

                // /LET to LAMBDA — keep every value binding as a parameter.
                var parsed = LetParser.Parse(refactored);
                var inputs = parsed.Bindings
                    .Where(b => !b.IsCalculation)
                    .Select(b => new InputChoice(b.Name, b.Name, Keep: true))
                    .ToList();
                Assert.NotEmpty(inputs); // Refactor must have hoisted A1/B1.

                var lambdaText = LetToLambdaBuilder.Build(
                    new LambdaGenerationRequest("UNNEST_CHAIN_TEST", parsed, inputs));
                _output.WriteLine($"LAMBDA:\n{lambdaText}");

                _excel.Workbook.Names.Add("UNNEST_CHAIN_TEST", lambdaText);

                // Invoke the registered LAMBDA, passing the same leaves in
                // first-seen order (A1, then B1).
                viaLambda.Formula2 = "=UNNEST_CHAIN_TEST(A1, B1)";
                _excel.Application.Calculate();
                var actual = Convert.ToDouble(viaLambda.Value);

                _output.WriteLine($"Original = {expected}, via LAMBDA = {actual}");
                Assert.Equal(expected, actual);
            }
            finally
            {
                Marshal.ReleaseComObject(original);
                Marshal.ReleaseComObject(viaLambda);
                TryDeleteName("UNNEST_CHAIN_TEST");
            }
        }
        finally
        {
            CleanupSheet(ws);
        }
    }

    [Fact]
    public void Unnest_ThenReNestAll_ProducesNestedFormulaExcelAccepts()
    {
        // Issue #285 — the reverse direction. Unnest the nested formula into a
        // LET, then re-open /Unnest on that LET and inline every binding-step
        // (deselect-all). The result must be a bare nested formula that Excel
        // accepts and computes to the same value as the original.
        var ws = _excel.AddWorksheet();
        try
        {
            SeedLeaves(ws);

            var original = ws.Range["D1"];
            var reNested = ws.Range["D2"];
            try
            {
                original.Formula2 = NestedFormula;
                _excel.Application.Calculate();
                var expected = Convert.ToDouble(original.Value);

                // /Unnest the nested formula into a LET of named steps.
                var let = UnnestEngine.Unnest(NestedFormula).SynthesisedLet;
                Assert.True(LetParser.IsLetFormula(let));

                // Re-open /Unnest on the LET: every calc binding is now a
                // toggleable binding-step. Inline them all.
                var reopened = UnnestEngine.Unnest(let);
                Assert.NotEmpty(reopened.Steps);
                var inlineAll = reopened.Steps
                    .Select(s => new UnnestRowState(s.Key, s.Name, Include: false))
                    .ToList();
                var collapsed = UnnestEngine.Recompute(let, inlineAll).SynthesisedLet;
                _output.WriteLine($"Re-nested:\n{collapsed}");

                // Collapsed back to a single expression (no LET wrapper).
                Assert.False(LetParser.IsLetFormula(collapsed));

                reNested.Formula2 = collapsed;
                _excel.Application.Calculate();
                var actual = Convert.ToDouble(reNested.Value);

                _output.WriteLine($"Original = {expected}, re-nested = {actual}");
                Assert.Equal(expected, actual);
            }
            finally
            {
                Marshal.ReleaseComObject(original);
                Marshal.ReleaseComObject(reNested);
            }
        }
        finally
        {
            CleanupSheet(ws);
        }
    }

    [Fact]
    public void Unnest_Refactor_ThenReNestAll_RoundTripsToBareFormula()
    {
        // Issue #285 follow-up: a LET carrying inputs must still fully nest.
        // Unnest → Refactor hoists A1/B1 into value bindings, so the LET now has
        // inputs. Re-opening /Unnest and inlining every step must collapse the
        // inputs too, yielding a bare formula (no LET) that Excel accepts and
        // computes to the original value — the true round-trip.
        var ws = _excel.AddWorksheet();
        var sheetName = (string)ws.Name;
        try
        {
            SeedLeaves(ws);

            var original = ws.Range["D1"];
            var reNested = ws.Range["D2"];
            try
            {
                original.Formula2 = NestedFormula;
                _excel.Application.Calculate();
                var expected = Convert.ToDouble(original.Value);

                var unnested = UnnestEngine.Unnest(NestedFormula).SynthesisedLet;
                var refactored = RefactorEngine.Refactor(unnested, sheetName).SynthesisedLet;
                _output.WriteLine($"Refactored (has inputs):\n{refactored}");
                // Refactor must have produced at least one value-binding input.
                Assert.Contains(LetParser.Parse(refactored).Bindings, b => !b.IsCalculation);

                // Re-open /Unnest and inline every step.
                var reopened = UnnestEngine.Unnest(refactored);
                Assert.NotEmpty(reopened.Steps);
                var inlineAll = reopened.Steps
                    .Select(s => new UnnestRowState(s.Key, s.Name, Include: false))
                    .ToList();
                var collapsed = UnnestEngine.Recompute(refactored, inlineAll).SynthesisedLet;
                _output.WriteLine($"Re-nested (no LET):\n{collapsed}");

                // No residual LET — the inputs were inlined back to their leaves.
                Assert.False(LetParser.IsLetFormula(collapsed));

                reNested.Formula2 = collapsed;
                _excel.Application.Calculate();
                var actual = Convert.ToDouble(reNested.Value);

                _output.WriteLine($"Original = {expected}, re-nested = {actual}");
                Assert.Equal(expected, actual);
            }
            finally
            {
                Marshal.ReleaseComObject(original);
                Marshal.ReleaseComObject(reNested);
            }
        }
        finally
        {
            CleanupSheet(ws);
        }
    }

    [Fact]
    public void EmptyOrLiteralCell_HasNoFormula_SoCommandNoOps()
    {
        var ws = _excel.AddWorksheet();
        try
        {
            var empty = ws.Range["A1"];
            var literal = ws.Range["A2"];
            try
            {
                // The /Unnest command opens the dialog only when the active
                // cell HasFormula; an empty or literal cell makes it return
                // silently (no dialog, no error).
                Assert.False((bool)empty.HasFormula);

                literal.Value = 42;
                Assert.False((bool)literal.HasFormula);
            }
            finally
            {
                Marshal.ReleaseComObject(empty);
                Marshal.ReleaseComObject(literal);
            }
        }
        finally
        {
            CleanupSheet(ws);
        }
    }

    private static void SeedLeaves(dynamic ws)
    {
        var a1 = ws.Range["A1"];
        var b1 = ws.Range["B1"];
        try
        {
            a1.Value = 3;
            b1.Value = 0;
        }
        finally
        {
            Marshal.ReleaseComObject(a1);
            Marshal.ReleaseComObject(b1);
        }
    }

    private void TryDeleteName(string name)
    {
        try
        {
            var n = _excel.Workbook.Names.Item(name);
            n.Delete();
            Marshal.ReleaseComObject(n);
        }
        catch
        {
            // Name was never added or already gone — ignore.
        }
    }

    private static void CleanupSheet(dynamic ws)
    {
        try
        {
            ws.Delete();
            Marshal.ReleaseComObject(ws);
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }
}
