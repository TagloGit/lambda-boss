using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GongSolutions.Wpf.DragDrop;

namespace LambdaBoss.UI;

/// <summary>The fold to write once the LAMBDA is registered.</summary>
public record FoldWrite(string Formula, string Target);

public class LetInputRow : INotifyPropertyChanged
{
    public const string StateBadge = "state";
    public const string InputBadge = "input";

    private string _badge = "";
    private bool _isStateStepRow;
    private bool _isOptional;
    private bool _keep = true;
    private string _paramName = "";
    private string _defaultExpression = "";

    public string BindingName { get; set; } = "";
    public string RhsPreview { get; set; } = "";

    /// <summary>
    ///     The default expression for an optional param, editable in the row
    ///     template when <see cref="IsOptional" /> is set. Seeded with the
    ///     original RHS so the dialog opens showing the existing behaviour.
    /// </summary>
    public string DefaultExpression
    {
        get => _defaultExpression;
        set
        {
            _defaultExpression = value;
            OnChanged();
        }
    }

    /// <summary>
    ///     The read-only RHS preview is shown only while the row is not
    ///     optional; when optional, the editable default box takes its place.
    /// </summary>
    public bool ShowRhsPreview => !IsOptional;

    /// <summary>The row's role in a state step: <see cref="StateBadge" />, <see cref="InputBadge" /> or empty.</summary>
    public string Badge
    {
        get => _badge;
        set
        {
            _badge = value;
            OnChanged();
            OnChanged(nameof(ShowBadge));
            OnChanged(nameof(CanTakeInputBadge));
        }
    }

    public bool ShowBadge => Badge.Length > 0;

    /// <summary>
    ///     A state row's RHS is a whole SetVars call, so it's capped to leave room for the
    ///     parameter box. Ordinary rows stay uncapped.
    /// </summary>
    public double RhsPreviewMaxWidth => _isStateStepRow ? 200 : double.PositiveInfinity;

    /// <summary>True on the rows of a state step that the input badge can move to.</summary>
    public bool CanTakeInputBadge => _isStateStepRow && Badge.Length == 0;

    public bool IsStateStepRow
    {
        get => _isStateStepRow;
        set
        {
            _isStateStepRow = value;
            OnChanged();
            OnChanged(nameof(CanTakeInputBadge));
        }
    }

    /// <summary>
    ///     Zero-based position in the original LET source order. Used to keep
    ///     unchecked rows sorted by source order.
    /// </summary>
    public int SourceIndex { get; set; }

    public string ParamName
    {
        get => _paramName;
        set
        {
            _paramName = value;
            OnChanged();
        }
    }

    public bool Keep
    {
        get => _keep;
        set
        {
            if (_keep == value) return;
            _keep = value;
            if (!_keep && _isOptional)
            {
                _isOptional = false;
                OnChanged(nameof(IsOptional));
                OnChanged(nameof(ShowRhsPreview));
            }
            OnChanged();
        }
    }

    public bool IsOptional
    {
        get => _isOptional;
        set
        {
            if (_isOptional == value) return;
            _isOptional = value;
            OnChanged();
            OnChanged(nameof(ShowRhsPreview));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? prop = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

public partial class LetToLambdaWindow
{
    private static readonly Brush ErrorBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e74856"));
    private static readonly Brush InfoBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#d7ba7d"));

    private readonly Func<string, string?> _resolveExistingRefersTo;
    private readonly ParsedLet _parsed;
    private readonly ObservableCollection<LetInputRow> _rows;
    private readonly bool _isStateStep;
    private string _foldName = "";

    /// <summary>
    ///     Creates the dialog. <paramref name="resolveExistingRefersTo" />
    ///     returns the <c>RefersTo</c> of an existing workbook name (or
    ///     <c>null</c> if the name isn't taken). When the existing name is a
    ///     LAMBDA, Save proceeds as an overwrite so authors can round-trip
    ///     via Edit Lambda and re-register with the same name.
    ///     <paramref name="stateStep" /> is the recognised state step, or null to
    ///     treat the LET as an ordinary one.
    /// </summary>
    public LetToLambdaWindow(
        ParsedLet parsed,
        Func<string, string?> resolveExistingRefersTo,
        StateStep? stateStep = null)
    {
        InitializeComponent();
        _parsed = parsed;
        _resolveExistingRefersTo = resolveExistingRefersTo;
        _isStateStep = stateStep != null;

        var rowBindings = stateStep != null
            ? StateStepParser.DialogBindings(parsed, stateStep)
            : parsed.Bindings.Where(b => !b.IsCalculation).ToList();

        _rows = new ObservableCollection<LetInputRow>(
            rowBindings.Select((b, i) => new LetInputRow
            {
                BindingName = b.Name,
                ParamName = b.Name,
                RhsPreview = b.RhsText,
                DefaultExpression = b.RhsText,
                // REDUCE passes exactly two arguments, so only the step's roles start kept.
                Keep = stateStep == null || b == stateStep.StateBinding || b == stateStep.InputBinding,
                Badge = stateStep == null ? ""
                    : b == stateStep.StateBinding ? LetInputRow.StateBadge
                    : b == stateStep.InputBinding ? LetInputRow.InputBadge
                    : "",
                IsStateStepRow = stateStep != null,
                SourceIndex = i
            }));

        foreach (var row in _rows)
            row.PropertyChanged += Row_PropertyChanged;
        _rows.CollectionChanged += Rows_CollectionChanged;

        InputsList.ItemsSource = _rows;

        var keepHandler = new KeptRowReorderHandler();
        GongSolutions.Wpf.DragDrop.DragDrop.SetDragHandler(InputsList, keepHandler);
        GongSolutions.Wpf.DragDrop.DragDrop.SetDropHandler(InputsList, keepHandler);

        if (stateStep != null)
        {
            ShowFold(parsed, stateStep);

            // An Edit Lambda stepper prototype names its step: offer it, so saving back is one Enter (#447).
            var stepName = StateStepper.PrototypeStepName(stateStep.StateBinding.RhsText, resolveExistingRefersTo);
            if (stepName != null)
            {
                LambdaNameBox.Text = stepName;
                LambdaNameBox.SelectAll();
            }
        }

        LambdaNameBox.Focus();
        UpdateSaveEnabled();
    }

    /// <summary>
    ///     Populated on successful Save. Null if cancelled.
    /// </summary>
    public LambdaGenerationRequest? Result { get; private set; }

    /// <summary>
    ///     Populated on Save when the fold is complete and has a target cell.
    /// </summary>
    public FoldWrite? FoldToWrite { get; private set; }

    private void ShowFold(ParsedLet parsed, StateStep step)
    {
        Height += 110;
        FoldPanel.Visibility = Visibility.Visible;
        FoldBox.Text = StateFold.Build(StateFold.Seed(parsed, step), "");

        // Name, fold, target: the path under the clock, ahead of the input rows.
        LambdaNameBox.TabIndex = 0;
        FoldBox.TabIndex = 1;
        FoldTargetBox.TabIndex = 2;

        UpdateAbsolutizeWarningVisibility();
    }

    private void FoldBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Queued, because tabbing into a TextBox selects its text after this event.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var caret = StateFold.InputsCaret(FoldBox.Text);
            if (caret >= 0) FoldBox.CaretIndex = caret;
        }));
    }

    private void Fold_TextChanged(object sender, TextChangedEventArgs e)
    {
        var target = FoldTargetBox.Text.Trim();
        var complete = StateFold.IsComplete(FoldBox.Text);

        if (target.Length == 0)
            FoldHintText.Text = "Blank writes nothing. Ctrl+C copies the fold.";
        else if (!StateFold.IsCellAddress(target))
            FoldHintText.Text = "Not a cell on this sheet, so nothing will be written.";
        else if (!complete)
            FoldHintText.Text = "Needs a seed and inputs before it can be written.";
        else
            FoldHintText.Text = $"Written to {target.ToUpperInvariant()} after Save.";
    }

    private void MakeInput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LetInputRow row }) return;

        var previous = _rows.FirstOrDefault(r => r.Badge == LetInputRow.InputBadge);
        if (previous != null)
        {
            previous.Badge = "";
            previous.Keep = false;
        }

        row.Badge = LetInputRow.InputBadge;
        row.Keep = true;

        var stateIndex = _rows.IndexOf(_rows.First(r => r.Badge == LetInputRow.StateBadge));
        _rows.Move(_rows.IndexOf(row), stateIndex + 1);

        // The clicked button has just collapsed, so give the keyboard somewhere to be.
        LambdaNameBox.Focus();
    }

    private void CopyFold()
    {
        try
        {
            Clipboard.SetText(FoldBox.Text);
            FoldHintText.Text = "Fold copied.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app holds the clipboard; say so rather than pretend.
            FoldHintText.Text = "Clipboard busy: copy again.";
        }
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LetInputRow.Keep) && sender is LetInputRow row)
            RepositionAfterKeepChanged(row);

        if (e.PropertyName is nameof(LetInputRow.IsOptional) or nameof(LetInputRow.Keep))
            UpdateAbsolutizeWarningVisibility();

        UpdateSaveEnabled();
    }

    private void UpdateAbsolutizeWarningVisibility()
    {
        var anyAbsolutized = _rows.Any(r => (r.Keep && r.IsOptional) || !r.Keep);
        OptionalWarningText.Visibility = anyAbsolutized ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    ///     Maintain the invariant that <see cref="_rows" /> is [kept rows in
    ///     user-chosen order] followed by [unchecked rows in source order] by
    ///     moving the row that just changed Keep into its correct slot.
    /// </summary>
    private void RepositionAfterKeepChanged(LetInputRow row)
    {
        var currentIndex = _rows.IndexOf(row);
        if (currentIndex < 0) return;

        int targetIndex;
        if (row.Keep)
        {
            // Append to end of kept section.
            targetIndex = _rows.Count(r => r != row && r.Keep);
        }
        else
        {
            var keptCount = _rows.Count(r => r != row && r.Keep);
            var uncheckedBefore = _rows.Count(r =>
                r != row && !r.Keep && r.SourceIndex < row.SourceIndex);
            targetIndex = keptCount + uncheckedBefore;
        }

        if (targetIndex != currentIndex)
            _rows.Move(currentIndex, targetIndex);
    }

    private void MoveRowUp(LetInputRow row)
    {
        if (!row.Keep) return;
        var index = _rows.IndexOf(row);
        if (index <= 0) return;
        // Find previous kept row.
        for (var i = index - 1; i >= 0; i--)
        {
            if (_rows[i].Keep)
            {
                _rows.Move(index, i);
                UpdateSaveEnabled();
                return;
            }
        }
    }

    private void MoveRowDown(LetInputRow row)
    {
        if (!row.Keep) return;
        var index = _rows.IndexOf(row);
        if (index < 0) return;
        for (var i = index + 1; i < _rows.Count; i++)
        {
            if (_rows[i].Keep)
            {
                _rows.Move(index, i);
                UpdateSaveEnabled();
                return;
            }
        }
    }

    private void InputRow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        if (!alt) return;
        if (e.Key != Key.Up && e.Key != Key.Down && e.SystemKey != Key.Up && e.SystemKey != Key.Down)
            return;

        if (sender is not FrameworkElement { DataContext: LetInputRow row }) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Up)
            MoveRowUp(row);
        else if (key == Key.Down)
            MoveRowDown(row);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (_isStateStep
            && e.Key == Key.C
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && Keyboard.FocusedElement is not TextBox { SelectionLength: > 0 })
        {
            CopyFold();
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

    private void Rows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reorder via drag/drop fires Move; keep validation in sync.
        if (e.Action == NotifyCollectionChangedAction.Move)
            UpdateSaveEnabled();
    }

    private void LambdaNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isStateStep) SyncFoldName();

        UpdateSaveEnabled();
    }

    private void SyncFoldName()
    {
        var name = LambdaNameBox.Text.Trim();
        if (name.Length > 0 && !ExcelNameValidator.Validate(name).IsValid) return;

        // An unreadable fold stays as it is and keeps the old name, so a later change still finds it.
        var updated = StateFold.WithLambdaName(FoldBox.Text, _foldName, name);
        if (updated == FoldBox.Text) return;

        var caret = FoldBox.CaretIndex;
        FoldBox.Text = updated;
        FoldBox.CaretIndex = caret;
        _foldName = name;
    }

    private void UpdateSaveEnabled()
    {
        var name = LambdaNameBox.Text.Trim();
        var validation = ExcelNameValidator.Validate(name);

        if (!validation.IsValid)
        {
            ShowNameError(validation.Error!);
            SaveButton.IsEnabled = false;
            return;
        }

        var existingRefersTo = _resolveExistingRefersTo(name);
        if (existingRefersTo != null)
        {
            if (LambdaSignatureParser.IsLambdaFormula(existingRefersTo))
            {
                ShowNameInfo($"'{name}' already exists — Save will overwrite the existing LAMBDA.");
            }
            else
            {
                ShowNameError(
                    $"'{name}' already exists in this workbook and is not a LAMBDA. Choose another name.");
                SaveButton.IsEnabled = false;
                return;
            }
        }
        else
        {
            HideNameError();
        }

        // Validate rows: kept rows must have non-empty unique param names
        // not colliding with any retained LET binding name.
        var keptRows = _rows.Where(r => r.Keep).ToList();
        if (keptRows.Any(r => string.IsNullOrWhiteSpace(r.ParamName)))
        {
            StatusText.Text = "Every kept input needs a parameter name.";
            SaveButton.IsEnabled = false;
            return;
        }

        var dup = keptRows
            .GroupBy(r => r.ParamName.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (dup != null)
        {
            StatusText.Text = $"Duplicate parameter name: '{dup.Key}'.";
            SaveButton.IsEnabled = false;
            return;
        }

        var blankDefault = keptRows
            .FirstOrDefault(r => r.IsOptional && string.IsNullOrWhiteSpace(r.DefaultExpression));
        if (blankDefault != null)
        {
            StatusText.Text = $"Optional input '{blankDefault.ParamName}' needs a default value.";
            SaveButton.IsEnabled = false;
            return;
        }

        var retainedBindingNames = _parsed.Bindings
            .Where(b => (b.IsCalculation && keptRows.All(r => r.BindingName != b.Name))
                || _rows.Any(r => r.BindingName == b.Name && !r.Keep))
            .Select(b => b.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collidingParam = keptRows
            .FirstOrDefault(r => retainedBindingNames.Contains(r.ParamName.Trim()));
        if (collidingParam != null)
        {
            StatusText.Text =
                $"Parameter '{collidingParam.ParamName}' collides with an internal binding.";
            SaveButton.IsEnabled = false;
            return;
        }

        StatusText.Text = "";
        SaveButton.IsEnabled = true;
    }

    private void ShowNameError(string message)
    {
        NameErrorText.Text = message;
        NameErrorText.Foreground = ErrorBrush;
        NameErrorText.Visibility = Visibility.Visible;
    }

    private void ShowNameInfo(string message)
    {
        NameErrorText.Text = message;
        NameErrorText.Foreground = InfoBrush;
        NameErrorText.Visibility = Visibility.Visible;
    }

    private void HideNameError()
    {
        NameErrorText.Text = "";
        NameErrorText.Visibility = Visibility.Collapsed;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var inputs = _rows
            .Select(r => new InputChoice(
                r.BindingName,
                r.ParamName.Trim(),
                r.Keep,
                r.IsOptional,
                r.IsOptional ? r.DefaultExpression.Trim() : null))
            .ToList();

        StateStepRoles? roles = null;
        if (_isStateStep)
        {
            roles = new StateStepRoles(
                _rows.First(r => r.Badge == LetInputRow.StateBadge).BindingName,
                _rows.FirstOrDefault(r => r.Badge == LetInputRow.InputBadge)?.BindingName);

            var target = FoldTargetBox.Text.Trim();
            if (StateFold.IsCellAddress(target) && StateFold.IsComplete(FoldBox.Text))
                FoldToWrite = new FoldWrite(FoldBox.Text.Trim(), target);
        }

        Result = new LambdaGenerationRequest(LambdaNameBox.Text.Trim(), _parsed, inputs, roles);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
        Close();
    }
}

/// <summary>
///     Restricts gong-wpf-dragdrop reorder to kept rows on both ends.
///     Unchecked rows can't be dragged or dropped onto, which preserves
///     the kept-rows-first invariant in <c>_rows</c>. The base
///     <see cref="DefaultDropHandler" /> already does the right
///     <c>ObservableCollection.Move</c> for same-collection reorders.
/// </summary>
internal class KeptRowReorderHandler : DefaultDropHandler, IDragSource
{
    private readonly DefaultDragHandler _dragBase = new();

    public override void DragOver(IDropInfo dropInfo)
    {
        if (dropInfo.Data is LetInputRow source
            && dropInfo.TargetItem is LetInputRow target
            && source.Keep
            && target.Keep)
        {
            base.DragOver(dropInfo);
        }
        else
        {
            dropInfo.Effects = DragDropEffects.None;
            dropInfo.DropTargetAdorner = null;
        }
    }

    public void StartDrag(IDragInfo dragInfo)
    {
        if (dragInfo.SourceItem is LetInputRow row && row.Keep)
            _dragBase.StartDrag(dragInfo);
        else
            dragInfo.Effects = DragDropEffects.None;
    }

    public bool CanStartDrag(IDragInfo dragInfo) =>
        dragInfo.SourceItem is LetInputRow row && row.Keep && _dragBase.CanStartDrag(dragInfo);

    public void Dropped(IDropInfo dropInfo) => _dragBase.Dropped(dropInfo);

    public void DragDropOperationFinished(DragDropEffects operationResult, IDragInfo dragInfo) =>
        _dragBase.DragDropOperationFinished(operationResult, dragInfo);

    public void DragCancelled() => _dragBase.DragCancelled();

    public bool TryCatchOccurredException(Exception exception) =>
        _dragBase.TryCatchOccurredException(exception);
}
