using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LambdaBoss.UI;

/// <summary>
///     The edit mode of <c>/State</c> (spec 0011, part 2): one row per state variable and a blank
///     row to add one, with a live preview. Each row remembers the key it started as, so a new
///     name reads as a rename, not a remove and an add.
/// </summary>
public partial class StateEditWindow
{
    private readonly string _formula;
    private readonly List<RowBoxes> _rows = new();
    private string? _message;

    public StateEditWindow(string formula, IReadOnlyList<StateEditRow> rows, string? message)
    {
        InitializeComponent();

        _formula = formula;
        _message = message;

        foreach (var row in rows) AddRow(row);
        EnsureBlankRow();
        Refresh();

        // Adding is the usual edit, so the blank row is where the caret starts.
        Loaded += (_, _) =>
        {
            var first = _rows.First(r => r.OriginalKey == null && r.Name.Text.Length == 0).Name;
            first.Focus();
            first.SelectAll();
        };
    }

    /// <summary>The edited step as saved. Null when the dialog was cancelled.</summary>
    public string? Formula { get; private set; }

    /// <summary>The rows as they stand, for re-opening the dialog.</summary>
    public IReadOnlyList<StateEditRow> CurrentRows => _rows.Select(r => r.ToRow()).ToList();

    private void AddRow(StateEditRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        var isNew = row.OriginalKey == null;
        var name = new TextBox { Text = row.Key };
        var value = new TextBox
        {
            Text = row.Value,
            Margin = new Thickness(6, 0, 0, 0),
            IsReadOnly = !isNew,
            IsTabStop = isNew,
            ToolTip = isNew ? "The new variable's value in the sample state" : "Edit it in the cell"
        };

        if (isNew) value.TextChanged += Field_TextChanged;
        name.TextChanged += Field_TextChanged;

        Grid.SetColumn(name, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(name);
        grid.Children.Add(value);

        if (!isNew)
        {
            var remove = new Button
            {
                Content = "✕",
                Margin = new Thickness(6, 0, 0, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(0),
                IsTabStop = false,
                ToolTip = $"Remove {row.OriginalKey}"
            };
            remove.Click += (_, _) => name.Text = "";
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
        }

        RowsPanel.Children.Add(grid);
        _rows.Add(new RowBoxes(row.OriginalKey, name, value));
    }

    private void EnsureBlankRow()
    {
        var last = _rows.LastOrDefault();
        if (last == null || last.OriginalKey != null || last.Name.Text.Length > 0 || last.Value.Text.Length > 0)
            AddRow(new StateEditRow(null, "", ""));
    }

    private void Refresh()
    {
        var result = StateEdit.Apply(_formula, CurrentRows);
        PreviewBox.Text = (result.Formula ?? "").Replace("\n", Environment.NewLine);
        SaveButton.IsEnabled = result.Formula != null && result.Formula != _formula;

        var lines = new List<string>();
        if (_message != null) lines.Add(_message);
        if (result.Error != null) lines.Add(result.Error);
        lines.AddRange(result.Blockers);

        MessageText.Text = string.Join(Environment.NewLine, lines);
        MessageText.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e)
    {
        _message = null;
        EnsureBlankRow();
        Refresh();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SaveButton.IsEnabled && !CancelButton.IsKeyboardFocused)
        {
            SaveButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Formula = StateEdit.Apply(_formula, CurrentRows).Formula;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private sealed record RowBoxes(string? OriginalKey, TextBox Name, TextBox Value)
    {
        public StateEditRow ToRow() => new(OriginalKey, Name.Text, Value.Text);
    }
}
