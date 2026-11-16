using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Answers as LET names (spec 0018 addendum, #480): the ticked variables as
///     <c>name, GetVar(fold, "key")</c> lines after the fold's line, in a new LET round a
///     whole-formula fold or in the LET the fold already ends. The example is Tim's R4 v2 food truck.
/// </summary>
public class StateAnswerLetTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static readonly IReadOnlyList<StateFoldKind> Kinds = Array.Empty<StateFoldKind>();

    private static string Truck() => Lines(
        "REDUCE(SetVars(\"crates\", 0, \"cash\", 20), TEXTSPLIT(G55, \",\"), LAMBDA(a, v, LET(",
        "    crates, GetVar(a, \"crates\"),",
        "    cash, GetVar(a, \"cash\"),",
        "    qty, --MID(v, 2, 9),",
        "    bought, IF(LEFT(v) = \"B\", MIN(qty, INT(cash / cost)), 0),",
        "    sold, IF(LEFT(v) = \"S\", MIN(qty, crates), 0),",
        "    nextCrates, crates + bought - sold,",
        "    nextCash, cash - cost * bought + price * sold,",
        "    SetVars(\"crates\", nextCrates, \"cash\", nextCash)",
        ")))");

    private static readonly string Bare = "=" + Truck();

    private static readonly string Outer = Lines("=LET(", "    cost, 3,", "    price, 5,", "    " + Truck(), ")");

    private static string Answered(string name1 = "cash", string key1 = "cash", string name2 = "crates", string key2 = "crates",
        string? last = null) => Lines(
        "=LET(",
        "    fold, " + Truck() + ",",
        $"    {name1}, GetVar(fold, \"{key1}\"),",
        $"    {name2}, GetVar(fold, \"{key2}\"),",
        $"    {last ?? $"HSTACK({name1}, {name2})"}",
        ")");

    private static readonly string OuterAnswered = Lines(
        "=LET(",
        "    cost, 3,",
        "    price, 5,",
        "    fold, " + Truck() + ",",
        "    cash, GetVar(fold, \"cash\"),",
        "    crates, GetVar(fold, \"crates\"),",
        "    HSTACK(cash, crates)",
        ")");

    private static StateEngine Read(string formula)
    {
        var read = StateEngines.Read(formula, _ => null, Kinds);
        Assert.Null(read.Refusal);
        return read.Engine!;
    }

    private static StateEngineResult Apply(StateEngine engine, StateEngineChange change) =>
        StateEngines.Apply(engine, change, _ => null, k => k.Name);

    private static StateEngineChange Ticks(StateEngine engine, bool? asLet, params string[] answer) =>
        new(engine.Rows, engine.Fold, engine.StepName, null, answer, null, asLet);

    private static string Set(string formula, bool? asLet, params string[] answer)
    {
        var engine = Read(formula);
        var result = Apply(engine, Ticks(engine, asLet, answer));
        Assert.Null(result.Error);
        return result.Formula!;
    }

    private static string? Error(string formula, bool? asLet, params string[] answer)
    {
        var engine = Read(formula);
        var result = Apply(engine, Ticks(engine, asLet, answer));
        Assert.Null(result.Formula);
        return result.Error;
    }

    // ------------------------------------------------------------ write

    [Fact]
    public void Tick_WholeFormula_PutsANewLetRoundTheFold()
    {
        Assert.Equal(Answered(), Set(Bare, true, "cash", "crates"));
    }

    [Fact]
    public void Tick_OneAnswer_TheLastLineIsTheBareName()
    {
        Assert.EndsWith("    cash, GetVar(fold, \"cash\"),\n    cash\n)", Set(Bare, true, "cash"));
    }

    [Fact]
    public void Tick_ReusesTheOuterLet_WhenTheFoldIsItsLastLine()
    {
        Assert.Equal(OuterAnswered, Set(Outer, true, "cash", "crates"));
    }

    [Fact]
    public void Tick_FoldInsideTextJoin_IsRefused()
    {
        var error = Error("=TEXTJOIN(\" \", , " + Truck() + ")", true, "cash");
        Assert.Equal("LET names need the fold to be the whole formula or the last line of its LET.", error);
    }

    [Fact]
    public void Tick_FoldThatIsNotTheLastLineOfItsLet_IsRefused()
    {
        var formula = Lines("=LET(", "    r, " + Truck() + ",", "    r", ")");
        Assert.Contains("LET names need the fold", Error(formula, true, "cash"));
    }

    [Fact]
    public void Names_AKeyThatIsNotAName_OrIsBoundInThatLet_GetsAnUnderscore()
    {
        var step = Lines(
            "REDUCE(SetVars(\"cash\", 0, \"A1\", 0), A1:A3, LAMBDA(a, v, LET(",
            "    cash, GetVar(a, \"cash\"),",
            "    n, GetVar(a, \"A1\"),",
            "    nextCash, cash + v,",
            "    nextN, n + 1,",
            "    SetVars(\"cash\", nextCash, \"A1\", nextN)",
            ")))");
        var formula = Lines("=LET(", "    cash, 3,", "    " + step, ")");

        var written = Set(formula, true, "cash", "A1");
        Assert.Contains("    cash_, GetVar(fold, \"cash\"),", written);
        Assert.Contains("    A1_, GetVar(fold, \"A1\"),", written);
        Assert.EndsWith("    HSTACK(cash_, A1_)\n)", written);
        Assert.Equal(new[] { "cash", "A1" }, Read(written).Answer);
    }

    [Fact]
    public void Names_TheFoldIsCalledFoldUnderscore_WhenFoldIsTaken()
    {
        var formula = Lines("=LET(", "    fold, 1,", "    " + Truck(), ")");
        var written = Set(formula, true, "cash");
        Assert.Contains("    fold_, REDUCE(", written);
        Assert.Contains("cash, GetVar(fold_, \"cash\")", written);
    }

    [Fact]
    public void Prefix_FollowsTheStateLibrary()
    {
        var truck = Lines(
            "REDUCE(loops.SetVars(\"n\", 0), A1:A3, LAMBDA(a, v, LET(",
            "    n, loops.GetVar(a, \"n\"),",
            "    nextN, n + v,",
            "    loops.SetVars(\"n\", nextN)",
            ")))");
        var engine = Read("=" + truck);
        var result = Apply(engine, Ticks(engine, true, "n"));
        Assert.Null(result.Error);
        Assert.Contains("n, loops.GetVar(fold, \"n\")", result.Formula);
    }

    // ------------------------------------------------------------ read back

    [Fact]
    public void Read_GivesTheAnswerAndTheFormBack()
    {
        var engine = Read(OuterAnswered);
        Assert.Equal(new[] { "cash", "crates" }, engine.Answer);
        Assert.True(engine.AnswerAsLet);
        Assert.Equal(StateFoldKind.Reduce.Name, engine.Fold!.Name);
        Assert.Equal("=LET(\n    cost, 3,\n    price, 5,\n    fold, ", engine.Before);

        var plain = Read(Outer);
        Assert.Empty(plain.Answer);
        Assert.False(plain.AnswerAsLet);

        var getVars = Read(Lines("=LET(", "    cost, 3,", "    price, 5,", "    GetVars(" + Truck() + ", {\"cash\"})", ")"));
        Assert.Equal(new[] { "cash" }, getVars.Answer);
        Assert.False(getVars.AnswerAsLet);
    }

    [Fact]
    public void Read_ALetThatIsNotTheAnswerForm_IsNoAnswer()
    {
        var formula = Lines("=LET(", "    r, " + Truck() + ",", "    GetVar(r, \"cash\")", ")");
        var engine = Read(formula);
        Assert.Empty(engine.Answer);
        Assert.False(engine.AnswerAsLet);
    }

    [Fact]
    public void OuterNames_AreOnlyThoseAboveTheFold_AndTheStepStillReadsAsAStep()
    {
        var engine = Read(OuterAnswered);
        var outer = StateAnswer.OuterNames(engine);
        Assert.Contains("cost", outer);
        Assert.DoesNotContain("cash", outer);
        Assert.DoesNotContain("crates", outer);
        Assert.Equal(new[] { "crates", "cash" }, engine.Rows.Select(r => r.Key));
        Assert.Empty(engine.DebugKeys);
    }

    // ------------------------------------------------------------ ticks and order

    [Fact]
    public void Reorder_RegeneratesTheDefaultLastLine()
    {
        var written = Set(Answered(), null, "crates", "cash");
        Assert.Equal(Answered("crates", "crates", "cash", "cash"), written);
    }

    [Fact]
    public void Untick_OneOfTwo_KeepsTheOtherAndRegeneratesTheLastLine()
    {
        var written = Set(Answered(), null, "cash");
        Assert.DoesNotContain("crates, GetVar(fold", written);
        Assert.EndsWith("    cash, GetVar(fold, \"cash\"),\n    cash\n)", written);
    }

    [Fact]
    public void OwnLastLine_IsKeptOnReorder()
    {
        var own = "\"C\" & cash & \" S\" & crates";
        var typed = Answered(last: own);
        var written = Set(typed, null, "crates", "cash");
        Assert.Equal(Answered("crates", "crates", "cash", "cash", own), written);
    }

    [Fact]
    public void OwnLastLine_BlocksUntickingANameItReads()
    {
        var typed = Answered(last: "\"C\" & cash & \" S\" & crates");
        Assert.Equal("The last line reads crates. Take that out first.", Error(typed, null, "cash"));
    }

    [Fact]
    public void OwnLastLine_AllowsUntickingANameItDoesNotRead()
    {
        var typed = Answered(last: "\"C\" & cash");
        var written = Set(typed, null, "cash");
        Assert.DoesNotContain("crates, GetVar(fold", written);
        Assert.EndsWith("    \"C\" & cash\n)", written);
    }

    // ------------------------------------------------------------ untick all

    [Fact]
    public void UntickAll_TakesTheLetThatWasMadeOff_AndGivesTheTextBack()
    {
        Assert.Equal(Bare, Set(Answered(), null));
    }

    [Fact]
    public void UntickAll_OfAReusedLet_KeepsTheLetAndGivesTheTextBack()
    {
        Assert.Equal(Outer, Set(OuterAnswered, null));
    }

    [Fact]
    public void UntickAll_WithOwnLastLine_IsRefused()
    {
        var error = Error(Answered(last: "\"C\" & cash"), null);
        Assert.Contains("last line is your own", error);
    }

    [Fact]
    public void Tick_ThenUntick_RoundTripsExactly_BothWays()
    {
        Assert.Equal(Bare, Set(Set(Bare, true, "cash", "crates"), true));
        Assert.Equal(Outer, Set(Set(Outer, true, "crates"), true));
    }

    // ------------------------------------------------------------ switching

    [Fact]
    public void SwitchGetVarsToLetNames_AndBack_IsExact()
    {
        var getVars = Lines("=LET(", "    cost, 3,", "    price, 5,", "    GetVars(" + Truck() + ", {\"cash\",\"crates\"})", ")");
        var asLet = Set(getVars, true, "cash", "crates");
        Assert.Equal(OuterAnswered, asLet);
        Assert.Equal(getVars, Set(asLet, false, "cash", "crates"));

        var bareGetVars = "=GetVars(" + Truck() + ", {\"cash\",\"crates\"})";
        Assert.Equal(Answered(), Set(bareGetVars, true, "cash", "crates"));
        Assert.Equal(bareGetVars, Set(Answered(), false, "cash", "crates"));
    }

    [Fact]
    public void SwitchFromLetNamesWithOwnLastLine_IsRefused()
    {
        var error = Error(Answered(last: "\"C\" & cash"), false, "cash", "crates");
        Assert.Contains("would lose it", error);
    }

    [Fact]
    public void FormChangedWithNoTicks_WritesNothing()
    {
        Assert.Equal(Bare, Set(Bare, true));
    }

    // ------------------------------------------------------------ debug

    private static StateEngineChange DebugOn(StateEngine engine) => StateDebug.Toggle(engine, true, Kinds);

    [Fact]
    public void DebugOn_PutsTheAnswerLineAboveTheFoldName_AndOffGivesTheTextBack()
    {
        var engine = Read(Answered());
        var on = Apply(engine, DebugOn(engine));
        Assert.Null(on.Error);

        Assert.Contains("fold, SplitState(SCAN(SetVars(", on.Formula);
        Assert.Contains("\n))), 1),\n    cash, GetVar(fold, \"cash\"),", on.Formula);
        Assert.EndsWith("    crates, GetVar(fold, \"crates\"),\n    _answer, HSTACK(cash, crates),\n    fold\n)", on.Formula);

        var debug = Read(on.Formula!);
        Assert.True(debug.IsDebug);
        Assert.True(debug.AnswerAsLet);
        Assert.Equal(new[] { "cash", "crates" }, debug.Answer);

        var off = Apply(debug, StateDebug.Toggle(debug, false, Kinds));
        Assert.Null(off.Error);
        Assert.Equal(Answered(), off.Formula);
    }

    [Fact]
    public void DebugOnAndOff_ThroughTheDialogsChange_IsExact_InBothLetForms()
    {
        foreach (var text in new[] { Answered(), OuterAnswered, Answered(last: "\"C\" & cash") })
        {
            var engine = Read(text);
            var on = Apply(engine, new StateEngineChange(engine.Rows, StateFoldKind.Scan, null, true, null, null, null));
            Assert.Null(on.Error);
            Assert.Contains("_answer, ", on.Formula);

            var debug = Read(on.Formula!);
            var off = Apply(debug, new StateEngineChange(debug.Rows, StateFoldKind.Reduce, null, false, null, null, null));
            Assert.Null(off.Error);
            Assert.Equal(text, off.Formula);
        }
    }

    [Fact]
    public void DebugOnWithoutAnAnswer_IsUnchangedByThis()
    {
        var engine = Read(Outer);
        var on = Apply(engine, DebugOn(engine));
        Assert.DoesNotContain("_answer", on.Formula);
        var debug = Read(on.Formula!);
        Assert.Equal(Outer, Apply(debug, StateDebug.Toggle(debug, false, Kinds)).Formula);
    }

    [Fact]
    public void TickingWhileDebugIsOn_WritesTheAnswerLine_AndDebugOffGivesBareText()
    {
        var engine = Read(Bare);
        var on = Apply(engine, DebugOn(engine));
        var debug = Read(on.Formula!);

        var ticked = Apply(debug, new StateEngineChange(debug.Rows, debug.Fold, null, null, new[] { "cash" }, null, true));
        Assert.Null(ticked.Error);
        Assert.Contains("_answer, cash,\n    fold\n)", ticked.Formula);

        var again = Read(ticked.Formula!);
        Assert.Equal(new[] { "cash" }, again.Answer);
        var off = Apply(again, StateDebug.Toggle(again, false, Kinds));
        Assert.Contains("fold, REDUCE(", off.Formula);
        Assert.EndsWith("\n    cash, GetVar(fold, \"cash\"),\n    cash\n)", off.Formula);
    }

    [Fact]
    public void UntickingAllWhileDebugIsOn_TakesTheAnswerOff_AndTheWrapperStays()
    {
        var engine = Read(Answered());
        var on = Apply(engine, DebugOn(engine));
        var debug = Read(on.Formula!);

        var cleared = Apply(debug, new StateEngineChange(debug.Rows, debug.Fold, null, null, Array.Empty<string>(), null, null));
        Assert.Null(cleared.Error);
        Assert.DoesNotContain("_answer", cleared.Formula);
        Assert.StartsWith("=SplitState(SCAN(", cleared.Formula);
    }

    // ------------------------------------------------------------ rename, remove, unfold, carry

    [Fact]
    public void Rename_FollowsTheGetVarKey_AndTheLetNameStaysTims()
    {
        var engine = Read(OuterAnswered);
        var rows = engine.Rows.Select(r => r.Key == "cash" ? r with { Key = "money" } : r).ToList();

        var fromDialog = Apply(engine, new StateEngineChange(rows, engine.Fold, null, null, new[] { "money", "crates" }, null, null));
        Assert.Null(fromDialog.Error);
        Assert.Contains("    cash, GetVar(fold, \"money\"),", fromDialog.Formula);
        Assert.Contains("    HSTACK(cash, crates)\n)", fromDialog.Formula);
        Assert.Contains("nextMoney, money - cost * bought", fromDialog.Formula);

        var leftAlone = Apply(engine, new StateEngineChange(rows, engine.Fold, null));
        Assert.Equal(fromDialog.Formula, leftAlone.Formula);
    }

    [Fact]
    public void Remove_ATickedVariable_IsBlockedWhileItsLineReadsIt()
    {
        var engine = Read(Answered());
        var rows = engine.Rows.Select(r => r.Key == "cash" ? r with { Key = "" } : r).ToList();
        var result = Apply(engine, new StateEngineChange(rows, engine.Fold, null));
        Assert.Null(result.Formula);
        Assert.Contains("The formula reads cash from the fold's result", result.Error);
    }

    [Fact]
    public void Unfold_WithLetNames_SaysUntick()
    {
        var engine = Read(OuterAnswered);
        var result = Apply(engine, new StateEngineChange(engine.Rows, null, null));
        Assert.Null(result.Formula);
        Assert.Equal("The LET step can't carry answer outputs. Untick them, or pick a fold.", result.Error);

        var whole = Read(Answered());
        Assert.Equal(result.Error, Apply(whole, new StateEngineChange(whole.Rows, null, null)).Error);
    }

    [Fact]
    public void Guard_OnAndOff_WorksWithLetNames()
    {
        var engine = Read(Answered());
        var on = Apply(engine, new StateEngineChange(engine.Rows, engine.Fold, null, null, null, true));
        Assert.Null(on.Error);
        Assert.Contains("fold, REDUCE(SetVars(", on.Formula);
        Assert.Equal(new[] { "cash", "crates" }, Read(on.Formula!).Answer);
    }

    [Fact]
    public void ACarriedVariable_CanBeInTheLetNames()
    {
        var plain = Lines(
            "=REDUCE(SetVars(\"n\", 0), TEXTSPLIT(G55, \",\"), LAMBDA(a, v, LET(",
            "    n, GetVar(a, \"n\"),",
            "    pos, n + LEN(v),",
            "    nextN, n + 1,",
            "    SetVars(\"n\", nextN)",
            ")))");
        var engine = Read(plain);
        var typed = new StateEngineChange(
            engine.Rows.Append(new StateEditRow(null, "pos", "")).ToList(), engine.Fold, null, null, new[] { "n", "pos" }, null, true);
        var result = Apply(engine, typed);
        Assert.Null(result.Error);
        Assert.Contains("SetVars(\"n\", nextN, \"pos\", pos)", result.Formula);
        Assert.Contains("    n, GetVar(fold, \"n\"),\n    pos, GetVar(fold, \"pos\"),\n    HSTACK(n, pos)\n)", result.Formula);

        var read = Read(result.Formula!);
        Assert.Equal(new[] { "n", "pos" }, read.Answer);
        Assert.Equal(new[] { "pos" }, read.CarriedKeys);

        var on = Apply(read, StateDebug.Toggle(read, true, Kinds));
        var debug = Read(on.Formula!);
        var off = Apply(debug, StateDebug.Toggle(debug, false, Kinds));
        Assert.Equal(result.Formula, off.Formula);
    }
}
