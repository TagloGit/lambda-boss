using ExcelDna.IntelliSense;

using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     The argument hints' metadata (spec 0013): header parsing, the catalogue of names, and the
///     XML ExcelDna.IntelliSense reads.
/// </summary>
public class LambdaHintTests
{
    private const string Tlookup = @"/*  FUNCTION NAME:      TLOOKUP
    DESCRIPTION:*//**Looks up a value from a header-row table.*/
/*  RETURNS:            the value under HeaderName for each Item
    PARAMETERS:
        Table           (Required) Full table including its header row (e.g. tblData[#All]).
        Item            (Optional) Value to find in LookupCol.
        HeaderName      (Optional) Header text of the column to return.
    EXAMPLE:            =TLOOKUP(tblData[#All], ""Tim"", ""Age"") -> 42
    NOTE:               Row mode when HeaderName is omitted.
    REVISIONS:          Date        Developer   Description
                        2026-05-07  Claude      Initial version
*/
TLOOKUP = LAMBDA(
//  Parameter Declarations
    [Table],
    [Item],
    [HeaderName],
    LET(
        result, INDEX(Table, MATCH(Item, TAKE(Table, , 1), 0), MATCH(HeaderName, TAKE(Table, 1), 0)),
        result
    )
);";

    [Fact]
    public void Parse_ReadsSummaryReturnsAndParameters()
    {
        var doc = LambdaDocParser.Parse(Tlookup);

        Assert.Equal("TLOOKUP", doc.Name);
        Assert.Equal("Looks up a value from a header-row table.", doc.Summary);
        Assert.Equal("the value under HeaderName for each Item", doc.Returns);
        Assert.Equal("=TLOOKUP(tblData[#All], \"Tim\", \"Age\") -> 42", doc.Example);
        Assert.Equal(new[] { "Table", "Item", "HeaderName" }, doc.Parameters.Select(p => p.Name));
        Assert.Equal(new[] { true, false, false }, doc.Parameters.Select(p => p.Required));
        Assert.Equal("Full table including its header row (e.g. tblData[#All]).", doc.Parameters[0].Description);
    }

    [Fact]
    public void Parse_UndocumentedParameter_IsOptionalWithNoText()
    {
        var content = Tlookup.Replace("        Item            (Optional) Value to find in LookupCol.\n", "");
        var doc = LambdaDocParser.Parse(content);

        Assert.Equal(new[] { "Table", "Item", "HeaderName" }, doc.Parameters.Select(p => p.Name));
        Assert.Equal("", doc.Parameters[1].Description);
        Assert.Equal(new[] { "Table", "HeaderName" }, LambdaDocParser.DocumentedParameterNames(content));
    }

    [Fact]
    public void Parse_NoMetadata_StillParses()
    {
        var doc = LambdaDocParser.Parse("Double = LAMBDA(x, x * 2);");

        Assert.Equal("Double", doc.Name);
        Assert.Equal("", doc.Returns);
        Assert.Single(doc.Parameters);
    }

    [Fact]
    public void Parse_FormulaIgnoresMetadata()
    {
        // The header is a comment: the injected formula must not change.
        var (_, formula) = LambdaParser.Parse(Tlookup);

        Assert.DoesNotContain("RETURNS", formula);
        Assert.DoesNotContain("Full table", formula);
    }

    [Fact]
    public void FunctionInfo_BracketsOptionalArgumentsAndPutsReturnsFirst()
    {
        var info = LambdaHintCatalog.ToFunctionInfo("TLOOKUP", LambdaDocParser.Parse(Tlookup));

        Assert.Equal("TLOOKUP", info.Name);
        Assert.Equal(new[] { "Table", "[Item]", "[HeaderName]" }, info.ArgumentList.Select(a => a.Name));
        Assert.Equal(
            "Returns: the value under HeaderName for each Item\nLooks up a value from a header-row table.",
            info.Description);
    }

    [Fact]
    public void Catalog_KnowsBareAndDefaultPrefixedNames_AndLoadedPrefixes()
    {
        var catalog = new LambdaHintCatalog();
        var library = new LibraryInfo { DefaultPrefix = "lookup", FolderName = "lookup" };
        catalog.AddLibraryLambdas(new[]
        {
            new LambdaInfo { Name = "TLOOKUP", LibraryInfo = library, Doc = LambdaDocParser.Parse(Tlookup) },
            new LambdaInfo { Name = "_k", LibraryInfo = library, IsConstant = true }
        });

        Assert.Equal(new[] { "lookup.TLOOKUP", "TLOOKUP" }, catalog.BuildFunctionInfos().Select(f => f.Name));

        Assert.True(catalog.AddLoadedNames("lk", new[] { "lk.TLOOKUP", "lk.UNKNOWN" }));
        Assert.False(catalog.AddLoadedNames("lk", new[] { "lk.TLOOKUP" }));
        Assert.False(catalog.AddLoadedNames("", new[] { "TLOOKUP" }));

        Assert.Equal(
            new[] { "lk.TLOOKUP", "lookup.TLOOKUP", "TLOOKUP" },
            catalog.BuildFunctionInfos().Select(f => f.Name));
    }

    [Fact]
    public void Xml_RoundTripsThroughExcelDnaIntelliSense()
    {
        var catalog = new LambdaHintCatalog();
        var zeroArg = LambdaDocParser.Parse(@"/*  FUNCTION NAME:      ALPHABET
    DESCRIPTION:*//**The 26 capital letters.*/
/*  RETURNS:            text array: A to Z in a column
    PARAMETERS:
    REVISIONS:
*/
ALPHABET = LAMBDA(CHAR(SEQUENCE(26, , 65)));");
        catalog.AddLibraryLambdas(new[]
        {
            new LambdaInfo { Name = "TLOOKUP", LibraryInfo = new LibraryInfo(), Doc = LambdaDocParser.Parse(Tlookup) },
            new LambdaInfo { Name = "ALPHABET", LibraryInfo = new LibraryInfo(), Doc = zeroArg }
        });

        // Parsed with the same code the IntelliSense server uses to read the file.
        var parsed = XmlIntelliSense.Parse(catalog.BuildXml(), "test");
        var functions = parsed.XmlFunctionInfo.FunctionsList;

        Assert.Equal(new[] { "ALPHABET", "TLOOKUP" }, functions.Select(f => f.Name));
        var tlookup = functions[1];
        Assert.Equal(new[] { "Table", "[Item]", "[HeaderName]" }, tlookup.ArgumentList.Select(a => a.Name));
        Assert.Equal("Value to find in LookupCol.", tlookup.ArgumentList[1].Description);
        Assert.Contains("Returns: ", tlookup.Description);

        // The server reads ArgumentList.Count for every function, so it must never be null.
        Assert.NotNull(functions[0].ArgumentList);
        Assert.Empty(functions[0].ArgumentList);
    }
}
