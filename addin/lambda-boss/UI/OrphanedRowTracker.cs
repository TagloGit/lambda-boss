namespace LambdaBoss.UI;

/// <summary>
///     A row that the engine no longer surfaces because its only path
///     into the LET ran through a cell the user demoted to input or
///     excluded. The dialog renders these in a separate "Orphaned"
///     section beneath the active bindings so the drop doesn't pass
///     silently (mirrors the excluded-row pattern from PR 10).
///     <see cref="Snapshot" /> carries the cell's last-known
///     <see cref="BindingRow" /> shape (Address, Role, Name, Rhs) for
///     display; <see cref="CausedBy" /> identifies the cell whose
///     demote-or-exclude orphaned this row, used both to compose the
///     "orphaned by &lt;addr&gt;" hint and to drop the orphan when the
///     user reverses that action (promote a demoted cell, re-include
///     an excluded one).
/// </summary>
internal sealed record OrphanedRow(BindingRow Snapshot, FormulaRef CausedBy);

/// <summary>
///     Bookkeeping for the orphaned-rows section in
///     <see cref="GatherWindow" />. Tracks which cells the user lost
///     from view by demoting a step to input, or by excluding a step
///     entirely — both actions drop precedents that were only reachable
///     via the affected cell. The dialog uses this to keep the dropped
///     rows visible in a separate read-only list rather than letting
///     them disappear silently.
///     This class is pure state — no WPF or engine coupling — so the
///     diff logic is exercisable without spinning up a dialog. The
///     dialog wires it into <see cref="GatherEngine.Recompute" />:
///     <list type="bullet">
///         <item>
///             Before each Recompute, capture the active rows'
///             <see cref="BindingRow" /> snapshots.
///         </item>
///         <item>
///             After Recompute, call <see cref="Reconcile" /> with the
///             pre/post active sets and (if the trigger was a demote
///             or an exclude on an active row) the cell whose action
///             caused the drop. New orphans are recorded; orphans that
///             reappeared in the active set are dropped.
///         </item>
///         <item>
///             On a promote or re-include of a previously orphan-
///             causing cell, call <see cref="Forget" /> to drop any
///             orphans attributed to it.
///         </item>
///     </list>
/// </summary>
internal sealed class OrphanedRowTracker
{
    private readonly Dictionary<FormulaRef, OrphanedRow> _orphaned = new();

    /// <summary>The current orphan rows, keyed by their cell ref.</summary>
    public IReadOnlyDictionary<FormulaRef, OrphanedRow> Orphans => _orphaned;

    /// <summary>True if any orphan rows are currently tracked.</summary>
    public bool HasOrphans => _orphaned.Count > 0;

    /// <summary>
    ///     Drop every orphan attributed to <paramref name="cell" />.
    ///     Called when the user reverses the action that originally
    ///     caused those orphans — promoting a demoted cell back to a
    ///     step, or re-including an excluded cell. Done up-front so
    ///     the dialog's reconcile pass during the rebuild doesn't see
    ///     a stale "orphaned by" record while the engine is still
    ///     surfacing the formerly-orphaned cell back into the active
    ///     list. Safe to call on cells that don't have orphans
    ///     attributed (no-op).
    /// </summary>
    public void Forget(FormulaRef cell)
    {
        var keys = new List<FormulaRef>();
        foreach (var (key, entry) in _orphaned)
            if (entry.CausedBy.Equals(cell))
                keys.Add(key);
        foreach (var k in keys)
            _orphaned.Remove(k);
    }

    /// <summary>
    ///     Reconcile orphan tracking with the result of a Recompute.
    ///     Always drops orphans whose cell reappeared in
    ///     <paramref name="activeAfter" /> (the engine surfaced them
    ///     again, e.g. after another role/include change opened a new
    ///     path into the cell). When <paramref name="causedBy" /> is
    ///     non-null (the Recompute was triggered by the user demoting
    ///     or excluding that cell), records each cell that was active
    ///     before but isn't now — and isn't excluded, isn't the
    ///     causing cell itself, and isn't already tracked — as a new
    ///     orphan, with its <see cref="OrphanedRow.Snapshot" /> drawn
    ///     from <paramref name="activeBefore" />.
    /// </summary>
    public void Reconcile(
        IReadOnlyDictionary<FormulaRef, BindingRow> activeBefore,
        ISet<FormulaRef> activeAfter,
        ISet<FormulaRef> excluded,
        FormulaRef? causedBy)
    {
        // Drop orphans that the engine resurfaced. A row reappears when
        // some unrelated change (e.g. promoting another cell that
        // restored a path into it) opens a new route into the LET; the
        // orphan-by-X label no longer reflects reality once the row is
        // back in active rotation.
        foreach (var src in activeAfter)
            _orphaned.Remove(src);

        if (causedBy == null)
            return;

        foreach (var (src, snap) in activeBefore)
        {
            if (activeAfter.Contains(src)) continue;
            // Excluded cells already have their own visual treatment
            // (the excluded-row snapshot lane); they're not orphans.
            if (excluded.Contains(src)) continue;
            // The causing cell stays visible by design — demoted to
            // input in the bindings list, or muted in the excluded
            // lane — not an orphan.
            if (src.Equals(causedBy)) continue;
            // First-write wins. A cell already attributed to a
            // previous causer shouldn't get re-attributed by a fresh
            // demote/exclude that didn't actually cause this drop.
            if (_orphaned.ContainsKey(src)) continue;
            _orphaned[src] = new OrphanedRow(snap, causedBy);
        }
    }
}
