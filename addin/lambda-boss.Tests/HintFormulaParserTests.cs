using Xunit;

using HintParser = ExcelDna.IntelliSense.FormulaParser;

namespace LambdaBoss.Tests;

/// <summary>
///     The vendored IntelliSense server's formula-prefix parser: which function the caret is in,
///     and which argument. Multi-line formulas (Alt+Enter, tabs, CRLF) must read the same as
///     one-line ones (VENDORED.md, patch 5).
/// </summary>
public class HintFormulaParserTests
{
    [Theory]
    // Last line of a LET.
    [InlineData("=LET(\n  x, A1:A5,\n  BIROW(", "BIROW", 0)]
    [InlineData("=LET(\n  x, A1:A5,\n  BIROW(x, ", "BIROW", 1)]
    // After a line break mid-argument.
    [InlineData("=SUM(1,\nNEIGHBOUROP(", "NEIGHBOUROP", 0)]
    [InlineData("=LET(x, 1, y, BIROW(A1:B2,\n  ", "BIROW", 1)]
    [InlineData("=BIROW(a,\nb", "BIROW", 1)]
    [InlineData("=BIROW(a\n,b", "BIROW", 1)]
    // Tab-indented.
    [InlineData("=LET(\n\tx, 1,\n\tBIROW(", "BIROW", 0)]
    [InlineData("=LET(\n\tx, 1,\n\tBIROW(x,\n\t\t", "BIROW", 1)]
    // CRLF.
    [InlineData("=LET(\r\n  x, 1,\r\n  BIROW(", "BIROW", 0)]
    [InlineData("=LET(\r\n  x, 1,\r\n  BIROW(a,\r\n  b,", "BIROW", 2)]
    // Several spaces.
    [InlineData("=LET(x, 1,    BIROW(a,   ", "BIROW", 1)]
    // Line break inside a string, closed and still open.
    [InlineData("=BIROW(\"a,\nb\", ", "BIROW", 1)]
    [InlineData("=BIROW(x, \"a,\nb", "BIROW", 1)]
    [InlineData("=LET(s, \"x\ny\",\nBIROW(", "BIROW", 0)]
    // Line break inside an array.
    [InlineData("=BIROW({1,2;\n3,4}, ", "BIROW", 1)]
    // Nested call closed on an earlier line.
    [InlineData("=BIROW(SUM(1,\n2),\n", "BIROW", 1)]
    // Upstream one-line behaviour unchanged.
    [InlineData("=SUM( 1, 2", "SUM", 1)]
    [InlineData("=BIROW(", "BIROW", 0)]
    public void Finds_function_and_argument(string prefix, string expectedName, int expectedIndex)
    {
        Assert.True(HintParser.TryGetFormulaInfo(prefix, out var name, out var index));
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedIndex, index);
    }

    [Theory]
    [InlineData("=LET(\n  x, 1,\n  BIROW(", "  ")]
    [InlineData("=LET(\r\n\tx, 1,\r\n\tBIROW(", "\t")]
    [InlineData("=BIROW(", "=")]
    public void Line_before_function_starts_after_the_last_line_break(string prefix, string expected)
    {
        Assert.Equal(expected, HintParser.GetLineBeforeFunctionName(prefix, "BIROW"));
    }
}
