using System.Windows;
using System.Windows.Input;

namespace LambdaBoss.UI;

public partial class LambdaPopup : Window
{
    private readonly List<LambdaItem> _allItems;

    public event EventHandler<(string Name, string Formula)>? LambdaSelected;

    public LambdaPopup()
    {
        InitializeComponent();

        _allItems = LambdaLoader.GetTracerBulletLambdas()
            .Select(l => new LambdaItem { Name = l.Name, Formula = l.Formula })
            .ToList();

        LambdaList.ItemsSource = _allItems;
        if (_allItems.Count > 0)
        {
            LambdaList.SelectedIndex = 0;
        }

        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void ResetAndShow()
    {
        SearchBox.Text = "";
        LambdaList.ItemsSource = _allItems;
        if (_allItems.Count > 0)
        {
            LambdaList.SelectedIndex = 0;
        }

        Show();
        Activate();
        SearchBox.Focus();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Hide instead of close for window reuse
        e.Cancel = true;
        Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;

            case Key.Enter:
                LoadSelectedLambda();
                e.Handled = true;
                break;

            case Key.Down:
                if (SearchBox.IsFocused && LambdaList.Items.Count > 0)
                {
                    LambdaList.Focus();
                    if (LambdaList.SelectedIndex < 0)
                    {
                        LambdaList.SelectedIndex = 0;
                    }
                }
                else if (LambdaList.IsFocused && LambdaList.SelectedIndex < LambdaList.Items.Count - 1)
                {
                    LambdaList.SelectedIndex++;
                }
                e.Handled = true;
                break;

            case Key.Up:
                if (LambdaList.IsFocused)
                {
                    if (LambdaList.SelectedIndex > 0)
                    {
                        LambdaList.SelectedIndex--;
                    }
                    else
                    {
                        SearchBox.Focus();
                    }
                }
                e.Handled = true;
                break;
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();

        if (string.IsNullOrEmpty(query))
        {
            LambdaList.ItemsSource = _allItems;
        }
        else
        {
            var filtered = _allItems
                .Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            LambdaList.ItemsSource = filtered;
        }

        if (LambdaList.Items.Count > 0)
        {
            LambdaList.SelectedIndex = 0;
        }
    }

    private void LoadSelectedLambda()
    {
        if (LambdaList.SelectedItem is LambdaItem item)
        {
            LambdaSelected?.Invoke(this, (item.Name, item.Formula));
            Hide();
        }
    }
}

internal class LambdaItem
{
    public string Name { get; init; } = "";
    public string Formula { get; init; } = "";
}
