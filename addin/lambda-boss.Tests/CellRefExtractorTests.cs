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
    public void Extract_RangeRef_ReturnedAsSingleRangeFormulaRef()
    {
        // PR 4 promotes ranges to first-class refs. A1:A3 surfaces as one
        // FormulaRef (Start=A1, End=A3), not two cells.
        var refs = CellRefExtractor.Extract("=SUM(A1:A3)", Sheet);

        Assert.Single(refs);
        Assert.True(refs[0].IsRange);
        Assert.Equal("A1", refs[0].Start.A1Address);
        Assert.Equal("A3", refs[0].End!.A1Address);
        Assert.Equal(Sheet, refs[0].Start.Sheet);
        Assert.Equal(Sheet, refs[0].End.Sheet);
    }

    [Fact]
    public void Extract_RangeRefWithDollarAnchors_StripsAnchors()
    {
        var refs = CellRefExtractor.Extract("=SUM($A$1:$A$3)", Sheet);

        Assert.Single(refs);
        Assert.True(refs[0].IsRange);
        Assert.Equal("A1", refs[0].Start.A1Address);
        Assert.Equal("A3", refs[0].End!.A1Address);
    }

    [Fact]
    public void Extract_MultiRowRange_BothEndpointsCaptured()
    {
        // Two-dimensional range: the End cell carries its own column AND
        // row — the engine uses both to test cell coverage.
        var refs = CellRefExtractor.Extract("=SUM(A1:C3)", Sheet);

        Assert.Single(refs);
        Assert.True(refs[0].IsRange);
        Assert.Equal("A1", refs[0].Start.A1Address);
        Assert.Equal("C3", refs[0].End!.A1Address);
    }

    [Fact]
    public void Extract_CrossSheetRange_BothEndpointsOnQualifiedSheet()
    {
        var refs = CellRefExtractor.Extract("=SUM(Sheet1!A1:A3)", "Sheet2");

        Assert.Single(refs);
        Assert.True(refs[0].IsRange);
        Assert.Equal("Sheet1", refs[0].Start.Sheet);
        Assert.Equal("Sheet1", refs[0].End!.Sheet);
        Assert.Equal("A1", refs[0].Start.A1Address);
        Assert.Equal("A3", refs[0].End.A1Address);
    }

    [Fact]
    public void Extract_QuotedSheetRange_StripsQuotesOnBothEndpoints()
    {
        var refs = CellRefExtractor.Extract("=SUM('My Sheet'!A1:A3)", "Sheet2");

        Assert.Single(refs);
        Assert.True(refs[0].IsRange);
        Assert.Equal("My Sheet", refs[0].Start.Sheet);
        Assert.Equal("My Sheet", refs[0].End!.Sheet);
    }

    [Fact]
    public void Extract_RangeAndIndividualCell_BothReturnedSeparately()
    {
        // The PR 4 acceptance scenario: =SUM(A1:A3) + A4 must yield two
        // distinct refs — the range and the individual cell — so the
        // engine can promote the range while still walking A4.
        var refs = CellRefExtractor.Extract("=SUM(A1:A3) + A4", Sheet);

        Assert.Equal(2, refs.Count);
        Assert.True(refs[0].IsRange);
        Assert.Equal("A1:A3", refs[0].A1Address);
        Assert.False(refs[1].IsRange);
        Assert.Equal("A4", refs[1].Start.A1Address);
    }

    [Fact]
    public void Extract_SheetQualifiedRef_KeepsSheetName()
    {
        // PR 3 widens to cross-sheet — Sheet1!A1 surfaces as a CellRef on
        // Sheet1, while the bare B1 stays on the default sheet.
        var refs = CellRefExtractor.Extract("=Sheet1!A1+B1", "Sheet2");

        Assert.Equal(2, refs.Count);
        Assert.Equal("Sheet1", refs[0].Sheet);
        Assert.Equal("A1", refs[0].A1Address);
        Assert.False(refs[0].IsExternal);
        Assert.Equal("Sheet2", refs[1].Sheet);
        Assert.Equal("B1", refs[1].A1Address);
    }

    [Fact]
    public void Extract_QuotedSheetRef_StripsQuotesAndUnescapes()
    {
        var refs = CellRefExtractor.Extract("='My Sheet'!A1+'It''s Mine'!B2", Sheet);

        Assert.Equal(2, refs.Count);
        Assert.Equal("My Sheet", refs[0].Sheet);
        Assert.Equal("A1", refs[0].A1Address);
        Assert.Equal("It's Mine", refs[1].Sheet);
        Assert.Equal("B2", refs[1].A1Address);
    }

    [Fact]
    public void Extract_ExternalWorkbookRef_KeepsWorkbookTag()
    {
        var refs = CellRefExtractor.Extract("=[Other.xlsx]Sheet1!A1+B1", "Sheet2");

        Assert.Equal(2, refs.Count);
        Assert.True(refs[0].IsExternal);
        Assert.Equal("Other.xlsx", refs[0].ExternalWorkbook);
        Assert.Equal("Sheet1", refs[0].Sheet);
        Assert.Equal("A1", refs[0].A1Address);
        Assert.False(refs[1].IsExternal);
        Assert.Equal("Sheet2", refs[1].Sheet);
    }

    [Fact]
    public void Extract_QuotedExternalRef_HandlesSpacesInSheetName()
    {
        var refs = CellRefExtractor.Extract("='[Other.xlsx]My Sheet'!A1", Sheet);

        Assert.Single(refs);
        Assert.True(refs[0].IsExternal);
        Assert.Equal("Other.xlsx", refs[0].ExternalWorkbook);
        Assert.Equal("My Sheet", refs[0].Sheet);
        Assert.Equal("A1", refs[0].A1Address);
    }

    [Fact]
    public void Extract_QuotedExternalRefWithPath_StillCapturesWorkbook()
    {
        // Closed-workbook form Excel writes when the source file isn't open.
        var refs = CellRefExtractor.Extract(@"='C:\path\to\[Other.xlsx]Sheet1'!A1", Sheet);

        Assert.Single(refs);
        Assert.True(refs[0].IsExternal);
        Assert.Equal("Other.xlsx", refs[0].ExternalWorkbook);
        Assert.Equal("Sheet1", refs[0].Sheet);
    }

    [Fact]
    public void Extract_DollarAnchorsOnSheetQualifiedRef_AreStripped()
    {
        var refs = CellRefExtractor.Extract("=Sheet1!$A$1", "Sheet2");

        Assert.Single(refs);
        Assert.Equal("Sheet1", refs[0].Sheet);
        Assert.Equal("A1", refs[0].A1Address);
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
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef(Sheet, 1, 1))] = "numbers",
            [new FormulaRef(new CellRef(Sheet, 2, 1))] = "step_1",
        };

        var rewritten = CellRefExtractor.Rewrite("=A1*2+B1", Sheet, lookup);

        Assert.Equal("=numbers*2+step_1", rewritten);
    }

    [Fact]
    public void Rewrite_LeavesOutOfScopeRefsAlone()
    {
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef(Sheet, 1, 1))] = "numbers",
        };

        var rewritten = CellRefExtractor.Rewrite("=A1*Z99", Sheet, lookup);

        Assert.Equal("=numbers*Z99", rewritten);
    }

    [Fact]
    public void Rewrite_PreservesStringLiterals()
    {
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef(Sheet, 1, 1))] = "numbers",
        };

        var rewritten = CellRefExtractor.Rewrite("=A1&\"A1 unchanged\"", Sheet, lookup);

        Assert.Equal("=numbers&\"A1 unchanged\"", rewritten);
    }

    [Fact]
    public void Rewrite_SheetQualifiedRef_ReplacesWholeQualifier()
    {
        // The whole `Sheet1!A1` collapses to the binding name, not just A1.
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef("Sheet1", 1, 1))] = "shared",
        };

        var rewritten = CellRefExtractor.Rewrite("=Sheet1!A1*2", "Sheet2", lookup);

        Assert.Equal("=shared*2", rewritten);
    }

    [Fact]
    public void Rewrite_QuotedSheetRef_ReplacesWholeQualifier()
    {
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef("My Sheet", 1, 1))] = "mine",
        };

        var rewritten = CellRefExtractor.Rewrite("='My Sheet'!A1+1", "Sheet2", lookup);

        Assert.Equal("=mine+1", rewritten);
    }

    [Fact]
    public void Rewrite_ExternalRef_ReplacesWholeQualifier()
    {
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef("Sheet1", 1, 1, "Other.xlsx"))] = "outside",
        };

        var rewritten = CellRefExtractor.Rewrite("=[Other.xlsx]Sheet1!A1*2", "Sheet2", lookup);

        Assert.Equal("=outside*2", rewritten);
    }

    [Fact]
    public void Rewrite_BareRefAndCrossSheetRef_BothMatchedSeparately()
    {
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef("Sheet2", 2, 1))] = "local",
            [new FormulaRef(new CellRef("Sheet1", 1, 1))] = "shared",
        };

        var rewritten = CellRefExtractor.Rewrite("=B1+Sheet1!A1", "Sheet2", lookup);

        Assert.Equal("=local+shared", rewritten);
    }

    [Fact]
    public void Rewrite_RangeRef_ReplacedByRangeBindingName()
    {
        // PR 4: a range key in the lookup collapses A1:A3 to one binding
        // name in the rewritten formula.
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef(Sheet, 1, 1), new CellRef(Sheet, 1, 3))] = "values",
        };

        var rewritten = CellRefExtractor.Rewrite("=SUM(A1:A3)", Sheet, lookup);

        Assert.Equal("=SUM(values)", rewritten);
    }

    [Fact]
    public void Rewrite_RangeAndCellBindings_BothApplied()
    {
        // Range + standalone cell ref in the same formula. The range
        // binding takes the whole `A1:A3` token; the standalone `A4`
        // collapses separately.
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef(Sheet, 1, 1), new CellRef(Sheet, 1, 3))] = "values",
            [new FormulaRef(new CellRef(Sheet, 1, 4))] = "extra",
        };

        var rewritten = CellRefExtractor.Rewrite("=SUM(A1:A3)+A4", Sheet, lookup);

        Assert.Equal("=SUM(values)+extra", rewritten);
    }

    [Fact]
    public void Rewrite_CrossSheetRange_ReplacesWholeQualifier()
    {
        var lookup = new Dictionary<FormulaRef, string>
        {
            [new FormulaRef(new CellRef("Sheet1", 1, 1), new CellRef("Sheet1", 1, 3))] = "values",
        };

        var rewritten = CellRefExtractor.Rewrite("=SUM(Sheet1!A1:A3)", "Sheet2", lookup);

        Assert.Equal("=SUM(values)", rewritten);
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
