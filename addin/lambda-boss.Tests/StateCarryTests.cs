using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Carry a body variable in the state (spec 0018 addendum, #479). Tim's R4 v3 L5 step has a
///     <c>pos</c> line in the body; typing its name in the new-variable row keeps its last value
///     in the state, with no new line and no rename.
/// </summary>
public class StateCarryTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static readonly IReadOnlyList<StateFoldKind> Kinds = Array.Empty<StateFoldKind>();

    // n is the state; pos and cell are plain lines of the step.
    private static readonly string Plain = Lines(
        "=REDUCE(SetVars(\"n\", 0), TEXTSPLIT(G55, \",\"), LAMBDA(a, v, LET(",
        "    n, GetVar(a, \"n\"),",
        "    pos, n + LEN(v),",
        "    cell, pos * 2,",
        "    nextN, n + 1,",
        "    SetVars(\"n\", nextN)",
        ")))");

    private static readonly string CarriedPos = Lines(
        "=REDUCE(SetVars(\"n\", 0, \"pos\", \"\"), TEXTSPLIT(G55, \",\"), LAMBDA(a, v, LET(",
        "    n, GetVar(a, \"n\"),",
        "    pos, n + LEN(v),",
        "    cell, pos * 2,",
        "    nextN, n + 1,",
        "    SetVars(\"n\", nextN, \"pos\", pos)",
        ")))");

    private static StateEngine Read(string formula)
    {
        var read = StateEngines.Read(formula, _ => null, Kinds);
        Assert.Null(read.Refusal);
        return read.Engine!;
    }

    private static StateEngineResult Apply(StateEngine engine, StateEngineChange change) =>
        StateEngines.Apply(engine, change, _ => null, k => k.Name);

    private static StateEngineChange Typed(StateEngine engine, params string[] keys) =>
        new(engine.Rows.Concat(keys.Select(k => new StateEditRow(null, k, ""))).ToList(), engine.Fold, engine.StepName);

    private static string Carry(string formula, string key)
    {
        var engine = Read(formula);
        var result = Apply(engine, Typed(engine, key));
        Assert.Null(result.Error);
        return result.Formula!;
    }

    private static string Toggle(string formula, bool on)
    {
        var engine = Read(formula);
        var result = Apply(engine, StateDebug.Toggle(engine, on, Kinds));
        Assert.Null(result.Error);
        return result.Formula!;
    }

    private static int Count(string text, string part)
    {
        var n = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(part, at + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string[] Keys(string formula) => Read(formula).Rows.Select(r => r.Key).ToArray();

    // ------------------------------------------------------------ carry

    [Fact]
    public void Candidates_AreTheBodyLinesOnly()
    {
        Assert.Equal(new[] { "pos", "cell" }, Read(Plain).CarryCandidates);
    }

    [Fact]
    public void Carry_InAnInlineFold_WritesTheSeedAndOutputPairsAndLeavesTheLine()
    {
        var formula = Carry(Plain, "pos");

        Assert.Equal(CarriedPos, formula);
        Assert.Equal(new[] { "n", "pos" }, Keys(formula));
    }

    [Fact]
    public void Carry_UsesTheLinesOwnSpelling()
    {
        var formula = Carry(Plain, "POS");
        Assert.Contains("\"pos\", pos)", formula);
        Assert.DoesNotContain("\"POS\"", formula);
    }

    [Fact]
    public void ACarriedRow_IsListedAndCarried_AndLeavesTheCandidates()
    {
        var engine = Read(Carry(Plain, "pos"));
        Assert.Equal(new[] { "n", "pos" }, engine.Rows.Select(r => r.Key));
        Assert.Equal(new[] { "pos" }, engine.CarriedKeys);
        Assert.Equal(new[] { "cell" }, engine.CarryCandidates);
        Assert.Empty(Read(Plain).CarriedKeys);
    }

    [Fact]
    public void ACarriedRow_CanBeTickedIntoTheAnswer()
    {
        var engine = Read(Plain);
        var result = Apply(engine, Typed(engine, "pos") with { Answer = new[] { "pos", "n" } });

        Assert.Null(result.Error);
        Assert.StartsWith("=GetVars(REDUCE(SetVars(\"n\", 0, \"pos\", \"\"),", result.Formula);
        Assert.EndsWith("{\"pos\",\"n\"})", result.Formula);
        Assert.Equal(new[] { "pos", "n" }, Read(result.Formula!).Answer);
    }

    [Fact]
    public void ATickedCarriedRow_ThatIsAlreadyThere_CanBeReordered()
    {
        var engine = Read(Carry(Plain, "pos"));
        var result = Apply(engine, new StateEngineChange(engine.Rows, engine.Fold, null, null, new[] { "pos" }));
        Assert.Null(result.Error);
        Assert.EndsWith("{\"pos\"})", result.Formula);
    }

    [Fact]
    public void Unmark_KeepsTheLine()
    {
        var carried = Carry(Plain, "pos");
        var engine = Read(carried);
        var rows = engine.Rows.Select(r => r.Key == "pos" ? r with { Key = "" } : r).ToList();
        var result = Apply(engine, new StateEngineChange(rows, engine.Fold, null));

        Assert.Null(result.Error);
        Assert.Contains("pos, n + LEN(v),", result.Formula);
        Assert.Contains("cell, pos * 2,", result.Formula);
        Assert.DoesNotContain("\"pos\"", result.Formula);
        Assert.Equal(new[] { "n" }, Keys(result.Formula!));
        Assert.Equal(new[] { "pos", "cell" }, Read(result.Formula!).CarryCandidates);
    }

    [Fact]
    public void Unmark_IsBlockedWhileTheCellReadsTheKey()
    {
        var read = Lines("=LET(", "    fold, " + Carry(Plain, "pos")[1..] + ",", "    GetVar(fold, \"pos\")", ")");
        var engine = Read(read);
        var rows = engine.Rows.Select(r => r.Key == "pos" ? r with { Key = "" } : r).ToList();
        var result = Apply(engine, new StateEngineChange(rows, engine.Fold, null));

        Assert.Null(result.Formula);
        Assert.Contains("pos", result.Error);
    }

    [Fact]
    public void Rename_RenamesTheKeyTheLineAndItsReferences()
    {
        var engine = Read(Carry(Plain, "pos"));
        var rows = engine.Rows.Select(r => r.Key == "pos" ? r with { Key = "place" } : r).ToList();
        var result = Apply(engine, new StateEngineChange(rows, engine.Fold, null));

        Assert.Null(result.Error);
        Assert.Equal(CarriedPos.Replace("pos", "place"), result.Formula);
        Assert.Equal(new[] { "n", "place" }, Keys(result.Formula!));
        Assert.Equal(new[] { "place" }, Read(result.Formula!).CarriedKeys);
    }

    [Fact]
    public void Rename_ToANameAnotherLineHas_IsRefused()
    {
        var engine = Read(Carry(Plain, "pos"));
        var rows = engine.Rows.Select(r => r.Key == "pos" ? r with { Key = "cell" } : r).ToList();
        var result = Apply(engine, new StateEngineChange(rows, engine.Fold, null));

        Assert.Null(result.Formula);
        Assert.Contains("cell is already used", result.Error);
    }

    // ------------------------------------------------------------ debug and guard

    [Fact]
    public void DebugOnThenOff_GivesTheTextBackExactly_AndTheCarriedPairStays()
    {
        var carried = Carry(Plain, "pos");
        var debug = Toggle(carried, on: true);

        Assert.StartsWith("=SplitState(SCAN(", debug);
        Assert.DoesNotContain("in.pos", debug);
        Assert.Contains("\"in.n\"", debug);

        // The line is already the output's value: it isn't shown twice.
        Assert.Equal(1, Count(debug, "\"pos\", pos"));
        Assert.Contains("\"cell\", cell", debug);

        var off = Toggle(debug, on: false);
        Assert.Equal(carried, off);
        Assert.Equal(new[] { "n", "pos" }, Keys(off));
    }

    [Fact]
    public void CarryingWhileDebugIsOn_AddsOnlyTheSeedPair_AndSurvivesDebugOff()
    {
        var debug = Toggle(Plain, on: true);
        var engine = Read(debug);
        Assert.Equal(new[] { "n" }, engine.Rows.Select(r => r.Key));
        Assert.Contains("pos", engine.DebugKeys);
        Assert.Contains("pos", engine.CarryCandidates);

        var result = Apply(engine, Typed(engine, "pos"));
        Assert.Null(result.Error);
        Assert.Equal(1, Count(result.Formula!, "\"pos\", pos"));
        Assert.Contains("\"pos\", \"\"", result.Formula);
        Assert.Equal(new[] { "n", "pos" }, Keys(result.Formula!));

        var off = Toggle(result.Formula!, on: false);
        Assert.StartsWith("=REDUCE(", off);
        Assert.Contains("SetVars(\"n\", 0, \"pos\", \"\")", off);
        // The debug pair became the carry, so it keeps the place debug gave it.
        Assert.Equal(1, Count(off, "\"pos\", pos"));
        Assert.DoesNotContain("\"cell\", cell", off);
        Assert.DoesNotContain("in.n", off);
        Assert.Equal(new[] { "n", "pos" }, Keys(off));
    }

    [Fact]
    public void GuardOnThenOff_KeepsTheCarriedVariable()
    {
        var carried = Carry(Plain, "pos");
        var engine = Read(carried);

        var on = Apply(engine, new StateEngineChange(engine.Rows, engine.Fold, null, null, null, true));
        Assert.Null(on.Error);
        Assert.Equal(
            Lines(
                "=REDUCE(SetVars(\"n\", 0, \"pos\", \"\", \"st\", \"go\"), TEXTSPLIT(G55, \",\"), LAMBDA(a, v, LET(",
                "    n, GetVar(a, \"n\"),",
                "    st, GetVar(a, \"st\"),",
                "    pos, n + LEN(v),",
                "    cell, pos * 2,",
                "    nextN, n + 1,",
                "    nextSt, st,",
                "    IF(GetVar(a, \"st\") <> \"go\", a, SetVars(\"n\", nextN, \"pos\", pos, \"st\", nextSt))",
                ")))"),
            on.Formula);

        var guarded = Read(on.Formula!);
        Assert.True(guarded.IsGuarded);
        Assert.Contains("pos", guarded.Rows.Select(r => r.Key));

        var off = Apply(guarded, new StateEngineChange(guarded.Rows, guarded.Fold, null, null, null, false));
        Assert.Null(off.Error);
        Assert.False(Read(off.Formula!).IsGuarded);
        Assert.Equal(
            Lines(
                "=REDUCE(SetVars(\"n\", 0, \"pos\", \"\", \"st\", \"go\"), TEXTSPLIT(G55, \",\"), LAMBDA(a, v, LET(",
                "    n, GetVar(a, \"n\"),",
                "    st, GetVar(a, \"st\"),",
                "    pos, n + LEN(v),",
                "    cell, pos * 2,",
                "    nextN, n + 1,",
                "    nextSt, st,",
                "    SetVars(\"n\", nextN, \"pos\", pos, \"st\", nextSt)",
                ")))"),
            off.Formula);
    }

    // ------------------------------------------------------------ what can't be carried

    // The GetVar line of n is called count; f is a LAMBDA; out is the output's own line.
    private static readonly string Awkward = Lines(
        "=REDUCE(SetVars(\"n\", 0), TEXTSPLIT(G55, \",\"), LAMBDA(a, v, LET(",
        "    count, GetVar(a, \"n\"),",
        "    f, LAMBDA(x, x + 1),",
        "    pos, f(count) + LEN(v),",
        "    nextN, count + 1,",
        "    out, SetVars(\"n\", nextN),",
        "    out",
        ")))");

    [Fact]
    public void NonCarryableLines_AreNotCandidates()
    {
        Assert.Equal(new[] { "pos" }, Read(Awkward).CarryCandidates);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("f")]
    [InlineData("out")]
    [InlineData("nextN")]
    [InlineData("a")]
    public void ANonCarryableName_StillAddsANormalVariable(string name)
    {
        var engine = Read(Awkward);
        var result = Apply(engine, Typed(engine, name));

        // Either a normal variable with a line of its own, or a plain refusal: never a carry.
        if (result.Formula == null)
        {
            Assert.False(string.IsNullOrEmpty(result.Error));
            return;
        }

        Assert.DoesNotContain($"\"{name}\", {name})", result.Formula);
        Assert.Contains(name, Keys(result.Formula));
        Assert.Empty(Read(result.Formula).CarriedKeys);
    }

    [Fact]
    public void ALineAfterTheOutput_IsNotCarried()
    {
        // The guard line comes after the output's own line, so the output can't read it.
        var after = Lines(
            "=REDUCE(SetVars(\"n\", 0), TEXTSPLIT(G55, \",\"), LAMBDA(a, v, LET(",
            "    n, GetVar(a, \"n\"),",
            "    nextN, n + 1,",
            "    out, SetVars(\"n\", nextN),",
            "    late, n + 5,",
            "    IF(late > 99, a, out)",
            ")))");
        Assert.DoesNotContain("late", Read(after).CarryCandidates);
    }

    // ------------------------------------------------------------ the step parameter (#492)

    // Tim's R4 v3 L7: carrying v, the fold lambda's own step parameter, so the answer gives the
    // step number. v is in scope every step: no GetVar line, no rename.
    private static readonly string CarriedV = Lines(
        "=REDUCE(SetVars(\"n\", 0, \"v\", \"\"), TEXTSPLIT(G55, \",\"), LAMBDA(a, v, LET(",
        "    n, GetVar(a, \"n\"),",
        "    pos, n + LEN(v),",
        "    cell, pos * 2,",
        "    nextN, n + 1,",
        "    nextV, v,",
        "    SetVars(\"n\", nextN, \"v\", nextV)",
        ")))");

    [Fact]
    public void CarryTheStepParameter_AddsSeedNextLineAndPair_AndNoGetVar()
    {
        var formula = Carry(Plain, "v");

        Assert.Equal(CarriedV, formula);
        Assert.DoesNotContain("GetVar(a, \"v\")", formula);
        Assert.DoesNotContain("v_", formula);
        Assert.Equal(new[] { "n", "v" }, Keys(formula));
    }

    [Fact]
    public void CarryTheStepParameter_UsesItsOwnSpelling()
    {
        Assert.Equal(CarriedV, Carry(Plain, "V"));
    }

    [Fact]
    public void CarryTheStepParameter_WhateverItIsCalled()
    {
        var plain = Plain.Replace("LAMBDA(a, v,", "LAMBDA(a, step,").Replace("LEN(v)", "LEN(step)");
        var formula = Carry(plain, "step");

        Assert.Contains("SetVars(\"n\", 0, \"step\", \"\")", formula);
        Assert.Contains("nextStep, step,", formula);
        Assert.Contains("SetVars(\"n\", nextN, \"step\", nextStep)", formula);
        Assert.DoesNotContain("GetVar(a, \"step\")", formula);
    }

    [Fact]
    public void CarryTheStepParameter_CanBeTickedIntoTheAnswer()
    {
        var engine = Read(Plain);
        var result = Apply(engine, Typed(engine, "v") with { Answer = new[] { "n", "v" } });

        Assert.Null(result.Error);
        Assert.DoesNotContain("GetVar(a, \"v\")", result.Formula);
        Assert.Contains("nextV, v,", result.Formula);
        Assert.EndsWith("{\"n\",\"v\"})", result.Formula);
        Assert.Equal(new[] { "n", "v" }, Read(result.Formula!).Answer);
    }

    [Fact]
    public void UntickTheStepParameter_GivesTheStepBackExactly()
    {
        var engine = Read(CarriedV);
        var rows = engine.Rows.Select(r => r.Key == "v" ? r with { Key = "" } : r).ToList();
        var result = Apply(engine, new StateEngineChange(rows, engine.Fold, null));

        Assert.Null(result.Error);
        Assert.Equal(Plain, result.Formula);
    }

    [Fact]
    public void DebugOnThenOff_WithTheStepParameterCarried_ShowsInVAsV_AndRoundTrips()
    {
        var debug = Toggle(CarriedV, on: true);

        Assert.StartsWith("=SplitState(SCAN(", debug);
        Assert.Contains("\"in.v\", v", debug);
        Assert.Contains("\"in.n\", n", debug);
        Assert.DoesNotContain("GetVar(a, \"v\")", debug);
        Assert.Equal(new[] { "n", "v" }, Keys(debug));

        var off = Toggle(debug, on: false);
        Assert.Equal(CarriedV, off);
    }

    [Fact]
    public void CarryTheStepParameter_WhileDebugIsOn_SurvivesDebugOff()
    {
        var debug = Toggle(Plain, on: true);
        var engine = Read(debug);
        var result = Apply(engine, Typed(engine, "v"));

        Assert.Null(result.Error);
        Assert.DoesNotContain("GetVar(a, \"v\")", result.Formula);

        var off = Toggle(result.Formula!, on: false);
        Assert.StartsWith("=REDUCE(", off);
        Assert.Contains("SetVars(\"n\", 0, \"v\", \"\")", off);
        Assert.Contains("nextV, v,", off);
        Assert.Contains("\"v\", nextV", off);
        Assert.DoesNotContain("in.", off);
        Assert.Equal(new[] { "n", "v" }, Keys(off));
    }

    [Theory]
    [InlineData("n")]
    [InlineData("pos")]
    [InlineData("v")]
    public void CarryTheStepParameter_WithASeedReadingTheStep_IsRefused(string seed)
    {
        var engine = Read(Plain);
        var change = new StateEngineChange(
            engine.Rows.Append(new StateEditRow(null, "v", seed)).ToList(), engine.Fold, engine.StepName);
        var result = Apply(engine, change);

        Assert.Null(result.Formula);
        Assert.Contains("can't read", result.Error);
    }

    [Fact]
    public void CarryTheStepParameter_WithATypedSeed_UsesIt()
    {
        var engine = Read(Plain);
        var change = new StateEngineChange(
            engine.Rows.Append(new StateEditRow(null, "v", "0")).ToList(), engine.Fold, engine.StepName);
        var result = Apply(engine, change);

        Assert.Null(result.Error);
        Assert.Equal(CarriedV.Replace("\"v\", \"\"", "\"v\", 0"), result.Formula);
    }

    [Fact]
    public void CarryABodyLine_AndTheStepParameter_Together()
    {
        var engine = Read(Plain);
        var result = Apply(engine, Typed(engine, "pos", "v"));

        Assert.Null(result.Error);
        Assert.Contains("SetVars(\"n\", 0, \"pos\", \"\", \"v\", \"\")", result.Formula);
        Assert.Contains("SetVars(\"n\", nextN, \"pos\", pos, \"v\", nextV)", result.Formula);
        Assert.DoesNotContain("GetVar(a, \"v\")", result.Formula);
        Assert.Equal(new[] { "pos" }, Read(result.Formula!).CarriedKeys);
    }

    [Fact]
    public void CarryTheStepParameter_InTheUnfoldedLetStep()
    {
        var engine = Read(LetStep);
        var result = Apply(engine, Typed(engine, "v"));

        Assert.Null(result.Error);
        Assert.Contains("_seed, SetVars(\"n\", 0, \"v\", \"\")", result.Formula);
        Assert.Contains("nextV, v,", result.Formula);
        Assert.Contains("SetVars(\"n\", nextN, \"v\", nextV)", result.Formula);
        Assert.DoesNotContain("GetVar(a, \"v\")", result.Formula);
    }

    [Fact]
    public void StateEditApply_CarriesTheInputWithNoValue()
    {
        // The 0011 dialog: no parameter names, the input is the LET's v line.
        var formula = Lines(
            "=LET(",
            "    a, SetVars(\"n\", 0),",
            "    v, A1,",
            "    n, GetVar(a, \"n\"),",
            "    nextN, n + v,",
            "    SetVars(\"n\", nextN)",
            ")");
        var result = StateEdit.Apply(formula, new[]
        {
            new StateEditRow("n", "n", "0"),
            new StateEditRow(null, "v", "")
        });

        Assert.Null(result.Error);
        Assert.Contains("SetVars(\"n\", 0, \"v\", \"\")", result.Formula);
        Assert.Contains("nextV, v,", result.Formula);
        Assert.DoesNotContain("GetVar(a, \"v\")", result.Formula);
    }

    // ------------------------------------------------------------ the unfolded LET step

    private static readonly string LetStep = Lines(
        "=LET(",
        "    _seed, SetVars(\"n\", 0),",
        "    _inputs, TEXTSPLIT(G55, \",\"),",
        "    a, _seed,",
        "    v, INDEX(_inputs, 1),",
        "    n, GetVar(a, \"n\"),",
        "    pos, n + LEN(v),",
        "    nextN, n + 1,",
        "    SetVars(\"n\", nextN)",
        ")");

    [Fact]
    public void Carry_InTheUnfoldedLetStep_Works()
    {
        var engine = Read(LetStep);
        Assert.Null(engine.Fold);
        Assert.Equal(new[] { "pos" }, engine.CarryCandidates);

        var result = Apply(engine, Typed(engine, "pos"));
        Assert.Null(result.Error);
        Assert.Contains("_seed, SetVars(\"n\", 0, \"pos\", \"\")", result.Formula);
        Assert.Contains("SetVars(\"n\", nextN, \"pos\", pos)", result.Formula);
        Assert.Contains("pos, n + LEN(v),", result.Formula);
        Assert.Equal(new[] { "n", "pos" }, Keys(result.Formula!));
    }

    [Fact]
    public void StateEditApply_CarriesAZeroValueRowToo()
    {
        // The 0011 dialog: the row has no value, and the step is a plain LET.
        var formula = Lines(
            "=LET(",
            "    a, SetVars(\"n\", 0),",
            "    v, A1,",
            "    n, GetVar(a, \"n\"),",
            "    pos, n + v,",
            "    nextN, n + 1,",
            "    SetVars(\"n\", nextN)",
            ")");
        var result = StateEdit.Apply(formula, new[]
        {
            new StateEditRow("n", "n", "0"),
            new StateEditRow(null, "pos", "")
        });

        Assert.Null(result.Error);
        Assert.Contains("\"pos\", \"\"", result.Formula);
        Assert.Contains("\"pos\", pos", result.Formula);
        Assert.Contains("pos, n + v,", result.Formula);
        Assert.Equal(new[] { "pos" }, StateEdit.CarriedKeys(result.Formula!));
    }
}
