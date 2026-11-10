using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Step outputs (spec 0015, "Step outputs: a named output and a guarded step", #438/#443):
///     a LET that ends in a name bound to SetVars, and a final IF(cond, a, SetVars(…)).
///     Tim's two formulas, exactly as he wrote them.
/// </summary>
public class StateOutputFormsTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    // #438, as Tim typed it (no space after the comma in LAMBDA(a,v).
    private static readonly string Named = Lines(
        "=REDUCE(SetVars(\"visited\", VSTACK(G148), \"p\", G148), SEQUENCE(1), LAMBDA(a,v, LET(",
        "    visited, GetVar(a, \"visited\"),",
        "    p, GetVar(a, \"p\"),",
        "    item, XLOOKUP(4&p, ids, vals),",
        "    nextVisited, visited,",
        "    nextP, p,",
        "    out, SetVars(\"visited\", nextVisited, \"p\", nextP),",
        "    out",
        ")))");

    // #443, as Tim typed it.
    private static readonly string Guarded = Lines(
        "=SplitState(REDUCE(SetVars(\"visited\", G151, \"p\", G151), SEQUENCE(5), LAMBDA(a,v, LET(",
        "    visited, GetVar(a, \"visited\"),",
        "    p, GetVar(a, \"p\"),",
        "    item, XLOOKUP(4&p, ids, vals),",
        "    ns, TOCOL(POSTOCELL(TOPOS(p)+dirs*(1+(item=\"T\"))),3),",
        "    items, XLOOKUP(4&ns, ids, vals),",
        "    c, XLOOKUP(\"C\", items, ns, \"\"),",
        "    w, XLOOKUP(\"W\", items, ns, \"\"),",
        "    nextP, IF(c<>\"\", c, IF(w<>\"\", w, \"???\")),",
        "    nextVisited, VSTACK(visited, nextP),",
        "    IF(item=\"C\", a, SetVars(\"visited\", nextVisited, \"p\", nextP, \"items\", items))",
        "))),1)");

    private static readonly string GuardedPlain = Guarded["=SplitState(".Length..^3].Insert(0, "=");

    // The combined forms: a guard around the name, and a guard inside the named binding.
    private static string Combined(string outLine, string body) => Lines(
        "=REDUCE(SetVars(\"visited\", G1, \"p\", G1), SEQUENCE(3), LAMBDA(a, v, LET(",
        "    visited, GetVar(a, \"visited\"),",
        "    p, GetVar(a, \"p\"),",
        "    item, XLOOKUP(4&p, ids, vals),",
        "    nextVisited, VSTACK(visited, p),",
        "    nextP, p + 1,",
        "    " + outLine + ",",
        "    " + body,
        ")))");

    private static readonly string GuardInBinding = Combined(
        "out, IF(item=\"C\", a, SetVars(\"visited\", nextVisited, \"p\", nextP))", "out");

    private static readonly string GuardAroundName = Combined(
        "out, SetVars(\"visited\", nextVisited, \"p\", nextP)", "IF(item=\"C\", a, out)");

    private static readonly string NestedGuard = Combined(
        "out, SetVars(\"visited\", nextVisited, \"p\", nextP)",
        "IF(item=\"C\", a, IF(item=\"W\", a, out))");

    private static readonly string SwappedGuard = Combined(
        "out, SetVars(\"visited\", nextVisited, \"p\", nextP)", "IF(item<>\"C\", out, a)");

    private sealed class Book
    {
        public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? RefersTo(string name) => Names.TryGetValue(name, out var v) ? v : null;

        public StateEngine Read(string formula)
        {
            var read = StateEngines.Read(formula, RefersTo, Array.Empty<StateFoldKind>());
            Assert.Null(read.Refusal);
            Assert.NotNull(read.Engine);
            return read.Engine!;
        }

        public string Save(
            string formula, StateFoldKind? fold, string? name,
            Func<IReadOnlyList<StateEditRow>, IReadOnlyList<StateEditRow>>? rows = null)
        {
            var engine = Read(formula);
            var result = StateEngines.Apply(
                engine, new StateEngineChange(rows?.Invoke(engine.Rows) ?? engine.Rows, fold, name), RefersTo, k => k.Name);
            Assert.Null(result.Error);
            if (result.DefinitionName != null) Names[result.DefinitionName] = result.Definition!;
            return result.Formula!;
        }
    }

    private static IReadOnlyList<StateEditRow> Add(IReadOnlyList<StateEditRow> rows, string key, string value) =>
        rows.Append(new StateEditRow(null, key, value)).ToList();

    private static IReadOnlyList<StateEditRow> Rename(IReadOnlyList<StateEditRow> rows, string from, string to) =>
        rows.Select(r => r.Key == from ? r with { Key = to } : r).ToList();

    private static IReadOnlyList<StateEditRow> Remove(IReadOnlyList<StateEditRow> rows, string key) =>
        rows.Select(r => r.Key == key ? r with { Key = "" } : r).ToList();

    // ------------------------------------------------------------ recognition

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void EveryForm_IsRead(int which)
    {
        var formula = new[] { Named, Guarded, GuardInBinding, GuardAroundName, NestedGuard, SwappedGuard, GuardedPlain }[which];
        var engine = new Book().Read(formula);

        Assert.Equal("REDUCE", engine.Fold!.Name);
        Assert.Equal(new[] { "a", "v" }, engine.Parameters);
        Assert.Equal(new[] { "visited", "p" }, engine.Rows.Select(r => r.Key).Take(2));
    }

    [Fact]
    public void Guarded_KeepsTheTextAroundTheFold()
    {
        var engine = new Book().Read(Guarded);

        Assert.Equal("=SplitState(", engine.Before);
        Assert.Equal(",1)", engine.After);
        Assert.Equal(new[] { "visited", "p", "items" }, engine.Rows.Select(r => r.Key));
    }

    [Fact]
    public void Named_ParsesAsAStateStep()
    {
        var parsed = LetParser.Parse(
            "=LET(a, SetVars(\"visited\", G148, \"p\", G148), v, 1, visited, GetVar(a, \"visited\"), p, GetVar(a, \"p\"), "
            + "nextVisited, visited, nextP, p, out, SetVars(\"visited\", nextVisited, \"p\", nextP), out)");
        var step = StateStepParser.TryParse(parsed)!;

        Assert.Equal(StateFamily.SetVars, step.Family);
        Assert.Equal("a", step.StateBinding.Name);
        Assert.Equal(new[] { "visited", "p" }, step.Variables.Select(v => v.Key));
        Assert.Equal(new[] { "nextVisited", "nextP" }, step.Variables.Select(v => v.NextLine!.Name));
    }

    [Fact]
    public void Guarded_ParsesAsAStateStep()
    {
        var parsed = LetParser.Parse(
            "=LET(a, SetVars(\"visited\", G151, \"p\", G151), v, 1, visited, GetVar(a, \"visited\"), p, GetVar(a, \"p\"), "
            + "nextVisited, VSTACK(visited, p), nextP, p, IF(v=\"C\", SetVars(\"visited\", nextVisited, \"p\", nextP), a))");
        var step = StateStepParser.TryParse(parsed)!;

        Assert.Equal("a", step.StateBinding.Name);
        Assert.Equal(new[] { "visited", "p" }, step.Variables.Select(x => x.Key));
    }

    [Theory]
    [InlineData("=LET(a, SetVars(\"p\", 1), v, 1, p, GetVar(a, \"p\"), np, p, IF(v=1, b, SetVars(\"p\", np)))")]
    [InlineData("=LET(a, SetVars(\"p\", 1), v, 1, p, GetVar(a, \"p\"), np, p, a)")]
    [InlineData("=LET(a, SetVars(\"p\", 1), v, 1, p, GetVar(a, \"p\"), np, p, IF(v=1, np, a))")]
    public void NotAStep_WhenTheOtherBranchIsntTheState(string formula)
    {
        Assert.Null(StateStepParser.TryParse(LetParser.Parse(formula)));
    }

    // ------------------------------------------------------------ edits keep Tim's form

    [Fact]
    public void Named_AddRenameRemove()
    {
        var book = new Book();

        var added = book.Save(Named, StateFoldKind.Reduce, null, r => Add(r, "q", "0"));
        Assert.Contains("    out, SetVars(\"visited\", nextVisited, \"p\", nextP, \"q\", nextQ),", added);
        Assert.EndsWith("\n    out\n)))", added);
        Assert.Contains("REDUCE(SetVars(\"visited\", VSTACK(G148), \"p\", G148, \"q\", 0), SEQUENCE(1)", added);

        var renamed = book.Save(Named, StateFoldKind.Reduce, null, r => Rename(r, "p", "pos"));
        Assert.Contains("    out, SetVars(\"visited\", nextVisited, \"pos\", nextPos),", renamed);
        Assert.Contains("GetVar(a, \"pos\")", renamed);
        Assert.EndsWith("\n    out\n)))", renamed);

        var removed = book.Save(Named, StateFoldKind.Reduce, null, r => Remove(r, "visited"));
        Assert.Contains("    out, SetVars(\"p\", nextP),", removed);
        Assert.DoesNotContain("visited", removed);
        Assert.EndsWith("\n    out\n)))", removed);
    }

    [Fact]
    public void Guarded_AddRenameRemove_KeepTheGuardAndTheWrapper()
    {
        var book = new Book();
        const string guard = "IF(item=\"C\", a, SetVars(";

        var added = book.Save(Guarded, StateFoldKind.Reduce, null, r => Add(r, "q", "0"));
        Assert.Contains(guard + "\"visited\", nextVisited, \"p\", nextP, \"items\", items, \"q\", nextQ))", added);
        Assert.StartsWith("=SplitState(REDUCE(SetVars(\"visited\", G151, \"p\", G151, \"q\", 0), SEQUENCE(5)", added);
        Assert.EndsWith("\n)))" + ",1)", added);

        var renamed = book.Save(Guarded, StateFoldKind.Reduce, null, r => Rename(r, "p", "pos"));
        Assert.Contains(guard + "\"visited\", nextVisited, \"pos\", nextPos, \"items\", items))", renamed);
        Assert.Contains("XLOOKUP(4&pos, ids, vals)", renamed);
        Assert.EndsWith(",1)", renamed);

        var removed = book.Save(Guarded, StateFoldKind.Reduce, null, r => Remove(r, "visited"));
        Assert.Contains(guard + "\"p\", nextP, \"items\", items))", removed);
        Assert.DoesNotContain("visited", removed, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("=SplitState(REDUCE(SetVars(\"p\", G151)", removed);
    }

    [Fact]
    public void RenamingAKeyTheGuardReads_RewritesTheGuard()
    {
        var formula = Guarded.Replace("IF(item=\"C\", a,", "IF(GetVar(a, \"p\")=\"C\", a,");
        var renamed = new Book().Save(formula, StateFoldKind.Reduce, null, r => Rename(r, "p", "pos"));

        Assert.Contains("IF(GetVar(a, \"pos\")=\"C\", a, SetVars(", renamed);
    }

    [Fact]
    public void RemovingAKeyTheGuardReads_IsBlocked()
    {
        var formula = Guarded.Replace("IF(item=\"C\", a,", "IF(GetVar(a, \"visited\")=\"C\", a,");
        var book = new Book();
        var engine = book.Read(formula);
        var result = StateEngines.Apply(
            engine, new StateEngineChange(Remove(engine.Rows, "visited"), StateFoldKind.Reduce, null), book.RefersTo, k => k.Name);

        Assert.Null(result.Formula);
        Assert.Contains("visited", string.Join(" ", result.Blockers));
    }

    [Theory]
    [InlineData(2, "out, IF(item=\"C\", a, SetVars(\"visited\", nextVisited, \"p\", nextP, \"q\", nextQ)),", "\n    out\n)))")]
    [InlineData(3, "out, SetVars(\"visited\", nextVisited, \"p\", nextP, \"q\", nextQ),", "\n    IF(item=\"C\", a, out)\n)))")]
    [InlineData(4, "out, SetVars(\"visited\", nextVisited, \"p\", nextP, \"q\", nextQ),", "\n    IF(item=\"C\", a, IF(item=\"W\", a, out))\n)))")]
    [InlineData(5, "out, SetVars(\"visited\", nextVisited, \"p\", nextP, \"q\", nextQ),", "\n    IF(item<>\"C\", out, a)\n)))")]
    public void CombinedForms_AddAVariable(int which, string expectedLine, string expectedEnd)
    {
        var formula = new[] { Named, Guarded, GuardInBinding, GuardAroundName, NestedGuard, SwappedGuard }[which];
        var added = new Book().Save(formula, StateFoldKind.Reduce, null, r => Add(r, "q", "0"));

        Assert.Contains("    " + expectedLine, added);
        Assert.EndsWith(expectedEnd, added);
    }

    [Fact]
    public void CombinedForms_Rename()
    {
        var renamed = new Book().Save(GuardAroundName, StateFoldKind.Reduce, null, r => Rename(r, "visited", "seen"));

        Assert.Contains("out, SetVars(\"seen\", nextSeen, \"p\", nextP),", renamed);
        Assert.EndsWith("IF(item=\"C\", a, out)\n)))", renamed);
    }

    // ------------------------------------------------------------ forms and round trips

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FoldToLetStepToFold_GivesTheSameText(int which)
    {
        var book = new Book();
        var start = book.Save(new[] { Named, Guarded, GuardInBinding, GuardAroundName, NestedGuard }[which].Replace("(a,v,", "(a, v,"),
            StateFoldKind.Reduce, null);
        var step = book.Save(start, null, null);

        Assert.StartsWith("=LET(\n    _seed, ", step);
        Assert.NotNull(StateStepParser.TryParse(LetParser.Parse(step)));
        Assert.Equal(start, book.Save(step, StateFoldKind.Reduce, null));
    }

    [Fact]
    public void Guarded_LetStepRoundTrip()
    {
        var book = new Book();
        var start = book.Save(GuardedPlain, StateFoldKind.Reduce, null);
        var step = book.Save(start, null, null);

        Assert.Contains("    IF(item=\"C\", a, SetVars(", step);
        Assert.Equal(start, book.Save(step, StateFoldKind.Reduce, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InlineToNamedToInline_GivesTheSameText(int which)
    {
        var book = new Book();
        var start = book.Save(new[] { Named, Guarded, GuardInBinding }[which], StateFoldKind.Reduce, null);
        var named = book.Save(start, StateFoldKind.Reduce, "walk");

        Assert.Contains("walk)", named);
        Assert.Contains(which == 1 ? "IF(item=\"C\", a, SetVars(" : "out", book.Names["walk"]);
        Assert.Equal(start, book.Save(named, StateFoldKind.Reduce, null));
    }

    [Fact]
    public void Guarded_NamedKeepsTheSplitStateWrapper()
    {
        var book = new Book();
        var named = book.Save(Guarded, StateFoldKind.Reduce, "walk");

        Assert.StartsWith("=SplitState(REDUCE(", named);
        Assert.EndsWith(", walk),1)", named);
        Assert.Contains("IF(item=\"C\", a, SetVars(", book.Names["walk"]);

        var edited = book.Save(named, StateFoldKind.Reduce, "walk", r => Add(r, "q", "0"));
        Assert.EndsWith(", walk),1)", edited);
        Assert.Contains("\"q\", nextQ))", book.Names["walk"]);
    }

    [Fact]
    public void Guarded_CantUnfoldInsideASplitState()
    {
        var book = new Book();
        var engine = book.Read(Guarded);
        var result = StateEngines.Apply(engine, new StateEngineChange(engine.Rows, null, null), book.RefersTo, k => k.Name);

        Assert.Null(result.Formula);
    }

    // ------------------------------------------------------------ Edit state (0011)

    [Fact]
    public void StateEdit_ReadsAndEditsTheLetStep()
    {
        var book = new Book();
        var step = book.Save(Named.Replace("(a,v,", "(a, v,"), null, null);

        var view = StateEdit.Read(step)!;
        Assert.Null(view.Refusal);
        Assert.Equal(new[] { "visited", "p" }, view.Rows.Select(r => r.Key));

        var result = StateEdit.Apply(step, view.Rows.Select(r => r.Key == "p" ? r with { Key = "pos" } : r).ToList());
        Assert.Null(result.Error);
        Assert.Contains("    out, SetVars(\"visited\", nextVisited, \"pos\", nextPos),\n    out\n)", result.Formula);
    }

    // ------------------------------------------------------------ Edit Lambda's k stepper

    [Fact]
    public void Stepper_StepsANamedOutputAndAGuardedStep()
    {
        string? Names(string name) => name switch
        {
            "walkNamed" => "=LAMBDA(a, v, LET(p, GetVar(a, \"p\"), np, p + v, out, SetVars(\"p\", np), out))",
            "walkGuard" => "=LAMBDA(a, v, LET(p, GetVar(a, \"p\"), np, p + v, IF(p > 5, a, SetVars(\"p\", np))))",
            _ => null
        };

        var named = StateStepper.Build("=REDUCE(SetVars(\"p\", 0), SEQUENCE(3), walkNamed)", Names)!;
        Assert.Null(named.Refusal);
        Assert.Contains("k, 1,", named.Formula);
        Assert.Contains("INDEX(SCAN(SetVars(\"p\", 0), SEQUENCE(3), walkNamed), k - 1)", named.Formula);
        Assert.Contains("out, SetVars(\"p\", np),", named.Formula);

        var guarded = StateStepper.Build("=SplitState(REDUCE(SetVars(\"p\", 0), SEQUENCE(3), walkGuard), 1)", Names)!;
        Assert.Null(guarded.Refusal);
        Assert.Contains("k, 1,", guarded.Formula);
        Assert.Contains("IF(p > 5, a, SetVars(\"p\", np))", guarded.Formula);
    }
}
