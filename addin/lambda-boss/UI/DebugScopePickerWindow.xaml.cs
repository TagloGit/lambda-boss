using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace LambdaBoss.UI;

/// <summary>
///     Spec 0010 (spike) / issue #279 — the modal chooser for <c>/Debug Lambda</c>.
///     Lists the formula's <c>LAMBDA(...)</c> scopes (indented by nesting) and a
///     1-based example index, then returns the user's choice via
///     <see cref="SelectedScopeKey" /> / <see cref="ExampleIndex" />. It does no
///     Excel work — the command does all COM after the dialog closes, so the
///     macro thread is free to block on <c>ShowDialog</c>.
/// </summary>
public partial class DebugScopePickerWindow
{
    public DebugScopePickerWindow(string formula, DebugDiscovery discovery)
    {
        InitializeComponent();

        var scopeVms = discovery.Scopes
            .Select(s => new ScopeVm(s.Key, ScopeDisplay(s)))
            .ToList();
        ScopeCombo.ItemsSource = scopeVms;
        if (scopeVms.Count > 0)
            ScopeCombo.SelectedIndex = 0;
    }

    /// <summary>The chosen scope key, or null when cancelled.</summary>
    public string? SelectedScopeKey { get; private set; }

    /// <summary>The chosen 1-based example index (defaults to 1).</summary>
    public int ExampleIndex { get; private set; } = 1;

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScopeCombo.SelectedItem is not ScopeVm vm)
            return;

        SelectedScopeKey = vm.Key;
        ExampleIndex = ParseIndex(IndexBox.Text);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedScopeKey = null;
        DialogResult = false;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            GenerateButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private static int ParseIndex(string text)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1
            ? n
            : 1;
    }

    private static string ScopeDisplay(DebugScope scope)
    {
        var indent = scope.Depth > 0 ? new string(' ', scope.Depth * 4) + "↳ " : "";
        return indent + scope.Label;
    }

    private sealed record ScopeVm(string Key, string Display);
}
