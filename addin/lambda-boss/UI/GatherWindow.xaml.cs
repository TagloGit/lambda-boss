using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LambdaBoss.UI;

/// <summary>
///     Display of a <see cref="GatherResult" /> with PR 10's Include
///     checkbox column. Toggling Include calls back into
///     <see cref="GatherEngine.Recompute" /> via
///     <see cref="_recompute" />, then rebuilds the row list:
///     re-included rows return as the engine surfaces them, explicitly-
///     excluded rows are kept visible (greyed out, checkbox off) so the
///     user can re-tick them, and orphaned rows that the engine no longer
///     surfaces drop out. Save returns the synthesised LET text from the
///     latest result so the caller writes that back to the sink.
/// </summary>
public partial class GatherWindow
{
    private readonly Func<IReadOnlyList<RowState>, GatherResult?> _recompute;
    private GatherResult _result;
    private readonly ObservableCollection<GatherRowVm> _rows = new();
    // Snapshots of explicitly-excluded rows so they can re-appear in the
    // visible list after a Recompute (which only returns included
    // bindings). Stored as plain BindingRow data — no VM subscriptions to
    // manage across rebuilds. Re-checking removes the entry; the engine's
    // result is the source of truth for re-included rows.
    private readonly Dictionary<FormulaRef, BindingRow> _excluded = new();
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

            var surfaced = new HashSet<FormulaRef>();
            foreach (var binding in bindings)
            {
                var vm = new GatherRowVm(binding, include: true);
                vm.PropertyChanged += Row_PropertyChanged;
                _rows.Add(vm);
                surfaced.Add(binding.Source);
            }

            // Explicitly-excluded rows that the engine didn't surface
            // appear at the bottom so the user can find them to re-tick.
            // An excluded ref the engine still surfaces (e.g. an inner-
            // expansion row sharing a host cell that's still in the
            // result) is suppressed here — surfacing once avoids
            // duplicate rows for a single Source.
            foreach (var (source, snapshot) in _excluded)
            {
                if (surfaced.Contains(source)) continue;
                var vm = new GatherRowVm(snapshot, include: false);
                vm.PropertyChanged += Row_PropertyChanged;
                _rows.Add(vm);
            }
        }
        finally
        {
            _suppressRecompute = false;
        }
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressRecompute) return;
        if (e.PropertyName != nameof(GatherRowVm.Include)) return;
        if (sender is not GatherRowVm vm) return;

        // Snapshot when unchecking so the row stays visible for re-tick;
        // forget the snapshot when re-checking so the engine result
        // owns the row's display (Role/Name/Rhs may have shifted as
        // other rows toggled in the meantime).
        if (!vm.Include)
        {
            _excluded[vm.Source] = new BindingRow(
                vm.Source, vm.RoleEnum, vm.Name, vm.Rhs, vm.IsExpansion);
        }
        else
        {
            _excluded.Remove(vm.Source);
        }

        Recompute();
    }

    private void Recompute()
    {
        // Build RowState list from the visible rows. Inner-LET rows are
        // skipped because they share a Source with their host cell — the
        // host's row owns the toggle, so passing the inner rows' state
        // would double-flag their Source.
        var states = new List<RowState>(_rows.Count);
        var seen = new HashSet<FormulaRef>();
        foreach (var vm in _rows)
        {
            if (vm.IsExpansion) continue;
            if (!seen.Add(vm.Source)) continue;
            states.Add(new RowState(vm.Source, vm.Include));
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

        _result = newResult;
        PreviewText.Text = newResult.SynthesisedLet;
        BuildRowsFromBindings(newResult.Bindings);
    }
}

/// <summary>
///     Row view-model bound to <see cref="GatherWindow" />'s ItemsControl.
///     Carries the row's static identity (Source, IsExpansion) plus the
///     mutable Include flag; engine-driven fields (Role, Name, Rhs) are
///     immutable per VM instance — a Recompute rebuilds the collection
///     rather than mutating individual rows, so we don't need INPCC
///     plumbing on those fields.
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

    public GatherRowVm(BindingRow binding, bool include)
    {
        Source = binding.Source;
        RoleEnum = binding.Role;
        Name = binding.Name;
        Rhs = binding.Rhs;
        IsExpansion = binding.IsExpansion;
        _include = include;
    }

    public FormulaRef Source { get; }
    public BindingRole RoleEnum { get; }
    public string Address => Source.A1Address;
    public string Role => RoleEnum == BindingRole.Input ? "input" : "step";
    public string Name { get; }
    public string Rhs { get; }
    public bool IsExpansion { get; }

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
    ///     Inner-LET expansion rows hide their checkbox: the host cell
    ///     drives the toggle, and a checkbox here would be a confusing
    ///     no-op (the engine doesn't expose per-inner-binding exclusion).
    /// </summary>
    public Visibility IncludeCheckboxVisibility =>
        IsExpansion ? Visibility.Hidden : Visibility.Visible;

    public Brush AddressBrush => Include ? ActiveAddressBrush : MutedBrush;
    public Brush RoleBrush => Include ? ActiveRoleBrush : MutedBrush;
    public Brush NameBrush => Include ? ActiveNameBrush : MutedBrush;
    public Brush RhsBrush => Include ? ActiveRhsBrush : MutedBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
