using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using LambdaBoss.Commands;
using LambdaBoss.Common;

namespace LambdaBoss.UI;

/// <summary>
///     Check Formula's result (spec 0014): the formula with each problem highlighted, then one
///     line per problem with a short excerpt around it. Built in code; one window at a time, on the
///     popup's WPF thread. Esc or Enter closes it and hands focus back to Excel, which is still
///     editing if it was.
/// </summary>
internal sealed class CheckFormulaWindow : Window
{
    private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xFF, 0xC9, 0xC9));
    private static readonly Brush NoteBrush = Frozen(Color.FromRgb(0xFF, 0xEC, 0xB3));
    private static readonly Brush RelatedBrush = Frozen(Color.FromRgb(0xC9, 0xDD, 0xFF));
    private static readonly Brush OkBrush = Frozen(Color.FromRgb(0x1E, 0x7B, 0x34));
    private static readonly Brush BadBrush = Frozen(Color.FromRgb(0xB0, 0x1E, 0x1E));
    private static readonly Brush PanelBrush = Frozen(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush FixBrush = Frozen(Color.FromRgb(0xB8, 0xF0, 0xC0));
    private static readonly Brush FixPanelBrush = Frozen(Color.FromRgb(0xF0, 0xFA, 0xF2));
    private static readonly FontFamily Mono = new("Consolas");

    // Frozen, because the type may first be touched on Excel's thread and used on the window thread.
    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static CheckFormulaWindow? _current;
    private readonly IntPtr _excelHwnd;
    private readonly FixTarget? _target;
    private readonly bool _canRevert;
    private bool _closing;

    private CheckFormulaWindow(IntPtr excelHwnd, FormulaCheckResult? result, string source, DateTime? namesTakenUtc,
        FixTarget? target)
    {
        _excelHwnd = excelHwnd;
        _target = target;
        _canRevert = result is not null && FormulaFixer.CanRevert(target, result.Formula);
        Title = "Check Formula";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStyle = WindowStyle.ToolWindow;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.White;
        Content = Build(result, source, namesTakenUtc);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _canRevert && _target is not null)
            {
                e.Handled = true;
                FormulaFixer.Revert(_target, Close);
                return;
            }

            if (e.Key is Key.Escape or Key.Enter)
            {
                e.Handled = true;
                Close();
            }
        };
        // A window left open goes stale while Tim works on: close it when Excel takes focus back,
        // so a fix is never applied to text that has changed since the check (review of #434).
        Deactivated += (_, _) =>
        {
            if (!_closing)
                Close();
        };
        Closing += (_, _) => _closing = true;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this))
                _current = null;
            if (_excelHwnd != IntPtr.Zero)
                SetForegroundWindow(_excelHwnd);
        };
    }

    /// <summary>From Excel's main thread: shows the result on the window thread.</summary>
    public static void Show(IntPtr excelHwnd, FormulaCheckResult? result, string source, DateTime? namesTakenUtc,
        FixTarget? target) =>
        ShowLambdaPopupCommand.InvokeOnWindowThread(d =>
            d.BeginInvoke(new Action(() => ShowOnWindowThread(excelHwnd, result, source, namesTakenUtc, target))));

    public static void ShowMessage(string message)
    {
        ShowLambdaPopupCommand.InvokeOnWindowThread(d => d.BeginInvoke(new Action(() =>
            MessageBox.Show(message, "Lambda Boss", MessageBoxButton.OK, MessageBoxImage.Information))));
    }

    /// <summary>On the window thread.</summary>
    public static void ShowOnWindowThread(IntPtr excelHwnd, FormulaCheckResult? result, string source, DateTime? namesTakenUtc,
        FixTarget? target)
    {
        try
        {
            _current?.Close();
            var w = new CheckFormulaWindow(excelHwnd, result, source, namesTakenUtc, target);
            _current = w;
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            if (excelHwnd != IntPtr.Zero)
                WindowPositioner.CenterOnExcel(excelHwnd, hwnd);
            w.Show();
            w.Activate();
        }
        catch (Exception ex)
        {
            Logger.Error("CheckFormulaWindow", ex);
        }
    }

    // ------------------------------------------------------------ layout

    private StackPanel Build(FormulaCheckResult? result, string source, DateTime? namesTakenUtc)
    {
        var root = new StackPanel { Margin = new Thickness(14) };

        if (result is null)
        {
            root.Children.Add(new TextBlock { Text = source, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            root.Children.Add(Footer());
            return root;
        }

        var errors = result.Problems.Count(p => !p.IsNote);
        var notes = result.Problems.Count(p => p.Kind == FormulaProblemKind.UnusedName);
        var headline = errors == 0
            ? "No bracket or name problems found."
            : $"{errors} problem{(errors == 1 ? "" : "s")} found.";
        if (notes > 0)
            headline += $" {notes} note{(notes == 1 ? "" : "s")}.";

        // One bracket edit fixes it: lead with that edit (issue #426).
        if (result.HasSingleFix)
            headline = result.Repairs[0].Describe(result.Formula);

        root.Children.Add(new TextBlock
        {
            Text = headline,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = errors == 0 ? OkBrush : BadBrush
        });
        var sub = source;
        if (namesTakenUtc is { } taken)
            sub += $" · workbook names as of {taken.ToLocalTime():HH:mm:ss}";
        root.Children.Add(new TextBlock { Text = sub, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 10) });

        // Fixes (issue #426): only where there's somewhere to put them back.
        var fixes = _target is null ? null : FormulaFixes.For(result);
        if (_canRevert || fixes?.All is not null)
        {
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (_canRevert)
                bar.Children.Add(ActionButton("Revert last fix (Ctrl+Z)", () => FormulaFixer.Revert(_target!, Close)));
            if (fixes?.All is { } all)
                bar.Children.Add(ActionButton($"{all.Label} — the formula becomes the one below", () => FormulaFixer.Apply(all, _target!, Close)));
            root.Children.Add(bar);
            if (fixes?.All is { } a)
                root.Children.Add(new TextBlock
                {
                    Text = Shorten(a.After),
                    FontFamily = Mono,
                    FontSize = 13,
                    Background = FixBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                });
        }

        // The whole formula, problems highlighted.
        var formulaText = new TextBlock
        {
            FontFamily = Mono,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(8)
        };
        foreach (var inline in Highlighted(result.Formula, 0, result.Formula.Length, result.Problems, null))
            formulaText.Inlines.Add(inline);
        root.Children.Add(new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Background = PanelBrush,
            Child = new ScrollViewer
            {
                Content = formulaText,
                MaxHeight = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        });

        // The bracket repair, when the search found anything.
        var repairPanel = RepairPanel(result, fixes?.Bracket);
        if (repairPanel is not null)
            root.Children.Add(repairPanel);

        // One entry per problem: what, and an excerpt around it. Problems the fix clears are
        // listed after the rest, under "caused by this".
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var n = 0;
        var causedHeader = false;
        foreach (var p in result.Problems.OrderBy(p => p.Kind == FormulaProblemKind.More ? 2 : p.IsConsequence ? 1 : 0))
        {
            if (p.IsConsequence && !causedHeader)
            {
                causedHeader = true;
                list.Children.Add(new TextBlock
                {
                    Text = "Caused by this ) — the fix clears them:",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.DimGray,
                    Margin = new Thickness(0, 4, 0, 6)
                });
            }

            if (p.Kind == FormulaProblemKind.More)
            {
                list.Children.Add(new TextBlock { Text = p.Message, Foreground = Brushes.Gray });
                continue;
            }

            n++;
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            item.Children.Add(new TextBlock
            {
                Text = $"{n}. {Label(p.Kind)} · character {p.Start + 1}",
                FontWeight = FontWeights.SemiBold,
                Foreground = p.IsNote ? Brushes.DarkGoldenrod : p.IsConsequence ? Brushes.DimGray : BadBrush
            });
            item.Children.Add(new TextBlock { Text = p.Message, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
            if (p.Start < result.Formula.Length)
            {
                var excerpt = new TextBlock { FontFamily = Mono, FontSize = 14, Margin = new Thickness(0, 3, 0, 0) };
                var from = Math.Max(0, p.Start - 30);
                var to = Math.Min(result.Formula.Length, p.Start + p.Length + 30);
                if (from > 0)
                    excerpt.Inlines.Add(new Run("…") { Foreground = Brushes.Gray });
                foreach (var inline in Highlighted(result.Formula, from, to, new[] { p }, p))
                    excerpt.Inlines.Add(inline);
                if (to < result.Formula.Length)
                    excerpt.Inlines.Add(new Run("…") { Foreground = Brushes.Gray });
                item.Children.Add(excerpt);
            }

            if (fixes is not null && fixes.ByProblem.TryGetValue(p, out var nameFixes))
            {
                foreach (var fix in nameFixes)
                    item.Children.Add(FixRow(fix, p.FixLabel ?? $"Use {fix.Label[(fix.Label.IndexOf('→') + 2)..]}"));
            }
            else if (!string.IsNullOrEmpty(p.Suggestion))
                item.Children.Add(new TextBlock
                {
                    Text = $"Nearest valid name: {p.Suggestion}",
                    FontFamily = Mono,
                    Foreground = OkBrush,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            list.Children.Add(item);
        }

        root.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        root.Children.Add(Footer());
        return root;
    }

    /// <summary>The edit(s) the repair search found, each with what the changed call then reads.</summary>
    private FrameworkElement? RepairPanel(FormulaCheckResult result, FormulaFix? bracketFix)
    {
        if (result.Repair is RepairOutcome.NotNeeded)
            return null;

        var panel = new StackPanel();
        string? intro = result.Repair switch
        {
            RepairOutcome.One => result.RepairNote is null
                ? "This one bracket edit makes the whole formula valid:"
                : "This bracket edit makes the whole formula valid. The search was cut short, so there may be others:",
            RepairOutcome.Several => "Each of these single bracket edits makes the formula valid (likeliest first). Check which one you meant:",
            RepairOutcome.Closest => $"No single bracket edit makes the formula valid. The closest leave {result.Repairs[0].Remaining} problem{(result.Repairs[0].Remaining == 1 ? "" : "s")}:",
            RepairOutcome.NoneFound => "No single bracket edit fixes this.",
            _ => null
        };
        if (intro is not null)
            panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, FontSize = 13 });

        foreach (var r in result.Repairs)
        {
            var item = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (!result.HasSingleFix)
                item.Children.Add(new TextBlock
                {
                    Text = r.Describe(result.Formula),
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

            // The spot in the formula as it is.
            item.Children.Add(Excerpt(result.Formula, r.Position, r.Insert ? 0 : 1, r.Insert ? FixBrush : ErrorBrush,
                r.Insert ? "⟨)⟩" : null));

            // What the changed call reads after the edit.
            if (r.CallStart >= 0 && r.CallLength > 0)
            {
                item.Children.Add(new TextBlock
                {
                    Text = "After the edit it reads:",
                    Foreground = Brushes.DimGray,
                    Margin = new Thickness(0, 4, 0, 0)
                });
                item.Children.Add(new TextBlock
                {
                    Text = Shorten(r.Fixed.Substring(r.CallStart, r.CallLength)),
                    FontFamily = Mono,
                    FontSize = 14,
                    Background = FixBrush,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            if (bracketFix is not null && result.HasSingleFix)
                item.Children.Add(new WrapPanel
                {
                    Margin = new Thickness(0, 6, 0, 0),
                    Children = { ActionButton(bracketFix.Label, () => FormulaFixer.Apply(bracketFix, _target!, Close)) }
                });

            panel.Children.Add(item);
        }

        if (result.RepairNote is { } note)
            panel.Children.Add(new TextBlock
            {
                Text = note,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0)
            });

        return new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Background = FixPanelBrush,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 10, 0, 0),
            Child = panel
        };
    }

    /// <summary>One line, at most 160 characters: the start and the end of a long call.</summary>
    private static string Shorten(string call)
    {
        var flat = string.Join(" ", call.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        return flat.Length <= 160 ? flat : flat[..100] + " … " + flat[^56..];
    }

    /// <summary>About 30 characters each side of a spot, the spot marked; <paramref name="insertMark"/> shows an insertion.</summary>
    private static TextBlock Excerpt(string text, int start, int length, Brush brush, string? insertMark)
    {
        var block = new TextBlock { FontFamily = Mono, FontSize = 14, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
        var from = Math.Max(0, start - 30);
        var to = Math.Min(text.Length, start + length + 30);
        string Flat(string s) => s.Replace("\r", "").Replace('\n', ' ');
        if (from > 0)
            block.Inlines.Add(new Run("…") { Foreground = Brushes.Gray });
        block.Inlines.Add(new Run(Flat(text[from..start])));
        block.Inlines.Add(new Run(insertMark ?? Flat(text.Substring(start, length))) { Background = brush, FontWeight = FontWeights.Bold });
        block.Inlines.Add(new Run(Flat(text[(start + length)..to])));
        if (to < text.Length)
            block.Inlines.Add(new Run("…") { Foreground = Brushes.Gray });
        return block;
    }

    private static Button ActionButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        b.Click += (_, _) =>
        {
            try
            {
                onClick();
            }
            catch (Exception ex)
            {
                Logger.Error("CheckFormulaWindow/fix", ex);
            }
        };
        return b;
    }

    /// <summary>A fix button and, beside it, the changed line with the change highlighted.</summary>
    private FrameworkElement FixRow(FormulaFix fix, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(ActionButton(label, () => FormulaFixer.Apply(fix, _target!, Close)));

        var (start, end) = fix.PreviewLine;
        var hs = Math.Max(start, Math.Min(fix.HighlightStart, end));
        var he = Math.Max(hs, Math.Min(fix.HighlightStart + fix.HighlightLength, end));
        // At most about 40 characters each side of the change.
        var from = Math.Max(start, hs - 40);
        var to = Math.Min(end, he + 40);
        var preview = new TextBlock { FontFamily = Mono, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        if (from > start)
            preview.Inlines.Add(new Run("…") { Foreground = Brushes.Gray });
        preview.Inlines.Add(new Run(fix.After[from..hs]));
        preview.Inlines.Add(new Run(fix.After[hs..he]) { Background = FixBrush, FontWeight = FontWeights.Bold });
        preview.Inlines.Add(new Run(fix.After[he..to]));
        if (to < end)
            preview.Inlines.Add(new Run("…") { Foreground = Brushes.Gray });
        row.Children.Add(preview);
        return row;
    }

    private static TextBlock Footer() => new()
    {
        Text = $"Esc closes and returns to Excel. {CheckFormulaCommand.ShortcutLabel} checks again, in a cell or while editing.",
        Foreground = Brushes.Gray,
        FontSize = 11,
        Margin = new Thickness(0, 6, 0, 0)
    };

    /// <summary>
    ///     Runs for <paramref name="text"/>[from..to] with each problem's span (and its related span)
    ///     given a background. <paramref name="focus"/> bolds one problem.
    /// </summary>
    private static IEnumerable<Inline> Highlighted(
        string text, int from, int to, IEnumerable<FormulaProblem> problems, FormulaProblem? focus)
    {
        var marks = new Brush?[to - from];
        var bold = new bool[to - from];

        void Mark(int start, int length, Brush brush, bool isBold, bool overwrite)
        {
            var len = Math.Max(1, length);
            for (var i = Math.Max(start, from); i < Math.Min(start + len, to); i++)
            {
                if (overwrite || marks[i - from] is null)
                    marks[i - from] = brush;
                bold[i - from] |= isBold;
            }
        }

        var list = problems.ToList();
        foreach (var p in list.Where(p => p.RelatedStart >= 0))
            Mark(p.RelatedStart, p.RelatedLength, RelatedBrush, false, false);
        foreach (var p in list.Where(p => p.IsNote))
            Mark(p.Start, p.Length, NoteBrush, ReferenceEquals(p, focus), true);
        foreach (var p in list.Where(p => !p.IsNote))
            Mark(p.Start, p.Length, ErrorBrush, ReferenceEquals(p, focus), true);

        var runStart = 0;
        for (var i = 1; i <= marks.Length; i++)
        {
            if (i < marks.Length && marks[i] == marks[runStart] && bold[i] == bold[runStart])
                continue;
            var run = new Run(text.Substring(from + runStart, i - runStart));
            if (marks[runStart] is { } b)
                run.Background = b;
            if (bold[runStart])
                run.FontWeight = FontWeights.Bold;
            yield return run;
            runStart = i;
        }
    }

    private static string Label(FormulaProblemKind kind) => kind switch
    {
        FormulaProblemKind.ExtraCloseBracket => "Extra )",
        FormulaProblemKind.UnclosedBracket => "Unclosed (",
        FormulaProblemKind.CommaOutsideFunction => "Misplaced )",
        FormulaProblemKind.Unterminated => "Never closed",
        FormulaProblemKind.BadShape => "LET / LAMBDA shape",
        FormulaProblemKind.ArgumentCount => "Argument count",
        FormulaProblemKind.UnknownName => "Unknown name",
        FormulaProblemKind.NotLoaded => "Library LAMBDA not loaded",
        FormulaProblemKind.UnknownReference => "Unknown sheet, table or column",
        FormulaProblemKind.ImplicitIntersection => "@ on a fold's array",
        FormulaProblemKind.UnusedName => "Note: unused name",
        _ => kind.ToString()
    };

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
