using Ookii.Dialogs.Wpf;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LambdaBoss.UI;

public partial class SettingsWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
        RefreshRepoList();
        RefreshLocalSourceList();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    ///     Fired when the user changes settings (add/remove/toggle repos).
    /// </summary>
    public event EventHandler? SettingsChanged;

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
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

    private void AddRepo()
    {
        var url = RepoUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
            return;

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

    private void AddRepoButton_Click(object sender, RoutedEventArgs e)
    {
        AddRepo();
    }

    private void RepoUrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddRepo();
            e.Handled = true;
        }
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

    // --- Local directory sources ---

    private void RefreshLocalSourceList()
    {
        var settings = Settings.Current;
        LocalSourceList.ItemsSource = settings.LocalSources
            .Select(s => new LocalSourceDisplayItem
            {
                Path = s.Path,
                Enabled = s.Enabled,
                DisplayLabel = s.DisplayName,
                PathLabel = s.Path
            })
            .ToList();
    }

    private void AddLocalSource()
    {
        var path = LocalPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        if (!Directory.Exists(path))
        {
            StatusText.Text = "Directory does not exist";
            return;
        }

        var settings = Settings.Current;
        if (!settings.AddLocalSource(path))
        {
            StatusText.Text = "Local source already exists";
            return;
        }

        settings.Save();
        LocalPathBox.Text = "";
        RefreshLocalSourceList();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        StatusText.Text = $"Added local source: {path}";
    }

    private void AddLocalButton_Click(object sender, RoutedEventArgs e)
    {
        AddLocalSource();
    }

    private void BrowseLocalButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new VistaFolderBrowserDialog
        {
            Description = "Select the folder containing library sub-folders (e.g. repo\\lambdas)",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == true) LocalPathBox.Text = dialog.SelectedPath;
    }

    private void LocalPathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddLocalSource();
            e.Handled = true;
        }
    }

    private void RemoveLocalButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
            return;

        var settings = Settings.Current;
        if (settings.RemoveLocalSource(path))
        {
            settings.Save();
            RefreshLocalSourceList();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            StatusText.Text = $"Removed local source: {path}";
        }
    }

    private void LocalEnabledCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: LocalSourceDisplayItem item })
            return;

        var settings = Settings.Current;
        var source = settings.LocalSources.FirstOrDefault(s =>
            string.Equals(s.Path.TrimEnd('\\', '/'), item.Path.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase));

        if (source != null)
        {
            source.Enabled = item.Enabled;
            settings.Save();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

internal class RepoDisplayItem
{
    public string Url { get; init; } = "";
    public bool Enabled { get; set; }
    public string DisplayLabel { get; init; } = "";
    public string LastFetchedLabel { get; init; } = "";
}

internal class LocalSourceDisplayItem
{
    public string Path { get; init; } = "";
    public bool Enabled { get; set; }
    public string DisplayLabel { get; init; } = "";
    public string PathLabel { get; init; } = "";
}