using System.Text;
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

        var debug = StateDebug.TryUnwrap(before, after, out var b, out var a);
        if (ReadShape(b, a, debug) is { } shape) return shape.Lines.Select(l => l.Key).ToList();
        return TryUnwrap(b, a, out _, out _, out var keys) ? keys : Array.Empty<string>();
    }

    /// <summary>True when the answer is written as LET names (spec 0018 addendum, #480).</summary>
    public static bool IsLetNames(string before, string after)
    {
        if (before is null) throw new ArgumentNullException(nameof(before));
        if (after is null) throw new ArgumentNullException(nameof(after));

        var debug = StateDebug.TryUnwrap(before, after, out var b, out var a);
        return ReadShape(b, a, debug) != null;
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
    ///     The text around a fold with the answer set to <paramref name="keys" /> (none: no answer).
    ///     <paramref name="asLet" /> picks the form: LET names (true), a <c>GetVars</c> wrapper (false),
    ///     or as the text has it now (null). A debug wrapper stays innermost, exactly as it was written.
    ///     Throws <see cref="InvalidOperationException" /> with the reason when the text can't take the form.
    /// </summary>
    public static (string Before, string After) Set(
        string before, string after, string prefix, IReadOnlyList<string> keys, StateAnswerMade made, bool? asLet = null)
    {
        if (made is null) throw new ArgumentNullException(nameof(made));
        if (before is null) throw new ArgumentNullException(nameof(before));
        if (after is null) throw new ArgumentNullException(nameof(after));
        if (keys is null) throw new ArgumentNullException(nameof(keys));

        var debug = StateDebug.TryUnwrap(before, after, out var b, out var a);
        var debugOpen = debug ? before[b.Length..] : "";
        var debugClose = debug ? after[..(after.Length - a.Length)] : "";

        var shape = ReadShape(b, a, debug);
        var wantLet = asLet ?? shape != null;

        if (shape != null)
        {
            // Work on the plain form: the debug tail goes back on at the end.
            if (shape.DebugTail)
            {
                (b, a) = DropDebugTail(b, a, shape);
                shape = ReadShape(b, a, false)!;
            }

            if (wantLet && keys.Count > 0)
            {
                (b, a) = Rewrite(b, a, shape, prefix, keys);
            }
            else
            {
                if (!IsDefaultTail(shape))
                    throw new InvalidOperationException(keys.Count == 0
                        ? "The last line is your own, so the answer names can't come off. Take the last line out first."
                        : "The last line is your own, and GetVars would lose it. Take it out first, or keep LET names.");
                var (plainB, plainA) = Collapse(b, a, shape);
                if (made.Contains(plainB, plainA))
                {
                    (b, a) = (plainB, plainA);
                }
                else if (keys.Count == 0)
                {
                    // The fold's line is Tim's: only the answer lines and the default last line come off.
                    (b, a) = KeepFold(b, a, shape);
                }
                else
                {
                    throw new InvalidOperationException(
                        "The fold's line is your own, and GetVars would take it away. Keep LET names.");
                }
            }
        }
        else if (TryUnwrap(b, a, out var bareBefore, out var bareAfter, out _))
        {
            (b, a) = (bareBefore, bareAfter);
        }

        if (shape == null || !(wantLet && keys.Count > 0))
        {
            if (keys.Count > 0)
            {
                if (wantLet) (b, a) = AddLet(b, a, prefix, keys, made);
                else (b, a) = (b + prefix + WrapperName + "(", ", " + ArrayText(keys) + ")" + a);
            }
        }

        var result = (Before: b + debugOpen, After: debugClose + a);
        if (debug && wantLet && keys.Count > 0)
            result = DebugAnswer(result.Before, result.After, true);
        return result;
    }

    /// <summary>
    ///     The Debug on or off done to the LET names' last line (spec 0018 addendum). With the debug
    ///     wrapper on, the cell has to show the whole debug table, which sits in the fold's name: the
    ///     last line <c>X</c> becomes <c>_answer, X, fold</c>. Off takes it out again, character for
    ///     character. The text must still have the debug wrapper; it is returned with it. Text that
    ///     isn't LET names comes back as it was.
    /// </summary>
    public static (string Before, string After) DebugAnswer(string before, string after, bool on)
    {
        if (!StateDebug.TryUnwrap(before, after, out var b, out var a)) return (before, after);
        var open = before[b.Length..];
        var close = after[..(after.Length - a.Length)];

        var shape = ReadShape(b, a, true);
        if (shape == null || shape.DebugTail == on) return (before, after);

        (b, a) = on ? AddDebugTail(b, a, shape) : DropDebugTail(b, a, shape);
        return (b + open, close + a);
    }

    private const string AnswerName = "_answer";

    // _answer, or _answer_ and so on when the LET already binds the shorter one.
    private static bool IsMarker(string name) =>
        name.StartsWith(AnswerName, StringComparison.OrdinalIgnoreCase) && name[AnswerName.Length..].All(c => c == '_');

    private const string FoldWord = "fold";

    private const string NeedsLet =
        "LET names need the fold to be the whole formula or the last line of its LET.";

    // ------------------------------------------------------------ LET names

    private sealed record LetLine(string Name, string Key, int Start, int End);

    /// <summary>
    ///     A fold seen as a line of a LET. <see cref="FoldName" /> null: the fold is the LET's last
    ///     argument (no answer yet). Offsets are in <see cref="F" />, the text with the fold's place
    ///     held by a <c>0</c> (so <c>Offset</c> is where the text after the fold starts).
    /// </summary>
    private sealed class LetShape
    {
        public string F = "";
        public int Offset;
        public FunctionCallNode Let = null!;
        public IReadOnlyList<FormulaNode> Args = Array.Empty<FormulaNode>();
        public int K;
        public string? FoldName;
        public List<LetLine> Lines = new();
        public int TailIndex;
        public bool DebugTail;

        public string TailText =>
            DebugTail
                ? F[Args[TailIndex].Start..Args[^3].Start] + F[Args[^2].Start..Args[^2].End]
                : F[Args[TailIndex].Start..Args[^1].End];
    }

    // The LET that holds the fold as one of its arguments, or null.
    private static LetShape? Find(string before, string after)
    {
        var f = before + "0" + after;
        var at = before.Length;

        FormulaAst ast;
        try
        {
            ast = FormulaParser.Parse(f);
        }
        catch (FormatException)
        {
            return null;
        }

        foreach (var call in StateStepParser.Walk(ast.Root).OfType<FunctionCallNode>())
        {
            if (!StateStepParser.IsNamed(call.Name, "LET")) continue;
            for (var i = 0; i < call.Arguments.Count; i++)
                if (call.Arguments[i] is LeafNode { TokenType: FormulaTokenType.Number } n && n.Start == at && n.End == at + 1)
                    return new LetShape { F = f, Offset = at + 1, Let = call, Args = call.Arguments, K = i };
        }

        return null;
    }

    /// <summary>
    ///     The answer written as LET names: the lines <c>name, GetVar(fold, "key")</c> straight after
    ///     the line <c>fold, &lt;fold&gt;</c>, with something after them (the last line). Null when the
    ///     text isn't that.
    /// </summary>
    private static LetShape? ReadShape(string before, string after, bool debug)
    {
        var s = Find(before, after);
        if (s == null || s.K < 1 || s.K % 2 == 0 || s.Args[s.K - 1] is not LeafNode { TokenType: FormulaTokenType.Name } name)
            return null;

        s.FoldName = name.Text;
        var i = s.K + 1;
        while (i + 1 <= s.Args.Count - 2
               && s.Args[i] is LeafNode { TokenType: FormulaTokenType.Name } line
               && !(debug && IsMarker(line.Text))
               && s.Args[i + 1] is FunctionCallNode read && StateStepParser.IsNamed(read.Name, "GetVar")
               && read.Arguments.Count == 2
               && read.Arguments[0] is LeafNode { TokenType: FormulaTokenType.Name } source
               && string.Equals(source.Text, s.FoldName, StringComparison.OrdinalIgnoreCase)
               && read.Arguments[1] is LeafNode { TokenType: FormulaTokenType.String } key)
        {
            s.Lines.Add(new LetLine(line.Text, key.Text[1..^1].Replace("\"\"", "\""), line.Start, read.End));
            i += 2;
        }

        if (s.Lines.Count == 0) return null;
        s.TailIndex = i;

        var count = s.Args.Count;
        if (debug && count - i >= 3
            && s.Args[^1] is LeafNode { TokenType: FormulaTokenType.Name } last
            && string.Equals(last.Text, s.FoldName, StringComparison.OrdinalIgnoreCase)
            && s.Args[^3] is LeafNode { TokenType: FormulaTokenType.Name } marker
            && IsMarker(marker.Text))
            s.DebugTail = true;
        return s;
    }

    private static string Squash(string text) => Regex.Replace(text, @"\s+", "");

    private static string DefaultTail(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : "HSTACK(" + string.Join(", ", names) + ")";

    private static bool IsDefaultTail(LetShape s) =>
        Squash(s.TailText) == Squash(DefaultTail(s.Lines.Select(l => l.Name).ToList()));

    // The names a piece of formula text reads (strings left out), as written.
    private static HashSet<string> WordsIn(string text)
    {
        var bare = Regex.Replace(text, "\"(?:[^\"]|\"\")*\"", "\"\"");
        return new HashSet<string>(
            Regex.Matches(bare, @"(?<![A-Za-z0-9_.])[A-Za-z_\\][A-Za-z0-9_.]*").Cast<Match>().Select(m => m.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    // The key as a LET name: itself when it is one and free, else with _ added (bad characters become _).
    private static string NameFor(string key, HashSet<string> taken)
    {
        var name = key;
        if (!ExcelNameValidator.Validate(name).IsValid)
        {
            name = Regex.Replace(name, @"[^A-Za-z0-9_.]", "_");
            if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_')) name = "_" + name;
        }

        while (taken.Contains(name) || !StateScaffold.IsUsableBinding(name)) name += "_";
        taken.Add(name);
        return name;
    }

    private static string LineText(string name, string prefix, string foldName, string key) =>
        $"{name}, {prefix}GetVar({foldName}, \"{key.Replace("\"", "\"\"")}\")";

    // Names bound by the LET (its even arguments, the last one excepted), but not those of the answer lines.
    private static HashSet<string> BoundNames(LetShape s, bool skipAnswer)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < s.Args.Count; i += 2)
        {
            if (s.Args[i] is not LeafNode { TokenType: FormulaTokenType.Name } n) continue;
            if (skipAnswer && i > s.K && i < s.TailIndex) continue;
            taken.Add(n.Text);
        }
        return taken;
    }

    // The answer set again in a LET that already has one: lines for the keys, in order; the lines
    // that stay keep their text; the last line follows only while it is the default.
    private static (string, string) Rewrite(string b, string a, LetShape s, string prefix, IReadOnlyList<string> keys)
    {
        var tail = s.TailText;
        var oldNames = s.Lines.Select(l => l.Name).ToList();
        var isDefault = IsDefaultTail(s);

        var removed = s.Lines.Where(l => !keys.Contains(l.Key, StringComparer.OrdinalIgnoreCase)).Select(l => l.Name).ToList();
        if (!isDefault)
        {
            var read = WordsIn(tail);
            var gone = removed.Where(read.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (gone.Count > 0)
                throw new InvalidOperationException($"The last line reads {List(gone)}. Take that out first.");
        }

        var taken = BoundNames(s, skipAnswer: true);
        taken.Add(s.FoldName!);
        // Names Tim's own last line reads are taken too: a new line never captures one.
        if (!isDefault) taken.UnionWith(WordsIn(tail));

        var lines = new List<(string Name, string Text)>();
        foreach (var key in keys)
        {
            var old = s.Lines.FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));
            if (old != null)
            {
                taken.Add(old.Name);
                lines.Add((old.Name, s.F[old.Start..old.End]));
            }
            else
            {
                var name = NameFor(key, taken);
                lines.Add((name, LineText(name, prefix, s.FoldName!, key)));
            }
        }

        var gap = s.F[s.Args[s.K].End..s.Args[s.K + 1].Start];
        var newTail = isDefault ? DefaultTail(lines.Select(l => l.Name).ToList()) : tail;
        var region = string.Concat(lines.Select(l => gap + l.Text)) + gap + newTail;

        var tailEnd = s.Args[^1].End;
        if (s.DebugTail) throw new InvalidOperationException("Internal: debug tail not dropped.");
        return (b, region + a[(tailEnd - s.Offset)..]);
    }

    // The fold back where it was: a LET made for the answer is taken off, Tim's own keeps its lines.
    private static (string, string) Collapse(string b, string a, LetShape s)
    {
        // A LET left with no bindings is taken off, whatever surrounds it.
        var letStart = s.Let.Start;
        if (s.K == 1)
            return (s.F[..letStart], s.F[s.Let.End..]);

        var nameStart = s.Args[s.K - 1].Start;
        return (b[..nameStart], a[(s.Args[^1].End - s.Offset)..]);
    }

    // Tim's own fold line stays: the answer lines and the default last line go, and the fold's name
    // is the last line.
    private static (string, string) KeepFold(string b, string a, LetShape s)
    {
        var gap = s.F[s.Args[s.K].End..s.Args[s.K + 1].Start];
        return (b, gap + s.FoldName + a[(s.Args[^1].End - s.Offset)..]);
    }

    // The answer as LET names where there is none: a new LET round a whole-formula fold, or the
    // fold's own LET when the fold is its last line.
    private static (string, string) AddLet(string b, string a, string prefix, IReadOnlyList<string> keys, StateAnswerMade made)
    {
        var s = Find(b, a);

        // Tim's own `fold, <fold>, fold)`: the lines go in after his fold line and the last line `fold`
        // becomes the default one. Nothing is recorded: the fold line is his.
        if (s != null && s.K >= 1 && s.K % 2 == 1 && s.K == s.Args.Count - 2
            && s.Args[s.K - 1] is LeafNode { TokenType: FormulaTokenType.Name } own
            && s.Args[s.K + 1] is LeafNode { TokenType: FormulaTokenType.Name } last
            && string.Equals(own.Text, last.Text, StringComparison.OrdinalIgnoreCase))
        {
            var used = BoundNames(s, skipAnswer: false);
            var ownGap = s.F[s.Args[s.K].End..last.Start];
            var ownNames = new List<string>();
            var ownSb = new StringBuilder(ownGap);
            foreach (var key in keys)
            {
                var name = NameFor(key, used);
                ownNames.Add(name);
                ownSb.Append(LineText(name, prefix, own.Text, key)).Append(ownGap);
            }

            ownSb.Append(DefaultTail(ownNames));
            return (b, ownSb + a[(last.End - s.Offset)..]);
        }

        var reuse = s != null && s.K == s.Args.Count - 1 && s.K % 2 == 0 && s.K >= 2;
        var whole = b.Trim() == "=" && a.Trim().Length == 0;
        if (!reuse && !whole) throw new InvalidOperationException(NeedsLet);

        HashSet<string> taken;
        string sep;
        made.Record(b, a);
        if (reuse)
        {
            taken = BoundNames(s!, skipAnswer: false);
            var nl = b.LastIndexOf('\n');
            sep = nl >= 0 && b[(nl + 1)..].Trim().Length == 0 ? "\n" + b[(nl + 1)..] : " ";
        }
        else
        {
            taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            sep = "\n    ";
        }

        var foldName = FoldWord;
        while (taken.Contains(foldName)) foldName += "_";
        taken.Add(foldName);

        var names = new List<string>();
        var sb = new StringBuilder();
        foreach (var key in keys)
        {
            var name = NameFor(key, taken);
            names.Add(name);
            sb.Append(',').Append(sep).Append(LineText(name, prefix, foldName, key));
        }

        sb.Append(',').Append(sep).Append(DefaultTail(names));

        return reuse
            ? (b + foldName + ", ", sb + a)
            : ("=LET(" + sep + foldName + ", ", sb + "\n)");
    }

    // _answer, X, fold in place of the last line X (the debug table shows, X stays one line above).
    private static (string, string) AddDebugTail(string b, string a, LetShape s)
    {
        var x = s.Args[^1];
        var gap = s.F[s.Args[^2].End..x.Start];
        var taken = BoundNames(s, skipAnswer: false);
        var marker = AnswerName;
        while (taken.Contains(marker)) marker += "_";
        var text = marker + ", " + s.F[x.Start..x.End] + gap + s.FoldName;
        return (b, a[..(x.Start - s.Offset)] + text + a[(x.End - s.Offset)..]);
    }

    private static (string, string) DropDebugTail(string b, string a, LetShape s)
    {
        var marker = s.Args[^3].Start - s.Offset;
        var x = s.Args[^2];
        return (b, a[..marker] + s.F[x.Start..x.End] + a[(s.Args[^1].End - s.Offset)..]);
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
