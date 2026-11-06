using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>Which carrier a state step uses: a SetVars text state, or an HSTACK/VSTACK array.</summary>
public enum StateFamily
{
    SetVars,
    Array
}

/// <summary>
///     One value a state step carries. <paramref name="Key" /> is the state key (for the array
///     family, the 1-based position as text). <paramref name="GetVarLine" /> is the canonical
///     binding whose whole RHS reads the key, or null when the key is only read inline or not at
///     all. <paramref name="NextLine" /> is the binding passed as the key's value in the output,
///     or null when the output holds an expression there or doesn't set the key.
/// </summary>
public record StateVariable(string Key, LetBinding? GetVarLine, LetBinding? NextLine)
{
    public string? BindingName => GetVarLine?.Name;
}

/// <summary>
///     A <c>LET</c> recognised as a state step (spec 0011, "Terms"). <paramref name="InputBinding" />
///     is null when no binding qualifies as the sample input.
/// </summary>
public record StateStep(
    StateFamily Family,
    LetBinding StateBinding,
    LetBinding? InputBinding,
    IReadOnlyList<StateVariable> Variables,
    string Output);

/// <summary>
///     Recognises state steps from formula text alone (spec 0011, "Shared engine"). Part 3 reads
///     the shape; parts 1, 2 and 4 are meant to share this recogniser. It never touches Excel.
/// </summary>
public static class StateStepParser
{
    private static readonly Regex SingleCell = new(
        @"^(?:(?:'[^']+'|[A-Za-z0-9_.]+)!)?\$?[A-Za-z]{1,3}\$?[0-9]+$");

    /// <summary>Returns the state step <paramref name="parsed" /> is, or null if it isn't one.</summary>
    public static StateStep? TryParse(ParsedLet parsed)
    {
        if (parsed is null) throw new ArgumentNullException(nameof(parsed));
        try
        {
            return Recognise(parsed);
        }
        catch (FormatException)
        {
            // FormulaParser couldn't read part of the LET, so nothing can be claimed about it.
            return null;
        }
    }

    /// <summary>
    ///     The bindings the LET to LAMBDA dialog lists for a step, in order: the state binding,
    ///     the input binding, then every other value binding in LET order.
    /// </summary>
    public static IReadOnlyList<LetBinding> DialogBindings(ParsedLet parsed, StateStep step)
    {
        var rows = new List<LetBinding> { step.StateBinding };
        if (step.InputBinding != null) rows.Add(step.InputBinding);
        rows.AddRange(parsed.Bindings.Where(b => !b.IsCalculation && !rows.Contains(b)));
        return rows;
    }

    /// <summary>True when <paramref name="callName" /> is <paramref name="name" />, bare or <c>prefix.name</c>.</summary>
    internal static bool IsNamed(string callName, string name)
    {
        var dot = callName.LastIndexOf('.');
        return string.Equals(dot < 0 ? callName : callName[(dot + 1)..], name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The state's starting value can be lifted into a seed only when it's written as a state-building call.</summary>
    internal static bool IsStateBuildingCall(string rhs)
    {
        try
        {
            return FormulaParser.Parse(rhs).Root is FunctionCallNode call
                && (IsNamed(call.Name, "SetVars") || IsNamed(call.Name, "SetVar")
                    || IsNamed(call.Name, "HSTACK") || IsNamed(call.Name, "VSTACK"));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Each binding's direct references to other bindings of the same LET, keyed
    ///     case-insensitively. Inner LET or LAMBDA names that shadow a binding count as reads,
    ///     which only ever makes a caller keep more.
    /// </summary>
    /// <exception cref="FormatException">A binding's RHS can't be parsed.</exception>
    internal static Dictionary<string, HashSet<string>> DirectReads(ParsedLet parsed, bool ignoreInnerNames = false)
    {
        var names = parsed.Bindings.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reads = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in parsed.Bindings)
        {
            reads[b.Name] = NamesIn(b.RhsText, names);
            if (ignoreInnerNames)
                reads[b.Name].ExceptWith(InnerNames(FormulaParser.Parse(b.RhsText).Root));
        }
        return reads;
    }

    /// <summary>The names from <paramref name="candidates" /> that <paramref name="expression" /> mentions.</summary>
    /// <exception cref="FormatException"><paramref name="expression" /> can't be parsed.</exception>
    internal static HashSet<string> NamesIn(string expression, HashSet<string> candidates)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in Walk(FormulaParser.Parse(expression).Root))
        {
            var text = node switch
            {
                LeafNode { TokenType: FormulaTokenType.Name } leaf => leaf.Text,
                // A binding can hold a LAMBDA, so a call can read a binding too.
                FunctionCallNode call => call.Name,
                _ => null
            };
            if (text != null && candidates.Contains(text)) found.Add(text);
        }
        return found;
    }

    /// <summary>
    ///     The names <paramref name="root" /> binds for itself, in inner <c>LET</c> and
    ///     <c>LAMBDA</c> calls. Coarse: a name counts throughout the expression, not just in
    ///     its scope, so callers use this only where a miss costs less than a false read.
    /// </summary>
    internal static HashSet<string> InnerNames(FormulaNode root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in Walk(root).OfType<FunctionCallNode>())
        {
            var args = call.Arguments;
            if (IsNamed(call.Name, "LET"))
            {
                for (var i = 0; i + 1 < args.Count; i += 2)
                    if (NameLeaf(args[i]) is { } name) names.Add(name);
            }
            else if (IsNamed(call.Name, "LAMBDA"))
            {
                foreach (var arg in args.Take(args.Count - 1))
                    if (NameLeaf(arg) is { } name) names.Add(name.Trim('[', ']'));
            }
        }
        return names;
    }

    /// <summary>Everything reachable from <paramref name="start" /> through <paramref name="reads" />, start included.</summary>
    internal static HashSet<string> Closure(IEnumerable<string> start, Dictionary<string, HashSet<string>> reads)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(start);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name) || !reads.TryGetValue(name, out var next)) continue;
            foreach (var n in next) queue.Enqueue(n);
        }
        return seen;
    }

    private static StateStep? Recognise(ParsedLet parsed)
    {
        if (FormulaParser.Parse(parsed.Body).Root is not FunctionCallNode output) return null;

        StateFamily family;
        if (IsNamed(output.Name, "SetVars") || IsNamed(output.Name, "SetVar"))
            family = StateFamily.SetVars;
        else if (IsNamed(output.Name, "HSTACK") || IsNamed(output.Name, "VSTACK"))
            family = StateFamily.Array;
        else
            return null;

        if (family == StateFamily.Array && output.Arguments.Count < 2) return null;

        var bindings = parsed.Bindings;
        var roots = bindings.Select(b => FormulaParser.Parse(b.RhsText).Root).ToList();
        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < bindings.Count; i++) indexOf[bindings[i].Name] = i;

        var reads = new List<StateRead>();
        for (var line = 0; line < bindings.Count; line++)
        {
            foreach (var call in Walk(roots[line]).OfType<FunctionCallNode>())
            {
                var read = family == StateFamily.SetVars
                    ? AsGetVar(call)
                    : AsPositionalRead(call, output.Arguments.Count);
                if (read is not { } r) continue;
                if (!indexOf.TryGetValue(r.State, out var source) || source == line) continue;
                reads.Add(new StateRead(line, source, r.Key, ReferenceEquals(call, roots[line])));
            }
        }

        if (reads.Count == 0) return null;

        // Most-read binding wins; ties go to the earliest.
        var state = reads
            .GroupBy(r => r.Source)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .First()
            .Key;
        var stateReads = reads.Where(r => r.Source == state).ToList();

        var pairs = family == StateFamily.SetVars
            ? ReadOutputPairs(output, bindings[state].Name)
            : output.Arguments
                .Select((arg, i) => (Key: (i + 1).ToString(CultureInfo.InvariantCulture), Value: arg.ToFormula().Trim()))
                .ToList();
        if (pairs == null) return null;

        var nextLineNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var variables = BuildVariables(parsed, stateReads, pairs, indexOf, nextLineNames);

        var getVarLines = stateReads.Where(r => r.WholeRhs).Select(r => bindings[r.Line].Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var input = FindInput(
            parsed, bindings[state], getVarLines, nextLineNames, pairs, roots,
            allowStoredOnly: family == StateFamily.SetVars);

        // INDEX plus HSTACK is everyday Excel. Without an input its rules use, it's a lookup.
        if (family == StateFamily.Array && input == null) return null;

        return new StateStep(family, bindings[state], input, variables, parsed.Body);
    }

    private static List<StateVariable> BuildVariables(
        ParsedLet parsed,
        List<StateRead> reads,
        List<(string Key, string Value)> pairs,
        Dictionary<string, int> indexOf,
        HashSet<string> nextLineNames)
    {
        // A key set twice in a SetVar chain ends up with the last value.
        var valueByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs) valueByKey[key] = value;

        LetBinding? NextLine(string key)
        {
            if (!valueByKey.TryGetValue(key, out var value)
                || !indexOf.TryGetValue(value, out var line)) return null;
            nextLineNames.Add(parsed.Bindings[line].Name);
            return parsed.Bindings[line];
        }

        var variables = new List<StateVariable>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in reads.Select(r => r.Key).Concat(pairs.Select(p => p.Key)))
        {
            if (!seen.Add(key)) continue;
            var canonical = reads.FirstOrDefault(r =>
                r.WholeRhs && string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
            variables.Add(new StateVariable(
                key, canonical == null ? null : parsed.Bindings[canonical.Line], NextLine(key)));
        }
        return variables;
    }

    private static LetBinding? FindInput(
        ParsedLet parsed,
        LetBinding state,
        HashSet<string> getVarLines,
        HashSet<string> nextLines,
        List<(string Key, string Value)> pairs,
        List<FormulaNode> roots,
        bool allowStoredOnly)
    {
        var reads = DirectReads(parsed, ignoreInnerNames: true);
        var names = parsed.Bindings.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Whatever reads the state, directly or through other lines, can't be a sample input.
        bool Excluded(LetBinding b) =>
            getVarLines.Contains(b.Name) || Closure(new[] { b.Name }, reads).Contains(state.Name);

        var named = parsed.Bindings.FirstOrDefault(b =>
            string.Equals(b.Name, "v", StringComparison.OrdinalIgnoreCase) && !Excluded(b));
        if (named != null) return named;

        // What the step's rules use: read by a next line or in an expression in the output.
        // A binding passed straight to the output is only stored, which is weaker evidence.
        var ruleReads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in nextLines) ruleReads.UnionWith(reads[line]);
        foreach (var (_, value) in pairs)
            if (!names.Contains(value)) ruleReads.UnionWith(NamesIn(value, names));
        var usedByRules = Closure(ruleReads, reads);

        var stored = Closure(nextLines, reads);
        var candidates = new List<(LetBinding Binding, FormulaNode Root)>();
        for (var i = 0; i < parsed.Bindings.Count; i++)
        {
            var b = parsed.Bindings[i];
            if (!Excluded(b) && (usedByRules.Contains(b.Name) || stored.Contains(b.Name)))
                candidates.Add((b, roots[i]));
        }

        var rules = candidates.Where(c => usedByRules.Contains(c.Binding.Name)).ToList();

        // A next line stays a candidate: a step can store its input ("last", inp).
        var input = BestSample(rules)
            ?? rules.FirstOrDefault(c => !nextLines.Contains(c.Binding.Name)).Binding;
        if (input != null || !allowStoredOnly) return input;
        return BestSample(candidates);
    }

    private static LetBinding? BestSample(List<(LetBinding Binding, FormulaNode Root)> candidates)
    {
        bool LooksLikeInput((LetBinding Binding, FormulaNode Root) c) =>
            SingleCell.IsMatch(c.Binding.RhsText) || (c.Root is FunctionCallNode call && IsNamed(call.Name, "INDEX"));

        // A $-anchored cell reads as a constant (maxB, $K$2), so a relative one goes first.
        var sample = candidates.FirstOrDefault(c => LooksLikeInput(c) && !c.Binding.RhsText.Contains('$'));
        return sample.Binding ?? candidates.FirstOrDefault(LooksLikeInput).Binding;
    }

    /// <summary>
    ///     The key/value pairs of a SetVars output: a <c>SetVars</c> call, a <c>SetVar</c> chain
    ///     rooted at the state, or a <c>SetVar</c> chain on a <c>SetVars</c> call (how a step
    ///     past SetVars' ten pairs is written). Null when it is none of those.
    /// </summary>
    private static List<(string Key, string Value)>? ReadOutputPairs(FunctionCallNode call, string stateName)
    {
        if (IsNamed(call.Name, "SetVars"))
        {
            var args = call.Arguments;
            if (args.Count < 2 || args.Count % 2 != 0) return null;
            var pairs = new List<(string, string)>();
            for (var i = 0; i < args.Count; i += 2)
            {
                if (StringLiteral(args[i]) is not { } key || args[i + 1] is EmptyArgNode) return null;
                pairs.Add((key, args[i + 1].ToFormula().Trim()));
            }
            return pairs;
        }

        if (!IsNamed(call.Name, "SetVar") || call.Arguments.Count != 3) return null;
        if (StringLiteral(call.Arguments[1]) is not { } chainKey || call.Arguments[2] is EmptyArgNode) return null;

        var inner = call.Arguments[0] switch
        {
            LeafNode { TokenType: FormulaTokenType.Name } leaf
                when string.Equals(leaf.Text, stateName, StringComparison.OrdinalIgnoreCase)
                => new List<(string, string)>(),
            FunctionCallNode nested => ReadOutputPairs(nested, stateName),
            _ => null
        };
        inner?.Add((chainKey, call.Arguments[2].ToFormula().Trim()));
        return inner;
    }

    private static (string State, string Key)? AsGetVar(FunctionCallNode call)
    {
        if (!IsNamed(call.Name, "GetVar") || call.Arguments.Count != 2) return null;
        return NameLeaf(call.Arguments[0]) is { } state && StringLiteral(call.Arguments[1]) is { } key
            ? (state, key)
            : null;
    }

    /// <summary>
    ///     <c>INDEX(S, k)</c>, <c>NTH(S, k)</c> or <c>CHOOSECOLS(S, k)</c>, plus the two-index
    ///     forms of a row or column state (<c>INDEX(S, 1, k)</c>, <c>INDEX(S, , k)</c>,
    ///     <c>INDEX(S, k, 1)</c>). A position past the output's size isn't a read of this state.
    /// </summary>
    private static (string State, string Key)? AsPositionalRead(FunctionCallNode call, int outputSize)
    {
        var args = call.Arguments;
        FormulaNode position;
        if (args.Count == 2
            && (IsNamed(call.Name, "INDEX") || IsNamed(call.Name, "NTH") || IsNamed(call.Name, "CHOOSECOLS")))
        {
            position = args[1];
        }
        else if (args.Count == 3 && IsNamed(call.Name, "INDEX"))
        {
            if (IsUnitOrEmpty(args[1])) position = args[2];
            else if (IsUnitOrEmpty(args[2])) position = args[1];
            else return null;
        }
        else
        {
            return null;
        }

        if (NameLeaf(args[0]) is not { } state
            || position is not LeafNode { TokenType: FormulaTokenType.Number } number
            || !int.TryParse(number.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var k)
            || k < 1 || k > outputSize)
            return null;

        return (state, k.ToString(CultureInfo.InvariantCulture));
    }

    private static bool IsUnitOrEmpty(FormulaNode node) =>
        node is EmptyArgNode || node is LeafNode { TokenType: FormulaTokenType.Number, Text: "1" };

    private static string? NameLeaf(FormulaNode node) =>
        node is LeafNode { TokenType: FormulaTokenType.Name } leaf ? leaf.Text : null;

    private static string? StringLiteral(FormulaNode node) =>
        node is LeafNode { TokenType: FormulaTokenType.String } leaf
            ? leaf.Text[1..^1].Replace("\"\"", "\"")
            : null;

    internal static IEnumerable<FormulaNode> Walk(FormulaNode node)
    {
        yield return node;

        IEnumerable<FormulaNode> children = node switch
        {
            FunctionCallNode call => call.Arguments,
            BinaryNode binary => new[] { binary.Left, binary.Right },
            UnaryNode unary => new[] { unary.Operand },
            PostfixNode postfix => new[] { postfix.Operand },
            ParenNode paren => paren.Items,
            _ => Array.Empty<FormulaNode>()
        };

        foreach (var child in children)
            foreach (var descendant in Walk(child))
                yield return descendant;
    }

    private sealed record StateRead(int Line, int Source, string Key, bool WholeRhs);
}

/// <summary>
///     The fold line of the LET to LAMBDA dialog: <c>=REDUCE(seed, inputs, step)</c> (spec 0011,
///     part 3). Tim edits it as free text, so everything here reads the text back.
/// </summary>
public static class StateFold
{
    private static readonly Regex CellAddress = new(@"^\$?[A-Za-z]{1,3}\$?[1-9][0-9]*$");
    private static readonly Regex CellRef = new(@"^(.*!)?\$?([A-Za-z]{1,3})\$?([0-9]+)$");
    private static readonly Regex LineBreak = new(@"\s*\r?\n\s*");

    /// <summary>
    ///     The seed for a step: its state binding's RHS when that builds a state, with other
    ///     bindings of the LET written in place (a cell can't see them) and cell references made
    ///     absolute. Empty when the state binding is a cell or anything else.
    /// </summary>
    public static string Seed(ParsedLet parsed, StateStep step)
    {
        if (parsed is null) throw new ArgumentNullException(nameof(parsed));
        if (step is null) throw new ArgumentNullException(nameof(step));
        if (!StateStepParser.IsStateBuildingCall(step.StateBinding.RhsText)) return "";

        var others = parsed.Bindings
            .Where(b => !string.Equals(b.Name, step.StateBinding.Name, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(b => b.Name, b => b.RhsText, StringComparer.OrdinalIgnoreCase);

        return Render(step.StateBinding.RhsText, others);
    }

    /// <summary>
    ///     Rewrites the leaves of <paramref name="expression" />: a binding becomes its RHS, a
    ///     cell becomes absolute, everything else keeps its text. Working on leaves leaves
    ///     strings, sheet names, table columns and function names like LOG10 alone. Whitespace
    ///     between tokens collapses, so the seed fits the fold's single line.
    /// </summary>
    private static string Render(string expression, Dictionary<string, string> bindings)
    {
        var root = FormulaParser.Parse(expression).Root;
        var inner = StateStepParser.InnerNames(root);
        var nodes = StateStepParser.Walk(root).ToList();

        // A call's name is a token too: a binding can hold a LAMBDA.
        var tokens = nodes.OfType<LeafNode>().Select(l => (l.Token, IsCall: false))
            .Concat(nodes.OfType<FunctionCallNode>().Select(c => (Token: c.NameToken, IsCall: true)))
            .OrderBy(t => t.Token.Start);

        var sb = new StringBuilder();
        var at = 0;
        foreach (var (token, isCall) in tokens)
        {
            sb.Append(CollapseLines(expression[at..token.Start]));
            at = token.End;

            if (token.Type != FormulaTokenType.Name || inner.Contains(token.Text))
                sb.Append(token.Text);
            else if (bindings.TryGetValue(token.Text, out var rhs))
                sb.Append(InPlace(rhs, Without(bindings, token.Text)));
            else
                sb.Append(isCall ? token.Text : CellRef.Replace(token.Text, "$1$$$2$$$3"));
        }

        sb.Append(CollapseLines(expression[at..]));
        return sb.ToString().Trim();
    }

    private static Dictionary<string, string> Without(Dictionary<string, string> bindings, string name) =>
        bindings.Where(b => !string.Equals(b.Key, name, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(b => b.Key, b => b.Value, StringComparer.OrdinalIgnoreCase);

    private static string InPlace(string rhs, Dictionary<string, string> bindings)
    {
        var text = Render(rhs, bindings);
        return FormulaParser.Parse(rhs).Root is LeafNode or FunctionCallNode ? text : $"({text})";
    }

    private static string CollapseLines(string gap) => LineBreak.Replace(gap, " ");

    /// <summary>The fold text for a seed and a step name, with the inputs slot empty.</summary>
    public static string Build(string seed, string lambdaName) => $"=REDUCE({seed}, , {lambdaName})";

    /// <summary>Where the caret goes in <paramref name="fold" />: the empty inputs slot, or -1 when it's filled.</summary>
    public static int InputsCaret(string fold) =>
        FindFold(fold) is { } call && call.Arguments[1] is EmptyArgNode slot ? slot.Position : -1;

    /// <summary>
    ///     Moves the fold's step from <paramref name="oldName" /> to <paramref name="newName" />,
    ///     leaving the rest of Tim's edits alone. A fold whose step is anything else stays as it is.
    /// </summary>
    public static string WithLambdaName(string fold, string oldName, string newName)
    {
        var call = Folds(fold).FirstOrDefault(c => c.Arguments[2] switch
        {
            EmptyArgNode => true,
            LeafNode { TokenType: FormulaTokenType.Name } leaf =>
                string.Equals(leaf.Text, oldName, StringComparison.OrdinalIgnoreCase),
            _ => false
        });
        if (call == null) return fold;

        var step = call.Arguments[2];
        return fold[..step.Start] + newName + fold[step.End..];
    }

    /// <summary>True when the text is a formula whose fold has a seed, inputs and a step.</summary>
    public static bool IsComplete(string fold) =>
        fold.TrimStart().StartsWith("=", StringComparison.Ordinal)
        && FindFold(fold) is { } call
        && call.Arguments.All(a => a is not EmptyArgNode);

    public static bool IsCellAddress(string text) => CellAddress.IsMatch(text.Trim());

    private static FunctionCallNode? FindFold(string fold) => Folds(fold).FirstOrDefault();

    // The fold can be wrapped (=TEXTJOIN(" ",,REDUCE(…))), so look past the outermost call.
    private static IEnumerable<FunctionCallNode> Folds(string fold)
    {
        FormulaNode root;
        try
        {
            root = FormulaParser.Parse(fold).Root;
        }
        catch (FormatException)
        {
            return Array.Empty<FunctionCallNode>();
        }

        return StateStepParser.Walk(root).OfType<FunctionCallNode>().Where(IsFold).ToList();
    }

    private static bool IsFold(FunctionCallNode call) =>
        call.Arguments.Count == 3
        && (StateStepParser.IsNamed(call.Name, "REDUCE") || StateStepParser.IsNamed(call.Name, "SCAN"));
}
