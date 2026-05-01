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

        WalkHintText.Text = BuildWalkHint(result);
        SinkAddressText.Text = result.Sink.A1Address;
        OriginalFormulaText.Text = result.OriginalFormula;
        PreviewText.Text = result.SynthesisedLet;

        BindingsList.ItemsSource = result.Bindings
            .Select(b => new GatherRowDisplay
            {
                Address = b.Source.A1Address,
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

    /// <summary>
    ///     Renders the header hint per spec 0005 §"Selection-restricted
    ///     walk". A free walk (single-cell selection, or a multi-selection
    ///     that happened to cover every walked cell) reads
    ///     <c>Walking N cells from &lt;addr&gt;</c>; a restriction that
    ///     actually narrowed the walk reads
    ///     <c>Walking M of N cells from &lt;addr&gt; — restricted by selection</c>.
    ///     The "M == N" case is treated as a free walk in the header even
    ///     when the selection was multi-cell, matching the issue's "behaves
    ///     like a free walk" wording — restricting and then happening to
    ///     cover everything is observationally indistinguishable from a
    ///     free walk and showing "N of N" reads as noise.
    /// </summary>
    private static string BuildWalkHint(GatherResult result)
    {
        var addr = result.Sink.A1Address;
        var n = result.FreeWalkCount;
        var m = result.WalkedCount;
        if (m == n)
            return $"Walking {n} cells from {addr}";
        return $"Walking {m} of {n} cells from {addr} — restricted by selection";
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
