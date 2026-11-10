using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LambdaBoss.UI;

/// <summary>
///     The <c>/State</c> dialog (spec 0015): the variables by name, the form (LET step or a fold)
///     and the step's name, with a live preview of the cell and of the named LAMBDA. Excel's thread
///     waits on this window, so everything it needs about the workbook is handed in.
/// </summary>
public partial class StateWindow
{
    private const string LetStepLabel = "LET step (unfolded)";

    private readonly StateEngine _engine;
    private readonly Func<string, string?> _refersTo;
    private readonly Func<StateFoldKind, string> _callName;
    private readonly List<RowBox> _rows = new();
    private readonly List<StateFoldKind> _folds = new();
    private readonly bool _loaded;

    public StateWindow(
        StateEngine engine,
        IReadOnlyList<StateFoldKind> libraryFolds,
        Func<string, string?> refersTo,
        Func<StateFoldKind, string> callName,
        string? note)
    {
        InitializeComponent();

        _engine = engine;
        _refersTo = refersTo;
        _callName = callName;

        HeaderText.Text = StateEngines.Describe(engine) + (note == null ? "" : " " + note);

        foreach (var row in engine.Rows) AddRow(row);
        EnsureBlankRow();

        var folds = new List<StateFoldKind> { StateFoldKind.Reduce, StateFoldKind.Scan };
        folds.AddRange(libraryFolds);
        if (engine.Fold != null && !folds.Any(f => Same(f, engine.Fold))) folds.Add(engine.Fold);

        FormBox.Items.Add(new ComboBoxItem { Content = LetStepLabel, Tag = null });
        foreach (var fold in folds)
        {
            var label = fold.IsNative ? fold.Name : $"{fold.Name}  ({string.Join(", ", fold.StepParameters)})";
            FormBox.Items.Add(new ComboBoxItem { Content = label, Tag = fold });
        }

        _folds.AddRange(folds);

        var debugKeys = engine.DebugKeys;
        if (debugKeys.Count > 0)
        {
            DebugText.Text = "Debug outputs, not state variables: " + string.Join(", ", debugKeys) + ".";
            DebugText.Visibility = Visibility.Visible;
        }

        FormBox.SelectedIndex = engine.Fold == null
            ? 0
            : 1 + folds.FindIndex(f => Same(f, engine.Fold));
        NameBox.Text = engine.StepName ?? "";
        DebugBox.IsChecked = engine.IsDebug;

        _loaded = true;
        Refresh();

        Loaded += (_, _) =>
        {
            var blank = _rows.First(r => r.OriginalKey == null && r.Name.Text.Length == 0).Name;
            blank.Focus();
        };
    }

    /// <summary>The change as saved. Null when the dialog was cancelled.</summary>
    public StateEngineChange? Change { get; private set; }

    private StateFoldKind? SelectedFold => (FormBox.SelectedItem as ComboBoxItem)?.Tag as StateFoldKind;

    private StateEngineChange CurrentChange() => new(
        _rows.Select(r => r.ToRow()).ToList(),
        SelectedFold,
        NameBox.Text.Trim().Length == 0 ? null : NameBox.Text.Trim(),
        DebugBox.IsChecked == true);

    private static bool Same(StateFoldKind a, StateFoldKind b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private void AddRow(StateEditRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        var name = new TextBox { Text = row.Key };
        name.TextChanged += Field_TextChanged;
        grid.Children.Add(name);

        if (row.OriginalKey != null)
        {
            var remove = new Button
            {
                Content = "✕",
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = Brushes.Gray,
                BorderThickness = new Thickness(0),
                IsTabStop = false,
                ToolTip = $"Remove {row.OriginalKey}"
            };
            remove.Click += (_, _) => name.Text = "";
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);
        }

        RowsPanel.Children.Add(grid);
        _rows.Add(new RowBox(row.OriginalKey, name));
    }

    private void EnsureBlankRow()
    {
        var last = _rows.LastOrDefault();
        if (last == null || last.OriginalKey != null || last.Name.Text.Length > 0)
            AddRow(new StateEditRow(null, "", ""));
    }

    private void Refresh()
    {
        if (!_loaded) return;

        var result = StateEngines.Apply(_engine, CurrentChange(), _refersTo, _callName);

        var preview = result.Formula ?? "";
        if (result.Definition != null)
            preview += $"\n\n{result.DefinitionName} =\n{result.Definition[1..]}";
        PreviewBox.Text = preview.Replace("\n", Environment.NewLine);
        SaveButton.IsEnabled = result.Formula != null;

        var lines = new List<string>();
        if (result.Error != null) lines.Add(result.Error);
        lines.AddRange(result.Blockers);
        lines.AddRange(result.Notes);

        var dropping = _engine.IsDebug && DebugBox.IsChecked != true && _engine.DebugKeys.Count > 0;
        DebugText.Text = "Debug outputs, not state variables: " + string.Join(", ", _engine.DebugKeys)
            + (dropping ? ". Save drops them." : ".");

        MessageText.Text = string.Join(Environment.NewLine, lines);
        MessageText.Foreground = result.Error != null
            ? new SolidColorBrush(Color.FromRgb(0xe7, 0x48, 0x56))
            : new SolidColorBrush(Color.FromRgb(0xd7, 0xba, 0x7d));
        MessageText.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        EnsureBlankRow();
        Refresh();
    }

    private void Form_SelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    // Turning debug on or off switches the form to its twin (REDUCE and SCAN, REDUCEUNTIL and
    // SCANUNTIL, ...) so one click does the whole job; the Form box can still be changed after.
    private void Debug_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;

        // On: the SCAN twin. Off: only when the dialog opened in debug form does the fold go back
        // to its REDUCE twin; otherwise back to the form it opened with (a plain SCAN stays a SCAN).
        var on = DebugBox.IsChecked == true;
        StateFoldKind? target = null;
        if (SelectedFold is { } current)
        {
            if (on || _engine.IsDebug)
                target = StateDebug.Twin(current, _folds, on);
            else if (_engine.Fold != null
                && StateDebug.Twin(_engine.Fold, _folds, toScan: true) is { } scan && Same(current, scan))
                target = _engine.Fold;
        }

        var at = target == null ? -1 : _folds.FindIndex(f => Same(f, target));
        if (at >= 0)
        {
            FormBox.SelectedIndex = 1 + at;
            return;
        }

        Refresh();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SaveButton.IsEnabled && !CancelButton.IsKeyboardFocused && !FormBox.IsDropDownOpen)
        {
            SaveButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Change = CurrentChange();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private sealed record RowBox(string? OriginalKey, TextBox Name)
    {
        public StateEditRow ToRow() => new(OriginalKey, Name.Text, "");
    }
}
