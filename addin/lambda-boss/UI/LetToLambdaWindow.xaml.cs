using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LambdaBoss.UI;

public class LetInputRow : INotifyPropertyChanged
{
    private bool _canMoveDown;
    private bool _canMoveUp;
    private bool _isOptional;
    private bool _keep = true;
    private string _paramName = "";

    public string BindingName { get; set; } = "";
    public string RhsPreview { get; set; } = "";

    /// <summary>
    ///     Display form of <see cref="RhsPreview" /> used by the row template.
    ///     When the row is marked optional, the RHS becomes the default
    ///     expression in the generated LAMBDA, so we prefix "default:" to
    ///     make that role explicit.
    /// </summary>
    public string RhsPreviewDisplay => IsOptional ? $"default: {RhsPreview}" : RhsPreview;

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
                OnChanged(nameof(RhsPreviewDisplay));
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
            OnChanged(nameof(RhsPreviewDisplay));
        }
    }

    public bool CanMoveUp
    {
        get => _canMoveUp;
        set
        {
            if (_canMoveUp == value) return;
            _canMoveUp = value;
            OnChanged();
        }
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        set
        {
            if (_canMoveDown == value) return;
            _canMoveDown = value;
            OnChanged();
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

    /// <summary>
    ///     Creates the dialog. <paramref name="resolveExistingRefersTo" />
    ///     returns the <c>RefersTo</c> of an existing workbook name (or
    ///     <c>null</c> if the name isn't taken). When the existing name is a
    ///     LAMBDA, Save proceeds as an overwrite so authors can round-trip
    ///     via Edit Lambda and re-register with the same name.
    /// </summary>
    public LetToLambdaWindow(ParsedLet parsed, Func<string, string?> resolveExistingRefersTo)
    {
        InitializeComponent();
        _parsed = parsed;
        _resolveExistingRefersTo = resolveExistingRefersTo;

        var valueBindings = parsed.Bindings
            .Where(b => !b.IsCalculation)
            .ToList();

        _rows = new ObservableCollection<LetInputRow>(
            valueBindings.Select((b, i) => new LetInputRow
            {
                BindingName = b.Name,
                ParamName = b.Name,
                RhsPreview = b.RhsText,
                Keep = true,
                SourceIndex = i
            }));

        foreach (var row in _rows)
            row.PropertyChanged += Row_PropertyChanged;

        InputsList.ItemsSource = _rows;
        UpdateReorderButtonStates();

        LambdaNameBox.Focus();
        UpdateSaveEnabled();
    }

    /// <summary>
    ///     Populated on successful Save. Null if cancelled.
    /// </summary>
    public LambdaGenerationRequest? Result { get; private set; }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LetInputRow.Keep) && sender is LetInputRow row)
            RepositionAfterKeepChanged(row);

        if (e.PropertyName is nameof(LetInputRow.IsOptional) or nameof(LetInputRow.Keep))
            UpdateOptionalWarningVisibility();

        UpdateSaveEnabled();
    }

    private void UpdateOptionalWarningVisibility()
    {
        var anyOptional = _rows.Any(r => r.Keep && r.IsOptional);
        OptionalWarningText.Visibility = anyOptional ? Visibility.Visible : Visibility.Collapsed;
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

        UpdateReorderButtonStates();
    }

    private void UpdateReorderButtonStates()
    {
        var firstKept = -1;
        var lastKept = -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].Keep) continue;
            if (firstKept < 0) firstKept = i;
            lastKept = i;
        }

        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (!r.Keep)
            {
                r.CanMoveUp = false;
                r.CanMoveDown = false;
                continue;
            }

            r.CanMoveUp = i > firstKept;
            r.CanMoveDown = i < lastKept;
        }
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
                UpdateReorderButtonStates();
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
                UpdateReorderButtonStates();
                UpdateSaveEnabled();
                return;
            }
        }
    }

    private void MoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LetInputRow row })
            MoveRowUp(row);
    }

    private void MoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LetInputRow row })
            MoveRowDown(row);
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

    private void LambdaNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSaveEnabled();
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

        var retainedBindingNames = _parsed.Bindings
            .Where(b => b.IsCalculation || _rows.Any(r => r.BindingName == b.Name && !r.Keep))
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
            .Select(r => new InputChoice(r.BindingName, r.ParamName.Trim(), r.Keep, r.IsOptional))
            .ToList();

        Result = new LambdaGenerationRequest(LambdaNameBox.Text.Trim(), _parsed, inputs);
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
