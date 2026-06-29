using System.Globalization;
using System.Text;

namespace LambdaBoss;

/// <summary>
///     Spec 0009 — the <c>/Unnest</c> decomposition engine (non-LET path). Takes
///     a nested formula, parses it into an expression tree via
///     <see cref="FormulaParser" />, and explodes each function-call and binary-
///     operator node (other than the root) into a named, leaf-first <c>=LET(...)</c>
///     step. The root stays as the LET body with its child references rewritten
///     to step names.
///
///     <para>
///     When the active cell's formula is <em>already</em> a <c>=LET(...)</c>
///     (issue #273), the engine takes a second path: <see cref="LetParser" />
///     splits the top-level bindings, and each <em>calculation</em> binding
///     itself becomes a toggleable step row keyed by its existing name
///     (issue #285) — so a fully-unnested LET is decomposed in <em>reverse</em>:
///     un-including a binding inlines its RHS into every downstream reference
///     (the cross-scope analogue of node-identity inlining, see
///     <see cref="Render" />), and toggling them all off collapses the LET back
///     into a single nested expression. Any <em>further</em> nesting inside a
///     calc binding's RHS (or the body) still explodes into its own sub-step,
///     inserted immediately before the binding (or body) it came from. Existing
///     <em>value</em> bindings are left untouched (they're already inputs —
///     hoisting their leaves is <c>/Refactor</c>'s job), and every existing
///     binding name is reserved so an auto-named sub-step never collides with
///     one. While a binding is included it stays a single <em>shared</em>
///     binding; only deliberately un-including it duplicates its RHS at each use
///     site (no CSE, matching the non-LET path). The default state (all bindings
///     included, nothing further to explode, no renames) returns the LET
///     verbatim — a true no-op. A malformed LET (<see cref="LetParser" /> throws)
///     is refused via <see cref="UnnestDiagnosticKind.MalformedLet" />; a calc
///     binding or body that the expression parser can't read is kept verbatim
///     (no steps).
///     </para>
///
///     Reference operators (range <c>:</c> and intersection
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
        // An existing =LET(...) is exploded binding-by-binding (issue #273);
        // everything else is a single nested expression.
        return LetParser.IsLetFormula(formula)
            ? DecomposeExistingLet(formula, rowStates, definedNames)
            : DecomposeNonLet(formula, rowStates, definedNames);
    }

    // ---------------- non-LET path ----------------

    private static UnnestResult DecomposeNonLet(
        string formula,
        IReadOnlyList<UnnestRowState>? rowStates,
        IReadOnlyCollection<string>? definedNames)
    {
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

        var used = new HashSet<string>(
            definedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var (nameOf, included, rows) = AssignAndRender(candidates, rowStates, used);

        var includedRows = rows.Where(r => r.Include).ToList();
        var synthesised = includedRows.Count == 0
            ? formula // No steps (or all inlined): a no-op rewrite.
            : BuildLet(includedRows, Render(ast.Root, nameOf, included, isRoot: true));

        return new UnnestResult(formula, rows, synthesised);
    }

    // ---------------- existing-LET path (issue #273) ----------------

    /// <summary>
    ///     Decomposes an existing <c>=LET(...)</c> bidirectionally (issues #273,
    ///     #285). The LET is split into top-level bindings by
    ///     <see cref="LetParser" /> (a <see cref="FormatException" /> there is
    ///     refused via <see cref="UnnestDiagnosticKind.MalformedLet" />). Every
    ///     <em>calculation</em> binding becomes a toggleable step row under its
    ///     existing name, and any further nesting inside its RHS (or the body)
    ///     explodes into extra sub-step rows inserted just before it. Value
    ///     bindings, binding names, and binding order are preserved. The combined
    ///     step order — for each calc binding, its sub-steps leaf-first then the
    ///     binding itself, then the body's sub-steps — is fixed by the source, so
    ///     each step's key is stable across <see cref="Recompute" />.
    ///
    ///     <para>
    ///     Including a binding emits it (and a reference to it renders to its
    ///     name); un-including it inlines its RHS at every downstream reference
    ///     (see <see cref="Render" />'s cross-scope branch), and un-including all
    ///     of them collapses the LET to a single nested expression. When nothing
    ///     further is exploded and every binding is included with its original
    ///     name, the LET is returned verbatim — a true no-op (no cosmetic
    ///     re-spacing). A calc binding or body the expression parser can't read
    ///     is kept verbatim and contributes no steps.
    ///     </para>
    /// </summary>
    private static UnnestResult DecomposeExistingLet(
        string formula,
        IReadOnlyList<UnnestRowState>? rowStates,
        IReadOnlyCollection<string>? definedNames)
    {
        ParsedLet parsed;
        try
        {
            parsed = LetParser.Parse(formula);
        }
        catch (FormatException ex)
        {
            return new UnnestResult(
                formula,
                Array.Empty<UnnestStepRow>(),
                formula,
                new UnnestDiagnostic(
                    UnnestDiagnosticKind.MalformedLet,
                    $"Could not parse LET formula: {ex.Message}"));
        }

        var bindings = parsed.Bindings;

        // Build the ordered step list. For each calc binding: its nested
        // sub-steps (leaf-first) followed by the binding-step itself; then the
        // body's sub-steps. Value (and unparseable) bindings contribute none.
        // The order is fully determined by the source, so step keys are stable.
        var calcRootByIndex = new Dictionary<int, FormulaNode>();
        var calcSubsByIndex = new Dictionary<int, List<FormulaNode>>();
        var descs = new List<StepDesc>();

        for (var i = 0; i < bindings.Count; i++)
        {
            if (!bindings[i].IsCalculation) continue;
            var root = TryParseScope(bindings[i].RhsText);
            if (root is null) continue; // Unparseable RHS — kept verbatim, no steps.

            var subs = new List<FormulaNode>();
            CollectSteps(root, isRoot: true, subs);
            calcRootByIndex[i] = root;
            calcSubsByIndex[i] = subs;
            foreach (var s in subs) descs.Add(new StepDesc(s, -1));
            descs.Add(new StepDesc(root, i)); // the binding itself
        }

        var bodyRoot = TryParseScope(parsed.Body);
        var bodySubs = new List<FormulaNode>();
        if (bodyRoot is not null)
            CollectSteps(bodyRoot, isRoot: true, bodySubs);
        foreach (var s in bodySubs) descs.Add(new StepDesc(s, -1));

        // Reserve defined names, every existing binding name, and any explicit
        // user rename so an auto-named sub-step never shadows one.
        var stateByKey = BuildStateLookup(rowStates);
        var used = new HashSet<string>(
            definedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var b in bindings)
            used.Add(b.Name);
        for (var i = 0; i < descs.Count; i++)
            if (stateByKey.TryGetValue(KeyOf(i), out var st) && !string.IsNullOrWhiteSpace(st.Name))
                used.Add(st.Name);

        // Assign each step a name + Include flag, recording sub-steps by node
        // identity and binding-steps by their existing name for the renderer.
        var subNameOf = new Dictionary<FormulaNode, string>();
        var subIncluded = new HashSet<FormulaNode>();
        var bindingByName = new Dictionary<string, BindingStep>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<UnnestStepRow>(descs.Count);

        for (var i = 0; i < descs.Count; i++)
        {
            var d = descs[i];
            var key = KeyOf(i);
            stateByKey.TryGetValue(key, out var state);
            var include = state?.Include ?? true;
            var renamed = state is { } s && !string.IsNullOrWhiteSpace(s.Name) ? s.Name : null;

            string name;
            if (d.IsBinding)
            {
                // Preserve the existing binding name (the author's choice) unless
                // the dialog explicitly renamed it.
                name = renamed ?? bindings[d.BindingIndex].Name;
                bindingByName[bindings[d.BindingIndex].Name] = new BindingStep(name, include, d.Node);
            }
            else
            {
                name = renamed ?? AllocateName(BaseNameOf(d.Node), used);
                subNameOf[d.Node] = name;
                if (include) subIncluded.Add(d.Node);
            }

            rows.Add(new UnnestStepRow(
                key, name, Rhs: "", OriginOf(d.Node), OriginLabelOf(d.Node), include));
        }

        // Fill each row's RHS now that every name + binding mapping is known.
        for (var i = 0; i < descs.Count; i++)
            rows[i] = rows[i] with
            {
                Rhs = Render(descs[i].Node, subNameOf, subIncluded, isRoot: true, bindingByName)
            };

        // No structural change — return the LET verbatim (preserve the no-op
        // contract). A change is any exploded sub-step, any un-included binding,
        // or any rename away from an original binding name.
        var changed = descs.Any(d => !d.IsBinding) || rows.Any(r => !r.Include);
        for (var i = 0; !changed && i < descs.Count; i++)
            if (descs[i].IsBinding && rows[i].Name != bindings[descs[i].BindingIndex].Name)
                changed = true;

        if (!changed)
            return new UnnestResult(formula, rows, formula);

        // Fully nested → no LET. When no step is kept (every binding-step and
        // sub-step inlined) the result is a single bare expression — and a bare
        // formula can't carry named inputs, so the value bindings are inlined
        // too (re-hoisting their leaves is /Refactor's job). The LET reappears
        // the moment one step is included. This makes a non-LET formula a true
        // round-trip: unnest → re-unnest → inline-all returns the original.
        if (!rows.Any(r => r.Include))
        {
            var inlineAll = new Dictionary<string, BindingStep>(
                bindingByName, StringComparer.OrdinalIgnoreCase);
            foreach (var b in bindings)
                if (!inlineAll.ContainsKey(b.Name)) // a value (or unparseable) binding
                    inlineAll[b.Name] = new BindingStep(
                        b.Name, include: false, TryParseScope(b.RhsText), b.RhsText);

            var collapsed = bodyRoot is not null
                ? Render(bodyRoot, subNameOf, subIncluded, isRoot: true, inlineAll)
                : parsed.Body;
            return new UnnestResult(formula, rows, "=" + collapsed);
        }

        // Emit value bindings verbatim and, for each calc binding, its included
        // sub-steps then the binding itself (skipped when inlined); then the
        // body's included sub-steps; then the body.
        var letBindings = new List<(string Name, string Value)>();
        for (var i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];
            if (!b.IsCalculation || !calcRootByIndex.TryGetValue(i, out var root))
            {
                letBindings.Add((b.Name, b.RhsText));
                continue;
            }

            foreach (var sub in calcSubsByIndex[i])
                if (subIncluded.Contains(sub))
                    letBindings.Add((subNameOf[sub], Render(sub, subNameOf, subIncluded, isRoot: true, bindingByName)));

            var bs = bindingByName[b.Name];
            if (bs.Include)
                letBindings.Add((bs.Name, Render(root, subNameOf, subIncluded, isRoot: true, bindingByName)));
        }

        foreach (var sub in bodySubs)
            if (subIncluded.Contains(sub))
                letBindings.Add((subNameOf[sub], Render(sub, subNameOf, subIncluded, isRoot: true, bindingByName)));

        var bodyText = bodyRoot is not null
            ? Render(bodyRoot, subNameOf, subIncluded, isRoot: true, bindingByName)
            : parsed.Body;

        // Every binding inlined and no value bindings left → a bare nested
        // formula (a LET with no bindings would be malformed).
        var synthesised = letBindings.Count == 0
            ? "=" + bodyText
            : BuildLetFromBindings(letBindings, bodyText);

        return new UnnestResult(formula, rows, synthesised);
    }

    /// <summary>
    ///     Parses a calc-binding RHS or LET body (expression text, no leading
    ///     <c>=</c>) into its root node, or returns null when the expression
    ///     parser can't read it — in which case the caller keeps the text
    ///     verbatim rather than failing the whole explosion.
    /// </summary>
    private static FormulaNode? TryParseScope(string text)
    {
        try
        {
            return FormulaParser.Parse(text).Root;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Assigns a name and Include flag to each step candidate (honouring any
    ///     dialog <paramref name="rowStates" />), then renders each candidate's
    ///     RHS with its included children collapsed to names. <paramref name="used" />
    ///     is the pre-seeded set of reserved names (defined names, plus existing
    ///     binding names on the existing-LET path) the auto-namer avoids; explicit
    ///     user names are reserved on top before auto-allocation so a rename never
    ///     collides with an auto suffix. Candidate order is the engine's leaf-first
    ///     order, so <c>step</c><i>N</i> keys stay stable across Recompute.
    /// </summary>
    private static (Dictionary<FormulaNode, string> NameOf,
        HashSet<FormulaNode> Included,
        List<UnnestStepRow> Rows) AssignAndRender(
            IReadOnlyList<FormulaNode> candidates,
            IReadOnlyList<UnnestRowState>? rowStates,
            HashSet<string> used)
    {
        var stateByKey = BuildStateLookup(rowStates);

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

        for (var i = 0; i < candidates.Count; i++)
            rows[i] = rows[i] with { Rhs = Render(candidates[i], nameOf, included, isRoot: true) };

        return (nameOf, included, rows);
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
    ///
    ///     <para>
    ///     On the existing-LET path <paramref name="bindings" /> maps an existing
    ///     binding's name to its <see cref="BindingStep" />, so a <em>reference</em>
    ///     to that binding (a leaf whose text is the binding name) resolves
    ///     <em>across scopes</em>: it renders to the binding's current name when
    ///     the binding-step is included, or inlines (renders through) the
    ///     binding's RHS when it isn't — the cross-scope analogue of the
    ///     node-identity inlining above. The map is null on the non-LET path,
    ///     where every leaf renders verbatim.
    ///     </para>
    /// </summary>
    private static string Render(
        FormulaNode node,
        IReadOnlyDictionary<FormulaNode, string> nameOf,
        ISet<FormulaNode> included,
        bool isRoot,
        IReadOnlyDictionary<string, BindingStep>? bindings = null)
    {
        if (!isRoot && included.Contains(node))
            return nameOf[node];

        switch (node)
        {
            case LeafNode leaf:
                // A reference to an existing LET binding: keep its name when the
                // binding-step is included, else inline its RHS (cross-scope).
                if (bindings is not null && bindings.TryGetValue(leaf.Text, out var bs))
                {
                    if (bs.Include)
                        return bs.Name;
                    return bs.Root is not null
                        ? Render(bs.Root, nameOf, included, isRoot: true, bindings)
                        : bs.RawText ?? leaf.Text;
                }

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
                    sb.Append(Render(fc.Arguments[i], nameOf, included, isRoot: false, bindings));
                }

                sb.Append(')');
                return sb.ToString();
            }

            case BinaryNode b:
            {
                var left = Render(b.Left, nameOf, included, isRoot: false, bindings);
                var right = Render(b.Right, nameOf, included, isRoot: false, bindings);
                if (b.Operator == ":")
                    return left + ":" + right;       // range — no spaces
                if (b.Operator == " ")
                    return left + " " + right;       // intersection — the space is the operator
                return left + " " + b.Operator + " " + right;
            }

            case UnaryNode u:
                return u.Operator + Render(u.Operand, nameOf, included, isRoot: false, bindings);

            case PostfixNode p:
                return Render(p.Operand, nameOf, included, isRoot: false, bindings) + p.Operator;

            case ParenNode pn:
            {
                var sb = new StringBuilder();
                sb.Append('(');
                for (var i = 0; i < pn.Items.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Render(pn.Items[i], nameOf, included, isRoot: false, bindings));
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
        return BuildLetFromBindings(bindings, body);
    }

    private static string BuildLetFromBindings(
        IReadOnlyList<(string Name, string Value)> bindings, string body)
    {
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

    // ---------------- existing-LET step model (issue #285) ----------------

    /// <summary>
    ///     One entry in the existing-LET step list: either a sub-step exploded
    ///     from inside a binding (or the body), identified by its
    ///     <see cref="Node" />, or an existing calc binding promoted to a step
    ///     (<see cref="BindingIndex" /> &gt;= 0), whose <see cref="Node" /> is the
    ///     binding's RHS root.
    /// </summary>
    private readonly struct StepDesc
    {
        public StepDesc(FormulaNode node, int bindingIndex)
        {
            Node = node;
            BindingIndex = bindingIndex;
        }

        public FormulaNode Node { get; }
        public int BindingIndex { get; }
        public bool IsBinding => BindingIndex >= 0;
    }

    /// <summary>
    ///     A promoted existing-LET binding for the renderer's cross-scope lookup:
    ///     its current (possibly renamed) <see cref="Name" />, whether it is
    ///     <see cref="Include" />d (kept as a binding) or inlined at use sites,
    ///     and its RHS as a parsed <see cref="Root" /> (rendered when inlining).
    ///     Value bindings folded in for a full collapse may carry only
    ///     <see cref="RawText" /> when their RHS doesn't parse — inlined verbatim.
    /// </summary>
    private sealed class BindingStep
    {
        public BindingStep(string name, bool include, FormulaNode? root, string? rawText = null)
        {
            Name = name;
            Include = include;
            Root = root;
            RawText = rawText;
        }

        public string Name { get; }
        public bool Include { get; }
        public FormulaNode? Root { get; }
        public string? RawText { get; }
    }
}
