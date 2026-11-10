namespace LambdaBoss;

/// <summary>
///     One fix Check Formula can apply (issue #426): the whole formula before and after, where the
///     cursor goes after it (<see cref="Caret"/>, an index into <see cref="After"/>), and the span
///     of <see cref="After"/> to highlight in the preview.
/// </summary>
internal sealed record FormulaFix(
    string Label,
    string Before,
    string After,
    int Caret,
    int HighlightStart,
    int HighlightLength)
{
    /// <summary>The line of <see cref="After"/> holding the highlight, for the preview.</summary>
    public (int Start, int End) PreviewLine
    {
        get
        {
            var at = Math.Min(HighlightStart, After.Length);
            var start = at == 0 ? 0 : After.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
            var end = After.IndexOf('\n', Math.Min(After.Length, at + Math.Max(0, HighlightLength - 1)));
            return (start, end < 0 ? After.Length : end);
        }
    }
}

/// <summary>
///     The fixes for one check result: the bracket edit, when exactly one makes the formula valid
///     and the search finished; one per candidate for each mistyped name; and "fix all" when every
///     problem has exactly one fix. Pure.
/// </summary>
internal sealed class FormulaFixes
{
    private FormulaFixes(FormulaFix? bracket, IReadOnlyDictionary<FormulaProblem, IReadOnlyList<FormulaFix>> byProblem,
        FormulaFix? all)
    {
        Bracket = bracket;
        ByProblem = byProblem;
        All = all;
    }

    public FormulaFix? Bracket { get; }

    /// <summary>Name fixes, keyed by problem.</summary>
    public IReadOnlyDictionary<FormulaProblem, IReadOnlyList<FormulaFix>> ByProblem { get; }

    public FormulaFix? All { get; }

    public bool Any => Bracket is not null || ByProblem.Count > 0;

    public static FormulaFixes For(FormulaCheckResult result)
    {
        var f = result.Formula;
        var edits = new List<(int Start, int Length, string Text)>();

        FormulaFix? bracket = null;
        if (result.HasSingleFix)
        {
            var r = result.Repairs[0];
            var caret = r.Insert ? r.Position + 1 : r.Position;
            bracket = new FormulaFix(r.Insert ? "Add the )" : "Remove the )", f, r.Fixed, caret,
                r.CallStart >= 0 ? r.CallStart : Math.Max(0, caret - 1),
                r.CallStart >= 0 ? r.CallLength : 1);
            edits.Add(r.Insert ? (r.Position, 0, ")") : (r.Position, 1, ""));
        }

        var byProblem = new Dictionary<FormulaProblem, IReadOnlyList<FormulaFix>>();
        var everyOneHasOne = true;
        foreach (var p in result.Problems.Where(p => !p.IsNote && !p.IsConsequence))
        {
            if (p.Replacements.Count == 0 || p.Start + p.Length > f.Length)
            {
                everyOneHasOne = false;
                continue;
            }

            var current = f.Substring(p.Start, p.Length);
            byProblem[p] = p.Replacements
                .Select(text => new FormulaFix($"{current} → {text}", f,
                    f[..p.Start] + text + f[(p.Start + p.Length)..],
                    p.Start + text.Length, p.Start, text.Length))
                .ToList();
            if (p.Replacements.Count == 1)
                edits.Add((p.Start, p.Length, p.Replacements[0]));
            else
                everyOneHasOne = false;
        }

        // Fix all: every problem has exactly one fix, and there's more than one to apply.
        FormulaFix? all = null;
        if (everyOneHasOne && edits.Count > 1 && Disjoint(edits))
        {
            var after = f;
            foreach (var e in edits.OrderByDescending(e => e.Start))
                after = after[..e.Start] + e.Text + after[(e.Start + e.Length)..];
            var first = edits.OrderBy(e => e.Start).First();
            all = new FormulaFix($"Fix all {edits.Count}", f, after, first.Start + first.Text.Length, 0, 0);
        }

        return new FormulaFixes(bracket, byProblem, all);
    }

    private static bool Disjoint(List<(int Start, int Length, string Text)> edits)
    {
        var sorted = edits.OrderBy(e => e.Start).ToList();
        for (var i = 1; i < sorted.Count; i++)
            if (sorted[i].Start < sorted[i - 1].Start + Math.Max(1, sorted[i - 1].Length))
                return false;
        return true;
    }
}
