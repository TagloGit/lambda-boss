using System.Text;
using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     Spec 0008 — the <c>/Refactor</c> entry point. Takes a formula and
///     the active cell's sheet, hoists every cell ref / range into its own
///     LET binding, and emits a tidy <c>=LET(...)</c>.
///     PR 2 adds existing-LET handling. When the input is already a LET,
///     value bindings pre-populate the Inputs section; calculation bindings
///     populate the read-only Calculation-bindings section; value bindings
///     with equivalent canonical RHS are merged (first wins); cell refs
///     inside calculation bindings or the body are extracted as new input
///     rows; the synthesised LET emits all value bindings first (in dialog
///     order) followed by all calc bindings (in source order).
///     The engine re-derives merge state on every <see cref="Recompute" />
///     call — the dialog only carries name / Include / order for each row
///     via <see cref="RefactorRowState" />, not the underlying merge graph,
///     so the engine has to be re-run anchored on the source formula
///     text.
/// </summary>
public static class RefactorEngine
{
    // Identifier rewriter: substitutes whole-token identifiers from
    // <paramref name="subs" /> while leaving strings, sheet/workbook
    // qualifiers, and cell-shaped tokens alone. Cell refs in qualified
    // form (e.g. <c>Sheet1!A1</c>) keep their pieces — the lookbehind
    // class guards against renaming the <c>A1</c> tail of a sheet
    // qualifier.
    private static readonly Regex IdentifierTokenPattern = new(
        // Lookbehind matches the same identifier-boundary characters
        // CellRefExtractor uses, so we don't accidentally substitute the
        // tail of a sheet-qualified ref (`Sheet1!A1` keeps its `A1`) or
        // the row digits inside a numeric (`123` isn't an identifier
        // anyway, but the negative lookbehind on '.' also prevents
        // matching across decimal points).
        @"(?<![A-Za-z0-9_.!:'\]#?])[A-Za-z_][A-Za-z0-9_.?]*",
        RegexOptions.CultureInvariant);

    /// <summary>
    ///     Runs the initial refactor over <paramref name="formula" />.
    ///     Existing LETs are parsed and merged/extracted per spec 0008;
    ///     non-LET formulas have their cell refs hoisted in first-seen
    ///     order.
    /// </summary>
    public static RefactorResult Refactor(string formula, string activeSheet)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));
        if (activeSheet is null) throw new ArgumentNullException(nameof(activeSheet));

        return RefactorInternal(formula, activeSheet, null);
    }

    /// <summary>
    ///     Re-runs the refactor with the dialog's per-row state.
    ///     <paramref name="rowStates" /> is the full set of rows in
    ///     user-chosen order; rows with <see cref="RefactorRowState.Include" />
    ///     = false are dropped. For non-LET formulas a dropped row leaves
    ///     its source cell ref inline (matching PR 1). For existing LETs a
    ///     dropped value binding is removed and its name is inlined back to
    ///     the binding's original RHS text wherever it appeared.
    /// </summary>
    public static RefactorResult Recompute(
        string formula,
        string activeSheet,
        IReadOnlyList<RefactorRowState> rowStates)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));
        if (activeSheet is null) throw new ArgumentNullException(nameof(activeSheet));
        if (rowStates is null) throw new ArgumentNullException(nameof(rowStates));

        return RefactorInternal(formula, activeSheet, rowStates);
    }

    private static RefactorResult RefactorInternal(
        string formula,
        string activeSheet,
        IReadOnlyList<RefactorRowState>? rowStates)
    {
        if (LetParser.IsLetFormula(formula))
            return RefactorExistingLet(formula, activeSheet, rowStates);
        return RefactorNonLet(formula, activeSheet, rowStates);
    }

    // ---------------- non-LET path ----------------

    private static RefactorResult RefactorNonLet(
        string formula,
        string activeSheet,
        IReadOnlyList<RefactorRowState>? rowStates)
    {
        var extracted = CellRefExtractor.Extract(formula, activeSheet);

        var defaultRows = new List<RefactorInputRow>(extracted.Count);
        var nameIndex = 1;
        foreach (var fr in extracted)
        {
            defaultRows.Add(new RefactorInputRow(
                BuildExtractedKey(fr, activeSheet),
                fr,
                $"input{nameIndex}",
                fr.DisplayAddress(activeSheet),
                RefactorRowOrigin.Extracted));
            nameIndex++;
        }

        var finalRows = ApplyRowStates(defaultRows, rowStates);
        var keptRows = finalRows.Where(r => r.IsIncluded).ToList();

        var synthesisedLet = BuildSynthesisedLet(
            formula, activeSheet, keptRows, Array.Empty<RefactorCalcBindingRow>(),
            false, null, null);

        return new RefactorResult(
            formula,
            keptRows.Select(r => r.Row).ToList(),
            Array.Empty<RefactorCalcBindingRow>(),
            synthesisedLet);
    }

    // ---------------- existing-LET path ----------------

    private static RefactorResult RefactorExistingLet(
        string formula,
        string activeSheet,
        IReadOnlyList<RefactorRowState>? rowStates)
    {
        ParsedLet parsed;
        try
        {
            parsed = LetParser.Parse(formula);
        }
        catch (FormatException ex)
        {
            return new RefactorResult(
                formula,
                Array.Empty<RefactorInputRow>(),
                Array.Empty<RefactorCalcBindingRow>(),
                formula,
                new RefactorDiagnostic(
                    RefactorDiagnosticKind.MalformedLet,
                    $"Refactor can't parse this LET: {ex.Message}"));
        }

        var valueBindings = parsed.Bindings.Where(b => !b.IsCalculation).ToList();
        var calcBindings = parsed.Bindings.Where(b => b.IsCalculation).ToList();

        // Step 1 — merge duplicate value bindings (first occurrence wins).
        var merge = MergeValueBindings(valueBindings, activeSheet);

        // Step 2 — walk calc binding RHSes and the body for new refs.
        //   Refs already represented by a kept value binding (matched via
        //   canonical FormulaRef) are skipped; everything else becomes an
        //   auto-named Extracted row.
        var existingRefs = new HashSet<FormulaRef>();
        foreach (var survivor in merge.Survivors)
        {
            var fr = TryParseSingleRef(survivor.RhsText, activeSheet);
            if (fr != null) existingRefs.Add(fr);
        }

        var usedNames = new HashSet<string>(
            parsed.Bindings.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

        var newRows = new List<RefactorInputRow>();
        var seenNew = new HashSet<FormulaRef>();
        var autoNameIndex = 1;

        foreach (var calc in calcBindings)
            ExtractNewRefs(calc.RhsText, activeSheet, existingRefs, seenNew, usedNames, ref autoNameIndex, newRows);
        ExtractNewRefs(parsed.Body, activeSheet, existingRefs, seenNew, usedNames, ref autoNameIndex, newRows);

        // Step 3 — build the existing-LET-value rows (in source order).
        var existingValueRows = new List<RefactorInputRow>(merge.Survivors.Count);
        foreach (var s in merge.Survivors)
        {
            merge.MergedFromBySurvivor.TryGetValue(s.Name, out var mergedFrom);
            existingValueRows.Add(new RefactorInputRow(
                BuildExistingLetKey(s.Name),
                TryParseSingleRef(s.RhsText, activeSheet),
                s.Name,
                s.RhsText,
                RefactorRowOrigin.ExistingLetValue,
                mergedFrom));
        }

        // Step 4 — combine and apply rowStates (rename / include / order).
        var defaultRows = existingValueRows.Concat(newRows).ToList();
        var finalRows = ApplyRowStates(defaultRows, rowStates);

        // Step 5 — assemble the rewrite maps and synthesise.
        var keptRows = finalRows.Where(r => r.IsIncluded).ToList();

        var identifierSubs = BuildIdentifierSubstitutions(
            valueBindings, merge, finalRows);

        var rewrittenCalcs = RewriteCalcBindings(
            calcBindings, activeSheet, keptRows, identifierSubs);

        var synthesisedLet = BuildSynthesisedLet(
            formula, activeSheet, keptRows, rewrittenCalcs,
            true, parsed.Body, identifierSubs);

        return new RefactorResult(
            formula,
            keptRows.Select(r => r.Row).ToList(),
            rewrittenCalcs,
            synthesisedLet);
    }

    private static MergeResult MergeValueBindings(
        IReadOnlyList<LetBinding> valueBindings,
        string activeSheet)
    {
        var survivors = new List<LetBinding>();
        var survivorByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var survivorByKey = new Dictionary<string, LetBinding>(StringComparer.Ordinal);
        var mergedFrom = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var vb in valueBindings)
        {
            var canon = CanonicalRhsKey(vb.RhsText, activeSheet);
            if (survivorByKey.TryGetValue(canon, out var existing))
            {
                survivorByName[vb.Name] = existing.Name;
                if (!mergedFrom.TryGetValue(existing.Name, out var list))
                {
                    list = [];
                    mergedFrom[existing.Name] = list;
                }

                list.Add(vb.Name);
            }
            else
            {
                survivors.Add(vb);
                survivorByName[vb.Name] = vb.Name;
                survivorByKey[canon] = vb;
            }
        }

        return new MergeResult(
            survivors,
            survivorByName,
            mergedFrom.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<string>)kv.Value,
                StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Canonical key for value-binding merge. A single-ref RHS canonicalises
    ///     to its <see cref="FormulaRef" />'s sheet-qualified display form
    ///     (so <c>A1</c> and <c>$A$1</c> and <c>Sheet1!A1</c> on Sheet1 all
    ///     collapse); anything else falls back to the trimmed literal text
    ///     (numeric / string / boolean / bare identifier RHSes compare by
    ///     value via string equality after trim).
    /// </summary>
    private static string CanonicalRhsKey(string rhsText, string activeSheet)
    {
        var fr = TryParseSingleRef(rhsText, activeSheet);
        if (fr != null)
            return "ref:" + BuildExtractedKey(fr, activeSheet);
        return "lit:" + rhsText.Trim();
    }

    // ---------------- identifier substitutions ----------------

    /// <summary>
    ///     Builds the <c>originalName → replacement</c> map applied to calc
    ///     bindings and the body. Three sources feed it:
    ///     <list type="bullet">
    ///         <item>
    ///             Merge survivors that were dropped by the user: the
    ///             original RHS text replaces every reference to the
    ///             survivor's original name (and to each merged-away
    ///             name).
    ///         </item>
    ///         <item>
    ///             Merge survivors that were kept but renamed: the new name
    ///             replaces every reference to the original name and to
    ///             each merged-away name.
    ///         </item>
    ///         <item>
    ///             Merge survivors that were kept with their original name:
    ///             references to merged-away names alone need rewriting to
    ///             the survivor's name; references to the survivor stay
    ///             as-is.
    ///         </item>
    ///     </list>
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildIdentifierSubstitutions(
        IReadOnlyList<LetBinding> originalValueBindings,
        MergeResult merge,
        IReadOnlyList<MaterialisedRow> finalRows)
    {
        var rowByKey = finalRows.ToDictionary(r => r.Row.Key, StringComparer.Ordinal);
        var subs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ovb in originalValueBindings)
        {
            var survivorName = merge.SurvivorByOriginalName[ovb.Name];
            var survivorRow = rowByKey[BuildExistingLetKey(survivorName)];

            string replacement;
            if (!survivorRow.IsIncluded)
            {
                // Dropped — inline the survivor's original RHS text.
                replacement = survivorRow.Row.Rhs;
            }
            else
            {
                replacement = survivorRow.Row.Name;
                // No-op when the survivor's name didn't change AND this row
                // is itself the survivor — body/calcs already reference it
                // by that name.
                if (string.Equals(replacement, ovb.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            subs[ovb.Name] = replacement;
        }

        return subs;
    }

    // ---------------- ref extraction (calc bindings + body) ----------------

    private static void ExtractNewRefs(
        string text,
        string activeSheet,
        HashSet<FormulaRef> existingRefs,
        HashSet<FormulaRef> seenNew,
        HashSet<string> usedNames,
        ref int autoNameIndex,
        List<RefactorInputRow> sink)
    {
        foreach (var fr in CellRefExtractor.Extract(text, activeSheet))
        {
            if (existingRefs.Contains(fr)) continue;
            if (!seenNew.Add(fr)) continue;

            string name;
            while (true)
            {
                name = "input" + autoNameIndex;
                autoNameIndex++;
                if (!usedNames.Contains(name))
                    break;
            }

            usedNames.Add(name);

            sink.Add(new RefactorInputRow(
                BuildExtractedKey(fr, activeSheet),
                fr,
                name,
                fr.DisplayAddress(activeSheet),
                RefactorRowOrigin.Extracted));
        }
    }

    private static List<MaterialisedRow> ApplyRowStates(
        IReadOnlyList<RefactorInputRow> defaultRows,
        IReadOnlyList<RefactorRowState>? rowStates)
    {
        if (rowStates is null)
            return defaultRows.Select(r => new MaterialisedRow(r, true)).ToList();

        var byKey = defaultRows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var ordered = new List<MaterialisedRow>(rowStates.Count);
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rs in rowStates)
        {
            if (!byKey.TryGetValue(rs.Key, out var row)) continue;
            consumed.Add(rs.Key);
            // The dialog owns the binding name; the engine just forwards it
            // (validation is enforced dialog-side).
            ordered.Add(new MaterialisedRow(
                row with { Name = rs.Name },
                rs.Include));
        }

        // Any rows the rowStates didn't mention (shouldn't happen in
        // practice — the dialog tracks every row) are appended at the end
        // in their default order, kept included so they aren't silently
        // lost.
        foreach (var row in defaultRows)
            if (!consumed.Contains(row.Key))
                ordered.Add(new MaterialisedRow(row, true));

        return ordered;
    }

    // ---------------- rewrite & synthesis ----------------

    private static IReadOnlyList<RefactorCalcBindingRow> RewriteCalcBindings(
        IReadOnlyList<LetBinding> calcBindings,
        string activeSheet,
        IReadOnlyList<MaterialisedRow> keptRows,
        IReadOnlyDictionary<string, string> identifierSubs)
    {
        if (calcBindings.Count == 0)
            return Array.Empty<RefactorCalcBindingRow>();

        var refLookup = BuildFormulaRefLookup(keptRows);
        var result = new List<RefactorCalcBindingRow>(calcBindings.Count);
        foreach (var c in calcBindings)
        {
            var rewritten = RewriteText(c.RhsText, activeSheet, refLookup, identifierSubs);
            result.Add(new RefactorCalcBindingRow(c.Name, rewritten));
        }

        return result;
    }

    private static IReadOnlyDictionary<FormulaRef, string> BuildFormulaRefLookup(
        IReadOnlyList<MaterialisedRow> keptRows)
    {
        var dict = new Dictionary<FormulaRef, string>();
        foreach (var r in keptRows)
            if (r.Row.Source is { } src)
                dict[src] = r.Row.Name;
        return dict;
    }

    /// <summary>
    ///     Runs the cell-ref rewrite (replacing in-scope <see cref="FormulaRef" />s
    ///     with their kept binding names) followed by the identifier
    ///     rewrite (renaming merged-away binding names or inlining the RHS
    ///     of dropped existing-LET value bindings).
    /// </summary>
    private static string RewriteText(
        string text,
        string activeSheet,
        IReadOnlyDictionary<FormulaRef, string> refLookup,
        IReadOnlyDictionary<string, string> identifierSubs)
    {
        var afterRefs = refLookup.Count > 0
            ? CellRefExtractor.Rewrite(text, activeSheet, refLookup)
            : text;
        if (identifierSubs.Count == 0)
            return afterRefs;
        return RewriteIdentifiers(afterRefs, identifierSubs);
    }

    private static string RewriteIdentifiers(
        string text,
        IReadOnlyDictionary<string, string> subs)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                var end = SkipString(text, i);
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }

            // Single-quoted sheet name / external workbook qualifier:
            // copy through verbatim so identifiers inside the quotes
            // aren't substituted.
            if (text[i] == '\'')
            {
                var end = SkipSingleQuoted(text, i);
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }

            var nextQuote = IndexOfAny(text, i, '"', '\'');
            var segEnd = nextQuote < 0 ? text.Length : nextQuote;
            var segment = text.Substring(i, segEnd - i);
            var rewritten = IdentifierTokenPattern.Replace(segment, m =>
                subs.TryGetValue(m.Value, out var replacement) ? replacement : m.Value);
            sb.Append(rewritten);
            i = segEnd;
        }

        return sb.ToString();
    }

    private static int IndexOfAny(string text, int start, char a, char b)
    {
        for (var i = start; i < text.Length; i++)
            if (text[i] == a || text[i] == b)
                return i;
        return -1;
    }

    private static int SkipString(string text, int openQuoteIndex)
    {
        var i = openQuoteIndex + 1;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    private static int SkipSingleQuoted(string text, int openQuoteIndex)
    {
        var i = openQuoteIndex + 1;
        while (i < text.Length)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    // ---------------- LET synthesis ----------------

    private static string BuildSynthesisedLet(
        string originalFormula,
        string activeSheet,
        IReadOnlyList<MaterialisedRow> keptInputs,
        IReadOnlyList<RefactorCalcBindingRow> rewrittenCalcs,
        bool isExistingLet,
        string? body,
        IReadOnlyDictionary<string, string>? identifierSubstitutions)
    {
        // No work to do — nothing extracted and nothing merged.
        if (!isExistingLet && keptInputs.Count == 0)
            return originalFormula;

        // An existing LET with zero kept inputs AND zero calc bindings
        // collapses to its body — the dialog still synthesises a valid
        // formula so the user can save (matches PR 1's "all dropped"
        // behaviour).
        if (isExistingLet && keptInputs.Count == 0 && rewrittenCalcs.Count == 0)
        {
            var rewrittenBody = RewriteText(
                body!, activeSheet,
                BuildFormulaRefLookup(keptInputs),
                identifierSubstitutions ?? new Dictionary<string, string>());
            return "=" + rewrittenBody;
        }

        var bindings = keptInputs
            .Select(r => (r.Row.Name, Value: r.Row.Rhs))
            .Concat(rewrittenCalcs.Select(c => (c.Name, c.RewrittenRhs)))
            .ToList();

        string bodyText;
        if (isExistingLet)
        {
            bodyText = RewriteText(
                body!, activeSheet,
                BuildFormulaRefLookup(keptInputs),
                identifierSubstitutions ?? new Dictionary<string, string>());
        }
        else
        {
            // Non-LET: the original formula's content (sans leading '=') is
            // the LET body, with cell refs rewritten to kept binding names.
            bodyText = CellRefExtractor.Rewrite(
                StripLeadingEquals(originalFormula),
                activeSheet,
                BuildFormulaRefLookup(keptInputs));
        }

        var sb = new StringBuilder();
        sb.Append('=');
        FormulaFormatter.AppendLet(sb, 0, bindings, bodyText);
        return sb.ToString();
    }

    // ---------------- keys & helpers ----------------

    private static string BuildExtractedKey(FormulaRef fr, string activeSheet)
    {
        // DisplayAddress with the active sheet as host is unique per
        // FormulaRef (spill suffix and range tail included), so it doubles
        // as a stable string key the dialog can echo back via RefactorRowState.
        return "ref:" + fr.DisplayAddress(activeSheet);
    }

    private static string BuildExistingLetKey(string bindingName)
    {
        return "name:" + bindingName;
    }

    /// <summary>
    ///     If <paramref name="rhsText" /> consists of exactly one cell-shaped
    ///     ref (single cell, range, or spill — optionally sheet- or
    ///     workbook-qualified) and nothing else, returns that ref;
    ///     otherwise null. Used to decide whether a value binding's RHS
    ///     can be merged by canonical FormulaRef (vs literal-string fallback)
    ///     and to populate <see cref="RefactorInputRow.Source" />.
    /// </summary>
    private static FormulaRef? TryParseSingleRef(string rhsText, string activeSheet)
    {
        if (string.IsNullOrWhiteSpace(rhsText)) return null;
        var refs = CellRefExtractor.Extract(rhsText, activeSheet);
        if (refs.Count != 1) return null;
        // Verify the RHS is JUST that ref (no surrounding tokens or
        // operators) by rewriting it to a sentinel and checking the
        // remainder is whitespace.
        var sentinel = "";
        var probe = CellRefExtractor.Rewrite(
            rhsText, activeSheet, new Dictionary<FormulaRef, string> { [refs[0]] = sentinel });
        return probe.Trim() == sentinel ? refs[0] : null;
    }

    private static string StripLeadingEquals(string formula)
    {
        var trimmed = formula.TrimStart();
        return trimmed.StartsWith("=", StringComparison.Ordinal) ? trimmed[1..] : trimmed;
    }

    // ---------------- merge ----------------

    private record MergeResult(
        IReadOnlyList<LetBinding> Survivors,
        IReadOnlyDictionary<string, string> SurvivorByOriginalName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> MergedFromBySurvivor);

    // ---------------- rowState materialisation ----------------

    /// <summary>
    ///     Pairs each engine-derived <see cref="RefactorInputRow" /> with
    ///     the dialog's user-edited state. Captures the row's "current"
    ///     Name and <see cref="IsIncluded" /> flag so the
    ///     rewrite + synthesis passes can read them without re-keying
    ///     against the input rowStates dictionary.
    /// </summary>
    private sealed record MaterialisedRow(RefactorInputRow Row, bool IsIncluded);
}