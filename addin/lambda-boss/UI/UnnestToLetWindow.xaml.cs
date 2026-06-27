using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LambdaBoss.UI;

/// <summary>
///     Spec 0009 / issue #272 — the <c>/Unnest</c> dialog. Shows the active
///     cell's original formula, one row per extracted step (leaf-first) with
///     an editable live-validated name, the read-only RHS (child references
///     already substituted to step names), an origin badge ("function: SUMSQ"
///     / "operator: −"), and an Include checkbox (default on — un-checking
///     inlines the step back into its parent), plus a live preview of the
///     synthesised LET. Save returns the preview text via
///     <see cref="SavedFormula" />; Cancel discards.
///     <para>
///         The code-behind is intentionally thin: every row-state change (rename
///         or Include toggle) calls back into the supplied <c>recompute</c>
///         delegate (which wraps <see cref="UnnestEngine.Recompute" />), and the
///         result replaces the preview + RHS text in place. Row Keys drive the
///         identity round-trip with the engine. There are no reorder controls —
///         step order is forced by data dependency.
///     </para>
/// </summary>
public partial class UnnestToLetWindow
{
    private const string InvalidNameStatusText = "Fix invalid names before saving";

    private readonly Func<IReadOnlyList<UnnestRowState>, UnnestResult> _recompute;
    private readonly ObservableCollection<UnnestStepRowVm> _rows = [];

    // Tracks the last name-validation outcome so the status bar can choose
    // between a validation error and the summary count.
    private bool _namesValid = true;

    private UnnestResult _result;

    // Reentrancy guard: rebuilding the row list during a Recompute fires
    // INotifyPropertyChanged on each VM, which would re-enter the change
    // handler. The guard short-circuits the nested calls so a single user
    // action yields exactly one engine call.
    private bool _suppressRecompute;

    public UnnestToLetWindow(
        UnnestResult initial,
        Func<IReadOnlyList<UnnestRowState>, UnnestResult> recompute)
    {
        InitializeComponent();
        _result = initial;
        _recompute = recompute;

        OriginalFormulaText.Text = initial.OriginalFormula;
        PreviewText.Text = initial.SynthesisedLet;

        BuildRowsFromSteps(initial.Steps);
        StepsList.ItemsSource = _rows;

        NoStepsText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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

    private void BuildRowsFromSteps(IReadOnlyList<UnnestStepRow> steps)
    {
        _suppressRecompute = true;
        try
        {
            foreach (var v in _rows)
                v.PropertyChanged -= Row_PropertyChanged;
            _rows.Clear();

            foreach (var step in steps)
            {
                var vm = new UnnestStepRowVm(step);
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
        if (e.PropertyName is not (nameof(UnnestStepRowVm.Name)
            or nameof(UnnestStepRowVm.Include)))
            return;

        UpdateSaveButtonEnabled(RevalidateNames());
        RecomputeAndRefresh();
    }

    private void RecomputeAndRefresh()
    {
        var rowStates = _rows
            .Select(r => new UnnestRowState(r.Key, r.Name, r.Include))
            .ToList();

        var newResult = _recompute(rowStates);
        if (newResult.Diagnostic != null)
            return;

        _suppressRecompute = true;
        try
        {
            _result = newResult;
            PreviewText.Text = newResult.SynthesisedLet;

            // The engine re-renders each step's RHS as Include toggles change
            // which children collapse to names; push the fresh text back into
            // the existing VMs (matched by Key) so the row list stays stable.
            var rhsByKey = newResult.Steps.ToDictionary(s => s.Key, s => s.Rhs, StringComparer.Ordinal);
            foreach (var vm in _rows)
                if (rhsByKey.TryGetValue(vm.Key, out var rhs))
                    vm.Rhs = rhs;
        }
        finally
        {
            _suppressRecompute = false;
        }

        RefreshStatusText();
    }

    /// <summary>
    ///     Stamps each row's <see cref="UnnestStepRowVm.IsNameValid" /> based on
    ///     per-row canonicality (shape, via <see cref="ExcelNameValidator" />)
    ///     and cross-row uniqueness among included rows. Un-included rows are
    ///     never invalid (their name isn't emitted). Returns true when every
    ///     included row is valid AND no duplicates exist.
    /// </summary>
    private bool RevalidateNames()
    {
        var included = _rows.Where(r => r.Include).ToList();
        var dupes = included
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();

        var allValid = true;
        foreach (var vm in _rows)
        {
            bool valid;
            if (!vm.Include)
                valid = true;
            else
            {
                var shapeOk = ExcelNameValidator.Validate(vm.Name).IsValid;
                valid = shapeOk && !dupes.Contains(vm);
                if (!valid) allValid = false;
            }

            vm.IsNameValid = valid;
        }

        return allValid;
    }

    private void UpdateSaveButtonEnabled(bool allValid)
    {
        _namesValid = allValid;
        // A zero-step formula (or one with every step inlined) still produces a
        // valid no-op rewrite, so Save stays enabled — there's simply nothing
        // to change. Only invalid names block Save.
        SaveButton.IsEnabled = allValid;
        RefreshStatusText();
    }

    private void RefreshStatusText()
    {
        if (!_namesValid)
        {
            StatusText.Text = InvalidNameStatusText;
            return;
        }

        var stepCount = _rows.Count(r => r.Include);
        StatusText.Text = stepCount switch
        {
            0 => "No steps — nothing to unnest",
            1 => "1 step",
            _ => stepCount + " steps"
        };
    }
}

/// <summary>
///     Row view-model bound to <see cref="UnnestToLetWindow" />'s steps list.
///     Carries the step's <see cref="Key" /> identity (used to round-trip user
///     state back to the engine via <see cref="UnnestRowState" />), the
///     user-editable <see cref="Name" /> + <see cref="Include" />, the
///     read-only <see cref="Rhs" /> (refreshed by the engine as Include
///     toggles change which children collapse to names), and the origin
///     <see cref="BadgeText" />. <see cref="IsNameValid" /> is set by the
///     dialog on every validation pass and drives the red border on the Name
///     TextBox.
/// </summary>
public class UnnestStepRowVm : INotifyPropertyChanged
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

    private bool _include;
    private bool _isNameValid = true;
    private string _name;
    private string _rhs;

    public UnnestStepRowVm(UnnestStepRow step)
    {
        Key = step.Key;
        _name = step.Name;
        _rhs = step.Rhs;
        _include = step.Include;
        BadgeText = step.Origin == UnnestStepOrigin.Function
            ? "function: " + step.OriginLabel
            : "operator: " + step.OriginLabel;
    }

    public string Key { get; }
    public string BadgeText { get; }

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

    public string Rhs
    {
        get => _rhs;
        set
        {
            if (_rhs == value) return;
            _rhs = value;
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