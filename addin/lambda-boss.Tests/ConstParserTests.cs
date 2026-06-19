using Xunit;

namespace LambdaBoss.Tests;

public class ConstParserTests
{
    [Fact]
    public void Parse_ScalarConstant_ExtractsNameAndFormula()
    {
        var content = "PI = 3.14159;";
        var (name, formula) = ConstParser.Parse(content);

        Assert.Equal("PI", name);
        Assert.Equal("=3.14159", formula);
    }

    [Fact]
    public void Parse_ArrayConstant_PreservesInternalSemicolons()
    {
        // Excel array-row separators (;) inside the literal must not be mistaken for the
        // statement terminator.
        var content = "DEFAULTARROWS = {\"↑\";\"↓\";\"←\";\"→\"};";
        var (name, formula) = ConstParser.Parse(content);

        Assert.Equal("DEFAULTARROWS", name);
        Assert.Equal("={\"↑\";\"↓\";\"←\";\"→\"}", formula);
    }

    [Fact]
    public void Parse_FormulaHasEqualsPrefix()
    {
        var content = "ANSWER = 42;";
        var (_, formula) = ConstParser.Parse(content);

        Assert.StartsWith("=", formula);
    }

    [Fact]
    public void Parse_WithBlockCommentHeader_StripsAndParses()
    {
        var content = @"/*  CONSTANT NAME:      DEFAULTARROWS
    DESCRIPTION:*//**The 8 default compass arrow characters.*/
/*  REVISIONS:          Date        Developer   Description
                        2026-06-19  Claude      Initial version
*/
DEFAULTARROWS = {""↑"";""↓"";""←"";""→"";""↖"";""↗"";""↙"";""↘""};";

        var (name, formula) = ConstParser.Parse(content);

        Assert.Equal("DEFAULTARROWS", name);
        Assert.Equal("={\"↑\";\"↓\";\"←\";\"→\";\"↖\";\"↗\";\"↙\";\"↘\"}", formula);
    }

    [Fact]
    public void Parse_MultiLineArray_CollapsesToSingleLine()
    {
        var content = @"GRID = {1,2,3;
                       4,5,6;
                       7,8,9};";

        var (name, formula) = ConstParser.Parse(content);

        Assert.Equal("GRID", name);
        Assert.Equal("={1,2,3; 4,5,6; 7,8,9}", formula);
    }

    [Fact]
    public void Parse_WhitespaceAroundEquals_Works()
    {
        var content = "  FOO   =   {1;2;3} ;";
        var (name, formula) = ConstParser.Parse(content);

        Assert.Equal("FOO", name);
        Assert.Equal("={1;2;3}", formula);
    }

    [Fact]
    public void Parse_LambdaRhs_ThrowsFormatException()
    {
        var content = "NotAConst = LAMBDA(x, x * 2);";
        Assert.Throws<FormatException>(() => ConstParser.Parse(content));
    }

    [Fact]
    public void Parse_LambdaRhs_CaseInsensitive_ThrowsFormatException()
    {
        var content = "NotAConst = lambda(x, x);";
        Assert.Throws<FormatException>(() => ConstParser.Parse(content));
    }

    [Fact]
    public void Parse_NoSemicolon_ThrowsFormatException()
    {
        var content = "Broken = {1;2;3}";
        Assert.Throws<FormatException>(() => ConstParser.Parse(content));
    }

    [Fact]
    public void Parse_NotAnAssignment_ThrowsFormatException()
    {
        var content = "this is not a constant file";
        Assert.Throws<FormatException>(() => ConstParser.Parse(content));
    }

    [Fact]
    public void Parse_DescriptionExtractedViaSharedConvention()
    {
        var content = @"/*  CONSTANT NAME:      DEFAULTARROWS
    DESCRIPTION:*//**The 8 default compass arrow characters.*/
DEFAULTARROWS = {""↑"";""↓""};";

        // ExtractDescription is shared with .lambda parsing.
        var description = LambdaParser.ExtractDescription(content);

        Assert.Equal("The 8 default compass arrow characters.", description);
    }
}
