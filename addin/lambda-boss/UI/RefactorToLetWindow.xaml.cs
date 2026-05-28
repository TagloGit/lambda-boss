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
///     Spec 0008 / PR 1 — the <c>/Refactor</c> dialog. Shows the active
///     cell's original formula, the engine-extracted input bindings (with
///     editable names, Include checkbox, and drag/Alt+Up-Down reorder),
///     and a live preview of the synthesised LET. Save returns the
///     preview text via <see cref="SavedFormula" />; Cancel discards.
///
///     The code-behind is intentionally thin: every row-state change
///     (rename, Include toggle, reorder) calls back into the supplied
///     <c>recompute</c> delegate (which wraps
///     <see cref="RefactorEngine.Recompute" />), and the result replaces
///     the preview + the row collection in place. Live name validation
///     gates the Save button via per-row canonicality (mirrors
///     <see cref="GatherWindow" />'s pattern but without the engine-side
///     collision-suffix dance — /Refactor is a one-shot pass, so the
///     dialog enforces both shape and cross-row uniqueness up front).
/// </summary>
public partial class RefactorToLetWindow
{
    private const string DefaultStatusText =
        "Save writes the LET into the active cell · Esc to cancel";

    private const string InvalidNameStatusText =
        "Fix invalid names before saving";

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

        BuildRowsFromInputs(initial.Inputs);
        _rows.CollectionChanged += Rows_CollectionChanged;
        InputsList.ItemsSource = _rows;

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
            .Select(r => new RefactorRowState(r.Source, r.Name, r.Include))
            .ToList();

        var newResult = _recompute(rowStates);
        if (newResult.Diagnostic != null)
            return;

        _suppressRecompute = true;
        try
        {
            _result = newResult;
            PreviewText.Text = newResult.SynthesisedLet;
        }
        finally
        {
            _suppressRecompute = false;
        }
    }

    /// <summary>
    ///     Stamps each row's <see cref="RefactorInputRowVm.IsNameValid" />
    ///     based on per-row canonicality (shape + non-reserved) AND
    ///     cross-row uniqueness among included rows. Returns true when
    ///     every included row is valid AND no duplicates exist.
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
            {
                valid = true;
            }
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
        SaveButton.IsEnabled = allValid && _rows.Any(r => r.Include);
        if (!allValid)
            StatusText.Text = InvalidNameStatusText;
        else if (!_rows.Any(r => r.Include))
            StatusText.Text = "No bindings selected — nothing to refactor";
        else
            StatusText.Text = DefaultStatusText;
    }
}

/// <summary>
///     Row view-model bound to <see cref="RefactorToLetWindow" />'s
///     inputs list. Carries the row's <see cref="Source" /> identity and
///     the user-editable <see cref="Name" /> + <see cref="Include" />.
///     <see cref="IsNameValid" /> is set by the dialog on every
///     validation pass and drives the red-border on the Name TextBox.
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
        Source = input.Source;
        _name = input.Name;
        Rhs = input.Rhs;
    }

    public FormulaRef Source { get; }
    public string Rhs { get; }

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
