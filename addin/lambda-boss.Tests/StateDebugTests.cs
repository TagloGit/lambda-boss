using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     The /State debug toggle (spec 0015, "Debug toggle (#440)"). Tim's normal and debug forms
///     of the walk are the examples: REDUCE with no wrapper, and SplitState(SCAN(…), 1) with
///     temporary outputs.
/// </summary>
public class StateDebugTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static readonly StateFoldArgument[] UntilArgs =
    {
        new("seed", true, false), new("cases", true, false), new("fn", true, true), new("stop", false, true)
    };

    private static readonly StateFoldKind ReduceUntil = new("REDUCEUNTIL", 0, 1, 2, new[] { "acc", "current", "i" }, "loops", UntilArgs);
    private static readonly StateFoldKind ScanUntil = new("SCANUNTIL", 0, 1, 2, new[] { "acc", "current", "i" }, "loops", UntilArgs);
    private static readonly IReadOnlyList<StateFoldKind> Kinds = new[] { ReduceUntil, ScanUntil };

    private const string NormalOutput = "SetVars(\"visited\", nextVisited, \"p\", nextP)";
    private const string DebugOutput = "SetVars(\"visited\", nextVisited, \"p\", nextP, \"item\", item, \"c\", c, \"w\", w)";

    private static readonly string[] Rules =
    {
        "    visited, GetVar(a, \"visited\"),",
        "    p, GetVar(a, \"p\"),",
        "    item, XLOOKUP(4&p, ids, vals),",
        "    ns, POSTOCELL(TOPOS(p)+dirs*(1+(item=\"T\"))),",
        "    items, XLOOKUP(4&ns, ids, vals),",
        "    c, XLOOKUP(\"C\", items, ns, \"\"),",
        "    w, XLOOKUP(\"W\", items, ns, \"\"),",
        "    nextP, IF(c<>\"\", c, IF(w<>\"\", w, \"???\")),",
        "    nextVisited, VSTACK(visited, nextP),"
    };

    private static string Fold(string open, string lambdaHead, string close, params string[] tail) =>
        Lines(new[] { open + "SetVars(\"visited\", G150, \"p\", G150), SEQUENCE(5), " + lambdaHead + "LET(" }
            .Concat(Rules).Concat(tail).Append(")))" + close).ToArray());

    // Tim's normal form: REDUCE, no wrapper, only the real variables.
    private static readonly string Normal = Fold("=REDUCE(", "LAMBDA(a, v, ", "", "    " + NormalOutput);

    // Tim's debug form as he typed it (LAMBDA(a,v, and ,1)).
    private static readonly string TimDebug = Fold("=SplitState(SCAN(", "LAMBDA(a,v, ", ",1)", "    " + DebugOutput);

    // The debug form the toggle writes, before any debug outputs are added.
    private static readonly string EnteredDebug = Fold("=SplitState(SCAN(", "LAMBDA(a, v, ", ", 1)", "    " + NormalOutput);

    private static readonly string CanonicalDebug = Fold("=SplitState(SCAN(", "LAMBDA(a, v, ", ", 1)", "    " + DebugOutput);

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

        public StateEngineResult Apply(StateEngine engine, StateEngineChange change, string callPrefix = "") =>
            StateEngines.Apply(engine, change, RefersTo, k => k.IsNative ? k.Name : callPrefix + k.Name);

        /// <summary>The cell after the one-command toggle.</summary>
        public string Toggle(string formula, bool on, string callPrefix = "")
        {
            var engine = Read(formula);
            var result = Apply(engine, StateDebug.Toggle(engine, on, Kinds), callPrefix);
            Assert.Null(result.Error);
            return result.Formula!;
        }
    }

    private static IReadOnlyList<StateEditRow> Add(IReadOnlyList<StateEditRow> rows, string key, string value) =>
        rows.Append(new StateEditRow(null, key, value)).ToList();

    // ------------------------------------------------------------ recognition

    [Fact]
    public void DebugForm_IsRecognised_WithTheExtraOutputsAsDebugOnly()
    {
        var engine = new Book().Read(TimDebug);

        Assert.True(engine.IsDebug);
        Assert.Equal("SCAN", engine.Fold!.Name);
        Assert.Equal(new[] { "visited", "p" }, engine.Rows.Select(r => r.Key));
        Assert.Equal(new[] { "item", "c", "w" }, engine.DebugKeys);
    }

    [Fact]
    public void NormalForm_IsNotDebug_AndHasNoDebugOutputs()
    {
        var engine = new Book().Read(Normal);

        Assert.False(engine.IsDebug);
        Assert.Empty(engine.DebugKeys);
        Assert.Equal(new[] { "visited", "p" }, engine.Rows.Select(r => r.Key));
    }

    [Fact]
    public void APlainScan_WithoutTheWrapper_IsNotDebug_AndIsLeftAlone()
    {
        var plain = Normal.Replace("=REDUCE(", "=SCAN(");
        var book = new Book();
        var engine = book.Read(plain);

        Assert.False(engine.IsDebug);

        var result = book.Apply(engine, new StateEngineChange(engine.Rows, engine.Fold, null));
        Assert.Null(result.Error);
        Assert.Equal(plain, result.Formula);
    }

    [Fact]
    public void AReduceInsideSplitState_IsNotDebug()
    {
        var wrapped = Normal.Replace("=REDUCE(", "=SplitState(REDUCE(") + ", 1)";
        var engine = new Book().Read(wrapped);

        Assert.False(engine.IsDebug);
    }

    [Fact]
    public void ASplitStateWithoutHeaders_IsNotDebug()
    {
        var engine = new Book().Read(CanonicalDebug.Replace(", 1)", ", 0)"));

        Assert.False(engine.IsDebug);
    }

    // ------------------------------------------------------------ enter and exit

    [Fact]
    public void Enter_SwitchesToScanAndWraps()
    {
        Assert.Equal(EnteredDebug, new Book().Toggle(Normal, on: true));
    }

    [Fact]
    public void NormalToDebugToNormal_GivesTheSameText()
    {
        var book = new Book();

        var debug = book.Toggle(Normal, on: true);
        Assert.True(book.Read(debug).IsDebug);
        Assert.Equal(Normal, book.Toggle(debug, on: false));
    }

    [Fact]
    public void Exit_DropsTheDebugOnlyOutputs_AndRestoresReduce()
    {
        var book = new Book();
        var engine = book.Read(TimDebug);
        var result = book.Apply(engine, StateDebug.Toggle(engine, false, Kinds));

        // Tim's own spacing (LAMBDA(a,v,) stays: only the fold, the wrapper and the outputs change (#447).
        Assert.Null(result.Error);
        Assert.Equal(Fold("=REDUCE(", "LAMBDA(a,v, ", "", "    " + NormalOutput), result.Formula);
        Assert.Contains(result.Notes, n => n.Contains("item, c, w"));
    }

    // ------------------------------------------------------------ Tim's exact text (#447)

    // The S6 rep's fold (International Women's Day L6), as Tim types it: no spaces anywhere.
    private static readonly string Compact =
        "=REDUCE(SetVars(\"x\",$A$12,\"seen\",$A$12),SEQUENCE(15),LAMBDA(a,v,LET(\n"
        + "    x,GetVar(a,\"x\"),\n"
        + "    seen,GetVar(a,\"seen\"),\n"
        + "    try,x-v,\n"
        + "    nextX,IF(OR(try<=0,ISNUMBER(XMATCH(try,seen))),x+v,try),\n"
        + "    nextSeen,VSTACK(seen,nextX),\n"
        + "    SetVars(\"x\",nextX,\"seen\",nextSeen))))";

    [Fact]
    public void CompactFold_OnThenOff_GivesBackTimsExactText()
    {
        var book = new Book();

        var debug = book.Toggle(Compact, on: true);
        Assert.Equal("=SplitState(" + Compact[1..].Replace("REDUCE(", "SCAN(") + ", 1)", debug);
        Assert.Equal(Compact, book.Toggle(debug, on: false));
    }

    [Fact]
    public void CompactFold_WithADebugOutputTypedIn_DropsItAndGivesBackTimsExactText()
    {
        var book = new Book();

        var debug = book.Toggle(Compact, on: true)
            .Replace("SetVars(\"x\",nextX,\"seen\",nextSeen)", "SetVars(\"x\",nextX,\"seen\",nextSeen,\"try\",try)");
        Assert.Equal(new[] { "try" }, book.Read(debug).DebugKeys);

        var result = book.Apply(book.Read(debug), StateDebug.Toggle(book.Read(debug), false, Kinds));
        Assert.Null(result.Error);
        Assert.Equal(Compact, result.Formula);
        Assert.Contains(result.Notes, n => n.Contains("try"));
    }

    [Fact]
    public void ADebugOutputInTheMiddle_GoesWithTheCommaTypedBeforeIt()
    {
        var book = new Book();
        var debug = book.Toggle(Compact, on: true)
            .Replace("SetVars(\"x\",nextX,\"seen\",nextSeen)", "SetVars(\"x\",nextX, \"try\" , try,\"seen\",nextSeen)");

        Assert.Equal(Compact, book.Toggle(debug, on: false));
    }

    [Fact]
    public void TheDialogsChange_WithItsBlankNewRow_StillGivesBackTimsExactText()
    {
        // StateWindow always sends a trailing blank row for a new variable (review of #448).
        var book = new Book();
        StateEngineChange Dialog(StateEngine e, bool on) =>
            StateDebug.Toggle(e, on, Kinds) with { Rows = e.Rows.Append(new StateEditRow(null, "", "")).ToList() };

        var on = book.Read(Compact);
        var debug = book.Apply(on, Dialog(on, true)).Formula!;
        Assert.Equal("=SplitState(" + Compact[1..].Replace("REDUCE(", "SCAN(") + ", 1)", debug);

        var off = book.Read(debug);
        Assert.Equal(Compact, book.Apply(off, Dialog(off, false)).Formula);
    }

    [Fact]
    public void AToggleWithAVariableEdit_IsLaidOutAfresh()
    {
        var book = new Book();
        var engine = book.Read(Compact);
        var change = StateDebug.Toggle(engine, true, Kinds) with { Rows = Add(engine.Rows, "n", "0") };
        var result = book.Apply(engine, change);

        Assert.Null(result.Error);
        Assert.StartsWith("=SplitState(SCAN(SetVars(\"x\", $A$12, \"seen\", $A$12, \"n\", 0), SEQUENCE(15), LAMBDA(a, v, LET(", result.Formula);
    }

    [Fact]
    public void DebugOutputsAddedByHand_GoWhenTheToggleGoesOut()
    {
        var book = new Book();
        var entered = book.Toggle(Normal, on: true);
        var typed = entered.Replace(NormalOutput, DebugOutput);

        Assert.Equal(CanonicalDebug, typed);
        Assert.Equal(Normal, book.Toggle(typed, on: false));
    }

    [Fact]
    public void ATogglingOnAnAlreadyDebugForm_ChangesNothing()
    {
        var book = new Book();
        var engine = book.Read(CanonicalDebug);
        var result = book.Apply(engine, StateDebug.Toggle(engine, true, Kinds));

        Assert.Null(result.Error);
        Assert.Equal(CanonicalDebug, result.Formula);
    }

    [Fact]
    public void ReduceUntil_GoesToScanUntil_AndBack()
    {
        var until = Normal.Replace("=REDUCE(", "=loops.REDUCEUNTIL(").Replace("LAMBDA(a, v,", "LAMBDA(a, v, i,");
        var book = new Book();

        var debug = book.Toggle(until, on: true, callPrefix: "loops.");
        Assert.StartsWith("=SplitState(loops.SCANUNTIL(SetVars(", debug);
        Assert.EndsWith(")))" + ", 1)", debug);
        Assert.Equal(new[] { "a", "v", "i" }, book.Read(debug).Parameters);

        Assert.Equal(until, book.Toggle(debug, on: false, callPrefix: "loops."));
    }

    [Fact]
    public void ALibraryPrefixOnTheState_IsUsedForTheWrapper()
    {
        var prefixed = Normal.Replace("SetVars(", "loops.SetVars(");
        var entered = new Book().Toggle(prefixed, on: true);

        Assert.StartsWith("=loops.SplitState(SCAN(loops.SetVars(", entered);
        Assert.Equal(prefixed, new Book().Toggle(entered, on: false));
    }

    [Fact]
    public void Twin_PairsEachReduceWithItsScan()
    {
        Assert.Same(StateFoldKind.Scan, StateDebug.Twin(StateFoldKind.Reduce, Kinds, toScan: true));
        Assert.Same(StateFoldKind.Reduce, StateDebug.Twin(StateFoldKind.Scan, Kinds, toScan: false));
        Assert.Same(ScanUntil, StateDebug.Twin(ReduceUntil, Kinds, toScan: true));
        Assert.Same(ReduceUntil, StateDebug.Twin(ScanUntil, Kinds, toScan: false));
        Assert.Null(StateDebug.Twin(ReduceUntil, Array.Empty<StateFoldKind>(), toScan: true));
    }

    [Fact]
    public void Enter_OnAFoldWithNoScanTwin_IsRefused()
    {
        var book = new Book();
        var engine = book.Read(Normal.Replace("=REDUCE(", "=loops.REDUCEUNTIL(").Replace("LAMBDA(a, v,", "LAMBDA(a, v, i,"));
        var result = book.Apply(engine, StateDebug.Toggle(engine, true, Array.Empty<StateFoldKind>()));

        Assert.Null(result.Formula);
        Assert.Contains("SCAN", result.Error);
    }

    [Fact]
    public void Enter_OnTheUnfoldedLetStep_IsRefused()
    {
        var book = new Book();
        var engine = book.Read(Normal);
        var result = book.Apply(engine, new StateEngineChange(engine.Rows, null, null, true));

        Assert.Null(result.Formula);
        Assert.Contains("Debug needs a fold", result.Error);
    }

    // ------------------------------------------------------------ with the #438 / #443 forms

    private static string Guarded(string outLine, string body, string open = "=SplitState(SCAN(", string close = ", 1)") =>
        Lines(new[] { open + "SetVars(\"visited\", G150, \"p\", G150), SEQUENCE(5), LAMBDA(a, v, LET(" }
            .Concat(Rules).Concat(new[] { "    " + outLine, "    " + body, ")))" + close }).ToArray());

    [Fact]
    public void GuardedStep_WithANamedOutput_ReadsExitsAndKeepsItsShape()
    {
        var debug = Guarded("out, " + DebugOutput + ",", "IF(item=\"C\", a, out)");
        var normal = Guarded("out, " + NormalOutput + ",", "IF(item=\"C\", a, out)", "=REDUCE(", "");
        var book = new Book();

        var engine = book.Read(debug);
        Assert.True(engine.IsDebug);
        Assert.Equal(new[] { "visited", "p" }, engine.Rows.Select(r => r.Key));
        Assert.Equal(new[] { "item", "c", "w" }, engine.DebugKeys);

        Assert.Equal(normal, book.Toggle(debug, on: false));
        Assert.Equal(debug.Replace(DebugOutput, NormalOutput), book.Toggle(normal, on: true));
    }

    [Fact]
    public void GuardedStep_WithASetVarsInTheGuard_Exits()
    {
        var debug = Guarded("", "IF(item=\"C\", a, " + DebugOutput + ")").Replace("\n    \n", "\n");
        var normal = Guarded("", "IF(item=\"C\", a, " + NormalOutput + ")", "=REDUCE(", "").Replace("\n    \n", "\n");
        var book = new Book();

        Assert.Equal(new[] { "item", "c", "w" }, book.Read(debug).DebugKeys);
        Assert.Equal(normal, book.Toggle(debug, on: false));
    }

    // ------------------------------------------------------------ variable edits in the debug form

    [Fact]
    public void AddingARealVariable_KeepsTheDebugOutputs()
    {
        var book = new Book();
        var engine = book.Read(CanonicalDebug);
        var result = book.Apply(engine, new StateEngineChange(Add(engine.Rows, "q", "0"), engine.Fold, null));

        Assert.Null(result.Error);
        Assert.StartsWith("=SplitState(SCAN(SetVars(\"visited\", G150, \"p\", G150, \"q\", 0), SEQUENCE(5)", result.Formula);
        Assert.Contains(
            "SetVars(\"visited\", nextVisited, \"p\", nextP, \"item\", item, \"c\", c, \"w\", w, \"q\", nextQ)", result.Formula);
        Assert.EndsWith(")))" + ", 1)", result.Formula);

        var after = book.Read(result.Formula!);
        Assert.Equal(new[] { "visited", "p", "q" }, after.Rows.Select(r => r.Key));
        Assert.Equal(new[] { "item", "c", "w" }, after.DebugKeys);
    }

    [Fact]
    public void RenamingARealVariable_LeavesTheDebugOutputsAlone()
    {
        var book = new Book();
        var engine = book.Read(CanonicalDebug);
        var rows = engine.Rows.Select(r => r.Key == "p" ? r with { Key = "pos" } : r).ToList();
        var result = book.Apply(engine, new StateEngineChange(rows, engine.Fold, null));

        Assert.Null(result.Error);
        Assert.Contains("\"pos\", nextPos, \"item\", item, \"c\", c, \"w\", w)", result.Formula);
        Assert.EndsWith(", 1)", result.Formula);
    }

    [Fact]
    public void RemovingARealVariable_LeavesTheDebugOutputsAlone()
    {
        var book = new Book();
        var engine = book.Read(CanonicalDebug.Replace("nextVisited, VSTACK(visited, nextP)", "nextVisited, visited"));
        var rows = engine.Rows.Select(r => r.Key == "visited" ? r with { Key = "" } : r).ToList();
        var result = book.Apply(engine, new StateEngineChange(rows, engine.Fold, null));

        Assert.Null(result.Error);
        Assert.Contains("SetVars(\"p\", nextP, \"item\", item, \"c\", c, \"w\", w)", result.Formula);
    }

    [Fact]
    public void ANewVariable_CantTakeADebugOutputsName()
    {
        var book = new Book();
        var engine = book.Read(CanonicalDebug);
        var result = book.Apply(engine, new StateEngineChange(Add(engine.Rows, "c", "0"), engine.Fold, null));

        Assert.Null(result.Formula);
        Assert.Contains("debug output", result.Error);
    }

    [Fact]
    public void AKeyThatIsInTheSeed_IsAVariable_EvenIfNothingReadsIt()
    {
        var engine = new Book().Read(CanonicalDebug.Replace("\"p\", G150)", "\"p\", G150, \"item\", 0)"));

        Assert.Equal(new[] { "c", "w" }, engine.DebugKeys);
        Assert.Contains("item", engine.Rows.Select(r => r.Key));
    }

    // ------------------------------------------------------------ review fixes

    [Fact]
    public void ReduceAlreadyInSplitState_EntersWithoutDoubleWrapping_AndExitsClean()
    {
        var wrapped = Fold("=SplitState(REDUCE(", "LAMBDA(a, v, ", ", 1)", "    " + DebugOutput);
        var book = new Book();

        var entered = book.Toggle(wrapped, on: true);
        Assert.DoesNotContain("SplitState(SplitState", entered);
        Assert.Equal(CanonicalDebug, entered);

        Assert.Equal(Normal, book.Toggle(entered, on: false));
    }

    [Fact]
    public void AKeyReadOutsideTheStep_IsNotDebugOnly()
    {
        const string formula = "=GetVar(REDUCE(SetVars(\"p\", G1), xs, LAMBDA(a, v, LET(\n"
            + "    p, GetVar(a, \"p\"),\n    np, p + v,\n    SetVars(\"p\", np, \"ans\", np * 2)\n))), \"ans\")";
        var engine = new Book().Read(formula);

        Assert.Empty(engine.DebugKeys);
        Assert.Contains("ans", engine.Rows.Select(r => r.Key));
    }

    [Fact]
    public void ToggleOff_OnAPlainScan_IsANoOp()
    {
        var plain = Normal.Replace("=REDUCE(", "=SCAN(");
        var book = new Book();

        Assert.Equal(plain, book.Toggle(plain, on: false));
    }

    [Theory]
    [InlineData("    k, GetVar(a, \"x\" & p),")]
    [InlineData("    k, GetVarAt(a, 1),")]
    public void AKeyReadSomeOtherWay_MakesEveryOutputReal(string extraLine)
    {
        var formula = CanonicalDebug.Replace(
            "    p, GetVar(a, \"p\"),", "    p, GetVar(a, \"p\"),\n" + extraLine);
        var engine = new Book().Read(formula);

        Assert.Empty(engine.DebugKeys);
        Assert.Contains("item", engine.Rows.Select(r => r.Key));
    }

    [Fact]
    public void RenamingToADebugOutputsName_SaysSo()
    {
        var book = new Book();
        var engine = book.Read(CanonicalDebug);
        var rows = engine.Rows.Select(r => r.Key == "p" ? r with { Key = "c" } : r).ToList();
        var result = book.Apply(engine, new StateEngineChange(rows, engine.Fold, null));

        Assert.Null(result.Formula);
        Assert.Contains("debug output", result.Error);
    }
}
