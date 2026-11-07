using Xunit;

namespace LambdaBoss.Tests;

public class StateScaffoldTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static string Formula(string state, string input = "H270", string prefix = "")
    {
        var result = StateScaffold.Build(state, input, prefix);
        Assert.Null(result.Error);
        return result.Formula!;
    }

    [Fact]
    public void Build_WritesTheSpecOutputExactly()
    {
        Assert.Equal(Lines(
            "=LET(",
            "    a, SetVars(\"p\", 0, \"b\", 4),",
            "    v, H270,",
            "    p, GetVar(a, \"p\"),",
            "    b, GetVar(a, \"b\"),",
            "    nextP, p,",
            "    nextB, b,",
            "    SetVars(\"p\", nextP, \"b\", nextB)",
            ")"), Formula("p 0, b 4"));
    }

    [Theory]
    [InlineData("p=0, b=4")]
    [InlineData("p = 0, b = 4")]
    [InlineData("p 0,b 4,")]
    [InlineData("  p   0 ,  b  4  ")]
    public void Build_AcceptsEitherPairForm(string state)
    {
        Assert.Equal(Formula("p 0, b 4"), Formula(state));
    }

    [Fact]
    public void Build_TakesValuesVerbatim()
    {
        var formula = Formula("x \"N\", y H268+1, z {1;2}, w SUM(A1, B1), s \"a, b\"");

        Assert.Contains(
            "a, SetVars(\"x\", \"N\", \"y\", H268+1, \"z\", {1;2}, \"w\", SUM(A1, B1), \"s\", \"a, b\"),",
            formula);
    }

    [Fact]
    public void Build_BlankInputIsAnEmptyString()
    {
        Assert.Contains("    v, \"\",\n", Formula("p 0", "  "));
    }

    [Fact]
    public void Build_UsesThePrefixOnEveryLibraryCall()
    {
        Assert.Equal(Lines(
            "=LET(",
            "    a, loops.SetVars(\"p\", 0),",
            "    v, H270,",
            "    p, loops.GetVar(a, \"p\"),",
            "    nextP, p,",
            "    loops.SetVars(\"p\", nextP)",
            ")"), Formula("p 0", prefix: "loops."));
    }

    [Theory]
    [InlineData("c", "c_")]
    [InlineData("C", "C_")]
    [InlineData("r", "r_")]
    [InlineData("b4", "b4_")]
    [InlineData("x1", "x1_")]
    [InlineData("ab12", "ab12_")]
    [InlineData("R1C1", "R1C1_")]
    [InlineData("RC", "RC_")]
    [InlineData("R1C", "R1C_")]
    [InlineData("RC1", "RC1_")]
    [InlineData("TRUE", "TRUE_")]
    public void Build_SuffixesANameExcelRejectsAndKeepsTheKey(string key, string binding)
    {
        var formula = Formula($"{key} 0");

        Assert.Contains($"    {binding}, GetVar(a, \"{key}\"),\n", formula);
        Assert.Contains($"SetVars(\"{key}\", 0)", formula);
    }

    [Fact]
    public void Build_NextLineComesFromTheNameBeforeTheSuffix()
    {
        var formula = Formula("c 0");

        Assert.Contains("    nextC, c_,\n", formula);
        Assert.Contains("SetVars(\"c\", nextC)", formula);
    }

    [Theory]
    [InlineData("best", "nextBest")]
    [InlineData("p", "nextP")]
    [InlineData("Max", "nextMax")]
    public void Build_NextLineCapitalisesTheFirstLetter(string key, string next)
    {
        Assert.Contains($"    {next}, {key},\n", Formula($"{key} 0"));
    }

    [Fact]
    public void Build_NextLineThatClashesWithAStateNameTakesANumber()
    {
        var formula = Formula("p 0, nextP 1");

        Assert.Contains("    nextP, GetVar(a, \"nextP\"),\n", formula);
        Assert.Contains("    nextP2, p,\n", formula);
        Assert.Contains("    nextNextP, nextP,\n", formula);
        Assert.Contains("SetVars(\"p\", nextP2, \"nextP\", nextNextP)", formula);
    }

    [Fact]
    public void Build_ClashIsCaseInsensitive()
    {
        Assert.Contains("    nextP2, p,\n", Formula("p 0, NEXTP 1"));
    }

    [Theory]
    [InlineData("a", "a_")]
    [InlineData("v", "v_")]
    public void Build_AVariableNamedLikeTheStepsOwnBindingsIsSuffixed(string key, string binding)
    {
        Assert.Contains($"    {binding}, GetVar(a, \"{key}\"),\n", Formula($"{key} 0"));
    }

    [Fact]
    public void Build_TwoKeysThatLandOnOneBindingStayApart()
    {
        var formula = Formula("c 0, c_ 1");

        Assert.Contains("    c_, GetVar(a, \"c\"),\n", formula);
        Assert.Contains("    c__, GetVar(a, \"c_\"),\n", formula);
    }

    [Fact]
    public void Build_ADottedKeyGetsALetName()
    {
        // Excel refuses a dot in a LET name.
        var formula = Formula("a.b 0");

        Assert.Contains("    aB, GetVar(a, \"a.b\"),\n", formula);
        Assert.Contains("    nextAB, aB,\n", formula);
    }

    [Fact]
    public void Build_ChainsThroughSetVarPastTen()
    {
        var names = Enumerable.Range(1, 12).Select(i => "k" + (char)('a' + i - 1) + "x").ToList();
        var formula = Formula(string.Join(", ", names.Select(n => n + " 0")));

        var pairs = string.Join(", ", names.Take(10).Select(n => $"\"{n}\", 0"));
        Assert.Contains(
            $"    a, SetVar(SetVar(SetVars({pairs}), \"kkx\", 0), \"klx\", 0),\n", formula);

        var nextPairs = string.Join(", ", names.Take(10).Select(n => $"\"{n}\", next{char.ToUpper(n[0])}{n[1..]}"));
        Assert.Contains(
            $"    SetVar(SetVar(SetVars({nextPairs}), \"kkx\", nextKkx), \"klx\", nextKlx)\n", formula);
    }

    [Fact]
    public void Build_TenVariablesDoNotChain()
    {
        var names = Enumerable.Range(0, 10).Select(i => "k" + (char)('a' + i) + "x");

        Assert.DoesNotContain("SetVar(", Formula(string.Join(", ", names.Select(n => n + " 0"))));
    }

    [Theory]
    [InlineData("", "Type the state")]
    [InlineData("   ", "Type the state")]
    [InlineData("p", "p needs a value")]
    [InlineData("p 0, p 1", "p is listed twice")]
    [InlineData("p 0, P 1", "P is listed twice")]
    [InlineData("\"p\" 0", "can't be a state name")]
    [InlineData("p \"open", "Can't read the state")]
    [InlineData("p (1", "Can't read the value of p")]
    public void Build_SaysWhatIsWrong(string state, string message)
    {
        var result = StateScaffold.Build(state, "H270", "");

        Assert.Null(result.Formula);
        Assert.Contains(message, result.Error);
    }

    [Fact]
    public void Build_RefusesAnInputItCantRead()
    {
        var result = StateScaffold.Build("p 0", "SUM(", "");

        Assert.Null(result.Formula);
        Assert.Contains("Can't read the input", result.Error);
    }

    [Fact]
    public void PrefillState_NamesFromTheTopRowValuesAsReferences()
    {
        Assert.Equal("p H268, b I268",
            StateScaffold.PrefillState(new object?[] { "p", "b" }, new[] { "H268", "I268" }));
    }

    [Fact]
    public void PrefillState_SanitisesLabels()
    {
        Assert.Equal("batteryLevel H268",
            StateScaffold.PrefillState(new object?[] { "Battery Level" }, new[] { "H268" }));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(null)]
    [InlineData("---")]
    [InlineData("")]
    public void PrefillState_NeedsTextInEveryLabel(object? label)
    {
        Assert.Null(StateScaffold.PrefillState(new[] { "p", label }, new[] { "H268", "I268" }));
    }

    [Fact]
    public void PrefilledStateBuildsTheSpecStep()
    {
        var state = StateScaffold.PrefillState(new object?[] { "p", "b" }, new[] { "H268", "I268" });

        Assert.Contains("    a, SetVars(\"p\", H268, \"b\", I268),\n", Formula(state!));
    }

    private static Dictionary<string, string> Names(params string[] names) =>
        names.ToDictionary(n => n, _ => "=LAMBDA(x, x)", StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void FindPrefix_PrefersBareNames()
    {
        Assert.Equal("", StateScaffold.FindPrefix(Names(
            "SetVars", "SetVar", "GetVar", "loops.SetVars", "loops.SetVar", "loops.GetVar")));
    }

    [Fact]
    public void FindPrefix_FindsAPrefix()
    {
        Assert.Equal("my.", StateScaffold.FindPrefix(Names("my.SetVars", "my.SetVar", "my.GetVar", "other")));
    }

    [Fact]
    public void FindPrefix_KeepsThePrefixAsWritten()
    {
        Assert.Equal("Loops.", StateScaffold.FindPrefix(Names("Loops.SetVars", "Loops.SetVar", "Loops.GetVar")));
    }

    [Theory]
    [InlineData("SetVars", "GetVar")]
    [InlineData("my.SetVars", "my.GetVar")]
    [InlineData("my.SetVars", "your.SetVar")]
    public void FindPrefix_NullWhenAnyIsMissing(params string[] names)
    {
        Assert.Null(StateScaffold.FindPrefix(Names(names)));
    }

    [Fact]
    public void FindPrefix_IgnoresNamesThatArentLambdas()
    {
        var names = Names("SetVars", "SetVar", "GetVar");
        names["GetVar"] = "=Sheet1!$A$1";

        Assert.Null(StateScaffold.FindPrefix(names));
    }

    [Fact]
    public void FindPrefix_IgnoresSheetScopedNames()
    {
        Assert.Null(StateScaffold.FindPrefix(Names(
            "Sheet1!my.SetVars", "Sheet1!my.SetVar", "Sheet1!my.GetVar")));
    }

    [Theory]
    [InlineData(1, 1, "A1")]
    [InlineData(268, 8, "H268")]
    [InlineData(5, 26, "Z5")]
    [InlineData(5, 27, "AA5")]
    [InlineData(5, 702, "ZZ5")]
    [InlineData(5, 703, "AAA5")]
    [InlineData(1048576, 16384, "XFD1048576")]
    public void Address_WritesA1(int row, int column, string expected)
    {
        Assert.Equal(expected, StateScaffold.Address(row, column));
    }

    [Theory]
    [InlineData("$h$270", "H270")]
    [InlineData(" H270 ", "H270")]
    public void NormalAddress_ComparesAcrossForms(string input, string expected)
    {
        Assert.Equal(expected, StateScaffold.NormalAddress(input));
    }

    // Round trip: what /State writes is a state step LET to LAMBDA understands.

    private static string RoundTrip(string formula, string name)
    {
        var parsed = LetParser.Parse(formula);
        var step = StateStepParser.TryParse(parsed);
        Assert.NotNull(step);

        var inputs = StateStepParser.DialogBindings(parsed, step!)
            .Select(b => new InputChoice(
                b.Name, b.Name,
                string.Equals(b.Name, step!.StateBinding.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(b.Name, step.InputBinding?.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var roles = new StateStepRoles(step!.StateBinding.Name, step.InputBinding?.Name);
        return LetToLambdaBuilder.Build(new LambdaGenerationRequest(name, parsed, inputs, roles));
    }

    [Theory]
    [InlineData("p 0, b 4", "H270")]
    [InlineData("p H268, b I268", "H270")]
    [InlineData("p 0, b 4", "")]
    public void RoundTrip_GivesTheStepLambda(string state, string input)
    {
        Assert.Equal(Lines(
            "=LAMBDA(",
            "    a,",
            "    v,",
            "    LET(",
            "        p, GetVar(a, \"p\"),",
            "        b, GetVar(a, \"b\"),",
            "        nextP, p,",
            "        nextB, b,",
            "        SetVars(\"p\", nextP, \"b\", nextB)",
            "    )",
            ")"), RoundTrip(Formula(state, input), "moveRobot"));
    }

    [Fact]
    public void RoundTrip_SeedComesFromTheScaffold()
    {
        var parsed = LetParser.Parse(Formula("p H268, b I268", prefix: "loops."));
        var step = StateStepParser.TryParse(parsed);

        Assert.Equal("loops.SetVars(\"p\", $H$268, \"b\", $I$268)", StateFold.Seed(parsed, step!));
    }

    [Fact]
    public void RoundTrip_FindsEveryVariablePastTen()
    {
        var names = Enumerable.Range(0, 12).Select(i => "k" + (char)('a' + i) + "x").ToList();
        var parsed = LetParser.Parse(Formula(string.Join(", ", names.Select(n => n + " 0"))));

        var step = StateStepParser.TryParse(parsed);

        Assert.NotNull(step);
        Assert.Equal(names, step!.Variables.Select(v => v.Key));
        Assert.All(step.Variables, v => Assert.NotNull(v.NextLine));
    }

    [Fact]
    public void RoundTrip_SuffixedAndPrefixedNames()
    {
        var parsed = LetParser.Parse(Formula("c 0, v 1", prefix: "loops."));

        var step = StateStepParser.TryParse(parsed);

        Assert.NotNull(step);
        Assert.Equal("a", step!.StateBinding.Name);
        Assert.Equal("v", step.InputBinding!.Name);
        Assert.Equal(new[] { "c_", "v_" }, step.Variables.Select(x => x.BindingName));
    }
}
