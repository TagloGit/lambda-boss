using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LambdaBoss.UI;

public partial class LambdaPopup
{
    private enum Mode { Library, Search, Settings }

    private Mode _mode = Mode.Library;
    private bool _prefixPromptActive;

    private List<LibraryDisplayItem> _allLibraries = new();
    private List<LambdaDisplayItem> _allLambdas = new();

    public LambdaPopup()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    ///     Fired when the user confirms loading a library with a chosen prefix.
    /// </summary>
    public event EventHandler<LibraryLoadRequest>? LibraryLoadRequested;

    /// <summary>
    ///     Fired when the user changes settings (add/remove/toggle repos).
    /// </summary>
    public event EventHandler? SettingsChanged;

    /// <summary>
    ///     Sets the data for the popup to display.
    /// </summary>
    public void SetData(IReadOnlyList<LibraryInfo> libraries, IReadOnlyList<LambdaInfo> lambdas)
    {
        _allLibraries = libraries
            .Select(l => new LibraryDisplayItem
            {
                DisplayName = l.DisplayName,
                Description = l.Description,
                LambdaCountLabel = $"({l.LambdaCount})",
                DefaultPrefix = l.DefaultPrefix,
                RepoLabel = l.RepoLabel,
                FolderName = l.FolderName,
                RepoConfig = l.RepoConfig
            })
            .ToList();

        _allLambdas = lambdas
            .Select(l => new LambdaDisplayItem
            {
                Name = l.Name,
                LibraryLabel = l.LibraryInfo.DisplayName,
                LibraryInfo = l.LibraryInfo
            })
            .ToList();
    }

    public void ResetAndShow()
    {
        SearchBox.Text = "";
        HidePrefixPrompt();
        SwitchMode(Mode.Library);

        Show();
        Activate();
        SearchBox.Focus();
    }

    /// <summary>
    ///     Opens the popup directly in settings mode.
    /// </summary>
    public void ShowSettingsMode()
    {
        SearchBox.Text = "";
        HidePrefixPrompt();
        SwitchMode(Mode.Settings);

        Show();
        Activate();
    }

    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Prefix prompt has its own key handling
        if (_prefixPromptActive)
        {
            HandlePrefixKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;

            case Key.Enter when _mode != Mode.Settings:
                BeginLoad();
                e.Handled = true;
                break;

            case Key.Tab when _mode != Mode.Settings:
                SwitchMode(_mode == Mode.Library ? Mode.Search : Mode.Library);
                e.Handled = true;
                break;

            case Key.Down when _mode != Mode.Settings:
                NavigateDown();
                e.Handled = true;
                break;

            case Key.Up when _mode != Mode.Settings:
                NavigateUp();
                e.Handled = true;
                break;
        }
    }

    private void HandlePrefixKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ConfirmLoad();
                e.Handled = true;
                break;

            case Key.Escape:
                HidePrefixPrompt();
                e.Handled = true;
                break;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_mode == Mode.Settings)
            return;

        var query = SearchBox.Text.Trim();

        // Auto-switch to search mode when typing
        if (!string.IsNullOrEmpty(query) && _mode == Mode.Library)
            SwitchMode(Mode.Search);

        // Auto-switch back to library mode when clearing
        if (string.IsNullOrEmpty(query) && _mode == Mode.Search)
            SwitchMode(Mode.Library);

        ApplyFilter(query);
    }

    private void LibraryModeLabel_Click(object sender, MouseButtonEventArgs e)
    {
        SwitchMode(Mode.Library);
    }

    private void SearchModeLabel_Click(object sender, MouseButtonEventArgs e)
    {
        SwitchMode(Mode.Search);
    }

    private void SettingsModeLabel_Click(object sender, MouseButtonEventArgs e)
    {
        SwitchMode(Mode.Settings);
    }

    private void SwitchMode(Mode newMode)
    {
        _mode = newMode;

        // Reset all mode labels to inactive
        LibraryModeLabel.FontWeight = FontWeights.Normal;
        LibraryModeLabel.Foreground = BrushFromHex("#808080");
        SearchModeLabel.FontWeight = FontWeights.Normal;
        SearchModeLabel.Foreground = BrushFromHex("#808080");
        SettingsModeLabel.FontWeight = FontWeights.Normal;
        SettingsModeLabel.Foreground = BrushFromHex("#808080");

        // Hide all content panels
        LibraryList.Visibility = Visibility.Collapsed;
        LambdaList.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;

        switch (_mode)
        {
            case Mode.Library:
                LibraryModeLabel.FontWeight = FontWeights.Bold;
                LibraryModeLabel.Foreground = BrushFromHex("#dcdcaa");
                LibraryList.Visibility = Visibility.Visible;
                SearchBox.Visibility = Visibility.Visible;
                ApplyFilter(SearchBox.Text.Trim());
                break;

            case Mode.Search:
                SearchModeLabel.FontWeight = FontWeights.Bold;
                SearchModeLabel.Foreground = BrushFromHex("#dcdcaa");
                LambdaList.Visibility = Visibility.Visible;
                SearchBox.Visibility = Visibility.Visible;
                ApplyFilter(SearchBox.Text.Trim());
                break;

            case Mode.Settings:
                SettingsModeLabel.FontWeight = FontWeights.Bold;
                SettingsModeLabel.Foreground = BrushFromHex("#dcdcaa");
                SettingsPanel.Visibility = Visibility.Visible;
                SearchBox.Visibility = Visibility.Collapsed;
                RefreshRepoList();
                StatusText.Text = "Add, remove, or toggle repository sources";
                break;
        }
    }

    private void RefreshRepoList()
    {
        var settings = Settings.Current;
        RepoList.ItemsSource = settings.Repos
            .Select(r => new RepoDisplayItem
            {
                Url = r.Url,
                Enabled = r.Enabled,
                DisplayLabel = FormatRepoLabel(r),
                LastFetchedLabel = r.LastFetched.HasValue
                    ? $"Last fetched: {r.LastFetched.Value:yyyy-MM-dd HH:mm}"
                    : "Never fetched"
            })
            .ToList();
    }

    private static string FormatRepoLabel(RepoConfig config)
    {
        try
        {
            var (owner, repo) = config.ParseOwnerRepo();
            return $"{owner}/{repo}";
        }
        catch
        {
            return config.Url;
        }
    }

    private void AddRepoButton_Click(object sender, RoutedEventArgs e)
    {
        var url = RepoUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
            return;

        // Basic validation
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "URL must start with https://github.com/";
            return;
        }

        var settings = Settings.Current;
        if (!settings.AddRepo(url))
        {
            StatusText.Text = "Repository already exists";
            return;
        }

        settings.Save();
        RepoUrlBox.Text = "";
        RefreshRepoList();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        StatusText.Text = $"Added {url}";
    }

    private void RemoveRepoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url })
            return;

        var settings = Settings.Current;
        if (settings.Repos.Count <= 1)
        {
            StatusText.Text = "Cannot remove the last repository";
            return;
        }

        if (settings.RemoveRepo(url))
        {
            settings.Save();
            RefreshRepoList();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            StatusText.Text = $"Removed {url}";
        }
    }

    private void RepoEnabledCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: RepoDisplayItem item })
            return;

        var settings = Settings.Current;
        var repo = settings.Repos.FirstOrDefault(r =>
            string.Equals(r.Url.TrimEnd('/'), item.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        if (repo != null)
        {
            repo.Enabled = item.Enabled;
            settings.Save();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyFilter(string query)
    {
        if (_mode == Mode.Library)
        {
            if (string.IsNullOrEmpty(query))
                LibraryList.ItemsSource = _allLibraries;
            else
            {
                LibraryList.ItemsSource = _allLibraries
                    .Where(l => l.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || l.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (LibraryList.Items.Count > 0)
                LibraryList.SelectedIndex = 0;
        }
        else
        {
            if (string.IsNullOrEmpty(query))
                LambdaList.ItemsSource = _allLambdas;
            else
            {
                LambdaList.ItemsSource = _allLambdas
                    .Where(l => l.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (LambdaList.Items.Count > 0)
                LambdaList.SelectedIndex = 0;
        }
    }

    private ListBox ActiveList => _mode == Mode.Library ? LibraryList : LambdaList;

    private void NavigateDown()
    {
        var list = ActiveList;

        if (SearchBox.IsFocused && list.Items.Count > 0)
        {
            list.Focus();
            if (list.SelectedIndex < 0) list.SelectedIndex = 0;
        }
        else if (list.IsFocused && list.SelectedIndex < list.Items.Count - 1)
        {
            list.SelectedIndex++;
        }
    }

    private void NavigateUp()
    {
        var list = ActiveList;

        if (list.IsFocused)
        {
            if (list.SelectedIndex > 0)
                list.SelectedIndex--;
            else
                SearchBox.Focus();
        }
    }

    private void BeginLoad()
    {
        LibraryInfo? libraryInfo = null;

        if (_mode == Mode.Library && LibraryList.SelectedItem is LibraryDisplayItem libItem)
        {
            libraryInfo = new LibraryInfo
            {
                RepoConfig = libItem.RepoConfig,
                RepoLabel = libItem.RepoLabel,
                FolderName = libItem.FolderName,
                DisplayName = libItem.DisplayName,
                Description = libItem.Description,
                DefaultPrefix = libItem.DefaultPrefix,
                LambdaCount = 0
            };
        }
        else if (_mode == Mode.Search && LambdaList.SelectedItem is LambdaDisplayItem lambdaItem)
        {
            libraryInfo = lambdaItem.LibraryInfo;
        }

        if (libraryInfo == null)
            return;

        ShowPrefixPrompt(libraryInfo.DefaultPrefix);
    }

    private void ShowPrefixPrompt(string defaultPrefix)
    {
        _prefixPromptActive = true;
        PrefixPanel.Visibility = Visibility.Visible;
        PrefixBox.Text = defaultPrefix;
        PrefixBox.Focus();
        PrefixBox.SelectAll();
        StatusText.Text = "Enter to confirm · Escape to cancel";
    }

    private void HidePrefixPrompt()
    {
        _prefixPromptActive = false;
        PrefixPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "↑↓ navigate · Enter load · Tab switch · Esc close";
    }

    private void ConfirmLoad()
    {
        LibraryInfo? libraryInfo = null;

        if (_mode == Mode.Library && LibraryList.SelectedItem is LibraryDisplayItem libItem)
        {
            libraryInfo = new LibraryInfo
            {
                RepoConfig = libItem.RepoConfig,
                RepoLabel = libItem.RepoLabel,
                FolderName = libItem.FolderName,
                DisplayName = libItem.DisplayName,
                Description = libItem.Description,
                DefaultPrefix = libItem.DefaultPrefix,
                LambdaCount = 0
            };
        }
        else if (_mode == Mode.Search && LambdaList.SelectedItem is LambdaDisplayItem lambdaItem)
        {
            libraryInfo = lambdaItem.LibraryInfo;
        }

        if (libraryInfo == null)
            return;

        var prefix = PrefixBox.Text.Trim();
        HidePrefixPrompt();

        LibraryLoadRequested?.Invoke(this, new LibraryLoadRequest(
            libraryInfo.RepoConfig,
            libraryInfo.FolderName,
            prefix,
            libraryInfo.DisplayName));

        Hide();
    }

    private static System.Windows.Media.SolidColorBrush BrushFromHex(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        return new System.Windows.Media.SolidColorBrush(color);
    }
}

/// <summary>
///     Event args for requesting a library load.
/// </summary>
public sealed class LibraryLoadRequest
{
    public RepoConfig RepoConfig { get; }
    public string LibraryName { get; }
    public string Prefix { get; }
    public string DisplayName { get; }

    public LibraryLoadRequest(RepoConfig repoConfig, string libraryName, string prefix, string displayName)
    {
        RepoConfig = repoConfig;
        LibraryName = libraryName;
        Prefix = prefix;
        DisplayName = displayName;
    }
}

internal class LibraryDisplayItem
{
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string LambdaCountLabel { get; init; } = "";
    public string DefaultPrefix { get; init; } = "";
    public string RepoLabel { get; init; } = "";
    public string FolderName { get; init; } = "";
    public RepoConfig RepoConfig { get; init; } = null!;
}

internal class LambdaDisplayItem
{
    public string Name { get; init; } = "";
    public string LibraryLabel { get; init; } = "";
    public LibraryInfo LibraryInfo { get; init; } = null!;
}

internal class RepoDisplayItem
{
    public string Url { get; init; } = "";
    public bool Enabled { get; set; }
    public string DisplayLabel { get; init; } = "";
    public string LastFetchedLabel { get; init; } = "";
}
