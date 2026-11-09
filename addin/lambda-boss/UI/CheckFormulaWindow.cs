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

    private CheckFormulaWindow(IntPtr excelHwnd, FormulaCheckResult? result, string source, DateTime? namesTakenUtc)
    {
        _excelHwnd = excelHwnd;
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
            if (e.Key is Key.Escape or Key.Enter)
            {
                e.Handled = true;
                Close();
            }
        };
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this))
                _current = null;
            if (_excelHwnd != IntPtr.Zero)
                SetForegroundWindow(_excelHwnd);
        };
    }

    /// <summary>From Excel's main thread: shows the result on the window thread.</summary>
    public static void Show(IntPtr excelHwnd, FormulaCheckResult? result, string source, DateTime? namesTakenUtc) =>
        ShowLambdaPopupCommand.InvokeOnWindowThread(d =>
            d.BeginInvoke(new Action(() => ShowOnWindowThread(excelHwnd, result, source, namesTakenUtc))));

    public static void ShowMessage(string message)
    {
        ShowLambdaPopupCommand.InvokeOnWindowThread(d => d.BeginInvoke(new Action(() =>
            MessageBox.Show(message, "Lambda Boss", MessageBoxButton.OK, MessageBoxImage.Information))));
    }

    /// <summary>On the window thread.</summary>
    public static void ShowOnWindowThread(IntPtr excelHwnd, FormulaCheckResult? result, string source, DateTime? namesTakenUtc)
    {
        try
        {
            _current?.Close();
            var w = new CheckFormulaWindow(excelHwnd, result, source, namesTakenUtc);
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

    private static StackPanel Build(FormulaCheckResult? result, string source, DateTime? namesTakenUtc)
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

        // One entry per problem: what, and an excerpt around it.
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var n = 0;
        foreach (var p in result.Problems)
        {
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
                Foreground = p.IsNote ? Brushes.DarkGoldenrod : BadBrush
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

            if (!string.IsNullOrEmpty(p.Suggestion))
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
        FormulaProblemKind.UnknownName => "Unknown name",
        FormulaProblemKind.NotLoaded => "Library LAMBDA not loaded",
        FormulaProblemKind.UnknownReference => "Unknown sheet, table or column",
        FormulaProblemKind.UnusedName => "Note: unused name",
        _ => kind.ToString()
    };

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
