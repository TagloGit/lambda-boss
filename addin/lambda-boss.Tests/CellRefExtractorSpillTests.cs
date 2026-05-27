using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Spec 0008 / PR 1 — focused tests for the new
///     <see cref="FormulaRef.IsSpilled" /> flag and its propagation
///     through <see cref="CellRefExtractor" />. Complements the broader
///     <see cref="CellRefExtractorTests" /> spill coverage.
/// </summary>
public class CellRefExtractorSpillTests
{
    private const string Sheet = "Sheet1";

    [Fact]
    public void Extract_SpillRef_IsSpilledTrue()
    {
        var refs = CellRefExtractor.Extract("=SUM(A1#)", Sheet);

        Assert.Single(refs);
        Assert.True(refs[0].IsSpilled);
        Assert.Equal("A1", refs[0].A1Address);
    }

    [Fact]
    public void Extract_PlainRef_IsSpilledFalse()
    {
        var refs = CellRefExtractor.Extract("=A1+B1", Sheet);

        Assert.Equal(2, refs.Count);
        Assert.All(refs, r => Assert.False(r.IsSpilled));
    }

    [Fact]
    public void Extract_Range_IsSpilledFalse()
    {
        // Excel has no A1:B5# syntax; the regex's range alternative is
        // mutually exclusive with the spill alternative.
        var refs = CellRefExtractor.Extract("=SUM(A1:B5)", Sheet);

        Assert.Single(refs);
        Assert.True(refs[0].IsRange);
        Assert.False(refs[0].IsSpilled);
    }

    [Fact]
    public void FormulaRef_Equality_DistinguishesSpilled()
    {
        var anchor = new FormulaRef(new CellRef(Sheet, 1, 1));
        var spilled = new FormulaRef(new CellRef(Sheet, 1, 1), IsSpilled: true);

        Assert.NotEqual(anchor, spilled);
        Assert.NotEqual(anchor.GetHashCode(), spilled.GetHashCode());
    }

    [Fact]
    public void FormulaRef_DisplayAddress_AppendsHashForSpilled()
    {
        var spilled = new FormulaRef(new CellRef(Sheet, 1, 1), IsSpilled: true);

        Assert.Equal("A1#", spilled.DisplayAddress(Sheet));
    }

    [Fact]
    public void FormulaRef_DisplayAddress_CrossSheetSpilled_QualifiesAndAppendsHash()
    {
        var spilled = new FormulaRef(new CellRef("Other", 1, 1), IsSpilled: true);

        Assert.Equal("Other!A1#", spilled.DisplayAddress(Sheet));
    }

    [Fact]
    public void Rewrite_SpilledLookupWins_OverNonSpilled()
    {
        // Both keys present — A1 maps to "scalar", A1# maps to "array".
        // Tokens rewrite to their own binding name.
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef(Sheet, 1, 1))] = "scalar",
            [new FormulaRef(new CellRef(Sheet, 1, 1), IsSpilled: true)] = "array"
        };

        var rewritten = CellRefExtractor.Rewrite("=A1+SUM(A1#)", Sheet, lookup);

        Assert.Equal("=scalar+SUM(array)", rewritten);
    }

    [Fact]
    public void Rewrite_OnlyNonSpilledKey_StillCollapsesSpillToken()
    {
        // /Gather's PR 5 behaviour: A1# falls back to the non-spilled
        // binding name. No trailing '#' in the rewrite — the binding IS
        // the array.
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef(Sheet, 1, 1))] = "numbers"
        };

        var rewritten = CellRefExtractor.Rewrite("=SUM(A1#)", Sheet, lookup);

        Assert.Equal("=SUM(numbers)", rewritten);
    }
}
