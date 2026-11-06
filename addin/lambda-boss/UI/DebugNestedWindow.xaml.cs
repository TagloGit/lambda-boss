using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LambdaBoss.UI;

/// <summary>
///     Spec 0010 (spike) / issue #279 — the <c>/Debug Nested</c> window. A
///     modeless debugger for a formula's <c>LAMBDA(...)</c> scopes: the user
///     picks a scope, pins each in-scope parameter to a concrete example value
///     (defaults suggested for recognised iterators), and clicks Evaluate to see
///     each decomposed step of the lambda body compute a live value for that
///     pinned example. Step decomposition is pure
///     (<see cref="DebugNestedEngine" />); the values come from the supplied
///     <c>evaluate</c> delegate, which probes the live grid on the Excel macro
///     thread. The window writes nothing back to the workbook — it's a read-only
///     debugging view.
/// </summary>
public partial class DebugNestedWindow
{
    private static readonly Brush ValueBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b5cea8"));

    private static readonly Brush ErrorBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f48771"));

    private static readonly Brush PendingBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6a6a6a"));

    private static readonly Brush ResultNameBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dcdcaa"));

    private static readonly Brush StepNameBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9cdcfe"));

    private readonly IReadOnlyList<DebugScope> _scopes;
    private readonly string _formula;
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<DebugValue>> _evaluate;

    private readonly ObservableCollection<PinVm> _pins = [];
    private readonly ObservableCollection<StepVm> _steps = [];

    private bool _suppress;

    public DebugNestedWindow(
        string formula,
        DebugDiscovery discovery,
        Func<IReadOnlyList<string>, IReadOnlyList<DebugValue>> evaluate)
    {
        InitializeComponent();

        _formula = formula;
        _scopes = discovery.Scopes;
        _evaluate = evaluate;

        OriginalFormulaText.Text = formula;
        PinsList.ItemsSource = _pins;
        StepsList.ItemsSource = _steps;

        var scopeVms = _scopes
            .Select(s => new ScopeVm(s.Key, ScopeDisplay(s)))
            .ToList();
        ScopeCombo.ItemsSource = scopeVms;

        if (scopeVms.Count > 0)
        {
            _suppress = true;
            ScopeCombo.SelectedIndex = 0;
            _suppress = false;
            LoadScope();
        }
    }

    private DebugScope? CurrentScope =>
        ScopeCombo.SelectedItem is ScopeVm vm
            ? _scopes.FirstOrDefault(s => s.Key == vm.Key)
            : null;

    private int ExampleIndex
    {
        get
        {
            if (int.TryParse(IndexBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1)
                return n;
            return 1;
        }
    }

    private void ScopeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        LoadScope();
    }

    private void SuggestButton_Click(object sender, RoutedEventArgs e)
    {
        FillSuggestedPins();
        RebuildSteps();
    }

    /// <summary>
    ///     Switches to the selected scope: suggests a pin for every in-scope
    ///     parameter and rebuilds the (value-less) step list.
    /// </summary>
    private void LoadScope()
    {
        FillSuggestedPins();
        RebuildSteps();
    }

    private void FillSuggestedPins()
    {
        _pins.Clear();
        var scope = CurrentScope;
        if (scope is null) return;

        var suggested = DebugNestedEngine.SuggestPins(_formula, scope.Key, ExampleIndex);
        foreach (var p in suggested)
            _pins.Add(new PinVm(p.Param, p.Expression));

        NoPinsText.Visibility = _pins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    ///     Rebuilds the step rows for the current scope + pins (names / RHS only;
    ///     values are cleared until the next Evaluate). Step structure is
    ///     independent of the pin <em>values</em>, but a blank pin still drops out
    ///     of the evaluable formula, so we rebuild here too.
    /// </summary>
    private void RebuildSteps()
    {
        _steps.Clear();

        var scope = CurrentScope;
        if (scope is null) return;

        var watch = DebugNestedEngine.BuildWatch(_formula, scope.Key, CurrentPins());
        if (watch.Diagnostic is not null)
        {
            StatusText.Text = watch.Diagnostic.Message;
            NoStepsText.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var s in watch.Steps)
            _steps.Add(new StepVm(s.Name, s.Rhs, s.EvaluableFormula, isResult: false));

        _steps.Add(new StepVm("result", Elide(scope.BodyText), watch.FinalEvaluableFormula, isResult: true));

        NoStepsText.Visibility = watch.Steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = watch.Steps.Count == 1
            ? "1 step — click Evaluate to compute values"
            : $"{watch.Steps.Count} steps — click Evaluate to compute values";
    }

    private async void EvaluateButton_Click(object sender, RoutedEventArgs e)
    {
        RebuildSteps();
        if (_steps.Count == 0) return;

        var formulas = _steps.Select(s => s.EvalFormula).ToList();

        EvaluateButton.IsEnabled = false;
        StatusText.Text = "Evaluating…";

        IReadOnlyList<DebugValue> values;
        try
        {
            values = await Task.Run(() => _evaluate(formulas));
        }
        catch (Exception)
        {
            StatusText.Text = "Evaluation failed — see the log.";
            EvaluateButton.IsEnabled = true;
            return;
        }

        for (var i = 0; i < _steps.Count && i < values.Count; i++)
            _steps[i].SetValue(values[i]);

        StatusText.Text = $"Evaluated for example #{ExampleIndex}.";
        EvaluateButton.IsEnabled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private IReadOnlyList<DebugPin> CurrentPins()
    {
        return _pins.Select(p => new DebugPin(p.Param, p.Expression)).ToList();
    }

    private static string ScopeDisplay(DebugScope scope)
    {
        // Indent nested scopes so the lambda nesting reads at a glance.
        var indent = scope.Depth > 0 ? new string(' ', scope.Depth * 4) + "↳ " : "";
        return indent + scope.Label;
    }

    private static string Elide(string text)
    {
        const int max = 80;
        return text.Length <= max ? text : text[..max] + "…";
    }

    private sealed record ScopeVm(string Key, string Display);

    /// <summary>One bindable pin: a parameter name and its example expression.</summary>
    private sealed class PinVm : INotifyPropertyChanged
    {
        private string _expression;

        public PinVm(string param, string expression)
        {
            Param = param;
            _expression = expression;
        }

        public string Param { get; }

        public string Expression
        {
            get => _expression;
            set
            {
                if (_expression == value) return;
                _expression = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }
    }

    /// <summary>One bindable step row: name, RHS, the hidden evaluable formula, and its computed value.</summary>
    private sealed class StepVm : INotifyPropertyChanged
    {
        private bool _isError;
        private string _value = "";

        public StepVm(string name, string rhs, string evalFormula, bool isResult)
        {
            Name = name;
            Rhs = rhs;
            EvalFormula = evalFormula;
            IsResult = isResult;
        }

        public string Name { get; }
        public string Rhs { get; }
        public string EvalFormula { get; }
        public bool IsResult { get; }

        public string Value
        {
            get => _value;
            private set
            {
                if (_value == value) return;
                _value = value;
                OnPropertyChanged();
            }
        }

        public Brush NameBrush => IsResult ? ResultNameBrush : StepNameBrush;

        public Brush ValueBrush => string.IsNullOrEmpty(_value)
            ? PendingBrush
            : _isError ? ErrorBrush : DebugNestedWindow.ValueBrush;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetValue(DebugValue v)
        {
            _isError = v.IsError;
            Value = v.Display;
            OnPropertyChanged(nameof(ValueBrush));
        }

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }
    }
}
