using System.Diagnostics;

using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Check Formula (spec 0014). The "Stall" cases are Tim's real stalls from his recordings
///     (excel-esports analysis/formula-stalls.md), as recreated in the VM.
/// </summary>
public class FormulaCheckerTests
{
    private static FormulaNameContext Workbook()
    {
        var ctx = new FormulaNameContext();
        foreach (var n in new[] { "fareIds", "fares", "types", "MOVECELL", "GRIDLOOKUP", "PAIROP", "Sheet1!localName" })
            ctx.DefinedNames.Add(n);
        ctx.Sheets.Add("Sheet1");
        ctx.Sheets.Add("Board");
        ctx.Sheets.Add("My Sheet");
        ctx.Tables["tblFares"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "From", "To", "Fare", "Odd [x] col" };
        foreach (var n in new[] { "POSTOCELL", "TOPOS", "TLOOKUP", "lookup.TLOOKUP", "EXPLODE", "NTH" })
            ctx.LibraryNames.Add(n);
        return ctx;
    }

    private static IReadOnlyList<FormulaProblem> Errors(string formula, FormulaNameContext? ctx = null) =>
        FormulaChecker.Check(formula, ctx ?? Workbook()).Problems.Where(p => !p.IsNote).ToList();

    private static string At(string formula, FormulaProblem p) => formula.Substring(p.Start, p.Length);

    // ------------------------------------------------------------ clean formulas

    [Theory]
    [InlineData("=SUM(A1:B2)")]
    [InlineData("=SUM(A:A)+SUM($B:$C)+SUM(1:1)")]
    [InlineData("=LET(a,1,b,a+1,a*b)")]
    [InlineData("=LET(f,LAMBDA(x,y,x+y),f(1,2))")]
    [InlineData("=REDUCE(0,SEQUENCE(3),LAMBDA(acc,v,acc+v))")]
    [InlineData("=MAP(A1:A3,LAMBDA(x,LET(y,x*2,y)))")]
    [InlineData("=XLOOKUP(\"a\",fareIds,fares)")]
    [InlineData("=\"(unbalanced ) text\"&\"((\"")]
    [InlineData("=Sheet1!A1+'My Sheet'!B2+Board!$C$3:$C$62")]
    [InlineData("=Sheet1!localName")]
    [InlineData("=tblFares[Fare]+SUM(tblFares[[#Data],[From]:[To]])")]
    [InlineData("=tblFares[@Fare]")]
    [InlineData("=tblFares[Odd '[x'] col]")]
    [InlineData("=SUM(tblFares)")]
    [InlineData("=SEQUENCE(3)#")]
    [InlineData("=A1#+@B1:B3")]
    [InlineData("=BYROW(A1:B3,SUM)")]
    [InlineData("=_xlfn.LET(_xlpm.a,1,_xlpm.a)")]
    [InlineData("=IF(TRUE,{1,2;3,4},FALSE)")]
    [InlineData("=LAMBDA(x,x+1)(5)")]
    [InlineData("=LET(keyDropped?,TRUE,keyDropped?)")]
    [InlineData("=LOG10(100)+ATAN2(1,1)")]
    [InlineData("=[Book2.xlsx]Sheet1!A1+[@Fare]")]
    public void Clean_formulas_have_no_errors(string formula)
    {
        Assert.Empty(Errors(formula));
    }

    // ------------------------------------------------------------ brackets

    [Fact]
    public void Stall_extra_bracket_closes_LET_early()
    {
        // Roll Dice Roll, 50:41: 21 s lost. Excel: "You've entered too few arguments for this function."
        const string f = "=LET(start,K9,pos,MOVECELL(start,$E$3,SEQUENCE(30)),nonIce,GRIDLOOKUP(mp,pos,1)<>\"I\"),nonIce)";
        var errors = Errors(f);
        var misplaced = f.IndexOf("\"I\")", StringComparison.Ordinal) + 3;

        var first = errors[0];
        Assert.Equal(FormulaProblemKind.CommaOutsideFunction, first.Kind);
        Assert.Equal(misplaced, first.RelatedStart);
        Assert.Contains(errors, p => p.Kind == FormulaProblemKind.BadShape && p.RelatedStart == misplaced);
        Assert.Contains(errors, p => p.Kind == FormulaProblemKind.ExtraCloseBracket && p.Start == f.Length - 1);
        // mp is a genuine unknown name in the recreated formula.
        Assert.Contains(errors, p => p.Kind == FormulaProblemKind.UnknownName && At(f, p) == "mp");
    }

    [Fact]
    public void Stall_extra_bracket_at_end()
    {
        // R1 reps, 02:00. Excel offers "We found a typo… =SUMIFS(…)" and corrects it.
        const string f = "=SUMIFS(Deliveries!$D$3:$D$42,Deliveries!$C$3:$C$42,G58,Deliveries!$D$3:$D$42,\">=\"&H58))";
        var ctx = Workbook();
        ctx.Sheets.Add("Deliveries");
        var errors = Errors(f, ctx);
        var p = Assert.Single(errors);
        Assert.Equal(FormulaProblemKind.ExtraCloseBracket, p.Kind);
        Assert.Equal(f.Length - 1, p.Start);
        Assert.Equal(f.Length - 2, p.RelatedStart);
    }

    [Fact]
    public void Stall_stray_bracket_closes_REDUCE_early()
    {
        // R2 reps, 24:01. Excel: "There's a problem with this formula."
        const string f = "=REDUCE(VSTACK(0,1),TAKE(EXPLODE(G142),$I$140)),LAMBDA(a,v,a+v))";
        var ctx = Workbook();
        ctx.DefinedNames.Add("EXPLODE");
        var errors = Errors(f, ctx);
        var misplaced = f.IndexOf("$I$140))", StringComparison.Ordinal) + 7;
        Assert.Equal(FormulaProblemKind.CommaOutsideFunction, errors[0].Kind);
        Assert.Equal(misplaced, errors[0].RelatedStart);
        Assert.Contains("REDUCE(", errors[0].Message);
    }

    [Fact]
    public void Missing_bracket_points_at_the_open_call()
    {
        const string f = "=SUM(IF(A2:A6=\"x\",B2:B6)";
        var p = Assert.Single(Errors(f));
        Assert.Equal(FormulaProblemKind.UnclosedBracket, p.Kind);
        Assert.Equal("SUM(", At(f, p));
    }

    [Fact]
    public void Brackets_inside_strings_and_sheet_names_are_ignored()
    {
        Assert.Empty(Errors("=\")\"&'My Sheet'!A1&\"(\""));
    }

    [Theory]
    [InlineData("=\"abc", "\"")]
    [InlineData("=tblFares[Fare", "[")]
    [InlineData("='My Sheet!A1", "'")]
    [InlineData("={1,2", "{")]
    public void Unterminated_constructs_are_reported(string f, string at)
    {
        var p = Errors(f).First();
        Assert.Equal(FormulaProblemKind.Unterminated, p.Kind);
        Assert.Equal(at, At(f, p));
    }

    // ------------------------------------------------------------ names

    [Fact]
    public void Stall_unknown_name_suggests_the_defined_name()
    {
        // R1 reps, 23:46: 17 s lost. Cell shows #NAME?.
        const string f = "=PAIROP(TEXTSPLIT(G248,,\"-\"),LAMBDA(a,b,XLOOKUP(a&\"-\"&b,fareIds,fare)))";
        var p = Assert.Single(Errors(f));
        Assert.Equal(FormulaProblemKind.UnknownName, p.Kind);
        Assert.Equal("fare", At(f, p));
        Assert.Equal("fares", p.Suggestion);
    }

    [Fact]
    public void Stall_misspelled_LET_name_is_found_both_ways()
    {
        // R2 reps, 05:11: 24 s lost. Cell shows #NAME?.
        const string f = "=LET(rolls,{1,2,3},ends,SCAN(0,rolls,LAMBDA(pos,roll,pos+roll)),ouyt,SUM(ends),out)";
        var result = FormulaChecker.Check(f, Workbook());
        var p = Assert.Single(result.Problems, x => !x.IsNote);
        Assert.Equal("out", At(f, p));
        Assert.Equal("ouyt", p.Suggestion);
        var note = Assert.Single(result.Problems, x => x.IsNote);
        Assert.Equal("ouyt", At(f, note));
    }

    [Fact]
    public void Stall_typo_in_a_table_or_name()
    {
        // Oachi in Zurich, 06:04.
        const string f = "=XLOOKUP(G113,tyupe,fares)";
        var p = Assert.Single(Errors(f));
        Assert.Equal("types", p.Suggestion);
    }

    [Fact]
    public void Stall_two_function_names_run_together()
    {
        // Airplane Battleship, 24:44.
        const string f = "=LET(guess,G113,cells,TOCOPOSTOCELL(TOPOS(guess)),cells)";
        var ctx = Workbook();
        ctx.DefinedNames.Add("POSTOCELL");
        ctx.DefinedNames.Add("TOPOS");
        var p = Assert.Single(Errors(f, ctx));
        Assert.Equal("TOCOPOSTOCELL", At(f, p));
        Assert.Contains("POSTOCELL", p.Message);
        Assert.Equal("TOCOL(POSTOCELL", p.Suggestion);
    }

    [Fact]
    public void Misspelled_native_function_suggests_it()
    {
        const string f = "=XLOKUP(1,A1:A3,B1:B3)";
        var p = Assert.Single(Errors(f));
        Assert.Equal("XLOOKUP", p.Suggestion);
    }

    [Fact]
    public void Library_lambda_not_loaded_says_so()
    {
        const string f = "=TLOOKUP(A1:B3,\"x\")";
        var p = Assert.Single(Errors(f));
        Assert.Equal(FormulaProblemKind.NotLoaded, p.Kind);
        Assert.Equal("TLOOKUP", At(f, p));
    }

    [Fact]
    public void Prefixed_library_lambda_not_loaded_says_so()
    {
        var p = Assert.Single(Errors("=lookup.TLOOKUP(A1:B3,\"x\")"));
        Assert.Equal(FormulaProblemKind.NotLoaded, p.Kind);
    }

    [Fact]
    public void Lambda_parameter_used_outside_its_lambda_is_out_of_scope()
    {
        const string f = "=LET(g,LAMBDA(x,x*2),g(3)+x)";
        var p = Assert.Single(Errors(f));
        Assert.Equal("x", At(f, p));
        Assert.Contains("out of scope", p.Message);
    }

    [Fact]
    public void Nested_lambda_scopes_see_outer_names()
    {
        Assert.Empty(Errors("=LET(k,2,MAP(A1:A3,LAMBDA(x,REDUCE(0,SEQUENCE(x),LAMBDA(a,v,a+v*k+x)))))"));
    }

    [Fact]
    public void LET_name_used_before_it_is_defined_is_unknown()
    {
        const string f = "=LET(a,b+1,b,2,a)";
        var p = Assert.Single(Errors(f));
        Assert.Equal("b", At(f, p));
    }

    [Fact]
    public void Unknown_sheet_and_table_and_column_are_reported()
    {
        var sheet = Errors("=Shet1!A1").Single();
        Assert.Equal(FormulaProblemKind.UnknownReference, sheet.Kind);
        Assert.Equal("Sheet1", sheet.Suggestion);

        var table = Errors("=tblFare[Fare]").Single();
        Assert.Equal("tblFares", table.Suggestion);

        const string f = "=tblFares[[#Data],[Faer]]";
        var col = Errors(f).Single();
        Assert.Equal("Faer", At(f, col));
        Assert.Equal("Fare", col.Suggestion);
    }

    [Fact]
    public void Without_a_workbook_snapshot_sheets_and_tables_are_not_checked()
    {
        Assert.Empty(Errors("=Anything!A1+tblX[Col]", new FormulaNameContext()));
    }

    [Fact]
    public void LET_name_that_is_a_cell_reference_is_reported()
    {
        const string f = "=LET(x1,5,x1*2)";
        var p = Assert.Single(Errors(f), e => e.Kind == FormulaProblemKind.BadShape);
        Assert.Equal("x1", At(f, p));
    }

    [Fact]
    public void LET_with_a_calculation_in_a_name_slot_is_reported()
    {
        const string f = "=LET(a,1,SUM(a),2,a)";
        Assert.Contains(Errors(f), p => p.Kind == FormulaProblemKind.BadShape && At(f, p) == "SUM");
    }

    // ------------------------------------------------------------ helpers & limits

    [Theory]
    [InlineData("A1", true)]
    [InlineData("$XFD$1048576", true)]
    [InlineData("XFE1", false)]
    [InlineData("A1048577", false)]
    [InlineData("A0", false)]
    [InlineData("out", false)]
    public void Cell_references(string s, bool expected) =>
        Assert.Equal(expected, FormulaChecker.IsCellReference(s));

    [Fact]
    public void A_3000_character_LET_checks_quickly()
    {
        var parts = new List<string>();
        for (var i = 0; i < 120; i++)
            parts.Add($"step{i},IF(ISNUMBER(step{Math.Max(0, i - 1)}),step{Math.Max(0, i - 1)}+{i},0)");
        var f = "=LET(step0,1," + string.Join(",", parts.Skip(1)) + ",stepp119)";
        Assert.True(f.Length > 3000, $"length {f.Length}");

        var sw = Stopwatch.StartNew();
        var errors = Errors(f);
        sw.Stop();

        var p = Assert.Single(errors);
        Assert.Equal("step119", p.Suggestion);
        Assert.True(sw.ElapsedMilliseconds < 500, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Deep_nesting_does_not_overflow()
    {
        var f = "=" + new string('(', 5000) + "1" + new string(')', 4999);
        var p = Assert.Single(Errors(f));
        Assert.Equal(FormulaProblemKind.UnclosedBracket, p.Kind);
    }

    [Fact]
    public void Many_problems_are_capped()
    {
        var f = "=" + string.Join("+", Enumerable.Range(0, 100).Select(i => $"zz{i}q"));
        var result = FormulaChecker.Check(f, Workbook());
        Assert.Equal(FormulaChecker.MaxProblems, result.Problems.Count);
    }
}
