using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     The slice ladder from spec 0010, exercised as a pure function: 1×1 /
///     1×N / N×1 / N×M spills against every reference shape, both flush edges
///     on each axis, interior bands, blocks constrained on both axes, the
///     negative-<c>TAKE</c>-with-cross-axis-<c>DROP</c> composition, and the
///     invariant that no input produces a 1×1 array where a scalar was asked
///     for.
/// </summary>
public class SpillSliceBuilderTests
{
    // ---- helpers: the shape is fixed per helper so the theory data stays ints ----

    private static string SpillRef(int rows, int cols, string name = "arr")
    {
        return SpillSliceBuilder.Build(name, rows, cols, 1, rows, 1, cols, SliceRefShape.SpillRef);
    }

    private static string Cell(int rows, int cols, int r, int c, string name = "arr")
    {
        return SpillSliceBuilder.Build(name, rows, cols, r, r, c, c, SliceRefShape.SingleCell);
    }

    private static string Rng(int rows, int cols, int r1, int r2, int c1, int c2, string name = "arr")
    {
        return SpillSliceBuilder.Build(name, rows, cols, r1, r2, c1, c2, SliceRefShape.Range);
    }

    // ---- rule 1: a spill ref is always the whole array ----

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 3)]
    [InlineData(4, 1)]
    [InlineData(5, 4)]
    public void SpillRef_AnyGeometry_IsTheBindingName(int rows, int cols)
    {
        Assert.Equal("arr", SpillRef(rows, cols));
    }

    [Fact]
    public void SpillRef_OnOneByOneSpill_IsStillTheArray()
    {
        // A 1×1 array is not a scalar; only an explicit A1# asks for it.
        Assert.Equal("arr", SpillRef(1, 1));
    }

    // ---- rule 2: a single cell is INDEX, positionally, always ----

    [Theory]
    [InlineData(1, 1, 1, 1, "INDEX(arr,1,1)")]
    [InlineData(1, 3, 1, 1, "INDEX(arr,1,1)")]
    [InlineData(1, 3, 1, 2, "INDEX(arr,1,2)")]
    [InlineData(1, 3, 1, 3, "INDEX(arr,1,3)")]
    [InlineData(4, 1, 1, 1, "INDEX(arr,1,1)")]
    [InlineData(4, 1, 3, 1, "INDEX(arr,3,1)")]
    [InlineData(4, 1, 4, 1, "INDEX(arr,4,1)")]
    [InlineData(5, 4, 1, 1, "INDEX(arr,1,1)")]
    [InlineData(5, 4, 3, 2, "INDEX(arr,3,2)")]
    [InlineData(5, 4, 1, 4, "INDEX(arr,1,4)")]
    [InlineData(5, 4, 5, 1, "INDEX(arr,5,1)")]
    [InlineData(5, 4, 5, 4, "INDEX(arr,5,4)")]
    public void SingleCellRef_EmitsPositionalIndex(int rows, int cols, int r, int c, string expected)
    {
        Assert.Equal(expected, Cell(rows, cols, r, c));
    }

    [Theory]
    [InlineData(1, 1, 1, 1, "INDEX(arr,1,1)")]
    [InlineData(1, 3, 1, 3, "INDEX(arr,1,3)")]
    [InlineData(4, 1, 4, 1, "INDEX(arr,4,1)")]
    [InlineData(5, 4, 2, 3, "INDEX(arr,2,3)")]
    [InlineData(5, 4, 5, 4, "INDEX(arr,5,4)")]
    public void DegenerateRange_TakesTheScalarPath(int rows, int cols, int r, int c, string expected)
    {
        // A2:A2 covers one cell, so the single-cell rule wins — including on a
        // 1×1 spill, where the same rectangle is simultaneously the whole array.
        Assert.Equal(expected, Rng(rows, cols, r, r, c, c));
    }

    // ---- rule 3: the whole array, spanning more than one cell ----

    [Theory]
    [InlineData(1, 3)]
    [InlineData(4, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 4)]
    public void RangeSpanningTheWholeSpill_IsTheBindingName(int rows, int cols)
    {
        Assert.Equal("arr", Rng(rows, cols, 1, rows, 1, cols));
    }

    // ---- rule 4a: row bands, all columns ----

    [Theory]
    [InlineData(5, 4, 1, 1, "TAKE(arr,1)")]
    [InlineData(5, 4, 1, 3, "TAKE(arr,3)")]
    [InlineData(5, 4, 1, 4, "TAKE(arr,4)")]
    [InlineData(5, 4, 3, 5, "TAKE(arr,-3)")]
    [InlineData(5, 4, 5, 5, "TAKE(arr,-1)")]
    [InlineData(5, 4, 2, 4, "TAKE(DROP(arr,1),3)")]
    [InlineData(5, 4, 2, 2, "TAKE(DROP(arr,1),1)")]
    [InlineData(10, 2, 4, 6, "TAKE(DROP(arr,3),3)")]
    [InlineData(4, 1, 1, 2, "TAKE(arr,2)")]
    [InlineData(4, 1, 2, 4, "TAKE(arr,-3)")]
    [InlineData(5, 1, 2, 3, "TAKE(DROP(arr,1),2)")]
    public void RowBand_FullWidth_TakesOnTheRowAxisOnly(int rows, int cols, int r1, int r2, string expected)
    {
        Assert.Equal(expected, Rng(rows, cols, r1, r2, 1, cols));
    }

    // ---- rule 4b: column bands, all rows ----

    [Theory]
    [InlineData(5, 4, 1, 1, "TAKE(arr,,1)")]
    [InlineData(5, 4, 1, 2, "TAKE(arr,,2)")]
    [InlineData(5, 4, 1, 3, "TAKE(arr,,3)")]
    [InlineData(5, 4, 3, 4, "TAKE(arr,,-2)")]
    [InlineData(5, 4, 4, 4, "TAKE(arr,,-1)")]
    [InlineData(5, 4, 2, 3, "TAKE(DROP(arr,,1),,2)")]
    [InlineData(5, 4, 2, 2, "TAKE(DROP(arr,,1),,1)")]
    [InlineData(1, 3, 1, 2, "TAKE(arr,,2)")]
    [InlineData(1, 3, 2, 3, "TAKE(arr,,-2)")]
    [InlineData(1, 5, 2, 3, "TAKE(DROP(arr,,1),,2)")]
    [InlineData(2, 3, 1, 1, "TAKE(arr,,1)")]
    [InlineData(2, 3, 3, 3, "TAKE(arr,,-1)")]
    public void ColumnBand_FullHeight_TakesOnTheColumnAxisOnly(int rows, int cols, int c1, int c2, string expected)
    {
        // The last column is edge-relative: TAKE(arr,,-1), never a counted
        // CHOOSECOLS or an absolute index.
        Assert.Equal(expected, Rng(rows, cols, 1, rows, c1, c2));
    }

    // ---- rule 4c: blocks constrained on both axes ----

    [Theory]
    [InlineData(5, 4, 1, 2, 1, 2, "TAKE(arr,2,2)")]
    [InlineData(5, 4, 4, 5, 3, 4, "TAKE(arr,-2,-2)")]
    [InlineData(5, 4, 1, 2, 3, 4, "TAKE(arr,2,-2)")]
    [InlineData(5, 4, 4, 5, 1, 2, "TAKE(arr,-2,2)")]
    [InlineData(5, 4, 4, 5, 4, 4, "TAKE(arr,-2,-1)")]
    [InlineData(5, 4, 2, 3, 2, 3, "TAKE(DROP(arr,1,1),2,2)")]
    [InlineData(5, 4, 2, 4, 3, 3, "TAKE(DROP(arr,1,2),3,1)")]
    [InlineData(5, 4, 1, 2, 2, 3, "TAKE(DROP(arr,,1),2,2)")]
    [InlineData(5, 4, 2, 3, 4, 4, "TAKE(DROP(arr,1),2,-1)")]
    [InlineData(5, 4, 2, 3, 1, 2, "TAKE(DROP(arr,1),2,2)")]
    public void Block_ComposesAtMostOneDropAndOneTake(
        int rows, int cols, int r1, int r2, int c1, int c2, string expected)
    {
        Assert.Equal(expected, Rng(rows, cols, r1, r2, c1, c2));
    }

    [Fact]
    public void NegativeTake_ComposesWithADropOnTheOtherAxis()
    {
        // The last row of everything after the first column.
        Assert.Equal("TAKE(DROP(arr,,1),-1,2)", Rng(5, 4, 5, 5, 2, 3));

        // The transpose: the last column of everything after the first row.
        Assert.Equal("TAKE(DROP(arr,1),2,-1)", Rng(4, 5, 2, 3, 5, 5));
    }

    // ---- argument omission ----

    [Fact]
    public void TrailingOmission_DropsTheArgument()
    {
        Assert.Equal("TAKE(arr,3)", Rng(5, 4, 1, 3, 1, 4));
        Assert.Equal("TAKE(DROP(arr,3),3)", Rng(10, 2, 4, 6, 1, 2));
    }

    [Fact]
    public void InteriorOmission_RendersAsABareComma()
    {
        Assert.Equal("TAKE(arr,,-1)", Rng(5, 4, 1, 5, 4, 4));
        Assert.Equal("TAKE(DROP(arr,,1),,2)", Rng(5, 4, 1, 5, 2, 3));
    }

    // ---- the binding name is substituted verbatim ----

    [Fact]
    public void BindingName_IsUsedVerbatimInEveryForm()
    {
        Assert.Equal("extracted", SpillRef(1, 2, "extracted"));
        Assert.Equal("extracted", Rng(1, 2, 1, 1, 1, 2, "extracted"));
        Assert.Equal("INDEX(extracted,1,2)", Cell(1, 3, 1, 2, "extracted"));
        Assert.Equal("TAKE(extracted,,-1)", Rng(5, 4, 1, 5, 4, 4, "extracted"));
        Assert.Equal("TAKE(DROP(extracted,1,2),3,1)", Rng(5, 4, 2, 4, 3, 3, "extracted"));
    }

    // ---- invariants, swept exhaustively ----

    [Fact]
    public void NoInputEverProducesAOneByOneArray()
    {
        foreach (var (rows, cols, r1, r2, c1, c2) in AllRectangles(5, 5))
        {
            var height = r2 - r1 + 1;
            var width = c2 - c1 + 1;
            var isOneCell = height == 1 && width == 1;

            var range = SpillSliceBuilder.Build("arr", rows, cols, r1, r2, c1, c2, SliceRefShape.Range);
            var label = $"{rows}x{cols} [{r1}..{r2},{c1}..{c2}] -> {range}";

            if (isOneCell)
                // A one-cell rectangle is a scalar, never an array — so never
                // TAKE, and never the bare binding name.
                Assert.True(range.StartsWith("INDEX(", StringComparison.Ordinal), label);
            else
                Assert.False(range.StartsWith("INDEX(", StringComparison.Ordinal), label);

            // TAKE is only ever emitted for a rectangle spanning 2+ cells.
            if (range.StartsWith("TAKE(", StringComparison.Ordinal))
                Assert.True(height * width > 1, label);

            // The bare binding name only comes back for a 2+ cell whole-array range.
            if (range == "arr")
                Assert.True(height == rows && width == cols && height * width > 1, label);

            if (isOneCell)
            {
                var cell = SpillSliceBuilder.Build("arr", rows, cols, r1, r2, c1, c2, SliceRefShape.SingleCell);
                Assert.Equal($"INDEX(arr,{r1},{c1})", cell);
            }
        }
    }

    [Fact]
    public void EveryOutputIsOneOfTheFourForms_WithAtMostOneDropAndOneTake()
    {
        foreach (var (rows, cols, r1, r2, c1, c2) in AllRectangles(5, 5))
        {
            var range = SpillSliceBuilder.Build("arr", rows, cols, r1, r2, c1, c2, SliceRefShape.Range);
            var label = $"{rows}x{cols} [{r1}..{r2},{c1}..{c2}] -> {range}";

            var isKnownForm = range == "arr"
                              || range.StartsWith("INDEX(arr,", StringComparison.Ordinal)
                              || range.StartsWith("TAKE(arr", StringComparison.Ordinal)
                              || range.StartsWith("TAKE(DROP(arr", StringComparison.Ordinal);
            Assert.True(isKnownForm, label);

            Assert.True(Occurrences(range, "TAKE(") <= 1, label);
            Assert.True(Occurrences(range, "DROP(") <= 1, label);

            // A spill ref over the same geometry is always just the array.
            Assert.Equal("arr", SpillSliceBuilder.Build("arr", rows, cols, 1, rows, 1, cols, SliceRefShape.SpillRef));
        }
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            count++;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static IEnumerable<(int Rows, int Cols, int R1, int R2, int C1, int C2)> AllRectangles(
        int maxRows, int maxCols)
    {
        for (var rows = 1; rows <= maxRows; rows++)
        for (var cols = 1; cols <= maxCols; cols++)
        for (var r1 = 1; r1 <= rows; r1++)
        for (var r2 = r1; r2 <= rows; r2++)
        for (var c1 = 1; c1 <= cols; c1++)
        for (var c2 = c1; c2 <= cols; c2++)
            yield return (rows, cols, r1, r2, c1, c2);
    }

    // ---- input validation ----

    [Theory]
    [InlineData(0, 4, 1, 1, 1, 1)]
    [InlineData(5, 0, 1, 1, 1, 1)]
    [InlineData(5, 4, 0, 1, 1, 1)]
    [InlineData(5, 4, 1, 6, 1, 1)]
    [InlineData(5, 4, 3, 2, 1, 1)]
    [InlineData(5, 4, 1, 1, 0, 1)]
    [InlineData(5, 4, 1, 1, 1, 5)]
    [InlineData(5, 4, 1, 1, 3, 2)]
    public void OutOfBoundsRectangle_Throws(int rows, int cols, int r1, int r2, int c1, int c2)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpillSliceBuilder.Build("arr", rows, cols, r1, r2, c1, c2, SliceRefShape.Range));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingBindingName_Throws(string? name)
    {
        Assert.Throws<ArgumentException>(
            () => SpillSliceBuilder.Build(name!, 5, 4, 1, 2, 1, 2, SliceRefShape.Range));
    }
}
