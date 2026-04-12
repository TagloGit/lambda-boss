using Xunit;

namespace LambdaBoss.Tests;

public class LambdaParserTests
{
    [Fact]
    public void Parse_SimpleFormula_ExtractsNameAndFormula()
    {
        var content = "Double = LAMBDA(x, x * 2);";
        var (name, formula) = LambdaParser.Parse(content);

        Assert.Equal("Double", name);
        Assert.StartsWith("=LAMBDA(", formula);
        Assert.Contains("x * 2", formula);
    }

    [Fact]
    public void Parse_WithBlockComments_StripsCommentsAndParses()
    {
        var content = @"/*  FUNCTION NAME:      Double
    DESCRIPTION:*//**Doubles a number.*/
Double = LAMBDA(x, x * 2);";

        var (name, formula) = LambdaParser.Parse(content);

        Assert.Equal("Double", name);
        Assert.StartsWith("=LAMBDA(", formula);
    }

    [Fact]
    public void Parse_WithLineComments_StripsCommentsAndParses()
    {
        var content = @"MyFunc = LAMBDA(
// Parameter Declarations
    x,
// Procedure
    x * 2
);";

        var (name, formula) = LambdaParser.Parse(content);

        Assert.Equal("MyFunc", name);
        Assert.StartsWith("=LAMBDA(", formula);
        Assert.Contains("x * 2", formula);
    }

    [Fact]
    public void Parse_FullFormatFile_ExtractsCorrectly()
    {
        var content = @"/*  FUNCTION NAME:      AddN
    DESCRIPTION:*//**Adds N to a number.*/
/*  REVISIONS:          Date        Developer   Description
                        2026-04-09  Tim Jacks   Initial version
*/
AddN = LAMBDA(
//  Parameter Declarations
    [x],
    [n],
//  Help
    LET(Help, TEXTSPLIT(
                ""FUNCTION:      →AddN(x, n)¶"" &
                ""DESCRIPTION:   →Adds N to a number.¶"",
                ""→"", ""¶""
                ),
    //  Check inputs
        Help?, ISOMITTED(x),
    //  Procedure
        result, x + n,
        IF(Help?, Help, result)
)
);";

        var (name, formula) = LambdaParser.Parse(content);

        Assert.Equal("AddN", name);
        Assert.StartsWith("=LAMBDA(", formula);
        Assert.Contains("LET(Help", formula);
        Assert.Contains("ISOMITTED(x)", formula);
        Assert.Contains("[x]", formula);
        Assert.Contains("[n]", formula);
        Assert.EndsWith(")", formula);
    }

    [Fact]
    public void Parse_HelpPattern_PreservesIsomitted()
    {
        var content = @"Double = LAMBDA(
    [x],
    LET(Help, TEXTSPLIT(""help text"", ""→"", ""¶""),
        Help?, ISOMITTED(x),
        result, x * 2,
        IF(Help?, Help, result)
)
);";

        var (_, formula) = LambdaParser.Parse(content);

        Assert.Contains("ISOMITTED(x)", formula);
        Assert.Contains("[x]", formula);
    }

    [Fact]
    public void Parse_HelpPattern_PreservesOptionalParams()
    {
        var content = @"AddN = LAMBDA(
    [x],
    [n],
    LET(Help, TEXTSPLIT(""help"", ""→"", ""¶""),
        Help?, ISOMITTED(x),
        result, x + n,
        IF(Help?, Help, result)
)
);";

        var (_, formula) = LambdaParser.Parse(content);

        Assert.Contains("[x]", formula);
        Assert.Contains("[n]", formula);
        Assert.Contains("ISOMITTED(x)", formula);
    }

    [Fact]
    public void Parse_FormulaHasEqualsPrefix()
    {
        var content = "Foo = LAMBDA(x, x + 1);";
        var (_, formula) = LambdaParser.Parse(content);

        Assert.StartsWith("=", formula);
    }

    [Fact]
    public void Parse_NestedParentheses_BalancesCorrectly()
    {
        var content = "Nested = LAMBDA(x, IF(x > 0, SUM(x, 1), 0));";
        var (name, formula) = LambdaParser.Parse(content);

        Assert.Equal("Nested", name);
        Assert.Contains("IF(x > 0, SUM(x, 1), 0)", formula);
    }

    [Fact]
    public void Parse_InvalidContent_ThrowsFormatException()
    {
        var content = "This is not a valid lambda file";
        Assert.Throws<FormatException>(() => LambdaParser.Parse(content));
    }

    [Fact]
    public void Parse_StringLiteralsWithQuotes_PreservesContent()
    {
        var content = @"Greeter = LAMBDA(name, ""Hello "" & name & ""!"");";
        var (name, formula) = LambdaParser.Parse(content);

        Assert.Equal("Greeter", name);
        Assert.StartsWith("=LAMBDA(", formula);
    }

    [Fact]
    public void Parse_WhitespaceAroundEquals_Works()
    {
        var content = "  MyFunc  =  LAMBDA( x , x * 2 ) ;";
        var (name, _) = LambdaParser.Parse(content);

        Assert.Equal("MyFunc", name);
    }
}
