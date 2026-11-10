using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     The /State debug toggle (spec 0015, "Debug toggle (#440)"). A debug fold is a SCAN-family
///     fold directly inside <c>SplitState(…, 1)</c>, with temporary outputs in its final SetVars
///     that nothing reads back. Everything is read from the cell's text: no hidden state.
///     Pure text; the dialog and the engine only wire it in.
/// </summary>
public static class StateDebug
{
    private const string WrapperName = "SplitState";

    // The text before a fold ends with the wrapper's opening, and the text after starts with
    // its header argument and close.
    private static readonly Regex Opening = new(
        @"(?<![A-Za-z0-9_.])(?:[A-Za-z_][A-Za-z0-9_]*\.)?SplitState\s*\(\s*$", RegexOptions.IgnoreCase);

    private static readonly Regex Closing = new(@"^\s*,\s*(?:1|TRUE)\s*\)", RegexOptions.IgnoreCase);

    // The way back from each SCAN: the REDUCE it was switched from.
    private static readonly (string Reduce, string Scan)[] Twins =
    {
        ("REDUCE", "SCAN"), ("REDUCEUNTIL", "SCANUNTIL"), ("REDUCEWHILE", "SCANWHILE")
    };

    /// <summary>True for SCAN and the library folds that scan (SCANUNTIL, SCANWHILE, SCANLEAD).</summary>
    public static bool IsScan(StateFoldKind? fold) =>
        fold != null && fold.Name.StartsWith("SCAN", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     True when the text around a fold is <c>SplitState(</c> before and <c>, 1)</c> after. The
    ///     two returned texts are the same text with that wrapper taken off.
    /// </summary>
    public static bool TryUnwrap(string before, string after, out string unwrappedBefore, out string unwrappedAfter)
    {
        unwrappedBefore = before;
        unwrappedAfter = after;

        var open = Opening.Match(before);
        var close = Closing.Match(after);
        if (!open.Success || !close.Success) return false;

        unwrappedBefore = before[..open.Index];
        unwrappedAfter = after[close.Length..];
        return true;
    }

    /// <summary>The text around a fold with the wrapper put on, spelled with the state library's prefix.</summary>
    public static (string Before, string After) Wrap(string before, string after, string prefix) =>
        (before + prefix + WrapperName + "(", ", 1)" + after);

    /// <summary>
    ///     The fold on the other side of the toggle: REDUCE ⇄ SCAN, REDUCEUNTIL ⇄ SCANUNTIL,
    ///     REDUCEWHILE ⇄ SCANWHILE. Null when the fold has no twin, or the library fold isn't known.
    /// </summary>
    public static StateFoldKind? Twin(StateFoldKind fold, IReadOnlyList<StateFoldKind> kinds, bool toScan)
    {
        if (fold is null) throw new ArgumentNullException(nameof(fold));
        if (kinds is null) throw new ArgumentNullException(nameof(kinds));

        foreach (var (reduce, scan) in Twins)
        {
            var from = toScan ? reduce : scan;
            var to = toScan ? scan : reduce;
            if (!string.Equals(fold.Name, from, StringComparison.OrdinalIgnoreCase)) continue;

            if (string.Equals(to, "REDUCE", StringComparison.OrdinalIgnoreCase)) return StateFoldKind.Reduce;
            if (string.Equals(to, "SCAN", StringComparison.OrdinalIgnoreCase)) return StateFoldKind.Scan;
            return kinds.FirstOrDefault(k => !k.IsNative && string.Equals(k.Name, to, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>
    ///     The one-command toggle: the engine's variables and name as they are, the fold switched
    ///     to its twin, and the debug state set. <see cref="StateEngines.Apply" /> wraps or unwraps
    ///     the fold and, on the way out, drops the debug-only outputs. A fold with no twin stays as
    ///     it is: going in then fails with a message, going out just takes the wrapper off.
    /// </summary>
    public static StateEngineChange Toggle(StateEngine engine, bool on, IReadOnlyList<StateFoldKind> kinds)
    {
        if (engine is null) throw new ArgumentNullException(nameof(engine));

        var fold = engine.Fold == null ? null : Twin(engine.Fold, kinds, on) ?? engine.Fold;
        return new StateEngineChange(engine.Rows, fold, engine.StepName, on);
    }

    /// <summary>
    ///     The output keys that are debug-only: set by the step's final SetVars, never read back
    ///     with <c>GetVar</c> anywhere in the step and not in the seed. They are not state
    ///     variables, so the variable list leaves them out and variable edits leave them alone.
    /// </summary>
    public static IReadOnlyList<string> DebugKeys(StateEngine engine)
    {
        if (engine is null) throw new ArgumentNullException(nameof(engine));
        if (engine.IsNew) return Array.Empty<string>();

        try
        {
            var parsed = LetParser.Parse(StateEngines.Intermediate(engine, withOtherArguments: true));
            var step = StateStepParser.TryParse(parsed);
            if (step?.Site == null) return Array.Empty<string>();

            var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in parsed.Bindings.Select(b => b.RhsText).Append(parsed.Body))
                foreach (var call in Calls(text))
                    if (StateStepParser.IsNamed(call.Name, "GetVar") && call.Arguments.Count == 2
                        && StringLiteral(call.Arguments[1]) is { } key)
                        read.Add(key);

            var seeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (engine.Seed.Length > 0)
                foreach (var call in Calls(engine.Seed))
                {
                    if (StateStepParser.IsNamed(call.Name, "SetVars"))
                    {
                        for (var i = 0; i < call.Arguments.Count; i += 2)
                            if (StringLiteral(call.Arguments[i]) is { } key)
                                seeded.Add(key);
                    }
                    else if (StateStepParser.IsNamed(call.Name, "SetVar") && call.Arguments.Count == 3
                        && StringLiteral(call.Arguments[1]) is { } chained)
                    {
                        seeded.Add(chained);
                    }
                }

            return step.Variables.Select(v => v.Key).Where(k => !read.Contains(k) && !seeded.Contains(k)).ToList();
        }
        catch (FormatException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<FunctionCallNode> Calls(string expression) =>
        StateStepParser.Walk(FormulaParser.Parse(expression).Root).OfType<FunctionCallNode>();

    private static string? StringLiteral(FormulaNode node) =>
        node is LeafNode { TokenType: FormulaTokenType.String } leaf
            ? leaf.Text[1..^1].Replace("\"\"", "\"")
            : null;
}
