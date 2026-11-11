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

    private static readonly Regex Literal = new("\"((?:[^\"]|\"\")*)\"");

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

        // Off on a form that isn't debug changes nothing: a SCAN Tim wrote himself stays a SCAN.
        if (!on && !engine.IsDebug)
            return new StateEngineChange(engine.Rows, engine.Fold, engine.StepName, false);

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

            // A string literal anywhere outside the fold may be a key read from the result.
            foreach (Match m in Literal.Matches(engine.Before + "\n" + engine.After))
                read.Add(m.Groups[1].Value.Replace("\"\"", "\""));

            foreach (var text in parsed.Bindings.Select(b => b.RhsText).Append(parsed.Body))
                foreach (var call in Calls(text))
                {
                    // A key read some way we can't follow: be conservative, every output is real.
                    if (StateStepParser.IsNamed(call.Name, "GetVarAt")) return Array.Empty<string>();
                    if (!StateStepParser.IsNamed(call.Name, "GetVar")) continue;
                    if (call.Arguments.Count != 2 || StringLiteral(call.Arguments[1]) is not { } key)
                        return Array.Empty<string>();
                    read.Add(key);
                }

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

    /// <summary>The key the incoming value of state variable <paramref name="key" /> shows under.</summary>
    public static string IncomingKey(string key) => IncomingPrefix + key;

    /// <summary>Incoming values show as <c>in.crates</c>: a usable key, read as "crates coming in".</summary>
    public const string IncomingPrefix = "in.";

    /// <summary>
    ///     What Debug on adds to the step's output (spec 0015, "Debug shows everything"): the
    ///     incoming value of each state variable (its <c>GetVar</c> line, as <c>in.key</c>), then
    ///     every LET intermediate under its own name. Left out: LAMBDA lines (SetVars can't hold a
    ///     function), the line that is the output, lines already passed as an output's value (the
    ///     next lines: their value is already in the row), and any key the output already sets.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> DebugOutputs(StateEngine engine)
    {
        if (engine is null) throw new ArgumentNullException(nameof(engine));
        if (engine.IsNew) return Array.Empty<(string, string)>();

        try
        {
            var parsed = LetParser.Parse(StateEngines.Intermediate(engine));
            var step = StateStepParser.TryParse(parsed);
            if (step?.Site == null) return Array.Empty<(string, string)>();

            // A key that wouldn't read back as debug-only (a literal outside the fold, a seed key)
            // would become a real variable and stay on Debug off: never add one.
            var taken = new HashSet<string>(step.Variables.Select(v => v.Key), StringComparer.OrdinalIgnoreCase);
            taken.UnionWith(LiteralsAround(engine));
            taken.UnionWith(SeedKeys(engine));

            // The output's line and everything after it (a guard held in a later line) can't be
            // read from inside the output.
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (step.Site.BindingIndex is { } outputLine)
                foreach (var b in parsed.Bindings.Skip(outputLine)) skip.Add(b.Name);

            var local = new HashSet<string>(parsed.Bindings.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

            var pairs = new List<(string Key, string Value)>();
            void Add(string key, string value)
            {
                if (!StateScaffold.IsUsableKey(key) || !taken.Add(key)) return;
                pairs.Add((key, value));
            }

            var debug = DebugKeys(engine);
            foreach (var v in step.Variables)
            {
                if (v.GetVarLine != null) skip.Add(v.GetVarLine.Name);
                if (v.NextLine != null) skip.Add(v.NextLine.Name);
                if (v.GetVarLine != null && !debug.Contains(v.Key, StringComparer.OrdinalIgnoreCase))
                    Add(IncomingKey(v.Key), v.GetVarLine.Name);
            }

            foreach (var (name, value) in engine.Bindings)
            {
                if (skip.Contains(name) || IsFunction(value, local)) continue;
                Add(name, name);
            }

            return pairs;
        }
        catch (FormatException)
        {
            return Array.Empty<(string, string)>();
        }
    }

    private static readonly HashSet<string> Arithmetic = new() { "+", "-", "*", "/", "^" };

    /// <summary>
    ///     A note when a seed value is <c>""</c> and the step does arithmetic on that variable's
    ///     <c>GetVar</c> line: <c>"" + 4</c> is #VALUE! (Tim's R4 retry, 11 Nov). Null otherwise.
    ///     Read-only: nothing is changed, the dialog just says it.
    /// </summary>
    public static string? BlankSeedNote(StateEngine engine)
    {
        if (engine is null) throw new ArgumentNullException(nameof(engine));
        if (engine.IsNew || engine.Seed.Length == 0) return null;

        try
        {
            if (FormulaParser.Parse(engine.Seed).Root is not FunctionCallNode seed
                || !StateStepParser.IsNamed(seed.Name, "SetVars")) return null;

            var blank = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < seed.Arguments.Count; i += 2)
                if (StringLiteral(seed.Arguments[i]) is { } key && StringLiteral(seed.Arguments[i + 1]) == "")
                    blank.Add(key);
            if (blank.Count == 0) return null;

            var parsed = LetParser.Parse(StateEngines.Intermediate(engine));
            var step = StateStepParser.TryParse(parsed);
            if (step == null) return null;

            var bindingOf = step.Variables
                .Where(v => v.GetVarLine != null && blank.Contains(v.Key))
                .ToDictionary(v => v.GetVarLine!.Name, v => v.Key, StringComparer.OrdinalIgnoreCase);
            if (bindingOf.Count == 0) return null;

            var keys = new List<string>();
            var users = new List<string>();
            foreach (var (name, text) in engine.Bindings.Append(("the output", engine.Output)))
                foreach (var op in StateStepParser.Walk(FormulaParser.Parse(text).Root).OfType<BinaryNode>())
                {
                    if (!Arithmetic.Contains(op.Operator)) continue;
                    foreach (var side in new[] { op.Left, op.Right })
                        if (side is LeafNode { TokenType: FormulaTokenType.Name } leaf
                            && bindingOf.TryGetValue(leaf.Text, out var key))
                        {
                            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase)) keys.Add(key);
                            if (!users.Contains(name, StringComparer.OrdinalIgnoreCase)) users.Add(name);
                        }
                }

            if (keys.Count == 0) return null;
            var those = keys.Count == 1 ? keys[0] + " starts" : string.Join(" and ", keys) + " start";
            var it = keys.Count == 1 ? "it" : "them";
            return $"{those} as \"\" in the seed, and {string.Join(", ", users)} adds or multiplies with {it}: \"\" + a number is #VALUE!. Seed a number (0) instead.";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    ///     A line that may hold a function, which SetVars can't store: a LAMBDA, a LET that ends in
    ///     one, or a bare name that isn't the step's own (a workbook LAMBDA or a range: left out).
    /// </summary>
    private static bool IsFunction(string value, HashSet<string> local)
    {
        try
        {
            return IsFunction(FormulaParser.Parse(value).Root, local);
        }
        catch (FormatException)
        {
            return true;
        }
    }

    private static bool IsFunction(FormulaNode node, HashSet<string> local) => node switch
    {
        FunctionCallNode call when string.Equals(call.Name, "LAMBDA", StringComparison.OrdinalIgnoreCase) => true,
        FunctionCallNode call when string.Equals(call.Name, "LET", StringComparison.OrdinalIgnoreCase)
                                   && call.Arguments.Count > 0 => IsFunction(call.Arguments[^1], local),
        LeafNode { TokenType: FormulaTokenType.Name } leaf => !local.Contains(leaf.Text),
        _ => false
    };

    private static IEnumerable<string> LiteralsAround(StateEngine engine) =>
        Literal.Matches(engine.Before + "\n" + engine.After).Cast<Match>()
            .Select(m => m.Groups[1].Value.Replace("\"\"", "\""));

    private static IEnumerable<string> SeedKeys(StateEngine engine)
    {
        if (engine.Seed.Length == 0) yield break;
        foreach (var call in Calls(engine.Seed))
        {
            if (StateStepParser.IsNamed(call.Name, "SetVars"))
            {
                for (var i = 0; i < call.Arguments.Count; i += 2)
                    if (StringLiteral(call.Arguments[i]) is { } key)
                        yield return key;
            }
            else if (StateStepParser.IsNamed(call.Name, "SetVar") && call.Arguments.Count == 3
                     && StringLiteral(call.Arguments[1]) is { } chained)
            {
                yield return chained;
            }
        }
    }

    private static IEnumerable<FunctionCallNode> Calls(string expression) =>
        StateStepParser.Walk(FormulaParser.Parse(expression).Root).OfType<FunctionCallNode>();

    private static string? StringLiteral(FormulaNode node) =>
        node is LeafNode { TokenType: FormulaTokenType.String } leaf
            ? leaf.Text[1..^1].Replace("\"\"", "\"")
            : null;
}
