using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace LambdaBoss.UI;

public class LetInputRow : INotifyPropertyChanged
{
    private bool _keep = true;
    private string _paramName = "";

    public string BindingName { get; set; } = "";
    public string RhsPreview { get; set; } = "";

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
            _keep = value;
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
    private readonly Func<string, bool> _nameCollides;
    private readonly ParsedLet _parsed;
    private readonly List<LetInputRow> _rows;

    public LetToLambdaWindow(ParsedLet parsed, Func<string, bool> nameCollides)
    {
        InitializeComponent();
        _parsed = parsed;
        _nameCollides = nameCollides;

        _rows = parsed.Bindings
            .Where(b => !b.IsCalculation)
            .Select(b => new LetInputRow
            {
                BindingName = b.Name,
                ParamName = b.Name,
                RhsPreview = b.RhsText,
                Keep = true
            })
            .ToList();

        foreach (var row in _rows)
            row.PropertyChanged += (_, _) => UpdateSaveEnabled();

        InputsList.ItemsSource = _rows;

        LambdaNameBox.Focus();
        UpdateSaveEnabled();
    }

    /// <summary>
    ///     Populated on successful Save. Null if cancelled.
    /// </summary>
    public LambdaGenerationRequest? Result { get; private set; }

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

        if (_nameCollides(name))
        {
            ShowNameError("Name already exists in this workbook.");
            SaveButton.IsEnabled = false;
            return;
        }

        HideNameError();

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
            .Select(r => new InputChoice(r.BindingName, r.ParamName.Trim(), r.Keep))
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