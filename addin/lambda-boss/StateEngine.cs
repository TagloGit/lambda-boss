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
    string? Library,
    IReadOnlyList<StateFoldArgument>? Arguments = null)
{
    private static readonly Regex StepSignature = new(@"^\s*LAMBDA\s*\(([^()]*)\)", RegexOptions.IgnoreCase);

    public static readonly StateFoldKind Reduce = new("REDUCE", 0, 1, 2, new[] { "a", "v" }, null);

    public static readonly StateFoldKind Scan = new("SCAN", 0, 1, 2, new[] { "a", "v" }, null);

    public bool IsNative => Library == null;

    /// <summary>Every documented argument, in order (empty for REDUCE and SCAN).</summary>
    public IReadOnlyList<StateFoldArgument> AllArguments => Arguments ?? Array.Empty<StateFoldArgument>();

    /// <summary>True for an argument that isn't the seed, the inputs or the step (a stop, a continue).</summary>
    public bool IsOther(int index) => index != SeedIndex && index != InputsIndex && index != StepIndex;

    /// <summary>The seed can be left out (FOLDROWS).</summary>
    public bool SeedIsOptional => SeedIndex < AllArguments.Count && !AllArguments[SeedIndex].Required;

    /// <summary>The name of argument <paramref name="index" />, for the unfolded form's <c>_name</c> line.</summary>
    public string ArgumentName(int index) => index < AllArguments.Count ? AllArguments[index].Name : $"arg{index + 1}";

    /// <summary>The number of arguments the fold needs written: up to its last seed, inputs or step slot.</summary>
    public int WrittenArguments => Math.Max(SeedIndex, Math.Max(InputsIndex, StepIndex)) + 1;

    /// <summary>The arguments a call must have to be read: an optional seed at the end can be left out.</summary>
    public int RequiredWritten => SeedIsOptional && SeedIndex == WrittenArguments - 1
        ? Math.Max(InputsIndex, StepIndex) + 1
        : WrittenArguments;

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

        var arguments = ps
            .Select(p => new StateFoldArgument(p.Name, p.Required, StepSignature.IsMatch(p.Description)))
            .ToList();
        return new StateFoldKind(doc.Name, seed, inputs, step, stepParameters, library ?? "", arguments);
    }

    private static int IndexWhere(IReadOnlyList<LambdaParamDoc> ps, Func<LambdaParamDoc, bool> test, int[]? skip = null)
    {
        for (var i = 0; i < ps.Count; i++)
            if ((skip == null || !skip.Contains(i)) && test(ps[i]))
                return i;
        return -1;
    }
}

/// <summary>One documented argument of a fold. <paramref name="IsLambda" />: documented as <c>LAMBDA(…)</c>.</summary>
public sealed record StateFoldArgument(string Name, bool Required, bool IsLambda);

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
    IReadOnlyList<(string Name, string Text)> OtherArguments,
    string? Note)
{
    /// <summary>
    ///     Lines of the unfolded form that stand in for an extra step argument (<c>i, 1</c> or
    ///     <c>i, k</c>, right after the input line). Only these become a parameter again on fold-back.
    /// </summary>
    public IReadOnlyList<string> StandIns { get; init; } = Array.Empty<string>();

    /// <summary>The fold call exactly as the cell holds it, from its name to its close bracket. Null for the unfolded form.</summary>
    public string? CallText { get; init; }

    /// <summary>The placeholder for a value Tim types over.</summary>
    public const string Placeholder = "\"\"";

    /// <summary>The placeholder inputs of a new engine.</summary>
    public const string NewInputs = "SEQUENCE(1)";

    public bool IsWholeFormula => Before.Trim() == "=" && After.Trim().Length == 0;

    /// <summary>The variables, in order. Debug-only outputs are not variables and are left out.</summary>
    public IReadOnlyList<StateEditRow> Rows
    {
        get
        {
            if (IsNew) return Array.Empty<StateEditRow>();

            var rows = StateEdit.Read(StateEngines.Intermediate(this))?.Rows;
            if (rows == null) return Array.Empty<StateEditRow>();

            var debug = DebugKeys;
            return debug.Count == 0
                ? rows
                : rows.Where(r => !debug.Contains(r.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        }
    }

    /// <summary>Output keys nothing reads back (spec 0015, "Debug toggle"): not state variables.</summary>
    public IReadOnlyList<string> DebugKeys => StateDebug.DebugKeys(this);

    /// <summary>A SCAN-family fold directly inside <c>SplitState(…, 1)</c>: the debug form.</summary>
    public bool IsDebug =>
        StateDebug.IsScan(Fold) && StateDebug.TryUnwrap(Before, After, out _, out _);

    /// <summary>A new engine for an empty cell: no variables yet, a REDUCE with placeholders.</summary>
    public static StateEngine New(string prefix) => new(
        StateFoldKind.Reduce, "REDUCE", null, "", NewInputs,
        new[] { "a", "v" }, Array.Empty<(string, string)>(), "", prefix, "=", "",
        Array.Empty<(string, string)>(), null);

    public bool IsNew => Output.Length == 0;
}

/// <summary>What <see cref="StateEngines.Read" /> found in a cell.</summary>
public sealed record StateEngineRead(StateEngine? Engine, bool IsEmpty, string? Refusal);

/// <summary>
///     What the dialog asks for: the variables, the form (null = LET step), the name (null =
///     inline) and the debug state (null = leave as it is; spec 0015, "Debug toggle").
/// </summary>
public sealed record StateEngineChange(
    IReadOnlyList<StateEditRow> Rows,
    StateFoldKind? Fold,
    string? StepName,
    bool? Debug = null);

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

    // Names the fold's other arguments take while the edit engine works on them.
    private const string OtherLine = "_arg_";

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
                .Where(c => c.Kind != null && c.Call.Arguments.Count >= c.Kind.RequiredWritten
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
            // 0. A name can't be a debug output's: it would be set twice in the output.
            if (!engine.IsNew)
            {
                var known = engine.DebugKeys;
                var clash = change.Rows.FirstOrDefault(r =>
                    r.Key.Trim().Length > 0 && !string.Equals(r.Key.Trim(), r.OriginalKey, StringComparison.Ordinal)
                    && known.Contains(r.Key.Trim(), StringComparer.OrdinalIgnoreCase));
                if (clash != null)
                    return Failed($"{clash.Key.Trim()} is already a debug output of this step. Pick another name.", notes);
            }

            // 1. Variables.
            string? editError;
            IReadOnlyList<string> blockers = Array.Empty<string>();
            var edited = engine.IsNew
                ? Create(engine, change.Rows, out editError)
                : Edit(engine, change.Rows, out editError, out blockers);
            if (edited == null) return Failed(editError ?? "Can't apply the variables.", notes, blockers);

            // 1b. Debug: in or out. Out drops the outputs only the debug form needed.
            var debug = change.Debug ?? engine.IsDebug;
            if (debug && change.Fold == null)
                return Failed("Debug needs a fold. Pick REDUCE or SCAN (or a library fold) as the form.", notes);
            if (debug && !StateDebug.IsScan(change.Fold))
                return Failed($"Debug runs the fold as a SCAN, and {change.Fold!.Name} has no SCAN twin the dialog knows.", notes);

            var debugKeys = edited.DebugKeys;
            if (engine.IsDebug && !debug && debugKeys.Count > 0)
            {
                if (edited.Rows.Count == 0)
                    return Failed(
                        $"Nothing in this step reads {string.Join(", ", debugKeys)} back, so leaving debug would drop every output. Read one with GetVar, or add a variable, first.",
                        notes);
                edited = Rebuild(edited, StateEdit.DropOutputKeys(Intermediate(edited, withOtherArguments: true), debugKeys));
                notes.Add($"Debug off: dropped the debug outputs {string.Join(", ", debugKeys)}.");
            }

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

            var shaped = edited with { Parameters = parameters, Bindings = bindings, StepName = name };

            string? definition = null;
            if (name != null)
            {
                definition = Definition(shaped);
                var stored = refersTo(name);
                if (string.Equals(name, engine.StepName, StringComparison.OrdinalIgnoreCase)
                    && stored != null && Normal(stored) != Normal(definition))
                    notes.Add($"Save updates {name}: every cell that calls it gets the change.");
            }

            var formula = change.Fold == null
                ? LetStep(shaped)
                : FoldFormula(shaped, change.Fold, engine, callName, notes, debug);

            // A pure debug toggle gives back the cell as Tim wrote it (#447).
            if (change.Fold != null && change.Debug is { } toggled && toggled != engine.IsDebug
                && string.Equals(name, engine.StepName, StringComparison.OrdinalIgnoreCase)
                && NoVariableEdits(engine, change.Rows))
                formula = TogglePreservingText(engine, shaped, change.Fold, callName, debug) ?? formula;

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

    private static bool NoVariableEdits(StateEngine engine, IReadOnlyList<StateEditRow> rows) =>
        rows.Count == engine.Rows.Count
        && rows.All(r => r.OriginalKey != null && string.Equals(r.Key.Trim(), r.OriginalKey, StringComparison.Ordinal));

    /// <summary>
    ///     The debug toggle done on the cell's own text (#447): the fold's name swapped for its twin,
    ///     the <c>SplitState(…, 1)</c> wrapper put on or taken off, and on the way out the debug
    ///     outputs taken out of the step's text. Everything else stays character for character, so
    ///     Debug on then off gives back exactly what Tim wrote. Null when anything else changed or
    ///     the text can't be matched up: the formula is then laid out afresh.
    /// </summary>
    private static string? TogglePreservingText(
        StateEngine engine, StateEngine shaped, StateFoldKind fold, Func<StateFoldKind, string> callName, bool debug)
    {
        if (engine.CallText == null || engine.FoldCallName == null || engine.Fold == null) return null;
        if (!engine.CallText.StartsWith(engine.FoldCallName, StringComparison.Ordinal)) return null;
        if (!shaped.Parameters.SequenceEqual(engine.Parameters, StringComparer.OrdinalIgnoreCase)) return null;
        if (shaped.Seed != engine.Seed || shaped.Inputs != engine.Inputs) return null;
        if (!shaped.OtherArguments.SequenceEqual(engine.OtherArguments)) return null;
        if (!shaped.Bindings.Select(b => b.Name).SequenceEqual(engine.Bindings.Select(b => b.Name), StringComparer.Ordinal))
            return null;

        var name = SameKind(engine.Fold, fold) ? engine.FoldCallName : callName(fold);
        var text = name + engine.CallText[engine.FoldCallName.Length..];

        // An inline step's rules are in the cell; a named step's are in its definition, written as before.
        if (engine.StepName == null)
        {
            var changed = engine.Bindings.Select(b => b.Value).Append(engine.Output)
                .Zip(shaped.Bindings.Select(b => b.Value).Append(shaped.Output), (old, now) => (Old: old, Now: now))
                .Where(c => c.Old != c.Now);
            foreach (var (old, now) in changed)
            {
                var at = old.Length == 0 ? -1 : text.IndexOf(old, StringComparison.Ordinal);
                if (at < 0 || text.IndexOf(old, at + 1, StringComparison.Ordinal) >= 0) return null;
                text = text[..at] + now + text[(at + old.Length)..];
            }
        }

        var before = engine.Before;
        var after = engine.After;
        if (engine.IsDebug && !debug)
        {
            if (!StateDebug.TryUnwrap(before, after, out var bare, out var bareAfter)) return null;
            (before, after) = (bare, bareAfter);
        }
        else if (!engine.IsDebug && debug && !StateDebug.TryUnwrap(before, after, out _, out _))
        {
            (before, after) = StateDebug.Wrap(before, after, shaped.Prefix);
        }

        return before + text + after;
    }

    /// <summary>A formula's tokens without the layout: Excel stores <c>LAMBDA(a, v,</c> as <c>LAMBDA(a,v,</c>.</summary>
    private static string Normal(string formula)
    {
        try
        {
            return string.Concat(FormulaParser.Tokenize(formula).Select(t => t.Text));
        }
        catch (FormatException)
        {
            return formula.Replace("\r", "").Trim();
        }
    }

    private static StateEngineResult Failed(string error, List<string> notes, IReadOnlyList<string>? blockers = null) =>
        new(null, null, null, notes, error, blockers ?? Array.Empty<string>());

    /// <summary>A short description of the form, for the dialog's header.</summary>
    public static string Describe(StateEngine engine)
    {
        if (engine is null) throw new ArgumentNullException(nameof(engine));
        if (engine.IsNew) return "Empty cell: a new state engine.";
        var form = engine.Fold == null ? "Unfolded LET step" : $"{engine.FoldCallName} fold{(engine.IsDebug ? " (debug)" : "")}";
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
            .Where(a => kind.IsOther(a.Index) && a.Text.Length > 0)
            .Select(a => (kind.ArgumentName(a.Index), a.Text))
            .ToList();

        var engine = new StateEngine(
            kind, call.Name, stepName, seed, inputs, parameters,
            body.Bindings.Select(b => (b.Name, b.RhsText)).ToList(), body.Body,
            "", formula[..call.Start], formula[call.End..], others, null);

        refusal = CheckStep(engine);
        // A named output (#438) or a guard (#443) leaves no call in the body: take the seed's prefix then.
        var prefix = IsStateCall(engine.Output) ? PrefixOf(engine.Output) : PrefixOf(engine.Seed);
        return refusal == null ? engine with { Prefix = prefix, CallText = formula[call.Start..call.End] } : null;
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
        var byName = parsed.Bindings.ToDictionary(b => b.Name, b => b.RhsText, StringComparer.OrdinalIgnoreCase);
        var slotted = byName.ContainsKey(SeedSlot) && byName.ContainsKey(InputsSlot);

        // In the form /State writes, the input is the line after the state that indexes _inputs,
        // whether or not the rules read it yet.
        var stateIndex = parsed.Bindings.ToList().FindIndex(b => ReferenceEquals(b, step.StateBinding));
        var inputBinding = slotted
            ? parsed.Bindings.Skip(stateIndex + 1).FirstOrDefault(b =>
                StateStepParser.NamesIn(b.RhsText, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { InputsSlot }).Count > 0)
              ?? step.InputBinding
            : step.InputBinding;
        if (inputBinding == null) return null;
        step = step with { InputBinding = inputBinding };

        var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { step.StateBinding.Name, inputBinding.Name };

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

        // _seed and _inputs are the fold's arguments whatever a and v say: Tim edits a and v to
        // look at another step, and that sample must never become the seed.
        string seed, inputs;
        if (byName.TryGetValue(SeedSlot, out var seedLine))
        {
            seed = seedLine;
            drop.Add(SeedSlot);
        }
        else
        {
            seed = Resolve(seedText);
        }

        if (byName.TryGetValue(InputsSlot, out var inputsLine))
        {
            inputs = inputsLine;
            drop.Add(InputsSlot);
        }
        else
        {
            inputs = Resolve(inputsText);
        }

        // The fold's other arguments (_stop) sit between _inputs and the state line.
        var names = parsed.Bindings.Select(b => b.Name).ToList();
        var stateAt = names.FindIndex(n => string.Equals(n, step.StateBinding.Name, StringComparison.OrdinalIgnoreCase));
        var others = new List<(string Name, string Text)>();
        for (var i = 0; i < stateAt; i++)
        {
            var b = parsed.Bindings[i];
            if (!slotted || drop.Contains(b.Name) || !b.Name.StartsWith("_", StringComparison.Ordinal) || b.Name.Length < 2) continue;
            others.Add((b.Name[1..], b.RhsText));
            drop.Add(b.Name);
        }

        // Stand-ins for extra step arguments (i, 1 or i, k) come right after the input line.
        var standIns = new List<string>();
        var inputAt = names.FindIndex(n => string.Equals(n, step.InputBinding.Name, StringComparison.OrdinalIgnoreCase));
        for (var i = inputAt + 1; i < parsed.Bindings.Count && IsStandInValue(parsed.Bindings[i].RhsText); i++)
            standIns.Add(parsed.Bindings[i].Name);

        // Lines that only fed the sample (the 0011 stepper's k, a slot nothing else reads) go with it.
        var bindings = parsed.Bindings.Where(b => !drop.Contains(b.Name)).Select(b => (b.Name, b.RhsText)).ToList();
        var parameters = new List<string> { step.StateBinding.Name, step.InputBinding.Name };

        // A named step's extra arguments (i) are lines in the unfolded form: they're its parameters again.
        if (stepName != null)
        {
            foreach (var extra in LambdaSignatureParser.Parse(refersTo(stepName)!).Parameters.Skip(2))
            {
                var at = bindings.FindIndex(b => string.Equals(b.Name, extra, StringComparison.OrdinalIgnoreCase));
                if (at >= 0 && standIns.Contains(bindings[at].Name, StringComparer.OrdinalIgnoreCase))
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
            bindings, parsed.Body, PrefixOf(parsed.Body), "=", "", others, null) { StandIns = standIns };

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

    private static bool IsStateCall(string text)
    {
        try
        {
            return FormulaParser.Parse(text).Root is FunctionCallNode call
                   && (StateStepParser.IsNamed(call.Name, "SetVars") || StateStepParser.IsNamed(call.Name, "SetVar"));
        }
        catch (FormatException)
        {
            return false;
        }
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
    internal static string Intermediate(StateEngine engine, bool withOtherArguments = false)
    {
        var lines = new List<(string Name, string Value)>
        {
            (engine.Parameters[0], engine.Seed.Length == 0 ? StateEngine.Placeholder : engine.Seed)
        };
        lines.AddRange(engine.Parameters.Skip(1).Select(p => (p, "0")));

        // A stop or continue reads the state's keys too, so a rename reaches it and a remove sees it.
        if (withOtherArguments)
            lines.AddRange(engine.OtherArguments.Select(o => (OtherLine + o.Name, o.Text)));
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

        var result = StateEdit.Apply(Intermediate(engine, withOtherArguments: true), rowsWithPlaceholders);
        if (result.Formula == null)
        {
            error = result.Error;
            blockers = result.Blockers;
            return null;
        }

        error = null;
        return Rebuild(engine, result.Formula);
    }

    /// <summary>The engine with the step's seed, other arguments, rules and output read back from an edited intermediate.</summary>
    private static StateEngine Rebuild(StateEngine engine, string formula)
    {
        var parsed = LetParser.Parse(formula);
        var paramLines = parsed.Bindings.Take(engine.Parameters.Count).ToList();
        if (!paramLines.Select(b => b.Name).SequenceEqual(engine.Parameters, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The edit moved the step's parameters; nothing was changed.");

        var seed = paramLines[0].RhsText;
        return engine with
        {
            // A seed that was left out (FOLDROWS) stays out: there is nothing to add keys to.
            Seed = engine.Seed.Length == 0 ? "" : seed,
            OtherArguments = engine.OtherArguments
                .Select(o => (o.Name, parsed.Bindings.FirstOrDefault(b => b.Name == OtherLine + o.Name)?.RhsText ?? o.Text))
                .ToList(),
            Bindings = parsed.Bindings.Skip(engine.Parameters.Count)
                .Where(b => !b.Name.StartsWith(OtherLine, StringComparison.Ordinal))
                .Select(b => (b.Name, b.RhsText)).ToList(),
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
            if (at >= 0 && engine.StandIns.Contains(lines[at].Name, StringComparer.OrdinalIgnoreCase))
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

    /// <summary>What the unfolded form writes for an extra step argument: a number, or the stepper's <c>k</c>.</summary>
    private static bool IsStandInValue(string value)
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

    private static string FoldFormula(
        StateEngine engine, StateFoldKind fold, StateEngine original, Func<StateFoldKind, string> callName, List<string> notes,
        bool debug)
    {
        var name = original.Fold != null && SameKind(original.Fold, fold) && original.FoldCallName != null
            ? original.FoldCallName
            : callName(fold);

        var step = engine.StepName ?? $"LAMBDA({string.Join(", ", engine.Parameters)}, {StepLet(0, engine, absolute: false)})";

        var count = Math.Max(fold.WrittenArguments, fold.AllArguments.Count);
        var args = new string[count];
        args[fold.SeedIndex] = engine.Seed.Length > 0 ? engine.Seed : fold.SeedIsOptional ? "" : StateEngine.Placeholder;
        args[fold.InputsIndex] = engine.Inputs.Length == 0 ? StateEngine.NewInputs : engine.Inputs;
        args[fold.StepIndex] = step;

        // The fold's other arguments (a stop, a continue) carry over by name.
        var unused = engine.OtherArguments.ToList();
        for (var i = 0; i < count; i++)
        {
            if (!fold.IsOther(i)) continue;
            var argName = fold.ArgumentName(i);
            var at = unused.FindIndex(o => string.Equals(o.Name, argName, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                args[i] = unused[at].Text;
                unused.RemoveAt(at);
            }
            else if (i < fold.AllArguments.Count && fold.AllArguments[i].Required)
            {
                args[i] = fold.AllArguments[i].IsLambda
                    ? $"LAMBDA({string.Join(", ", engine.Parameters)}, TRUE)"
                    : StateEngine.Placeholder;
                notes.Add($"{fold.Name} needs its {argName} argument: Save writes a placeholder, {args[i]}.");
            }
            else
            {
                args[i] = "";
            }
        }

        if (unused.Count > 0)
            notes.Add($"{fold.Name} has no {string.Join(" or ", unused.Select(u => u.Name))} argument, so {string.Join(", ", unused.Select(u => u.Text))} isn't written.");

        // Optional arguments left empty at the end are left out. An optional seed that is only the
        // placeholder is the same as no seed (FOLDROWS starts from "" either way).
        if (fold.SeedIsOptional && args[fold.SeedIndex] == StateEngine.Placeholder) args[fold.SeedIndex] = "";
        var written = args.Length;
        while (written > 0 && args[written - 1].Length == 0
               && written - 1 != fold.InputsIndex && written - 1 != fold.StepIndex)
            written--;

        var call = $"{name}({string.Join(", ", args.Take(written))})";
        var before = original.IsNew || original.Fold == null ? "=" : original.Before;
        var after = original.IsNew || original.Fold == null ? "" : original.After;

        // The debug wrapper goes on or comes off; every other wrapper is left as it was.
        if (original.IsDebug && !debug && StateDebug.TryUnwrap(before, after, out var bare, out var bareAfter))
            (before, after) = (bare, bareAfter);
        else if (!original.IsDebug && debug && !StateDebug.TryUnwrap(before, after, out _, out _))
            (before, after) = StateDebug.Wrap(before, after, engine.Prefix);

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
        lines.AddRange(engine.OtherArguments.Select(o => ("_" + o.Name, o.Text)));

        var reserved = lines.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clash = engine.Bindings.Select(b => b.Name).Concat(engine.Parameters).FirstOrDefault(reserved.Contains);
        if (clash != null)
            throw new InvalidOperationException($"{clash} is a name the unfolded step uses for the fold's arguments. Rename it first.");

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
