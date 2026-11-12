using System.Globalization;
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

    // The ticks as the dialog opened: while they stand, the answer is left as the cell has it
    // (a key with no row to tick, Tim's own spacing).
    private readonly List<string> _openedAnswer;

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

        // The answer's variables come first, in its order (spec 0018); then the rest in state order.
        var answer = engine.Answer;
        var rows = engine.Rows.Select((r, i) => (Row: r, State: i,
                At: answer.ToList().FindIndex(k => string.Equals(k, r.Key, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(r => r.At < 0 ? answer.Count + r.State : r.At)
            .ToList();
        foreach (var (row, state, at) in rows) AddRow(row, state, at >= 0);
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
        _openedAnswer = AnswerKeys();

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

    // The rows go to the engine in state order (new ones last, as typed); the answer is the
    // ticked rows top to bottom, by the names they now have.
    private StateEngineChange CurrentChange() => new(
        _rows.OrderBy(r => r.State).Select(r => r.ToRow()).ToList(),
        SelectedFold,
        NameBox.Text.Trim().Length == 0 ? null : NameBox.Text.Trim(),
        DebugBox.IsChecked == true,
        AnswerChanged() ? AnswerKeys() : null);

    private bool AnswerChanged() => !AnswerKeys().SequenceEqual(_openedAnswer, StringComparer.OrdinalIgnoreCase);

    private List<string> AnswerKeys() =>
        _rows.Where(r => r.Answer.IsChecked == true && r.Name.Text.Trim().Length > 0)
            .Select(r => r.Name.Text.Trim())
            .ToList();

    private static bool Same(StateFoldKind a, StateFoldKind b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private int _nextNewRow = int.MaxValue / 2;

    private void AddRow(StateEditRow row, int? state = null, bool answer = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        // Ticked: part of the answer; its number is its place in the answer (spec 0018).
        var tick = new CheckBox
        {
            IsChecked = answer,
            IsTabStop = false,
            Foreground = new SolidColorBrush(Color.FromRgb(0xdc, 0xdc, 0xaa)),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "In the answer (Alt+A). Alt+Up/Down sets its place."
        };
        tick.Checked += Answer_Changed;
        tick.Unchecked += Answer_Changed;
        grid.Children.Add(tick);

        var name = new TextBox { Text = row.Key };
        name.TextChanged += Field_TextChanged;
        Grid.SetColumn(name, 1);
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
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
        }

        RowsPanel.Children.Add(grid);
        _rows.Add(new RowBox(row.OriginalKey, name, tick, grid, state ?? _nextNewRow++));
    }

    private void Answer_Changed(object sender, RoutedEventArgs e) => Refresh();

    // A row Tim can tick or move: a variable, or a new one he has typed.
    private static bool IsReal(RowBox row) => row.OriginalKey != null || row.Name.Text.Length > 0;

    /// <summary>Alt+Up and Alt+Down move the focused row; the ticked rows' order is the answer's.</summary>
    private void MoveFocusedRow(int by)
    {
        var at = _rows.FindIndex(r => r.Name.IsKeyboardFocusWithin);
        var to = at + by;
        if (at < 0 || to < 0 || to >= _rows.Count || !IsReal(_rows[at]) || !IsReal(_rows[to])) return;

        var row = _rows[at];
        _rows.RemoveAt(at);
        _rows.Insert(to, row);
        RowsPanel.Children.Remove(row.Grid);
        RowsPanel.Children.Insert(to, row.Grid);
        row.Name.Focus();
        Refresh();
    }

    // Alt+Up/Down and Alt+A act on a variable row only: elsewhere (the Form box) they keep their own meaning.
    private bool RowHasFocus() => _rows.Any(r => r.Name.IsKeyboardFocusWithin);

    private void ToggleFocusedAnswer()
    {
        var row = _rows.FirstOrDefault(r => r.Name.IsKeyboardFocusWithin);
        if (row != null && IsReal(row)) row.Answer.IsChecked = row.Answer.IsChecked != true;
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

        var place = 0;
        foreach (var row in _rows)
            row.Answer.Content = row.Answer.IsChecked == true && row.Name.Text.Trim().Length > 0 ? (++place).ToString(CultureInfo.InvariantCulture) : "";

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
        else if (IsToggleDebugKey(e))
        {
            // Alt+D (spec 0017): flip Debug and save in one press. Debug_Changed has switched the
            // form and refreshed the preview by the time IsChecked returns.
            DebugBox.IsChecked = DebugBox.IsChecked != true;
            if (SaveButton.IsEnabled)
                SaveButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (MoveRowKey(e.Key, e.SystemKey, Keyboard.Modifiers) is { } by && RowHasFocus())
        {
            MoveFocusedRow(by);
            e.Handled = true;
        }
        else if (IsAnswerKey(e.Key, e.SystemKey, Keyboard.Modifiers) && RowHasFocus())
        {
            ToggleFocusedAnswer();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SaveButton.IsEnabled && !CancelButton.IsKeyboardFocused && !FormBox.IsDropDownOpen)
        {
            SaveButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    /// <summary>Alt+D, the Debug box's access key: WPF reports Alt combinations as Key.System.</summary>
    internal static bool IsToggleDebugKey(Key key, Key systemKey, ModifierKeys modifiers) =>
        key == Key.System && systemKey == Key.D && modifiers == ModifierKeys.Alt;

    /// <summary>Alt+Up moves the focused row up (-1), Alt+Down down (+1): the move-line keys of code editors.</summary>
    internal static int? MoveRowKey(Key key, Key systemKey, ModifierKeys modifiers) =>
        key != Key.System || modifiers != ModifierKeys.Alt ? null
        : systemKey == Key.Up ? -1
        : systemKey == Key.Down ? 1
        : null;

    /// <summary>Alt+A ticks or unticks the focused row for the answer.</summary>
    internal static bool IsAnswerKey(Key key, Key systemKey, ModifierKeys modifiers) =>
        key == Key.System && systemKey == Key.A && modifiers == ModifierKeys.Alt;

    private static bool IsToggleDebugKey(KeyEventArgs e) =>
        IsToggleDebugKey(e.Key, e.SystemKey, Keyboard.Modifiers);

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Change = CurrentChange();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private sealed record RowBox(string? OriginalKey, TextBox Name, CheckBox Answer, Grid Grid, int State)
    {
        public StateEditRow ToRow() => new(OriginalKey, Name.Text, "");
    }
}
