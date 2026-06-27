using System.Globalization;
using System.Text;

namespace LambdaBoss;

/// <summary>
///     Spec 0009 — the <c>/Unnest</c> decomposition engine (non-LET path). Takes
///     a nested formula, parses it into an expression tree via
///     <see cref="FormulaParser" />, and explodes each function-call and binary-
///     operator node (other than the root) into a named, leaf-first <c>=LET(...)</c>
///     step. The root stays as the LET body with its child references rewritten
///     to step names. Reference operators (range <c>:</c> and intersection
///     <c>space</c>) are treated as leaves, never steps — matching the parser's
///     "ranges are non-steps" rule. Unary expressions and postfix <c>%</c>/<c>#</c>
///     also stay inline. A name-binding construct (a <c>LAMBDA(...)</c> or a
///     nested <c>LET(...)</c>) is <em>opaque</em>: its body binds names that
///     exist only inside it, so the engine never descends into it nor emits it
///     as a step — hoisting a sub-expression out of that scope would leave the
///     bound names unbound (issue #278). Decomposing inside a lambda via a
///     scoped nested LET is a future enhancement (issue #279).
///
///     <para>
///     The decomposition is maximally granular by default; the dialog's per-row
///     Include toggle collapses a step back into its parent. Inlining and child
///     re-parenting fall out of the recursive renderer for free: a node renders
///     to its step name only when it is an <em>included</em> step and not the
///     node currently being rendered as its own RHS — so un-including a step
///     causes its parent to render through it, and that node's own included
///     children surface as names one level up.
///     </para>
///
///     <para>
///     Re-spacing is canonical (<c>, </c> between arguments, spaces around
///     arithmetic / concat / comparison operators, none around <c>:</c>) but no
///     parentheses are ever synthesised: all source grouping survives as
///     <see cref="ParenNode" />s in the tree, and substituting an atomic step
///     name for a subtree never lowers precedence, so the rewrite is always
///     structurally safe. A successful synthesis round-trips through
///     <see cref="LetParser" />.
///     </para>
/// </summary>
public static class UnnestEngine
{
    /// <summary>
    ///     Runs the initial decomposition over <paramref name="formula" />. Every
    ///     step is auto-named and included. <paramref name="definedNames" /> is
    ///     the workbook's defined-name catalogue the auto-namer avoids colliding
    ///     with (pass null when there are none to consider, e.g. unit tests).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="formula" /> is null.</exception>
    public static UnnestResult Unnest(
        string formula,
        IReadOnlyCollection<string>? definedNames = null)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));
        return Decompose(formula, null, definedNames);
    }

    /// <summary>
    ///     Re-runs the decomposition with the dialog's per-row state
    ///     (<paramref name="rowStates" />). Each step takes its name and Include
    ///     flag from the matching state by <see cref="UnnestRowState.Key" />;
    ///     rows with no state default to an auto name and included. Explicit
    ///     names are reserved before auto-naming the rest, so a user rename never
    ///     collides with an auto-allocated suffix.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="formula" /> or <paramref name="rowStates" /> is null.</exception>
    public static UnnestResult Recompute(
        string formula,
        IReadOnlyList<UnnestRowState> rowStates,
        IReadOnlyCollection<string>? definedNames = null)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));
        if (rowStates is null) throw new ArgumentNullException(nameof(rowStates));
        return Decompose(formula, rowStates, definedNames);
    }

    private static UnnestResult Decompose(
        string formula,
        IReadOnlyList<UnnestRowState>? rowStates,
        IReadOnlyCollection<string>? definedNames)
    {
        // Existing LETs are exploded by the existing-LET path (issue #273); the
        // non-LET engine refuses rather than parse a LET as a function call.
        if (LetParser.IsLetFormula(formula))
        {
            return new UnnestResult(
                formula,
                Array.Empty<UnnestStepRow>(),
                formula,
                new UnnestDiagnostic(
                    UnnestDiagnosticKind.ExistingLet,
                    "Unnest doesn't yet handle formulas that are already a LET."));
        }

        FormulaAst ast;
        try
        {
            ast = FormulaParser.Parse(formula);
        }
        catch (FormatException ex)
        {
            return new UnnestResult(
                formula,
                Array.Empty<UnnestStepRow>(),
                formula,
                new UnnestDiagnostic(
                    UnnestDiagnosticKind.MalformedFormula,
                    $"Unnest can't parse this formula: {ex.Message}"));
        }

        // Collect the step-candidate nodes leaf-first (post-order, root excluded).
        var candidates = new List<FormulaNode>();
        CollectSteps(ast.Root, isRoot: true, candidates);

        // Assign names + Include per row, honouring any dialog state.
        var stateByKey = BuildStateLookup(rowStates);
        var used = new HashSet<string>(
            definedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // Reserve every explicit user name first so auto-allocation skips them.
        for (var i = 0; i < candidates.Count; i++)
            if (stateByKey.TryGetValue(KeyOf(i), out var st) && !string.IsNullOrWhiteSpace(st.Name))
                used.Add(st.Name);

        var nameOf = new Dictionary<FormulaNode, string>();
        var included = new HashSet<FormulaNode>();
        var rows = new List<UnnestStepRow>(candidates.Count);

        for (var i = 0; i < candidates.Count; i++)
        {
            var node = candidates[i];
            var key = KeyOf(i);
            stateByKey.TryGetValue(key, out var state);

            var name = state is { } s && !string.IsNullOrWhiteSpace(s.Name)
                ? s.Name
                : AllocateName(BaseNameOf(node), used);
            var include = state?.Include ?? true;

            nameOf[node] = name;
            if (include) included.Add(node);

            // Rhs is filled in below once every step's name is known, so a
            // child reference resolves regardless of emission order.
            rows.Add(new UnnestStepRow(
                key, name, Rhs: "", OriginOf(node), OriginLabelOf(node), include));
        }

        // Now that names are assigned, render each step's RHS and the body.
        for (var i = 0; i < candidates.Count; i++)
        {
            var rhs = Render(candidates[i], nameOf, included, isRoot: true);
            rows[i] = rows[i] with { Rhs = rhs };
        }

        var includedRows = new List<UnnestStepRow>();
        foreach (var row in rows)
            if (row.Include)
                includedRows.Add(row);

        var synthesised = includedRows.Count == 0
            ? formula // No steps (or all inlined): a no-op rewrite.
            : BuildLet(includedRows, Render(ast.Root, nameOf, included, isRoot: true));

        return new UnnestResult(formula, rows, synthesised);
    }

    // ---------------- step collection ----------------

    /// <summary>
    ///     A node is a step candidate iff it is a function call or a binary
    ///     operator that isn't a reference operator (range <c>:</c> or the
    ///     space intersection). Unary / postfix nodes, parens, and leaves never
    ///     qualify.
    /// </summary>
    private static bool IsStepCandidate(FormulaNode node)
    {
        return node switch
        {
            FunctionCallNode => true,
            BinaryNode b => b.Operator != ":" && b.Operator != " ",
            _ => false
        };
    }

    /// <summary>
    ///     True when <paramref name="node" /> is a name-binding construct — a
    ///     <c>LAMBDA(...)</c> or a (nested) <c>LET(...)</c>. Both bind names
    ///     scoped to their own body: a <c>LAMBDA</c>'s parameters and a
    ///     <c>LET</c>'s bindings exist only inside the construct. Hoisting any
    ///     sub-expression out of that scope into the top-level LET would leave
    ///     those names unbound and break the formula, so the engine treats the
    ///     whole construct as opaque — it is never descended into and never
    ///     emitted as a step, staying inline in its parent's RHS verbatim.
    ///     (Top-level LET is refused earlier, so only nested LETs reach here.
    ///     Decomposing inside a lambda via a scoped nested LET is issue #279.)
    /// </summary>
    private static bool IsScopeIntroducing(FormulaNode node)
    {
        if (node is not FunctionCallNode fc) return false;
        var name = FunctionBaseName(fc.Name);
        return name is "lambda" or "let";
    }

    /// <summary>
    ///     Post-order walk appending step candidates leaf-first. The root is
    ///     never a step (it becomes the LET body), so <paramref name="isRoot" />
    ///     suppresses its own emission while still descending into its children.
    ///     A scope-introducing node (<see cref="IsScopeIntroducing" />) is opaque
    ///     — neither descended into nor emitted — so nothing escapes its scope.
    /// </summary>
    private static void CollectSteps(FormulaNode node, bool isRoot, List<FormulaNode> sink)
    {
        if (IsScopeIntroducing(node))
            return;

        foreach (var child in Children(node))
            CollectSteps(child, isRoot: false, sink);

        if (!isRoot && IsStepCandidate(node))
            sink.Add(node);
    }

    private static IEnumerable<FormulaNode> Children(FormulaNode node)
    {
        switch (node)
        {
            case FunctionCallNode fc:
                return fc.Arguments;
            case BinaryNode b:
                return new[] { b.Left, b.Right };
            case UnaryNode u:
                return new[] { u.Operand };
            case PostfixNode p:
                return new[] { p.Operand };
            case ParenNode pn:
                return pn.Items;
            default:
                return Array.Empty<FormulaNode>();
        }
    }

    // ---------------- rendering ----------------

    /// <summary>
    ///     Renders <paramref name="node" /> to expression text. A node renders to
    ///     its step name only when it is an <em>included</em> step and it isn't
    ///     the node being rendered as its own RHS (<paramref name="isRoot" />) —
    ///     so a step's RHS shows its own structure while its included children
    ///     collapse to names, and an un-included step renders through to its
    ///     structure (re-parenting its children one level up).
    /// </summary>
    private static string Render(
        FormulaNode node,
        IReadOnlyDictionary<FormulaNode, string> nameOf,
        ISet<FormulaNode> included,
        bool isRoot)
    {
        if (!isRoot && included.Contains(node))
            return nameOf[node];

        switch (node)
        {
            case LeafNode leaf:
                return leaf.Text;

            case EmptyArgNode:
                return "";

            case FunctionCallNode fc:
            {
                var sb = new StringBuilder();
                sb.Append(fc.Name).Append('(');
                for (var i = 0; i < fc.Arguments.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Render(fc.Arguments[i], nameOf, included, isRoot: false));
                }

                sb.Append(')');
                return sb.ToString();
            }

            case BinaryNode b:
            {
                var left = Render(b.Left, nameOf, included, isRoot: false);
                var right = Render(b.Right, nameOf, included, isRoot: false);
                if (b.Operator == ":")
                    return left + ":" + right;       // range — no spaces
                if (b.Operator == " ")
                    return left + " " + right;       // intersection — the space is the operator
                return left + " " + b.Operator + " " + right;
            }

            case UnaryNode u:
                return u.Operator + Render(u.Operand, nameOf, included, isRoot: false);

            case PostfixNode p:
                return Render(p.Operand, nameOf, included, isRoot: false) + p.Operator;

            case ParenNode pn:
            {
                var sb = new StringBuilder();
                sb.Append('(');
                for (var i = 0; i < pn.Items.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Render(pn.Items[i], nameOf, included, isRoot: false));
                }

                sb.Append(')');
                return sb.ToString();
            }

            default:
                // Defensive: any node type renders to its verbatim source.
                return node.ToFormula().TrimStart();
        }
    }

    private static string BuildLet(List<UnnestStepRow> steps, string body)
    {
        var bindings = new List<(string Name, string Value)>(steps.Count);
        foreach (var s in steps)
            bindings.Add((s.Name, s.Rhs));

        var sb = new StringBuilder();
        sb.Append('=');
        FormulaFormatter.AppendLet(sb, 0, bindings, body);
        return sb.ToString();
    }

    // ---------------- naming ----------------

    private static UnnestStepOrigin OriginOf(FormulaNode node)
    {
        return node is FunctionCallNode ? UnnestStepOrigin.Function : UnnestStepOrigin.Operator;
    }

    private static string OriginLabelOf(FormulaNode node)
    {
        return node switch
        {
            FunctionCallNode fc => fc.Name,
            BinaryNode b => b.Operator,
            _ => ""
        };
    }

    /// <summary>
    ///     The base name a step auto-names from: the lowercased function name for
    ///     a call (with any <c>_xlfn.</c>/<c>_xlws.</c> prefix and sheet qualifier
    ///     stripped), or the generic <c>calc</c> for an operator.
    /// </summary>
    private static string BaseNameOf(FormulaNode node)
    {
        if (node is FunctionCallNode fc)
            return FunctionBaseName(fc.Name);
        return "calc";
    }

    private static string FunctionBaseName(string fnName)
    {
        var s = fnName;

        // Drop a sheet / workbook qualifier on a scoped LAMBDA call.
        var bang = s.LastIndexOf('!');
        if (bang >= 0) s = s[(bang + 1)..];

        // Strip the future-function / worksheet-function compatibility prefixes.
        if (s.StartsWith("_xlfn._xlws.", StringComparison.OrdinalIgnoreCase))
            s = s["_xlfn._xlws.".Length..];
        else if (s.StartsWith("_xlfn.", StringComparison.OrdinalIgnoreCase))
            s = s["_xlfn.".Length..];
        else if (s.StartsWith("_xlws.", StringComparison.OrdinalIgnoreCase))
            s = s["_xlws.".Length..];

        s = s.ToLowerInvariant();
        if (s.Length == 0)
            return "fn";

        // A valid Excel name starts with a letter or underscore.
        if (!char.IsLetter(s[0]) && s[0] != '_')
            s = "_" + s;
        return s;
    }

    /// <summary>
    ///     Allocates the smallest <c>base + N</c> (N ≥ 1) not already in
    ///     <paramref name="used" />, and records it. A base used once still gets
    ///     its <c>1</c> suffix, so renumbering never shifts as the formula grows.
    ///
    ///     <para>
    ///     A short base (1–3 letters) followed by digits looks like a cell
    ///     reference — <c>SUM1</c>, <c>LOG2</c>, <c>ABS1</c> are all valid cell
    ///     addresses (the column letters are ≤ <c>XFD</c>), so Excel refuses
    ///     them as defined names and the dialog would flag the auto-name as
    ///     invalid. When <c>base + N</c> fails <see cref="ExcelNameValidator" />
    ///     for that (or any other) reason, an underscore separator is inserted
    ///     (<c>sum_1</c>) so the auto-name is always a legal name. Longer bases
    ///     (<c>sumsq1</c>, <c>sqrt1</c>, <c>calc1</c>) are unaffected.
    ///     </para>
    /// </summary>
    private static string AllocateName(string baseName, HashSet<string> used)
    {
        var n = 1;
        while (true)
        {
            var suffix = n.ToString(CultureInfo.InvariantCulture);
            var plain = baseName + suffix;
            var candidate = ExcelNameValidator.Validate(plain).IsValid
                ? plain
                : baseName + "_" + suffix;
            if (used.Add(candidate))
                return candidate;
            n++;
        }
    }

    // ---------------- keys ----------------

    private static string KeyOf(int leafFirstIndex)
    {
        return "step" + leafFirstIndex.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, UnnestRowState> BuildStateLookup(
        IReadOnlyList<UnnestRowState>? rowStates)
    {
        var map = new Dictionary<string, UnnestRowState>(StringComparer.Ordinal);
        if (rowStates is null) return map;
        foreach (var rs in rowStates)
            map[rs.Key] = rs;
        return map;
    }
}
