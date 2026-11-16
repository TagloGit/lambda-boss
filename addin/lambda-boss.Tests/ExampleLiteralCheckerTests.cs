using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     The example check (spec 0020, excel-esports#80). The Gerrymandering cases are Tim's three
///     slips from 14 Nov 2026 (excel-esports cases/2026-11-14-gerrymandering), with the case's
///     layout: game, level, points, answer, gap, map, threshold or district to the right.
/// </summary>
public class ExampleLiteralCheckerTests
{
    /// <summary>A sheet from cell values; a string starting "=" is a formula cell (its value given separately).</summary>
    private sealed class Sheet : IExampleSheet
    {
        private readonly Dictionary<(int, int), object> _values = new();
        private readonly HashSet<(int, int)> _formulas = new();

        public Sheet Set(string address, object value, bool formula = false)
        {
            Assert.True(ExampleLiteralChecker.TryCell(address, out var r, out var c));
            _values[(r, c)] = value is int i ? (double)i : value;
            if (formula)
                _formulas.Add((r, c));
            LastColumn = Math.Max(LastColumn, c);
            return this;
        }

        public Sheet Row(int row, params object?[] values)
        {
            for (var i = 0; i < values.Length; i++)
                if (values[i] is { } v)
                    Set(ExampleLiteralChecker.Address(row, i + 1), v);
            return this;
        }

        public object? Value(int row, int column) => _values.TryGetValue((row, column), out var v) ? v : null;

        public bool IsFormula(int row, int column) => _formulas.Contains((row, column));

        public int LastColumn { get; private set; }
    }

    private static IReadOnlyList<ExampleLiteralFinding> Check(Sheet sheet, string formula, string at, int[] otherRows,
        params string[] ruleTexts)
    {
        Assert.True(ExampleLiteralChecker.TryCell(at, out var r, out var c));
        return ExampleLiteralChecker.Check(new ExampleCheckRequest(formula, r, c, otherRows, new[] { c }), sheet,
            () => ExampleLiteralChecker.RuleWords(ruleTexts));
    }

    // ------------------------------------------------------------ Gerrymandering (Tim, 14 Nov)

    /// <summary>Level 4's rows: the threshold in H, to the right of the answer in E.</summary>
    private static Sheet Level4() => new Sheet()
        .Row(149, "", "Game", "Level", "Points", "Answer", null, "Map", "Threshold")
        .Row(151, "", "Example4a", 4, 0, null, null, "Enacted plan", 0.09)
        .Row(152, "", "Example4b", 4, 0, null, null, "Enacted plan", 0.13)
        .Row(154, "", 61, 4, 7, null, null, "01", 0.11)
        .Row(155, "", 62, 4, 7, null, null, "02", 0.08);

    private const string L4Fixed =
        "=LET(totals, MAP(dists, LAMBDA(v, SUM(jFlat[Votes]*(FILTER(jPlans[Value], jPlans[Section]=G151)=v)))),"
        + " all, SUM(totals), target, all/10, diff, totals-target, pct, ABS(diff/target), bad, pct>H151,"
        + " CONCAT(FILTER(dists, bad, ROUND(MAX(pct)*10000,))))";

    [Fact]
    public void Gerrymandering_L4_threshold_typed_from_the_example_row_is_flagged()
    {
        var formula = L4Fixed.Replace("pct>H151", "pct>0.09");
        var f = Assert.Single(Check(Level4(), formula, "E151", new[] { 152, 154, 155 }));
        Assert.Equal("0.09", f.Literal);
        Assert.Equal("H151", f.SourceAddress);
        Assert.Equal("0.09", formula.Substring(f.Start, f.Length));
        Assert.Contains("Use H151", f.Message);
    }

    [Fact]
    public void Gerrymandering_L4_as_fixed_is_silent() =>
        Assert.Empty(Check(Level4(), L4Fixed, "E151", new[] { 152, 154, 155 }));

    private static Sheet Level5() => new Sheet()
        .Row(192, "", "Example5", 5, 0, null, null, "Enacted plan", "A")
        .Row(194, "", 81, 5, 9, null, null, "01", "I")
        .Row(195, "", 82, 5, 9, null, null, "02", "C");

    private const string L5Fixed =
        "=LET(adds, FILTER(jPlanOne[Add], (jPlanOne[Dis]=H192)*(jPlanOne[Plan]=G192)),"
        + " ns, BIROW(adds, LAMBDA(v, TOROW(NEIGHBOURS(v)))),"
        + " items, XLOOKUP(ns&G192&H192, jPlanOne[Add]&jPlanOne[Plan]&jPlanOne[Dis], jPlanOne[Dis], \"X\"),"
        + " COUNTOF(items<>H192))";

    [Fact]
    public void Gerrymandering_L5_district_typed_from_the_example_row_is_flagged()
    {
        var formula = L5Fixed.Replace("H192", "\"A\"");
        // "A" appears three times, all matching H192: one finding.
        var f = Assert.Single(Check(Level5(), formula, "E192", new[] { 194, 195 }, "Districts are lettered A to J."));
        Assert.Equal("\"A\"", f.Literal);
        Assert.Equal("H192", f.SourceAddress);
    }

    [Fact]
    public void Gerrymandering_L5_as_fixed_is_silent_and_the_lookup_default_is_structure() =>
        Assert.Empty(Check(Level5(), L5Fixed, "E192", new[] { 194, 195 }));

    private static Sheet Level6() => new Sheet()
        .Set("B216", "Level 6").Set("C216", "Moderately Hard")
        .Set("B219", "The score is the district's actual area (number of precincts it contains), divided by the area of a circle with the same perimeter.")
        .Set("B222", "Calculate the lowest Polsby-Popper score for the districts on each map, then multiply it by 10000 and round to the nearest integer.")
        .Set("B225", "- Perimeter of a circle (aka Circumference): C = 2 × π × radius")
        .Set("B229", "Example: of the enacted plan's districts, E has a perimeter of 22 and an area of 10. A circle of the same perimeter would have a radius")
        .Set("B230", "of about 3.501409 and thus area of about 38.515496. This results in a Polsby-Popper score of 10 ÷ 38.515496, which is about 0.259635756.")
        .Set("B231", "This is the lowest score among the districts, so the final answer is 10000 × 0.259635756, rounded to the nearest integer, i.e. 2596.")
        .Row(235, "", "Game", "Level", "Points", "Answer", null, "Map")
        .Row(237, "", "Example6", 6, 0, null, null, "Enacted plan")
        .Row(239, "", 101, 6, 8, null, null, "01")
        .Row(240, "", 102, 6, 8, null, null, "02");

    private const string L6 =
        "=LET(ps, MAP(dists, LAMBDA(v, getPerim(G237,v))),"
        + " areas, MAP(dists, LAMBDA(v, COUNTOF((jPlanOne[Plan]=G237)*(jPlanOne[Dis]=v)))),"
        + " rads, ps/(2*PI()), as, PI()*(rads^2), scores, 10/as, ROUND(10000*MIN(scores),))";

    [Fact]
    public void Gerrymandering_L6_area_from_the_example_text_is_flagged()
    {
        var f = Assert.Single(Check(Level6(), L6, "E237", new[] { 239, 240 }));
        Assert.Equal("10", f.Literal);
        Assert.True(f.FromText);
        Assert.Contains("an area of 10", f.Message);
        Assert.Equal("10/as", L6.Substring(f.Start, 5));
    }

    [Fact]
    public void Gerrymandering_L6_as_corrected_is_silent() =>
        // 2 is in the rules (C = 2 × π × radius) and 10000 is a scale: neither is flagged.
        Assert.Empty(Check(Level6(), L6.Replace("10/as", "areas/as"), "E237", new[] { 239, 240 }));

    // ------------------------------------------------------------ where inputs are

    [Fact]
    public void An_input_to_the_left_of_the_formula_is_found()
    {
        var sheet = new Sheet()
            .Row(5, "Example", 3.5, "north", null)
            .Row(6, 1, 4.25, "south", null);
        var found = Check(sheet, "=A5&3.5&\"north\"", "D5", new[] { 6 });
        Assert.Equal(new[] { "B5", "C5" }, found.Select(f => f.SourceAddress));
    }

    [Fact]
    public void Both_sides_are_searched_and_each_match_is_reported()
    {
        var sheet = new Sheet()
            .Row(5, "Example", 7.5, null, null, "red")
            .Row(6, 1, 8.5, null, null, "blue");
        var found = Check(sheet, "=IF(E5=\"red\", 7.5, 0)", "C5", new[] { 6 });
        Assert.Equal(new[] { "B5" }, found.Select(f => f.SourceAddress));
        found = Check(sheet, "=IF(B5>7.5, \"red\", \"\")", "C5", new[] { 6 });
        Assert.Equal(new[] { "E5" }, found.Select(f => f.SourceAddress));
    }

    [Fact]
    public void Two_empty_columns_end_the_block_but_one_does_not()
    {
        var sheet = new Sheet()
            .Row(5, "Example", null, 12.5, null, null, 99.5)
            .Row(6, 1, null, 13.5, null, null, 98.5);
        // A: formula; C is past one empty column (found), F past two (another table: not looked at).
        Assert.Equal("C5", Assert.Single(Check(sheet, "=12.5+A5", "A5", new[] { 6 })).SourceAddress);
        Assert.Empty(Check(sheet, "=99.5+C5", "A5", new[] { 6 }));
    }

    [Fact]
    public void A_column_that_is_the_same_on_every_row_is_not_an_input()
    {
        var sheet = new Sheet()
            .Row(5, "Example", 4, 2.5)
            .Row(6, 1, 4, 3.5);
        Assert.Empty(Check(sheet, "=C5*4", "D5", new[] { 6 }));
    }

    [Fact]
    public void A_cell_the_formula_reads_is_used_not_replaced()
    {
        // Cricket Darts: IF(throw="Bull", 25, ...) with throw = the cell holding "Bull".
        var sheet = new Sheet()
            .Row(83, "Example", "T20", "Bull")
            .Row(84, 1, "S5", "D3");
        Assert.Empty(Check(sheet, "=LET(throw, C83, IF(throw=\"Bull\", 25, 0))", "D83", new[] { 84 }));
    }

    [Fact]
    public void A_word_the_rules_use_is_vocabulary_not_example_data()
    {
        var sheet = new Sheet()
            .Row(83, "Example", "T20", "Bull")
            .Row(84, 1, "S5", "D3");
        const string formula = "=LET(throw, B83, IF(throw=\"Bull\", 25, 0))";
        Assert.Empty(Check(sheet, formula, "D83", new[] { 84 },
            "Goal: in Cricket the goal is to open the numbers 15-20 and the Bull, and score more."));
        Assert.Single(Check(sheet, formula, "D83", new[] { 84 }));
    }

    [Fact]
    public void Vocabulary_keeps_its_case()
    {
        // Gerrymandering: the rules say "the enacted plan"; the example row's map is "Enacted plan".
        var sheet = new Sheet()
            .Row(151, "", "Example4a", 4, 0, null, null, "Enacted plan", 0.09)
            .Row(154, "", 61, 4, 7, null, null, "01", 0.11);
        var f = Assert.Single(Check(sheet, "=COUNTIFS(jPlans[Section], \"Enacted plan\", jPlans[Pct], \">\"&H151)", "E151",
            new[] { 154 }, "Of the districts under the enacted plan, count those above the threshold in each map."));
        Assert.Equal("G151", f.SourceAddress);
    }

    [Fact]
    public void An_example_word_does_not_count_as_rules_vocabulary()
    {
        var sheet = new Sheet()
            .Row(83, "Example", "T20", "Bull")
            .Row(84, 1, "S5", "D3");
        Assert.Single(Check(sheet, "=LET(throw, B83, IF(throw=\"Bull\", 25, 0))", "D83", new[] { 84 },
            "Example: the third dart hits the Bull, so the player scores 25."));
    }

    [Fact]
    public void A_small_integer_matches_typed_cells_only()
    {
        var sheet = new Sheet()
            .Row(7, "Example", 3, null)
            .Row(8, 1, 2, null)
            .Set("C7", 3.0, formula: true).Set("C8", 2.0, formula: true);
        // 3 equals B7 (typed): flagged. Against C7 alone (a computed 3), not.
        Assert.Equal("B7", Assert.Single(Check(sheet, "=A7>=3", "D7", new[] { 8 })).SourceAddress);
        var computed = new Sheet()
            .Row(7, "Example", null, null)
            .Row(8, 1, null, null)
            .Set("C7", 3.0, formula: true).Set("C8", 2.0, formula: true).Set("B7", "x").Set("B8", "y");
        Assert.Empty(Check(computed, "=A7>=3", "D7", new[] { 8 }));
    }

    [Fact]
    public void A_larger_number_matches_a_computed_cell_too()
    {
        var sheet = new Sheet()
            .Row(7, "Example", "x")
            .Row(8, 1, "y")
            .Set("C7", 107.7, formula: true).Set("C8", 98.2, formula: true);
        Assert.Equal("C7", Assert.Single(Check(sheet, "=B7&107.7", "D7", new[] { 8 })).SourceAddress);
    }

    [Fact]
    public void Structural_arguments_and_trivial_constants_are_never_flagged()
    {
        var sheet = new Sheet()
            .Row(5, "Example", 3, 0, 1, 100, 2)
            .Row(6, 1, 4, 5, 6, 7, 8);
        Assert.Empty(Check(sheet, "=INDEX(Z1:Z9, 3)+ROUND(A5, 2)+0+1+100+SWITCH(A5, 3, 2, 5)", "H5", new[] { 6 }));
    }

    [Fact]
    public void Without_other_rows_the_example_row_is_not_compared()
    {
        var sheet = new Sheet().Row(5, "Example", 0.09);
        Assert.Empty(Check(sheet, "=A5>0.09", "C5", Array.Empty<int>()));
    }

    [Fact]
    public void The_first_row_of_a_range_the_formula_reads_counts_when_its_column_varies()
    {
        var sheet = new Sheet()
            .Row(2, "Example", "Rioja", 0.45)
            .Row(3, 1, "Syrah", 0.55)
            .Row(4, 2, "Chianti", 0.35);
        var f = Assert.Single(Check(sheet, "=MAP(B2:B4, C2:C4, LAMBDA(w, p, IF(w=\"Rioja\", p, 0)))", "E2", Array.Empty<int>()));
        Assert.Equal("B2", f.SourceAddress);
        Assert.Contains("first row of a range", f.Message);
    }

    [Fact]
    public void A_number_in_both_the_rules_and_the_example_is_a_rule()
    {
        var sheet = new Sheet()
            .Set("B10", "Level 3").Set("B11", "A player who scores more than 21 points in a round loses that round.")
            .Set("B12", "Example: player 1 scores 21 points and wins, player 2 scores 23 and loses.")
            .Row(14, "", "Example3", 3)
            .Row(15, "", 1, 3);
        Assert.Empty(Check(sheet, "=IF(C14>21, 0, 1)", "E14", new[] { 15 }));
        Assert.Single(Check(sheet, "=IF(C14>23, 0, 1)", "E14", new[] { 15 }));
    }

    [Fact]
    public void Text_above_the_level_heading_is_not_this_level()
    {
        var sheet = new Sheet()
            .Set("B5", "Example: in the last level the answer was 42 for the first example game.")
            .Set("B10", "Level 3")
            .Row(14, "", "Example3", 3)
            .Row(15, "", 1, 3);
        Assert.Empty(Check(sheet, "=C14*42", "E14", new[] { 15 }));
    }

    // ------------------------------------------------------------ the formula scan

    [Fact]
    public void Scan_reads_signs_percents_and_same_sheet_references()
    {
        var (lits, refs) = ExampleLiteralChecker.Scan("=-0.5+A1*9%-3+Other!B2+SUM(C3:D9)+'My Sheet'!E5+t[Col]");
        Assert.Equal(new[] { "-0.5", "9%", "3" }, lits.Select(l => l.Source));
        Assert.Equal(new[] { -0.5, 0.09, 3 }, lits.Select(l => l.Number));
        Assert.Equal(new[] { (1, 1, 1, 1), (3, 3, 9, 4) }, refs.Select(r => (r.Row1, r.Col1, r.Row2, r.Col2)));
    }

    [Fact]
    public void Scan_marks_literals_by_their_argument_slot()
    {
        var (lits, _) = ExampleLiteralChecker.Scan("=ROUND(A1*2.5, 2)+INDEX(B:B, 7)+ROUND((A1)*3, 0)");
        Assert.Equal(new[] { false, true, true, false, true },
            lits.Select(l => l.Structural));
    }

    [Fact]
    public void A_formula_that_does_not_tokenize_is_skipped() =>
        Assert.Empty(Check(new Sheet().Row(5, "x", 2.5).Row(6, "y", 3.5), "=\"unterminated", "C5", new[] { 6 }));

    [Fact]
    public void Prose_numbers_read_thousands_and_percents()
    {
        Assert.Equal(new[] { 10000d, 12.22, 0.1222, 2596 },
            ExampleLiteralChecker.Numbers("by 10,000 and 12.22% gives 2596; Example4a is a name").ToArray());
    }

    // ------------------------------------------------------------ review (PR #476)

    [Fact]
    public void Every_written_column_is_excluded_from_every_column()
    {
        // A fill of D5:F7: E's values are outputs, not inputs, when D is checked.
        var sheet = new Sheet().Row(5, null, "a", null, null, 25).Row(6, null, "b", null, null, 30).Row(7, null, "c", null, null, 35);
        var r = ExampleLiteralChecker.Check(new ExampleCheckRequest("=IF(B5=\"a\",25,2)", 5, 4, new[] { 6, 7 }, new[] { 4, 5, 6 }),
            sheet, null);
        Assert.Empty(r);
    }

    [Fact]
    public void A_cell_read_through_a_range_is_not_a_candidate()
    {
        var sheet = new Sheet().Row(5, null, "x", 3, "x").Row(6, null, "y", 4, "x").Row(7, null, "x", 5, "y");
        Assert.Empty(Check(sheet, "=IF(COUNTIF(B5:D5,\"x\")>=3,\"yes\",\"no\")", "F5", new[] { 6, 7 }));
    }

    [Fact]
    public void A_column_blank_on_the_other_rows_does_not_vary()
    {
        // An input box level with the first row: Threshold 0.09 in H5, nothing below.
        var sheet = new Sheet().Row(5, null, "a", null, null, null, null, "Threshold", 0.09).Row(6, null, "b").Row(7, null, "c");
        Assert.Empty(Check(sheet, "=IF(B5>0.09,1,2)", "E5", new[] { 6, 7 }));
    }

    [Fact]
    public void A_range_that_does_not_start_on_the_example_row_is_not_a_candidate()
    {
        var sheet = new Sheet().Set("K2", 7.5).Set("K3", 8.5).Set("K4", 9.5).Row(10, null, "a").Row(11, null, "b");
        Assert.Empty(Check(sheet, "=XLOOKUP(B10,J2:J4,K2:K4)>7.5", "E10", new[] { 11 }));
    }

    [Fact]
    public void A_range_starting_on_the_example_row_is_a_candidate()
    {
        var sheet = new Sheet().Row(2, null, 7.5).Row(3, null, 8.5).Row(4, null, 9.5);
        var f = Assert.Single(Check(sheet, "=MAP(B2:B4,LAMBDA(x,x))*7.5", "E2", new[] { 3, 4 }));
        Assert.Equal("B2", f.SourceAddress);
    }

    // ------------------------------------------------------------ Tim's PC (16 Nov)

    /// <summary>
    ///     Tim's sheet: a header, a separate example line (26 | 670), then the table from row 299,
    ///     income in G and target in H; a three-line LET in I299 with the target typed, filled down.
    /// </summary>
    [Fact]
    public void Tim_16_Nov_multi_line_LET_typed_target_at_row_299_is_flagged()
    {
        var sheet = new Sheet()
            .Set("G290", "Weekly income").Set("H290", "Target")
            .Set("G292", 26).Set("H292", 670)
            .Set("G299", 26).Set("H299", 620)
            .Set("G300", 21).Set("H300", 500)
            .Set("G301", 29).Set("H301", 1300)
            .Set("G302", 13).Set("H302", 490)
            .Set("G303", 14).Set("H303", 470)
            .Set("G304", 27).Set("H304", 520);
        const string formula = "=LET(target, 620,\n  income, G299,\n  target+income)";
        var f = Assert.Single(Check(sheet, formula, "I299", new[] { 300, 301, 302, 303, 304 }));
        Assert.Equal("620", f.Literal);
        Assert.Equal("H299", f.SourceAddress);
    }

    /// <summary>
    ///     Excel's IDispatch rejects LCID 127 (the invariant culture) with 0x80028018 when the
    ///     Windows user locale is en-GB: every late-bound read failed on Tim's PC.
    /// </summary>
    [Fact]
    public void Late_bound_reads_pass_en_US_to_Excel() =>
        Assert.Equal(1033, LambdaBoss.Commands.Com.DispatchCulture.LCID);

    // ------------------------------------------------------------ own spill columns (#486)

    private static IReadOnlyList<ExampleLiteralFinding> CheckWith(Sheet sheet, string formula, string at,
        int[] otherRows, IReadOnlyCollection<int> target)
    {
        Assert.True(ExampleLiteralChecker.TryCell(at, out var r, out var c));
        return ExampleLiteralChecker.Check(new ExampleCheckRequest(formula, r, c, otherRows, target), sheet,
            () => ExampleLiteralChecker.RuleWords(Array.Empty<string>()));
    }

    /// <summary>kit-vmtest Spill sheet: B1 spills into B1:C1 (C1 = 10); a fill of B2:B6 with B1's formula.</summary>
    private static Sheet SpillSheet() => new Sheet()
        .Row(1, 1, "S", 10, 2).Set("C1", 10).Set("D1", 2, formula: true)
        .Set("D2", 5).Set("D3", 5).Set("D4", 5).Set("D5", 5).Set("D6", 5)
        .Set("A2", 2).Set("A3", 3).Set("A4", 4).Set("A5", 5).Set("A6", 6)
        .Set("B2", "S").Set("C2", 20).Set("C3", 30).Set("C4", 40).Set("C5", 50).Set("C6", 60);

    [Fact]
    public void Fill_literal_equal_to_the_formulas_own_sideways_spill_is_not_flagged()
    {
        var rows = new[] { 2, 3, 4, 5, 6 };
        const string formula = "=HSTACK(\"S\",A1*10)";
        var writtenOnly = new HashSet<int> { 2 };
        // Before the fix: only the written column was excluded, so C1 (10) matched the literal 10.
        Assert.Single(CheckWith(SpillSheet(), formula, "B1", rows, writtenOnly));
        var target = new HashSet<int> { 2 };
        ExampleLiteralChecker.ExcludeSpillColumns(target, 2, 2);
        Assert.Equal(new[] { 2, 3 }, target.OrderBy(x => x));
        Assert.Empty(CheckWith(SpillSheet(), formula, "B1", rows, target));
    }

    [Fact]
    public void J_formula_spilling_into_K_to_M_excludes_those_columns()
    {
        var sheet = new Sheet().Set("A1", 7).Set("A2", 8).Set("A3", 9)
            .Set("K1", 77).Set("K2", 88).Set("K3", 99).Set("L1", 1).Set("L2", 2).Set("L3", 3)
            .Set("M1", 4).Set("M2", 5).Set("M3", 6);
        var target = new HashSet<int> { 10 };
        ExampleLiteralChecker.ExcludeSpillColumns(target, 10, 4);
        Assert.Equal(new[] { 10, 11, 12, 13 }, target.OrderBy(x => x));
        Assert.Empty(CheckWith(sheet, "=A1+77", "J1", new[] { 2, 3 }, target));
        // Control: without the spill columns the 77 in K1 is flagged.
        Assert.Single(CheckWith(sheet, "=A1+77", "J1", new[] { 2, 3 }, new HashSet<int> { 10 }));
    }
}
