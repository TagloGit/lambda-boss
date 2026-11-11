using System.Text;
using System.Text.RegularExpressions;

using LambdaBoss.Commands;

namespace LambdaBoss;

/// <summary>
///     What Edit Lambda does on a fold cell: <paramref name="Formula" /> to write beside it, or
///     <paramref name="Refusal" /> to show instead.
/// </summary>
public record StepperOutcome(string? Formula, string? StepName, string? Refusal);

/// <summary>
///     Edit Lambda on a fold cell (spec 0011, part 4). From <c>REDUCE(seed, inputs, step)</c>, or
///     the same with <c>SCAN</c>, anywhere in a formula, builds a <c>LET</c> holding the step's
///     body with a stepper <c>k</c>: the cell shows the state after step <c>k</c>. Pure text, so
///     nothing here touches Excel.
/// </summary>
public static class StateStepper
{
    public const string SeveralFoldsMessage =
        "Edit Lambda found more than one REDUCE/SCAN with a named step in this cell; "
        + "put the one to edit in its own cell.";

    public const string NoRoomMessage =
        "Edit Lambda found no empty cell for the step: it looks 10 columns to the right of the fold, "
        + "then 10 rows below.";

    private const string NoSeedMessage =
        "Edit Lambda needs the fold's seed and inputs written out: REDUCE(seed, inputs, step).";

    private const int SearchDistance = 10;
    private const int LastRow = 1048576;
    private const int LastColumn = 16384;
    private const int MaxFormulaLength = 8192;

    // Stand-ins for the two arguments while the step's names are made safe. A step's text can't
    // hold them, and they keep the seed and inputs out of the renaming.
    private const string StateSlot = "\u0001state";
    private const string InputSlot = "\u0001input";

    // Marks a LET name bound a second time around the fold.
    private const string Rebound = "\u0002";

    /// <summary>
    ///     The prototype for the fold in <paramref name="formula" />, a refusal, or null when
    ///     the formula holds no fold with a named two-parameter LAMBDA step.
    /// </summary>
    /// <param name="formula">The cell's formula.</param>
    /// <param name="refersTo">Looks up a workbook name's definition; null when there's none.</param>
    public static StepperOutcome? Build(string? formula, Func<string, string?> refersTo)
    {
        if (string.IsNullOrEmpty(formula)) return null;
        if (refersTo is null) throw new ArgumentNullException(nameof(refersTo));

        FormulaNode root;
        try
        {
            root = FormulaParser.Parse(formula!).Root;
        }
        catch (FormatException)
        {
            return null;
        }

        var found = new List<Fold>();
        Search(root, new List<FunctionCallNode>(), found, refersTo);
        if (found.Count == 0) return null;
        if (found.Count > 1) return Refused(SeveralFoldsMessage);

        try
        {
            return BuildPrototype(found[0]);
        }
        catch (FormatException ex)
        {
            return Refused($"Edit Lambda could not read this fold: {ex.Message}");
        }
    }

    /// <summary>
    ///     Where the prototype goes: the first empty cell to the right of the fold within 10
    ///     columns, else the first empty cell below within 10 rows, else null.
    /// </summary>
    public static (int Row, int Column)? FindTarget(int row, int column, Func<int, int, bool> isEmpty)
    {
        if (isEmpty is null) throw new ArgumentNullException(nameof(isEmpty));

        for (var c = column + 1; c <= Math.Min(column + SearchDistance, LastColumn); c++)
            if (isEmpty(row, c))
                return (row, c);

        for (var r = row + 1; r <= Math.Min(row + SearchDistance, LastRow); r++)
            if (isEmpty(r, column))
                return (r, column);

        return null;
    }

    private static void Search(
        FormulaNode node, List<FunctionCallNode> around, List<Fold> found, Func<string, string?> refersTo)
    {
        if (node is FunctionCallNode call)
        {
            if (AsFold(call, around, refersTo) is { } fold) found.Add(fold);
            around.Add(call);
        }

        foreach (var child in StateStepParser.Children(node))
            Search(child, around, found, refersTo);

        if (node is FunctionCallNode) around.RemoveAt(around.Count - 1);
    }

    private static Fold? AsFold(
        FunctionCallNode call, List<FunctionCallNode> around, Func<string, string?> refersTo)
    {
        if (!StateFold.IsFold(call)
            || call.Arguments[2] is not LeafNode { TokenType: FormulaTokenType.Name } step
            || step.Text.Contains('!'))
            return null;

        // A LET around the fold can bind the step, and that's a local LAMBDA, not a workbook name.
        var path = around.Append(call).ToList();
        var bindings = BindingsAround(path);
        if (bindings.ContainsKey(step.Text)) return null;

        var definition = refersTo(step.Text);
        if (!LambdaSignatureParser.IsLambdaFormula(definition)) return null;

        LambdaSignature signature;
        try
        {
            signature = LambdaSignatureParser.Parse(definition!);
        }
        catch (FormatException)
        {
            return null;
        }

        return signature.Parameters.Count == 2
            ? new Fold(call, step.Text, signature, bindings, ParametersAround(path))
            : null;
    }

    /// <summary>What the LETs around the fold have bound by the time it's reached; inner ones win.</summary>
    private static Dictionary<string, string> BindingsAround(List<FunctionCallNode> path)
    {
        // One flat map: a name bound twice (an inner LET rebinding an outer one) can't be
        // written in place correctly, so it's listed as rebound and the fold is refused.
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var call = path[i];
            if (!StateStepParser.IsNamed(call.Name, "LET")) continue;

            var reached = IndexOfChild(call, path[i + 1]);
            for (var a = 0; a + 1 < reached; a += 2)
                if (call.Arguments[a] is LeafNode { TokenType: FormulaTokenType.Name } name)
                    bindings[bindings.ContainsKey(name.Text) ? Rebound + name.Text : name.Text] =
                        call.Arguments[a + 1].ToFormula().Trim();
        }
        return bindings;
    }

    private static HashSet<string> ParametersAround(List<FunctionCallNode> path)
    {
        var parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < path.Count; i++)
        {
            if (!StateStepParser.IsNamed(path[i].Name, "LAMBDA")) continue;
            foreach (var arg in path[i].Arguments.Take(path[i].Arguments.Count - 1))
                if (arg is LeafNode { TokenType: FormulaTokenType.Name } name)
                    parameters.Add(name.Text.Trim('[', ']'));
        }
        return parameters;
    }

    // The fold can sit deeper than a direct argument, so this finds the argument that holds it.
    private static int IndexOfChild(FunctionCallNode call, FunctionCallNode descendant)
    {
        for (var i = 0; i < call.Arguments.Count; i++)
            if (StateStepParser.Walk(call.Arguments[i]).Contains(descendant))
                return i;
        return -1;
    }

    /// <summary>
    ///     The named step a stepper prototype was written from (#447): the name in its state line's
    ///     <c>INDEX(SCAN(seed, inputs, name), k - 1)</c>, when the workbook holds that name as a
    ///     LAMBDA. LET to LAMBDA prefills it, so saving the edited step back is one Enter. Null
    ///     when the line holds no such call, or more than one name.
    /// </summary>
    /// <param name="stateRhs">The right-hand side of the step's state binding.</param>
    /// <param name="refersTo">Looks up a workbook name's definition; null when there's none.</param>
    public static string? PrototypeStepName(string? stateRhs, Func<string, string?> refersTo)
    {
        if (string.IsNullOrWhiteSpace(stateRhs)) return null;
        if (refersTo is null) throw new ArgumentNullException(nameof(refersTo));

        FormulaNode root;
        try
        {
            root = FormulaParser.Parse(stateRhs!).Root;
        }
        catch (FormatException)
        {
            return null;
        }

        // Only the stepper's own shape: IF(k = 1, seed, INDEX(SCAN(seed, inputs, name), k - 1)).
        static string Bare(FormulaNode n) => string.Concat(n.ToFormula().Where(ch => !char.IsWhiteSpace(ch)));

        var names = StateStepParser.Walk(root)
            .OfType<FunctionCallNode>()
            .Where(c => StateStepParser.IsNamed(c.Name, "IF") && c.Arguments.Count == 3)
            .Select(c => (Counter: Regex.Match(Bare(c.Arguments[0]), @"^([A-Za-z_][A-Za-z0-9_.]*)=1$"), Else: c.Arguments[2]))
            .Where(c => c.Counter.Success)
            .Select(c => (Counter: c.Counter.Groups[1].Value, Index: c.Else as FunctionCallNode))
            .Where(c => c.Index != null && StateStepParser.IsNamed(c.Index.Name, "INDEX") && c.Index.Arguments.Count == 2
                && string.Equals(Bare(c.Index.Arguments[1]), c.Counter + "-1", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Index!.Arguments[0])
            .OfType<FunctionCallNode>()
            .Where(s => StateStepParser.IsNamed(s.Name, "SCAN") && s.Arguments.Count == 3)
            .Select(s => s.Arguments[2])
            .OfType<LeafNode>()
            .Where(l => l.TokenType == FormulaTokenType.Name)
            .Select(l => l.Text.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 1 && LambdaSignatureParser.IsLambdaFormula(refersTo(names[0])) ? names[0] : null;
    }

    private static StepperOutcome BuildPrototype(Fold fold)
    {
        if (fold.Call.Arguments[0] is EmptyArgNode || fold.Call.Arguments[1] is EmptyArgNode)
            return Refused(NoSeedMessage);

        var rebound = fold.Bindings.Keys.FirstOrDefault(k => k.StartsWith(Rebound, StringComparison.Ordinal));
        if (rebound != null)
            return Refused($"Edit Lambda can't step this fold: the LETs around it bind {rebound[1..]} twice. "
                + "Give the inner one another name.");

        // Whole expressions, so a SetVar chain past ten variables comes through in one piece.
        var seed = StateFold.Inline(fold.Call.Arguments[0].ToFormula().Trim(), fold.Bindings);
        var inputs = StateFold.Inline(fold.Call.Arguments[1].ToFormula().Trim(), fold.Bindings);

        var foldNames = NamesIn(seed);
        foldNames.UnionWith(NamesIn(inputs));
        foldNames.Add(fold.StepName);

        var unavailable = fold.Parameters.FirstOrDefault(foldNames.Contains);
        if (unavailable != null)
            return Refused($"Edit Lambda can't step this fold: it reads {unavailable}, "
                + "a parameter of the LAMBDA around it, which a cell of its own can't see.");

        var (expanded, expandedBody) = EditLambdaCommand.ExpandBindings(
            fold.Signature, new[] { StateSlot, InputSlot });

        if (!expanded.Any(p => p.Value == StateSlot) || !expanded.Any(p => p.Value == InputSlot))
            return Refused($"Edit Lambda can't step {fold.StepName}: its body binds one of its own "
                + "parameters again.");

        var (pairs, body) = KeepNamesApart(expanded, expandedBody, foldNames);

        var stepper = !IsArrayStep(pairs, body, seed);
        var k = stepper ? FreeName(pairs, body, foldNames) : "";
        var state = stepper
            ? $"IF({k} = 1, {seed}, INDEX(SCAN({seed}, {inputs}, {fold.StepName}), {k} - 1))"
            : seed;
        var input = stepper ? $"INDEX({inputs}, {k})" : $"INDEX({inputs}, 1)";

        var bindings = pairs
            .Select(p => (p.Name, Value: p.Value == StateSlot ? state : p.Value == InputSlot ? input : p.Value))
            .ToList();
        if (stepper) bindings.Insert(0, (k, "1"));

        var sb = new StringBuilder("=");
        FormulaFormatter.AppendLet(sb, 0, bindings, body);

        return sb.Length > MaxFormulaLength
            ? Refused($"Edit Lambda can't step {fold.StepName}: the prototype would be longer than "
                + "Excel's limit for a formula.")
            : new StepperOutcome(sb.ToString(), fold.StepName, null);
    }

    /// <summary>
    ///     The step's names and the fold's expressions end up in one LET, where a step name can
    ///     capture what the fold reads. Excel lets a LET name shadow a cell, then writes that
    ///     cell's other uses as the name <c>'H1'</c>, which gives #NAME?. So a step name the
    ///     fold's expressions use gets a <c>_</c>.
    /// </summary>
    private static (List<(string Name, string Value)> Pairs, string Body) KeepNamesApart(
        List<(string Name, string Value)> pairs, string body, HashSet<string> foldNames)
    {
        var taken = new HashSet<string>(foldNames, StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(pairs.Select(p => p.Name));

        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in pairs)
        {
            if (!foldNames.Contains(name)) continue;
            var renamed = name + "_";
            while (taken.Contains(renamed)) renamed += "_";
            taken.Add(renamed);
            renames[name] = renamed;
        }

        if (renames.Count == 0) return (pairs, body);

        return (
            pairs.Select(p => (
                renames.TryGetValue(p.Name, out var name) ? name : p.Name,
                p.Value is StateSlot or InputSlot ? p.Value : LetToLambdaBuilder.ApplyRenames(p.Value, renames)))
                .ToList(),
            LetToLambdaBuilder.ApplyRenames(body, renames));
    }

    /// <summary>
    ///     SCAN can't carry an array, so an array step gets no stepper. A step the parser doesn't
    ///     recognise (a body that isn't a plain SetVars call) goes by its seed.
    /// </summary>
    private static bool IsArrayStep(List<(string Name, string Value)> pairs, string body, string seed)
    {
        var sample = pairs
            .Select(p => (p.Name, Value: p.Value == StateSlot ? seed : p.Value == InputSlot ? "INDEX(0, 1)" : p.Value))
            .ToList();
        var sb = new StringBuilder("=");
        FormulaFormatter.AppendLet(sb, 0, sample, body);

        if (LetParser.IsLetFormula(sb.ToString())
            && StateStepParser.TryParse(LetParser.Parse(sb.ToString())) is { } step)
            return step.Family == StateFamily.Array;

        var root = FormulaParser.Parse(seed).Root;
        return root is LeafNode { TokenType: FormulaTokenType.Array }
            || (root is FunctionCallNode call
                && (StateStepParser.IsNamed(call.Name, "HSTACK") || StateStepParser.IsNamed(call.Name, "VSTACK")));
    }

    private static string FreeName(
        List<(string Name, string Value)> pairs, string body, HashSet<string> foldNames)
    {
        var used = new HashSet<string>(foldNames, StringComparer.OrdinalIgnoreCase);
        used.UnionWith(pairs.Select(p => p.Name));
        used.UnionWith(NamesIn(body));
        foreach (var (_, value) in pairs.Where(p => p.Value is not (StateSlot or InputSlot)))
            used.UnionWith(NamesIn(value));

        var name = "k";
        while (used.Contains(name)) name += "_";
        return name;
    }

    /// <summary>
    ///     The bare names, cells, whole columns and calls in an expression: what a LET name
    ///     could capture. <c>$H$1</c>, <c>Sheet1!H1</c> and <c>t[b]</c> can't be captured.
    /// </summary>
    private static HashSet<string> NamesIn(string expression)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in StateStepParser.Walk(FormulaParser.Parse(expression).Root))
        {
            switch (node)
            {
                case FunctionCallNode call:
                    names.Add(call.Name);
                    break;
                case LeafNode { TokenType: FormulaTokenType.Name } leaf
                    when leaf.Text.IndexOfAny(new[] { '!', '[', '\'', '$' }) < 0:
                    names.UnionWith(leaf.Text.Split(':'));
                    break;
            }
        }
        return names;
    }

    private static StepperOutcome Refused(string message) => new(null, null, message);

    private sealed record Fold(
        FunctionCallNode Call,
        string StepName,
        LambdaSignature Signature,
        Dictionary<string, string> Bindings,
        HashSet<string> Parameters);
}
