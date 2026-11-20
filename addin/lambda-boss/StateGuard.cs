namespace LambdaBoss;

/// <summary>
///     The /State guard toggle (spec 0015, "Guard toggle (#464)"). A guarded step's body is
///     <c>IF(cond, a, body)</c> (or the branches swapped): once the walk has ended it hands the
///     state back unchanged. On wraps the body in the one fixed guard,
///     <c>IF(GetVar(a, "st") &lt;&gt; "go", a, body)</c>; off takes the outer guard off and gives back
///     the body's text exactly. Everything is read from the step's text: no hidden state.
/// </summary>
public static class StateGuard
{
    /// <summary>The status variable the guard reads.</summary>
    public const string StatusKey = "st";

    /// <summary>The status that lets the walk go on; any other status stops it.</summary>
    public const string GoValue = "\"go\"";

    /// <summary>The fixed guard's condition, spelled with the state library's prefix and the step's state name.</summary>
    public static string Condition(string prefix, string state) =>
        $"{prefix}GetVar({state}, \"{StatusKey}\") <> {GoValue}";

    /// <summary>The body with the fixed guard round it.</summary>
    public static string Wrap(string body, string prefix, string state) =>
        $"IF({Condition(prefix, state)}, {state}, {body})";

    /// <summary>
    ///     True when the body is a guard: an <c>IF</c> with three arguments, one branch the bare
    ///     state name. <paramref name="inner" /> is the other branch's exact text. Any condition
    ///     counts (Tim's hand-written <c>OR(…, LEFT(…))</c> guard too); only the outer guard is read.
    /// </summary>
    public static bool TryUnwrap(string body, string state, out string inner)
    {
        inner = body;
        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            if (FormulaParser.Parse(body).Root is not FunctionCallNode iff
                || !StateStepParser.IsNamed(iff.Name, "IF") || iff.Arguments.Count != 3) return false;

            FormulaNode? kept = null;
            if (IsState(iff.Arguments[1], state)) kept = iff.Arguments[2];
            else if (IsState(iff.Arguments[2], state)) kept = iff.Arguments[1];
            if (kept == null || kept is EmptyArgNode) return false;

            var text = body[kept.Start..kept.End];
            if (text.Trim().Length == 0) return false;
            inner = text;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>True when the step's body is guarded.</summary>
    public static bool IsGuarded(StateEngine engine)
    {
        if (engine is null) throw new ArgumentNullException(nameof(engine));
        return !engine.IsNew && engine.Parameters.Count > 0 && TryUnwrap(engine.Output, engine.Parameters[0], out _);
    }

    /// <summary>
    ///     The one-command toggle: the engine's variables, form and name as they are, the guard
    ///     flipped. <see cref="StateEngines.Apply" /> adds <c>st</c> when it's missing.
    /// </summary>
    public static StateEngineChange Toggle(StateEngine engine)
    {
        if (engine is null) throw new ArgumentNullException(nameof(engine));
        return new StateEngineChange(engine.Rows, engine.Fold, engine.StepName, Guard: !engine.IsGuarded);
    }

    private static bool IsState(FormulaNode node, string state) =>
        node is LeafNode { TokenType: FormulaTokenType.Name } leaf
        && string.Equals(leaf.Text, state, StringComparison.OrdinalIgnoreCase);
}
