using System.Text;
using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>One name and initial value from the <c>/State</c> dialog's State field.</summary>
public record StatePair(string Key, string Value);

/// <summary>
///     One state variable as the scaffold writes it: <paramref name="Key" /> is the state key,
///     <paramref name="Binding" /> its GetVar line and <paramref name="Next" /> its next line.
/// </summary>
public record ScaffoldVariable(string Key, string Binding, string Next, string Value);

/// <summary>What the State field reads as: the pairs, or why it can't be read.</summary>
public record ParsedState(IReadOnlyList<StatePair> Pairs, string? Error);

/// <summary>The scaffold's formula, or why there isn't one.</summary>
public record ScaffoldResult(string? Formula, string? Error);

/// <summary>
///     Writes a new state step for <c>/State</c> (spec 0011, part 1). Pure text: the command
///     reads the selection and the names, and this turns them into the formula.
/// </summary>
public static class StateScaffold
{
    /// <summary>The Loops library's folder, and the names to load when none are in the workbook.</summary>
    public const string LibraryFolder = "loops";

    public static readonly IReadOnlyList<string> LibraryNames = new[] { "SetVars", "SetVar", "GetVar", "NewState" };

    private const int SetVarsPairLimit = 10;
    private const string StateBinding = "a";
    private const string InputBinding = "v";

    private static readonly string[] RequiredNames = { "SetVars", "SetVar", "GetVar" };
    private static readonly Regex Pair = new(@"^([^\s=]+)\s*=?\s*(.*)$", RegexOptions.Singleline);
    private static readonly Regex UsableKey = new(@"^[\p{L}\p{N}_.?]+$");
    private static readonly Regex RowColumnForm = new(@"^R[0-9]*C[0-9]*$", RegexOptions.IgnoreCase);

    /// <summary>
    ///     Reads <c>p 0, b 4</c> or <c>p=0, b=4</c>. Values are formula text, so a comma inside a
    ///     call, array or string stays in its value. Empty segments are ignored, so the preview
    ///     keeps working while a trailing comma is being typed.
    /// </summary>
    public static ParsedState ParseState(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        IReadOnlyList<FormulaToken> tokens;
        try
        {
            tokens = FormulaParser.Tokenize(text);
        }
        catch (FormatException ex)
        {
            return Refuse($"Can't read the state: {ex.Message}");
        }

        var pairs = new List<StatePair>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in SplitAtTopLevelCommas(text, tokens))
        {
            var match = Pair.Match(segment.Trim());
            if (!match.Success) continue;

            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value.Trim();

            if (!UsableKey.IsMatch(key)) return Refuse($"'{key}' can't be a state name.");
            if (value.Length == 0) return Refuse($"{key} needs a value.");
            if (!seen.Add(key)) return Refuse($"{key} is listed twice.");
            if (!Reads(value)) return Refuse($"Can't read the value of {key}: {value}");

            pairs.Add(new StatePair(key, value));
        }

        return new ParsedState(pairs, null);

        static ParsedState Refuse(string error) => new(Array.Empty<StatePair>(), error);
    }

    /// <summary>
    ///     Chooses each variable's binding and next-line names. A name Excel wouldn't take gets
    ///     a <c>_</c> suffix and keeps its key. A next line that clashes takes a number.
    /// </summary>
    public static IReadOnlyList<ScaffoldVariable> PlanVariables(IReadOnlyList<StatePair> pairs)
    {
        if (pairs is null) throw new ArgumentNullException(nameof(pairs));

        // A binding named after a library call would hide it from the lines below.
        var taken = new HashSet<string>(RequiredNames, StringComparer.OrdinalIgnoreCase)
        {
            StateBinding,
            InputBinding
        };

        var bindings = new List<(StatePair Pair, string Base, string Binding)>();
        foreach (var pair in pairs)
        {
            var nameBase = BaseName(pair.Key);
            var binding = IsUsableBinding(nameBase) ? nameBase : nameBase + "_";
            while (!taken.Add(binding)) binding += "_";
            bindings.Add((pair, nameBase, binding));
        }

        var variables = new List<ScaffoldVariable>();
        foreach (var (pair, nameBase, binding) in bindings)
        {
            var next = "next" + char.ToUpperInvariant(nameBase[0]) + nameBase[1..];
            var candidate = next;
            for (var i = 2; !taken.Add(candidate); i++) candidate = next + i;
            variables.Add(new ScaffoldVariable(pair.Key, binding, candidate, pair.Value));
        }

        return variables;
    }

    /// <summary>
    ///     Builds the step. <paramref name="prefix" /> is empty or ends in a dot (<c>loops.</c>).
    ///     A blank input is <c>""</c>.
    /// </summary>
    public static ScaffoldResult Build(string stateText, string inputText, string prefix)
    {
        if (inputText is null) throw new ArgumentNullException(nameof(inputText));

        var state = ParseState(stateText);
        if (state.Error != null) return new ScaffoldResult(null, state.Error);
        if (state.Pairs.Count == 0) return new ScaffoldResult(null, "Type the state: p 0, b 4");

        var input = inputText.Trim().TrimStart('=').TrimStart();
        if (input.Length == 0) input = "\"\"";
        else if (!Reads(input)) return new ScaffoldResult(null, $"Can't read the input: {input}");

        var variables = PlanVariables(state.Pairs);

        var bindings = new List<(string Name, string Value)>
        {
            (StateBinding, StateCall(prefix, variables.Select(v => (v.Key, v.Value)).ToList())),
            (InputBinding, input)
        };
        bindings.AddRange(variables.Select(v => (v.Binding, $"{prefix}GetVar({StateBinding}, {Quoted(v.Key)})")));
        bindings.AddRange(variables.Select(v => (v.Next, v.Binding)));

        var sb = new StringBuilder("=");
        FormulaFormatter.AppendLet(sb, 0, bindings, StateCall(prefix, variables.Select(v => (v.Key, v.Next)).ToList()));
        return new ScaffoldResult(sb.ToString(), null);
    }

    /// <summary>
    ///     The State text for a selected block: names from its top row, references from the row
    ///     below. Null unless every label is text with something usable as a name.
    /// </summary>
    public static string? PrefillState(IReadOnlyList<object?> labels, IReadOnlyList<string> valueRefs)
    {
        if (labels is null) throw new ArgumentNullException(nameof(labels));
        if (valueRefs is null) throw new ArgumentNullException(nameof(valueRefs));
        if (labels.Count == 0 || labels.Count != valueRefs.Count) return null;

        var names = labels.Select(l => LetNameSanitizer.Sanitize(l as string)).ToList();
        return names.Any(n => n == null)
            ? null
            : string.Join(", ", names.Select((n, i) => $"{n} {valueRefs[i]}"));
    }

    /// <summary>
    ///     How the workbook spells the library: <c>""</c> for bare names, <c>loops.</c> for
    ///     prefixed ones, or null when <c>SetVars</c>, <c>SetVar</c> and <c>GetVar</c> aren't all
    ///     loaded as LAMBDAs. Bare names are preferred. <paramref name="names" /> maps each
    ///     workbook name to its <c>RefersTo</c>.
    /// </summary>
    public static string? FindPrefix(IReadOnlyDictionary<string, string> names)
    {
        if (names is null) throw new ArgumentNullException(nameof(names));

        bool Loaded(string prefix) => RequiredNames.All(n =>
            names.TryGetValue(prefix + n, out var refersTo)
            && refersTo.TrimStart().StartsWith("=LAMBDA(", StringComparison.OrdinalIgnoreCase));

        if (Loaded("")) return "";

        const string suffix = ".SetVars";
        return names.Keys
            .Where(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && !n.Contains('!'))
            .Select(n => n[..(n.Length - suffix.Length + 1)])
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault(Loaded);
    }

    /// <summary>The A1 address of a 1-based row and column.</summary>
    public static string Address(int row, int column)
    {
        var letters = "";
        for (var c = column; c > 0; c = (c - 1) / 26)
            letters = (char)('A' + (c - 1) % 26) + letters;
        return letters + row;
    }

    /// <summary>A cell address as text to compare: no <c>$</c>, upper case.</summary>
    public static string NormalAddress(string address) =>
        address.Trim().Replace("$", "").ToUpperInvariant();

    private static bool IsUsableBinding(string name) =>
        ExcelNameValidator.Validate(name).IsValid && !RowColumnForm.IsMatch(name);

    /// <summary>
    ///     The name a key's lines are built from. <c>c</c>, <c>b4</c> and <c>RC</c> only read as
    ///     references, which a suffix cures, so they stay. Anything else Excel wouldn't take goes
    ///     through the sanitiser, including a dot: fine in a defined name, refused in a LET name.
    /// </summary>
    private static string BaseName(string key) =>
        ExcelNameValidator.Validate(key + "_").IsValid && !key.Contains('.')
            ? key
            : LetNameSanitizer.Sanitize(key) ?? "_";

    private static string StateCall(string prefix, IReadOnlyList<(string Key, string Value)> pairs)
    {
        var first = pairs.Take(SetVarsPairLimit).SelectMany(p => new[] { Quoted(p.Key), p.Value });
        var call = $"{prefix}SetVars({string.Join(", ", first)})";

        // SetVars takes ten pairs, so the rest chain through SetVar.
        return pairs.Skip(SetVarsPairLimit)
            .Aggregate(call, (state, p) => $"{prefix}SetVar({state}, {Quoted(p.Key)}, {p.Value})");
    }

    private static string Quoted(string key) => $"\"{key}\"";

    /// <summary>
    ///     True when <paramref name="expression" /> can be a value. A literal beside something
    ///     with only a space between them is a missing comma (<c>p 0 b 4</c>), though it parses
    ///     as an intersection.
    /// </summary>
    private static bool Reads(string expression)
    {
        try
        {
            var ast = FormulaParser.Parse(expression);
            return ast.EqualsToken == null
                && !StateStepParser.Walk(ast.Root).OfType<BinaryNode>().Any(n =>
                    n.Operator == " " && (IsLiteral(n.Left) || IsLiteral(n.Right)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsLiteral(FormulaNode node) =>
        node is LeafNode { TokenType: FormulaTokenType.Number or FormulaTokenType.String };

    private static IEnumerable<string> SplitAtTopLevelCommas(string text, IReadOnlyList<FormulaToken> tokens)
    {
        var depth = 0;
        var start = 0;
        foreach (var token in tokens)
        {
            switch (token.Type)
            {
                case FormulaTokenType.LParen:
                    depth++;
                    break;
                case FormulaTokenType.RParen:
                    depth--;
                    break;
                case FormulaTokenType.Comma when depth == 0:
                    yield return text[start..token.Start];
                    start = token.End;
                    break;
            }
        }

        yield return text[start..];
    }
}
