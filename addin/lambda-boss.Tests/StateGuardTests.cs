using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     The /State guard toggle (spec 0015, "Guard toggle (#464)"). On wraps the step's body in
///     <c>IF(GetVar(a, "st") &lt;&gt; "go", a, …)</c>, adding <c>st</c> seeded "go" when it's missing;
///     off takes it off and gives back the body exactly.
/// </summary>
public class StateGuardTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private const string Guard = "IF(GetVar(a, \"st\") <> \"go\", a, ";

    private const string Output = "SetVars(\"pos\", nextPos, \"st\", nextSt)";

    // Tim's walk with st already in it (the lesson's recipe step 2), Tim's own spacing.
    private static string Walk(string output, string open = "=REDUCE(", string close = "", string seed = "SetVars(\"pos\", \"BA31\", \"st\", \"go\")") =>
        Lines(
            open + seed + ", SEQUENCE(30), LAMBDA(a,v, LET(",
            "    pos, GetVar(a, \"pos\"),",
            "    st, GetVar(a, \"st\"),",
            "    nextPos, pos&\"x\",",
            "    nextSt, IF(nextPos=\"BAx\", \"DONE\", st),",
            "    " + output,
            ")))" + close);

    private static readonly string Plain = Walk(Output);
    private static readonly string Guarded = Walk(Guard + Output + ")");

    // A walk with no st yet.
    private static readonly string NoStatus = Lines(
        "=REDUCE(SetVars(\"pos\", \"BA31\"), SEQUENCE(30), LAMBDA(a, v, LET(",
        "    pos, GetVar(a, \"pos\"),",
        "    nextPos, pos&\"x\",",
        "    SetVars(\"pos\", nextPos)",
        ")))");

    private static readonly IReadOnlyList<StateFoldKind> Kinds = Array.Empty<StateFoldKind>();

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    private static string? RefersTo(string name) => Names.TryGetValue(name, out var v) ? v : null;

    private static StateEngine Read(string formula)
    {
        var read = StateEngines.Read(formula, RefersTo, Kinds);
        Assert.Null(read.Refusal);
        Assert.NotNull(read.Engine);
        return read.Engine!;
    }

    private static StateEngineResult Apply(StateEngine engine, StateEngineChange change) =>
        StateEngines.Apply(engine, change, RefersTo, k => k.Name);

    private static string Guard_(string formula, bool on)
    {
        var engine = Read(formula);
        var result = Apply(engine, new StateEngineChange(engine.Rows, engine.Fold, engine.StepName, Guard: on));
        Assert.Null(result.Error);
        return result.Formula!;
    }

    private static string Debug(string formula, bool on)
    {
        var engine = Read(formula);
        var result = Apply(engine, StateDebug.Toggle(engine, on, Kinds));
        Assert.Null(result.Error);
        return result.Formula!;
    }

    private static string ToggleCommand(string formula)
    {
        var engine = Read(formula);
        var result = Apply(engine, StateGuard.Toggle(engine));
        Assert.Null(result.Error);
        return result.Formula!;
    }

    // ------------------------------------------------------------ recognition

    [Fact]
    public void TheFixedGuard_ReadsAsAGuardedStep_WithStAVariable()
    {
        var engine = Read(Guarded);

        Assert.True(engine.IsGuarded);
        Assert.Equal(new[] { "pos", "st" }, engine.Rows.Select(r => r.Key));
        Assert.Empty(engine.DebugKeys);
    }

    [Fact]
    public void TimsOrLeftGuard_StillReads_AndCountsAsGuarded()
    {
        var engine = Read(Walk("IF(OR(GetVar(a,\"st\")=\"DONE\", LEFT(GetVar(a,\"st\"),4)=\"EDGE\"), a, " + Output + ")"));

        Assert.True(engine.IsGuarded);
        Assert.Equal(new[] { "pos", "st" }, engine.Rows.Select(r => r.Key));
    }

    [Fact]
    public void APlainStep_IsNotGuarded()
    {
        Assert.False(Read(Plain).IsGuarded);
        Assert.False(Read(NoStatus).IsGuarded);
    }

    [Theory]
    [InlineData("IF(x, a, SetVars(\"p\", 1))", "SetVars(\"p\", 1)")]
    [InlineData("IF(x, SetVars(\"p\", 1), a)", "SetVars(\"p\", 1)")]
    [InlineData("IF(x,a,  out)", "out")]
    [InlineData("IF( x , A , IF(y, a, out) )", "IF(y, a, out)")]
    public void TryUnwrap_GivesTheOtherBranchExactly(string body, string inner)
    {
        Assert.True(StateGuard.TryUnwrap(body, "a", out var got));
        Assert.Equal(inner, got);
    }

    [Theory]
    [InlineData("SetVars(\"p\", 1)")]
    [InlineData("IF(x, b, SetVars(\"p\", 1))")]
    [InlineData("IF(x, a)")]
    [InlineData("IFS(x, a, TRUE, out)")]
    [InlineData("")]
    public void TryUnwrap_RefusesWhatIsntAGuard(string body)
    {
        Assert.False(StateGuard.TryUnwrap(body, "a", out _));
    }

    // ------------------------------------------------------------ on and off

    [Fact]
    public void On_WrapsOnlyTheBody_EverythingElseCharacterForCharacter()
    {
        Assert.Equal(Guarded, Guard_(Plain, true));
    }

    [Fact]
    public void Off_GivesBackTheBodyExactly()
    {
        Assert.Equal(Plain, Guard_(Guarded, false));
    }

    [Fact]
    public void OnThenOff_AndOffThenOn_RoundTrip()
    {
        Assert.Equal(Plain, Guard_(Guard_(Plain, true), false));
        Assert.Equal(Guarded, Guard_(Guard_(Guarded, false), true));
    }

    [Fact]
    public void TheCommand_Flips()
    {
        var on = ToggleCommand(Plain);
        Assert.Equal(Guarded, on);
        Assert.Equal(Plain, ToggleCommand(on));
    }

    [Fact]
    public void OnWhenGuarded_AndOffWhenNot_DontWrapOrUnwrapAgain()
    {
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Guard_(Guarded, true), "<> \"go\""));
        Assert.False(Read(Guard_(Plain, false)).IsGuarded);
    }

    [Fact]
    public void Off_TakesTimsOwnGuardOffToo_AndOnPutsTheFixedOneBack()
    {
        var tims = Walk("IF(OR(GetVar(a,\"st\")=\"DONE\", LEFT(GetVar(a,\"st\"),4)=\"EDGE\"), a, " + Output + ")");

        var off = Guard_(tims, false);
        Assert.Equal(Plain, off);
        Assert.Equal(Guarded, Guard_(off, true));
    }

    [Fact]
    public void On_WithNoSt_AddsItSeededGo_AndReadsItInTheGuard()
    {
        var engine = Read(NoStatus);
        var result = Apply(engine, new StateEngineChange(engine.Rows, engine.Fold, engine.StepName, Guard: true));
        Assert.Null(result.Error);
        var on = result.Formula!;

        Assert.Contains("SetVars(\"pos\", \"BA31\", \"st\", \"go\")", on);
        Assert.Contains("st, GetVar(a, \"st\")", on);
        Assert.Matches(@"IF\(GetVar\(a, ""st""\) <> ""go"", a, SetVars\(""pos"", nextPos, ""st"", \w+\)\)", on);
        Assert.Contains(result.Notes, n => n.StartsWith("Guard on: st is a new state variable", StringComparison.Ordinal));

        var read = Read(on);
        Assert.True(read.IsGuarded);
        Assert.Equal(new[] { "pos", "st" }, read.Rows.Select(r => r.Key));

        // Off leaves st; on again round-trips.
        var off = Guard_(on, false);
        Assert.DoesNotContain("<> \"go\"", off);
        Assert.Contains("\"st\", \"go\"", off);
        Assert.Equal(on, Guard_(off, true));
    }

    [Fact]
    public void On_WithStTypedInTheDialog_SeedsItGo_NotBlank()
    {
        var engine = Read(NoStatus);
        var rows = engine.Rows.Append(new StateEditRow(null, "st", "")).ToList();
        var result = Apply(engine, new StateEngineChange(rows, engine.Fold, engine.StepName, Guard: true));

        Assert.Null(result.Error);
        Assert.DoesNotContain(result.Notes, n => n.StartsWith("Guard on:", StringComparison.Ordinal));
        Assert.Contains("SetVars(\"pos\", \"BA31\", \"st\", \"go\")", result.Formula);
        Assert.Contains("<> \"go\", a, ", result.Formula);
    }

    [Fact]
    public void ANamedOutput_RoundTripsExactly()
    {
        // `out` also names its own binding: the body is found by position, not by text.
        var named = Walk("out, " + Output + ",\n    out");

        var on = Guard_(named, true);
        Assert.Equal(Walk("out, " + Output + ",\n    " + Guard + "out)"), on);
        Assert.Equal(named, Guard_(on, false));
    }

    [Fact]
    public void GuardOff_AndRemovingSt_InOneSave_Works()
    {
        // On adds st with a plain next line; untick the box and clear st in the same save.
        var engine = Read(Guard_(NoStatus, true));
        var rows = engine.Rows.Select(r => r.Key == "st" ? r with { Key = "" } : r).ToList();
        var result = Apply(engine, new StateEngineChange(rows, engine.Fold, engine.StepName, Guard: false));

        Assert.Null(result.Error);
        Assert.False(Read(result.Formula!).IsGuarded);
        Assert.DoesNotContain("\"st\"", result.Formula);
        Assert.DoesNotContain("<> \"go\"", result.Formula);
    }

    [Fact]
    public void RemovingSt_WhileGuarded_IsBlocked()
    {
        var engine = Read(Guarded);
        var rows = engine.Rows.Select(r => r.Key == "st" ? r with { Key = "" } : r).ToList();
        var result = Apply(engine, new StateEngineChange(rows, engine.Fold, engine.StepName, Guard: true));

        Assert.Null(result.Formula);
    }

    [Fact]
    public void TheGuard_UsesTheStepsOwnStateName_AndThePrefix()
    {
        var formula = Plain.Replace("LAMBDA(a,v,", "LAMBDA(acc,v,").Replace("(a, ", "(acc, ")
            .Replace("GetVar(", "loops.GetVar(").Replace("SetVars(", "loops.SetVars(");

        var on = Guard_(formula, true);

        Assert.Contains("IF(loops.GetVar(acc, \"st\") <> \"go\", acc, loops.SetVars(\"pos\", nextPos, \"st\", nextSt))", on);
        Assert.Equal(formula, Guard_(on, false));
    }

    // ------------------------------------------------------------ composition

    [Fact]
    public void ComposesWithDebug_InEitherOrder()
    {
        // Guard on, Debug on, Guard off, Debug off.
        var a = Debug(Guard_(Debug(Guard_(Plain, true), true), false), false);
        Assert.Equal(Plain, a);

        // Debug on, Guard on, Debug off, Guard off.
        var debugged = Debug(Plain, true);
        var guardedDebug = Guard_(debugged, true);
        Assert.StartsWith("=SplitState(SCAN(", guardedDebug);
        Assert.Contains(Guard + "SetVars(\"in.pos\", pos", guardedDebug);
        Assert.Equal(Plain, Guard_(Debug(guardedDebug, false), false));
    }

    [Fact]
    public void Debug_OnAGuardedStep_KeepsTheGuard_AndRoundTrips()
    {
        var on = Debug(Guarded, true);
        Assert.Contains(Guard + "SetVars(\"in.pos\", pos", on);
        Assert.Equal(Guarded, Debug(on, false));
    }

    [Fact]
    public void ComposesWithAnOuterLet_AndTheAnswerTicks()
    {
        var outer = Lines("=LET(", "    goal, \"BAx\",", "    GetVars(" + Walk(Output, "REDUCE(").Replace("\"BAx\"", "goal") + ", {\"pos\",\"st\"})", ")");

        var on = Guard_(outer, true);
        Assert.StartsWith("=LET(\n    goal, \"BAx\",\n    GetVars(REDUCE(", on);
        Assert.Contains(Guard + Output + ")", on);
        Assert.EndsWith(", {\"pos\",\"st\"})\n)", on);
        Assert.Equal(outer, Guard_(on, false));

        var debugged = Debug(on, true);
        Assert.Equal(outer, Guard_(Debug(debugged, false), false));
    }

    [Fact]
    public void ANamedStep_GetsTheGuardInItsDefinition()
    {
        var engine = Read(Plain);
        var named = Apply(engine, new StateEngineChange(engine.Rows, engine.Fold, "WalkStep"));
        Assert.Null(named.Error);
        Names["WalkStep"] = named.Definition!;
        try
        {
            var cell = Read(named.Formula!);
            var on = Apply(cell, new StateEngineChange(cell.Rows, cell.Fold, cell.StepName, Guard: true));
            Assert.Null(on.Error);
            Assert.Contains("IF(GetVar(a, \"st\") <> \"go\", a, SetVars(", on.Definition);
            Assert.Equal(named.Formula, on.Formula);

            Names["WalkStep"] = on.Definition!;
            Assert.True(Read(named.Formula!).IsGuarded);
        }
        finally
        {
            Names.Remove("WalkStep");
        }
    }

    [Fact]
    public void TheUnfoldedLetStep_TakesTheGuardToo()
    {
        var engine = Read(Plain);
        var unfolded = Apply(engine, new StateEngineChange(engine.Rows, null, null));
        Assert.Null(unfolded.Error);

        var on = Guard_(unfolded.Formula!, true);
        Assert.Contains("IF(GetVar(a, \"st\") <> \"go\", a, SetVars(", on);
        Assert.True(Read(on).IsGuarded);
        Assert.False(Read(Guard_(on, false)).IsGuarded);
    }

    [Fact]
    public void ANewEngine_CanStartGuarded()
    {
        var engine = StateEngine.New("");
        var rows = new[] { new StateEditRow(null, "pos", ""), new StateEditRow(null, "", "") };
        var result = Apply(engine, new StateEngineChange(rows, StateFoldKind.Reduce, null, Guard: true));

        Assert.Null(result.Error);
        Assert.StartsWith("=REDUCE(SetVars(\"pos\", \"\", \"st\", \"go\"), SEQUENCE(1), LAMBDA(a, v, LET(", result.Formula);
        Assert.Matches(@"IF\(GetVar\(a, ""st""\) <> ""go"", a, SetVars\(""pos"", nextPos, ""st"", \w+\)\)", result.Formula);
        Assert.True(Read(result.Formula!).IsGuarded);
    }

    [Fact]
    public void ANewEngine_WithNoVariableTyped_StillAsksForOne()
    {
        var result = Apply(StateEngine.New(""), new StateEngineChange(new[] { new StateEditRow(null, "", "") }, StateFoldKind.Reduce, null, Guard: true));

        Assert.Equal("Type a variable name.", result.Error);
    }
}
