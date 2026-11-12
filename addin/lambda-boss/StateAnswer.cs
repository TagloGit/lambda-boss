using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     The fold's answer outputs and the text around a fold (spec 0018, lambda-boss#460 and #461).
///     The answer is a <c>GetVars(…, {"cash","crates"})</c> wrapper round the fold: the ticked
///     variables, in the order Tim set. The debug wrapper sits inside it, directly round the fold,
///     and GetVars passes the debug table through, so Debug on and off work unchanged. Pure text.
/// </summary>
public static class StateAnswer
{
    private const string WrapperName = "GetVars";

    private static readonly Regex Opening = new(
        @"(?<![A-Za-z0-9_.])(?:[A-Za-z_][A-Za-z0-9_]*\.)?GetVars\s*\(\s*$", RegexOptions.IgnoreCase);

    // ", {…})" with the array's strings skipped, so a brace inside a key doesn't end it.
    private static readonly Regex Closing = new(
        @"^\s*,\s*(\{(?:[^{}""]|""(?:[^""]|"""")*"")*\})\s*\)", RegexOptions.IgnoreCase);

    private static readonly Regex Literal = new("\"((?:[^\"]|\"\")*)\"");

    /// <summary>
    ///     The answer keys in order, read from the text around the fold (inside any debug wrapper's
    ///     outside). Empty when there is no answer wrapper.
    /// </summary>
    public static IReadOnlyList<string> Read(string before, string after)
    {
        if (before is null) throw new ArgumentNullException(nameof(before));
        if (after is null) throw new ArgumentNullException(nameof(after));

        StateDebug.TryUnwrap(before, after, out var b, out var a);
        return TryUnwrap(b, a, out _, out _, out var keys) ? keys : Array.Empty<string>();
    }

    /// <summary>
    ///     True when the text around a fold is <c>GetVars(</c> before and <c>, {"…","…"})</c> after,
    ///     with every element a string. The returned texts are the same with that wrapper taken off.
    /// </summary>
    public static bool TryUnwrap(
        string before, string after, out string unwrappedBefore, out string unwrappedAfter, out IReadOnlyList<string> keys)
    {
        unwrappedBefore = before;
        unwrappedAfter = after;
        keys = Array.Empty<string>();

        var open = Opening.Match(before);
        var close = Closing.Match(after);
        if (!open.Success || !close.Success) return false;

        var parsed = ArrayKeys(close.Groups[1].Value);
        if (parsed == null || parsed.Count == 0) return false;

        unwrappedBefore = before[..open.Index];
        unwrappedAfter = after[close.Length..];
        keys = parsed;
        return true;
    }

    /// <summary>
    ///     The text around a fold with the answer set to <paramref name="keys" /> (none: no wrapper).
    ///     A debug wrapper stays innermost, exactly as it was written.
    /// </summary>
    public static (string Before, string After) Set(string before, string after, string prefix, IReadOnlyList<string> keys)
    {
        if (before is null) throw new ArgumentNullException(nameof(before));
        if (after is null) throw new ArgumentNullException(nameof(after));
        if (keys is null) throw new ArgumentNullException(nameof(keys));

        var debug = StateDebug.TryUnwrap(before, after, out var b, out var a);
        var debugOpen = debug ? before[b.Length..] : "";
        var debugClose = debug ? after[..(after.Length - a.Length)] : "";

        if (TryUnwrap(b, a, out var bareBefore, out var bareAfter, out _))
            (b, a) = (bareBefore, bareAfter);
        if (keys.Count > 0)
            (b, a) = (b + prefix + WrapperName + "(", ", " + ArrayText(keys) + ")" + a);

        return (b + debugOpen, debugClose + a);
    }

    /// <summary>The keys as Excel writes an array constant: <c>{"cash","crates"}</c>.</summary>
    public static string ArrayText(IReadOnlyList<string> keys) =>
        "{" + string.Join(",", keys.Select(k => "\"" + k.Replace("\"", "\"\"") + "\"")) + "}";

    // The strings of a one-row or one-column array constant; null when any element isn't one.
    private static List<string>? ArrayKeys(string array)
    {
        var inner = array.Trim()[1..^1];
        var keys = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            var m = Literal.Match(inner, i);
            if (!m.Success || m.Index != i) return null;
            keys.Add(m.Groups[1].Value.Replace("\"\"", "\""));
            i = m.Index + m.Length;
            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            if (i == inner.Length) return keys;
            if (inner[i] != ',' && inner[i] != ';') return null;
            i++;
        }
    }

    // ------------------------------------------------------------ the text around the fold

    /// <summary>A key read from the fold's result outside the step: a GetVar or GetVars literal.</summary>
    internal sealed record KeyRead(int Start, int End, string Key);

    /// <summary>
    ///     The keys the cell reads from a state outside the fold (<c>GetVar(s, "cash")</c> in an
    ///     outer LET, the answer's <c>GetVars</c> array), with their places in the formula. Coarse: a
    ///     GetVar outside the fold is taken to read this fold's state.
    /// </summary>
    internal static IReadOnlyList<KeyRead> ReadsAround(StateEngine engine)
    {
        if (engine.CallText == null || engine.IsWholeFormula) return Array.Empty<KeyRead>();

        var formula = engine.Before + engine.CallText + engine.After;
        var foldStart = engine.Before.Length;
        var foldEnd = foldStart + engine.CallText.Length;

        FormulaAst ast;
        try
        {
            ast = FormulaParser.Parse(formula);
        }
        catch (FormatException)
        {
            return Array.Empty<KeyRead>();
        }

        var reads = new List<KeyRead>();
        foreach (var call in StateStepParser.Walk(ast.Root).OfType<FunctionCallNode>())
        {
            var getVar = StateStepParser.IsNamed(call.Name, "GetVar");
            if (!getVar && !StateStepParser.IsNamed(call.Name, WrapperName)) continue;
            if (call.Arguments.Count != 2 || call.Arguments[1] is not LeafNode leaf) continue;
            if (leaf.Start < foldEnd && leaf.End > foldStart) continue;

            if (leaf.TokenType == FormulaTokenType.String)
                reads.Add(new KeyRead(leaf.Start, leaf.End, leaf.Text[1..^1].Replace("\"\"", "\"")));
            else if (!getVar && leaf.TokenType == FormulaTokenType.Array)
                foreach (Match m in Literal.Matches(leaf.Text))
                    reads.Add(new KeyRead(leaf.Start + m.Index, leaf.Start + m.Index + m.Length, m.Groups[1].Value.Replace("\"\"", "\"")));
        }

        return reads;
    }

    /// <summary>The engine with reads of renamed keys outside the fold renamed too.</summary>
    internal static StateEngine RenameReads(StateEngine engine, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0) return engine;
        var reads = ReadsAround(engine).Where(r => renames.ContainsKey(r.Key)).OrderByDescending(r => r.Start).ToList();
        if (reads.Count == 0) return engine;

        var before = engine.Before;
        var after = engine.After;
        var afterStart = engine.Before.Length + engine.CallText!.Length;
        foreach (var r in reads)
        {
            var text = "\"" + renames[r.Key].Replace("\"", "\"\"") + "\"";
            if (r.Start < engine.Before.Length)
                before = before[..r.Start] + text + before[r.End..];
            else
                after = after[..(r.Start - afterStart)] + text + after[(r.End - afterStart)..];
        }

        return engine with { Before = before, After = after };
    }

    /// <summary>
    ///     The names bound around the fold: by an enclosing LET (bound above the fold) or LAMBDA.
    ///     A step reading one can't be stored as a named LAMBDA: the name can't see them.
    /// </summary>
    internal static HashSet<string> OuterNames(StateEngine engine)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (engine.CallText == null || engine.IsWholeFormula) return names;

        var formula = engine.Before + engine.CallText + engine.After;
        var foldStart = engine.Before.Length;
        var foldEnd = foldStart + engine.CallText.Length;

        FormulaAst ast;
        try
        {
            ast = FormulaParser.Parse(formula);
        }
        catch (FormatException)
        {
            return names;
        }

        foreach (var call in StateStepParser.Walk(ast.Root).OfType<FunctionCallNode>())
        {
            if (call.Start > foldStart || call.End < foldEnd) continue;
            var args = call.Arguments;
            if (StateStepParser.IsNamed(call.Name, "LET"))
            {
                for (var i = 0; i + 1 < args.Count; i += 2)
                    if (args[i] is LeafNode { TokenType: FormulaTokenType.Name } name && name.End <= foldStart)
                        names.Add(name.Text);
            }
            else if (StateStepParser.IsNamed(call.Name, "LAMBDA") && args.Count > 0
                     && args[^1].Start <= foldStart && args[^1].End >= foldEnd)
            {
                foreach (var p in args.Take(args.Count - 1))
                    if (p is LeafNode { TokenType: FormulaTokenType.Name } param)
                        names.Add(param.Text.Trim('[', ']'));
            }
        }

        return names;
    }

    /// <summary>The names from around the fold that the step reads (its own bindings and parameters excepted).</summary>
    internal static IReadOnlyList<string> OuterNamesRead(StateEngine engine)
    {
        var outer = OuterNames(engine);
        if (outer.Count == 0) return Array.Empty<string>();

        outer.ExceptWith(engine.Bindings.Select(b => b.Name));
        outer.ExceptWith(engine.Parameters);
        if (outer.Count == 0) return Array.Empty<string>();

        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in engine.Bindings.Select(b => b.Value).Append(engine.Output))
            read.UnionWith(StateStepParser.NamesIn(text, outer));
        return read.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string List(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}
