using System.Text;
using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     A fold <c>/State</c> can wrap a step in (spec 0015): <c>REDUCE</c>, <c>SCAN</c>, or a library
///     fold whose header metadata (spec 0013) shows a seed, an input and a step LAMBDA. The
///     positions are the fold's own argument order; <paramref name="StepParameters" /> is what the
///     step is called with (<c>acc, current, i</c>).
/// </summary>
public sealed record StateFoldKind(
    string Name,
    int SeedIndex,
    int InputsIndex,
    int StepIndex,
    IReadOnlyList<string> StepParameters,
    string? Library)
{
    private static readonly Regex StepSignature = new(@"^\s*LAMBDA\s*\(([^()]*)\)", RegexOptions.IgnoreCase);

    public static readonly StateFoldKind Reduce = new("REDUCE", 0, 1, 2, new[] { "a", "v" }, null);

    public static readonly StateFoldKind Scan = new("SCAN", 0, 1, 2, new[] { "a", "v" }, null);

    public bool IsNative => Library == null;

    /// <summary>The number of arguments the fold needs written: up to its last seed, inputs or step slot.</summary>
    public int WrittenArguments => Math.Max(SeedIndex, Math.Max(InputsIndex, StepIndex)) + 1;

    /// <summary>
    ///     The fold <paramref name="doc" /> describes, or null when it isn't one. A fold has a
    ///     parameter named <c>seed</c>, a parameter documented as <c>LAMBDA(x, y, …)</c> with at
    ///     least two names (the step), and another required parameter (the inputs).
    /// </summary>
    public static StateFoldKind? FromDoc(LambdaDoc doc, string? library)
    {
        if (doc is null) throw new ArgumentNullException(nameof(doc));

        var ps = doc.Parameters;
        var seed = IndexWhere(ps, p => string.Equals(p.Name, "seed", StringComparison.OrdinalIgnoreCase));
        if (seed < 0) return null;

        var step = -1;
        IReadOnlyList<string> stepParameters = Array.Empty<string>();
        for (var i = 0; i < ps.Count && step < 0; i++)
        {
            var m = StepSignature.Match(ps[i].Description);
            if (!m.Success) continue;
            var names = m.Groups[1].Value.Split(',').Select(n => n.Trim().Trim('[', ']')).Where(n => n.Length > 0).ToList();
            if (names.Count < 2 || !names.All(n => ExcelNameValidator.Validate(n).IsValid)) continue;
            step = i;
            stepParameters = names;
        }
        if (step < 0 || step == seed) return null;

        var inputs = IndexWhere(ps, p => p.Required, skip: new[] { seed, step });
        if (inputs < 0) return null;

        return new StateFoldKind(doc.Name, seed, inputs, step, stepParameters, library ?? "");
    }

    private static int IndexWhere(IReadOnlyList<LambdaParamDoc> ps, Func<LambdaParamDoc, bool> test, int[]? skip = null)
    {
        for (var i = 0; i < ps.Count; i++)
            if ((skip == null || !skip.Contains(i)) && test(ps[i]))
                return i;
        return -1;
    }
}

/// <summary>
///     One state engine as <c>/State</c> sees it, whatever form the cell holds it in (spec 0015).
///     <see cref="Fold" /> null is the unfolded LET step. <see cref="Parameters" /> are the step's
///     own names for the state, the input and any extra arguments its fold passes (<c>i</c>).
///     <see cref="Bindings" /> and <see cref="Output" /> are the step's rules, never rewritten
///     except by a variable edit. <see cref="Before" /> and <see cref="After" /> are the cell's text
///     around the fold call (<c>=</c> and empty when the fold is the whole formula).
/// </summary>
public sealed record StateEngine(
    StateFoldKind? Fold,
    string? FoldCallName,
    string? StepName,
    string Seed,
    string Inputs,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<(string Name, string Value)> Bindings,
    string Output,
    string Prefix,
    string Before,
    string After,
    IReadOnlyList<(int Index, string Text)> OtherArguments,
    string? Note)
{
    /// <summary>The placeholder for a value Tim types over.</summary>
    public const string Placeholder = "\"\"";

    /// <summary>The placeholder inputs of a new engine.</summary>
    public const string NewInputs = "SEQUENCE(1)";

    public bool IsWholeFormula => Before.Trim() == "=" && After.Trim().Length == 0;

    /// <summary>The variables, in order.</summary>
    public IReadOnlyList<StateEditRow> Rows =>
        IsNew
            ? Array.Empty<StateEditRow>()
            : StateEdit.Read(StateEngines.Intermediate(this))?.Rows ?? (IReadOnlyList<StateEditRow>)Array.Empty<StateEditRow>();

    /// <summary>A new engine for an empty cell: no variables yet, a REDUCE with placeholders.</summary>
    public static StateEngine New(string prefix) => new(
        StateFoldKind.Reduce, "REDUCE", null, "", NewInputs,
        new[] { "a", "v" }, Array.Empty<(string, string)>(), "", prefix, "=", "",
        Array.Empty<(int, string)>(), null);

    public bool IsNew => Output.Length == 0;
}

/// <summary>What <see cref="StateEngines.Read" /> found in a cell.</summary>
public sealed record StateEngineRead(StateEngine? Engine, bool IsEmpty, string? Refusal);

/// <summary>What the dialog asks for: the variables, the form (null = LET step) and the name (null = inline).</summary>
public sealed record StateEngineChange(
    IReadOnlyList<StateEditRow> Rows,
    StateFoldKind? Fold,
    string? StepName);

/// <summary>
///     The cell after a change, and the named LAMBDA to store when the step is named. Null
///     <paramref name="Formula" /> means the change can't be made, and <paramref name="Error" />
///     says why.
/// </summary>
public sealed record StateEngineResult(
    string? Formula,
    string? DefinitionName,
    string? Definition,
    IReadOnlyList<string> Notes,
    string? Error,
    IReadOnlyList<string> Blockers);

/// <summary>
///     Reads a state engine from any form and writes it in any other (spec 0015, lambda-boss#429):
///     an inline fold, a fold calling a named step, or the unfolded LET step with <c>_seed</c> and
///     <c>_inputs</c>. Pure text. Variables are edited by the 0011 engine (<see cref="StateEdit" />)
///     on an intermediate LET whose state binding holds the seed, so the seed, the GetVar lines,
///     the next lines and the output move together and the rules are never touched.
/// </summary>
public static class StateEngines
{
    public const string SeedSlot = "_seed";
    public const string InputsSlot = "_inputs";

    private const int MaxFormulaLength = 8192;
    private const string NotAStep =
        "The active cell holds a formula that isn't a state step, so /State can't edit it. Pick an empty cell for a new one.";

    /// <summary>Reads the active cell's formula.</summary>
    /// <param name="formula">The cell's formula (Formula2).</param>
    /// <param name="refersTo">A workbook name's definition, or null.</param>
    /// <param name="kinds">The library folds known from metadata (REDUCE and SCAN are always known).</param>
    public static StateEngineRead Read(string? formula, Func<string, string?> refersTo, IReadOnlyList<StateFoldKind> kinds)
    {
        if (refersTo is null) throw new ArgumentNullException(nameof(refersTo));
        if (kinds is null) throw new ArgumentNullException(nameof(kinds));
        if (string.IsNullOrWhiteSpace(formula)) return new StateEngineRead(null, true, null);

        try
        {
            // A LET step comes first: the named form holds a SCAN of the step inside it.
            var letStep = ReadLetStep(formula!, refersTo, out var letRefusal);
            if (letStep != null) return new StateEngineRead(letStep, false, null);
            if (letRefusal != null) return new StateEngineRead(null, false, letRefusal);

            var root = FormulaParser.Parse(formula!).Root;
            var calls = StateStepParser.Walk(root).OfType<FunctionCallNode>()
                .Select(c => (Call: c, Kind: KindOf(c, kinds)))
                .Where(c => c.Kind != null && c.Call.Arguments.Count >= c.Kind.WrittenArguments
                    && c.Call.Arguments[c.Kind.StepIndex] is not EmptyArgNode)
                .ToList();

            string? refusal = null;
            var found = new List<StateEngine>();
            foreach (var (call, kind) in calls)
            {
                var engine = ReadFold(formula!, call, kind!, refersTo, out var why);
                if (engine != null) found.Add(engine);
                refusal ??= why;
            }

            if (found.Count == 0) return new StateEngineRead(null, false, refusal ?? NotAStep);

            var first = found[0];
            if (found.Count > 1)
                first = first with
                {
                    Note = $"This cell has {found.Count} state folds; /State works on the first, {first.FoldCallName}."
                };
            return new StateEngineRead(first, false, null);
        }
        catch (FormatException)
        {
            return new StateEngineRead(null, false, NotAStep);
        }
    }

    /// <summary>Applies the dialog's change: variables first, then the form and the name.</summary>
    public static StateEngineResult Apply(
        StateEngine engine,
        StateEngineChange change,
        Func<string, string?> refersTo,
        Func<StateFoldKind, string> callName)
    {
        if (engine is null) throw new ArgumentNullException(nameof(engine));
        if (change is null) throw new ArgumentNullException(nameof(change));
        if (refersTo is null) throw new ArgumentNullException(nameof(refersTo));
        if (callName is null) throw new ArgumentNullException(nameof(callName));

        var notes = new List<string>();
        if (engine.Note != null) notes.Add(engine.Note);

        try
        {
            // 1. Variables.
            string? editError;
            IReadOnlyList<string> blockers = Array.Empty<string>();
            var edited = engine.IsNew
                ? Create(engine, change.Rows, out editError)
                : Edit(engine, change.Rows, out editError, out blockers);
            if (edited == null) return Failed(editError ?? "Can't apply the variables.", notes, blockers);

            // 2. Name.
            var name = string.IsNullOrWhiteSpace(change.StepName) ? null : change.StepName!.Trim();
            if (name != null)
            {
                var nameError = CheckName(name, edited.StepName, refersTo, notes);
                if (nameError != null) return Failed(nameError, notes);
            }
            else if (edited.StepName != null)
            {
                notes.Add($"{edited.StepName} stays in the workbook: another cell may use it. Delete it in Name Manager if not.");
            }

            // 3. Form.
            if (change.Fold == null && !engine.IsWholeFormula)
                return Failed("The fold sits inside a larger formula, so it can't unfold here. Put the fold in a cell of its own first.", notes);

            var parameters = ParametersFor(edited, change.Fold, out var bindings, out var paramError);
            if (paramError != null) return Failed(paramError, notes);

            if (change.Fold != null && engine.Fold != null && !SameKind(change.Fold, engine.Fold) && engine.OtherArguments.Count > 0)
                notes.Add($"{engine.FoldCallName}'s other arguments ({string.Join(", ", engine.OtherArguments.Select(a => a.Text))}) aren't written by {change.Fold.Name}.");

            var shaped = edited with { Parameters = parameters, Bindings = bindings, StepName = name };

            string? definition = null;
            if (name != null)
                definition = Definition(shaped);

            var formula = change.Fold == null
                ? LetStep(shaped)
                : FoldFormula(shaped, change.Fold, engine, callName);

            if (formula.Length > MaxFormulaLength)
                return Failed($"The cell would be over {MaxFormulaLength:N0} characters, Excel's limit.", notes);
            if (definition != null && definition.Length > MaxFormulaLength)
                return Failed($"The step would be over {MaxFormulaLength:N0} characters, Excel's limit for a name.", notes);

            // What goes in the cell has to read back.
            FormulaParser.Parse(formula);
            return new StateEngineResult(formula, name, definition, notes, null, Array.Empty<string>());
        }
        catch (FormatException ex)
        {
            return Failed($"Can't read the step: {ex.Message}", notes);
        }
        catch (InvalidOperationException ex)
        {
            return Failed(ex.Message, notes);
        }
    }

    private static StateEngineResult Failed(string error, List<string> notes, IReadOnlyList<string>? blockers = null) =>
        new(null, null, null, notes, error, blockers ?? Array.Empty<string>());

    /// <summary>A short description of the form, for the dialog's header.</summary>
    public static string Describe(StateEngine engine)
    {
        if (engine is null) throw new ArgumentNullException(nameof(engine));
        if (engine.IsNew) return "Empty cell: a new state engine.";
        var form = engine.Fold == null ? "Unfolded LET step" : $"{engine.FoldCallName} fold";
        var step = engine.StepName == null ? "inline step" : $"named step {engine.StepName}";
        return $"{form}, {step}.";
    }

    // ---------------------------------------------------------------- reading

    private static StateFoldKind? KindOf(FunctionCallNode call, IReadOnlyList<StateFoldKind> kinds)
    {
        if (!call.Name.Contains('.'))
        {
            if (string.Equals(call.Name, "REDUCE", StringComparison.OrdinalIgnoreCase)) return StateFoldKind.Reduce;
            if (string.Equals(call.Name, "SCAN", StringComparison.OrdinalIgnoreCase)) return StateFoldKind.Scan;
        }

        return kinds.FirstOrDefault(k => !k.IsNative && StateStepParser.IsNamed(call.Name, k.Name));
    }

    private static bool SameKind(StateFoldKind a, StateFoldKind b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private static StateEngine? ReadFold(
        string formula, FunctionCallNode call, StateFoldKind kind, Func<string, string?> refersTo, out string? refusal)
    {
        refusal = null;
        var args = call.Arguments;
        var stepArg = args[kind.StepIndex];

        IReadOnlyList<string> parameters;
        ParsedLet body;
        string? stepName = null;

        if (stepArg is FunctionCallNode lambda && string.Equals(lambda.Name, "LAMBDA", StringComparison.OrdinalIgnoreCase))
        {
            if (lambda.Arguments.Count < 3) return null;
            var names = lambda.Arguments.Take(lambda.Arguments.Count - 1)
                .Select(a => a is LeafNode { TokenType: FormulaTokenType.Name } leaf ? leaf.Text.Trim('[', ']') : null)
                .ToList();
            if (names.Any(n => n == null)) return null;
            if (lambda.Arguments[^1] is not FunctionCallNode let
                || !string.Equals(let.Name, "LET", StringComparison.OrdinalIgnoreCase)) return null;
            parameters = names!;
            body = LetParser.Parse("=" + let.ToFormula().Trim());
        }
        else if (stepArg is LeafNode { TokenType: FormulaTokenType.Name } leaf && !leaf.Text.Contains('!'))
        {
            var definition = refersTo(leaf.Text);
            if (!LambdaSignatureParser.IsLambdaFormula(definition)) return null;
            var signature = LambdaSignatureParser.Parse(definition!);
            if (!LetParser.IsLetFormula("=" + signature.Body)) return null;
            parameters = signature.Parameters;
            body = LetParser.Parse("=" + signature.Body);
            stepName = leaf.Text;
        }
        else
        {
            return null;
        }

        if (parameters.Count < 2) return null;

        var seed = kind.SeedIndex < args.Count ? args[kind.SeedIndex].ToFormula().Trim() : "";
        var inputs = args[kind.InputsIndex].ToFormula().Trim();
        var others = args
            .Select((a, i) => (Index: i, Text: a.ToFormula().Trim()))
            .Where(a => a.Index != kind.SeedIndex && a.Index != kind.InputsIndex && a.Index != kind.StepIndex
                && a.Text.Length > 0)
            .ToList();

        var engine = new StateEngine(
            kind, call.Name, stepName, seed, inputs, parameters,
            body.Bindings.Select(b => (b.Name, b.RhsText)).ToList(), body.Body,
            "", formula[..call.Start], formula[call.End..], others, null);

        refusal = CheckStep(engine);
        return refusal == null ? engine with { Prefix = PrefixOf(engine.Output) } : null;
    }

    /// <summary>
    ///     The unfolded form: a whole-cell LET that is a state step. <c>_seed</c> and <c>_inputs</c>
    ///     hold the fold's arguments; a stepper (<c>IF(k = 1, _seed, INDEX(SCAN(…, step), k - 1))</c>)
    ///     names the step. A 0011 step (<c>a, SetVars(…)</c>, <c>v, H270</c>) reads too.
    /// </summary>
    private static StateEngine? ReadLetStep(string formula, Func<string, string?> refersTo, out string? refusal)
    {
        refusal = null;
        if (!IsWholeLet(formula)) return null;

        var parsed = LetParser.Parse(formula);
        var step = StateStepParser.TryParse(parsed);
        if (step == null) return null;
        if (step.Family == StateFamily.Array)
        {
            refusal = ArrayRefusal;
            return null;
        }
        if (step.InputBinding == null) return null;

        var byName = parsed.Bindings.ToDictionary(b => b.Name, b => b.RhsText, StringComparer.OrdinalIgnoreCase);
        var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { step.StateBinding.Name, step.InputBinding.Name };

        var seedText = step.StateBinding.RhsText;
        string? inputsText = null;
        string? stepName = null;

        var stateRoot = FormulaParser.Parse(seedText).Root;
        if (AsStepper(stateRoot) is { } stepper)
        {
            seedText = stepper.Seed;
            stepName = stepper.StepName;
            inputsText = stepper.Inputs;
            drop.Add(stepper.Counter);
            if (stepName != null && !LambdaSignatureParser.IsLambdaFormula(refersTo(stepName))) stepName = null;
        }

        var inputRoot = FormulaParser.Parse(step.InputBinding.RhsText).Root;
        if (inputRoot is FunctionCallNode index && StateStepParser.IsNamed(index.Name, "INDEX") && index.Arguments.Count == 2)
            inputsText ??= index.Arguments[0].ToFormula().Trim();
        else
            inputsText ??= step.InputBinding.RhsText;

        string Resolve(string text)
        {
            if (byName.TryGetValue(text.Trim(), out var rhs) && !drop.Contains(text.Trim()))
            {
                drop.Add(text.Trim());
                return rhs;
            }
            return text;
        }

        var seed = Resolve(seedText);
        var inputs = Resolve(inputsText);

        // Lines that only fed the sample (the 0011 stepper's k, a slot nothing else reads) go with it.
        var bindings = parsed.Bindings.Where(b => !drop.Contains(b.Name)).Select(b => (b.Name, b.RhsText)).ToList();
        var parameters = new List<string> { step.StateBinding.Name, step.InputBinding.Name };

        // A named step's extra arguments (i) are lines in the unfolded form: they're its parameters again.
        if (stepName != null)
        {
            foreach (var extra in LambdaSignatureParser.Parse(refersTo(stepName)!).Parameters.Skip(2))
            {
                var at = bindings.FindIndex(b => string.Equals(b.Name, extra, StringComparison.OrdinalIgnoreCase));
                if (at >= 0 && IsCounterValue(bindings[at].RhsText))
                {
                    parameters.Add(bindings[at].Name);
                    bindings.RemoveAt(at);
                }
                else
                {
                    parameters.Add(extra);
                }
            }
        }

        var engine = new StateEngine(
            null, null, stepName, seed, inputs, parameters,
            bindings, parsed.Body, PrefixOf(parsed.Body), "=", "", Array.Empty<(int, string)>(), null);

        var leftover = NamesIn(
            bindings.Select(b => b.RhsText).Append(parsed.Body),
            drop.Where(d => !engine.Parameters.Contains(d, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (leftover.Count > 0)
        {
            refusal = $"The rules read {string.Join(", ", leftover)}, which only the sample should use. Rename it first.";
            return null;
        }

        refusal = CheckStep(engine);
        return refusal == null ? engine : null;
    }

    private sealed record Stepper(string Counter, string Seed, string? Inputs, string? StepName);

    /// <summary>
    ///     <c>IF(k = 1, seed, INDEX(SCAN(seed, inputs, step), k - 1))</c>, or the same with
    ///     <c>SCAN(seed, SEQUENCE(k - 1), LAMBDA(_s, _j, step(…)))</c> for a step with extra arguments.
    /// </summary>
    private static Stepper? AsStepper(FormulaNode root)
    {
        if (root is not FunctionCallNode iff || !StateStepParser.IsNamed(iff.Name, "IF") || iff.Arguments.Count != 3) return null;
        if (iff.Arguments[0] is not BinaryNode { Operator: "=" } test
            || test.Left is not LeafNode { TokenType: FormulaTokenType.Name } counter
            || test.Right is not LeafNode { TokenType: FormulaTokenType.Number, Text: "1" }) return null;

        var scan = StateStepParser.Walk(iff.Arguments[2]).OfType<FunctionCallNode>()
            .FirstOrDefault(c => StateStepParser.IsNamed(c.Name, "SCAN") && c.Arguments.Count == 3);
        if (scan == null) return null;

        string? stepName = null;
        string? inputs = scan.Arguments[1].ToFormula().Trim();
        switch (scan.Arguments[2])
        {
            case LeafNode { TokenType: FormulaTokenType.Name } name:
                stepName = name.Text;
                break;
            case FunctionCallNode lambda when StateStepParser.IsNamed(lambda.Name, "LAMBDA"):
                stepName = (lambda.Arguments[^1] as FunctionCallNode)?.Name;
                inputs = null;
                break;
        }

        return new Stepper(counter.Text, iff.Arguments[1].ToFormula().Trim(), inputs, stepName);
    }

    private const string ArrayRefusal = "/State works on SetVars states. This step holds its state in an array (HSTACK).";

    private static string? CheckStep(StateEngine engine)
    {
        var text = Intermediate(engine);
        var step = StateStepParser.TryParse(LetParser.Parse(text));
        if (step?.Family == StateFamily.Array) return ArrayRefusal;
        if (step == null || !string.Equals(step.StateBinding.Name, engine.Parameters[0], StringComparison.OrdinalIgnoreCase))
            return NotAStep;
        return StateEdit.Read(text) is { } view ? view.Refusal : NotAStep;
    }

    private static string PrefixOf(string output)
    {
        try
        {
            return FormulaParser.Parse(output).Root is FunctionCallNode call
                ? call.Name[..(call.Name.LastIndexOf('.') + 1)]
                : "";
        }
        catch (FormatException)
        {
            return "";
        }
    }

    private static bool IsWholeLet(string formula)
    {
        if (!LetParser.IsLetFormula(formula)) return false;
        var close = LetParser.FindMatchingClose(formula, formula.IndexOf('('));
        return close >= 0 && formula[(close + 1)..].Trim().Length == 0;
    }

    // ---------------------------------------------------------------- variables

    /// <summary>
    ///     The step as a 0011 state step: the state parameter bound to the seed, the input and any
    ///     extra parameter bound to a stand-in, then the rules. <see cref="StateEdit" /> works on this.
    /// </summary>
    internal static string Intermediate(StateEngine engine)
    {
        var lines = new List<(string Name, string Value)>
        {
            (engine.Parameters[0], engine.Seed.Length == 0 ? StateEngine.Placeholder : engine.Seed)
        };
        lines.AddRange(engine.Parameters.Skip(1).Select(p => (p, "0")));
        lines.AddRange(engine.Bindings);

        var sb = new StringBuilder("=");
        FormulaFormatter.AppendLet(sb, 0, lines, engine.Output);
        return sb.ToString();
    }

    private static StateEngine? Edit(StateEngine engine, IReadOnlyList<StateEditRow> rows, out string? error, out IReadOnlyList<string> blockers)
    {
        blockers = Array.Empty<string>();
        var rowsWithPlaceholders = rows
            .Select(r => r.OriginalKey == null && r.Key.Trim().Length > 0 && r.Value.Trim().Length == 0
                ? r with { Value = StateEngine.Placeholder }
                : r)
            .ToList();

        var result = StateEdit.Apply(Intermediate(engine), rowsWithPlaceholders);
        if (result.Formula == null)
        {
            error = result.Error;
            blockers = result.Blockers;
            return null;
        }

        error = null;
        var parsed = LetParser.Parse(result.Formula);
        var paramLines = parsed.Bindings.Take(engine.Parameters.Count).ToList();
        if (!paramLines.Select(b => b.Name).SequenceEqual(engine.Parameters, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The edit moved the step's parameters; nothing was changed.");

        var seed = paramLines[0].RhsText;
        return engine with
        {
            Seed = engine.Seed.Length == 0 && seed == StateEngine.Placeholder ? "" : seed,
            Bindings = parsed.Bindings.Skip(engine.Parameters.Count).Select(b => (b.Name, b.RhsText)).ToList(),
            Output = parsed.Body
        };
    }

    /// <summary>A new engine's shape from the names typed, every value a placeholder (spec 0015).</summary>
    private static StateEngine? Create(StateEngine engine, IReadOnlyList<StateEditRow> rows, out string? error)
    {
        var keys = rows.Select(r => r.Key.Trim()).Where(k => k.Length > 0).ToList();
        if (keys.Count == 0)
        {
            error = "Type a variable name.";
            return null;
        }

        var state = string.Join(", ", keys.Select(k => $"{k} {StateEngine.Placeholder}"));
        var parsedState = StateScaffold.ParseState(state);
        if (parsedState.Error != null)
        {
            error = parsedState.Error;
            return null;
        }

        var variables = StateScaffold.PlanVariables(parsedState.Pairs);
        var bindings = variables.Select(v => (v.Binding, $"{engine.Prefix}GetVar({engine.Parameters[0]}, {StateScaffold.Quoted(v.Key)})")).ToList();
        bindings.AddRange(variables.Select(v => (v.Next, v.Binding)));

        error = null;
        return engine with
        {
            Seed = StateScaffold.StateCall(engine.Prefix, variables.Select(v => (v.Key, StateEngine.Placeholder)).ToList()),
            Bindings = bindings,
            Output = StateScaffold.StateCall(engine.Prefix, variables.Select(v => (v.Key, v.Next)).ToList())
        };
    }

    // ---------------------------------------------------------------- form and name

    private static string? CheckName(string name, string? current, Func<string, string?> refersTo, List<string> notes)
    {
        if (!ExcelNameValidator.Validate(name).IsValid || name.Contains('!'))
            return $"{name} can't be a name in Excel.";

        if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) return null;

        var existing = refersTo(name);
        if (existing == null) return null;
        if (!LambdaSignatureParser.IsLambdaFormula(existing))
            return $"{name} is already a workbook name for something else.";

        notes.Add($"Save replaces the LAMBDA already stored as {name}.");
        return null;
    }

    /// <summary>
    ///     The step's parameters for <paramref name="fold" />. The state and input keep their
    ///     names; extra arguments (<c>i</c>) take the fold's own names. Dropping one the rules read
    ///     is refused. From a LET step, a line of that name holding a number or <c>k</c> becomes it.
    /// </summary>
    private static IReadOnlyList<string> ParametersFor(
        StateEngine engine, StateFoldKind? fold, out IReadOnlyList<(string Name, string Value)> bindings, out string? error)
    {
        error = null;
        var lines = engine.Bindings.ToList();
        var parameters = engine.Parameters.ToList();

        if (fold == null)
        {
            bindings = lines;
            return parameters;
        }

        var wanted = fold.StepParameters.Count;
        if (parameters.Count > wanted)
        {
            var dropped = parameters.Skip(wanted).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var read = NamesIn(lines.Select(l => l.Value).Append(engine.Output), dropped);
            if (read.Count > 0)
            {
                bindings = lines;
                error = $"The rules read {string.Join(", ", read)}, which {fold.Name} doesn't pass to its step.";
                return parameters;
            }
            parameters = parameters.Take(wanted).ToList();
        }

        for (var i = parameters.Count; i < wanted; i++)
        {
            var name = fold.StepParameters[i];
            var at = lines.FindIndex(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0 && IsCounterValue(lines[at].Value))
            {
                parameters.Add(lines[at].Name);
                lines.RemoveAt(at);
                continue;
            }

            var used = UsedNames(engine);
            used.UnionWith(parameters);
            while (used.Contains(name)) name += "_";
            parameters.Add(name);
        }

        bindings = lines;
        return parameters;
    }

    private static HashSet<string> NamesIn(IEnumerable<string> expressions, HashSet<string> candidates)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in expressions) found.UnionWith(StateStepParser.NamesIn(e, candidates));
        return found;
    }

    private static bool IsCounterValue(string value)
    {
        var v = value.Trim();
        return Regex.IsMatch(v, @"^[0-9]+$") || Regex.IsMatch(v, @"^k_*$", RegexOptions.IgnoreCase);
    }

    private static HashSet<string> UsedNames(StateEngine engine)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in engine.Bindings)
        {
            used.Add(name);
            AddTokens(value);
        }
        AddTokens(engine.Output);
        AddTokens(engine.Seed);
        AddTokens(engine.Inputs);
        used.UnionWith(engine.Parameters);
        return used;

        void AddTokens(string text)
        {
            if (text.Length == 0) return;
            foreach (var token in FormulaParser.Tokenize(text))
                if (token.Type == FormulaTokenType.Name)
                    used.Add(token.Text);
        }
    }

    private static string StepLet(int indent, StateEngine engine, bool absolute)
    {
        var lines = engine.Bindings;
        var output = engine.Output;
        if (absolute)
        {
            var names = engine.Bindings.Select(b => b.Name).Concat(engine.Parameters).ToList();
            lines = lines.Select(l => (l.Name, LetToLambdaBuilder.AbsolutizeCellRefs(l.Value, names))).ToList();
            output = LetToLambdaBuilder.AbsolutizeCellRefs(output, names);
        }

        var sb = new StringBuilder();
        FormulaFormatter.AppendLet(sb, indent, lines, output);
        return sb.ToString();
    }

    /// <summary>The named LAMBDA, laid out as LET to LAMBDA writes it, with cell references made absolute.</summary>
    private static string Definition(StateEngine engine)
    {
        var sb = new StringBuilder("=");
        FormulaFormatter.AppendLambda(sb, 0, engine.Parameters, StepLet(FormulaFormatter.IndentStep, engine, absolute: true));
        return sb.ToString();
    }

    private static string FoldFormula(StateEngine engine, StateFoldKind fold, StateEngine original, Func<StateFoldKind, string> callName)
    {
        var name = original.Fold != null && SameKind(original.Fold, fold) && original.FoldCallName != null
            ? original.FoldCallName
            : callName(fold);

        var step = engine.StepName ?? $"LAMBDA({string.Join(", ", engine.Parameters)}, {StepLet(0, engine, absolute: false)})";

        var slots = new string?[fold.WrittenArguments];
        slots[fold.SeedIndex] = engine.Seed.Length == 0 ? StateEngine.Placeholder : engine.Seed;
        slots[fold.InputsIndex] = engine.Inputs.Length == 0 ? StateEngine.NewInputs : engine.Inputs;
        slots[fold.StepIndex] = step;

        var args = slots.Select(s => s ?? "").ToList();
        if (original.Fold != null && SameKind(original.Fold, fold))
        {
            foreach (var (index, text) in original.OtherArguments.OrderBy(a => a.Index))
            {
                while (args.Count <= index) args.Add("");
                args[index] = text;
            }
        }

        var call = $"{name}({string.Join(", ", args)})";
        var before = original.IsNew || original.Fold == null ? "=" : original.Before;
        var after = original.IsNew || original.Fold == null ? "" : original.After;
        return before + call + after;
    }

    /// <summary>
    ///     The unfolded step: <c>_seed</c> and <c>_inputs</c> keep the fold's arguments, so folding
    ///     back restores them. Inline, the state is the seed and the input the first one. Named, a
    ///     <c>k</c> stepper shows step k, with the state after k − 1 steps of the named step.
    /// </summary>
    private static string LetStep(StateEngine engine)
    {
        var state = engine.Parameters[0];
        var input = engine.Parameters[1];
        var extras = engine.Parameters.Skip(2).ToList();

        var lines = new List<(string Name, string Value)>
        {
            (SeedSlot, engine.Seed.Length == 0 ? StateEngine.Placeholder : engine.Seed),
            (InputsSlot, engine.Inputs.Length == 0 ? StateEngine.NewInputs : engine.Inputs)
        };

        if (engine.StepName == null)
        {
            lines.Add((state, SeedSlot));
            lines.Add((input, $"INDEX({InputsSlot}, 1)"));
            lines.AddRange(extras.Select((e, i) => (e, i == 0 ? "1" : "0")));
        }
        else
        {
            var used = UsedNames(engine);
            var k = "k";
            while (used.Contains(k)) k += "_";

            string run;
            if (extras.Count == 0)
            {
                run = $"SCAN({SeedSlot}, {InputsSlot}, {engine.StepName})";
            }
            else
            {
                var extraArgs = string.Concat(extras.Select((_, i) => i == 0 ? ", _j" : ", 0"));
                run = $"SCAN({SeedSlot}, SEQUENCE({k} - 1), LAMBDA(_s, _j, {engine.StepName}(_s, INDEX({InputsSlot}, _j){extraArgs})))";
            }

            lines.Add((k, "1"));
            lines.Add((state, $"IF({k} = 1, {SeedSlot}, INDEX({run}, {k} - 1))"));
            lines.Add((input, $"INDEX({InputsSlot}, {k})"));
            lines.AddRange(extras.Select((e, i) => (e, i == 0 ? k : "0")));
        }

        lines.AddRange(engine.Bindings);

        var sb = new StringBuilder("=");
        FormulaFormatter.AppendLet(sb, 0, lines, engine.Output);
        return sb.ToString();
    }
}
