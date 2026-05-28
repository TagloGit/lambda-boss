using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LambdaBoss.UI;

/// <summary>
///     Spec 0008 — the <c>/Refactor</c> dialog. Shows the active cell's
///     original formula, the engine-extracted / existing-LET input rows
///     (with editable names, Include checkbox, drag + Alt+Up/Down reorder),
///     the PR 3 promotable section (named ranges + external refs),
///     a read-only Calculation-bindings section, and a live preview of
///     the synthesised LET. Save returns the preview text via
///     <see cref="SavedFormula" />; Cancel discards.
///     The code-behind is intentionally thin: every row-state change
///     (rename, Include toggle, reorder, Promote toggle) calls back into
///     the supplied <c>recompute</c> delegate (which wraps
///     <see cref="RefactorEngine.Recompute" />), and the result replaces
///     the preview + calc-binding list + promotables in place. Row Keys
///     (not <c>FormulaRef</c>s) drive the identity round-trip with the
///     engine so existing-LET value bindings whose RHS isn't a single
///     cell ref (e.g. a literal or a named range) and promoted promotables
///     can be tracked alongside extracted refs.
/// </summary>
public partial class RefactorToLetWindow
{
    private const string DefaultStatusText =
        "Save writes the LET into the active cell · Esc to cancel";

    private const string InvalidNameStatusText =
        "Fix invalid names before saving";

    // Calc binding names are read-only in the dialog but they still occupy
    // the LET's name namespace — Inputs are flagged invalid when they
    // collide with one.
    private readonly IReadOnlyList<string> _calcBindingNames;
    private readonly ObservableCollection<RefactorCalcBindingVm> _calcRows = [];
    private readonly ObservableCollection<RefactorPromotableRowVm> _promotables = [];

    private readonly Func<IReadOnlyList<RefactorRowState>, RefactorResult> _recompute;
    private readonly ObservableCollection<RefactorInputRowVm> _rows = [];

    private RefactorResult _result;

    // Reentrancy guard: rebuilding the row list during a Recompute fires
    // INotifyPropertyChanged on each VM, which would re-enter the change
    // handler. The guard short-circuits the nested calls so a single
    // user action yields exactly one engine call.
    private bool _suppressRecompute;

    public RefactorToLetWindow(
        RefactorResult initial,
        Func<IReadOnlyList<RefactorRowState>, RefactorResult> recompute)
    {
        InitializeComponent();
        _result = initial;
        _recompute = recompute;

        OriginalFormulaText.Text = initial.OriginalFormula;
        PreviewText.Text = initial.SynthesisedLet;

        _calcBindingNames = initial.CalcBindings.Select(c => c.Name).ToList();
        BuildRowsFromInputs(initial.Inputs);
        UpdateCalcBindings(initial.CalcBindings);
        UpdatePromotables(initial.Promotables);
        _rows.CollectionChanged += Rows_CollectionChanged;
        InputsList.ItemsSource = _rows;
        CalcBindingsList.ItemsSource = _calcRows;
        PromotablesList.ItemsSource = _promotables;

        UpdateSaveButtonEnabled(RevalidateNames());
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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && SaveButton.IsEnabled)
        {
            SaveButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void InputRow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        if (!alt) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Up && key != Key.Down) return;
        if (sender is not FrameworkElement { DataContext: RefactorInputRowVm row }) return;

        var index = _rows.IndexOf(row);
        if (index < 0) return;

        if (key == Key.Up && index > 0)
            _rows.Move(index, index - 1);
        else if (key == Key.Down && index < _rows.Count - 1)
            _rows.Move(index, index + 1);

        e.Handled = true;
    }

    private void NameTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.SelectAll();
    }

    private void NameTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.IsKeyboardFocusWithin) return;
        tb.Focus();
        e.Handled = true;
    }

    private void BuildRowsFromInputs(IReadOnlyList<RefactorInputRow> inputs)
    {
        _suppressRecompute = true;
        try
        {
            foreach (var v in _rows)
                v.PropertyChanged -= Row_PropertyChanged;
            _rows.Clear();

            foreach (var input in inputs)
            {
                var vm = new RefactorInputRowVm(input);
                vm.PropertyChanged += Row_PropertyChanged;
                _rows.Add(vm);
            }
        }
        finally
        {
            _suppressRecompute = false;
        }
    }

    private void UpdateCalcBindings(IReadOnlyList<RefactorCalcBindingRow> calcBindings)
    {
        _calcRows.Clear();
        foreach (var c in calcBindings)
            _calcRows.Add(new RefactorCalcBindingVm(c.Name, c.RewrittenRhs));

        var show = calcBindings.Count > 0;
        CalcBindingsHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CalcBindingsBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    ///     Rebuilds the promotables collection from the engine's most recent
    ///     result. The dialog doesn't track per-promotable user state besides
    ///     the Promote toggle (which has just been processed by the engine),
    ///     so a wholesale rebuild is safe.
    /// </summary>
    private void UpdatePromotables(IReadOnlyList<RefactorPromotableRow> promotables)
    {
        _suppressRecompute = true;
        try
        {
            foreach (var vm in _promotables)
                vm.PropertyChanged -= Promotable_PropertyChanged;
            _promotables.Clear();
            foreach (var p in promotables)
            {
                var vm = new RefactorPromotableRowVm(p);
                vm.PropertyChanged += Promotable_PropertyChanged;
                _promotables.Add(vm);
            }
        }
        finally
        {
            _suppressRecompute = false;
        }

        var show = promotables.Count > 0;
        PromotablesHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PromotablesBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Rows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reorder via drag/drop or Alt+Up/Down fires Move; rerun the
        // engine so the preview reflects the new binding order.
        if (e.Action == NotifyCollectionChangedAction.Move && !_suppressRecompute)
            RecomputeAndRefresh();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressRecompute) return;
        if (e.PropertyName is not (nameof(RefactorInputRowVm.Name)
            or nameof(RefactorInputRowVm.Include)))
            return;

        UpdateSaveButtonEnabled(RevalidateNames());
        RecomputeAndRefresh();
    }

    /// <summary>
    ///     When a promotable's Promote checkbox flips on/off, mutate
    ///     <see cref="_rows" /> to add or remove the corresponding input
    ///     row (Promote-on creates a fresh promoted input row with a
    ///     locally allocated <c>inputN</c> name; Promote-off removes the
    ///     row matched by Key), then recompute. The recompute pass
    ///     overwrites <see cref="_promotables" /> from the engine's
    ///     <see cref="RefactorResult.Promotables" />, so the VM that fired
    ///     this event is replaced with a fresh one carrying the engine's
    ///     authoritative state.
    /// </summary>
    private void Promotable_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressRecompute) return;
        if (e.PropertyName != nameof(RefactorPromotableRowVm.Promote)) return;
        if (sender is not RefactorPromotableRowVm vm) return;

        if (vm.Promote)
        {
            // Already present? Defensive — shouldn't happen since promotable
            // VMs only show when un-promoted, but the engine's reconciliation
            // is the source of truth so we tolerate it.
            if (_rows.Any(r => string.Equals(r.Key, vm.Key, StringComparison.Ordinal)))
                return;

            var name = AllocateLocalAutoName();
            var origin = vm.Kind == RefactorPromotableKind.NamedRange
                ? RefactorRowOrigin.PromotedNamedRange
                : RefactorRowOrigin.PromotedExternalRef;
            var input = new RefactorInputRow(vm.Key, null, name, vm.Token, origin);
            var rowVm = new RefactorInputRowVm(input);
            rowVm.PropertyChanged += Row_PropertyChanged;
            _suppressRecompute = true;
            try
            {
                _rows.Add(rowVm);
            }
            finally
            {
                _suppressRecompute = false;
            }
        }
        else
        {
            var row = _rows.FirstOrDefault(r => string.Equals(r.Key, vm.Key, StringComparison.Ordinal));
            if (row != null)
            {
                _suppressRecompute = true;
                try
                {
                    row.PropertyChanged -= Row_PropertyChanged;
                    _rows.Remove(row);
                }
                finally
                {
                    _suppressRecompute = false;
                }
            }
        }

        UpdateSaveButtonEnabled(RevalidateNames());
        RecomputeAndRefresh();
    }

    /// <summary>
    ///     Picks the lowest free <c>inputN</c> name not already in use by an
    ///     existing input row or a calc binding. Matches the engine's
    ///     allocator so dialog-allocated and engine-allocated names land in
    ///     the same sequence when the user mixes extracted refs with
    ///     promoted promotables.
    /// </summary>
    private string AllocateLocalAutoName()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows) used.Add(r.Name);
        foreach (var c in _calcBindingNames) used.Add(c);
        var i = 1;
        while (used.Contains("input" + i)) i++;
        return "input" + i;
    }

    private void RecomputeAndRefresh()
    {
        var rowStates = _rows
            .Select(r => new RefactorRowState(r.Key, r.Name, r.Include))
            .ToList();

        var newResult = _recompute(rowStates);
        if (newResult.Diagnostic != null)
            return;

        _suppressRecompute = true;
        try
        {
            _result = newResult;
            PreviewText.Text = newResult.SynthesisedLet;
            UpdateCalcBindings(newResult.CalcBindings);
        }
        finally
        {
            _suppressRecompute = false;
        }

        // Reconcile promotables AFTER releasing the guard so the
        // promotables update can rewire the per-VM property handlers.
        UpdatePromotables(newResult.Promotables);
    }

    /// <summary>
    ///     Stamps each row's <see cref="RefactorInputRowVm.IsNameValid" />
    ///     based on per-row canonicality (shape + non-reserved), cross-row
    ///     uniqueness among included rows, AND collision with calc binding
    ///     names. Returns true when every included row is valid AND no
    ///     duplicates exist.
    /// </summary>
    private bool RevalidateNames()
    {
        var included = _rows.Where(r => r.Include).ToList();
        var dupes = included
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();

        var calcNameSet = new HashSet<string>(_calcBindingNames, StringComparer.OrdinalIgnoreCase);

        var allValid = true;
        foreach (var vm in _rows)
        {
            bool valid;
            if (!vm.Include)
                valid = true;
            else
            {
                var shapeOk = ExcelNameValidator.Validate(vm.Name).IsValid;
                var collidesWithCalc = calcNameSet.Contains(vm.Name);
                valid = shapeOk && !dupes.Contains(vm) && !collidesWithCalc;
                if (!valid) allValid = false;
            }

            vm.IsNameValid = valid;
        }

        return allValid;
    }

    private void UpdateSaveButtonEnabled(bool allValid)
    {
        // An existing LET with every input dropped still produces a valid
        // formula (the bare body), so Save stays enabled when calc
        // bindings exist OR at least one row is kept.
        var hasKeptRows = _rows.Any(r => r.Include);
        var hasCalcBindings = _calcBindingNames.Count > 0;
        SaveButton.IsEnabled = allValid && (hasKeptRows || hasCalcBindings);
        if (!allValid)
            StatusText.Text = InvalidNameStatusText;
        else if (!hasKeptRows && !hasCalcBindings)
            StatusText.Text = "No bindings selected — nothing to refactor";
        else
            StatusText.Text = DefaultStatusText;
    }
}

/// <summary>
///     Row view-model bound to <see cref="RefactorToLetWindow" />'s inputs
///     list. Carries the row's <see cref="Key" /> identity (used to round-
///     trip user state back to the engine via <see cref="RefactorRowState" />),
///     the user-editable <see cref="Name" /> + <see cref="Include" />, and
///     the merge-survivor <see cref="BadgeText" /> when applicable.
///     <see cref="IsNameValid" /> is set by the dialog on every validation
///     pass and drives the red-border on the Name TextBox.
/// </summary>
public class RefactorInputRowVm : INotifyPropertyChanged
{
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

    private bool _include = true;
    private bool _isNameValid = true;
    private string _name;

    public RefactorInputRowVm(RefactorInputRow input)
    {
        Key = input.Key;
        _name = input.Name;
        Rhs = input.Rhs;
        if (input.MergedFrom is { Count: > 0 })
        {
            BadgeText = "merged ← " + string.Join(", ", input.MergedFrom);
            BadgeTooltip =
                "This binding's RHS matched " +
                string.Join(", ", input.MergedFrom) +
                "; refs to those names were rewritten to use this one.";
            BadgeVisibility = Visibility.Visible;
        }
        else if (input.Origin == RefactorRowOrigin.PromotedNamedRange)
        {
            BadgeText = "promoted name";
            BadgeTooltip =
                "Promoted from the named-range section. Uncheck Promote there to un-promote.";
            BadgeVisibility = Visibility.Visible;
        }
        else if (input.Origin == RefactorRowOrigin.PromotedExternalRef)
        {
            BadgeText = "promoted ref";
            BadgeTooltip =
                "Promoted external-workbook ref. Uncheck Promote there to un-promote.";
            BadgeVisibility = Visibility.Visible;
        }
        else
        {
            BadgeText = string.Empty;
            BadgeTooltip = string.Empty;
            BadgeVisibility = Visibility.Collapsed;
        }
    }

    public string Key { get; }
    public string Rhs { get; }
    public string BadgeText { get; }
    public string BadgeTooltip { get; }
    public Visibility BadgeVisibility { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public bool Include
    {
        get => _include;
        set
        {
            if (_include == value) return;
            _include = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NameBrush));
            OnPropertyChanged(nameof(RhsBrush));
        }
    }

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

    public Brush NameBrush => Include ? ActiveNameBrush : MutedBrush;
    public Brush RhsBrush => Include ? ActiveRhsBrush : MutedBrush;
    public Brush NameBorderBrush => IsNameValid ? DefaultNameBorderBrush : InvalidNameBorderBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

/// <summary>
///     Row view-model bound to <see cref="RefactorToLetWindow" />'s
///     promote-to-input list. The dialog flips <see cref="Promote" /> in
///     response to the user's checkbox toggle; the
///     <c>PropertyChanged</c> handler on the window adds or removes the
///     corresponding row in the inputs list. Read-only otherwise —
///     <see cref="Token" /> and <see cref="OccurrencesLabel" /> come from
///     the engine and don't change for the lifetime of this VM.
/// </summary>
public class RefactorPromotableRowVm : INotifyPropertyChanged
{
    private bool _promote;

    public RefactorPromotableRowVm(RefactorPromotableRow row)
    {
        Key = row.Key;
        Kind = row.Kind;
        Token = row.Token;
        Occurrences = row.Occurrences;
        KindLabel = row.Kind == RefactorPromotableKind.NamedRange
            ? "named range"
            : "external ref";
        OccurrencesLabel = row.Occurrences == 1 ? "1 use" : row.Occurrences + " uses";
    }

    public string Key { get; }
    public RefactorPromotableKind Kind { get; }
    public string Token { get; }
    public int Occurrences { get; }
    public string KindLabel { get; }
    public string OccurrencesLabel { get; }

    public bool Promote
    {
        get => _promote;
        set
        {
            if (_promote == value) return;
            _promote = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

/// <summary>
///     Read-only view-model for a calc binding in the dialog's
///     Calculation-bindings section.
/// </summary>
public sealed record RefactorCalcBindingVm(string Name, string RewrittenRhs);
