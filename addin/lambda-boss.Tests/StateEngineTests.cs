using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     /State from any form (spec 0015, lambda-boss#429): read, edit variables, change form and
///     name, and the round trips. Tim's p/visited walk and the R1 L12 robot are the examples.
/// </summary>
public class StateEngineTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static readonly StateFoldArgument[] UntilArgs =
    {
        new("seed", true, false), new("cases", true, false), new("fn", true, true), new("stop", false, true)
    };

    private static readonly StateFoldKind ReduceUntil = new("REDUCEUNTIL", 0, 1, 2, new[] { "acc", "current", "i" }, "loops", UntilArgs);
    private static readonly StateFoldKind ScanUntil = new("SCANUNTIL", 0, 1, 2, new[] { "acc", "current", "i" }, "loops", UntilArgs);
    private static readonly StateFoldKind ReduceWhile = new("REDUCEWHILE", 0, 1, 2, new[] { "acc", "current", "i" }, "loops",
        new StateFoldArgument[] { new("seed", true, false), new("cases", true, false), new("fn", true, true), new("continue", true, true) });
    private static readonly StateFoldKind FoldRows = new("FOLDROWS", 2, 0, 1, new[] { "accSoFar", "currentRow", "i" }, "loops",
        new StateFoldArgument[] { new("items", true, false), new("fn", true, true), new("seed", false, false) });
    private static readonly IReadOnlyList<StateFoldKind> Kinds = new[] { ReduceUntil, ScanUntil, ReduceWhile, FoldRows };

    // Tim's walk from the issue, as /State writes it on an empty cell.
    private static readonly string NewWalk = Lines(
        "=REDUCE(SetVars(\"p\", \"\", \"visited\", \"\"), SEQUENCE(1), LAMBDA(a, v, LET(",
        "    p, GetVar(a, \"p\"),",
        "    visited, GetVar(a, \"visited\"),",
        "    nextP, p,",
        "    nextVisited, visited,",
        "    SetVars(\"p\", nextP, \"visited\", nextVisited)",
        ")))");

    // The R1 L12 robot as an inline fold: p 0, b 4; F forward, B back, C charges; battery 0..6.
    private static readonly string Robot = Lines(
        "=REDUCE(SetVars(\"p\", 0, \"b\", 4), EXPLODE(G270), LAMBDA(a, v, LET(",
        "    p, GetVar(a, \"p\"),",
        "    b, GetVar(a, \"b\"),",
        "    nextP, p + IF(b > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)),",
        "    nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)),",
        "    SetVars(\"p\", nextP, \"b\", nextB)",
        ")))");

    private static readonly string RobotLet = Lines(
        "=LET(",
        "    _seed, SetVars(\"p\", 0, \"b\", 4),",
        "    _inputs, EXPLODE(G270),",
        "    a, _seed,",
        "    v, INDEX(_inputs, 1),",
        "    p, GetVar(a, \"p\"),",
        "    b, GetVar(a, \"b\"),",
        "    nextP, p + IF(b > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)),",
        "    nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)),",
        "    SetVars(\"p\", nextP, \"b\", nextB)",
        ")");

    private static readonly string RobotDefinition = Lines(
        "=LAMBDA(",
        "    a,",
        "    v,",
        "    LET(",
        "        p, GetVar(a, \"p\"),",
        "        b, GetVar(a, \"b\"),",
        "        nextP, p + IF(b > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)),",
        "        nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)),",
        "        SetVars(\"p\", nextP, \"b\", nextB)",
        "    )",
        ")");

    private sealed class Book
    {
        public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? RefersTo(string name) => Names.TryGetValue(name, out var v) ? v : null;

        public StateEngine Read(string formula)
        {
            var read = StateEngines.Read(formula, RefersTo, Kinds);
            Assert.Null(read.Refusal);
            Assert.NotNull(read.Engine);
            return read.Engine!;
        }

        public StateEngineResult Apply(StateEngine engine, StateFoldKind? fold, string? name, IReadOnlyList<StateEditRow>? rows = null) =>
            StateEngines.Apply(engine, new StateEngineChange(rows ?? engine.Rows, fold, name), RefersTo, k => k.Name);

        /// <summary>Applies the change and stores any definition, as Save does.</summary>
        public string Save(string formula, StateFoldKind? fold, string? name, Func<IReadOnlyList<StateEditRow>, IReadOnlyList<StateEditRow>>? rows = null)
        {
            var engine = Read(formula);
            var result = Apply(engine, fold, name, rows?.Invoke(engine.Rows));
            Assert.Null(result.Error);
            if (result.DefinitionName != null) Names[result.DefinitionName] = result.Definition!;
            return result.Formula!;
        }
    }

    private static IReadOnlyList<StateEditRow> Add(IReadOnlyList<StateEditRow> rows, string key) =>
        rows.Append(new StateEditRow(null, key, "")).ToList();

    private static IReadOnlyList<StateEditRow> Rename(IReadOnlyList<StateEditRow> rows, string from, string to) =>
        rows.Select(r => r.Key == from ? r with { Key = to } : r).ToList();

    private static IReadOnlyList<StateEditRow> Remove(IReadOnlyList<StateEditRow> rows, string key) =>
        rows.Select(r => r.Key == key ? r with { Key = "" } : r).ToList();

    // ------------------------------------------------------------ empty cell

    [Fact]
    public void EmptyCell_WritesTheIssuesShape()
    {
        var read = StateEngines.Read("", _ => null, Kinds);
        Assert.True(read.IsEmpty);

        var result = StateEngines.Apply(
            StateEngine.New(""),
            new StateEngineChange(new[] { new StateEditRow(null, "p", ""), new StateEditRow(null, "visited", "") }, StateFoldKind.Reduce, null),
            _ => null, k => k.Name);

        Assert.Null(result.Error);
        Assert.Equal(NewWalk, result.Formula);
    }

    [Fact]
    public void EmptyCell_NeedsAVariable()
    {
        var result = StateEngines.Apply(StateEngine.New(""), new StateEngineChange(Array.Empty<StateEditRow>(), StateFoldKind.Reduce, null), _ => null, k => k.Name);
        Assert.Null(result.Formula);
    }

    [Fact]
    public void EmptyCell_ToEachForm()
    {
        var rows = new[] { new StateEditRow(null, "p", ""), new StateEditRow(null, "visited", "") };
        var book = new Book();

        var let = StateEngines.Apply(StateEngine.New(""), new StateEngineChange(rows, null, null), book.RefersTo, k => k.Name);
        Assert.Equal(Lines(
            "=LET(",
            "    _seed, SetVars(\"p\", \"\", \"visited\", \"\"),",
            "    _inputs, SEQUENCE(1),",
            "    a, _seed,",
            "    v, INDEX(_inputs, 1),",
            "    p, GetVar(a, \"p\"),",
            "    visited, GetVar(a, \"visited\"),",
            "    nextP, p,",
            "    nextVisited, visited,",
            "    SetVars(\"p\", nextP, \"visited\", nextVisited)",
            ")"), let.Formula);

        var until = StateEngines.Apply(StateEngine.New("loops."), new StateEngineChange(rows, ReduceUntil, null), book.RefersTo, k => "loops." + k.Name);
        Assert.StartsWith("=loops.REDUCEUNTIL(loops.SetVars(\"p\", \"\", \"visited\", \"\"), SEQUENCE(1), LAMBDA(a, v, i, LET(", until.Formula);

        var foldRows = StateEngines.Apply(StateEngine.New(""), new StateEngineChange(rows, FoldRows, null), book.RefersTo, k => k.Name);
        Assert.StartsWith("=FOLDROWS(SEQUENCE(1), LAMBDA(a, v, i, LET(", foldRows.Formula);
        Assert.EndsWith(")), SetVars(\"p\", \"\", \"visited\", \"\"))", foldRows.Formula);

        var named = StateEngines.Apply(StateEngine.New(""), new StateEngineChange(rows, StateFoldKind.Reduce, "walk"), book.RefersTo, k => k.Name);
        Assert.Equal("=REDUCE(SetVars(\"p\", \"\", \"visited\", \"\"), SEQUENCE(1), walk)", named.Formula);
        Assert.Equal("walk", named.DefinitionName);
        Assert.StartsWith("=LAMBDA(\n    a,\n    v,\n    LET(\n        p, GetVar(a, \"p\"),", named.Definition);
    }

    // ------------------------------------------------------------ reading

    [Fact]
    public void Read_InlineFold()
    {
        var engine = new Book().Read(Robot);

        Assert.Equal("REDUCE", engine.Fold!.Name);
        Assert.Null(engine.StepName);
        Assert.Equal("SetVars(\"p\", 0, \"b\", 4)", engine.Seed);
        Assert.Equal("EXPLODE(G270)", engine.Inputs);
        Assert.Equal(new[] { "a", "v" }, engine.Parameters);
        Assert.Equal(new[] { "p", "b" }, engine.Rows.Select(r => r.Key));
    }

    [Fact]
    public void Read_NamedFold()
    {
        var book = new Book();
        book.Names["moveRobot"] = RobotDefinition;

        var engine = book.Read("=REDUCE(SetVars(\"p\", 0, \"b\", 4), EXPLODE(G270), moveRobot)");

        Assert.Equal("moveRobot", engine.StepName);
        Assert.Equal(new[] { "p", "b" }, engine.Rows.Select(r => r.Key));
    }

    [Fact]
    public void Read_LetStep()
    {
        var engine = new Book().Read(RobotLet);

        Assert.Null(engine.Fold);
        Assert.Equal("SetVars(\"p\", 0, \"b\", 4)", engine.Seed);
        Assert.Equal("EXPLODE(G270)", engine.Inputs);
        Assert.Equal(4, engine.Bindings.Count);
    }

    [Fact]
    public void Read_A0011Step()
    {
        var engine = new Book().Read(Lines(
            "=LET(",
            "    a, SetVars(\"p\", 0, \"b\", 4),",
            "    v, H270,",
            "    p, GetVar(a, \"p\"),",
            "    b, GetVar(a, \"b\"),",
            "    nextP, p,",
            "    nextB, b,",
            "    SetVars(\"p\", nextP, \"b\", nextB)",
            ")"));

        Assert.Equal("SetVars(\"p\", 0, \"b\", 4)", engine.Seed);
        Assert.Equal("H270", engine.Inputs);
    }

    [Fact]
    public void Read_ScanAndLibraryFolds()
    {
        var book = new Book();
        Assert.Equal("SCAN", book.Read(Robot.Replace("=REDUCE(", "=SCAN(")).Fold!.Name);

        var until = book.Read(Robot.Replace("=REDUCE(", "=loops.SCANUNTIL(").Replace("LAMBDA(a, v,", "LAMBDA(a, v, i,"));
        Assert.Equal("SCANUNTIL", until.Fold!.Name);
        Assert.Equal("loops.SCANUNTIL", until.FoldCallName);
        Assert.Equal(new[] { "a", "v", "i" }, until.Parameters);
    }

    [Fact]
    public void Read_RefusesAnHstackState()
    {
        var read = StateEngines.Read(
            "=REDUCE(HSTACK(0, 4), G1:G9, LAMBDA(a, v, LET(p, INDEX(a, 1), b, INDEX(a, 2), nextP, p + v, nextB, b - v, HSTACK(nextP, nextB))))",
            _ => null, Kinds);

        Assert.Null(read.Engine);
        Assert.Contains("HSTACK", read.Refusal);
    }

    [Fact]
    public void Read_RefusesAFormulaThatIsntAStep()
    {
        var read = StateEngines.Read("=SUM(A1:A3)", _ => null, Kinds);
        Assert.Null(read.Engine);
        Assert.NotNull(read.Refusal);
    }

    [Fact]
    public void Read_SeveralFolds_TakesTheFirstAndSaysSo()
    {
        var two = "=" + Robot[1..] + "&" + Robot[1..].Replace("REDUCE(", "SCAN(");
        var engine = new Book().Read(two);

        Assert.Equal("REDUCE", engine.Fold!.Name);
        Assert.Contains("2 state folds", engine.Note);
    }

    // ------------------------------------------------------------ round trips

    [Fact]
    public void FoldToLetStep_AndBack_IsTheSameText()
    {
        var book = new Book();

        var let = book.Save(Robot, null, null);
        Assert.Equal(RobotLet, let);

        var back = book.Save(let, StateFoldKind.Reduce, null);
        Assert.Equal(Robot, back);
    }

    [Fact]
    public void LibraryFold_RoundTripsThroughTheLetStep()
    {
        var book = new Book();
        var until = Robot.Replace("=REDUCE(", "=loops.REDUCEUNTIL(").Replace("LAMBDA(a, v,", "LAMBDA(a, v, i,");

        var let = book.Save(until, null, null);
        Assert.Contains("    v, INDEX(_inputs, 1),\n    i, 1,\n    p, GetVar", let);

        Assert.Equal(until, book.Save(let, ReduceUntil, null).Replace("=REDUCEUNTIL(", "=loops.REDUCEUNTIL("));
    }

    [Fact]
    public void InlineToNamed_AndBack_IsTheSameText()
    {
        var book = new Book();

        var named = book.Save(Robot, StateFoldKind.Reduce, "moveRobot");
        Assert.Equal("=REDUCE(SetVars(\"p\", 0, \"b\", 4), EXPLODE(G270), moveRobot)", named);
        Assert.Equal(RobotDefinition, book.Names["moveRobot"]);

        var engine = book.Read(named);
        var inline = book.Apply(engine, StateFoldKind.Reduce, null);
        Assert.Equal(Robot, inline.Formula);
        Assert.Contains(inline.Notes, n => n.Contains("moveRobot stays in the workbook"));
    }

    [Fact]
    public void Named_Unfolds_WithAKStepper_AndFoldsBack()
    {
        var book = new Book();
        var named = book.Save(Robot, StateFoldKind.Reduce, "moveRobot");

        var let = book.Save(named, null, "moveRobot");
        Assert.Equal(Lines(
            "=LET(",
            "    _seed, SetVars(\"p\", 0, \"b\", 4),",
            "    _inputs, EXPLODE(G270),",
            "    k, 1,",
            "    a, IF(k = 1, _seed, INDEX(SCAN(_seed, _inputs, moveRobot), k - 1)),",
            "    v, INDEX(_inputs, k),",
            "    p, GetVar(a, \"p\"),",
            "    b, GetVar(a, \"b\"),",
            "    nextP, p + IF(b > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)),",
            "    nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)),",
            "    SetVars(\"p\", nextP, \"b\", nextB)",
            ")"), let);

        var engine = book.Read(let);
        Assert.Equal("moveRobot", engine.StepName);

        Assert.Equal(named, book.Save(let, StateFoldKind.Reduce, "moveRobot"));
    }

    [Fact]
    public void NamedLibraryFold_StepperPassesTheExtraArgument()
    {
        var book = new Book();
        var until = Robot.Replace("=REDUCE(", "=REDUCEUNTIL(").Replace("LAMBDA(a, v,", "LAMBDA(a, v, i,");
        var named = book.Save(until, ReduceUntil, "moveRobot");
        Assert.Equal("=REDUCEUNTIL(SetVars(\"p\", 0, \"b\", 4), EXPLODE(G270), moveRobot)", named);

        var let = book.Save(named, null, "moveRobot");
        Assert.Contains("a, IF(k = 1, _seed, INDEX(SCAN(_seed, SEQUENCE(k - 1), LAMBDA(_s, _j, moveRobot(_s, INDEX(_inputs, _j), _j))), k - 1)),", let);
        Assert.Contains("    i, k,\n", let);

        Assert.Equal(named, book.Save(let, ReduceUntil, "moveRobot"));
    }

    [Fact]
    public void FoldInsideAFormula_KeepsItsWrapper_AndWontUnfold()
    {
        var book = new Book();
        var wrapped = "=GetVar(" + Robot[1..] + ", \"p\")";

        var engine = book.Read(wrapped);
        var named = book.Apply(engine, StateFoldKind.Reduce, "moveRobot");
        Assert.Equal("=GetVar(REDUCE(SetVars(\"p\", 0, \"b\", 4), EXPLODE(G270), moveRobot), \"p\")", named.Formula);

        Assert.NotNull(book.Apply(engine, null, null).Error);
    }

    // ------------------------------------------------------------ form changes

    [Fact]
    public void ChangingTheFold_SetsTheParameters()
    {
        var book = new Book();
        var until = book.Save(Robot, ReduceUntil, null);
        Assert.StartsWith("=REDUCEUNTIL(SetVars(\"p\", 0, \"b\", 4), EXPLODE(G270), LAMBDA(a, v, i, LET(", until);

        var rows = book.Save(until, FoldRows, null);
        Assert.StartsWith("=FOLDROWS(EXPLODE(G270), LAMBDA(a, v, i, LET(", rows);
        Assert.EndsWith(")), SetVars(\"p\", 0, \"b\", 4))", rows);

        Assert.Equal(Robot, book.Save(until, StateFoldKind.Reduce, null));
    }

    [Fact]
    public void DroppingAParameterTheRulesRead_IsRefused()
    {
        var book = new Book();
        var until = Robot.Replace("=REDUCE(", "=REDUCEUNTIL(").Replace("LAMBDA(a, v,", "LAMBDA(a, v, i,")
            .Replace("nextP, p + IF", "nextP, i + p + IF");

        var result = book.Apply(book.Read(until), StateFoldKind.Reduce, null);
        Assert.Contains("rules read i", result.Error);
    }

    [Fact]
    public void ChangingTheFold_CarriesOtherArgumentsByName()
    {
        var book = new Book();
        var until = Robot.Replace("=REDUCE(", "=REDUCEUNTIL(").Replace("LAMBDA(a, v,", "LAMBDA(a, v, i,")
            .Replace(")))", ")), LAMBDA(a, v, i, GetVar(a, \"b\") = 0))");

        var same = book.Save(until, ReduceUntil, null, r => Add(r, "c"));
        Assert.EndsWith("LAMBDA(a, v, i, GetVar(a, \"b\") = 0))", same);

        var scan = book.Apply(book.Read(until), ScanUntil, null);
        Assert.EndsWith("LAMBDA(a, v, i, GetVar(a, \"b\") = 0))", scan.Formula);

        var other = book.Apply(book.Read(until), StateFoldKind.Reduce, "moveRobot");
        Assert.DoesNotContain("= 0)", other.Formula);
        Assert.Contains(other.Notes, n => n.Contains("isn't written"));
    }

    // ------------------------------------------------------------ variables, from each form

    public static IEnumerable<object[]> Forms()
    {
        yield return new object[] { "inline fold", "" };
        yield return new object[] { "named fold", "moveRobot" };
        yield return new object[] { "let step", "" };
        yield return new object[] { "named let step", "moveRobot" };
    }

    private static string InForm(Book book, string form)
    {
        return form switch
        {
            "inline fold" => Robot,
            "named fold" => book.Save(Robot, StateFoldKind.Reduce, "moveRobot"),
            "let step" => book.Save(Robot, null, null),
            "named let step" => book.Save(book.Save(Robot, StateFoldKind.Reduce, "moveRobot"), null, "moveRobot"),
            _ => throw new ArgumentOutOfRangeException(nameof(form))
        };
    }

    private static StateFoldKind? FoldOf(string form) => form.Contains("fold") ? StateFoldKind.Reduce : null;

    [Theory]
    [MemberData(nameof(Forms))]
    public void Add_FromEachForm(string form, string name)
    {
        var book = new Book();
        var cell = InForm(book, form);

        var saved = book.Save(cell, FoldOf(form), name.Length == 0 ? null : name, r => Add(r, "c"));
        var inline = book.Save(saved, StateFoldKind.Reduce, null);

        Assert.Equal(Lines(
            "=REDUCE(SetVars(\"p\", 0, \"b\", 4, \"c\", \"\"), EXPLODE(G270), LAMBDA(a, v, LET(",
            "    p, GetVar(a, \"p\"),",
            "    b, GetVar(a, \"b\"),",
            "    c_, GetVar(a, \"c\"),",
            "    nextP, p + IF(b > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)),",
            "    nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)),",
            "    nextC, c_,",
            "    SetVars(\"p\", nextP, \"b\", nextB, \"c\", nextC)",
            ")))"), inline);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void Rename_FromEachForm(string form, string name)
    {
        var book = new Book();
        var cell = InForm(book, form);

        var saved = book.Save(cell, FoldOf(form), name.Length == 0 ? null : name, r => Rename(r, "b", "bat"));
        var inline = book.Save(saved, StateFoldKind.Reduce, null);

        Assert.Equal(Lines(
            "=REDUCE(SetVars(\"p\", 0, \"bat\", 4), EXPLODE(G270), LAMBDA(a, v, LET(",
            "    p, GetVar(a, \"p\"),",
            "    bat, GetVar(a, \"bat\"),",
            "    nextP, p + IF(bat > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)),",
            "    nextBat, MEDIAN(0, 6, IF(v = \"C\", bat + 2, bat - 1)),",
            "    SetVars(\"p\", nextP, \"bat\", nextBat)",
            ")))"), inline);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void Remove_FromEachForm(string form, string name)
    {
        var book = new Book();
        var cell = InForm(book, form);
        var added = book.Save(cell, FoldOf(form), name.Length == 0 ? null : name, r => Add(r, "visited"));

        var saved = book.Save(added, FoldOf(form), name.Length == 0 ? null : name, r => Remove(r, "visited"));
        var inline = book.Save(saved, StateFoldKind.Reduce, null);

        Assert.Equal(Robot, inline);
    }

    [Fact]
    public void Remove_ARuleStillReads_IsBlocked()
    {
        var book = new Book();
        var result = book.Apply(book.Read(Robot), StateFoldKind.Reduce, null, Remove(book.Read(Robot).Rows, "b"));

        Assert.Null(result.Formula);
        Assert.NotEmpty(result.Blockers);
    }

    [Fact]
    public void NamedStep_IsReRegisteredWithTheEdit()
    {
        var book = new Book();
        var named = book.Save(Robot, StateFoldKind.Reduce, "moveRobot");

        book.Save(named, StateFoldKind.Reduce, "moveRobot", r => Add(r, "c"));

        Assert.Contains("c_, GetVar(a, \"c\")", book.Names["moveRobot"]);
    }

    [Fact]
    public void Name_ForSomethingElse_IsRefused()
    {
        var book = new Book();
        book.Names["data"] = "=Sheet1!$A$1:$A$9";

        Assert.Contains("something else", book.Apply(book.Read(Robot), StateFoldKind.Reduce, "data").Error);
        Assert.NotNull(book.Apply(book.Read(Robot), StateFoldKind.Reduce, "A1").Error);
    }

    [Fact]
    public void Name_ReplacingAnotherLambda_SaysSo()
    {
        var book = new Book();
        book.Names["step"] = "=LAMBDA(x, x + 1)";

        var result = book.Apply(book.Read(Robot), StateFoldKind.Reduce, "step");
        Assert.Contains(result.Notes, n => n.Contains("replaces"));
    }

    [Fact]
    public void Named_MakesRuleCellsAbsolute()
    {
        var book = new Book();
        book.Save(Robot.Replace("MEDIAN(0, 6,", "MEDIAN(0, K2,"), StateFoldKind.Reduce, "moveRobot");

        Assert.Contains("MEDIAN(0, $K$2,", book.Names["moveRobot"]);
    }

    // ------------------------------------------------------------ library metadata

    [Fact]
    public void FromDoc_ReadsTheLibraryFolds()
    {
        static LambdaDoc Doc(string name, params (string Name, bool Required, string Description)[] ps) =>
            new(name, "", "", ps.Select(p => new LambdaParamDoc(p.Name, p.Required, p.Description)).ToList(), "");

        var until = StateFoldKind.FromDoc(Doc("REDUCEUNTIL",
            ("seed", true, "Initial accumulator."),
            ("cases", true, "Array to iterate over."),
            ("fn", true, "LAMBDA(acc, current, i) returning the new accumulator. i is 1-based."),
            ("stop", false, "LAMBDA(acc, current, i) returning TRUE to halt.")), "loops")!;
        Assert.Equal((0, 1, 2), (until.SeedIndex, until.InputsIndex, until.StepIndex));
        Assert.Equal(new[] { "acc", "current", "i" }, until.StepParameters);

        var rows = StateFoldKind.FromDoc(Doc("FOLDROWS",
            ("items", true, "The rows to fold over."),
            ("fn", true, "LAMBDA(accSoFar, currentRow, i). accSoFar is the table built so far."),
            ("seed", false, "Initial accumulator rows.")), "loops")!;
        Assert.Equal((2, 0, 1), (rows.SeedIndex, rows.InputsIndex, rows.StepIndex));

        Assert.Null(StateFoldKind.FromDoc(Doc("FIXPOINT",
            ("init", true, "Starting value."),
            ("fn", true, "LAMBDA(state) returning the next state.")), "functional"));
        Assert.Null(StateFoldKind.FromDoc(Doc("FINDFIRST",
            ("cases", true, "Array of case tokens."),
            ("predicate", true, "LAMBDA(case, i) returning the answer.")), "loops"));
    }

    [Fact]
    public void FromDoc_ReadsTheRealHeaders()
    {
        var root = FindRepoRoot();
        foreach (var (file, expected) in new[] { ("REDUCEUNTIL", "acc,current,i"), ("SCANUNTIL", "acc,current,i"), ("FOLDROWS", "accSoFar,currentRow,i") })
        {
            var doc = LambdaDocParser.Parse(File.ReadAllText(Path.Combine(root, "lambdas", "loops", file + ".lambda")));
            var kind = StateFoldKind.FromDoc(doc, "loops");
            Assert.NotNull(kind);
            Assert.Equal(expected, string.Join(",", kind!.StepParameters));
        }
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable("LAMBDA_BOSS_ROOT") ?? "" })
        {
            var dir = start.Length == 0 ? null : new DirectoryInfo(start);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "lambdas")))
                dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    // ------------------------------------------------------------ review findings (PR #430)

    [Fact]
    public void LetStep_HandEditedSample_NeverBecomesTheSeedOrInputs()
    {
        var book = new Book();
        var let = book.Save(Robot, null, null)
            .Replace("    a, _seed,", "    a, SetVars(\"p\", 3, \"b\", 1),")
            .Replace("    v, INDEX(_inputs, 1),", "    v, \"C\",");

        Assert.Equal(Robot, book.Save(let, StateFoldKind.Reduce, null));
    }

    [Fact]
    public void LibraryFold_StopArgument_SurvivesTheLetStep()
    {
        var book = new Book();
        var until = Robot.Replace("=REDUCE(", "=REDUCEUNTIL(").Replace("LAMBDA(a, v,", "LAMBDA(a, v, i,")
            .Replace(")))", ")), LAMBDA(a, v, i, GetVar(a, \"b\") = 0))");

        var let = book.Save(until, null, null);
        Assert.Contains("    _stop, LAMBDA(a, v, i, GetVar(a, \"b\") = 0),\n", let);

        Assert.Equal(until, book.Save(let, ReduceUntil, null));
        Assert.Equal(until.Replace("=REDUCEUNTIL(", "=SCANUNTIL("), book.Save(let, ScanUntil, null));
    }

    [Fact]
    public void RequiredOtherArgument_GetsAPlaceholderAndANote()
    {
        var book = new Book();
        var result = book.Apply(book.Read(Robot), ReduceWhile, null);

        Assert.EndsWith(")), LAMBDA(a, v, i, TRUE))", result.Formula);
        Assert.Contains(result.Notes, n => n.Contains("continue"));
    }

    [Fact]
    public void FoldRows_WithoutASeed_ReadsAndStaysWithout()
    {
        var book = new Book();
        var rows = Lines(
            "=FOLDROWS(G1:G5, LAMBDA(a, v, i, LET(",
            "    p, GetVar(a, \"p\"),",
            "    nextP, p,",
            "    SetVars(\"p\", nextP)",
            ")))");

        var engine = book.Read(rows);
        Assert.Equal("", engine.Seed);

        var added = book.Save(rows, FoldRows, null, r => Add(r, "c"));
        Assert.StartsWith("=FOLDROWS(G1:G5, LAMBDA(a, v, i, LET(", added);
        Assert.EndsWith(")))", added);
        Assert.Contains("SetVars(\"p\", nextP, \"c\", nextC)", added);

        Assert.Equal(rows, book.Save(book.Save(rows, null, null), FoldRows, null));
    }

    [Fact]
    public void ARuleNamedI_IsNotTakenAsTheStepIndex()
    {
        var book = new Book();
        var robot = Robot.Replace("    p, GetVar", "    i, 10,\n    p, GetVar").Replace("nextP, p + IF", "nextP, i + p + IF");

        var until = book.Save(robot, ReduceUntil, null);
        Assert.Contains("LAMBDA(a, v, i_, LET(\n    i, 10,", until);
    }

    [Fact]
    public void EditingANamedStep_SaysItChangesForEveryCaller()
    {
        var book = new Book();
        var named = book.Save(Robot, StateFoldKind.Reduce, "moveRobot");

        var result = book.Apply(book.Read(named), StateFoldKind.Reduce, "moveRobot", Add(book.Read(named).Rows, "c"));
        Assert.Contains(result.Notes, n => n.Contains("every cell that calls it"));

        var unchanged = book.Apply(book.Read(named), StateFoldKind.Reduce, "moveRobot");
        Assert.DoesNotContain(unchanged.Notes, n => n.Contains("every cell that calls it"));
    }

    [Fact]
    public void RenamingOntoASlotName_IsRefusedInTheLetStep()
    {
        var book = new Book();
        var result = book.Apply(book.Read(Robot), null, null, Rename(book.Read(Robot).Rows, "b", "_inputs"));

        Assert.Null(result.Formula);
        Assert.Contains("_inputs", result.Error);
    }
}
