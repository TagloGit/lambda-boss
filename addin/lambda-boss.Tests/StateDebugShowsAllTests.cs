using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Debug on shows everything (spec 0015, "Debug shows everything"): the incoming value of each
///     state variable as <c>in.key</c>, then every LET intermediate, then Tim's own outputs. Debug
///     off gives back exactly what he wrote. Tim's 11 Nov R4 retry is the example: seeds of "",
///     so the first buy is <c>"" + 4</c> and <c>bought</c> is the first #VALUE!.
/// </summary>
public class StateDebugShowsAllTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static readonly IReadOnlyList<StateFoldKind> Kinds = Array.Empty<StateFoldKind>();

    // Tim's R4 case: crates bought at 3 and sold at 5, seeded with "" by mistake.
    private static readonly string Trades = Lines(
        "=REDUCE(SetVars(\"crates\", \"\", \"cash\", \"\"), TEXTSPLIT(\"B4,S2,B9,S9,B5\", \",\"), LAMBDA(a, v, LET(",
        "    crates, GetVar(a, \"crates\"),",
        "    cash, GetVar(a, \"cash\"),",
        "    qty, --MID(v, 2, 9),",
        "    bought, IF(LEFT(v) = \"B\", crates + qty, crates),",
        "    nextCrates, IF(LEFT(v) = \"S\", bought - qty, bought),",
        "    nextCash, cash + IF(LEFT(v) = \"B\", -3, 5) * qty,",
        "    SetVars(\"crates\", nextCrates, \"cash\", nextCash)",
        ")))");

    private const string TradesShown =
        "SetVars(\"in.crates\", crates, \"in.cash\", cash, \"qty\", qty, \"bought\", bought, \"crates\", nextCrates, \"cash\", nextCash)";

    private static StateEngine Read(string formula)
    {
        var read = StateEngines.Read(formula, _ => null, Kinds);
        Assert.Null(read.Refusal);
        return read.Engine!;
    }

    private static StateEngineResult Toggle(string formula, bool on)
    {
        var engine = Read(formula);
        var result = StateEngines.Apply(engine, StateDebug.Toggle(engine, on, Kinds), _ => null, k => k.Name);
        Assert.Null(result.Error);
        return result;
    }

    [Fact]
    public void TimsCase_DebugOn_ShowsTheIncomingBlankAndBoughtInOneStep()
    {
        var result = Toggle(Trades, on: true);

        var expected = "=SplitState(" + Trades[1..].Replace("REDUCE(", "SCAN(")
            .Replace("SetVars(\"crates\", nextCrates, \"cash\", nextCash)", TradesShown) + ", 1)";
        Assert.Equal(expected, result.Formula);
        Assert.Contains(result.Notes, n => n.Contains("in.crates, in.cash, qty, bought"));

        // They are debug outputs: the variables are still crates and cash.
        var debug = Read(result.Formula!);
        Assert.True(debug.IsDebug);
        Assert.Equal(new[] { "crates", "cash" }, debug.Rows.Select(r => r.Key));
        Assert.Equal(new[] { "in.crates", "in.cash", "qty", "bought" }, debug.DebugKeys);
    }

    [Fact]
    public void TimsCase_DebugOff_GivesBackExactlyWhatHeWrote()
    {
        var debug = Toggle(Trades, on: true).Formula!;
        var off = Toggle(debug, on: false);

        Assert.Equal(Trades, off.Formula);
        Assert.Contains(off.Notes, n => n.Contains("in.crates, in.cash, qty, bought"));
    }

    [Fact]
    public void ADebugOutputDeletedByHand_StaysGone_AndOffCleansUpTheRest()
    {
        var debug = Toggle(Trades, on: true).Formula!.Replace("\"qty\", qty, ", "");

        Assert.Equal(new[] { "in.crates", "in.cash", "bought" }, Read(debug).DebugKeys);
        Assert.Equal(Trades, Toggle(debug, on: false).Formula);
    }

    [Fact]
    public void ALambdaInTheLet_IsNotShown()
    {
        var withHelper = Trades
            .Replace("    qty, --MID(v, 2, 9),", "    price, LAMBDA(side, IF(side = \"B\", -3, 5)),\n    qty, --MID(v, 2, 9),")
            .Replace("IF(LEFT(v) = \"B\", -3, 5) * qty", "price(LEFT(v)) * qty");

        var debug = Toggle(withHelper, on: true).Formula!;

        Assert.DoesNotContain("\"price\"", debug);
        Assert.Contains("\"qty\", qty, \"bought\", bought, \"crates\", nextCrates", debug);
        Assert.Equal(withHelper, Toggle(debug, on: false).Formula);
    }

    [Fact]
    public void ANamedOutput_IsNotShownTwice()
    {
        var named = Trades.Replace(
            "    SetVars(\"crates\", nextCrates, \"cash\", nextCash)",
            "    out, SetVars(\"crates\", nextCrates, \"cash\", nextCash),\n    out");

        var debug = Toggle(named, on: true).Formula!;

        Assert.Contains("    out, " + TradesShown + ",\n    out\n", debug);
        Assert.Equal(named, Toggle(debug, on: false).Formula);
    }

    [Fact]
    public void PastTenPairs_TheRestGoOnAsSetVarLayers_AndOffStillGivesTheTextBack()
    {
        // Four variables, five intermediates (the outputs are expressions, so none is a next
        // line): 4 + 5 + 4 = 13 pairs, three past SetVars' ten.
        var big = Lines(
            "=REDUCE(SetVars(\"a1\", 0, \"a2\", 0, \"a3\", 0, \"a4\", 0), SEQUENCE(3), LAMBDA(s, v, LET(",
            "    a1, GetVar(s, \"a1\"),",
            "    a2, GetVar(s, \"a2\"),",
            "    a3, GetVar(s, \"a3\"),",
            "    a4, GetVar(s, \"a4\"),",
            "    t1, a1 + v,",
            "    t2, a2 + t1,",
            "    t3, a3 + t2,",
            "    t4, a4 + t3,",
            "    t5, t4 * 2,",
            "    SetVars(\"a1\", t1*1, \"a2\", t2*1, \"a3\", t3*1, \"a4\", t5*1)",
            ")))");

        var result = Toggle(big, on: true);
        var debug = result.Formula!;

        Assert.Contains(
            "    SetVar(SetVar(SetVar(SetVars(\"in.a1\", a1, \"in.a2\", a2, \"in.a3\", a3, \"in.a4\", a4, \"t1\", t1, \"t2\", t2, "
            + "\"a1\", t1*1, \"a2\", t2*1, \"a3\", t3*1, \"a4\", t5*1), \"t3\", t3), \"t4\", t4), \"t5\", t5)\n",
            debug);

        var engine = Read(debug);
        Assert.Equal(new[] { "in.a1", "in.a2", "in.a3", "in.a4", "t1", "t2", "t3", "t4", "t5" }, engine.DebugKeys);
        Assert.Equal(new[] { "a1", "a2", "a3", "a4" }, engine.Rows.Select(r => r.Key));
        Assert.Equal(big, Toggle(debug, on: false).Formula);
    }

    [Fact]
    public void AStepWithNoIntermediates_ShowsTheIncomingValue()
    {
        var bare = Lines(
            "=REDUCE(SetVars(\"n\", 0), SEQUENCE(3), LAMBDA(a, v, LET(",
            "    n, GetVar(a, \"n\"),",
            "    SetVars(\"n\", n + v)",
            ")))");

        var debug = Toggle(bare, on: true).Formula!;

        Assert.Contains("SetVars(\"in.n\", n, \"n\", n + v)", debug);
        Assert.Equal(bare, Toggle(debug, on: false).Formula);
    }

    [Fact]
    public void DebugOn_WithAVariableAdded_ShowsTheNewOneToo()
    {
        var engine = Read(Trades);
        var change = StateDebug.Toggle(engine, true, Kinds) with
        {
            Rows = engine.Rows.Append(new StateEditRow(null, "days", "0")).ToList()
        };
        var result = StateEngines.Apply(engine, change, _ => null, k => k.Name);

        Assert.Null(result.Error);
        Assert.Contains("\"in.crates\", crates, \"in.cash\", cash, \"in.days\", days, \"qty\", qty, \"bought\", bought", result.Formula);
        Assert.Contains("\"days\", nextDays)", result.Formula);
    }

    // ------------------------------------------------------------ review of #457

    private static readonly string Counter = Lines(
        "=REDUCE(SetVars(\"n\", 0, \"m\", 0), SEQUENCE(3), LAMBDA(a, v, LET(",
        "    n, GetVar(a, \"n\"),",
        "    m, GetVar(a, \"m\"),",
        "    nn, n + v,",
        "    mm, m * 2,",
        "    SetVar(SetVars(\"n\", nn), \"m\", mm)",
        ")))");

    [Fact]
    public void ASetVarChainTimWrote_KeepsItsTextBothWays()
    {
        var debug = Toggle(Counter, on: true).Formula!;

        Assert.Contains("    SetVar(SetVars(\"in.n\", n, \"in.m\", m, \"n\", nn), \"m\", mm)\n", debug);
        Assert.Equal(Counter, Toggle(debug, on: false).Formula);
    }

    [Fact]
    public void AGuardHeldInALaterLine_IsNotPutInsideTheOutput()
    {
        var guarded = Lines(
            "=REDUCE(SetVars(\"n\", 0), SEQUENCE(3), LAMBDA(a, v, LET(",
            "    n, GetVar(a, \"n\"),",
            "    nn, n + v,",
            "    out, SetVars(\"n\", nn),",
            "    res, IF(v = \"\", a, out),",
            "    res",
            ")))");

        var debug = Toggle(guarded, on: true).Formula!;

        Assert.Contains("    out, SetVars(\"in.n\", n, \"n\", nn),\n", debug);
        Assert.DoesNotContain("\"res\"", debug);
        Assert.Equal(guarded, Toggle(debug, on: false).Formula);
    }

    [Fact]
    public void ALetEndingInALambda_OrANameFromTheWorkbook_IsNotShown()
    {
        var helpers = Counter
            .Replace("    nn, n + v,", "    step, LET(k, 2, LAMBDA(x, x * k)),\n    inc, MyInc,\n    nn, n + step(v),");

        var debug = Toggle(helpers, on: true).Formula!;

        Assert.DoesNotContain("\"step\"", debug);
        Assert.DoesNotContain("\"inc\"", debug);
        Assert.Equal(helpers, Toggle(debug, on: false).Formula);
    }

    [Fact]
    public void AKeyReadFromOutsideTheFold_IsNotAddedAsADebugOutput()
    {
        var outside = "=LET(r, REDUCE(SetVars(\"n\", 0), SEQUENCE(3), LAMBDA(a, v, LET(\n"
            + "    n, GetVar(a, \"n\"),\n    x, v * 2,\n    SetVars(\"n\", n + x)\n))), IF(GetVar(r, \"n\") > 5, \"x\", \"y\"))";

        var debug = Toggle(outside, on: true).Formula!;

        Assert.DoesNotContain("\"x\", x", debug);
        Assert.Equal(new[] { "n" }, Read(debug).Rows.Select(r => r.Key));
        Assert.Equal(outside, Toggle(debug, on: false).Formula);
    }

    // ------------------------------------------------------------ a "" seed used in arithmetic

    [Fact]
    public void ABlankSeedUsedInArithmetic_IsNoted()
    {
        var note = StateDebug.BlankSeedNote(Read(Trades));

        Assert.NotNull(note);
        Assert.StartsWith("crates and cash start as \"\" in the seed, and bought, nextCash", note);
    }

    [Fact]
    public void ANumberSeed_OrABlankNobodyAddsTo_IsNotNoted()
    {
        Assert.Null(StateDebug.BlankSeedNote(Read(Trades.Replace("SetVars(\"crates\", \"\", \"cash\", \"\")", "SetVars(\"crates\", 0, \"cash\", 0)"))));

        // A fresh scaffold: the placeholders pass straight through.
        var fresh = Lines(
            "=REDUCE(SetVars(\"p\", \"\"), SEQUENCE(1), LAMBDA(a, v, LET(",
            "    p, GetVar(a, \"p\"),",
            "    nextP, p,",
            "    SetVars(\"p\", nextP)",
            ")))");
        Assert.Null(StateDebug.BlankSeedNote(Read(fresh)));
    }

    // ------------------------------------------------------------ Loops loads bare

    [Theory]
    [InlineData("loops", null, "loops", "")]
    [InlineData("Loops", null, "loops", "")]
    [InlineData("loops", "", "loops", "")]
    [InlineData("loops", "loops.", "loops", "loops")]
    [InlineData("loops", "lp.", "loops", "lp")]
    [InlineData("string", null, "str", "str")]
    public void LoopsLoadsWithNoPrefix_UnlessTheWorkbookAlreadyHasAPrefixedCopy(
        string folder, string? statePrefix, string defaultPrefix, string expected)
    {
        Assert.Equal(expected, StateScaffold.LoadPrefix(folder, statePrefix, defaultPrefix));
    }

    [Fact]
    public void AFoldNotYetLoaded_IsSpelledAsItWillLoad()
    {
        Assert.Equal("REDUCEUNTIL", StateScaffold.NameToLoad("loops", null, "loops", "REDUCEUNTIL"));
        Assert.Equal("loops.REDUCEUNTIL", StateScaffold.NameToLoad("loops", "loops.", "loops", "REDUCEUNTIL"));
    }

    [Fact]
    public void ANewEngineWithNoLoops_IsWrittenBare()
    {
        var engine = StateEngine.New("");
        var result = StateEngines.Apply(
            engine,
            new StateEngineChange(new[] { new StateEditRow(null, "crates", ""), new StateEditRow(null, "cash", "") }, StateFoldKind.Reduce, null),
            _ => null, k => k.Name);

        Assert.Null(result.Error);
        Assert.StartsWith("=REDUCE(SetVars(\"crates\", \"\", \"cash\", \"\"), SEQUENCE(1), LAMBDA(a, v, LET(", result.Formula);
        Assert.DoesNotContain("loops.", result.Formula);
    }
}
