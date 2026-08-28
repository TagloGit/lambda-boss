using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LambdaBoss.UI;

/// <summary>
///     Display of a <see cref="GatherResult" /> with the Include checkbox
///     column (PR 10), the role toggle (PR 11), and live-editable binding
///     names (PR 12). Toggling Include or Role calls back into
///     <see cref="GatherEngine.Recompute" /> via <see cref="_recompute" />
///     and rebuilds the row list: re-included rows return as the engine
///     surfaces them, explicitly-excluded rows are kept visible (greyed
///     out, checkbox off) so the user can re-tick them, and rows the
///     engine no longer surfaces because their only path ran through a
///     cell the user demoted or excluded appear in a separate "Orphaned"
///     section beneath the active bindings (issue #157, mirrors the
///     excluded-row pattern). Editing a name takes a different path:
///     <see cref="RecomputeForNameOnly" /> runs the engine but updates
///     each existing VM's <see cref="GatherRowVm.Rhs" /> in place rather
///     than rebuilding the row list, so the focused TextBox keeps its
///     caret position while the user types. Role overrides persist
///     across rebuilds via <see cref="_roleOverrides" /> and name
///     overrides via <see cref="_nameOverrides" />, so the user's
///     choices stay in effect through subsequent unrelated toggles.
///     Names are validated live: invalid (non-canonical or colliding)
///     names disable the Save button and freeze the preview at the
///     last valid state. Save returns the synthesised LET text from
///     the latest valid result so the caller writes that back to the
///     sink.
/// </summary>
public partial class GatherWindow
{
    private const string DefaultStatusText =
        "Save writes the LET into the sink cell · Esc to cancel";

    private const string InvalidNameStatusText =
        "Fix invalid names before saving";

    // Snapshots of explicitly-excluded rows so they can re-appear in the
    // visible list after a Recompute (which only returns included
    // bindings). Stored as plain BindingRow data — no VM subscriptions to
    // manage across rebuilds. Re-checking removes the entry; the engine's
    // result is the source of truth for re-included rows.
    private readonly Dictionary<FormulaRef, BindingRow> _excluded = [];

    // Name overrides — what the user has typed into the editable Name
    // column (PR 12). The engine consumes only valid entries (dialog-
    // side validation gates pass-through), but the dictionary itself
    // mirrors the TextBox state including any in-progress invalid edit;
    // a rebuild reads the dict to seed the new VM's Name regardless of
    // validity, so the user's typed text survives unrelated Include or
    // Role toggles. An empty string is removed instead of stored: an
    // empty override is meaningless to the engine (no name = no
    // binding), and persisting it would muddle the "this row has been
    // user-edited" signal.
    private readonly Dictionary<FormulaRef, string> _nameOverrides = [];

    private readonly ObservableCollection<GatherRowVm> _orphans = [];

    // Orphan tracker: precedents that fell out of the active list because
    // their only path ran through a cell the user demoted or excluded.
    // Surfaced in the Orphaned section below the bindings list and
    // removed when the user reverses the causing action — re-includes
    // an excluded cell, or promotes a demoted one back to a step.
    private readonly OrphanedRowTracker _orphanTracker = new();

    private readonly Func<IReadOnlyList<RowState>, GatherResult?> _recompute;

    // Role overrides persist across rebuilds: when the user demotes B1 to
    // input, that choice survives an unrelated checkbox toggle on A1
    // (which would otherwise re-run the engine without the override and
    // restore B1 to its natural classification). The dialog re-injects
    // overrides into every Recompute so the engine sees a consistent
    // view of user intent.
    private readonly Dictionary<FormulaRef, BindingRole> _roleOverrides = [];

    private readonly ObservableCollection<GatherRowVm> _rows = [];

    private GatherResult _result;

    // Reentrancy guard: rebuilding the row list during a Recompute fires
    // INotifyPropertyChanged on each VM, which would re-enter the change
    // handler and trigger another Recompute. The guard short-circuits the
    // nested calls so a single user toggle yields exactly one Recompute.
    private bool _suppressRecompute;

    public GatherWindow(
        GatherResult initial,
        Func<IReadOnlyList<RowState>, GatherResult?> recompute)
    {
        InitializeComponent();
        _result = initial;
        _recompute = recompute;

        WalkHintText.Text = BuildWalkHint(initial);
        SinkAddressText.Text = initial.Sink.A1Address;
        OriginalFormulaText.Text = initial.OriginalFormula;
        PreviewText.Text = initial.SynthesisedLet;

        BuildRowsFromBindings(initial.Bindings);
        BindingsList.ItemsSource = _rows;
        OrphansList.ItemsSource = _orphans;
    }

    /// <summary>Populated on Save; null when cancelled.</summary>
    public string? SavedFormula { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SavedFormula = _result.SynthesisedLet;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SavedFormula = null;
        DialogResult = false;
        Close();
    }

    /// <summary>
    ///     Renders the header hint per spec 0005 §"Selection-restricted
    ///     walk". A free walk (single-cell selection, or a multi-selection
    ///     that happened to cover every walked cell) reads
    ///     <c>Walking N cells from &lt;addr&gt;</c>; a restriction that
    ///     actually narrowed the walk reads
    ///     <c>Walking M of N cells from &lt;addr&gt; — restricted by selection</c>.
    ///     The "M == N" case is treated as a free walk in the header even
    ///     when the selection was multi-cell, matching the issue's "behaves
    ///     like a free walk" wording — restricting and then happening to
    ///     cover everything is observationally indistinguishable from a
    ///     free walk and showing "N of N" reads as noise. The hint stays
    ///     stable across Include toggles — we anchor on the initial walk
    ///     so the user has one consistent reference point rather than a
    ///     count that drifts on every checkbox click.
    /// </summary>
    private static string BuildWalkHint(GatherResult result)
    {
        var addr = result.Sink.A1Address;
        var n = result.FreeWalkCount;
        var m = result.WalkedCount;
        if (m == n)
            return $"Walking {n} cells from {addr}";
        return $"Walking {m} of {n} cells from {addr} — restricted by selection";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SaveButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    /// <summary>
    ///     Selects the entire Name when the TextBox gains focus — covers
    ///     Tab navigation between rows so the user can immediately
    ///     overtype each name without backspacing. The mouse-click path
    ///     is hooked separately via
    ///     <see cref="NameTextBox_PreviewMouseLeftButtonDown" /> because
    ///     a click would otherwise position the caret and clear the
    ///     selection right after this handler ran.
    /// </summary>
    private void NameTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.SelectAll();
    }

    /// <summary>
    ///     First click into a TextBox that doesn't yet have keyboard
    ///     focus: hijack it to call <c>Focus()</c> ourselves and mark
    ///     the event handled, so the default click-to-position-caret
    ///     behavior never runs and the
    ///     <see cref="NameTextBox_GotFocus" /> SelectAll sticks.
    ///     Subsequent clicks (when the box already has focus) fall
    ///     through normally so the user can place the caret to edit
    ///     part of the name.
    /// </summary>
    private void NameTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.IsKeyboardFocusWithin) return;
        tb.Focus();
        e.Handled = true;
    }

    private void BuildRowsFromBindings(IReadOnlyList<BindingRow> bindings)
    {
        _suppressRecompute = true;
        try
        {
            foreach (var v in _rows)
                v.PropertyChanged -= Row_PropertyChanged;
            _rows.Clear();
            // Orphan rows are inert (no checkbox, no toggle) so they
            // never raise PropertyChanged events — clearing the list
            // is enough.
            _orphans.Clear();

            var surfaced = new HashSet<FormulaRef>();
            foreach (var binding in bindings)
            {
                var vm = new GatherRowVm(binding, true);
                // Restore non-canonical user-typed names across rebuilds.
                // Canonical overrides are already reflected in the engine's
                // binding.Name (the engine consumed them and possibly
                // collision-suffixed) so the constructor's
                // <c>vm.Name = binding.Name</c> already matches; non-
                // canonical overrides were filtered out by
                // <see cref="BuildRowStates" /> so the engine produced an
                // auto-derived name instead, and we re-apply the user's
                // typed text here so they can see what they wrote and
                // (with the red border) why Save is disabled. Inner-LET
                // expansions share Source with their host and are skipped.
                if (!binding.IsExpansion && HasNonCanonicalOverride(binding.Source))
                    vm.Name = _nameOverrides[binding.Source];
                vm.PropertyChanged += Row_PropertyChanged;
                _rows.Add(vm);
                surfaced.Add(binding.Source);
            }

            // Explicitly-excluded rows that the engine didn't surface
            // appear at the bottom of the bindings list so the user can
            // find them to re-tick. An excluded ref the engine still
            // surfaces (e.g. an inner-expansion row sharing a host cell
            // that's still in the result) is suppressed here — surfacing
            // once avoids duplicate rows for a single Source.
            foreach (var (source, snapshot) in _excluded)
            {
                if (surfaced.Contains(source)) continue;
                var vm = new GatherRowVm(snapshot, false);
                vm.PropertyChanged += Row_PropertyChanged;
                _rows.Add(vm);
            }

            // Orphaned rows go into a separate section. Filtering rules
            // mirror the excluded lane: skip rows the engine just
            // surfaced (the orphan came back) and rows the user has
            // also explicitly excluded (excluded-row treatment wins —
            // the user's deliberate choice trumps the implicit drop).
            foreach (var (source, entry) in _orphanTracker.Orphans)
            {
                if (surfaced.Contains(source)) continue;
                if (_excluded.ContainsKey(source)) continue;
                var hint = entry.CausedBy.Start.A1Address;
                var vm = new GatherRowVm(entry.Snapshot, true, hint);
                _orphans.Add(vm);
            }
        }
        finally
        {
            _suppressRecompute = false;
        }

        // Run validation outside the suppress block so each VM's
        // IsNameValid lands with the right value (and so a non-canonical
        // typed-name restored from <see cref="_nameOverrides" /> shows
        // its red border immediately on rebuild).
        UpdateSaveButtonEnabled(RevalidateNames());

        OrphansSection.Visibility =
            _orphans.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Spec 0010: the frozen-geometry caveat is stated once, under the
        // list, and only when there is a slice row to which it applies —
        // a gather with no spill in it should carry no spill wording at all.
        SliceNoteText.Visibility =
            HasSliceRow(_rows) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    ///     True when any row currently rendered in the bindings list is a
    ///     slice row — including an excluded one, which is still visible and
    ///     still subject to the fixed-position caveat the moment it is
    ///     re-ticked. Orphan rows live in their own collection and are
    ///     deliberately not scanned: they contribute nothing to the LET, so
    ///     the caveat does not apply to them. They <em>can</em> be slices —
    ///     unticking a spilling anchor cascades its slice rows out of the
    ///     bindings list and the tracker records each as orphaned by that
    ///     anchor — but the state is self-healing: re-ticking the anchor
    ///     forgets the orphans, returns the slices to the list, and runs
    ///     this check again.
    /// </summary>
    private static bool HasSliceRow(IEnumerable<GatherRowVm> rows)
    {
        foreach (var vm in rows)
            if (vm.IsSlice)
                return true;
        return false;
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressRecompute) return;
        if (sender is not GatherRowVm vm) return;

        if (e.PropertyName == nameof(GatherRowVm.Include))
        {
            // Snapshot when unchecking so the row stays visible for re-tick;
            // forget the snapshot when re-checking so the engine result
            // owns the row's display (Role/Name/Rhs may have shifted as
            // other rows toggled in the meantime).
            FormulaRef? causedBy;
            if (!vm.Include)
            {
                _excluded[vm.Source] = Snapshot(vm);
                // Excluding a step drops any precedents reachable only
                // via this cell — same upstream effect as a demote.
                // Pass the cell as the cause so the tracker records
                // those drops as orphans instead of letting them
                // vanish silently. Reconcile no-ops on excludes that
                // don't drop anything (leaves, or steps with no sole-
                // path upstream cells).
                causedBy = vm.Source;
            }
            else
            {
                _excluded.Remove(vm.Source);
                // Re-including the cell reverses the exclude; any
                // orphans we attributed to it should clear up-front so
                // the rebuild after Recompute doesn't render stale
                // orphan rows. No-op when the cell never caused any.
                _orphanTracker.Forget(vm.Source);
                // The just-re-included cell may still be unreachable
                // because some unrelated cell is currently demoted or
                // excluded and cuts the path. Pass that cell as
                // causedBy so the unsurfaced row lands in the orphan
                // section attributed to the actual blocker, instead
                // of vanishing. Reconcile no-ops when the cell does
                // surface (the diff is empty), so this is safe in the
                // common case.
                causedBy = FindCurrentBlocker();
            }

            Recompute(causedBy);
            return;
        }

        if (e.PropertyName == nameof(GatherRowVm.Name))
        {
            // Inner-LET expansions share Source with their host cell —
            // their TextBox is read-only so users never reach this path,
            // but defensively skip the override write so we don't
            // corrupt the host cell's override slot.
            if (vm.IsExpansion)
                return;

            // Mirror the TextBox into the override dict. Empty strings
            // drop the entry entirely so an empty override (which would
            // never be canonical) doesn't sit in the dictionary and clog
            // up <see cref="BuildRowStates" />'s filtering.
            if (string.IsNullOrEmpty(vm.Name))
                _nameOverrides.Remove(vm.Source);
            else
                _nameOverrides[vm.Source] = vm.Name;

            // Recompute always runs — non-canonical overrides are
            // filtered out by <see cref="BuildRowStates" /> so the
            // engine emits a valid LET regardless. Validation just
            // gates the Save button (and lights the row's red border)
            // because committing a LET that differs from the user's
            // typed text would surprise them.
            UpdateSaveButtonEnabled(RevalidateNames());
            RecomputeForNameOnly();
            return;
        }

        if (e.PropertyName == nameof(GatherRowVm.IsStep))
        {
            // The toggle's IsChecked drives the override, not the
            // engine's classification — record the user's chosen role
            // here so subsequent Recomputes (including unrelated
            // Include toggles) keep enforcing it. Setting an override
            // for a row that already matched its natural role is
            // harmless; the engine's defensive paths handle no-op
            // overrides without churn.
            var newRole = vm.IsStep ? BindingRole.Step : BindingRole.Input;
            _roleOverrides[vm.Source] = newRole;

            // If this row is currently marked excluded (snapshot in
            // _excluded), refresh that snapshot too so the cached row
            // carries the new role on its next re-render.
            if (_excluded.TryGetValue(vm.Source, out var existing))
                _excluded[vm.Source] = existing with { Role = newRole };

            // Promotion: drop any orphans this cell originally caused
            // (whether by a prior demote or a prior exclude). Done up-
            // front so the post-recompute reconcile pass doesn't see a
            // stale orphan record while the engine is still surfacing
            // the formerly-orphaned cell back into the active list. A
            // role flip on an excluded row is a snapshot-only edit
            // (the engine keeps treating the cell as excluded so the
            // override never takes effect) — we skip the tracker
            // hooks in that case so excluding a causer doesn't
            // accidentally lose its orphan attribution.
            FormulaRef? causedBy = null;
            if (vm.Include)
            {
                if (newRole == BindingRole.Step)
                    _orphanTracker.Forget(vm.Source);
                else
                    causedBy = vm.Source;
            }

            Recompute(causedBy);
        }
    }

    /// <summary>
    ///     Heuristic: pick any cell currently demoted-to-input or
    ///     excluded — those are the only user actions that can block a
    ///     path through the precedent graph. Used as the fallback
    ///     <c>causedBy</c> when re-including a cell, so a re-included
    ///     cell that still doesn't surface (because another override
    ///     cuts its only path) lands in the orphan section attributed
    ///     to whatever's plausibly blocking it. Returns the first
    ///     candidate found; when several blockers are in effect we
    ///     can't tell which one the user "really" meant, but the hint
    ///     just needs to point at *a* row above so the user knows
    ///     where to look — they can experiment to find the actual
    ///     cause. Returns null when nothing is currently overridden;
    ///     in that case a re-included cell that fails to surface
    ///     genuinely has no recoverable explanation, and falling out
    ///     of the UI is acceptable.
    /// </summary>
    private FormulaRef? FindCurrentBlocker()
    {
        foreach (var (src, role) in _roleOverrides)
            if (role == BindingRole.Input)
                return src;
        foreach (var src in _excluded.Keys)
            return src;
        return null;
    }

    /// <summary>
    ///     Rebuilds the plain <see cref="BindingRow" /> a VM was constructed
    ///     from, for the two places the dialog caches row data outside the
    ///     engine's result: the excluded-row snapshots (so an unticked row
    ///     stays renderable) and the pre-Recompute active snapshot the orphan
    ///     tracker diffs against. Every display-affecting field has to be
    ///     carried through — a single shared builder so a new
    ///     <c>BindingRow</c> field can't be forgotten on one of the two call
    ///     sites (which is exactly how the straddle marker went missing on
    ///     excluded rows).
    /// </summary>
    internal static BindingRow Snapshot(GatherRowVm vm)
    {
        return new BindingRow(
            vm.Source, vm.RoleEnum, vm.Name, vm.Rhs,
            vm.IsExpansion, vm.CanToggleRole, vm.SliceOf,
            vm.StraddlesSpillAnchor);
    }

    private void Recompute(FormulaRef? causedBy)
    {
        // Snapshot the active rows BEFORE the recompute. Used by the
        // orphan tracker to detect cells that fell off the active list
        // because of a demote or an exclude — diff'd against the new
        // active set returned by the engine. Skip expansion rows (they
        // share a Source with their host cell, so snapshotting them
        // would collide on the dictionary key) and excluded rows
        // (those have their own visual lane and are explicitly not
        // "active").
        var snapshotBefore = new Dictionary<FormulaRef, BindingRow>();
        foreach (var vm in _rows)
        {
            if (vm.IsExpansion) continue;
            if (!vm.Include) continue;
            if (snapshotBefore.ContainsKey(vm.Source)) continue;
            snapshotBefore[vm.Source] = Snapshot(vm);
        }

        var states = BuildRowStates();
        var newResult = _recompute(states);
        if (newResult == null || newResult.Diagnostic != null)
        {
            // Defensive: a Recompute shouldn't return null or a
            // diagnostic for a sink that gathered cleanly the first
            // time — exclusion only removes nodes. If it does (e.g. the
            // workbook changed under us), leave the dialog state as-is
            // and skip the rebuild.
            return;
        }

        var activeAfter = new HashSet<FormulaRef>(
            newResult.Bindings.Count);
        foreach (var b in newResult.Bindings)
            activeAfter.Add(b.Source);

        _orphanTracker.Reconcile(
            snapshotBefore,
            activeAfter,
            new HashSet<FormulaRef>(_excluded.Keys),
            causedBy);

        _result = newResult;
        PreviewText.Text = newResult.SynthesisedLet;
        BuildRowsFromBindings(newResult.Bindings);
    }

    /// <summary>
    ///     Builds the <see cref="RowState" /> list passed to the engine.
    ///     Inner-LET expansion rows are skipped because they share a
    ///     <see cref="GatherRowVm.Source" /> with their host cell — the
    ///     host's row owns Include/Role/Name, so passing the inner rows'
    ///     state would double-flag their Source. Orphan rows live in a
    ///     separate collection (<see cref="_orphans" />) and are inert,
    ///     so they don't appear here at all. Role and name overrides
    ///     are layered onto each row's Include flag so the engine sees
    ///     a single consistent view of user intent. Name overrides are
    ///     filtered to canonical entries — an in-progress invalid edit
    ///     in <see cref="_nameOverrides" /> is held back so the engine
    ///     never sees a non-identifier as an override; the caller of
    ///     <see cref="RecomputeForNameOnly" /> already gates on
    ///     all-valid, but this filter also protects unrelated
    ///     Include/Role <see cref="Recompute" /> calls when one row
    ///     happens to be mid-edit.
    /// </summary>
    private List<RowState> BuildRowStates()
    {
        var states = new List<RowState>(_rows.Count);
        var seen = new HashSet<FormulaRef>();
        foreach (var vm in _rows)
        {
            if (vm.IsExpansion) continue;
            if (!seen.Add(vm.Source)) continue;
            BindingRole? roleOverride = null;
            if (_roleOverrides.TryGetValue(vm.Source, out var o))
                roleOverride = o;
            string? nameOverride = null;
            if (_nameOverrides.TryGetValue(vm.Source, out var n)
                && GatherNameValidator.IsCanonical(n))
                nameOverride = n;
            states.Add(new RowState(vm.Source, vm.Include, roleOverride, nameOverride));
        }

        return states;
    }

    /// <summary>
    ///     Re-runs the engine and updates each editable row's Name and
    ///     Rhs in place rather than rebuilding the row VMs. Used for
    ///     live name editing so the focused TextBox keeps its caret
    ///     position while the user types — a full
    ///     <see cref="BuildRowsFromBindings" /> would tear down the
    ///     ItemsControl's children and lose focus on every keystroke.
    ///     Topology is stable across name-only changes (Include and Role
    ///     drive shape, not names), so the engine result's binding
    ///     sequence matches the existing engine-surfaced rows in
    ///     <see cref="_rows" /> 1:1.
    ///
    ///     Each row's Name is synced from the engine's
    ///     <see cref="BindingRow.Name" /> so the dialog reflects the
    ///     engine's collision-suffix resolution: when the user types
    ///     <c>"values"</c> in B and another row was already <c>"values"</c>,
    ///     the engine emits one as <c>"values_2"</c> and the
    ///     suffixed row's TextBox updates to match. The exception is
    ///     non-canonical user overrides (sanitiser-altered, empty,
    ///     reserved, cell-ref-shape) — those are filtered out by
    ///     <see cref="BuildRowStates" />, so the engine binds the row
    ///     with an auto-derived name; we keep the user's typed text
    ///     in the TextBox (with the red border via
    ///     <see cref="GatherRowVm.IsNameValid" />) so they can see
    ///     what they wrote and Save stays disabled until they fix it.
    ///     Re-entrancy is guarded by <see cref="_suppressRecompute" />.
    /// </summary>
    private void RecomputeForNameOnly()
    {
        var states = BuildRowStates();
        var newResult = _recompute(states);
        if (newResult == null || newResult.Diagnostic != null)
            return;

        _suppressRecompute = true;
        try
        {
            _result = newResult;
            PreviewText.Text = newResult.SynthesisedLet;

            var bindings = newResult.Bindings;
            var n = Math.Min(bindings.Count, _rows.Count);
            for (var i = 0; i < n; i++)
            {
                var vm = _rows[i];
                var binding = bindings[i];
                // Defensive: if positions diverge (shouldn't happen for
                // name-only changes), skip the in-place update for this
                // row rather than writing a mismatched Rhs onto the wrong
                // cell. The user's next non-name action will trigger a
                // full rebuild and resync everything.
                if (!Equals(vm.Source, binding.Source))
                    continue;
                vm.Rhs = binding.Rhs;
                if (!HasNonCanonicalOverride(vm.Source))
                    vm.Name = binding.Name;
            }
        }
        finally
        {
            _suppressRecompute = false;
        }
    }

    private bool HasNonCanonicalOverride(FormulaRef source)
    {
        return _nameOverrides.TryGetValue(source, out var n)
               && !GatherNameValidator.IsCanonical(n);
    }

    /// <summary>
    ///     Stamps each editable row's <see cref="GatherRowVm.IsNameValid" />
    ///     based on per-row canonicality, and returns true when every
    ///     editable row's name is canonical. Cross-row collisions are
    ///     <em>not</em> a validity concern — the engine resolves them
    ///     by suffixing user overrides, and the dialog reflects the
    ///     resolved name back into the row's TextBox. Save is gated
    ///     only on canonicality: a name like <c>"Hello World"</c>
    ///     can't be honored by the engine (the override is filtered
    ///     out by <see cref="BuildRowStates" />), so the user's typed
    ///     text would diverge from the LET — disabling Save until
    ///     they pick a name the engine can use. Inner-LET expansions,
    ///     orphan rows, and excluded rows skip the check: they don't
    ///     drive bindings the user is committing to.
    /// </summary>
    private bool RevalidateNames()
    {
        var allValid = true;
        foreach (var vm in _rows)
        {
            if (vm.IsExpansion || vm.IsOrphan) continue;
            if (!vm.Include) continue;
            var isValid = GatherNameValidator.IsCanonical(vm.Name);
            vm.IsNameValid = isValid;
            if (!isValid) allValid = false;
        }
        return allValid;
    }

    private void UpdateSaveButtonEnabled(bool allValid)
    {
        SaveButton.IsEnabled = allValid;
        StatusText.Text = allValid ? DefaultStatusText : InvalidNameStatusText;
    }
}

/// <summary>
///     Row view-model bound to <see cref="GatherWindow" />'s ItemsControl.
///     Carries the row's static identity (Source, IsExpansion,
///     CanToggleRole) plus the mutable Include flag, IsStep toggle, and
///     (PR 12) editable <see cref="Name" /> and <see cref="Rhs" />.
///     Role flips through <see cref="IsStep" /> trigger an override that
///     the dialog persists across rebuilds. Name edits flow through
///     <see cref="Name" />'s setter, fire INPC for the dialog handler to
///     pick up, and gate Save via <see cref="IsNameValid" />. Orphan
///     rows (issue #157) reuse this VM with
///     <see cref="OrphanedByAddress" /> set: the row stays visible in a
///     separate section, hides its checkbox and role toggle (those don't
///     apply to inert orphan rows), shows an "orphaned by &lt;addr&gt;"
///     hint, and stays muted regardless of the Include flag.
/// </summary>
public class GatherRowVm : INotifyPropertyChanged
{
    /// <summary>
    ///     Indent applied to a slice row's address column. Sized to read as
    ///     child-of-the-row-above without pushing longer block addresses
    ///     (<c>A2:A4</c>) out of the column — the address column is widened by
    ///     the same amount in the XAML to absorb it.
    /// </summary>
    internal const double SliceIndentPixels = 14;

    private static readonly Brush ActiveAddressBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9cdcfe"));

    private static readonly Brush ActiveRoleBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#808080"));

    private static readonly Brush ActiveNameBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dcdcaa"));

    private static readonly Brush ActiveRhsBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#cccccc"));

    private static readonly Brush MutedBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6a6a6a"));

    private static readonly Brush InvalidNameBorderBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f48771"));

    private static readonly Brush DefaultNameBorderBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3e3e3e"));

    private bool _include;
    private bool _isNameValid = true;
    private bool _isStep;
    private string _name;
    private string _rhs;

    public GatherRowVm(BindingRow binding, bool include, string? orphanedByAddress = null)
    {
        Source = binding.Source;
        RoleEnum = binding.Role;
        _name = binding.Name;
        _rhs = binding.Rhs;
        IsExpansion = binding.IsExpansion;
        CanToggleRole = binding.CanToggleRole;
        SliceOf = binding.SliceOf;
        StraddlesSpillAnchor = binding.StraddlesSpillAnchor;
        OrphanedByAddress = orphanedByAddress;
        _include = include;
        _isStep = binding.Role == BindingRole.Step;
    }

    public FormulaRef Source { get; }
    public BindingRole RoleEnum { get; }

    /// <summary>
    ///     The row's address as displayed in the dialog. A spilling anchor's
    ///     row renders the <em>spilled</em> form (<c>A2#</c>) because its
    ///     binding is the whole array, not the anchor cell's scalar value —
    ///     which is what tells it apart from a slice row for the scalar
    ///     reference <c>A2</c> to the same cell, since both rows would
    ///     otherwise read the bare <c>A2</c>.
    ///     <see cref="FormulaRef.A1Address" /> deliberately omits the suffix
    ///     (it is the dedupe/identity form), so the <c>#</c> is appended here,
    ///     in the display layer that needs it.
    /// </summary>
    public string Address => Source.IsSpilled ? Source.A1Address + "#" : Source.A1Address;

    /// <summary>
    ///     Spec 0010 PR 8. Left margin applied to the address column so slice
    ///     rows sit visibly indented under the anchor row they slice — the
    ///     engine already emits each anchor's slices immediately after it, so
    ///     the indent alone reads as parentage. Zero on every other row kind.
    /// </summary>
    public Thickness AddressIndent => IsSlice ? new Thickness(SliceIndentPixels, 0, 0, 0) : default;

    /// <summary>
    ///     Spec 0010: slice rows read "slice" rather than "input" so the
    ///     author can tell a named slice of a spill apart from a plain cell
    ///     input at a glance. They never carry a role toggle
    ///     (<see cref="BindingRow.CanToggleRole" /> is false), so the static
    ///     text is always the one rendered. Indentation under the anchor and
    ///     the fixed-slice-position note are spec 0010 PR 8.
    /// </summary>
    public string Role => IsSlice
        ? "slice"
        : RoleEnum == BindingRole.Input ? "input" : "step";

    public bool IsExpansion { get; }
    public bool CanToggleRole { get; }

    /// <summary>
    ///     The anchor row this row is a slice of, or null on ordinary rows.
    /// </summary>
    public FormulaRef? SliceOf { get; }

    public bool IsSlice => SliceOf != null;

    /// <summary>
    ///     Spec 0010 PR 5. The spill anchor this row's range is partly inside,
    ///     or null on every other row. A straddling range cannot be expressed
    ///     as a slice of the anchor's array, so it stays a literal range input
    ///     — correct, just not self-contained — and the row carries a warning
    ///     marker rather than a diagnostic or a refusal. Held as the
    ///     <see cref="CellRef" /> rather than just its address so
    ///     <see cref="GatherWindow" /> can round-trip the flag back into a
    ///     <see cref="BindingRow" /> when it snapshots an excluded row; a
    ///     snapshot that dropped it made the marker disappear until the row was
    ///     re-ticked.
    /// </summary>
    public CellRef? StraddlesSpillAnchor { get; }

    public string? StraddledSpillAnchorAddress => StraddlesSpillAnchor?.A1Address;

    public bool IsStraddlingSpill => StraddledSpillAnchorAddress != null;

    /// <summary>
    ///     Tooltip on the straddle warning marker, naming the anchor so the
    ///     author can see which spill the range half-overlaps. Spec 0010's
    ///     wording verbatim.
    /// </summary>
    public string StraddleWarningText =>
        StraddledSpillAnchorAddress == null
            ? ""
            : $"Partly inside {StraddledSpillAnchorAddress}'s spill range — left as a cell reference.";

    public Visibility StraddleWarningVisibility =>
        IsStraddlingSpill ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    ///     The binding name as displayed in (and edited from) the dialog.
    ///     Two-way bound to the Name TextBox; the setter fires INPC so
    ///     <see cref="GatherWindow" /> can validate and recompute. Inner-
    ///     LET expansion rows and orphan rows expose this read-only
    ///     (<see cref="IsNameEditable" /> = false) — the engine owns
    ///     their names and a user edit would corrupt the host's
    ///     override slot.
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            var v = value;
            if (_name == v) return;
            _name = v;
            OnPropertyChanged();
        }
    }

    /// <summary>
    ///     The binding's right-hand-side text. Mutable so
    ///     <see cref="GatherWindow.RecomputeForNameOnly" /> can update
    ///     it in place without rebuilding the row collection — that's
    ///     what keeps focus stable during live name editing.
    /// </summary>
    public string Rhs
    {
        get => _rhs;
        set
        {
            var v = value;
            if (_rhs == v) return;
            _rhs = v;
            OnPropertyChanged();
        }
    }

    /// <summary>
    ///     False when the user's typed <see cref="Name" /> isn't a
    ///     canonical identifier (would be silently changed by the
    ///     sanitizer or is empty after sanitization) or collides with
    ///     another row's name. Drives the red-border style on the Name
    ///     TextBox via <see cref="NameBorderBrush" /> and the Save
    ///     button's enabled state at the dialog level. Defaults to true
    ///     so engine-derived initial names (which are canonical by
    ///     construction) display cleanly until the dialog runs its
    ///     first validation pass.
    /// </summary>
    public bool IsNameValid
    {
        get => _isNameValid;
        set
        {
            if (_isNameValid == value) return;
            _isNameValid = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NameBorderBrush));
        }
    }

    /// <summary>
    ///     Inner-LET expansion rows and orphan rows aren't user-editable:
    ///     expansion rows share Source with their host (an edit would
    ///     wrongly slot into the host's override key) and orphan rows
    ///     are inert by definition. Editable rows render the Name as a
    ///     plain TextBox with a TwoWay binding; non-editable rows render
    ///     the same TextBox but IsReadOnly so the column layout stays
    ///     consistent.
    /// </summary>
    public bool IsNameEditable => !IsExpansion && !IsOrphan;

    /// <summary>
    ///     Inverse of <see cref="IsNameEditable" /> for the TextBox's
    ///     <c>IsReadOnly</c> binding — WPF doesn't have a built-in
    ///     inverse converter, so the VM exposes the negated form.
    /// </summary>
    public bool IsNameReadOnly => !IsNameEditable;

    /// <summary>
    ///     Address of the cell whose demote-or-exclude orphaned this
    ///     row, or null when the row isn't an orphan. Drives the
    ///     "orphaned by &lt;addr&gt;" hint in the orphan section and
    ///     gates which controls render on the row.
    /// </summary>
    public string? OrphanedByAddress { get; }

    public bool IsOrphan => OrphanedByAddress != null;

    public string OrphanHintText =>
        OrphanedByAddress == null ? "" : $"orphaned by {OrphanedByAddress}";

    public bool Include
    {
        get => _include;
        set
        {
            if (_include == value) return;
            _include = value;
            OnPropertyChanged();
            // Brushes change with Include — mute when off so excluded
            // rows visibly read as "kept around for re-tick" rather than
            // active bindings.
            OnPropertyChanged(nameof(AddressBrush));
            OnPropertyChanged(nameof(RoleBrush));
            OnPropertyChanged(nameof(NameBrush));
            OnPropertyChanged(nameof(RhsBrush));
        }
    }

    /// <summary>
    ///     Two-way bound to the role toggle button. The setter fires the
    ///     PropertyChanged event the dialog handler watches for to spawn
    ///     a Recompute with the override applied. The natural-role
    ///     baseline is the engine-classified <see cref="RoleEnum" />, so
    ///     IsStep is initialised to <c>RoleEnum == Step</c> at
    ///     construction; toggling flips the override.
    /// </summary>
    public bool IsStep
    {
        get => _isStep;
        set
        {
            if (_isStep == value) return;
            _isStep = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    ///     Inner-LET expansion rows hide their checkbox: the host cell
    ///     drives the toggle, and a checkbox here would be a confusing
    ///     no-op (the engine doesn't expose per-inner-binding exclusion).
    ///     Orphan rows hide it too — they're inert until the user
    ///     reverses the action on the row above (re-include or
    ///     promote) that orphaned them.
    /// </summary>
    public Visibility IncludeCheckboxVisibility =>
        IsExpansion || IsOrphan ? Visibility.Hidden : Visibility.Visible;

    /// <summary>
    ///     Show the role toggle button only on rows where promote/demote
    ///     is meaningful — cell rows with a source formula (range rows,
    ///     literal-cell inputs, and inner-LET expansions all keep the
    ///     toggle hidden). When hidden, the static Role text takes the
    ///     same column slot via <see cref="RoleStaticVisibility" />.
    ///     Orphan rows always render the static text so they read as
    ///     read-only — toggling on an orphan row would imply you can
    ///     edit it, which you can't.
    /// </summary>
    public Visibility RoleToggleVisibility =>
        CanToggleRole && !IsOrphan ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RoleStaticVisibility =>
        CanToggleRole && !IsOrphan ? Visibility.Collapsed : Visibility.Visible;

    // Orphan rows always render muted regardless of Include — they're
    // visually distinct from active rows by design (the user lost them
    // from view, the muted treatment communicates "this isn't in the
    // LET right now").
    public Brush AddressBrush => Include && !IsOrphan ? ActiveAddressBrush : MutedBrush;
    public Brush RoleBrush => Include && !IsOrphan ? ActiveRoleBrush : MutedBrush;
    public Brush NameBrush => Include && !IsOrphan ? ActiveNameBrush : MutedBrush;
    public Brush RhsBrush => Include && !IsOrphan ? ActiveRhsBrush : MutedBrush;

    /// <summary>
    ///     Border brush for the Name TextBox: red when
    ///     <see cref="IsNameValid" /> is false to flag the bad input,
    ///     the dialog's neutral border colour otherwise. The
    ///     setter on <see cref="IsNameValid" /> fires INPC for this
    ///     property too so the binding refreshes without an explicit
    ///     trigger.
    /// </summary>
    public Brush NameBorderBrush => IsNameValid ? DefaultNameBorderBrush : InvalidNameBorderBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}