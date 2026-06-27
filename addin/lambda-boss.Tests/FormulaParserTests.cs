using Xunit;

namespace LambdaBoss.Tests;

public class FormulaParserTests
{
    private static FormulaNode Root(string formula) => FormulaParser.Parse(formula).Root;

    private static void AssertRoundTrips(string formula) =>
        Assert.Equal(formula, FormulaParser.Parse(formula).ToFormula());

    // ----------------------------------------------------------- Round-trip

    [Theory]
    // Literals
    [InlineData("=1")]
    [InlineData("=100")]
    [InlineData("=.5")]
    [InlineData("=1.5E-3")]
    [InlineData("=1E3")]
    [InlineData("=-2.5")]
    [InlineData("=TRUE")]
    [InlineData("=FALSE")]
    [InlineData("=\"hello\"")]
    [InlineData("=\"he said \"\"hi\"\"\"")]
    [InlineData("=\"a, b; c\"")]
    // Error literals
    [InlineData("=#N/A")]
    [InlineData("=#REF!")]
    [InlineData("=#DIV/0!")]
    [InlineData("=#VALUE!")]
    [InlineData("=#NAME?")]
    [InlineData("=#NULL!")]
    [InlineData("=#NUM!")]
    [InlineData("=#SPILL!")]
    [InlineData("=#CALC!")]
    // Array constants
    [InlineData("={1,2,3}")]
    [InlineData("={1,2;3,4}")]
    [InlineData("={1,\"a\",TRUE;2,#N/A,FALSE}")]
    [InlineData("={\"↑\";\"↓\";\"←\";\"→\"}")]
    // Cell refs
    [InlineData("=A1")]
    [InlineData("=$A$1")]
    [InlineData("=$A1")]
    [InlineData("=A$1")]
    [InlineData("=Sheet1!A1")]
    [InlineData("='My Sheet'!A1")]
    [InlineData("=Sheet1:Sheet3!A1")]
    [InlineData("=[Book.xlsx]Sheet1!A1")]
    [InlineData("='[Book.xlsx]My Sheet'!A1")]
    [InlineData("=A:A")]
    [InlineData("=1:1")]
    [InlineData("=A1#")]
    [InlineData("=$A$1:$B$10")]
    // Structured table refs
    [InlineData("=t[City]")]
    [InlineData("=t[[X-Coordinates]:[Y-Coordinates]]")]
    [InlineData("=[@Column]")]
    [InlineData("=t[@Column]")]
    [InlineData("=t[[#Headers],[City]]")]
    [InlineData("=t[#All]")]
    [InlineData("=t[#Data]")]
    [InlineData("=t[#Headers]")]
    [InlineData("=t[#Totals]")]
    [InlineData("=t[[#This Row],[City]]")]
    [InlineData("=t[Column With Spaces]")]
    [InlineData("=t['[Bracketed]")]
    [InlineData("=t['#Hashed]")]
    // Operators & precedence
    [InlineData("=A1+B1")]
    [InlineData("=A1-B1-C1")]
    [InlineData("=A1*B1+C1")]
    [InlineData("=A1+B1*C1")]
    [InlineData("=2^3^2")]
    [InlineData("=-A1^2")]
    [InlineData("=A1&B1&C1")]
    [InlineData("=A1<=B1")]
    [InlineData("=A1<>B1")]
    [InlineData("=A1>=B1")]
    [InlineData("=A1=B1")]
    [InlineData("=50%")]
    [InlineData("=A1%+B1")]
    [InlineData("=@A1")]
    [InlineData("=@INDEX(arr,1,1)")]
    [InlineData("=(A1:A3,B1:B3)")]
    [InlineData("=A1:A10 B1:B10")]
    [InlineData("=(A1+B1)*C1")]
    // Function calls
    [InlineData("=SUM(A1:A10)")]
    [InlineData("=SUM(A1,B1,C1)")]
    [InlineData("=SUM(A1,,A3)")]
    [InlineData("=SUM()")]
    [InlineData("=ROUND(SQRT(SUMSQ(A1)),0)")]
    [InlineData("=_xlfn.XLOOKUP(H94,t[City],t[Pop])")]
    [InlineData("=LET(x,1,x+1)")]
    [InlineData("=LAMBDA(x,x*2)")]
    // Whitespace / newlines preserved
    [InlineData("= SUM( A1 , B1 ) ")]
    [InlineData("=SUM(\n    A1,\n    B1\n)")]
    [InlineData("=A1 + B1")]
    public void Parse_RoundTrips(string formula) => AssertRoundTrips(formula);

    [Fact]
    public void Parse_WorkedExample_RoundTrips()
    {
        const string f =
            "=ROUND(SQRT(SUMSQ(XLOOKUP(H94, t[City], t[[X-Coordinates]:[Y-Coordinates]]) " +
            "- $I$92:$J$92)) * 100, 0)";
        AssertRoundTrips(f);
    }

    // ------------------------------------------------------- Structure: leaves

    [Theory]
    [InlineData("=t[City]")]
    [InlineData("=t[[X-Coordinates]:[Y-Coordinates]]")]
    [InlineData("=[@Column]")]
    [InlineData("=t[[#Headers],[City]]")]
    [InlineData("=t[#All]")]
    [InlineData("=$A$1")]
    [InlineData("=Sheet1!A1")]
    [InlineData("='My Sheet'!A1")]
    [InlineData("=Sheet1:Sheet3!A1")]
    [InlineData("=[Book.xlsx]Sheet1!A1")]
    [InlineData("={1,2;3,4}")]
    [InlineData("=#N/A")]
    [InlineData("=42")]
    [InlineData("=\"text\"")]
    [InlineData("=myNamedRange")]
    public void Parse_AtomicLeaf_IsOpaqueLeafNode(string formula)
    {
        var leaf = Assert.IsType<LeafNode>(Root(formula));
        Assert.Equal(formula[1..], leaf.Text);
    }

    [Fact]
    public void Parse_SpillRef_IsPostfixOverLeaf_NotAStep()
    {
        var postfix = Assert.IsType<PostfixNode>(Root("=A1#"));
        Assert.Equal("#", postfix.Operator);
        Assert.IsType<LeafNode>(postfix.Operand);
    }

    [Fact]
    public void Parse_Range_IsRangeBinaryOverLeaves()
    {
        var binary = Assert.IsType<BinaryNode>(Root("=A1:B10"));
        Assert.Equal(":", binary.Operator);
        Assert.IsType<LeafNode>(binary.Left);
        Assert.IsType<LeafNode>(binary.Right);
    }

    // --------------------------------------------------- Structure: function calls

    [Fact]
    public void Parse_FunctionCall_CapturesNameAndArgs()
    {
        var call = Assert.IsType<FunctionCallNode>(Root("=XLOOKUP(H94, t[City], t[Pop])"));
        Assert.Equal("XLOOKUP", call.Name);
        Assert.Equal(3, call.Arguments.Count);
        Assert.All(call.Arguments, a => Assert.IsType<LeafNode>(a));
    }

    [Fact]
    public void Parse_EmptyArgument_IsEmptyArgNode()
    {
        var call = Assert.IsType<FunctionCallNode>(Root("=SUM(A1,,A3)"));
        Assert.Equal(3, call.Arguments.Count);
        Assert.IsType<LeafNode>(call.Arguments[0]);
        Assert.IsType<EmptyArgNode>(call.Arguments[1]);
        Assert.IsType<LeafNode>(call.Arguments[2]);
        Assert.Equal(2, call.Commas.Count);
    }

    [Fact]
    public void Parse_ZeroArgCall_HasNoArguments()
    {
        var call = Assert.IsType<FunctionCallNode>(Root("=NOW()"));
        Assert.Empty(call.Arguments);
        Assert.Empty(call.Commas);
    }

    [Fact]
    public void Parse_NestedCall_NestsFunctionNodes()
    {
        var round = Assert.IsType<FunctionCallNode>(Root("=ROUND(SQRT(SUMSQ(A1)), 0)"));
        Assert.Equal("ROUND", round.Name);
        var sqrt = Assert.IsType<FunctionCallNode>(round.Arguments[0]);
        Assert.Equal("SQRT", sqrt.Name);
        var sumsq = Assert.IsType<FunctionCallNode>(sqrt.Arguments[0]);
        Assert.Equal("SUMSQ", sumsq.Name);
        Assert.IsType<LeafNode>(sumsq.Arguments[0]);
    }

    [Fact]
    public void Parse_XlfnPrefixedFunction_KeepsPrefixInName()
    {
        var call = Assert.IsType<FunctionCallNode>(Root("=_xlfn.SINGLE(A1)"));
        Assert.Equal("_xlfn.SINGLE", call.Name);
    }

    // ---------------------------------------------------- Structure: precedence

    [Fact]
    public void Parse_MultiplicationBindsTighterThanAddition()
    {
        var add = Assert.IsType<BinaryNode>(Root("=a+b*c"));
        Assert.Equal("+", add.Operator);
        Assert.IsType<LeafNode>(add.Left);
        var mul = Assert.IsType<BinaryNode>(add.Right);
        Assert.Equal("*", mul.Operator);
    }

    [Fact]
    public void Parse_SubtractionIsLeftAssociative()
    {
        var outer = Assert.IsType<BinaryNode>(Root("=a-b-c"));
        Assert.Equal("-", outer.Operator);
        var inner = Assert.IsType<BinaryNode>(outer.Left);
        Assert.Equal("-", inner.Operator);
        Assert.IsType<LeafNode>(outer.Right);
    }

    [Fact]
    public void Parse_ExponentIsLeftAssociative()
    {
        // Excel: 2^3^2 == (2^3)^2.
        var outer = Assert.IsType<BinaryNode>(Root("=2^3^2"));
        Assert.Equal("^", outer.Operator);
        Assert.IsType<BinaryNode>(outer.Left);
        Assert.IsType<LeafNode>(outer.Right);
    }

    [Fact]
    public void Parse_UnaryMinusIsLooserThanExponent()
    {
        // -A1^2 == -(A1^2).
        var unary = Assert.IsType<UnaryNode>(Root("=-A1^2"));
        Assert.Equal("-", unary.Operator);
        Assert.IsType<BinaryNode>(unary.Operand);
    }

    [Fact]
    public void Parse_ComparisonIsLoosestOperator()
    {
        // a&b=c == (a&b)=c.
        var eq = Assert.IsType<BinaryNode>(Root("=a&b=c"));
        Assert.Equal("=", eq.Operator);
        var concat = Assert.IsType<BinaryNode>(eq.Left);
        Assert.Equal("&", concat.Operator);
    }

    [Fact]
    public void Parse_RangeBindsTighterThanIntersection()
    {
        // A1:A10 B1:B10 == (A1:A10) ∩ (B1:B10).
        var intersect = Assert.IsType<BinaryNode>(Root("=A1:A10 B1:B10"));
        Assert.Equal(" ", intersect.Operator);
        Assert.Null(intersect.OperatorToken);
        Assert.Equal(":", Assert.IsType<BinaryNode>(intersect.Left).Operator);
        Assert.Equal(":", Assert.IsType<BinaryNode>(intersect.Right).Operator);
    }

    [Fact]
    public void Parse_Union_IsMultiItemParen()
    {
        var paren = Assert.IsType<ParenNode>(Root("=(A1:A3,B1:B3)"));
        Assert.True(paren.IsUnion);
        Assert.Equal(2, paren.Items.Count);
    }

    [Fact]
    public void Parse_AtPrefix_IsUnaryOverCall()
    {
        var at = Assert.IsType<UnaryNode>(Root("=@INDEX(arr,1,1)"));
        Assert.Equal("@", at.Operator);
        Assert.IsType<FunctionCallNode>(at.Operand);
    }

    [Fact]
    public void Parse_GroupingParen_IsSingleItemParen()
    {
        var product = Assert.IsType<BinaryNode>(Root("=(A1+B1)*C1"));
        Assert.Equal("*", product.Operator);
        var paren = Assert.IsType<ParenNode>(product.Left);
        Assert.False(paren.IsUnion);
        Assert.Single(paren.Items);
        Assert.Equal("+", Assert.IsType<BinaryNode>(paren.Items[0]).Operator);
    }

    // ------------------------------------------------------------- Spans

    [Fact]
    public void Parse_NodeSpan_MatchesSourceText()
    {
        const string f = "=ROUND(SQRT(A1), 0)";
        var round = Assert.IsType<FunctionCallNode>(Root(f));
        var sqrt = round.Arguments[0];
        Assert.Equal("SQRT(A1)", f[sqrt.Start..sqrt.End]);
    }

    // ------------------------------------------------------------- Errors

    [Theory]
    [InlineData("=SUM(A1")]          // unbalanced paren
    [InlineData("=\"abc")]            // unterminated string
    [InlineData("=t[City")]           // unterminated bracket
    [InlineData("=")]                 // nothing after marker
    [InlineData("=,")]                // stray comma
    [InlineData("=)")]                // stray close paren
    [InlineData("=A1 B1 +")]          // dangling operator
    [InlineData("=A1 + + ")]          // dangling operator
    public void Parse_Malformed_ThrowsFormatException(string formula)
    {
        Assert.Throws<FormatException>(() => FormulaParser.Parse(formula));
    }

    [Fact]
    public void Parse_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FormulaParser.Parse(null!));
    }

    [Fact]
    public void Parse_NoLeadingEquals_StillParses()
    {
        var ast = FormulaParser.Parse("SUM(A1,B1)");
        Assert.Null(ast.EqualsToken);
        Assert.IsType<FunctionCallNode>(ast.Root);
        Assert.Equal("SUM(A1,B1)", ast.ToFormula());
    }

    // ------------------------------------------------- Golden round-trip corpus

    public static IEnumerable<object[]> LibraryFormulas()
    {
        var lambdasDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "lambdas"));

        foreach (var file in Directory.EnumerateFiles(lambdasDir, "*.lambda", SearchOption.AllDirectories))
        {
            (string Name, string Formula) parsed;
            try
            {
                parsed = LambdaParser.ParseFile(file);
            }
            catch (FormatException)
            {
                continue; // not a parseable .lambda definition; skip
            }

            yield return new object[] { Path.GetFileName(file), parsed.Formula };
        }
    }

    [Theory]
    [MemberData(nameof(LibraryFormulas))]
    public void Parse_EveryLibraryFormula_RoundTripsIdentically(string fileName, string formula)
    {
        // fileName is included so a failure names the offending .lambda file.
        Assert.NotNull(fileName);
        var ast = FormulaParser.Parse(formula);
        Assert.Equal(formula, ast.ToFormula());
    }
}
