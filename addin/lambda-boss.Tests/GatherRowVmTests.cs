using System.Windows;
using LambdaBoss.UI;
using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Unit tests for <see cref="GatherRowVm" />, the row view-model behind the
///     Gather dialog's bindings list, plus
///     <see cref="GatherWindow.Snapshot" />, the shared VM-to-
///     <see cref="BindingRow" /> builder the dialog uses when it caches a row
///     outside the engine's result.
///
///     The VM is plain presentation state — no engine, no window, no
///     dispatcher — so its display rules are exercisable directly. Spec 0010
///     PR 8's polish lives here: the spilled address form on an anchor row,
///     the slice-row indent, and the confirmation that the three special row
///     kinds (inner-LET expansion, orphan, slice) each render the control set
///     their own rule sheet calls for.
/// </summary>
public sealed class GatherRowVmTests
{
    private static CellRef Cell(string sheet, int column, int row)
    {
        return new CellRef(sheet, column, row);
    }

    private static FormulaRef Ref(int column, int row, bool spilled = false)
    {
        return new FormulaRef(Cell("Sheet1", column, row), null, spilled);
    }

    private static FormulaRef Range(int c1, int r1, int c2, int r2)
    {
        return new FormulaRef(Cell("Sheet1", c1, r1), Cell("Sheet1", c2, r2));
    }

    private static BindingRow Row(
        FormulaRef source,
        BindingRole role = BindingRole.Input,
        string name = "x",
        string rhs = "A1",
        bool isExpansion = false,
        bool canToggleRole = false,
        FormulaRef? sliceOf = null,
        CellRef? straddles = null)
    {
        return new BindingRow(
            source, role, name, rhs, isExpansion, canToggleRole, sliceOf, straddles);
    }

    // ---- Address: the spilled form distinguishes anchor from slice ----

    /// <summary>
    ///     The routed finding from PR #365's review: a spilling anchor's row
    ///     and a scalar-slice row of the same cell both rendered the bare
    ///     address, so the list showed two rows reading "A2". The anchor's
    ///     binding is the whole array, and its row now says so.
    /// </summary>
    [Fact]
    public void Address_SpillingAnchorRow_ShowsSpilledForm()
    {
        var vm = new GatherRowVm(Row(Ref(1, 2, true), rhs: "A2#"), true);

        Assert.Equal("A2#", vm.Address);
    }

    [Fact]
    public void Address_NonSpilledRow_ShowsBareAddress()
    {
        var vm = new GatherRowVm(Row(Ref(1, 2)), true);

        Assert.Equal("A2", vm.Address);
    }

    [Fact]
    public void Address_AnchorAndSliceOfSameCell_RenderDistinctly()
    {
        var anchorRef = Ref(1, 2, true);
        var anchor = new GatherRowVm(Row(anchorRef, name: "arr", rhs: "A2#"), true);
        // The scalar reference A2 landing inside A2's own spill: a slice row
        // whose Source is the non-spilled ref to the very same cell.
        var slice = new GatherRowVm(
            Row(Ref(1, 2), name: "first", rhs: "INDEX(arr,1,1)", sliceOf: anchorRef), true);

        Assert.Equal("A2#", anchor.Address);
        Assert.Equal("A2", slice.Address);
        Assert.NotEqual(anchor.Address, slice.Address);
    }

    [Fact]
    public void Address_RangeRow_ShowsStartToEnd()
    {
        var vm = new GatherRowVm(Row(Range(1, 1, 2, 3)), true);

        Assert.Equal("A1:B3", vm.Address);
    }

    // ---- Indentation: slice rows sit under their anchor ----

    [Fact]
    public void AddressIndent_SliceRow_IsIndented()
    {
        var vm = new GatherRowVm(Row(Ref(2, 1), sliceOf: Ref(1, 2, true)), true);

        Assert.True(vm.IsSlice);
        Assert.Equal(GatherRowVm.SliceIndentPixels, vm.AddressIndent.Left);
    }

    [Theory]
    [InlineData(false, false)] // ordinary cell input
    [InlineData(true, false)] // inner-LET expansion
    [InlineData(false, true)] // orphan
    public void AddressIndent_NonSliceRows_AreFlush(bool isExpansion, bool isOrphan)
    {
        var vm = new GatherRowVm(
            Row(Ref(1, 1), isExpansion: isExpansion),
            true,
            isOrphan ? "B2" : null);

        Assert.False(vm.IsSlice);
        Assert.Equal(new Thickness(0), vm.AddressIndent);
    }

    // ---- The three special row kinds render coherently in one list ----

    /// <summary>
    ///     Issue #360's rule table. The kinds deliberately stay separate
    ///     rather than unifying into one "child row" concept, because no two
    ///     of them share a rule set — this test is the executable statement of
    ///     that table, so a future attempt to collapse them has to confront
    ///     the differences.
    /// </summary>
    [Fact]
    public void RowKinds_ExpansionRow_HidesCheckboxAndToggleAndIsNotEditable()
    {
        var vm = new GatherRowVm(
            Row(Ref(1, 1), BindingRole.Step, isExpansion: true), true);

        Assert.Equal(Visibility.Hidden, vm.IncludeCheckboxVisibility);
        Assert.Equal(Visibility.Collapsed, vm.RoleToggleVisibility);
        Assert.Equal(Visibility.Visible, vm.RoleStaticVisibility);
        Assert.False(vm.IsNameEditable);
        Assert.Equal("step", vm.Role);
    }

    [Fact]
    public void RowKinds_OrphanRow_HidesCheckboxAndToggleAndIsNotEditable()
    {
        var vm = new GatherRowVm(Row(Ref(1, 1), canToggleRole: true), true, "B2");

        Assert.Equal(Visibility.Hidden, vm.IncludeCheckboxVisibility);
        // CanToggleRole is true on the underlying row, but an orphan is inert.
        Assert.Equal(Visibility.Collapsed, vm.RoleToggleVisibility);
        Assert.Equal(Visibility.Visible, vm.RoleStaticVisibility);
        Assert.False(vm.IsNameEditable);
        Assert.Equal("orphaned by B2", vm.OrphanHintText);
    }

    [Fact]
    public void RowKinds_SliceRow_ShowsCheckboxHidesToggleAndIsEditable()
    {
        var vm = new GatherRowVm(
            Row(Ref(2, 1), name: "second", rhs: "INDEX(arr,1,2)", sliceOf: Ref(1, 1, true)),
            true);

        Assert.Equal(Visibility.Visible, vm.IncludeCheckboxVisibility);
        Assert.Equal(Visibility.Collapsed, vm.RoleToggleVisibility);
        Assert.Equal(Visibility.Visible, vm.RoleStaticVisibility);
        Assert.True(vm.IsNameEditable);
        Assert.Equal("slice", vm.Role);
    }

    [Fact]
    public void RowKinds_OrdinaryStepRow_ShowsCheckboxAndToggle()
    {
        var vm = new GatherRowVm(
            Row(Ref(1, 1), BindingRole.Step, canToggleRole: true), true);

        Assert.Equal(Visibility.Visible, vm.IncludeCheckboxVisibility);
        Assert.Equal(Visibility.Visible, vm.RoleToggleVisibility);
        Assert.Equal(Visibility.Collapsed, vm.RoleStaticVisibility);
        Assert.True(vm.IsNameEditable);
        Assert.Equal("step", vm.Role);
    }

    // ---- The straddle marker survives a snapshot round-trip ----

    [Fact]
    public void StraddleMarker_RendersWithAnchorNamingTooltip()
    {
        var vm = new GatherRowVm(
            Row(Range(1, 1, 2, 3), straddles: Cell("Sheet1", 1, 2)), true);

        Assert.True(vm.IsStraddlingSpill);
        Assert.Equal("A2", vm.StraddledSpillAnchorAddress);
        Assert.Equal(Visibility.Visible, vm.StraddleWarningVisibility);
        Assert.Contains("A2", vm.StraddleWarningText, StringComparison.Ordinal);
    }

    [Fact]
    public void StraddleMarker_AbsentOnOrdinaryRow()
    {
        var vm = new GatherRowVm(Row(Range(1, 1, 2, 3)), true);

        Assert.False(vm.IsStraddlingSpill);
        Assert.Equal(Visibility.Collapsed, vm.StraddleWarningVisibility);
        Assert.Equal("", vm.StraddleWarningText);
    }

    /// <summary>
    ///     The routed finding from PR #369's review: unticking a straddling
    ///     range row snapshotted it without <c>StraddlesSpillAnchor</c>, so the
    ///     amber marker vanished until the row was re-ticked. The snapshot now
    ///     round-trips every display-affecting field.
    /// </summary>
    [Fact]
    public void Snapshot_PreservesStraddleAnchor()
    {
        var anchor = Cell("Sheet1", 1, 2);
        var vm = new GatherRowVm(Row(Range(1, 1, 2, 3), straddles: anchor), true);

        var snapshot = GatherWindow.Snapshot(vm);
        var rebuilt = new GatherRowVm(snapshot, false);

        Assert.Equal(anchor, snapshot.StraddlesSpillAnchor);
        Assert.True(rebuilt.IsStraddlingSpill);
        Assert.Equal(Visibility.Visible, rebuilt.StraddleWarningVisibility);
    }

    [Fact]
    public void Snapshot_PreservesEveryDisplayAffectingField()
    {
        var sliceOf = Ref(1, 2, true);
        var original = Row(
            Ref(2, 2),
            BindingRole.Input,
            "second",
            "INDEX(arr,1,2)",
            isExpansion: true,
            canToggleRole: true,
            sliceOf: sliceOf,
            straddles: Cell("Sheet1", 1, 2));

        var snapshot = GatherWindow.Snapshot(new GatherRowVm(original, true));

        Assert.Equal(original, snapshot);
    }
}
