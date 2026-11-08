using System.Text;

namespace LambdaBoss;

/// <summary>
///     One row of the edit-state dialog. <paramref name="OriginalKey" /> is the key the row
///     started as, or null for a new variable. A blank <paramref name="Key" /> on an existing row
///     removes it. <paramref name="Value" /> is the sample value, which only a new row writes.
/// </summary>
public record StateEditRow(string? OriginalKey, string Key, string Value);

/// <summary>What the edit dialog opens with, or why this step can't be edited.</summary>
public record StateEditView(IReadOnlyList<StateEditRow> Rows, string? Refusal);

/// <summary>
///     The step after an edit, or why there isn't one. <paramref name="Blockers" /> names the lines
///     that stop a removal.
/// </summary>
public record StateEditResult(string? Formula, string? Error, IReadOnlyList<string> Blockers);

/// <summary>
///     Adds, removes and renames the variables of a state step (spec 0011, part 2). Pure text:
///     the command reads the cell and writes the result. Lines that aren't the variable's own
///     keep their place and text; only a key rename reaches into them.
/// </summary>
public static class StateEdit
{
    private const int MaxFormulaLength = 8192;

    /// <summary>The rows for <paramref name="formula" />, or null when it isn't a state step.</summary>
    public static StateEditView? Read(string? formula)
    {
        if (!IsWholeLet(formula)) return null;

        try
        {
            var parsed = LetParser.Parse(formula!);
            var step = StateStepParser.TryParse(parsed);
            if (step == null) return null;

            if (step.Family == StateFamily.Array)
                return Refused("Edit state works on SetVars steps. This one holds its state in an array.");

            if (ReadChain(Root(parsed.Body)).Base != null)
                return Refused("Edit state needs a SetVars output. This step's output is a SetVar chain on the state.");

            var sample = ReadChain(Root(step.StateBinding.RhsText)).Pairs;
            var rows = step.Variables
                .Select(v => new StateEditRow(
                    v.Key,
                    v.Key,
                    sample.LastOrDefault(p => SameKey(p.Key, v.Key))?.Value ?? ""))
                .ToList();
            return new StateEditView(rows, null);
        }
        catch (FormatException)
        {
            return null;
        }

        static StateEditView Refused(string why) => new(Array.Empty<StateEditRow>(), why);
    }

    /// <summary>Applies the dialog's rows: removals first, then renames, then new variables.</summary>
    public static StateEditResult Apply(string formula, IReadOnlyList<StateEditRow> rows)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));
        if (rows is null) throw new ArgumentNullException(nameof(rows));

        try
        {
            var rowError = CheckRows(rows);
            if (rowError != null) return Failed(rowError);

            var existing = rows.Where(r => r.OriginalKey != null).ToList();
            var removed = existing.Where(r => r.Key.Trim().Length == 0).Select(r => r.OriginalKey!).ToList();
            var renamed = existing
                .Where(r => r.Key.Trim().Length > 0 && r.Key.Trim() != r.OriginalKey)
                .ToList();
            var added = rows.Where(r => r.OriginalKey == null && r.Key.Trim().Length > 0).ToList();

            if (removed.Count == 0 && renamed.Count == 0 && added.Count == 0)
                return new StateEditResult(formula, null, Array.Empty<string>());

            if (removed.Count == existing.Count && added.Count == 0)
                return Failed("A state needs at least one variable.");

            var step = new EditableStep(formula);

            if (removed.Count > 0)
            {
                var blockers = step.Remove(removed);
                if (blockers.Count > 0)
                    return new StateEditResult(
                        null, $"Can't remove {string.Join(", ", removed)}: other lines still read it.", blockers);
            }

            foreach (var row in renamed)
            {
                var clash = step.Rename(row.OriginalKey!, row.Key.Trim());
                if (clash != null) return Failed(clash);
            }

            foreach (var row in added)
                step.Add(row.Key.Trim(), row.Value.Trim());

            var written = step.Write();
            if (written.Length > MaxFormulaLength)
                return Failed($"The step would be over {MaxFormulaLength:N0} characters, Excel's limit.");

            // What goes in the cell has to read back.
            LetParser.Parse(written);
            return new StateEditResult(written, null, Array.Empty<string>());
        }
        catch (FormatException ex)
        {
            return Failed($"Can't read the step: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return Failed(ex.Message);
        }

        static StateEditResult Failed(string error) => new(null, error, Array.Empty<string>());
    }

    private static string? CheckRows(IReadOnlyList<StateEditRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = row.Key.Trim();
            var value = row.Value.Trim();

            if (row.OriginalKey == null)
            {
                if (key.Length == 0)
                {
                    if (value.Length > 0) return $"Name the new variable with the value {value}.";
                    continue;
                }

                if (value.Length == 0) return $"{key} needs a value.";
                if (!StateScaffold.Reads(value)) return $"Can't read the value of {key}: {value}";
            }

            if (key.Length == 0) continue;
            if (key != row.OriginalKey && !StateScaffold.IsUsableKey(key)) return $"'{key}' can't be a state name.";
            if (!seen.Add(key)) return $"{key} is listed twice.";
        }

        return null;
    }

    /// <summary>LET has to be the whole formula: an edit rewrites the cell from its parts.</summary>
    private static bool IsWholeLet(string? formula)
    {
        if (!LetParser.IsLetFormula(formula)) return false;

        var close = LetParser.FindMatchingClose(formula!, formula!.IndexOf('('));
        return close >= 0 && formula[(close + 1)..].Trim().Length == 0;
    }

    private static FormulaNode Root(string expression) => FormulaParser.Parse(expression).Root;

    private static bool SameKey(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Reads a <c>SetVars</c> call and any <c>SetVar</c> layers on it. <c>Base</c> is the text
    ///     under the layers when that isn't a <c>SetVars</c> call: a cell, a state binding, an
    ///     <c>INDEX(SCAN(…))</c>. As with SetVar itself, a key set again moves to the end.
    /// </summary>
    private static Chain ReadChain(FormulaNode node)
    {
        if (node is FunctionCallNode call)
        {
            if (StateStepParser.IsNamed(call.Name, "SetVars") && ReadPairs(call) is { } pairs)
            {
                var chain = new Chain(new List<KeyedText>(), null);
                foreach (var pair in pairs) chain.Set(pair.Key, pair.Value);
                return chain;
            }

            if (StateStepParser.IsNamed(call.Name, "SetVar")
                && call.Arguments.Count == 3
                && StringLiteral(call.Arguments[1]) is { } key
                && call.Arguments[2] is not EmptyArgNode)
            {
                var chain = ReadChain(call.Arguments[0]);
                chain.Set(key, call.Arguments[2].ToFormula().Trim());
                return chain;
            }
        }

        return new Chain(new List<KeyedText>(), node.ToFormula().Trim());
    }

    private static List<KeyedText>? ReadPairs(FunctionCallNode call)
    {
        var args = call.Arguments;
        if (args.Count < 2 || args.Count % 2 != 0) return null;

        var pairs = new List<KeyedText>();
        for (var i = 0; i < args.Count; i += 2)
        {
            if (StringLiteral(args[i]) is not { } key || args[i + 1] is EmptyArgNode) return null;
            pairs.Add(new KeyedText(key, args[i + 1].ToFormula().Trim()));
        }

        return pairs;
    }

    private static string? StringLiteral(FormulaNode node) =>
        node is LeafNode { TokenType: FormulaTokenType.String } leaf
            ? leaf.Text[1..^1].Replace("\"\"", "\"")
            : null;

    /// <summary>The key string a call on the state holds, for <c>GetVar(S, "k")</c> and <c>SetVar(S, "k", …)</c>.</summary>
    private static LeafNode? KeyOnState(FunctionCallNode call, string stateName)
    {
        var isCall = (StateStepParser.IsNamed(call.Name, "GetVar") && call.Arguments.Count == 2)
            || (StateStepParser.IsNamed(call.Name, "SetVar") && call.Arguments.Count == 3);

        return isCall
            && call.Arguments[0] is LeafNode { TokenType: FormulaTokenType.Name } state
            && string.Equals(state.Text, stateName, StringComparison.OrdinalIgnoreCase)
            && call.Arguments[1] is LeafNode { TokenType: FormulaTokenType.String } key
                ? key
                : null;
    }

    private static IEnumerable<FunctionCallNode> Calls(string expression) =>
        StateStepParser.Walk(Root(expression)).OfType<FunctionCallNode>();

    /// <summary>
    ///     True when <paramref name="text" /> reads or sets one of the keys on anything but the
    ///     bare state: an alias, a nested <c>SetVar</c>, a LAMBDA's parameter. That can't be told
    ///     from another state by reading the text, so an edit stops rather than guess.
    /// </summary>
    private static bool UsesKeyOffState(string text, string stateName, IReadOnlyCollection<string> keys) =>
        Calls(text).Any(call =>
            ((StateStepParser.IsNamed(call.Name, "GetVar") && call.Arguments.Count == 2)
                || (StateStepParser.IsNamed(call.Name, "SetVar") && call.Arguments.Count == 3))
            && KeyOnState(call, stateName) == null
            && StringLiteral(call.Arguments[1]) is { } key
            && keys.Contains(key, StringComparer.OrdinalIgnoreCase));

    private static string RewriteKey(string text, string stateName, string oldKey, string newKey)
    {
        var spans = Calls(text)
            .Select(c => KeyOnState(c, stateName))
            .Where(k => k != null && SameKey(StringLiteral(k)!, oldKey))
            .OrderByDescending(k => k!.Start)
            .ToList();

        foreach (var span in spans)
            text = text[..span!.Start] + StateScaffold.Quoted(newKey) + text[span.End..];

        return text;
    }

    private sealed record KeyedText(string Key, string Value);

    private sealed class Chain
    {
        public Chain(List<KeyedText> pairs, string? @base)
        {
            Pairs = pairs;
            Base = @base;
        }

        public List<KeyedText> Pairs { get; private set; }

        public string? Base { get; private set; }

        public void Set(string key, string value)
        {
            Pairs.RemoveAll(p => SameKey(p.Key, key));
            Pairs.Add(new KeyedText(key, value));
        }

        public void RenameKey(string oldKey, string newKey) =>
            Pairs = Pairs.Select(p => SameKey(p.Key, oldKey) ? p with { Key = newKey } : p).ToList();

        public void Remove(ICollection<string> keys) =>
            Pairs.RemoveAll(p => keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase));

        public bool Has(string key) => Pairs.Any(p => SameKey(p.Key, key));

        public void RewriteValues(Func<string, string> rewrite) =>
            Pairs = Pairs.Select(p => p with { Value = rewrite(p.Value) }).ToList();

        public void ApplyRenames(Dictionary<string, string> renames)
        {
            RewriteValues(value => LetToLambdaBuilder.ApplyRenames(value, renames));
            if (Base != null) Base = LetToLambdaBuilder.ApplyRenames(Base, renames);
        }

        public IEnumerable<string> Texts() => Pairs.Select(p => p.Value).Concat(Base == null ? Array.Empty<string>() : new[] { Base });

        public string Write(string prefix)
        {
            var pairs = Pairs.Select(p => (p.Key, p.Value)).ToList();
            if (Base == null)
            {
                if (pairs.Count == 0) throw new InvalidOperationException("The sample state would be left with no variables.");
                return StateScaffold.StateCall(prefix, pairs);
            }

            return pairs.Aggregate(
                Base, (state, p) => $"{prefix}SetVar({state}, {StateScaffold.Quoted(p.Key)}, {p.Value})");
        }
    }

    private sealed class Line
    {
        public Line(string name, string rhs)
        {
            Name = name;
            Rhs = rhs;
        }

        public string Name { get; set; }

        public string Rhs { get; set; }
    }

    private sealed class Variable
    {
        public Variable(string key, string? binding, string? next)
        {
            Key = key;
            Binding = binding;
            Next = next;
        }

        public string Key { get; set; }

        public string? Binding { get; set; }

        public string? Next { get; set; }
    }

    /// <summary>A state step taken apart, so several edits can apply before anything is written.</summary>
    private sealed class EditableStep
    {
        private readonly string? _inputName;
        private readonly List<Line> _lines;
        private readonly Chain _output;
        private readonly string _prefix;
        private readonly Chain _sample;
        private readonly string _stateName;
        private readonly List<Variable> _variables;
        private bool _sampleChanged;

        public EditableStep(string formula)
        {
            if (!IsWholeLet(formula)) throw new FormatException("The cell isn't a LET.");

            var parsed = LetParser.Parse(formula);
            var step = StateStepParser.TryParse(parsed)
                ?? throw new FormatException("The cell isn't a state step.");

            _stateName = step.StateBinding.Name;
            _inputName = step.InputBinding?.Name;
            _lines = parsed.Bindings.Select(b => new Line(b.Name, b.RhsText)).ToList();
            _variables = step.Variables
                .Select(v => new Variable(v.Key, v.GetVarLine?.Name, v.NextLine?.Name))
                .ToList();

            var outputCall = Root(parsed.Body);
            _output = ReadChain(outputCall);
            if (_output.Base != null) throw new FormatException("The output is a SetVar chain on the state.");

            var name = ((FunctionCallNode)outputCall).Name;
            _prefix = name[..(name.LastIndexOf('.') + 1)];

            _sample = ReadChain(Root(step.StateBinding.RhsText));
        }

        private Line StateLine => _lines.First(l => IsNamed(l.Name, _stateName));

        private static bool IsNamed(string? a, string? b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Deletes the variables' lines and pairs, or returns the lines that still need them.</summary>
        public List<string> Remove(IReadOnlyCollection<string> keys)
        {
            var leaving = _variables.Where(v => keys.Contains(v.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            var staying = _variables.Except(leaving).ToList();
            var keptOutput = _output.Pairs.Where(p => !keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).ToList();

            var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in leaving)
            {
                if (variable.Binding != null) deleted.Add(variable.Binding);
                if (variable.Next == null
                    || IsNamed(variable.Next, _stateName)
                    || IsNamed(variable.Next, _inputName)
                    || staying.Any(v => IsNamed(v.Binding, variable.Next))
                    || keptOutput.Any(p => IsNamed(p.Value, variable.Next)))
                    continue;
                deleted.Add(variable.Next);
            }

            var blockers = new List<string>();

            void Check(string who, string text)
            {
                var names = StateStepParser.NamesIn(text, deleted).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var inline = Calls(text)
                    .Select(c => KeyOnState(c, _stateName))
                    .Where(k => k != null && keys.Contains(StringLiteral(k)!, StringComparer.OrdinalIgnoreCase))
                    .Select(k => k!.Text);
                var reads = names.Concat(inline).Distinct().ToList();
                if (reads.Count > 0) blockers.Add($"{who} reads {string.Join(", ", reads)}");
                else if (UsesKeyOffState(text, _stateName, keys))
                    blockers.Add($"{who} uses the key through another name");
            }

            foreach (var line in _lines.Where(l => !deleted.Contains(l.Name)))
            {
                // The state's own line is checked below, by its pairs: the ones leaving don't count.
                if (IsNamed(line.Name, _stateName)) continue;
                Check(line.Name, line.Rhs);
            }

            foreach (var pair in keptOutput) Check($"the output's {StateScaffold.Quoted(pair.Key)}", pair.Value);

            foreach (var pair in _sample.Pairs.Where(p => !keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)))
                Check(_stateName, pair.Value);
            if (_sample.Base != null) Check(_stateName, _sample.Base);

            if (blockers.Count > 0) return blockers;

            _lines.RemoveAll(l => deleted.Contains(l.Name));
            _variables.RemoveAll(leaving.Contains);
            _output.Remove(keys.ToList());

            var before = _sample.Pairs.Count;
            _sample.Remove(keys.ToList());
            _sampleChanged |= _sample.Pairs.Count != before;

            return blockers;
        }

        /// <summary>Renames a key and its lines. Returns why not, or null.</summary>
        public string? Rename(string oldKey, string newKey)
        {
            var variable = _variables.First(v => SameKey(v.Key, oldKey));

            if (!SameKey(oldKey, newKey) && _variables.Any(v => SameKey(v.Key, newKey)))
                return $"{newKey} is already a state variable.";

            if (!SameKey(oldKey, newKey) && _sample.Has(newKey))
                return $"{newKey} is already in the sample state.";

            var oldKeys = new[] { oldKey };
            var offState = _lines
                .Where(l => !IsNamed(l.Name, _stateName) && UsesKeyOffState(l.Rhs, _stateName, oldKeys))
                .Select(l => l.Name)
                .Concat(_output.Pairs
                    .Where(p => UsesKeyOffState(p.Value, _stateName, oldKeys))
                    .Select(p => $"the output's {StateScaffold.Quoted(p.Key)}"))
                .ToList();
            if (offState.Count > 0)
                return $"{string.Join(", ", offState)} uses {StateScaffold.Quoted(oldKey)} through another name. Change it by hand first.";

            // The lines being renamed don't clash with themselves (b to B is fine).
            var used = UsedNames();
            used.Remove(variable.Binding ?? "");

            var renamesNext = variable.Next != null
                && !IsNamed(variable.Next, variable.Binding)
                && !IsNamed(variable.Next, _inputName)
                && StateScaffold.IsNextLineOf(variable.Next, oldKey);
            if (renamesNext) used.Remove(variable.Next!);

            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (variable.Binding != null)
            {
                var binding = StateScaffold.BindingName(newKey);
                while (IsReserved(binding)) binding += "_";
                if (used.Contains(binding)) return $"{binding} is already used in this step.";
                renames[variable.Binding] = binding;
                used.Add(binding);
            }

            if (renamesNext)
            {
                var next = StateScaffold.NextName(newKey);
                if (used.Contains(next)) return $"{next} is already used in this step.";
                renames[variable.Next!] = next;
            }

            // Keys first: rewriting needs the state's name, which never changes here.
            foreach (var line in _lines)
                line.Rhs = RewriteKey(line.Rhs, _stateName, oldKey, newKey);
            _output.RewriteValues(value => RewriteKey(value, _stateName, oldKey, newKey));

            ApplyRenames(renames);

            _output.RenameKey(oldKey, newKey);

            if (_sample.Has(oldKey))
            {
                _sample.RenameKey(oldKey, newKey);
                _sampleChanged = true;
            }
            else if (_sample.Base != null && !SameKey(oldKey, newKey))
            {
                // A state held elsewhere keeps its old key, so carry the value across for the sample.
                var read = $"{_prefix}GetVar({_sample.Base}, {StateScaffold.Quoted(oldKey)})";
                _sample.Set(newKey, read);
                _sampleChanged = true;
            }

            variable.Key = newKey;
            return null;
        }

        public void Add(string key, string value)
        {
            // The value goes in the sample state, which can't read itself or anything below it.
            var stateAt = _lines.FindIndex(l => IsNamed(l.Name, _stateName));
            var below = _lines.Skip(stateAt).Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var readsBelow = StateStepParser.NamesIn(value, below);
            if (readsBelow.Count > 0)
                throw new InvalidOperationException(
                    $"The value of {key} can't read {string.Join(", ", readsBelow)}: it goes in {_stateName}, above that line.");

            var taken = UsedNames();
            taken.UnionWith(StateScaffold.ReservedNames);
            taken.Add(_stateName);
            if (_inputName != null) taken.Add(_inputName);

            var binding = StateScaffold.ChooseBinding(key, taken);
            var next = StateScaffold.ChooseNext(key, taken);

            var getVarAt = LastGetVarLine();
            if (getVarAt < 0)
                getVarAt = _lines.FindLastIndex(l => IsNamed(l.Name, _stateName) || IsNamed(l.Name, _inputName));
            getVarAt++;
            _lines.Insert(
                getVarAt,
                new Line(binding, $"{_prefix}GetVar({_stateName}, {StateScaffold.Quoted(key)})"));

            // Never above the line it reads.
            var nextAt = Math.Max(LastNextLine(), getVarAt) + 1;
            _lines.Insert(nextAt, new Line(next, binding));

            _output.Set(key, next);
            _sample.Set(key, value);
            _sampleChanged = true;
            _variables.Add(new Variable(key, binding, next));
        }

        public string Write()
        {
            if (_sampleChanged) StateLine.Rhs = _sample.Write(_prefix);

            var sb = new StringBuilder("=");
            FormulaFormatter.AppendLet(
                sb, 0, _lines.Select(l => (l.Name, l.Rhs)).ToList(), _output.Write(_prefix));
            return sb.ToString();
        }

        private bool IsReserved(string name) =>
            IsNamed(name, _stateName)
            || IsNamed(name, _inputName)
            || StateScaffold.ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase);

        private void ApplyRenames(Dictionary<string, string> renames)
        {
            if (renames.Count == 0) return;

            foreach (var line in _lines)
            {
                line.Rhs = LetToLambdaBuilder.ApplyRenames(line.Rhs, renames);
                if (renames.TryGetValue(line.Name, out var name)) line.Name = name;
            }

            _output.ApplyRenames(renames);

            var sampleBefore = string.Join("\n", _sample.Texts());
            _sample.ApplyRenames(renames);
            _sampleChanged |= sampleBefore != string.Join("\n", _sample.Texts());

            foreach (var variable in _variables)
            {
                if (variable.Binding != null && renames.TryGetValue(variable.Binding, out var binding))
                    variable.Binding = binding;
                if (variable.Next != null && renames.TryGetValue(variable.Next, out var next))
                    variable.Next = next;
            }
        }

        /// <summary>Every name in the step, so a new line can't capture a cell, name or call it already uses.</summary>
        private HashSet<string> UsedNames()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in _lines)
            {
                used.Add(line.Name);
                AddNames(line.Rhs);
            }

            foreach (var text in _output.Texts()) AddNames(text);
            foreach (var text in _sample.Texts()) AddNames(text);
            return used;

            void AddNames(string text)
            {
                foreach (var token in FormulaParser.Tokenize(text))
                    if (token.Type == FormulaTokenType.Name)
                        used.Add(token.Text);
            }
        }

        private int LastGetVarLine() =>
            _lines.FindLastIndex(l =>
                Root(l.Rhs) is FunctionCallNode call
                && StateStepParser.IsNamed(call.Name, "GetVar")
                && KeyOnState(call, _stateName) != null);

        private int LastNextLine() =>
            _lines.FindLastIndex(l =>
                _output.Pairs.Any(p => IsNamed(p.Value, l.Name))
                && !IsNamed(l.Name, _stateName)
                && !IsNamed(l.Name, _inputName)
                && !_variables.Any(v => IsNamed(v.Binding, l.Name)));
    }
}
