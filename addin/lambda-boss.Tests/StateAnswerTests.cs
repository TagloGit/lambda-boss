using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Answer outputs in order (spec 0018, #461) and the fold inside an outer LET (#460). The
///     example is Tim's R4 v2 food truck: crates and cash in the state, the answer cash first.
/// </summary>
public class StateAnswerTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static readonly IReadOnlyList<StateFoldKind> Kinds = Array.Empty<StateFoldKind>();

    private static string Truck(string fold = "REDUCE", string prefix = "") => Lines(
        $"{fold}({prefix}SetVars(\"crates\", 0, \"cash\", 20), TEXTSPLIT(G55, \",\"), LAMBDA(a, v, LET(",
        $"    crates, {prefix}GetVar(a, \"crates\"),",
        $"    cash, {prefix}GetVar(a, \"cash\"),",
        "    qty, --MID(v, 2, 9),",
        "    bought, IF(LEFT(v) = \"B\", MIN(qty, INT(cash / cost)), 0),",
        "    sold, IF(LEFT(v) = \"S\", MIN(qty, crates), 0),",
        "    nextCrates, crates + bought - sold,",
        "    nextCash, cash - cost * bought + price * sold,",
        $"    {prefix}SetVars(\"crates\", nextCrates, \"cash\", nextCash)",
        ")))");

    // Tim's L1 with the constants in an outer LET (#460).
    private static readonly string Outer = Lines("=LET(", "    cost, 3,", "    price, 5,", "    " + Truck(), ")");

    private static readonly string OuterAnswered =
        Lines("=LET(", "    cost, 3,", "    price, 5,", "    GetVars(" + Truck() + ", {\"cash\",\"crates\"})", ")");

    private static StateEngine Read(string formula, Func<string, string?>? refersTo = null)
    {
        var read = StateEngines.Read(formula, refersTo ?? (_ => null), Kinds);
        Assert.Null(read.Refusal);
        return read.Engine!;
    }

    private static StateEngineResult Apply(StateEngine engine, StateEngineChange change, Func<string, string?>? refersTo = null) =>
        StateEngines.Apply(engine, change, refersTo ?? (_ => null), k => k.Name);

    private static StateEngineChange Same(StateEngine engine, IReadOnlyList<string>? answer) =>
        new(engine.Rows, engine.Fold, engine.StepName, null, answer);

    // ------------------------------------------------------------ the answer

    [Fact]
    public void Tick_WritesGetVarsInTheOrderGiven()
    {
        var engine = Read(Outer);
        var result = Apply(engine, Same(engine, new[] { "cash", "crates" }));
        Assert.Null(result.Error);
        Assert.Equal(OuterAnswered, result.Formula);
    }

    [Fact]
    public void Read_GivesTheAnswerBack()
    {
        Assert.Equal(new[] { "cash", "crates" }, Read(OuterAnswered).Answer);
        Assert.Empty(Read(Outer).Answer);
    }

    [Fact]
    public void Reorder_RewritesOnlyTheArray()
    {
        var engine = Read(OuterAnswered);
        var result = Apply(engine, Same(engine, new[] { "crates", "cash" }));
        Assert.Equal(OuterAnswered.Replace("{\"cash\",\"crates\"}", "{\"crates\",\"cash\"}"), result.Formula);
    }

    [Fact]
    public void UntickAll_GivesTheTextBackExactly()
    {
        var engine = Read(OuterAnswered);
        Assert.Equal(Outer, Apply(engine, Same(engine, Array.Empty<string>())).Formula);
    }

    [Fact]
    public void SameAnswer_LeavesTimsSpacing()
    {
        var typed = OuterAnswered.Replace("{\"cash\",\"crates\"}", "{ \"cash\" ; \"crates\" }");
        var engine = Read(typed);
        Assert.Equal(new[] { "cash", "crates" }, engine.Answer);
        Assert.Equal(typed, Apply(engine, Same(engine, new[] { "cash", "crates" })).Formula);
    }

    [Fact]
    public void ACaseOnlyDifference_IsNoChange()
    {
        var typed = OuterAnswered.Replace("{\"cash\",\"crates\"}", "{ \"CASH\" ; \"crates\" }");
        var engine = Read(typed);
        Assert.Equal(typed, Apply(engine, Same(engine, new[] { "cash", "crates" })).Formula);

        var on = Apply(engine, new StateEngineChange(engine.Rows, StateFoldKind.Scan, null, true, new[] { "cash", "crates" }));
        var debug = Read(on.Formula!);
        var off = Apply(debug, new StateEngineChange(debug.Rows, StateFoldKind.Reduce, null, false, new[] { "cash", "crates" }));
        Assert.Equal(typed, off.Formula);
    }

    [Fact]
    public void NewEngine_CanStartWithAnAnswer()
    {
        var rows = new[] { new StateEditRow(null, "crates", ""), new StateEditRow(null, "cash", "") };
        var result = Apply(StateEngine.New(""), new StateEngineChange(rows, StateFoldKind.Reduce, null, null, new[] { "cash", "crates" }));
        Assert.Null(result.Error);
        Assert.StartsWith("=GetVars(REDUCE(SetVars(\"crates\", \"\", \"cash\", \"\"), SEQUENCE(1), LAMBDA(a, v, LET(", result.Formula);
        Assert.EndsWith("\n))), {\"cash\",\"crates\"})", result.Formula);
        Assert.Equal(new[] { "cash", "crates" }, Read(result.Formula!).Answer);
    }

    [Fact]
    public void Prefix_FollowsTheStateLibrary()
    {
        var engine = Read("=" + Truck(prefix: "loops."));
        var result = Apply(engine, Same(engine, new[] { "cash" }));
        Assert.Equal("=loops.GetVars(" + Truck(prefix: "loops.") + ", {\"cash\"})", result.Formula);
    }

    [Fact]
    public void AnUnknownKey_IsRefused()
    {
        var engine = Read(Outer);
        var result = Apply(engine, Same(engine, new[] { "cash", "pitch" }));
        Assert.Null(result.Formula);
        Assert.Contains("pitch isn't a state variable", result.Error);
    }

    [Fact]
    public void RenameATickedVariable_TheAnswerFollows()
    {
        var engine = Read(OuterAnswered);
        var rows = engine.Rows.Select(r => r.Key == "cash" ? r with { Key = "money" } : r).ToList();

        // From the dialog: the answer by the rows' new names.
        var fromDialog = Apply(engine, new StateEngineChange(rows, engine.Fold, null, null, new[] { "money", "crates" }));
        Assert.Contains("{\"money\",\"crates\"})", fromDialog.Formula);
        Assert.Contains("nextMoney, money - cost * bought", fromDialog.Formula);

        // With the answer left alone, the array still follows the rename.
        var leftAlone = Apply(engine, new StateEngineChange(rows, engine.Fold, null));
        Assert.Equal(fromDialog.Formula, leftAlone.Formula);
    }

    [Fact]
    public void RemoveATickedVariable_BlockedUntilUnticked()
    {
        // Nothing in the rules reads t: only the answer does.
        var counter = Lines(
            "=GetVars(REDUCE(SetVars(\"n\", 0, \"t\", 0), A1:A3, LAMBDA(a, v, LET(",
            "    n, GetVar(a, \"n\"),",
            "    t, GetVar(a, \"t\"),",
            "    nextN, n + 1,",
            "    nextT, t,",
            "    SetVars(\"n\", nextN, \"t\", nextT)",
            "))), {\"t\",\"n\"})");
        var engine = Read(counter);
        var rows = engine.Rows.Select(r => r.Key == "t" ? r with { Key = "" } : r).ToList();

        var blocked = Apply(engine, new StateEngineChange(rows, engine.Fold, null));
        Assert.Null(blocked.Formula);
        Assert.Contains("reads t from the fold's result", blocked.Error);

        var unticked = Apply(engine, new StateEngineChange(rows, engine.Fold, null, null, new[] { "n" }));
        Assert.Null(unticked.Error);
        Assert.Equal(Lines(
            "=GetVars(REDUCE(SetVars(\"n\", 0), A1:A3, LAMBDA(a, v, LET(",
            "    n, GetVar(a, \"n\"),",
            "    nextN, n + 1,",
            "    SetVars(\"n\", nextN)",
            "))), {\"n\"})"), unticked.Formula);
    }

    [Fact]
    public void Unfold_WithAnAnswer_SaysUntick()
    {
        var engine = Read("=GetVars(" + Truck() + ", {\"cash\"})");
        var result = Apply(engine, new StateEngineChange(engine.Rows, null, null));
        Assert.Equal("The LET step can't carry answer outputs. Untick them, or pick a fold.", result.Error);
    }

    [Fact]
    public void LetStep_ToFold_TakesTheAnswer()
    {
        var unfolded = Apply(Read("=" + Truck()), new StateEngineChange(Read("=" + Truck()).Rows, null, null)).Formula!;
        var engine = Read(unfolded);
        var result = Apply(engine, new StateEngineChange(engine.Rows, StateFoldKind.Reduce, null, null, new[] { "cash", "crates" }));
        Assert.Equal("=GetVars(" + Truck() + ", {\"cash\",\"crates\"})", result.Formula);
    }

    // ------------------------------------------------------------ debug inside the answer

    [Fact]
    public void Debug_GoesInsideTheAnswer_AndComesBackExactly()
    {
        var engine = Read(OuterAnswered);
        var on = Apply(engine, StateDebug.Toggle(engine, true, Kinds));
        Assert.Null(on.Error);
        Assert.Contains("    GetVars(SplitState(SCAN(SetVars(", on.Formula);
        Assert.Contains("))), 1), {\"cash\",\"crates\"})\n)", on.Formula);

        var debug = Read(on.Formula!);
        Assert.True(debug.IsDebug);
        Assert.Equal(new[] { "cash", "crates" }, debug.Answer);
        Assert.Equal(new[] { "crates", "cash" }, debug.Rows.Select(r => r.Key));

        var off = Apply(debug, StateDebug.Toggle(debug, false, Kinds));
        Assert.Equal(OuterAnswered, off.Formula);
    }

    [Fact]
    public void Debug_FromTheDialog_WithTheAnswerUnchanged_ComesBackExactly()
    {
        var engine = Read(OuterAnswered);
        var on = Apply(engine, new StateEngineChange(engine.Rows, StateFoldKind.Scan, null, true, new[] { "cash", "crates" }));
        var debug = Read(on.Formula!);
        var off = Apply(debug, new StateEngineChange(debug.Rows, StateFoldKind.Reduce, null, false, new[] { "cash", "crates" }));
        Assert.Equal(OuterAnswered, off.Formula);
    }

    [Fact]
    public void Answer_SetWhileInDebug_StaysOutsideTheDebugWrapper()
    {
        var engine = Read(Outer);
        var on = Apply(engine, StateDebug.Toggle(engine, true, Kinds));
        var debug = Read(on.Formula!);
        var ticked = Apply(debug, new StateEngineChange(debug.Rows, debug.Fold, null, true, new[] { "cash" }));
        Assert.Contains("GetVars(SplitState(SCAN(", ticked.Formula);
        Assert.Contains(", 1), {\"cash\"})", ticked.Formula);

        var back = Read(ticked.Formula!);
        Assert.Equal(new[] { "cash" }, back.Answer);
        Assert.True(back.IsDebug);
    }

    // ------------------------------------------------------------ outer LET (#460)

    [Fact]
    public void OuterLet_NamingAStepThatReadsOuterNames_IsRefused()
    {
        var engine = Read(Outer);
        var result = Apply(engine, new StateEngineChange(engine.Rows, engine.Fold, "truck"));
        Assert.Null(result.Formula);
        Assert.Contains("reads cost and price from the LET around the fold", result.Error);
    }

    [Fact]
    public void OuterLet_NamingAStepThatReadsNoOuterName_Works()
    {
        var engine = Read(Lines("=LET(", "    unused, 3,", "    " + Truck().Replace("cost", "3").Replace("price", "5"), ")"));
        var result = Apply(engine, new StateEngineChange(engine.Rows, engine.Fold, "truck"));
        Assert.Null(result.Error);
        Assert.Contains("REDUCE(SetVars(\"crates\", 0, \"cash\", 20), TEXTSPLIT(G55, \",\"), truck)", result.Formula);
    }

    [Fact]
    public void OuterLet_AStepBindingOfTheSameName_IsItsOwn()
    {
        var shadowed = Truck().Replace("    qty, --MID(v, 2, 9),", "    qty, --MID(v, 2, 9),\n    cost, 3,\n    price, 5,");
        var engine = Read(Lines("=LET(", "    cost, 9,", "    price, 9,", "    " + shadowed, ")"));
        Assert.Null(Apply(engine, new StateEngineChange(engine.Rows, engine.Fold, "truck")).Error);
    }

    [Fact]
    public void OuterLet_ALambdaParameterAroundTheFold_CountsAsOuter()
    {
        var engine = Read("=MAP(G55:G60, LAMBDA(cost, " + Truck().Replace("TEXTSPLIT(G55, \",\")", "TEXTSPLIT(cost, \",\")").Replace("price", "5") + "))");
        var result = Apply(engine, new StateEngineChange(engine.Rows, engine.Fold, "truck"));
        Assert.Contains("reads cost from the LET around the fold", result.Error);
    }

    [Fact]
    public void OuterLet_ARenameFollowsGetVarReadsOutsideTheFold()
    {
        var formula = Lines("=LET(", "    cost, 3,", "    price, 5,", "    s, " + Truck() + ",",
            "    \"C\" & GetVar(s, \"cash\") & \" S\" & GetVar(s, \"crates\")", ")");
        var engine = Read(formula);
        var rows = engine.Rows.Select(r => r.Key == "cash" ? r with { Key = "money" } : r).ToList();
        var result = Apply(engine, new StateEngineChange(rows, engine.Fold, null));
        Assert.Contains("\"C\" & GetVar(s, \"money\") & \" S\" & GetVar(s, \"crates\")", result.Formula);
        Assert.Contains("SetVars(\"crates\", 0, \"money\", 20)", result.Formula);
    }

    [Fact]
    public void OuterLet_ARemoveIsBlockedWhileTheResultIsRead()
    {
        var formula = Lines("=LET(", "    cost, 3,", "    price, 5,", "    s, " + Truck().Replace("cash - cost * bought + price * sold", "price * sold") + ",",
            "    GetVar(s, \"cash\")", ")");
        var engine = Read(formula);
        var rows = engine.Rows.Select(r => r.Key == "cash" ? r with { Key = "" } : r).ToList();
        var result = Apply(engine, new StateEngineChange(rows, engine.Fold, null));
        Assert.Null(result.Formula);
        Assert.Contains("reads cash from the fold's result", result.Error);
    }

    [Fact]
    public void OuterLet_AddRenameAndDebug_KeepTheLetAround()
    {
        var engine = Read(Outer);
        var add = Apply(engine, new StateEngineChange(engine.Rows.Append(new StateEditRow(null, "pitch", "")).ToList(), engine.Fold, null));
        Assert.StartsWith("=LET(\n    cost, 3,\n    price, 5,\n    REDUCE(SetVars(\"crates\", 0, \"cash\", 20, \"pitch\", \"\")", add.Formula);
        Assert.EndsWith(")))\n)", add.Formula);

        var on = Apply(engine, StateDebug.Toggle(engine, true, Kinds));
        var debug = Read(on.Formula!);
        Assert.Equal(Outer, Apply(debug, StateDebug.Toggle(debug, false, Kinds)).Formula);
    }

    [Theory]
    [InlineData("{\"a\",\"b\"}", new[] { "a", "b" })]
    [InlineData("{\"a\";\"b\"}", new[] { "a", "b" })]
    [InlineData("{\"x,y\",\"q\"\"\"}", new[] { "x,y", "q\"" })]
    public void TryUnwrap_ReadsTheArray(string array, string[] keys)
    {
        Assert.True(StateAnswer.TryUnwrap("=GetVars(", ", " + array + ")", out var b, out var a, out var read));
        Assert.Equal("=", b);
        Assert.Equal("", a);
        Assert.Equal(keys, read);
    }

    [Theory]
    [InlineData("=GetVars(", ", {1,2})")]
    [InlineData("=GetVars(", ", A1:A3)")]
    [InlineData("=MyGetVars(", ", {\"a\"})")]
    [InlineData("=GetVars(", ", {\"a\"}, 1)")]
    public void TryUnwrap_LeavesOtherTextAlone(string before, string after)
    {
        Assert.False(StateAnswer.TryUnwrap(before, after, out _, out _, out _));
    }
}
