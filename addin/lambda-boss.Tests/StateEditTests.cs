using Xunit;

namespace LambdaBoss.Tests;

public class StateEditTests
{
    private static readonly string Robot = Lines(
        "=LET(",
        "    a, SetVars(\"p\", 0, \"b\", 4),",
        "    v, H270,",
        "    p, GetVar(a, \"p\"),",
        "    b, GetVar(a, \"b\"),",
        "    nextP, p + IF(b > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)),",
        "    nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)),",
        "    SetVars(\"p\", nextP, \"b\", nextB)",
        ")");

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static IReadOnlyList<StateEditRow> Rows(string formula) => StateEdit.Read(formula)!.Rows;

    private static StateEditResult Edit(string formula, params (string Old, string New)[] renames)
    {
        var rows = Rows(formula)
            .Select(r =>
            {
                var i = Array.FindIndex(renames, x => x.Old == r.Key);
                return i < 0 ? r : r with { Key = renames[i].New };
            })
            .ToList();
        return StateEdit.Apply(formula, rows);
    }

    private static StateEditResult Add(string formula, string key, string value) =>
        StateEdit.Apply(formula, Rows(formula).Append(new StateEditRow(null, key, value)).ToList());

    private static string Ok(StateEditResult result)
    {
        Assert.Null(result.Error);
        Assert.NotNull(result.Formula);
        return result.Formula!;
    }

    private static string Scaffolded(string state, string input = "H270") =>
        StateScaffold.Build(state, input, "").Formula!;

    [Fact]
    public void Read_ListsVariablesAndSampleValues()
    {
        var rows = Rows(Robot);

        Assert.Equal(new[] { "p", "b" }, rows.Select(r => r.Key));
        Assert.Equal(new[] { "0", "4" }, rows.Select(r => r.Value));
        Assert.All(rows, r => Assert.Equal(r.Key, r.OriginalKey));
    }

    [Theory]
    [InlineData("=SUM(A1:A3)")]
    [InlineData("=LET(x, 1, x + 1)")]
    [InlineData("")]
    [InlineData(null)]
    public void Read_IsNullWhenNotAStep(string? formula)
    {
        Assert.Null(StateEdit.Read(formula));
    }

    [Fact]
    public void Read_IsNullWhenTheLetIsOnlyPartOfTheFormula()
    {
        Assert.Null(StateEdit.Read(Robot + "&\"x\""));
    }

    [Fact]
    public void Read_RefusesAnArrayStep()
    {
        var view = StateEdit.Read(
            "=LET(a, HSTACK(0, 4), v, H1, p, INDEX(a, 1), b, INDEX(a, 2), nextP, p + v, nextB, b - v, HSTACK(nextP, nextB))");

        Assert.NotNull(view!.Refusal);
    }

    [Fact]
    public void Read_RefusesASetVarChainOnTheState()
    {
        var view = StateEdit.Read(
            "=LET(a, SetVars(\"p\", 0), v, H1, p, GetVar(a, \"p\"), nextP, p + v, SetVar(a, \"p\", nextP))");

        Assert.NotNull(view!.Refusal);
    }

    [Fact]
    public void NoChange_GivesTheFormulaBack()
    {
        Assert.Equal(Robot, Ok(StateEdit.Apply(Robot, Rows(Robot))));
    }

    [Fact]
    public void Add_WritesTheSpecOutputExactly()
    {
        Assert.Equal(Lines(
            "=LET(",
            "    a, SetVars(\"p\", 0, \"b\", 4, \"c\", 0),",
            "    v, H270,",
            "    p, GetVar(a, \"p\"),",
            "    b, GetVar(a, \"b\"),",
            "    c_, GetVar(a, \"c\"),",
            "    nextP, p,",
            "    nextB, b,",
            "    nextC, c_,",
            "    SetVars(\"p\", nextP, \"b\", nextB, \"c\", nextC)",
            ")"), Ok(Add(Scaffolded("p 0, b 4"), "c", "0")));
    }

    [Fact]
    public void Add_KeepsTheRulesAndTheNewLinesAfterTheirKind()
    {
        var edited = Ok(Add(Robot, "n", "0"));

        Assert.Contains("    nextB, MEDIAN(0, 6, IF(v = \"C\", b + 2, b - 1)),\n    nextN, n,\n", edited);
        Assert.Contains("    b, GetVar(a, \"b\"),\n    n, GetVar(a, \"n\"),\n", edited);
        Assert.Contains("SetVars(\"p\", nextP, \"b\", nextB, \"n\", nextN)", edited);
    }

    [Fact]
    public void Add_UsesTheStepsPrefix()
    {
        var edited = Ok(Add(StateScaffold.Build("p 0", "H1", "loops.").Formula!, "n", "0"));

        Assert.Contains("n, loops.GetVar(a, \"n\"),", edited);
        Assert.Contains("a, loops.SetVars(\"p\", 0, \"n\", 0),", edited);
        Assert.Contains("loops.SetVars(\"p\", nextP, \"n\", nextN)\n", edited);
    }

    [Fact]
    public void Add_LayersOnASampleStateHeldInACell()
    {
        var step = "=LET(a, I266, v, H270, p, GetVar(a, \"p\"), nextP, p + 1, SetVars(\"p\", nextP))";

        Assert.Contains("    a, SetVar(I266, \"c\", 0),\n", Ok(Add(step, "c", "0")));
    }

    [Fact]
    public void Add_LayersOnTheStepperSample()
    {
        const string sample = "IF(k = 1, SetVars(\"p\", 0), INDEX(SCAN(SetVars(\"p\", 0), G1:G9, f), k - 1))";
        var step = $"=LET(k, 1, a, {sample}, v, INDEX(G1:G9, k), p, GetVar(a, \"p\"), nextP, p + v, SetVars(\"p\", nextP))";

        Assert.Contains($"a, SetVar({sample}, \"n\", 0),", Ok(Add(step, "n", "0")));
    }

    [Fact]
    public void Add_ChainsAnEleventhVariable()
    {
        var ten = string.Join(", ", Enumerable.Range(1, 10).Select(i => $"k{i}x 0"));
        var edited = Ok(Add(Scaffolded(ten), "n", "5"));

        Assert.Contains("a, SetVar(SetVars(", edited);
        Assert.Contains(", \"n\", 5),", edited);
        Assert.Contains("    SetVar(SetVars(", edited);
        Assert.EndsWith(", \"n\", nextN)\n)", edited);
        Assert.Equal(11, StateEdit.Read(edited)!.Rows.Count);
    }

    [Fact]
    public void Add_PutsTheNextLineBelowALateGetVarLine()
    {
        var step = "=LET(a, SetVars(\"p\", 0, \"q\", 0), v, H1, p, GetVar(a, \"p\"), nextP, p + v, q, GetVar(a, \"q\"), SetVars(\"p\", nextP, \"q\", q + 1))";
        var edited = Ok(Add(step, "n", "0"));

        Assert.True(edited.IndexOf("n, GetVar", StringComparison.Ordinal) < edited.IndexOf("nextN, n", StringComparison.Ordinal));
    }

    [Fact]
    public void Add_DoesNotCaptureANameAlreadyInTheStep()
    {
        var step = "=LET(a, SetVars(\"p\", 0), v, H1, maxB, $K$2, p, GetVar(a, \"p\"), nextP, MIN(p + v, maxB), SetVars(\"p\", nextP))";

        Assert.Contains("maxB_, GetVar(a, \"maxB\"),", Ok(Add(step, "maxB", "0")));
    }

    [Theory]
    [InlineData("q", "", "q needs a value.")]
    [InlineData("", "3", "Name the new variable with the value 3.")]
    [InlineData("p", "0", "p is listed twice.")]
    [InlineData("a b", "0", "'a b' can't be a state name.")]
    public void Add_RefusesABadRow(string key, string value, string error)
    {
        var result = Add(Robot, key, value);

        Assert.Null(result.Formula);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Remove_DeletesTheLinesAndPairs()
    {
        var step = Scaffolded("p 0, b 4");

        Assert.Equal(Scaffolded("p 0"), Ok(Edit(step, ("b", ""))));
    }

    [Fact]
    public void Remove_ListsTheLinesThatStillReadIt()
    {
        var result = Edit(Robot, ("b", ""));

        Assert.Null(result.Formula);
        Assert.Contains("nextP reads b", result.Blockers);
    }

    [Fact]
    public void Remove_AllowsReadersThatAreLeavingToo()
    {
        var step = "=LET(a, SetVars(\"p\", 0, \"b\", 4, \"c\", 1), p, GetVar(a, \"p\"), b, GetVar(a, \"b\"), c_, GetVar(a, \"c\"), nextP, p, nextB, b + c_, nextC, c_, SetVars(\"p\", nextP, \"b\", nextB, \"c\", nextC))";

        Assert.Single(StateEdit.Read(Ok(Edit(step, ("b", ""), ("c", ""))))!.Rows);
    }

    [Fact]
    public void Remove_BlocksOnAnInlineRead()
    {
        var step = "=LET(a, SetVars(\"p\", 0, \"b\", 4), p, GetVar(a, \"p\"), b, GetVar(a, \"b\"), nextP, p + GetVar(a, \"b\"), nextB, b, SetVars(\"p\", nextP, \"b\", nextB))";

        Assert.Contains("nextP reads \"b\"", Edit(step, ("b", "")).Blockers);
    }

    [Fact]
    public void Remove_BlocksOnAReadInTheOutput()
    {
        var step = "=LET(a, SetVars(\"p\", 0, \"b\", 4), p, GetVar(a, \"p\"), b, GetVar(a, \"b\"), nextB, b, SetVars(\"p\", p + nextB, \"b\", nextB))";

        Assert.Contains("the output's \"p\" reads nextB", Edit(step, ("b", "")).Blockers);
    }

    [Fact]
    public void Remove_KeepsTheInputWhenTheOutputStoredIt()
    {
        var step = "=LET(a, SetVars(\"p\", 0, \"last\", 0), inp, H1, p, GetVar(a, \"p\"), nextP, p + inp, SetVars(\"p\", nextP, \"last\", inp))";
        var edited = Ok(Edit(step, ("last", "")));

        Assert.Contains("inp, H1,", edited);
        Assert.Contains("    SetVars(\"p\", nextP)\n", edited);
    }

    [Fact]
    public void Remove_KeepsANextLineAnotherKeyStillPasses()
    {
        var step = "=LET(a, SetVars(\"p\", 0, \"b\", 4), p, GetVar(a, \"p\"), b, GetVar(a, \"b\"), x, p + 1, SetVars(\"p\", x, \"b\", x))";
        var edited = Ok(Edit(step, ("b", "")));

        Assert.Contains("x, p + 1,", edited);
        Assert.Contains("SetVars(\"p\", x)", edited);
    }

    [Fact]
    public void Remove_RefusesTheLastVariable()
    {
        var result = Edit(Scaffolded("p 0"), ("p", ""));

        Assert.Equal("A state needs at least one variable.", result.Error);
    }

    [Fact]
    public void Remove_MovesAChainedVariableBackIntoSetVars()
    {
        var eleven = string.Join(", ", Enumerable.Range(1, 11).Select(i => $"k{i}x 0"));
        var edited = Ok(Edit(Scaffolded(eleven), ("k1x", "")));

        Assert.DoesNotContain("SetVar(", edited);
        Assert.Contains("\"k11x\", nextK11x)", edited);
    }

    [Fact]
    public void Rename_FollowsTheSpec()
    {
        var edited = Ok(Edit(Robot, ("b", "bat")));

        Assert.Equal(Lines(
            "=LET(",
            "    a, SetVars(\"p\", 0, \"bat\", 4),",
            "    v, H270,",
            "    p, GetVar(a, \"p\"),",
            "    bat, GetVar(a, \"bat\"),",
            "    nextP, p + IF(bat > 0, SWITCH(v, \"F\", 1, \"B\", -1, 0)),",
            "    nextBat, MEDIAN(0, 6, IF(v = \"C\", bat + 2, bat - 1)),",
            "    SetVars(\"p\", nextP, \"bat\", nextBat)",
            ")"), edited);
    }

    [Fact]
    public void Rename_LeavesOtherStringsAndNearbyReferencesAlone()
    {
        var step = Lines(
            "=LET(",
            "    a, SetVars(\"b\", 4),",
            "    v, H270,",
            "    b, GetVar(a, \"b\"),",
            "    nextB, b + $B$2 + B2 + Sheet1!B2 + t[b] + SWITCH(v, \"b\", 1, 0),",
            "    SetVars(\"b\", nextB)",
            ")");
        var edited = Ok(Edit(step, ("b", "bat")));

        Assert.Contains("nextBat, bat + $B$2 + B2 + Sheet1!B2 + t[b] + SWITCH(v, \"b\", 1, 0),", edited);
    }

    [Fact]
    public void Rename_ToANameExcelRejects_SuffixesTheBindingOnly()
    {
        var edited = Ok(Edit(Robot, ("b", "c")));

        Assert.Contains("c_, GetVar(a, \"c\"),", edited);
        Assert.Contains("nextC, MEDIAN(0, 6, IF(v = \"C\", c_ + 2, c_ - 1)),", edited);
        Assert.Contains("SetVars(\"p\", nextP, \"c\", nextC)", edited);
    }

    [Theory]
    [InlineData("p", "p is listed twice.")]
    [InlineData("P", "P is listed twice.")]
    [InlineData("nextP", "nextP is already used in this step.")]
    [InlineData("H270", null)]
    public void Rename_BlocksAClash(string newName, string? error)
    {
        var result = Edit(Robot, ("b", newName));

        if (error != null)
        {
            Assert.Null(result.Formula);
            Assert.Equal(error, result.Error);
        }
        else
        {
            // H270 reads as a cell, so its binding is suffixed and can't capture the input.
            Assert.Contains("H270_, GetVar(a, \"H270\"),", Ok(result));
            Assert.Contains("v, H270,", result.Formula);
        }
    }

    [Fact]
    public void Rename_BlocksANameAnInnerLetBinds()
    {
        var step = "=LET(a, SetVars(\"b\", 4), v, H1, b, GetVar(a, \"b\"), nextB, LET(x, 2, x + b + v), SetVars(\"b\", nextB))";

        Assert.Equal("x is already used in this step.", Edit(step, ("b", "x")).Error);
    }

    [Fact]
    public void Rename_CaseOnly_ChangesTheKeyAndBinding()
    {
        var edited = Ok(Edit(Robot, ("b", "B")));

        Assert.Contains("B, GetVar(a, \"B\"),", edited);
        Assert.Contains("SetVars(\"p\", nextP, \"B\", nextB)", edited);
    }

    [Fact]
    public void Rename_ReachesNonCanonicalReads()
    {
        var step = "=LET(a, SetVars(\"b\", 4), v, H1, x, GetVar(a, \"b\") + 0, nextB, x + v, SetVars(\"b\", nextB))";
        var edited = Ok(Edit(step, ("b", "bat")));

        Assert.Contains("x, GetVar(a, \"bat\") + 0,", edited);
    }

    [Fact]
    public void Rename_LeavesAnUnconventionalNextLineItsName()
    {
        var step = "=LET(a, SetVars(\"b\", 4), v, H1, b, GetVar(a, \"b\"), np, b + v, SetVars(\"b\", np))";
        var edited = Ok(Edit(step, ("b", "bat")));

        Assert.Contains("np, bat + v,", edited);
        Assert.Contains("SetVars(\"bat\", np)", edited);
    }

    [Fact]
    public void Rename_CarriesTheValueAcrossForASampleStateHeldInACell()
    {
        var step = "=LET(a, I266, v, H270, b, GetVar(a, \"b\"), nextB, b + v, SetVars(\"b\", nextB))";

        Assert.Contains("a, SetVar(I266, \"bat\", GetVar(I266, \"b\")),", Ok(Edit(step, ("b", "bat"))));
    }

    [Fact]
    public void Rename_ReachesReadsInTheOutputsExpressions()
    {
        var step = "=LET(a, SetVars(\"p\", 0, \"b\", 4), v, H1, p, GetVar(a, \"p\"), nextP, p + v, SetVars(\"p\", nextP, \"b\", GetVar(a, \"b\") + 1))";
        var edited = Ok(Edit(step, ("b", "bat")));

        Assert.Contains("SetVars(\"p\", nextP, \"bat\", GetVar(a, \"bat\") + 1)", edited);
    }

    [Theory]
    [InlineData("s2, SetVar(a, \"p\", v), nextP, GetVar(s2, \"b\")")]
    [InlineData("nextP, GetVar(SetVar(a, \"p\", v), \"b\")")]
    [InlineData("nextP, REDUCE(a, v, LAMBDA(s, i, SetVar(s, \"b\", i)))")]
    public void AKeyUsedThroughAnotherName_StopsARenameAndARemove(string lines)
    {
        var step = $"=LET(a, SetVars(\"p\", 0, \"b\", 4), v, H1, p, GetVar(a, \"p\"), b, GetVar(a, \"b\"), {lines}, nextB, b, SetVars(\"p\", nextP, \"b\", nextB))";

        Assert.Contains("through another name", Edit(step, ("b", "bat")).Error);
        Assert.Contains("nextP uses the key through another name", Edit(step, ("b", "")).Blockers);
    }

    [Fact]
    public void Add_RefusesAValueThatReadsALineBelowTheSample()
    {
        Assert.Contains("can't read v", Add(Robot, "n", "v + 1").Error);
        Assert.Contains("can't read p", Add(Robot, "n", "p").Error);
    }

    [Fact]
    public void Add_TakesAValueThatReadsALineAboveTheSample()
    {
        var step = "=LET(k, 1, a, SetVars(\"p\", k), v, H1, p, GetVar(a, \"p\"), nextP, p + v, SetVars(\"p\", nextP))";

        Assert.Contains("a, SetVars(\"p\", k, \"n\", k),", Ok(Add(step, "n", "k")));
    }

    [Fact]
    public void Rename_BlocksAKeyOnlyTheSampleHolds()
    {
        var step = "=LET(a, SetVars(\"p\", 0, \"z\", 9), v, H1, p, GetVar(a, \"p\"), nextP, p + v, SetVars(\"p\", nextP))";

        Assert.Equal("z is already in the sample state.", Edit(step, ("p", "z")).Error);
    }

    [Fact]
    public void Seed_StaysEmptyForAnEditedStepper_AndFillsForALayeredCell()
    {
        const string sample = "IF(k = 1, SetVars(\"p\", 0), INDEX(SCAN(SetVars(\"p\", 0), G1:G9, f), k - 1))";
        var stepper = Ok(Add(
            $"=LET(k, 3, a, {sample}, v, INDEX(G1:G9, k), p, GetVar(a, \"p\"), nextP, p + v, SetVars(\"p\", nextP))",
            "n",
            "0"));
        var parsedStepper = LetParser.Parse(stepper);
        Assert.Equal("", StateFold.Seed(parsedStepper, StateStepParser.TryParse(parsedStepper)!));

        var cell = Ok(Add("=LET(a, I266, v, H270, p, GetVar(a, \"p\"), nextP, p + 1, SetVars(\"p\", nextP))", "c", "0"));
        var parsedCell = LetParser.Parse(cell);
        Assert.Equal("SetVar($I$266, \"c\", 0)", StateFold.Seed(parsedCell, StateStepParser.TryParse(parsedCell)!));
    }

    [Fact]
    public void Preserved_OtherBindingsKeepTextAndOrder()
    {
        var step = Lines(
            "=LET(",
            "    _note, \"battery caps at 6\",",
            "    maxB, $K$2,",
            "    a, SetVars(\"p\", 0, \"b\", 4),",
            "    v, H270,",
            "    p, GetVar(a, \"p\"),",
            "    b, GetVar(a, \"b\"),",
            "    nextP, LET(x, p + 1, x * 2),",
            "    nextB, MIN(b, maxB),",
            "    SetVars(\"p\", nextP, \"b\", nextB)",
            ")");
        var edited = Ok(Add(step, "n", "0"));

        Assert.StartsWith("=LET(\n    _note, \"battery caps at 6\",\n    maxB, $K$2,\n", edited);
        Assert.Contains("nextP, LET(x, p + 1, x * 2),", edited);
    }

    [Fact]
    public void Several_RemoveThenRenameThenAddOnOneSave()
    {
        var step = Scaffolded("p 0, b 4, q 1");
        var rows = Rows(step).Select(r => r.Key switch
        {
            "b" => r with { Key = "" },
            "q" => r with { Key = "b" },
            _ => r
        }).Append(new StateEditRow(null, "n", "0")).ToList();

        var edited = Ok(StateEdit.Apply(step, rows));

        Assert.Equal(new[] { "p", "b", "n" }, Rows(edited).Select(r => r.Key));
        Assert.Equal(new[] { "0", "1", "0" }, Rows(edited).Select(r => r.Value));
    }

    [Fact]
    public void RoundTrip_TheSteppersPrototypeTakesAVariable()
    {
        const string robot =
            "=LAMBDA(a, v, LET(p, GetVar(a, \"p\"), b, GetVar(a, \"b\"), nextP, p + 1, nextB, b - 1, SetVars(\"p\", nextP, \"b\", nextB)))";
        var prototype = StateStepper.Build(
            "=REDUCE(SetVars(\"p\", 0, \"b\", 4), EXPLODE(G270), moveRobot)",
            name => name == "moveRobot" ? robot : null)!.Formula!;

        var edited = Ok(Add(prototype, "n", "0"));
        var step = StateStepParser.TryParse(LetParser.Parse(edited));

        Assert.Contains("k, 1,", edited);
        Assert.Contains("a, SetVar(IF(k = 1,", edited);
        Assert.Equal(new[] { "p", "b", "n" }, step!.Variables.Select(v => v.Key));
        Assert.Equal("v", step.InputBinding!.Name);
    }

    [Fact]
    public void RoundTrip_ScaffoldAddThenLetToLambdaAgree()
    {
        var edited = Ok(Add(Scaffolded("p H268, b I268"), "n", "0"));
        var parsed = LetParser.Parse(edited);
        var step = StateStepParser.TryParse(parsed);

        Assert.NotNull(step);
        Assert.Equal("a", step!.StateBinding.Name);
        Assert.Equal("v", step.InputBinding!.Name);
        Assert.Equal(new[] { "p", "b", "n" }, step.Variables.Select(v => v.Key));
        Assert.Equal("n", step.Variables[2].BindingName);
        Assert.Equal("nextN", step.Variables[2].NextLine!.Name);
        Assert.Equal("SetVars(\"p\", $H$268, \"b\", $I$268, \"n\", 0)", StateFold.Seed(parsed, step));
    }
}
