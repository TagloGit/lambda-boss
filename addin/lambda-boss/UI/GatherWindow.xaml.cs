using System.Windows;
using System.Windows.Input;

namespace LambdaBoss.UI;

/// <summary>
///     Read-only display of a <see cref="GatherResult" />. PR 1 wires the
///     header, binding list, and preview pane; Save returns the engine's
///     synthesised LET text so the caller can write it back to the sink.
///     Editable rows, role toggles, and live re-rendering land in PR 10+.
/// </summary>
public partial class GatherWindow
{
    private readonly GatherResult _result;

    public GatherWindow(GatherResult result)
    {
        InitializeComponent();
        _result = result;

        SinkAddressText.Text = result.Sink.A1Address;
        OriginalFormulaText.Text = result.OriginalFormula;
        PreviewText.Text = result.SynthesisedLet;

        BindingsList.ItemsSource = result.Bindings
            .Select(b => new GatherRowDisplay
            {
                Address = b.CellRef.A1Address,
                Role = b.Role == BindingRole.Input ? "input" : "step",
                Name = b.Name,
                Rhs = b.Rhs,
            })
            .ToList();

        StatusText.Text = "Save writes the LET into the sink cell · Esc to cancel";
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
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SaveButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}

internal sealed class GatherRowDisplay
{
    public string Address { get; init; } = "";
    public string Role { get; init; } = "";
    public string Name { get; init; } = "";
    public string Rhs { get; init; } = "";
}
