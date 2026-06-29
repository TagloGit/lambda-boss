using System.Globalization;
using System.Text;

namespace LambdaBoss;

/// <summary>
///     Spec 0010 (spike) — issue #279. The <c>/Debug Nested</c> engine. Where
///     <see cref="UnnestEngine" /> answers "decompose this static nested formula
///     into named steps", this engine answers the complementary, harder question:
///     a <c>LAMBDA(...)</c> applied over an array (<c>BYROW(arr, LAMBDA(r, …))</c>,
///     a custom <c>PAIROP(r, LAMBDA(a, b, …))</c>, …) runs its body once per
///     element with its parameters bound dynamically, so no sub-expression has a
///     single value to inspect. This engine lets the user <em>pin</em> one
///     concrete example — binding each in-scope parameter to a value — and then
///     produces, for every step of the body, a self-contained <c>=LET(...)</c>
///     the add-in evaluates to show that step's live value for the pinned example
///     (the "pin &amp; watch" approach: option A + mechanism C from the #279 spike).
///
///     <para>
///     The engine is pure (no Excel dependency): <see cref="Discover" /> finds
///     the lambda scopes, <see cref="SuggestPins" /> proposes default pin
///     expressions for recognised iterators, and <see cref="BuildWatch" /> turns
///     a chosen scope + pins into evaluable formulas. Actually running those
///     formulas (reading values back from Excel) is the command's job.
///     </para>
///
///     <para>
///     Step decomposition reuses <see cref="UnnestEngine.Unnest" /> over the
///     scope's body text, which already treats a deeper nested
///     <c>LAMBDA</c>/<c>LET</c> as opaque — so a step that contains an inner
///     lambda keeps it inline (the user drills into that inner scope separately),
///     and nothing escapes its binding scope.
///     </para>
/// </summary>
public static class DebugNestedEngine
{
    private static readonly string[] Empty = Array.Empty<string>();

    /// <summary>
    ///     Scans <paramref name="formula" /> for every <c>LAMBDA(...)</c> scope,
    ///     returning them outer-first. A malformed formula or one with no lambda
    ///     is reported via <see cref="DebugDiscovery.Diagnostic" />.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="formula" /> is null.</exception>
    public static DebugDiscovery Discover(string formula)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));

        List<ScopeInfo> infos;
        try
        {
            infos = WalkScopes(formula);
        }
        catch (FormatException ex)
        {
            return new DebugDiscovery(
                Array.Empty<DebugScope>(),
                new DebugDiagnostic(
                    DebugDiagnosticKind.MalformedFormula,
                    $"Debug Nested can't parse this formula: {ex.Message}"));
        }

        if (infos.Count == 0)
            return new DebugDiscovery(
                Array.Empty<DebugScope>(),
                new DebugDiagnostic(
                    DebugDiagnosticKind.NoLambda,
                    "This formula contains no LAMBDA(...) to debug. Use /Unnest for a static nested formula."));

        return new DebugDiscovery(infos.Select(ToScope).ToList());
    }

    /// <summary>
    ///     Proposes a default pin expression for every parameter in scope inside
    ///     <paramref name="scopeKey" /> (enclosing params outer-first, then the
    ///     scope's own), at the 1-based example <paramref name="index" />. A
    ///     recognised array iterator yields a slice of its source
    ///     (<c>CHOOSEROWS(arr, k)</c> for <c>BYROW</c>, <c>CHOOSECOLS</c> for
    ///     <c>BYCOL</c>); anything else yields an empty expression for the user to
    ///     fill in. Returns an empty list when the formula or scope can't be found.
    /// </summary>
    public static IReadOnlyList<DebugPin> SuggestPins(string formula, string scopeKey, int index = 1)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));

        List<ScopeInfo> infos;
        try
        {
            infos = WalkScopes(formula);
        }
        catch (FormatException)
        {
            return Array.Empty<DebugPin>();
        }

        var target = infos.FirstOrDefault(s => s.Key == scopeKey);
        if (target is null) return Array.Empty<DebugPin>();

        var pins = new List<DebugPin>();
        foreach (var scope in ChainTo(infos, target))
            foreach (var p in scope.Params)
                pins.Add(new DebugPin(p, SuggestExpr(scope, p, index)));

        return pins;
    }

    /// <summary>
    ///     Builds the watch model for <paramref name="scopeKey" /> pinned with
    ///     <paramref name="pins" />: the scope body's steps (via
    ///     <see cref="UnnestEngine" />) each paired with a self-contained
    ///     <c>=LET(...)</c> that binds the pins and preceding steps then returns
    ///     the step, plus a final formula for the whole body. Pins with a blank
    ///     expression are omitted (the corresponding parameter stays unbound, so
    ///     its dependent steps evaluate to an Excel error — a signal to fill it
    ///     in). A body the expression parser can't read is reported via
    ///     <see cref="DebugWatch.Diagnostic" />.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static DebugWatch BuildWatch(
        string formula,
        string scopeKey,
        IReadOnlyList<DebugPin> pins,
        IReadOnlyCollection<string>? definedNames = null)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));
        if (scopeKey is null) throw new ArgumentNullException(nameof(scopeKey));
        if (pins is null) throw new ArgumentNullException(nameof(pins));

        List<ScopeInfo> infos;
        try
        {
            infos = WalkScopes(formula);
        }
        catch (FormatException ex)
        {
            return Malformed(scopeKey, DebugDiagnosticKind.MalformedFormula,
                $"Debug Nested can't parse this formula: {ex.Message}");
        }

        var target = infos.FirstOrDefault(s => s.Key == scopeKey);
        if (target is null)
            return Malformed(scopeKey, DebugDiagnosticKind.MalformedBody,
                "That lambda scope is no longer present in the formula.");

        var un = UnnestEngine.Unnest(target.BodyText, definedNames);
        if (un.Diagnostic is not null)
            return Malformed(scopeKey, DebugDiagnosticKind.MalformedBody,
                $"Can't decompose this lambda body: {un.Diagnostic.Message}");

        // Pin bindings (outer-first); a blank expression is skipped so we never
        // emit a syntactically invalid `LET(p, , …)`.
        var pinBindings = pins
            .Where(p => !string.IsNullOrWhiteSpace(p.Expression))
            .Select(p => (Name: p.Param, Value: p.Expression.Trim()))
            .ToList();

        // Steps + final body. With no rowStates every step is included, so the
        // synthesised LET's bindings line up with un.Steps in order; its body is
        // the scope body with included children collapsed to step names. When the
        // body decomposes to nothing, the body text itself is the only result.
        var steps = un.Steps;
        string finalBody;
        if (steps.Count == 0)
        {
            finalBody = target.BodyText;
        }
        else
        {
            try
            {
                finalBody = LetParser.Parse(un.SynthesisedLet).Body;
            }
            catch (FormatException)
            {
                // Defensive: UnnestEngine always emits a parseable LET, but if
                // that ever changes, fall back to the raw body text.
                finalBody = target.BodyText;
            }
        }

        var stepBindings = new List<(string Name, string Value)>();
        var debugSteps = new List<DebugStep>(steps.Count);
        foreach (var s in steps)
        {
            stepBindings.Add((s.Name, s.Rhs));
            var eval = BuildEvalLet(pinBindings, stepBindings, s.Name);
            debugSteps.Add(new DebugStep(s.Key, s.Name, s.Rhs, eval));
        }

        var finalEval = BuildEvalLet(pinBindings, stepBindings, finalBody);
        return new DebugWatch(scopeKey, debugSteps, "result", finalEval);
    }

    // ---------------- evaluable-formula assembly ----------------

    /// <summary>
    ///     Assembles a compact, single-cell <c>=LET(pin1, e1, …, step1, r1, …, body)</c>
    ///     from the pin bindings, the step bindings emitted so far, and the
    ///     expression to return. With no bindings at all it degrades to <c>=body</c>.
    /// </summary>
    private static string BuildEvalLet(
        IReadOnlyList<(string Name, string Value)> pins,
        IReadOnlyList<(string Name, string Value)> steps,
        string body)
    {
        if (pins.Count == 0 && steps.Count == 0)
            return "=" + body;

        var sb = new StringBuilder();
        sb.Append("=LET(");
        foreach (var (name, value) in pins)
            sb.Append(name).Append(", ").Append(value).Append(", ");
        foreach (var (name, value) in steps)
            sb.Append(name).Append(", ").Append(value).Append(", ");
        sb.Append(body).Append(')');
        return sb.ToString();
    }

    private static DebugWatch Malformed(string scopeKey, DebugDiagnosticKind kind, string message)
    {
        return new DebugWatch(
            scopeKey,
            Array.Empty<DebugStep>(),
            "result",
            "",
            new DebugDiagnostic(kind, message));
    }

    // ---------------- default pins ----------------

    /// <summary>
    ///     Suggests a slice expression that picks example <paramref name="index" />
    ///     from <paramref name="scope" />'s host source, when the host is a
    ///     recognised array iterator. Unrecognised hosts (custom higher-order
    ///     lambdas, accumulator-style <c>SCAN</c>/<c>REDUCE</c>, multi-array
    ///     <c>MAP</c>) return an empty string so the dialog leaves the pin blank.
    /// </summary>
    private static string SuggestExpr(ScopeInfo scope, string param, int index)
    {
        var host = scope.HostFunction;
        var k = index.ToString(CultureInfo.InvariantCulture);

        // Single-source row/column iterators have an unambiguous per-element slice.
        if (scope.Params.Count == 1 && scope.HostSourceArgs.Count == 1)
        {
            if (host.Equals("BYROW", StringComparison.OrdinalIgnoreCase))
                return $"CHOOSEROWS({scope.HostSourceArgs[0]}, {k})";
            if (host.Equals("BYCOL", StringComparison.OrdinalIgnoreCase))
                return $"CHOOSECOLS({scope.HostSourceArgs[0]}, {k})";
        }

        return "";
    }

    // ---------------- AST walk ----------------

    private sealed class ScopeInfo
    {
        public string Key = "";
        public int Depth;
        public string HostFunction = "";
        public List<string> HostSourceArgs = new();
        public List<string> Params = new();
        public List<string> EnclosingParams = new();
        public string BodyText = "";
        public int? ParentIndex;
    }

    private static List<ScopeInfo> WalkScopes(string formula)
    {
        var ast = FormulaParser.Parse(formula);
        var sink = new List<ScopeInfo>();
        Visit(ast.Root, hostName: "", Empty, Empty, depth: 0, parentIndex: null, sink);
        return sink;
    }

    private static void Visit(
        FormulaNode node,
        string hostName,
        IReadOnlyList<string> hostArgs,
        IReadOnlyList<string> enclosing,
        int depth,
        int? parentIndex,
        List<ScopeInfo> sink)
    {
        if (IsLambdaCall(node, out var fc))
        {
            var (paramNames, body) = SplitLambda(fc);
            var info = new ScopeInfo
            {
                Key = "scope" + sink.Count.ToString(CultureInfo.InvariantCulture),
                Depth = depth,
                HostFunction = hostName,
                HostSourceArgs = hostArgs.ToList(),
                Params = paramNames,
                EnclosingParams = enclosing.ToList(),
                BodyText = body.ToFormula().Trim(),
                ParentIndex = parentIndex
            };
            var myIndex = sink.Count;
            sink.Add(info);

            // Descend into the body to find nested lambda scopes; the body's own
            // params join the enclosing set for them.
            var childEnclosing = enclosing.Concat(paramNames).ToList();
            Visit(body, hostName: "", Empty, childEnclosing, depth + 1, myIndex, sink);
            return;
        }

        if (node is FunctionCallNode call)
        {
            // Args of this call are hosted by it; the non-lambda args are the
            // "source" a default pin can slice.
            var sourceArgs = call.Arguments
                .Where(a => !IsLambdaCall(a, out _))
                .Select(a => a.ToFormula().Trim())
                .ToList();
            var name = CleanName(call.Name);
            foreach (var arg in call.Arguments)
                Visit(arg, name, sourceArgs, enclosing, depth, parentIndex, sink);
            return;
        }

        foreach (var child in Children(node))
            Visit(child, hostName, hostArgs, enclosing, depth, parentIndex, sink);
    }

    private static bool IsLambdaCall(FormulaNode node, out FunctionCallNode call)
    {
        if (node is FunctionCallNode fc &&
            CleanName(fc.Name).Equals("LAMBDA", StringComparison.OrdinalIgnoreCase))
        {
            call = fc;
            return true;
        }

        call = null!;
        return false;
    }

    /// <summary>
    ///     Splits a <c>LAMBDA(p1, …, pN, body)</c> call into its parameter names
    ///     (surrounding optional-arg <c>[brackets]</c> stripped) and body node. A
    ///     bodyless <c>LAMBDA()</c> can't occur from a successful parse, but is
    ///     guarded defensively.
    /// </summary>
    private static (List<string> Params, FormulaNode Body) SplitLambda(FunctionCallNode fc)
    {
        var args = fc.Arguments;
        var ps = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            var raw = args[i].ToFormula().Trim();
            if (raw.Length >= 2 && raw[0] == '[' && raw[^1] == ']')
                raw = raw[1..^1].Trim();
            ps.Add(raw);
        }

        var body = args.Count > 0 ? args[^1] : (FormulaNode)new LeafNode(fc.CloseParen);
        return (ps, body);
    }

    private static IEnumerable<FormulaNode> Children(FormulaNode node)
    {
        return node switch
        {
            FunctionCallNode fc => fc.Arguments,
            BinaryNode b => new[] { b.Left, b.Right },
            UnaryNode u => new[] { u.Operand },
            PostfixNode p => new[] { p.Operand },
            ParenNode pn => pn.Items,
            _ => Array.Empty<FormulaNode>()
        };
    }

    /// <summary>
    ///     Strips a sheet/workbook qualifier and the <c>_xlfn.</c> / <c>_xlws.</c>
    ///     compatibility prefixes from a function name, preserving case (so the
    ///     dialog shows <c>BYROW</c> / <c>PAIROP</c>, not a lowercased form).
    /// </summary>
    private static string CleanName(string fnName)
    {
        var s = fnName;
        var bang = s.LastIndexOf('!');
        if (bang >= 0) s = s[(bang + 1)..];

        if (s.StartsWith("_xlfn._xlws.", StringComparison.OrdinalIgnoreCase))
            s = s["_xlfn._xlws.".Length..];
        else if (s.StartsWith("_xlfn.", StringComparison.OrdinalIgnoreCase))
            s = s["_xlfn.".Length..];
        else if (s.StartsWith("_xlws.", StringComparison.OrdinalIgnoreCase))
            s = s["_xlws.".Length..];

        return s;
    }

    // ---------------- projection helpers ----------------

    private static DebugScope ToScope(ScopeInfo info)
    {
        return new DebugScope(
            info.Key,
            info.Depth,
            info.HostFunction,
            info.Params,
            info.EnclosingParams,
            info.BodyText,
            LabelOf(info));
    }

    private static string LabelOf(ScopeInfo info)
    {
        var sig = "LAMBDA(" + string.Join(", ", info.Params) + ")";
        return string.IsNullOrEmpty(info.HostFunction)
            ? sig
            : $"{sig} — arg of {info.HostFunction}";
    }

    /// <summary>
    ///     The root-to-target chain of scopes (outer-first, inclusive), so pin
    ///     suggestion walks each enclosing scope's own params before the target's.
    /// </summary>
    private static IEnumerable<ScopeInfo> ChainTo(List<ScopeInfo> infos, ScopeInfo target)
    {
        var chain = new List<ScopeInfo>();
        var cur = (ScopeInfo?)target;
        while (cur is not null)
        {
            chain.Add(cur);
            cur = cur.ParentIndex is { } pi ? infos[pi] : null;
        }

        chain.Reverse();
        return chain;
    }
}
