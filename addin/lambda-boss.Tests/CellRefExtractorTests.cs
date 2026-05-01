using Xunit;

namespace LambdaBoss.Tests;

public class CellRefExtractorTests
{
    private const string Sheet = "Sheet1";

    [Fact]
    public void Extract_SingleCellRef_ReturnsIt()
    {
        var refs = CellRefExtractor.Extract("=A1*2", Sheet);

        Assert.Single(refs);
        Assert.Equal("A1", refs[0].A1Address);
        Assert.Equal(Sheet, refs[0].Sheet);
    }

    [Fact]
    public void Extract_MultipleRefs_PreservesFirstOccurrenceOrder()
    {
        var refs = CellRefExtractor.Extract("=B1+A1+B1", Sheet);

        Assert.Equal(2, refs.Count);
        Assert.Equal("B1", refs[0].A1Address);
        Assert.Equal("A1", refs[1].A1Address);
    }

    [Fact]
    public void Extract_DollarAnchors_AreStripped()
    {
        var refs = CellRefExtractor.Extract("=$A$1+$B2+C$3", Sheet);

        Assert.Equal(3, refs.Count);
        Assert.Equal(new[] { "A1", "B2", "C3" }, refs.Select(r => r.A1Address));
    }

    [Fact]
    public void Extract_FunctionLikeIdentifier_NotMistakenForRef()
    {
        // LOG10( looks like cell-ref shape (letters+digits) but is followed
        // by '(', which the lookahead must reject.
        var refs = CellRefExtractor.Extract("=LOG10(A1)", Sheet);

        Assert.Single(refs);
        Assert.Equal("A1", refs[0].A1Address);
    }

    [Fact]
    public void Extract_RangeRef_ExcludesBothEndpoints()
    {
        // PR 1 doesn't promote ranges; we just need to NOT pick up A1 or
        // A3 as standalone cells while a range is being parsed elsewhere.
        var refs = CellRefExtractor.Extract("=SUM(A1:A3)", Sheet);

        Assert.Empty(refs);
    }

    [Fact]
    public void Extract_SheetQualifiedRef_IsExcluded()
    {
        // Cross-sheet support lands in PR 3; for now sheet-qualified refs
        // should be skipped so they don't get walked as if same-sheet.
        var refs = CellRefExtractor.Extract("=Sheet1!A1+B1", Sheet);

        Assert.Single(refs);
        Assert.Equal("B1", refs[0].A1Address);
    }

    [Fact]
    public void Extract_SpillRef_IsExcluded()
    {
        var refs = CellRefExtractor.Extract("=SUM(A1#)+B1", Sheet);

        Assert.Single(refs);
        Assert.Equal("B1", refs[0].A1Address);
    }

    [Fact]
    public void Extract_StringLiteralLooksLikeRef_IsSkipped()
    {
        var refs = CellRefExtractor.Extract("=\"A1 is a string\"&B1", Sheet);

        Assert.Single(refs);
        Assert.Equal("B1", refs[0].A1Address);
    }

    [Fact]
    public void Extract_EmptyOrNonFormula_ReturnsEmpty()
    {
        Assert.Empty(CellRefExtractor.Extract("", Sheet));
        Assert.Empty(CellRefExtractor.Extract("=1+2", Sheet));
    }

    [Fact]
    public void Rewrite_ReplacesInScopeRefsWithBindingNames()
    {
        var lookup = new Dictionary<CellRef, string>
        {
            [new CellRef(Sheet, 1, 1)] = "numbers",
            [new CellRef(Sheet, 2, 1)] = "step_1",
        };

        var rewritten = CellRefExtractor.Rewrite("=A1*2+B1", Sheet, lookup);

        Assert.Equal("=numbers*2+step_1", rewritten);
    }

    [Fact]
    public void Rewrite_LeavesOutOfScopeRefsAlone()
    {
        var lookup = new Dictionary<CellRef, string>
        {
            [new CellRef(Sheet, 1, 1)] = "numbers",
        };

        var rewritten = CellRefExtractor.Rewrite("=A1*Z99", Sheet, lookup);

        Assert.Equal("=numbers*Z99", rewritten);
    }

    [Fact]
    public void Rewrite_PreservesStringLiterals()
    {
        var lookup = new Dictionary<CellRef, string>
        {
            [new CellRef(Sheet, 1, 1)] = "numbers",
        };

        var rewritten = CellRefExtractor.Rewrite("=A1&\"A1 unchanged\"", Sheet, lookup);

        Assert.Equal("=numbers&\"A1 unchanged\"", rewritten);
    }

    [Fact]
    public void CellRef_ColumnLetters_RoundTripsViaLettersToColumn()
    {
        foreach (var col in new[] { 1, 26, 27, 52, 702, 703, 16384 })
        {
            var letters = CellRef.ColumnLetters(col);
            Assert.Equal(col, CellRef.LettersToColumn(letters));
        }
    }
}
