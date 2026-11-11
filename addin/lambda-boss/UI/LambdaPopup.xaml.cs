using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ExcelDna.Integration;

using LambdaBoss.Commands;

namespace LambdaBoss.UI;

public partial class LambdaPopup
{
    private readonly PopupNavigation _nav = new();
    private PopupMode _mode => _nav.Mode;
    private bool _prefixPromptActive;

    // Set when Enter was pressed on text that names a library exactly; the prefix
    // prompt then loads this library instead of the selected list item (#449).
    private LibraryDisplayItem? _exactLibrary;

    private List<LibraryDisplayItem> _allLibraries = new();
    private List<LambdaDisplayItem> _allLambdas = new();
    private readonly IReadOnlyList<SlashCommand> _allCommands;

    public LambdaPopup()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        _allCommands = PopupCommands.InOrder(BuildCommandRegistry());
        CommandsList.ItemsSource = SlashCommandFilter.Filter(_allCommands, "");
    }

    /// <summary>
    ///     Fired when the user confirms loading a library with a chosen prefix.
    /// </summary>
    public event EventHandler<LibraryLoadRequest>? LibraryLoadRequested;

    /// <summary>
    ///     Sets the data for the popup to display.
    /// </summary>
    public void SetData(IReadOnlyList<LibraryInfo> libraries, IReadOnlyList<LambdaInfo> lambdas,
        IReadOnlyList<ScannedLibrary>? loaded = null)
    {
        _dataReady = true;
        _libraryInfos = libraries;
        _loaded = loaded ?? Array.Empty<ScannedLibrary>();
        _allLibraries = BuildLibraryItems();

        _allLambdas = lambdas
            .Select(l => new LambdaDisplayItem
            {
                Name = l.Name,
                LibraryLabel = l.LibraryInfo.IsLocal
                    ? $"{l.LibraryInfo.DisplayName} [Local]"
                    : l.LibraryInfo.DisplayName,
                Description = l.Description,
                LibraryInfo = l.LibraryInfo,
                IsConstant = l.IsConstant
            })
            .ToList();
    }

    /// <summary>
    ///     Refreshes the loaded and out-of-date marks from a new scan of the workbook's names.
    /// </summary>
    public void UpdateLoaded(IReadOnlyList<ScannedLibrary>? loaded)
    {
        _loaded = loaded ?? Array.Empty<ScannedLibrary>();
        if (_libraryInfos.Count > 0)
            _allLibraries = BuildLibraryItems();
        else
            UpdateStaleBanner();
    }

    // False until the first SetData. Enter pressed before then is held, not acted on (#449 follow-up):
    // with no libraries there's nothing to match, and the top search hit could be anything.
    private bool _dataReady;
    private bool _pendingEnter;

    /// <summary>
    ///     The library data has arrived. If the popup is open, what's typed stays and the lists
    ///     refilter around it (clearing it here once cut "loops" to a fragment whose top hit was a
    ///     Math LAMBDA); an Enter pressed while loading runs now.
    /// </summary>
    public void OnDataArrived()
    {
        if (!IsVisible)
            return;

        if (_mode != PopupMode.Commands)
            ApplyFilter(SearchBox.Text);

        if (_pendingEnter && !_prefixPromptActive)
        {
            _pendingEnter = false;
            BeginLoad(fromSearchBox: true);
        }
    }

    private IReadOnlyList<LibraryInfo> _libraryInfos = Array.Empty<LibraryInfo>();
    private IReadOnlyList<ScannedLibrary> _loaded = Array.Empty<ScannedLibrary>();

    // The add-in's library under one prefix, kept while the provider's data stands: rewriting
    // Maps' 67 names on every open would be wasted work.
    private readonly Dictionary<(FetchedLibrary, string), IReadOnlyList<(string Name, string Formula)>> _prefixed = new();

    private List<LibraryDisplayItem> BuildLibraryItems()
    {
        var items = new List<LibraryDisplayItem>();
        foreach (var l in _libraryInfos)
        {
            var scanned = l.IsLocal ? null : _loaded.FirstOrDefault(s =>
                string.Equals(MakeLoadedKey(s.RepoUrl, s.LibraryName), MakeLoadedKey(l.RepoConfig.Url, l.FolderName),
                    StringComparison.Ordinal));
            var (differ, total) = scanned != null && l.Library != null
                ? StaleCount(l.Library, scanned)
                : (0, 0);

            items.Add(new LibraryDisplayItem
            {
                DisplayName = l.DisplayName,
                Description = l.Description,
                LambdaCountLabel = $"({l.LambdaCount})",
                DefaultPrefix = l.DefaultPrefix,
                RepoLabel = l.RepoLabel,
                FolderName = l.FolderName,
                RepoConfig = l.IsLocal ? null : l.RepoConfig,
                LocalSourceConfig = l.LocalSourceConfig,
                LoadedLabel = scanned == null ? "" : differ > 0 ? $"⚠ out of date ({differ} of {total})" : "✓ loaded",
                LoadedPrefix = scanned?.Prefix,
                Stale = differ > 0,
                Differ = differ,
                Total = total
            });
        }

        UpdateStaleBanner(items);
        return items;
    }

    private (int Differ, int Total) StaleCount(FetchedLibrary library, ScannedLibrary scanned)
    {
        try
        {
            if (!_prefixed.TryGetValue((library, scanned.Prefix), out var current))
                _prefixed[(library, scanned.Prefix)] = current = library.LoadWithPrefix(scanned.Prefix);
            return LibraryStaleness.Compare(scanned.Lambdas, current);
        }
        catch (Exception ex)
        {
            Common.Logger.Error($"LambdaPopup: couldn't compare '{scanned.LibraryName}' with the add-in's copy", ex);
            return (0, 0);
        }
    }

    /// <summary>
    ///     One line above the list when the workbook holds an out-of-date copy of any library.
    /// </summary>
    private void UpdateStaleBanner(List<LibraryDisplayItem>? items = null)
    {
        var stale = (items ?? _allLibraries).Where(l => l.Stale).ToList();
        if (stale.Count == 0)
        {
            StaleBanner.Visibility = Visibility.Collapsed;
            return;
        }

        var names = string.Join(", ", stale.Select(l => l.DisplayName));
        StaleBannerText.Text = stale.Count == 1
            ? $"⚠ {names} in this workbook is out of date: {stale[0].Differ} of {stale[0].Total} names differ from the add-in's. Ctrl+R reloads it."
            : $"⚠ {names} in this workbook are out of date. Ctrl+R reloads them.";
        StaleBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    ///     Reloads every out-of-date library with the prefix the workbook already uses (#452).
    /// </summary>
    private void ReloadStale()
    {
        var stale = _allLibraries.Where(l => l.Stale && l.RepoConfig != null).ToList();
        if (stale.Count == 0)
            return;

        HidePrefixPrompt();
        foreach (var l in stale)
            LibraryLoadRequested?.Invoke(this,
                new LibraryLoadRequest(l.RepoConfig!, l.FolderName, l.LoadedPrefix ?? "", l.DisplayName));
        Hide();
    }

    private void StaleBanner_Click(object sender, MouseButtonEventArgs e) => ReloadStale();

    public void ResetAndShow()
    {
        _pendingEnter = false;
        _nav.Open(Settings.Current.PopupOpensOn);
        ArrangeModeLabels();
        SearchBox.Text = "";
        HidePrefixPrompt();
        SwitchMode(_nav.Mode);
        UpdateChannelBadge();

        Show();
        Activate();
        SearchBox.Focus();
    }

    /// <summary>
    ///     Labs switch: shows which library channel the popup is reading.
    /// </summary>
    private void UpdateChannelBadge()
    {
        var on = Labs.IsOn(Labs.PopupChannelBadge);
        ChannelBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
            ChannelBadge.Text = Labs.ChannelLabel(Settings.Current.LibraryChannel);
    }

    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    /// <summary>The status line's hint for the current mode, or a note when there is one.</summary>
    public void ShowIdleStatus(string? note = null)
    {
        StatusText.Text = note ?? _nav.Hint;
    }

    /// <summary>
    ///     Commands first in the mode bar when the popup opens on them, Libraries first otherwise.
    /// </summary>
    private void ArrangeModeLabels()
    {
        var labels = _nav.OpensOn == PopupOpensOn.Commands
            ? new[] { CommandsModeLabel, LibraryModeLabel, SearchModeLabel }
            : new[] { LibraryModeLabel, SearchModeLabel, CommandsModeLabel };
        ModeBar.Children.Clear();
        for (var i = 0; i < labels.Length; i++)
        {
            if (i > 0)
                ModeBar.Children.Add(new TextBlock
                {
                    Text = "  ·  ",
                    FontSize = 12,
                    Foreground = BrushFromHex("#555555")
                });
            ModeBar.Children.Add(labels[i]);
        }
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

        if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ReloadStale();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                switch (_nav.Escape())
                {
                    case PopupEscape.Close:
                        Hide();
                        break;
                    case PopupEscape.ClearText:
                        // Libraries setting: the first Escape clears the leading "/", returning to
                        // Library or Search via TextChanged; a second Escape closes the popup.
                        SearchBox.Text = "";
                        break;
                    case PopupEscape.BackToCommands:
                        ShowCommands();
                        break;
                }
                e.Handled = true;
                break;

            case Key.Enter:
                if (_mode == PopupMode.Commands)
                    ExecuteSelectedCommand();
                else if (!_dataReady)
                {
                    _pendingEnter = true;
                    StatusText.Text = "Loading libraries… Enter will load when they arrive";
                }
                else
                    BeginLoad(SearchBox.IsFocused);
                e.Handled = true;
                break;

            case Key.Tab:
                // Tab toggles Library ↔ Search only. In Commands mode it's a no-op.
                if (_mode != PopupMode.Commands)
                    SwitchMode(_mode == PopupMode.Library ? PopupMode.Search : PopupMode.Library);
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
        var targetMode = _nav.ModeForText(raw);

        if (_mode != targetMode)
            SwitchMode(targetMode);

        ApplyFilter(raw);
    }

    private void LibraryModeLabel_Click(object sender, MouseButtonEventArgs e)
    {
        if (_nav.OpensOn == PopupOpensOn.Commands)
        {
            ShowLibraries();
            return;
        }

        // If the box carries a "/", clear it so the mode actually changes.
        if (SearchBox.Text.StartsWith("/"))
            SearchBox.Text = "";
        SwitchMode(PopupMode.Library);
    }

    private void SearchModeLabel_Click(object sender, MouseButtonEventArgs e)
    {
        if (_nav.OpensOn == PopupOpensOn.Commands && _mode == PopupMode.Commands)
            ShowLibraries();
        else if (SearchBox.Text.StartsWith("/"))
            SearchBox.Text = "";
        SwitchMode(PopupMode.Search);
    }

    private void CommandsModeLabel_Click(object sender, MouseButtonEventArgs e)
    {
        if (_nav.OpensOn == PopupOpensOn.Commands)
        {
            ShowCommands();
            return;
        }

        if (!SearchBox.Text.StartsWith("/"))
        {
            SearchBox.Text = "/";
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
        SearchBox.Focus();
    }

    /// <summary>Back to the commands from the library view (commands-first), with the box cleared.</summary>
    private void ShowCommands()
    {
        HidePrefixPrompt();
        _nav.Set(PopupMode.Commands);
        SearchBox.Text = "";
        SwitchMode(PopupMode.Commands);
        SearchBox.Focus();
    }

    /// <summary>The Library command: the library view, as the popup opened before spec 0017.</summary>
    private void ShowLibraries()
    {
        // The mode first, so clearing the box reads as the library list, not the commands.
        _nav.Set(PopupMode.Library);
        SearchBox.Text = "";
        SwitchMode(PopupMode.Library);
        SearchBox.Focus();
    }

    private void SwitchMode(PopupMode newMode)
    {
        _nav.Set(newMode);

        SetLabelActive(LibraryModeLabel, _mode == PopupMode.Library);
        SetLabelActive(SearchModeLabel, _mode == PopupMode.Search);
        SetLabelActive(CommandsModeLabel, _mode == PopupMode.Commands);

        LibraryList.Visibility = _mode == PopupMode.Library ? Visibility.Visible : Visibility.Collapsed;
        LambdaList.Visibility = _mode == PopupMode.Search ? Visibility.Visible : Visibility.Collapsed;
        CommandsList.Visibility = _mode == PopupMode.Commands ? Visibility.Visible : Visibility.Collapsed;

        if (!_prefixPromptActive)
            ShowIdleStatus();

        ApplyFilter(SearchBox.Text);
    }

    private static void SetLabelActive(TextBlock label, bool active)
    {
        label.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        label.Foreground = BrushFromHex(active ? "#dcdcaa" : "#808080");
    }

    private void ApplyFilter(string rawQuery)
    {
        if (_mode == PopupMode.Commands)
        {
            CommandsList.ItemsSource = SlashCommandFilter.Filter(_allCommands, rawQuery);
            if (CommandsList.Items.Count > 0)
                CommandsList.SelectedIndex = 0;
            return;
        }

        var query = rawQuery.Trim();

        if (_mode == PopupMode.Library)
        {
            if (string.IsNullOrEmpty(query))
                LibraryList.ItemsSource = _allLibraries;
            else
            {
                LibraryList.ItemsSource = _allLibraries
                    .Where(l => l.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                                || l.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
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
        PopupMode.Library => LibraryList,
        PopupMode.Search => LambdaList,
        PopupMode.Commands => CommandsList,
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

    private void BeginLoad(bool fromSearchBox)
    {
        string? defaultPrefix = null;
        _exactLibrary = null;

        // Typed a library's name or prefix and pressed Enter in the box: load that
        // library, whatever the top hit is. Picking a row with the arrows still wins.
        if (_mode != PopupMode.Commands && fromSearchBox
            && ExactLibraryMatch.Find(_allLibraries, SearchBox.Text) is { } exact)
        {
            _exactLibrary = exact;
            ShowPrefixPrompt(exact.DefaultPrefix);
            return;
        }

        if (_mode == PopupMode.Library && LibraryList.SelectedItem is LibraryDisplayItem libItem)
        {
            defaultPrefix = libItem.DefaultPrefix;
        }
        else if (_mode == PopupMode.Search && LambdaList.SelectedItem is LambdaDisplayItem lambdaItem)
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
        _exactLibrary = null;
        PrefixPanel.Visibility = Visibility.Collapsed;
        ShowIdleStatus();
    }

    private void ConfirmLoad()
    {
        LibraryLoadRequest? request = null;
        var prefix = PrefixBox.Text.Trim();

        if (_exactLibrary is { } exact)
        {
            request = exact.IsLocal
                ? new LibraryLoadRequest(exact.LocalSourceConfig!, exact.FolderName, prefix, exact.DisplayName)
                : new LibraryLoadRequest(exact.RepoConfig!, exact.FolderName, prefix, exact.DisplayName);
        }
        else if (_mode == PopupMode.Library && LibraryList.SelectedItem is LibraryDisplayItem libItem)
        {
            request = libItem.IsLocal
                ? new LibraryLoadRequest(libItem.LocalSourceConfig!, libItem.FolderName, prefix, libItem.DisplayName)
                : new LibraryLoadRequest(libItem.RepoConfig!, libItem.FolderName, prefix, libItem.DisplayName);
        }
        else if (_mode == PopupMode.Search && LambdaList.SelectedItem is LambdaDisplayItem lambdaItem)
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
            },
            () => !Labs.IsOn(Labs.State)),
        new SlashCommand(
            "Edit Lambda",
            "Expand a LAMBDA call back to =LET(...). On a fold, write its step beside it with a k stepper: "
            + "states come from the registered step, so re-register with LET to LAMBDA and trace to check a fix",
            () =>
            {
                Hide();
                ExcelAsyncUtil.QueueAsMacro(() => EditLambdaCommand.Run());
            },
            () => Labs.IsOn(Labs.State)),
        new SlashCommand(
            "Gather",
            "Roll up the active cell's precedents into a single =LET(...)",
            () =>
            {
                Hide();
                ExcelAsyncUtil.QueueAsMacro(() => GatherCommand.Run());
            }),
        new SlashCommand(
            "Refactor",
            "Rewrite the active cell's formula as a tidy =LET(...) with one binding per ref",
            () =>
            {
                Hide();
                ExcelAsyncUtil.QueueAsMacro(() => RefactorCommand.Run());
            }),
        new SlashCommand(
            "Unnest",
            "Explode the active cell's nested formula into a =LET(...) with one named step per call",
            () =>
            {
                Hide();
                ExcelAsyncUtil.QueueAsMacro(() => UnnestCommand.Run());
            }),
        new SlashCommand(
            "State",
            "State engine on the active cell: add, rename or remove a variable; fold or unfold; inline or named. An empty cell gets a new one",
            () =>
            {
                Hide();
                ExcelAsyncUtil.QueueAsMacro(() => StateStepCommand.Run());
            },
            () => Labs.IsOn(Labs.State)),
        new SlashCommand(
            PopupCommands.Debug,
            "Toggle state debug on the active cell (the /State dialog's Debug box, with no dialog)",
            () =>
            {
                Hide();
                ExcelAsyncUtil.QueueAsMacro(() => StateStepCommand.ToggleDebug());
            },
            () => Labs.IsOn(Labs.State)),
        new SlashCommand(
            PopupCommands.Library,
            "Load one library, or search the lambdas (Tab switches; Esc comes back here)",
            ShowLibraries),
        new SlashCommand(
            PopupCommands.ImportAllPrefixed,
            "Import every lambda from every configured library using each library's default prefix",
            () =>
            {
                Hide();
                ImportAllLambdasCommand.Run();
            }),
        new SlashCommand(
            PopupCommands.ImportAllBare,
            "Import every lambda from every configured library with bare names (no prefix)",
            () =>
            {
                Hide();
                ImportAllLambdasCommand.Run(applyPrefix: false);
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

    /// <summary>The prefix the workbook's copy was loaded with; null when it isn't loaded.</summary>
    public string? LoadedPrefix { get; init; }

    /// <summary>The workbook holds this library, but not as the add-in has it now (#452).</summary>
    public bool Stale { get; init; }
    public int Differ { get; init; }
    public int Total { get; init; }

    public string LoadedBrush => Stale ? "#d7ba7d" : "#608b4e";
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
    ///     Whether this entry is a named-range constant rather than a LAMBDA.
    /// </summary>
    public bool IsConstant { get; init; }

    /// <summary>
    ///     Shows the "const" badge only for constant entries.
    /// </summary>
    public Visibility ConstBadgeVisibility => IsConstant ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    ///     Tooltip value for the description. Returns null for empty descriptions so WPF
    ///     suppresses the tooltip entirely rather than showing a blank popup.
    /// </summary>
    public object? DescriptionToolTip => string.IsNullOrEmpty(Description) ? null : Description;
}
