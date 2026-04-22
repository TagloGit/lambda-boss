using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ExcelDna.Integration;

using LambdaBoss.Commands;

namespace LambdaBoss.UI;

public partial class LambdaPopup
{
    private enum Mode { Library, Search, Commands }

    private Mode _mode = Mode.Library;
    private bool _prefixPromptActive;

    private List<LibraryDisplayItem> _allLibraries = new();
    private List<LambdaDisplayItem> _allLambdas = new();
    private readonly IReadOnlyList<SlashCommand> _allCommands;

    public LambdaPopup()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        _allCommands = BuildCommandRegistry();
        CommandsList.ItemsSource = _allCommands;
    }

    /// <summary>
    ///     Fired when the user confirms loading a library with a chosen prefix.
    /// </summary>
    public event EventHandler<LibraryLoadRequest>? LibraryLoadRequested;

    /// <summary>
    ///     Sets the data for the popup to display.
    /// </summary>
    public void SetData(IReadOnlyList<LibraryInfo> libraries, IReadOnlyList<LambdaInfo> lambdas,
        IReadOnlySet<string>? loadedLibraryKeys = null)
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
                RepoConfig = l.IsLocal ? null : l.RepoConfig,
                LocalSourceConfig = l.LocalSourceConfig,
                LoadedLabel = !l.IsLocal && loadedLibraryKeys != null
                    && loadedLibraryKeys.Contains(MakeLoadedKey(l.RepoConfig.Url, l.FolderName))
                    ? "✓ loaded" : ""
            })
            .ToList();

        _allLambdas = lambdas
            .Select(l => new LambdaDisplayItem
            {
                Name = l.Name,
                LibraryLabel = l.LibraryInfo.IsLocal
                    ? $"{l.LibraryInfo.DisplayName} [Local]"
                    : l.LibraryInfo.DisplayName,
                Description = l.Description,
                LibraryInfo = l.LibraryInfo
            })
            .ToList();
    }

    /// <summary>
    ///     Refreshes the loaded indicators on existing library items without replacing all data.
    /// </summary>
    public void UpdateLoadedKeys(IReadOnlySet<string>? loadedLibraryKeys)
    {
        if (_allLibraries.Count == 0)
            return;

        _allLibraries = _allLibraries
            .Select(l => new LibraryDisplayItem
            {
                DisplayName = l.DisplayName,
                Description = l.Description,
                LambdaCountLabel = l.LambdaCountLabel,
                DefaultPrefix = l.DefaultPrefix,
                RepoLabel = l.RepoLabel,
                FolderName = l.FolderName,
                RepoConfig = l.RepoConfig,
                LocalSourceConfig = l.LocalSourceConfig,
                LoadedLabel = !l.IsLocal && l.RepoConfig != null && loadedLibraryKeys != null
                    && loadedLibraryKeys.Contains(MakeLoadedKey(l.RepoConfig.Url, l.FolderName))
                    ? "✓ loaded" : ""
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
                // Two-step exit from Commands mode: first Escape clears the
                // leading "/", returning to Library or Search via TextChanged.
                // A second Escape (now in the restored mode) closes the popup.
                if (_mode == Mode.Commands)
                    SearchBox.Text = "";
                else
                    Hide();
                e.Handled = true;
                break;

            case Key.Enter:
                if (_mode == Mode.Commands)
                    ExecuteSelectedCommand();
                else
                    BeginLoad();
                e.Handled = true;
                break;

            case Key.Tab:
                // Tab toggles Library ↔ Search only. In Commands mode it's a
                // no-op (the "/" prefix in the search box defines the mode).
                if (_mode != Mode.Commands)
                    SwitchMode(_mode == Mode.Library ? Mode.Search : Mode.Library);
                e.Handled = true;
                break;

            case Key.Down:
                NavigateDown();
                e.Handled = true;
                break;

            case Key.Up:
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
        var raw = SearchBox.Text;
        var targetMode = raw.StartsWith("/")
            ? Mode.Commands
            : string.IsNullOrEmpty(raw.Trim()) ? Mode.Library : Mode.Search;

        if (_mode != targetMode)
            SwitchMode(targetMode);

        ApplyFilter(raw);
    }

    private void LibraryModeLabel_Click(object sender, MouseButtonEventArgs e)
    {
        // If the box carries a "/", clear it so the mode actually changes.
        if (SearchBox.Text.StartsWith("/"))
            SearchBox.Text = "";
        SwitchMode(Mode.Library);
    }

    private void SearchModeLabel_Click(object sender, MouseButtonEventArgs e)
    {
        if (SearchBox.Text.StartsWith("/"))
            SearchBox.Text = "";
        SwitchMode(Mode.Search);
    }

    private void CommandsModeLabel_Click(object sender, MouseButtonEventArgs e)
    {
        if (!SearchBox.Text.StartsWith("/"))
        {
            SearchBox.Text = "/";
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
        SearchBox.Focus();
    }

    private void SwitchMode(Mode newMode)
    {
        _mode = newMode;

        SetLabelActive(LibraryModeLabel, _mode == Mode.Library);
        SetLabelActive(SearchModeLabel, _mode == Mode.Search);
        SetLabelActive(CommandsModeLabel, _mode == Mode.Commands);

        LibraryList.Visibility = _mode == Mode.Library ? Visibility.Visible : Visibility.Collapsed;
        LambdaList.Visibility = _mode == Mode.Search ? Visibility.Visible : Visibility.Collapsed;
        CommandsList.Visibility = _mode == Mode.Commands ? Visibility.Visible : Visibility.Collapsed;

        ApplyFilter(SearchBox.Text);
    }

    private static void SetLabelActive(TextBlock label, bool active)
    {
        label.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        label.Foreground = BrushFromHex(active ? "#dcdcaa" : "#808080");
    }

    private void ApplyFilter(string rawQuery)
    {
        if (_mode == Mode.Commands)
        {
            CommandsList.ItemsSource = SlashCommandFilter.Filter(_allCommands, rawQuery);
            if (CommandsList.Items.Count > 0)
                CommandsList.SelectedIndex = 0;
            return;
        }

        var query = rawQuery.Trim();

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
                // Name matches rank above description-only matches; this bonus
                // must exceed any realistic description score.
                const int nameBonus = 10_000;

                LambdaList.ItemsSource = _allLambdas
                    .Select(l =>
                    {
                        var nameScore = FuzzyMatcher.Score(query, l.Name);
                        var descScore = FuzzyMatcher.Score(query, l.Description);
                        var best = FuzzyMatcher.NoMatch;
                        if (nameScore != FuzzyMatcher.NoMatch)
                            best = nameScore + nameBonus;
                        if (descScore != FuzzyMatcher.NoMatch && descScore > best)
                            best = descScore;
                        return (Item: l, Score: best);
                    })
                    .Where(x => x.Score != FuzzyMatcher.NoMatch)
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Item)
                    .ToList();
            }

            if (LambdaList.Items.Count > 0)
                LambdaList.SelectedIndex = 0;
        }
    }

    private ListBox ActiveList => _mode switch
    {
        Mode.Library => LibraryList,
        Mode.Search => LambdaList,
        Mode.Commands => CommandsList,
        _ => LibraryList
    };

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
        string? defaultPrefix = null;

        if (_mode == Mode.Library && LibraryList.SelectedItem is LibraryDisplayItem libItem)
        {
            defaultPrefix = libItem.DefaultPrefix;
        }
        else if (_mode == Mode.Search && LambdaList.SelectedItem is LambdaDisplayItem lambdaItem)
        {
            defaultPrefix = lambdaItem.LibraryInfo.DefaultPrefix;
        }

        if (defaultPrefix == null)
            return;

        ShowPrefixPrompt(defaultPrefix);
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
        LibraryLoadRequest? request = null;
        var prefix = PrefixBox.Text.Trim();

        if (_mode == Mode.Library && LibraryList.SelectedItem is LibraryDisplayItem libItem)
        {
            request = libItem.IsLocal
                ? new LibraryLoadRequest(libItem.LocalSourceConfig!, libItem.FolderName, prefix, libItem.DisplayName)
                : new LibraryLoadRequest(libItem.RepoConfig!, libItem.FolderName, prefix, libItem.DisplayName);
        }
        else if (_mode == Mode.Search && LambdaList.SelectedItem is LambdaDisplayItem lambdaItem)
        {
            var info = lambdaItem.LibraryInfo;
            request = info.IsLocal
                ? new LibraryLoadRequest(info.LocalSourceConfig!, info.FolderName, prefix, info.DisplayName)
                : new LibraryLoadRequest(info.RepoConfig, info.FolderName, prefix, info.DisplayName);
        }

        if (request == null)
            return;

        HidePrefixPrompt();
        LibraryLoadRequested?.Invoke(this, request);
        Hide();
    }

    private void ExecuteSelectedCommand()
    {
        if (CommandsList.SelectedItem is SlashCommand cmd)
            cmd.Invoke();
    }

    private IReadOnlyList<SlashCommand> BuildCommandRegistry() => new[]
    {
        new SlashCommand(
            "LET to LAMBDA",
            "Convert the active =LET(...) cell into a named LAMBDA",
            () =>
            {
                Hide();
                ExcelAsyncUtil.QueueAsMacro(() => ConvertLetToLambdaCommand.Run());
            }),
        new SlashCommand(
            "Edit Lambda",
            "Expand the active LAMBDA call back to =LET(...)",
            () =>
            {
                Hide();
                ExcelAsyncUtil.QueueAsMacro(() => EditLambdaCommand.Run());
            }),
        new SlashCommand(
            "Load Library",
            "Switch to Library mode to pick a library",
            () =>
            {
                // Clearing the box drives TextChanged → Mode.Library.
                SearchBox.Text = "";
                SearchBox.Focus();
            }),
        new SlashCommand(
            "Settings",
            "Open Lambda Boss settings",
            () =>
            {
                Hide();
                ShowLambdaPopupCommand.ShowSettings();
            }),
    };

    internal static string MakeLoadedKey(string repoUrl, string libraryName) =>
        $"{repoUrl.TrimEnd('/').ToLowerInvariant()}|{libraryName.ToLowerInvariant()}";

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
    public RepoConfig? RepoConfig { get; }
    public LocalSourceConfig? LocalSourceConfig { get; }
    public string LibraryName { get; }
    public string Prefix { get; }
    public string DisplayName { get; }

    /// <summary>
    ///     Whether this request is for a local directory source.
    /// </summary>
    public bool IsLocal => LocalSourceConfig != null;

    public LibraryLoadRequest(RepoConfig repoConfig, string libraryName, string prefix, string displayName)
    {
        RepoConfig = repoConfig;
        LibraryName = libraryName;
        Prefix = prefix;
        DisplayName = displayName;
    }

    public LibraryLoadRequest(LocalSourceConfig localConfig, string libraryName, string prefix, string displayName)
    {
        LocalSourceConfig = localConfig;
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
    public RepoConfig? RepoConfig { get; init; }
    public LocalSourceConfig? LocalSourceConfig { get; init; }
    public string LoadedLabel { get; init; } = "";
    public bool IsLocal => LocalSourceConfig != null;
    public string SourceIcon => IsLocal ? "\U0001F4C1" : "";
}

internal class LambdaDisplayItem
{
    public string Name { get; init; } = "";
    public string LibraryLabel { get; init; } = "";
    public string Description { get; init; } = "";
    public LibraryInfo LibraryInfo { get; init; } = null!;

    /// <summary>
    ///     Tooltip value for the description. Returns null for empty descriptions so WPF
    ///     suppresses the tooltip entirely rather than showing a blank popup.
    /// </summary>
    public object? DescriptionToolTip => string.IsNullOrEmpty(Description) ? null : Description;
}
