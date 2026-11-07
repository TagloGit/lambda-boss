using System.Windows;
using System.Windows.Input;

namespace LambdaBoss.UI;

/// <summary>What's typed in the <c>/State</c> dialog.</summary>
public record StateStepDraft(string State, string Input, string Target);

/// <summary>
///     The <c>/State</c> dialog (spec 0011, part 1): State, Input and Write to, with a live
///     preview. Excel's thread waits on this window, so it can't ask Excel about a cell. It's told
///     which cells are filled, and the command re-opens it when a typed target proves to be.
/// </summary>
public partial class StateStepWindow
{
    private readonly ISet<string> _filledCells;
    private readonly string _prefix;
    private bool _loaded;

    public StateStepWindow(
        StateStepDraft draft, string prefix, ISet<string> filledCells, string? note, string? message)
    {
        InitializeComponent();

        _prefix = prefix;
        _filledCells = filledCells;

        StateBox.Text = draft.State;
        InputBox.Text = draft.Input;
        TargetBox.Text = draft.Target;
        NoteText.Text = note ?? "";
        _message = message;
        _loaded = true;

        Refresh();

        // After a prefill the input is the only thing left to type.
        Loaded += (_, _) =>
        {
            var first = TargetIsFilled() ? TargetBox
                : draft.State.Length > 0 ? InputBox
                : StateBox;
            first.Focus();
            first.SelectAll();
        };
    }

    // Shown once, then the next edit clears it.
    private string? _message;

    /// <summary>The fields as saved. Null when the dialog was cancelled.</summary>
    public StateStepDraft? Result { get; private set; }

    /// <summary>The step's formula as saved.</summary>
    public string? Formula { get; private set; }

    private void Refresh()
    {
        var built = StateScaffold.Build(StateBox.Text, InputBox.Text, _prefix);
        PreviewBox.Text = (built.Formula ?? "").Replace("\n", Environment.NewLine);

        var targetError = TargetError();
        SaveButton.IsEnabled = built.Formula != null && targetError == null;

        var message = _message ?? built.Error ?? targetError;
        MessageText.Text = message ?? "";
        MessageText.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private string? TargetError()
    {
        var target = TargetBox.Text.Trim();
        if (!LetToLambdaBuilder.IsGridCell(target))
            return "Write to needs one cell on this sheet, like H270.";

        return TargetIsFilled() ? $"{target} isn't empty. Type another cell." : null;
    }

    private bool TargetIsFilled() => _filledCells.Contains(StateScaffold.NormalAddress(TargetBox.Text));

    private void Field_TextChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _message = null;
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
        Formula = StateScaffold.Build(StateBox.Text, InputBox.Text, _prefix).Formula;
        Result = new StateStepDraft(StateBox.Text, InputBox.Text, TargetBox.Text.Trim());
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
