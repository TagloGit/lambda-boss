using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Issue #470: Alt+Right / Alt+Left move the cell Gather writes to, so the cell it
///     started on stays as a fallback.
/// </summary>
public class GatherSinkTargetTests
{
    private static CellRef C(string a1) => Ref("Sheet1", a1);

    private static CellRef Ref(string sheet, string a1)
    {
        var letters = new string(a1.TakeWhile(char.IsLetter).ToArray());
        var row = int.Parse(a1.Substring(letters.Length));
        return new CellRef(sheet, CellRef.LettersToColumn(letters), row);
    }

    private static readonly IReadOnlyList<BindingRow> NoBindings = Array.Empty<BindingRow>();

    private static BindingRow Row(FormulaRef source, FormulaRef? sliceOf = null) =>
        new(source, BindingRole.Input, "x", source.A1Address, SliceOf: sliceOf);

    // -- moving ----------------------------------------------------------------------

    [Fact]
    public void Move_StepsOneColumnAndNeverLeftOfTheStart()
    {
        var start = C("D5");
        Assert.Equal(1, GatherSinkTarget.Move(start, 0, 1));
        Assert.Equal(2, GatherSinkTarget.Move(start, 1, 1));
        Assert.Equal(1, GatherSinkTarget.Move(start, 2, -1));
        Assert.Equal(0, GatherSinkTarget.Move(start, 0, -1));
    }

    [Fact]
    public void Move_StopsAtMaxOffsetAndTheLastColumn()
    {
        Assert.Equal(GatherSinkTarget.MaxOffset,
            GatherSinkTarget.Move(C("A1"), GatherSinkTarget.MaxOffset, 1));
        var nearEnd = new CellRef("Sheet1", 16383, 1);
        Assert.Equal(1, GatherSinkTarget.Move(nearEnd, 1, 1));
        Assert.Equal(0, GatherSinkTarget.Move(new CellRef("Sheet1", 16384, 1), 0, 1));
    }

    [Fact]
    public void Move_StepsOverTheStartCellsOwnSpill()
    {
        // D5 spills four columns wide (D5:G5): the first target is H5, offset 4.
        var start = C("D5");
        Assert.Equal(4, GatherSinkTarget.Move(start, 0, 1, firstOffset: 4));
        Assert.Equal(5, GatherSinkTarget.Move(start, 4, 1, firstOffset: 4));
        Assert.Equal(4, GatherSinkTarget.Move(start, 5, -1, firstOffset: 4));
        Assert.Equal(0, GatherSinkTarget.Move(start, 4, -1, firstOffset: 4));
        Assert.Equal(GatherSinkTarget.LastOffset(4),
            GatherSinkTarget.Move(start, GatherSinkTarget.LastOffset(4), 1, firstOffset: 4));
        Assert.Equal(13, GatherSinkTarget.LastOffset(4));
    }

    [Fact]
    public void At_ShiftsTheColumnOnly()
    {
        Assert.Equal(C("F5"), GatherSinkTarget.At(C("D5"), 2));
        Assert.Equal(C("D5"), GatherSinkTarget.At(C("D5"), 0));
    }

    // -- checking ----------------------------------------------------------------------

    [Fact]
    public void Check_StartCell_IsAlwaysFine()
    {
        var check = GatherSinkTarget.Check(C("D5"), C("D5"), NoBindings,
            new[] { "=LET(a, D5, a)" }, new SinkCellInfo(false, true));
        Assert.Same(SinkTargetCheck.Fine, check);
    }

    [Fact]
    public void Check_EmptyCellNotInTheCalculation_IsFine()
    {
        var check = GatherSinkTarget.Check(C("D5"), C("E5"),
            new[] { Row(new FormulaRef(C("A5"))) },
            new[] { "=LET(input_1, A5, input_1*2)" }, SinkCellInfo.Empty);
        Assert.False(check.Blocked);
        Assert.Null(check.Message);
    }

    [Fact]
    public void Check_WalkedCell_Blocks()
    {
        // The LET inlines a step's cell, so its address never appears in the text.
        var check = GatherSinkTarget.Check(C("D5"), C("E5"),
            new[] { Row(new FormulaRef(C("E5"))) },
            new[] { "=LET(step_1, F5*2, step_1+1)" }, new SinkCellInfo(false, true));
        Assert.True(check.Blocked);
        Assert.Contains("part of the calculation", check.Message);
    }

    [Fact]
    public void Check_InsideAnInputRange_Blocks()
    {
        var check = GatherSinkTarget.Check(C("D5"), C("F5"),
            new[] { Row(new FormulaRef(C("E4"), C("G6"))) },
            new[] { "=LET(input_1, E4:G6, SUM(input_1))" }, new SinkCellInfo(false, false));
        Assert.True(check.Blocked);
    }

    [Fact]
    public void Check_InsideTheSpillOfAWalkedAnchor_Blocks()
    {
        var anchor = C("E4");
        var check = GatherSinkTarget.Check(C("D5"), C("E5"),
            new[] { Row(new FormulaRef(anchor, IsSpilled: true)) },
            new[] { "=LET(input_1, E4#, SUM(input_1))" }, new SinkCellInfo(false, false, anchor));
        Assert.True(check.Blocked);
    }

    [Fact]
    public void Check_InsideTheSpillASliceComesFrom_Blocks()
    {
        var anchor = C("E1");
        var check = GatherSinkTarget.Check(C("D5"), C("E5"),
            new[] { Row(new FormulaRef(C("E2")), sliceOf: new FormulaRef(anchor, IsSpilled: true)) },
            new[] { "=LET(coords_1, INDEX(x, 2), coords_1)" }, new SinkCellInfo(false, false, anchor));
        Assert.True(check.Blocked);
    }

    [Fact]
    public void Check_InsideTheStartCellsOwnSpill_Blocks()
    {
        var check = GatherSinkTarget.Check(C("D5"), C("E5"), NoBindings,
            new[] { "=LET(a, A1, SEQUENCE(1, a))" }, new SinkCellInfo(false, false, C("D5")));
        Assert.True(check.Blocked);
        Assert.Contains("D5's spill", check.Message);
    }

    [Fact]
    public void Check_ExcelsOwnPrecedent_Blocks()
    {
        // e.g. XLOOKUP(x, E:E, F:F): no cell ref the text check can see.
        var check = GatherSinkTarget.Check(C("D5"), C("E5"), NoBindings,
            new[] { "=LET(a, XLOOKUP(A5, E:E, F:F), a)" },
            new SinkCellInfo(true, false, IsPrecedent: true));
        Assert.True(check.Blocked);
        Assert.Contains("part of the calculation", check.Message);
    }

    [Theory]
    [InlineData("=LET(a, INDEX(A1:C1, COLUMN()-3), a)")]
    [InlineData("=LET(a, INDIRECT(\"RC[-1]\", FALSE), a)")]
    [InlineData("=LET(a, indirect(\"RC[-1]\", FALSE), a)")]
    public void Check_ColumnDependentFormula_WarnsWithoutBlocking(string formula)
    {
        var check = GatherSinkTarget.Check(C("D5"), C("E5"), NoBindings,
            new[] { formula }, SinkCellInfo.Empty);
        Assert.False(check.Blocked);
        Assert.Contains("COLUMN()", check.Message);
    }

    [Fact]
    public void Check_ColumnWithAnArgumentInAStringOrALongerName_DoesNotWarn()
    {
        var check = GatherSinkTarget.Check(C("D5"), C("E5"), NoBindings,
            new[] { "=LET(a, COLUMN(A5), b, \"COLUMN()\", c, MYCOLUMN(), d, OFFSET(A5,0,1), a)" },
            SinkCellInfo.Empty);
        Assert.Null(check.Message);
    }

    [Fact]
    public void Check_OverwriteAndColumnWarnings_AreBothShown()
    {
        var check = GatherSinkTarget.Check(C("D5"), C("E5"), NoBindings,
            new[] { "=LET(a, INDEX(A1:C1, COLUMN()-3), a)" }, new SinkCellInfo(false, false));
        Assert.False(check.Blocked);
        Assert.Contains("Overwrites the value in E5", check.Message);
        Assert.Contains("COLUMN()", check.Message);
    }

    [Fact]
    public void Check_TargetReadByTheFormula_BlocksAsCircular()
    {
        // An excluded row leaves its cell as a literal reference in the text.
        var check = GatherSinkTarget.Check(C("D5"), C("E5"), NoBindings,
            new[] { "=LET(step_1, E5*2, step_1+1)" }, SinkCellInfo.Empty);
        Assert.True(check.Blocked);
        Assert.Contains("circular", check.Message);
    }

    [Fact]
    public void Check_TargetReadByTheLambdaCall_Blocks()
    {
        var check = GatherSinkTarget.Check(C("D5"), C("F5"), NoBindings,
            new[] { "=LET(a, 1, a)", "=vend(E5:G5, L287)" }, SinkCellInfo.Empty);
        Assert.True(check.Blocked);
    }

    [Fact]
    public void Check_TargetOnAnotherSheetsSameAddress_IsNotRead()
    {
        var check = GatherSinkTarget.Check(C("D5"), C("E5"), NoBindings,
            new[] { "=LET(a, Data!E5, a)" }, SinkCellInfo.Empty);
        Assert.False(check.Blocked);
    }

    [Fact]
    public void Check_TargetWithAValueOrFormula_WarnsWithoutBlocking()
    {
        var value = GatherSinkTarget.Check(C("D5"), C("E5"), NoBindings,
            new[] { "=LET(a, A1, a)" }, new SinkCellInfo(false, false));
        Assert.False(value.Blocked);
        Assert.Equal("Overwrites the value in E5", value.Message);

        var formula = GatherSinkTarget.Check(C("D5"), C("E5"), NoBindings,
            new[] { "=LET(a, A1, a)" }, new SinkCellInfo(false, true));
        Assert.False(formula.Blocked);
        Assert.Equal("Overwrites the formula in E5", formula.Message);
    }

    [Fact]
    public void Check_InsideAnUnrelatedSpill_WarnsWithoutBlocking()
    {
        var check = GatherSinkTarget.Check(C("D5"), C("E5"), NoBindings,
            new[] { "=LET(a, A1, a)" }, new SinkCellInfo(false, false, C("E1")));
        Assert.False(check.Blocked);
        Assert.Contains("E1's spill", check.Message);
    }
}
