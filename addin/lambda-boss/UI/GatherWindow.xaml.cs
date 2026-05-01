using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LambdaBoss.UI;

/// <summary>
///     Display of a <see cref="GatherResult" /> with the Include checkbox
///     column (PR 10) and the role toggle (PR 11). Toggling either
///     control calls back into <see cref="GatherEngine.Recompute" /> via
///     <see cref="_recompute" />, then rebuilds the row list:
///     re-included rows return as the engine surfaces them, explicitly-
///     excluded rows are kept visible (greyed out, checkbox off) so the
///     user can re-tick them, and rows the engine no longer surfaces
///     because their only path ran through a cell the user demoted or
///     excluded appear in a separate "Orphaned" section beneath the
///     active bindings (issue #157, mirrors the excluded-row pattern).
///     Role overrides persist across rebuilds via
///     <see cref="_roleOverrides" /> so a user's promote/demote choice
///     stays in effect through subsequent Include toggles. Save returns
///     the synthesised LET text from the latest result so the caller
///     writes that back to the sink.
/// </summary>
public partial class GatherWindow
{
    private readonly Func<IReadOnlyList<RowState>, GatherResult?> _recompute;
    private GatherResult _result;
    private readonly ObservableCollection<GatherRowVm> _rows = new();
    private readonly ObservableCollection<GatherRowVm> _orphans = new();
    // Snapshots of explicitly-excluded rows so they can re-appear in the
    // visible list after a Recompute (which only returns included
    // bindings). Stored as plain BindingRow data — no VM subscriptions to
    // manage across rebuilds. Re-checking removes the entry; the engine's
    // result is the source of truth for re-included rows.
    private readonly Dictionary<FormulaRef, BindingRow> _excluded = new();
    // Role overrides persist across rebuilds: when the user demotes B1 to
    // input, that choice survives an unrelated checkbox toggle on A1
    // (which would otherwise re-run the engine without the override and
    // restore B1 to its natural classification). The dialog re-injects
    // overrides into every Recompute so the engine sees a consistent
    // view of user intent.
    private readonly Dictionary<FormulaRef, BindingRole> _roleOverrides = new();
    // Orphan tracker: precedents that fell out of the active list because
    // their only path ran through a cell the user demoted or excluded.
    // Surfaced in the Orphaned section below the bindings list and
    // removed when the user reverses the causing action — re-includes
    // an excluded cell, or promotes a demoted one back to a step.
    private readonly OrphanedRowTracker _orphanTracker = new();
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

        StatusText.Text = "Save writes the LET into the sink cell · Esc to cancel";
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
                var vm = new GatherRowVm(binding, include: true);
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
                var vm = new GatherRowVm(snapshot, include: false);
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
                var vm = new GatherRowVm(entry.Snapshot, include: true, orphanedByAddress: hint);
                _orphans.Add(vm);
            }
        }
        finally
        {
            _suppressRecompute = false;
        }

        OrphansSection.Visibility =
            _orphans.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            FormulaRef? causedBy = null;
            if (!vm.Include)
            {
                _excluded[vm.Source] = new BindingRow(
                    vm.Source, vm.RoleEnum, vm.Name, vm.Rhs,
                    vm.IsExpansion, vm.CanToggleRole);
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
            }

            Recompute(causedBy);
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
            {
                _excluded[vm.Source] = existing with { Role = newRole };
            }

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
            snapshotBefore[vm.Source] = new BindingRow(
                vm.Source, vm.RoleEnum, vm.Name, vm.Rhs,
                vm.IsExpansion, vm.CanToggleRole);
        }

        // Build RowState list from the visible bindings. Inner-LET rows
        // are skipped because they share a Source with their host cell
        // — the host's row owns the toggle, so passing the inner rows'
        // state would double-flag their Source. Orphan rows are inert
        // and live in a separate collection (_orphans), so they don't
        // appear in this loop at all. Role overrides are layered on top
        // of each row's Include flag so the engine sees a single
        // consistent view of user intent.
        var states = new List<RowState>(_rows.Count);
        var seen = new HashSet<FormulaRef>();
        foreach (var vm in _rows)
        {
            if (vm.IsExpansion) continue;
            if (!seen.Add(vm.Source)) continue;
            BindingRole? roleOverride = null;
            if (_roleOverrides.TryGetValue(vm.Source, out var o))
                roleOverride = o;
            states.Add(new RowState(vm.Source, vm.Include, roleOverride));
        }

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
}

/// <summary>
///     Row view-model bound to <see cref="GatherWindow" />'s ItemsControl.
///     Carries the row's static identity (Source, IsExpansion,
///     CanToggleRole) plus the mutable Include flag and IsStep toggle;
///     engine-driven Name/Rhs are immutable per VM instance — a Recompute
///     rebuilds the collection rather than mutating individual rows, so
///     we don't need INPCC plumbing on those fields. Role flips through
///     <see cref="IsStep" /> trigger an override that the dialog
///     persists across rebuilds. Orphan rows (issue #157) reuse this VM
///     with <see cref="OrphanedByAddress" /> set: the row stays visible
///     in a separate section, hides its checkbox and role toggle (those
///     don't apply to inert orphan rows), shows an "orphaned by &lt;addr&gt;"
///     hint, and stays muted regardless of the Include flag.
/// </summary>
public class GatherRowVm : INotifyPropertyChanged
{
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

    private bool _include;
    private bool _isStep;

    public GatherRowVm(BindingRow binding, bool include, string? orphanedByAddress = null)
    {
        Source = binding.Source;
        RoleEnum = binding.Role;
        Name = binding.Name;
        Rhs = binding.Rhs;
        IsExpansion = binding.IsExpansion;
        CanToggleRole = binding.CanToggleRole;
        OrphanedByAddress = orphanedByAddress;
        _include = include;
        _isStep = binding.Role == BindingRole.Step;
    }

    public FormulaRef Source { get; }
    public BindingRole RoleEnum { get; }
    public string Address => Source.A1Address;
    public string Role => RoleEnum == BindingRole.Input ? "input" : "step";
    public string Name { get; }
    public string Rhs { get; }
    public bool IsExpansion { get; }
    public bool CanToggleRole { get; }

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
