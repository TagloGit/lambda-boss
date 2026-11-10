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
    [InlineData("=LAMBDA([x],IF(ISOMITTED(x),0,x))(1)")]
    [InlineData("=LAMBDA(a,[b],a+IF(ISOMITTED(b),0,b))(1)")]
    [InlineData("=IFERROR(A1,#n/a)")]
    [InlineData("=SUM({1,2,3})+LEN(\"}\")")]
    [InlineData("=LET(a,1,LET(b,2,a+b))")]
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

    [Fact]
    public void LET_name_used_in_its_own_value_is_reported()
    {
        const string f = "=LET(x,x+1,x)";
        var p = Assert.Single(Errors(f));
        Assert.Equal(7, p.Start);
        Assert.Contains("own definition", p.Message);
    }

    [Theory]
    [InlineData("=LET(r,1,r)")]
    [InlineData("=LET(C,1,C)")]
    public void R_and_C_are_not_valid_LET_names(string f)
    {
        Assert.Contains(Errors(f), p => p.Kind == FormulaProblemKind.BadShape && p.Start == 5);
    }

    [Theory]
    [InlineData("=tblFares[ Faer]", "Faer")]
    [InlineData("=tblFares[Odd '[x'] coll]", "Odd '[x'] coll")]
    [InlineData("=tblFares[@Faer]", "Faer")]
    public void Column_spans_cover_the_column_as_written(string f, string expected)
    {
        var p = Assert.Single(Errors(f));
        Assert.Equal(expected, At(f, p));
    }

    [Fact]
    public void Unterminated_array_with_a_brace_in_a_string_is_reported()
    {
        const string f = "=SUM({1,2,3)+LEN(\"}\")";
        var p = Errors(f).First();
        Assert.Equal(FormulaProblemKind.Unterminated, p.Kind);
        Assert.Equal("{", At(f, p));
    }

    [Fact]
    public void Unterminated_string_does_not_also_report_the_bracket()
    {
        var p = Assert.Single(Errors("=IF(A1=\"x,1,2)"));
        Assert.Equal(FormulaProblemKind.Unterminated, p.Kind);
    }

    [Fact]
    public void Before_names_are_read_unknown_names_say_so()
    {
        var ctx = new FormulaNameContext { NamesRead = false };
        var p = Assert.Single(Errors("=fares*2", ctx));
        Assert.Contains("haven't been read", p.Message);
    }

    [Fact]
    public void Clone_is_independent()
    {
        var ctx = Workbook();
        var copy = ctx.Clone();
        ctx.DefinedNames.Add("later");
        Assert.DoesNotContain("later", copy.DefinedNames);
        Assert.Contains("fares", copy.DefinedNames);
        Assert.Contains("Fare", copy.Tables["tblFares"]);
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
        Assert.Equal(FormulaProblemKind.More, result.Problems[^1].Kind);
    }
    // ------------------------------------------------------------ argument counts (#424)

    [Theory]
    [InlineData("=IF(A1>0,SUM(B1),C1,D1)", "IF has 4 arguments; it takes at most 3.")]
    [InlineData("=XLOOKUP(A1,B1:B3)", "XLOOKUP has 2 arguments; it needs 3 to 6.")]
    [InlineData("=REDUCE(0,A1:A3)", "REDUCE has 2 arguments; it needs 3.")]
    [InlineData("=SUM()", "SUM has 0 arguments; it needs at least 1.")]
    [InlineData("=PI(1)", "PI has 1 argument; it takes none.")]
    [InlineData("=_xlfn.XLOOKUP(A1,B1:B3)", "_xlfn.XLOOKUP has 2 arguments; it needs 3 to 6.")]
    public void Wrong_argument_counts_name_the_function(string formula, string message)
    {
        var p = Assert.Single(Errors(formula));
        Assert.Equal(FormulaProblemKind.ArgumentCount, p.Kind);
        Assert.StartsWith(message, p.Message);
        Assert.Equal(formula.LastIndexOf(')'), p.RelatedStart);
    }

    [Theory]
    [InlineData("=XLOOKUP(1,A1:A3,B1:B3,,2)")]
    [InlineData("=ROW()+COLUMN()+PI()+TODAY()")]
    [InlineData("=REDUCE(,A1:A3,LAMBDA(a,v,a+v))")]
    [InlineData("=SORTBY(A1:A3,B1:B3,-1,C1:C3,1)")]
    [InlineData("=TEXTSPLIT(A1,,\"-\")")]
    [InlineData("=LET(IFX,LAMBDA(a,a),IFX(1))")]
    [InlineData("=CHOOSE(2,\"a\",\"b\",\"c\",\"d\")")]
    [InlineData("=VLOOKUP(1,A1:B3,2)")]
    public void Valid_argument_counts_are_clean(string formula)
    {
        Assert.Empty(Errors(formula));
    }

    [Fact]
    public void Library_LAMBDA_argument_counts_come_from_the_metadata()
    {
        var ctx = Workbook();
        ctx.DefinedNames.Add("TLOOKUP");
        ctx.DefinedNames.Add("lookup.TLOOKUP");
        ctx.LibraryArgumentCounts["TLOOKUP"] = (3, 4);
        ctx.LibraryArgumentCounts["lookup.TLOOKUP"] = (3, 4);

        var p = Assert.Single(Errors("=TLOOKUP(A1:B3,\"x\")", ctx));
        Assert.Equal("TLOOKUP has 2 arguments; it needs 3 to 4 (by its library header). The ) at character 19 closes it.", p.Message);
        Assert.Empty(Errors("=TLOOKUP(A1:B3,\"x\",\"y\")", ctx));
        Assert.Single(Errors("=lookup.TLOOKUP(A1:B3,1,2,3,4)", ctx));
    }

    // ------------------------------------------------------------ repair search (#426)

    private static FormulaCheckResult Check(string formula, FormulaNameContext? ctx = null) =>
        FormulaChecker.Check(formula, ctx ?? Workbook());

    [Fact]
    public void Tims_REDUCE_example_leads_with_the_stray_bracket()
    {
        // #426: one stray ) after TAKE(moves, 5) closed REDUCE early; #423 reported three consequences.
        const string f = "=LET(start, A1,\n moves, B1:B10,\n step, LAMBDA(acc, m, acc + m),\n" +
                         " final, REDUCE(start, TAKE(moves, 5)), step),\n final)";
        var r = Check(f);
        var stray = f.IndexOf("5)),", StringComparison.Ordinal) + 2;

        Assert.Equal(RepairOutcome.One, r.Repair);
        Assert.True(r.HasSingleFix);
        var fix = Assert.Single(r.Repairs);
        Assert.False(fix.Insert);
        Assert.Equal(stray, fix.Position);
        Assert.Equal($"Remove the ) after …start, TAKE(moves, 5) (character {stray + 1}).", fix.Describe(f));
        Assert.Equal("REDUCE(start, TAKE(moves, 5), step)", fix.Fixed.Substring(fix.CallStart, fix.CallLength));
        Assert.Empty(FormulaChecker.Check(fix.Fixed, Workbook()).Problems.Where(p => !p.IsNote));

        // Everything else is folded under the fix.
        Assert.All(r.Problems.Where(p => !p.IsNote), p => Assert.True(p.IsConsequence));
        Assert.Contains(r.Problems, p => p.Kind == FormulaProblemKind.ArgumentCount && p.Message.StartsWith("REDUCE has 2 arguments; it needs 3."));
    }

    [Fact]
    public void Stall_1_ranks_the_bracket_Tim_removed_first()
    {
        // Two single edits make it valid: the ) after "I" (Tim's fix) and the ) after GRIDLOOKUP's 1.
        const string f = "=LET(start,K9,pos,MOVECELL(start,$E$3,SEQUENCE(30)),nonIce,GRIDLOOKUP(mp,pos,1)<>\"I\"),nonIce)";
        var r = Check(f);
        Assert.Equal(RepairOutcome.Several, r.Repair);
        Assert.Equal(f.IndexOf("\"I\")", StringComparison.Ordinal) + 3, r.Repairs[0].Position);
        Assert.False(r.HasSingleFix);

        // mp is a typo the bracket doesn't explain: still reported on its own.
        Assert.Contains(r.Problems, p => p.Kind == FormulaProblemKind.UnknownName && At(f, p) == "mp" && !p.IsConsequence);
    }

    [Fact]
    public void Stall_6_removes_the_extra_bracket_at_the_end()
    {
        const string f = "=SUMIFS(Deliveries!$D$3:$D$42,Deliveries!$C$3:$C$42,G58,Deliveries!$D$3:$D$42,\">=\"&H58))";
        var ctx = Workbook();
        ctx.Sheets.Add("Deliveries");
        var r = Check(f, ctx);
        Assert.Equal(RepairOutcome.One, r.Repair);
        Assert.Equal(f.Length - 1, r.Repairs[0].Position);
    }

    [Fact]
    public void Stall_7_removes_the_bracket_that_closed_REDUCE()
    {
        const string f = "=REDUCE(VSTACK(0,1),TAKE(EXPLODE(G142),$I$140)),LAMBDA(a,v,a+v))";
        var ctx = Workbook();
        ctx.DefinedNames.Add("EXPLODE");
        ctx.LibraryArgumentCounts["EXPLODE"] = (1, 4);
        var r = Check(f, ctx);
        Assert.Equal(RepairOutcome.One, r.Repair);
        Assert.Equal(f.IndexOf("$I$140))", StringComparison.Ordinal) + 7, r.Repairs[0].Position);
    }

    [Fact]
    public void Stall_8_offers_a_valid_edit_and_shows_what_it_changes()
    {
        // Tim deleted TAKE( and left its ). Removing the ) after G142 is valid too (EXPLODE takes a
        // chunk size), though Tim restored TAKE( instead: the preview shows EXPLODE(G142,$I$140).
        const string f = "=REDUCE(VSTACK(0,1),EXPLODE(G142),$I$140),LAMBDA(a,v,a+v))";
        var ctx = Workbook();
        ctx.DefinedNames.Add("EXPLODE");
        ctx.LibraryArgumentCounts["EXPLODE"] = (1, 4);
        var r = Check(f, ctx);
        Assert.Equal(RepairOutcome.One, r.Repair);
        var fix = r.Repairs[0];
        Assert.Equal("EXPLODE(G142,$I$140)", fix.Fixed.Substring(fix.CallStart, fix.CallLength));
    }

    [Fact]
    public void Missing_bracket_is_added_at_the_end()
    {
        const string f = "=SUM(IF(A2:A6=\"x\",B2:B6)";
        var r = Check(f);
        Assert.Equal(RepairOutcome.One, r.Repair);
        Assert.True(r.Repairs[0].Insert);
        Assert.Equal(f.Length, r.Repairs[0].Position);
        Assert.Equal(f + ")", r.Repairs[0].Fixed);
    }

    [Theory]
    [InlineData("=IF(A1>0,1,2,3)")]
    [InlineData("=MID(ROUND(A1),2)")]
    [InlineData("=TEXT(A1)")]
    public void Balanced_brackets_skip_the_search(string formula)
    {
        // One ) edit unbalances them, so it can't help.
        var r = Check(formula);
        Assert.Equal(RepairOutcome.NotNeeded, r.Repair);
        Assert.Empty(r.Repairs);
    }

    [Fact]
    public void Two_brackets_off_reports_none()
    {
        Assert.Equal(RepairOutcome.NoneFound, Check("=SUM(1)))").Repair);
    }

    [Theory]
    [InlineData("=IF(A1,1,2)\n)")]
    [InlineData("=SUMIFS(A:A,B:B,1) )")]
    public void An_extra_bracket_after_a_space_ranks_the_trailing_one_first(string formula)
    {
        var r = Check(formula);
        Assert.Equal(RepairOutcome.Several, r.Repair);
        Assert.Equal(formula.Length - 1, r.Repairs[0].Position);
    }

    [Fact]
    public void An_empty_bracket_is_not_offered()
    {
        Assert.DoesNotContain(Check("=(").Repairs, r => r.Fixed == "=()");
    }

    [Fact]
    public void Name_only_problems_skip_the_search()
    {
        Assert.Equal(RepairOutcome.NotNeeded, Check("=XLOOKUP(G113,tyupe,fares)").Repair);
    }

    [Fact]
    public void Repair_search_on_a_5000_character_LET_is_fast()
    {
        var sb = new System.Text.StringBuilder("=LET(");
        for (var i = 0; sb.Length < 5000; i++)
            sb.Append($"\n val_{i}, REDUCE(0, SEQUENCE({i}+1), LAMBDA(acc, x, acc + IF(x > {i}, x, 0))),");
        sb.Append("\n val_0)");
        var valid = sb.ToString();
        Assert.Empty(Errors(valid));

        // A stray ) late in the formula, and a missing one at the end.
        var stray = valid.Insert(valid.LastIndexOf("SEQUENCE(", StringComparison.Ordinal) + 13, ")");
        var missing = valid[..^1];
        foreach (var f in new[] { stray, missing })
        {
            var sw = Stopwatch.StartNew();
            var r = Check(f);
            sw.Stop();
            // The likeliest edit is tried first, so even a search cut short by the clock finds it.
            Assert.Equal(RepairOutcome.One, r.Repair);
            Assert.Empty(FormulaChecker.Check(r.Repairs[0].Fixed, Workbook()).Problems.Where(p => !p.IsNote));
            Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
        }
    }
}
