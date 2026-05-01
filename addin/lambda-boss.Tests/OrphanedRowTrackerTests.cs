using LambdaBoss;
using LambdaBoss.UI;

using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Unit tests for <see cref="OrphanedRowTracker" />, the dialog's
///     bookkeeping for the orphaned-rows section. The tracker is pure
///     state — no engine or WPF coupling — so the diff logic between
///     before/after Recompute snapshots is exercisable directly. The
///     companion engine tests (GatherEngineTests) cover the underlying
///     orphan behaviour; these tests just verify that the dialog
///     records and clears orphans correctly across the user's
///     toggling actions.
/// </summary>
public sealed class OrphanedRowTrackerTests
{
    [Fact]
    public void Reconcile_DemoteCausesPrecedentToDrop_RecordsOrphan()
    {
        // Active before: A1, B1. After demoting B1 → A1 drops out
        // (only reachable via B1). Tracker records A1 as orphaned-by-B1.
        var a1 = Cell("A1");
        var b1 = Cell("B1");

        var before = new Dictionary<FormulaRef, BindingRow>
        {
            [a1] = Row(a1, BindingRole.Input, "numbers", "A1"),
            [b1] = Row(b1, BindingRole.Step, "step_1", "numbers*2"),
        };
        var after = new HashSet<FormulaRef> { b1 };
        var excluded = new HashSet<FormulaRef>();

        var tracker = new OrphanedRowTracker();
        tracker.Reconcile(before, after, excluded, demotedCell: b1);

        Assert.True(tracker.HasOrphans);
        Assert.Single(tracker.Orphans);
        var orphan = tracker.Orphans[a1];
        Assert.Equal(b1, orphan.DemotedBy);
        Assert.Equal("numbers", orphan.Snapshot.Name);
        Assert.Equal("A1", orphan.Snapshot.Rhs);
    }

    [Fact]
    public void Reconcile_DemoteWithoutOrphans_RecordsNothing()
    {
        // Demoting a step whose precedents are still reachable via
        // another path produces no orphans — the precedent stays in
        // the active list, so the tracker has nothing to record.
        var a1 = Cell("A1");
        var b1 = Cell("B1");
        var c1 = Cell("C1");

        var before = new Dictionary<FormulaRef, BindingRow>
        {
            [a1] = Row(a1, BindingRole.Input, "n", "A1"),
            [b1] = Row(b1, BindingRole.Step, "double", "n*2"),
            [c1] = Row(c1, BindingRole.Step, "another", "n+1"),
        };
        // After demoting B1, A1 still reachable via C1's reference, so
        // it stays active. Only B1's role flipped.
        var after = new HashSet<FormulaRef> { a1, b1, c1 };
        var excluded = new HashSet<FormulaRef>();

        var tracker = new OrphanedRowTracker();
        tracker.Reconcile(before, after, excluded, demotedCell: b1);

        Assert.False(tracker.HasOrphans);
    }

    [Fact]
    public void Reconcile_DemotedCellItself_IsNotOrphaned()
    {
        // The demoted cell stays as a binding (input role, RHS = cell-
        // ref) — it's not an orphan even though its old step-shaped
        // snapshot might look "dropped" if you only diff active sets.
        var a1 = Cell("A1");
        var b1 = Cell("B1");

        var before = new Dictionary<FormulaRef, BindingRow>
        {
            [a1] = Row(a1, BindingRole.Input, "numbers", "A1"),
            [b1] = Row(b1, BindingRole.Step, "step_1", "numbers*2"),
        };
        // Pretend after Recompute, B1 is in the active list as input.
        var after = new HashSet<FormulaRef> { b1 };
        var excluded = new HashSet<FormulaRef>();

        var tracker = new OrphanedRowTracker();
        tracker.Reconcile(before, after, excluded, demotedCell: b1);

        Assert.DoesNotContain(b1, tracker.Orphans);
        // A1 dropped — it's the orphan.
        Assert.Contains(a1, tracker.Orphans);
    }

    [Fact]
    public void Reconcile_ExcludedCellThatDropsOnDemote_IsNotOrphaned()
    {
        // A cell the user already excluded gets its excluded-row
        // treatment (greyed snapshot, re-tickable). When a later demote
        // happens to also drop it from the active set, the tracker
        // skips it — it's already accounted for in the excluded lane.
        var a1 = Cell("A1");
        var b1 = Cell("B1");

        var before = new Dictionary<FormulaRef, BindingRow>
        {
            [b1] = Row(b1, BindingRole.Step, "step_1", "A1*2"),
        };
        var after = new HashSet<FormulaRef> { b1 };
        var excluded = new HashSet<FormulaRef> { a1 };

        var tracker = new OrphanedRowTracker();
        tracker.Reconcile(before, after, excluded, demotedCell: b1);

        Assert.False(tracker.HasOrphans);
    }

    [Fact]
    public void Reconcile_NonDemoteRecompute_DoesNotAddOrphans()
    {
        // A Recompute with no demotedCell (e.g. user toggled Include or
        // promoted) shouldn't add orphan entries even if the active set
        // shrank. Promote-driven shrinkage doesn't happen in practice
        // (promote can only add precedents) but the contract is still:
        // "no demote ⇒ no new orphans."
        var a1 = Cell("A1");
        var b1 = Cell("B1");

        var before = new Dictionary<FormulaRef, BindingRow>
        {
            [a1] = Row(a1, BindingRole.Input, "n", "A1"),
            [b1] = Row(b1, BindingRole.Step, "step_1", "n*2"),
        };
        var after = new HashSet<FormulaRef> { b1 };
        var excluded = new HashSet<FormulaRef>();

        var tracker = new OrphanedRowTracker();
        tracker.Reconcile(before, after, excluded, demotedCell: null);

        Assert.False(tracker.HasOrphans);
    }

    [Fact]
    public void Reconcile_OrphanResurfacesInActiveSet_IsDropped()
    {
        // An orphan that's been recorded should be dropped if a later
        // Recompute surfaces its cell again in the active set — its
        // "orphaned by X" label no longer reflects reality.
        var a1 = Cell("A1");
        var b1 = Cell("B1");

        var tracker = new OrphanedRowTracker();
        var before1 = new Dictionary<FormulaRef, BindingRow>
        {
            [a1] = Row(a1, BindingRole.Input, "n", "A1"),
            [b1] = Row(b1, BindingRole.Step, "step_1", "n*2"),
        };
        tracker.Reconcile(before1, new HashSet<FormulaRef> { b1 }, new HashSet<FormulaRef>(), b1);
        Assert.True(tracker.HasOrphans);

        // Some later Recompute surfaces A1 again (e.g. user promoted
        // some other cell whose formula refs A1). Tracker should drop
        // the orphan record.
        var before2 = new Dictionary<FormulaRef, BindingRow>
        {
            [b1] = Row(b1, BindingRole.Input, "step_1", "B1"),
        };
        tracker.Reconcile(
            before2,
            new HashSet<FormulaRef> { a1, b1 },
            new HashSet<FormulaRef>(),
            demotedCell: null);

        Assert.False(tracker.HasOrphans);
    }

    [Fact]
    public void OnPromote_DropsOrphansAttributedToTheCell()
    {
        // Demote B1 → A1 orphans. Then promote B1 — A1 should clear
        // from the orphan list (the engine will surface A1 again on
        // the subsequent Recompute, but the tracker drops its own
        // entry up-front so the dialog's reconcile pass doesn't see
        // a stale orphan during the rebuild).
        var a1 = Cell("A1");
        var b1 = Cell("B1");

        var tracker = new OrphanedRowTracker();
        var before = new Dictionary<FormulaRef, BindingRow>
        {
            [a1] = Row(a1, BindingRole.Input, "n", "A1"),
            [b1] = Row(b1, BindingRole.Step, "step_1", "n*2"),
        };
        tracker.Reconcile(before, new HashSet<FormulaRef> { b1 }, new HashSet<FormulaRef>(), b1);
        Assert.True(tracker.HasOrphans);

        tracker.OnPromote(b1);

        Assert.False(tracker.HasOrphans);
    }

    [Fact]
    public void OnPromote_LeavesOrphansFromOtherDemotersIntact()
    {
        // Two independent demotes orphan two different cells. Promoting
        // only one demoter clears its own orphans without disturbing
        // the others.
        var a1 = Cell("A1");
        var b1 = Cell("B1");
        var c1 = Cell("C1");
        var d1 = Cell("D1");

        var tracker = new OrphanedRowTracker();
        // Demote 1: B1 demoted, A1 orphans.
        tracker.Reconcile(
            new Dictionary<FormulaRef, BindingRow>
            {
                [a1] = Row(a1, BindingRole.Input, "a", "A1"),
                [b1] = Row(b1, BindingRole.Step, "step_b", "a*2"),
            },
            new HashSet<FormulaRef> { b1 },
            new HashSet<FormulaRef>(),
            b1);
        // Demote 2: D1 demoted, C1 orphans.
        tracker.Reconcile(
            new Dictionary<FormulaRef, BindingRow>
            {
                [c1] = Row(c1, BindingRole.Input, "c", "C1"),
                [d1] = Row(d1, BindingRole.Step, "step_d", "c+1"),
            },
            new HashSet<FormulaRef> { d1, b1 },
            new HashSet<FormulaRef>(),
            d1);
        Assert.Equal(2, tracker.Orphans.Count);

        tracker.OnPromote(b1);

        Assert.Single(tracker.Orphans);
        Assert.Contains(c1, tracker.Orphans);
    }

    [Fact]
    public void Reconcile_UnrelatedRoleChangeAfterDemote_PreservesOrphan()
    {
        // After a demote orphans A1, an unrelated role change shouldn't
        // unintentionally clear A1 from the orphan list. The acceptance
        // criteria call this out explicitly: changing one row's role
        // doesn't drop another row's orphan-by attribution.
        var a1 = Cell("A1");
        var b1 = Cell("B1");
        var c1 = Cell("C1");
        var d1 = Cell("D1");

        var tracker = new OrphanedRowTracker();
        // Demote B1 → A1 orphans.
        tracker.Reconcile(
            new Dictionary<FormulaRef, BindingRow>
            {
                [a1] = Row(a1, BindingRole.Input, "a", "A1"),
                [b1] = Row(b1, BindingRole.Step, "b", "a*2"),
                [c1] = Row(c1, BindingRole.Input, "c", "C1"),
                [d1] = Row(d1, BindingRole.Step, "d", "c+1"),
            },
            new HashSet<FormulaRef> { b1, c1, d1 },
            new HashSet<FormulaRef>(),
            b1);
        Assert.True(tracker.HasOrphans);
        Assert.Contains(a1, tracker.Orphans);

        // Now demote D1 → C1 orphans. A1 should still be in orphans.
        tracker.Reconcile(
            new Dictionary<FormulaRef, BindingRow>
            {
                [b1] = Row(b1, BindingRole.Input, "b", "B1"),
                [c1] = Row(c1, BindingRole.Input, "c", "C1"),
                [d1] = Row(d1, BindingRole.Step, "d", "c+1"),
            },
            new HashSet<FormulaRef> { b1, d1 },
            new HashSet<FormulaRef>(),
            d1);

        Assert.Equal(2, tracker.Orphans.Count);
        Assert.Contains(a1, tracker.Orphans);
        Assert.Contains(c1, tracker.Orphans);
        Assert.Equal(b1, tracker.Orphans[a1].DemotedBy);
        Assert.Equal(d1, tracker.Orphans[c1].DemotedBy);
    }

    [Fact]
    public void Reconcile_AlreadyTrackedOrphan_IsNotReattributed()
    {
        // A cell already attributed to demoter X shouldn't get re-
        // attributed to a later demoter Y just because Y's Recompute
        // happens to also drop it from the active list. First-write
        // wins — the original demote is the cause of record.
        var a1 = Cell("A1");
        var b1 = Cell("B1");
        var c1 = Cell("C1");

        var tracker = new OrphanedRowTracker();
        // Demote B1 → A1 orphans (attributed to B1).
        tracker.Reconcile(
            new Dictionary<FormulaRef, BindingRow>
            {
                [a1] = Row(a1, BindingRole.Input, "a", "A1"),
                [b1] = Row(b1, BindingRole.Step, "b", "a*2"),
            },
            new HashSet<FormulaRef> { b1 },
            new HashSet<FormulaRef>(),
            b1);
        Assert.Equal(b1, tracker.Orphans[a1].DemotedBy);

        // Now demote C1 — A1 still doesn't reappear in the active list
        // (it's still orphaned). Second Reconcile shouldn't re-attribute
        // A1 to C1.
        tracker.Reconcile(
            new Dictionary<FormulaRef, BindingRow>
            {
                [b1] = Row(b1, BindingRole.Input, "b", "B1"),
                [c1] = Row(c1, BindingRole.Step, "c", "B1+1"),
            },
            new HashSet<FormulaRef> { b1, c1 },
            new HashSet<FormulaRef>(),
            c1);

        Assert.Equal(b1, tracker.Orphans[a1].DemotedBy);
    }

    private static FormulaRef Cell(string a1)
    {
        var i = 0;
        while (i < a1.Length && char.IsLetter(a1[i])) i++;
        var col = CellRef.LettersToColumn(a1[..i]);
        var row = int.Parse(a1[i..]);
        return new FormulaRef(new CellRef("Sheet1", col, row));
    }

    private static BindingRow Row(FormulaRef source, BindingRole role, string name, string rhs) =>
        new(source, role, name, rhs, IsExpansion: false, CanToggleRole: true);
}
