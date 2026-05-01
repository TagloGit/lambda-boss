namespace LambdaBoss.UI;

/// <summary>
///     A row that the engine no longer surfaces because its only path
///     into the LET ran through a cell the user demoted to input. The
///     dialog renders these in a separate "Orphaned" section beneath
///     the active bindings so a demote doesn't silently drop precedents
///     from view (mirrors the excluded-row pattern from PR 10).
///     <see cref="Snapshot" /> carries the cell's last-known
///     <see cref="BindingRow" /> shape (Address, Role, Name, Rhs) for
///     display; <see cref="DemotedBy" /> identifies the cell whose
///     demote orphaned this row, used both to compose the
///     "orphaned by &lt;addr&gt;" hint and to drop the orphan when the
///     user re-promotes that cell.
/// </summary>
internal sealed record OrphanedRow(BindingRow Snapshot, FormulaRef DemotedBy);

/// <summary>
///     Bookkeeping for the orphaned-rows section in
///     <see cref="GatherWindow" />. Tracks which cells the user lost from
///     view by demoting a step (precedents reachable only via the
///     demoted cell), so the dialog can keep them visible in a separate
///     read-only list rather than letting them disappear silently.
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
///             pre/post active sets and (if the trigger was a demote)
///             the demoted cell. New orphans are recorded; orphans that
///             reappeared in the active set are dropped.
///         </item>
///         <item>
///             On a promote, call <see cref="OnPromote" /> to drop any
///             orphans the now-promoted cell originally caused.
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
    ///     Drop every orphan that was caused by the user demoting
    ///     <paramref name="promotedCell" />. Called when the user flips
    ///     that same cell's role back from input to step — the precedents
    ///     it was hiding will surface naturally on the next Recompute,
    ///     so they shouldn't keep their orphan-row treatment.
    /// </summary>
    public void OnPromote(FormulaRef promotedCell)
    {
        var keys = new List<FormulaRef>();
        foreach (var (key, entry) in _orphaned)
            if (entry.DemotedBy.Equals(promotedCell))
                keys.Add(key);
        foreach (var k in keys)
            _orphaned.Remove(k);
    }

    /// <summary>
    ///     Reconcile orphan tracking with the result of a Recompute.
    ///     Always drops orphans whose cell reappeared in
    ///     <paramref name="activeAfter" /> (the engine surfaced them
    ///     again, e.g. after another role change opened a new path
    ///     into the cell). When <paramref name="demotedCell" /> is
    ///     non-null (the Recompute was triggered by a demote), records
    ///     each cell that was active before but isn't now — and isn't
    ///     excluded, isn't the demoted cell itself, and isn't already
    ///     tracked — as a new orphan, with its
    ///     <see cref="OrphanedRow.Snapshot" /> drawn from
    ///     <paramref name="activeBefore" />.
    /// </summary>
    public void Reconcile(
        IReadOnlyDictionary<FormulaRef, BindingRow> activeBefore,
        IReadOnlySet<FormulaRef> activeAfter,
        IReadOnlySet<FormulaRef> excluded,
        FormulaRef? demotedCell)
    {
        // Drop orphans that the engine resurfaced. A row reappears when
        // some unrelated change (e.g. promoting another cell that
        // restored a path into it) opens a new route into the LET; the
        // orphan-by-X label no longer reflects reality once the row is
        // back in active rotation.
        foreach (var src in activeAfter)
            _orphaned.Remove(src);

        if (demotedCell == null)
            return;

        foreach (var (src, snap) in activeBefore)
        {
            if (activeAfter.Contains(src)) continue;
            // Excluded cells already have their own visual treatment
            // (the excluded-row snapshot lane); they're not orphans.
            if (excluded.Contains(src)) continue;
            // The demoted cell itself stays as a binding (now an input
            // RHS = cell-ref) — it's the one row the demote keeps
            // visible by design, not an orphan.
            if (src.Equals(demotedCell)) continue;
            // First-write wins. A cell already attributed to a
            // previous demoter shouldn't get re-attributed by a fresh
            // demote that didn't actually cause this drop.
            if (_orphaned.ContainsKey(src)) continue;
            _orphaned[src] = new OrphanedRow(snap, demotedCell);
        }
    }
}
