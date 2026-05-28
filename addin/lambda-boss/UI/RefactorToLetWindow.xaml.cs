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
///     a read-only Calculation-bindings section (PR 2+), and a live
///     preview of the synthesised LET. Save returns the preview text via
///     <see cref="SavedFormula" />; Cancel discards.
///     The code-behind is intentionally thin: every row-state change
///     (rename, Include toggle, reorder) calls back into the supplied
///     <c>recompute</c> delegate (which wraps
///     <see cref="RefactorEngine.Recompute" />), and the result replaces
///     the preview + the calc-binding list in place. Row Keys (not
///     <c>FormulaRef</c>s) drive the identity round-trip with the engine
///     so existing-LET value bindings whose RHS isn't a single cell ref
///     (e.g. a literal or a named range) can be tracked alongside
///     extracted refs.
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
        _rows.CollectionChanged += Rows_CollectionChanged;
        InputsList.ItemsSource = _rows;
        CalcBindingsList.ItemsSource = _calcRows;

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
///     Read-only view-model for a calc binding in the dialog's
///     Calculation-bindings section.
/// </summary>
public sealed record RefactorCalcBindingVm(string Name, string RewrittenRhs);