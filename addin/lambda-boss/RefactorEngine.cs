using System.Text;
using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     Spec 0008 — the <c>/Refactor</c> entry point. Takes a formula and
///     the active cell's sheet, hoists every cell ref / range into its own
///     LET binding, and emits a tidy <c>=LET(...)</c>.
///     PR 2 added existing-LET handling (merge value bindings, extract
///     refs from calc bindings and body, keep calc bindings in source
///     order).
///     PR 3 adds the promotable section: external refs and workbook /
///     worksheet-scoped defined names (excluding LAMBDA names) become
///     default-off rows in a separate section the dialog renders below
///     the inputs. When the user toggles Promote, the dialog re-issues
///     the row state with the promotable's Key in <see cref="RefactorRowState" />;
///     the engine recognises the <c>extref:</c> / <c>named:</c> Key prefix
///     and materialises the promoted row as an input binding alongside
///     the extracted ones. The rewrite step uses a second-pass identifier
///     rewriter (separate from <see cref="CellRefExtractor.Rewrite" />) for
///     promoted named ranges, so the bare identifier is swapped for the
///     binding name while leaving cell-shaped tokens, sheet qualifiers,
///     and strings alone.
///     The engine re-derives merge state and promotable state on every
///     <see cref="Recompute" /> call — the dialog only carries name /
///     Include / order / Promote-by-membership for each row via
///     <see cref="RefactorRowState" />, not the underlying merge graph
///     or named-range catalogue, so the engine has to be re-run anchored
///     on the source formula text and the live workbook-name lookup.
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

    // Candidate-identifier walker used to find named-range references.
    // Same identifier shape as IdentifierTokenPattern but with a trailing
    // negative lookahead for '(' (function calls — SUM, MAX, etc.) and
    // '!' (sheet qualifiers — Sheet1! is the lead-in to a cell ref, not
    // a defined-name reference). The lookbehind class mirrors
    // CellRefExtractor's so we don't pick up the cell tail of a
    // sheet-qualified ref.
    private static readonly Regex CandidateIdentifierPattern = new(
        @"(?<![A-Za-z0-9_.!:'\]#?])[A-Za-z_][A-Za-z0-9_.?]*(?![A-Za-z0-9_.?(!])",
        RegexOptions.CultureInvariant);

    // Numeric-literal matcher (PR 4), anchored at the scan position via \G.
    // Accepts an optional integer/decimal mantissa with an optional
    // scientific exponent (the exponent carries its own sign — we never
    // capture a leading +/- because that's an operator in formula text, so
    // `A1*-5` correctly promotes `5` and keeps the `-` inline). The
    // lookbehind rejects digits sitting inside an identifier (`input1`); the
    // lookahead rejects a trailing identifier char so we don't truncate a
    // longer token.
    private static readonly Regex NumericLiteralPattern = new(
        @"\G(?<![A-Za-z0-9_.])(?:\d+(?:\.\d+)?|\.\d+)(?:[eE][+-]?\d+)?(?![A-Za-z0-9_.])",
        RegexOptions.CultureInvariant);

    // Boolean-literal matcher (PR 4). Case-insensitive TRUE / FALSE that
    // isn't part of a larger identifier and isn't a function call (`TRUE(`)
    // or sheet qualifier (`TRUE!`).
    private static readonly Regex BooleanLiteralPattern = new(
        @"\G(?<![A-Za-z0-9_.])(?:TRUE|FALSE)(?![A-Za-z0-9_.(!])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    ///     Runs the initial refactor over <paramref name="formula" />.
    ///     Existing LETs are parsed and merged/extracted per spec 0008;
    ///     non-LET formulas have their cell refs hoisted in first-seen
    ///     order. The optional <paramref name="context" /> supplies the
    ///     workbook-name catalogue for PR 3's promotable section; pass
    ///     null when only extracted-ref / existing-LET behaviour is
    ///     needed (e.g. unit tests for PR 1/2 paths).
    /// </summary>
    public static RefactorResult Refactor(
        string formula,
        string activeSheet,
        IWorkbookContext? context = null)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));
        if (activeSheet is null) throw new ArgumentNullException(nameof(activeSheet));

        return RefactorInternal(formula, activeSheet, null, context);
    }

    /// <summary>
    ///     Re-runs the refactor with the dialog's per-row state.
    ///     <paramref name="rowStates" /> is the full set of rows in
    ///     user-chosen order; rows with <see cref="RefactorRowState.Include" />
    ///     = false are dropped. For non-LET formulas a dropped row leaves
    ///     its source cell ref inline (matching PR 1). For existing LETs a
    ///     dropped value binding is removed and its name is inlined back to
    ///     the binding's original RHS text wherever it appeared.
    ///     PR 3: row states whose <see cref="RefactorRowState.Key" /> matches
    ///     a default promotable (external ref or named range) are treated
    ///     as promotions — the row materialises as an input binding instead
    ///     of staying in the promotable section.
    /// </summary>
    public static RefactorResult Recompute(
        string formula,
        string activeSheet,
        IReadOnlyList<RefactorRowState> rowStates,
        IWorkbookContext? context = null)
    {
        if (formula is null) throw new ArgumentNullException(nameof(formula));
        if (activeSheet is null) throw new ArgumentNullException(nameof(activeSheet));
        if (rowStates is null) throw new ArgumentNullException(nameof(rowStates));

        return RefactorInternal(formula, activeSheet, rowStates, context);
    }

    private static RefactorResult RefactorInternal(
        string formula,
        string activeSheet,
        IReadOnlyList<RefactorRowState>? rowStates,
        IWorkbookContext? context)
    {
        if (LetParser.IsLetFormula(formula))
            return RefactorExistingLet(formula, activeSheet, rowStates, context);
        return RefactorNonLet(formula, activeSheet, rowStates, context);
    }

    // ---------------- non-LET path ----------------

    private static RefactorResult RefactorNonLet(
        string formula,
        string activeSheet,
        IReadOnlyList<RefactorRowState>? rowStates,
        IWorkbookContext? context)
    {
        var extracted = CellRefExtractor.Extract(formula, activeSheet);
        var allRefs = CellRefExtractor.ExtractAll(formula, activeSheet);

        // Separate in-scope refs (always-input) from external refs (promotable).
        var inScopeRefs = new List<FormulaRef>();
        var externalRefs = new List<FormulaRef>();
        foreach (var fr in extracted)
            (fr.IsExternal ? externalRefs : inScopeRefs).Add(fr);

        var defaultInputRows = new List<RefactorInputRow>(inScopeRefs.Count);
        var nameIndex = 1;
        foreach (var fr in inScopeRefs)
        {
            defaultInputRows.Add(new RefactorInputRow(
                BuildExtractedKey(fr, activeSheet),
                fr,
                $"input{nameIndex}",
                fr.DisplayAddress(activeSheet),
                RefactorRowOrigin.Extracted));
            nameIndex++;
        }

        // Default promotables: external refs found anywhere in the
        // formula + workbook-named identifiers (non-LAMBDA) found in the
        // formula body.
        var defaultPromotables = BuildDefaultPromotables(
            new[] { formula },
            allRefs,
            activeSheet,
            context,
            ReadOnlyEmptySet<string>.Instance);

        var promotableLookup = BuildPromotableLookup(defaultPromotables, externalRefs, activeSheet);

        var (materialised, remainingPromotables) = MaterialiseRowStates(
            defaultInputRows,
            defaultPromotables,
            promotableLookup,
            rowStates,
            nextAutoNameIndex: nameIndex,
            existingBindingNames: ReadOnlyEmptySet<string>.Instance);

        var keptRows = materialised.Where(r => r.IsIncluded).ToList();

        var identifierSubs = BuildPromotedNamedRangeSubstitutions(keptRows);
        var literalSubs = BuildPromotedLiteralSubstitutions(keptRows);

        var synthesisedLet = BuildSynthesisedLet(
            formula, activeSheet, keptRows, Array.Empty<RefactorCalcBindingRow>(),
            false, null, identifierSubs, literalSubs);

        return new RefactorResult(
            formula,
            keptRows.Select(r => r.Row).ToList(),
            Array.Empty<RefactorCalcBindingRow>(),
            synthesisedLet,
            remainingPromotables);
    }

    // ---------------- existing-LET path ----------------

    private static RefactorResult RefactorExistingLet(
        string formula,
        string activeSheet,
        IReadOnlyList<RefactorRowState>? rowStates,
        IWorkbookContext? context)
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
                Array.Empty<RefactorPromotableRow>(),
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
        //   auto-named Extracted row. External refs are routed to the
        //   promotables list instead.
        var existingRefs = new HashSet<FormulaRef>();
        foreach (var survivor in merge.Survivors)
        {
            var fr = TryParseSingleRef(survivor.RhsText, activeSheet);
            if (fr != null) existingRefs.Add(fr);
        }

        var existingBindingNames = new HashSet<string>(
            parsed.Bindings.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

        var usedNames = new HashSet<string>(existingBindingNames, StringComparer.OrdinalIgnoreCase);

        var newRows = new List<RefactorInputRow>();
        var seenNew = new HashSet<FormulaRef>();
        var externalRefsFound = new List<FormulaRef>();
        var autoNameIndex = 1;

        foreach (var calc in calcBindings)
            ExtractNewRefs(
                calc.RhsText, activeSheet, existingRefs, seenNew, usedNames,
                ref autoNameIndex, newRows, externalRefsFound);
        ExtractNewRefs(
            parsed.Body, activeSheet, existingRefs, seenNew, usedNames,
            ref autoNameIndex, newRows, externalRefsFound);

        // For promotable counting we want EVERY occurrence (including
        // duplicates), so walk a second time without the dedupe filter.
        var allRefsInScope = new List<FormulaRef>();
        foreach (var calc in calcBindings)
            allRefsInScope.AddRange(CellRefExtractor.ExtractAll(calc.RhsText, activeSheet));
        allRefsInScope.AddRange(CellRefExtractor.ExtractAll(parsed.Body, activeSheet));

        var promotableScopes = new List<string>(calcBindings.Count + 1);
        foreach (var calc in calcBindings)
            promotableScopes.Add(calc.RhsText);
        promotableScopes.Add(parsed.Body);

        var defaultPromotables = BuildDefaultPromotables(
            promotableScopes, allRefsInScope, activeSheet, context, existingBindingNames);

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

        // Step 4 — combine defaults and apply rowStates (rename / include /
        // order / promote).
        var defaultRows = existingValueRows.Concat(newRows).ToList();

        var promotableLookup = BuildPromotableLookup(
            defaultPromotables, externalRefsFound, activeSheet);

        var (materialised, remainingPromotables) = MaterialiseRowStates(
            defaultRows,
            defaultPromotables,
            promotableLookup,
            rowStates,
            nextAutoNameIndex: autoNameIndex,
            existingBindingNames: existingBindingNames);

        // Step 5 — assemble the rewrite maps and synthesise.
        var keptRows = materialised.Where(r => r.IsIncluded).ToList();

        var identifierSubs = BuildIdentifierSubstitutions(
            valueBindings, merge, materialised);

        // Promoted named ranges add their identifier-to-binding entries
        // on top of the existing-LET ones; the dictionaries don't overlap
        // (promotable identifiers were excluded from defaults if they
        // matched an existing binding name).
        foreach (var kv in BuildPromotedNamedRangeSubstitutions(keptRows))
            identifierSubs[kv.Key] = kv.Value;

        var literalSubs = BuildPromotedLiteralSubstitutions(keptRows);

        var rewrittenCalcs = RewriteCalcBindings(
            calcBindings, activeSheet, keptRows, identifierSubs, literalSubs);

        var synthesisedLet = BuildSynthesisedLet(
            formula, activeSheet, keptRows, rewrittenCalcs,
            true, parsed.Body, identifierSubs, literalSubs);

        return new RefactorResult(
            formula,
            keptRows.Select(r => r.Row).ToList(),
            rewrittenCalcs,
            synthesisedLet,
            remainingPromotables);
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
    private static Dictionary<string, string> BuildIdentifierSubstitutions(
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

    /// <summary>
    ///     Builds the identifier → binding-name subs for promoted named
    ///     ranges among <paramref name="keptRows" />. Promoted external
    ///     refs aren't included here — they ride the FormulaRef rewrite
    ///     path via <see cref="BuildFormulaRefLookup" />.
    /// </summary>
    private static Dictionary<string, string> BuildPromotedNamedRangeSubstitutions(
        IReadOnlyList<MaterialisedRow> keptRows)
    {
        var subs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in keptRows)
        {
            if (r.Row.Origin != RefactorRowOrigin.PromotedNamedRange) continue;
            // RHS for a promoted named range is the identifier itself.
            subs[r.Row.Rhs] = r.Row.Name;
        }

        return subs;
    }

    /// <summary>
    ///     Builds the literal-value → binding-name subs for promoted literals
    ///     among <paramref name="keptRows" />. The key is the parsed-value
    ///     key (so every occurrence of the value rewrites, regardless of
    ///     spelling) — re-derived from the row's RHS (the original token
    ///     text) by re-tokenising it.
    /// </summary>
    private static Dictionary<string, string> BuildPromotedLiteralSubstitutions(
        IReadOnlyList<MaterialisedRow> keptRows)
    {
        var subs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in keptRows)
        {
            if (r.Row.Origin != RefactorRowOrigin.PromotedLiteral) continue;
            var key = LiteralValueKeyOf(r.Row.Rhs);
            if (key != null)
                subs[key] = r.Row.Name;
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
        List<RefactorInputRow> sink,
        List<FormulaRef> externalSink)
    {
        foreach (var fr in CellRefExtractor.Extract(text, activeSheet))
        {
            if (fr.IsExternal)
            {
                externalSink.Add(fr);
                continue;
            }

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

    // ---------------- promotable building & lookup ----------------

    /// <summary>
    ///     Builds the default-off promotable rows: external refs (deduped,
    ///     with occurrence counts) and workbook-name identifiers that
    ///     aren't LAMBDA names and aren't masked by an existing LET
    ///     binding name.
    /// </summary>
    private static List<RefactorPromotableRow> BuildDefaultPromotables(
        IReadOnlyList<string> identifierScopes,
        IReadOnlyList<FormulaRef> allRefs,
        string activeSheet,
        IWorkbookContext? context,
        ISet<string> excludeBindingNames)
    {
        var promotables = new List<RefactorPromotableRow>();

        // External refs — first-occurrence order, count all matches.
        var externalCounts = new Dictionary<string, (FormulaRef Ref, int Count)>(StringComparer.Ordinal);
        var externalOrder = new List<string>();
        foreach (var fr in allRefs)
        {
            if (!fr.IsExternal) continue;
            var key = BuildExternalRefKey(fr, activeSheet);
            if (externalCounts.TryGetValue(key, out var existing))
                externalCounts[key] = (existing.Ref, existing.Count + 1);
            else
            {
                externalCounts[key] = (fr, 1);
                externalOrder.Add(key);
            }
        }

        foreach (var key in externalOrder)
        {
            var (fr, count) = externalCounts[key];
            promotables.Add(new RefactorPromotableRow(
                key,
                RefactorPromotableKind.ExternalRef,
                fr.DisplayAddress(activeSheet),
                count));
        }

        // Named-range candidates — only when a workbook context supplies
        // names. First-occurrence-spelling wins for display; dedupe is
        // case-insensitive (Excel name resolution is case-insensitive).
        if (context is { WorkbookNames.Count: > 0 })
        {
            var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var firstSpelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var nameOrder = new List<string>(); // canonical (first-found) spellings

            foreach (var scope in identifierScopes)
            foreach (var identifier in WalkCandidateIdentifiers(scope))
            {
                if (excludeBindingNames.Contains(identifier)) continue;
                if (!context.WorkbookNames.TryGetValue(identifier, out var refersTo)) continue;
                if (LambdaSignatureParser.IsLambdaFormula(refersTo)) continue;

                if (!nameCounts.ContainsKey(identifier))
                {
                    firstSpelling[identifier] = identifier;
                    nameOrder.Add(identifier);
                }

                nameCounts[identifier] = nameCounts.TryGetValue(identifier, out var c) ? c + 1 : 1;
            }

            foreach (var spelling in nameOrder)
            {
                promotables.Add(new RefactorPromotableRow(
                    BuildNamedRangeKey(spelling),
                    RefactorPromotableKind.NamedRange,
                    spelling,
                    nameCounts[spelling]));
            }
        }

        // Literals — numeric / string / boolean, deduped by parsed value
        // (not spelling, so `0.20` and `0.2` collapse). First occurrence's
        // text and position win for display + order. Independent of the
        // workbook context: literals are purely textual.
        var litCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var litFirstText = new Dictionary<string, string>(StringComparer.Ordinal);
        var litOrder = new List<string>(); // value keys, first-seen order
        foreach (var scope in identifierScopes)
        foreach (var tok in WalkLiterals(scope))
        {
            if (!litCounts.ContainsKey(tok.ValueKey))
            {
                litOrder.Add(tok.ValueKey);
                litFirstText[tok.ValueKey] = tok.Text;
            }

            litCounts[tok.ValueKey] = litCounts.TryGetValue(tok.ValueKey, out var c) ? c + 1 : 1;
        }

        foreach (var vk in litOrder)
        {
            promotables.Add(new RefactorPromotableRow(
                BuildLiteralKey(vk),
                RefactorPromotableKind.Literal,
                litFirstText[vk],
                litCounts[vk]));
        }

        return promotables;
    }

    /// <summary>
    ///     Walks <paramref name="text" /> for bare-identifier tokens that
    ///     aren't immediately followed by '(' (function call) or '!'
    ///     (sheet qualifier). String literals and single-quoted sheet/
    ///     workbook qualifiers are skipped wholesale.
    /// </summary>
    private static IEnumerable<string> WalkCandidateIdentifiers(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                i = SkipString(text, i);
                continue;
            }

            if (text[i] == '\'')
            {
                i = SkipSingleQuoted(text, i);
                continue;
            }

            var nextQuote = IndexOfAny(text, i, '"', '\'');
            var segEnd = nextQuote < 0 ? text.Length : nextQuote;
            var segment = text.Substring(i, segEnd - i);

            foreach (Match m in CandidateIdentifierPattern.Matches(segment))
                yield return m.Value;

            i = segEnd;
        }
    }

    /// <summary>
    ///     Lookup the materialiser uses to turn a promoted promotable's
    ///     key into an input row. External refs carry their FormulaRef so
    ///     <see cref="BuildFormulaRefLookup" /> can register them for
    ///     cell-ref rewriting; named ranges carry only their identifier
    ///     text (the RHS).
    /// </summary>
    private static Dictionary<string, PromotedInfo> BuildPromotableLookup(
        IReadOnlyList<RefactorPromotableRow> defaultPromotables,
        IReadOnlyList<FormulaRef> externalRefs,
        string activeSheet)
    {
        var byKey = new Dictionary<string, PromotedInfo>(StringComparer.Ordinal);
        foreach (var p in defaultPromotables)
        {
            switch (p.Kind)
            {
                case RefactorPromotableKind.NamedRange:
                case RefactorPromotableKind.Literal:
                    byKey[p.Key] = new PromotedInfo(p, null);
                    break;
                case RefactorPromotableKind.ExternalRef:
                    var fr = externalRefs.FirstOrDefault(
                        r => BuildExternalRefKey(r, activeSheet) == p.Key);
                    if (fr != null)
                        byKey[p.Key] = new PromotedInfo(p, fr);
                    break;
            }
        }

        return byKey;
    }

    private sealed record PromotedInfo(RefactorPromotableRow Row, FormulaRef? ExternalSource);

    // ---------------- materialisation (rowState → kept rows) ----------------

    /// <summary>
    ///     Combines the engine-derived <paramref name="defaultInputRows" />
    ///     and <paramref name="defaultPromotables" /> with the dialog's
    ///     per-row state. Recognises three Key shapes in
    ///     <paramref name="rowStates" />:
    ///     <list type="bullet">
    ///         <item>
    ///             <c>ref:</c> (extracted cell ref) or <c>name:</c>
    ///             (existing-LET value binding) — already an input row;
    ///             update Name + Include.
    ///         </item>
    ///         <item>
    ///             <c>extref:</c> (promotable external ref) or
    ///             <c>named:</c> (promotable named range) — promotion;
    ///             materialise as an input row and remove from the
    ///             promotables list.
    ///         </item>
    ///     </list>
    ///     Promotable rows whose Key wasn't mentioned in rowStates remain
    ///     in the result's <see cref="RefactorResult.Promotables" />.
    /// </summary>
    private static (List<MaterialisedRow> Rows, List<RefactorPromotableRow> Promotables)
        MaterialiseRowStates(
            IReadOnlyList<RefactorInputRow> defaultInputRows,
            IReadOnlyList<RefactorPromotableRow> defaultPromotables,
            IReadOnlyDictionary<string, PromotedInfo> promotableLookup,
            IReadOnlyList<RefactorRowState>? rowStates,
            int nextAutoNameIndex,
            ISet<string> existingBindingNames)
    {
        if (rowStates is null)
            return (
                defaultInputRows.Select(r => new MaterialisedRow(r, true)).ToList(),
                defaultPromotables.ToList());

        var byInputKey = defaultInputRows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var ordered = new List<MaterialisedRow>(rowStates.Count);
        var consumedInputs = new HashSet<string>(StringComparer.Ordinal);
        var promotedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rs in rowStates)
        {
            if (byInputKey.TryGetValue(rs.Key, out var inputRow))
            {
                consumedInputs.Add(rs.Key);
                ordered.Add(new MaterialisedRow(inputRow with { Name = rs.Name }, rs.Include));
                continue;
            }

            if (promotableLookup.TryGetValue(rs.Key, out var promotable))
            {
                promotedKeys.Add(rs.Key);
                var name = string.IsNullOrEmpty(rs.Name)
                    ? AllocateAutoName(ref nextAutoNameIndex, existingBindingNames)
                    : rs.Name;
                ordered.Add(new MaterialisedRow(
                    BuildPromotedInputRow(promotable, name),
                    rs.Include));
            }
            // Unknown key (stale dialog state from a prior formula) — skip
            // silently rather than throwing; the dialog round-trips fresh
            // engine output on every change so stale keys shouldn't reach
            // here in practice.
        }

        foreach (var row in defaultInputRows)
            if (!consumedInputs.Contains(row.Key))
                ordered.Add(new MaterialisedRow(row, true));

        var remainingPromotables = defaultPromotables
            .Where(p => !promotedKeys.Contains(p.Key))
            .ToList();

        return (ordered, remainingPromotables);
    }

    private static RefactorInputRow BuildPromotedInputRow(PromotedInfo info, string name)
    {
        switch (info.Row.Kind)
        {
            case RefactorPromotableKind.NamedRange:
                return new RefactorInputRow(
                    info.Row.Key,
                    Source: null,
                    name,
                    info.Row.Token,
                    RefactorRowOrigin.PromotedNamedRange);
            case RefactorPromotableKind.ExternalRef:
                return new RefactorInputRow(
                    info.Row.Key,
                    info.ExternalSource,
                    name,
                    info.Row.Token,
                    RefactorRowOrigin.PromotedExternalRef);
            case RefactorPromotableKind.Literal:
                return new RefactorInputRow(
                    info.Row.Key,
                    Source: null,
                    name,
                    info.Row.Token,
                    RefactorRowOrigin.PromotedLiteral);
            default:
                throw new InvalidOperationException(
                    $"Unknown promotable kind: {info.Row.Kind}");
        }
    }

    private static string AllocateAutoName(ref int autoNameIndex, ISet<string> usedNames)
    {
        while (true)
        {
            var name = "input" + autoNameIndex;
            autoNameIndex++;
            if (!usedNames.Contains(name))
                return name;
        }
    }

    // ---------------- rewrite & synthesis ----------------

    private static IReadOnlyList<RefactorCalcBindingRow> RewriteCalcBindings(
        IReadOnlyList<LetBinding> calcBindings,
        string activeSheet,
        IReadOnlyList<MaterialisedRow> keptRows,
        IReadOnlyDictionary<string, string> identifierSubs,
        IReadOnlyDictionary<string, string> literalSubs)
    {
        if (calcBindings.Count == 0)
            return Array.Empty<RefactorCalcBindingRow>();

        var refLookup = BuildFormulaRefLookup(keptRows);
        var result = new List<RefactorCalcBindingRow>(calcBindings.Count);
        foreach (var c in calcBindings)
        {
            var rewritten = RewriteText(c.RhsText, activeSheet, refLookup, identifierSubs, literalSubs);
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
    ///     Runs, in order: the cell-ref rewrite (replacing in-scope
    ///     <see cref="FormulaRef" />s with their kept binding names), the
    ///     identifier rewrite (renaming merged-away binding names, inlining
    ///     the RHS of dropped existing-LET value bindings, or swapping a
    ///     promoted named range's identifier for its binding name), and
    ///     finally the literal rewrite (swapping promoted numeric / string /
    ///     boolean literals for their binding name). Literals run last so
    ///     the earlier passes — which skip string contents — never see a
    ///     promoted-string binding name as a candidate identifier.
    /// </summary>
    private static string RewriteText(
        string text,
        string activeSheet,
        IReadOnlyDictionary<FormulaRef, string> refLookup,
        IReadOnlyDictionary<string, string> identifierSubs,
        IReadOnlyDictionary<string, string> literalSubs)
    {
        var afterRefs = refLookup.Count > 0
            ? CellRefExtractor.Rewrite(text, activeSheet, refLookup)
            : text;
        var afterIds = identifierSubs.Count > 0
            ? RewriteIdentifiers(afterRefs, identifierSubs)
            : afterRefs;
        return literalSubs.Count > 0
            ? RewriteLiterals(afterIds, literalSubs)
            : afterIds;
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

    // ---------------- literal tokenizer & rewrite (PR 4) ----------------

    /// <summary>
    ///     One literal occurrence found by <see cref="WalkLiterals" />:
    ///     its <see cref="Start" /> / <see cref="Length" /> within the
    ///     scanned text, the original <see cref="Text" /> (preserving
    ///     spelling), and a value-based <see cref="ValueKey" /> used to
    ///     dedupe occurrences and to look up promotion substitutions
    ///     (so <c>0.20</c> and <c>0.2</c> share a key).
    /// </summary>
    private readonly record struct LiteralToken(
        int Start, int Length, string Text, string ValueKey);

    /// <summary>
    ///     Walks <paramref name="text" /> left-to-right yielding every
    ///     numeric / string / boolean literal occurrence in source order.
    ///     Cell-ref tokens are pre-masked (via
    ///     <see cref="CellRefExtractor.MatchSpans" />) so the row digits of
    ///     <c>A1</c> aren't surfaced as a numeric literal; single-quoted
    ///     sheet / workbook qualifiers are skipped wholesale. String
    ///     literals are recognised directly (with embedded <c>""</c>
    ///     un-escaped for the value key).
    /// </summary>
    private static IEnumerable<LiteralToken> WalkLiterals(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var masked = BuildCellRefMask(text);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '"')
            {
                var end = SkipString(text, i);
                // Only a properly-closed string is a literal; an unterminated
                // quote (malformed formula) is skipped, not promoted.
                if (end - i >= 2 && text[end - 1] == '"')
                {
                    var original = text.Substring(i, end - i);
                    yield return new LiteralToken(
                        i, end - i, original, "str:" + UnescapeString(original));
                }

                i = end;
                continue;
            }

            if (c == '\'')
            {
                i = SkipSingleQuoted(text, i);
                continue;
            }

            if (masked[i])
            {
                i++;
                continue;
            }

            var numeric = NumericLiteralPattern.Match(text, i);
            if (numeric.Success && numeric.Index == i
                && !SpanTouchesMask(masked, i, numeric.Length)
                && double.TryParse(
                    numeric.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var val))
            {
                yield return new LiteralToken(
                    i, numeric.Length, numeric.Value,
                    "num:" + val.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                i += numeric.Length;
                continue;
            }

            var boolean = BooleanLiteralPattern.Match(text, i);
            if (boolean.Success && boolean.Index == i
                && !SpanTouchesMask(masked, i, boolean.Length))
            {
                var isTrue = string.Equals(boolean.Value, "TRUE", StringComparison.OrdinalIgnoreCase);
                yield return new LiteralToken(
                    i, boolean.Length, boolean.Value, "bool:" + (isTrue ? "true" : "false"));
                i += boolean.Length;
                continue;
            }

            i++;
        }
    }

    /// <summary>
    ///     Rewrites promoted literals to their binding names. Each
    ///     occurrence whose value key is in <paramref name="subs" /> is
    ///     replaced; everything else (including un-promoted literals) is
    ///     left verbatim. Runs after the cell-ref and identifier passes;
    ///     it re-derives the cell-ref mask from the text it's handed, so
    ///     it's order-independent.
    /// </summary>
    private static string RewriteLiterals(
        string text, IReadOnlyDictionary<string, string> subs)
    {
        if (string.IsNullOrEmpty(text) || subs.Count == 0) return text;

        var sb = new StringBuilder(text.Length);
        var pos = 0;
        foreach (var tok in WalkLiterals(text))
        {
            if (!subs.TryGetValue(tok.ValueKey, out var name)) continue;
            sb.Append(text, pos, tok.Start - pos);
            sb.Append(name);
            pos = tok.Start + tok.Length;
        }

        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    /// <summary>
    ///     Re-tokenises a promoted literal row's RHS (its original token
    ///     text) back to the value key used in the substitution map.
    ///     Returns null when the text isn't a recognisable literal (which
    ///     shouldn't happen for a row the engine itself materialised).
    /// </summary>
    private static string? LiteralValueKeyOf(string token)
    {
        foreach (var t in WalkLiterals(token))
            return t.ValueKey;
        return null;
    }

    private static bool[] BuildCellRefMask(string text)
    {
        var mask = new bool[text.Length];
        foreach (var (start, end) in CellRefExtractor.MatchSpans(text))
            for (var k = start; k < end && k < mask.Length; k++)
                mask[k] = true;
        return mask;
    }

    private static bool SpanTouchesMask(bool[] mask, int start, int length)
    {
        for (var k = start; k < start + length && k < mask.Length; k++)
            if (mask[k])
                return true;
        return false;
    }

    /// <summary>
    ///     Strips the surrounding double quotes from a string literal and
    ///     un-escapes embedded <c>""</c> to a single <c>"</c>, giving the
    ///     value used for dedupe.
    /// </summary>
    private static string UnescapeString(string quoted)
    {
        if (quoted.Length < 2) return quoted;
        return quoted.Substring(1, quoted.Length - 2).Replace("\"\"", "\"");
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
        IReadOnlyDictionary<string, string>? identifierSubstitutions,
        IReadOnlyDictionary<string, string>? literalSubstitutions)
    {
        // No work to do — nothing extracted and nothing merged.
        if (!isExistingLet && keptInputs.Count == 0)
        {
            // PR 3: even without any cell-ref inputs we may need to emit a
            // LET if the user promoted a named range. The check above
            // already covers no-extracts AND no-promotables (the promoted
            // rows would be in keptInputs).
            return originalFormula;
        }

        // An existing LET with zero kept inputs AND zero calc bindings
        // collapses to its body — the dialog still synthesises a valid
        // formula so the user can save (matches PR 1's "all dropped"
        // behaviour).
        var idSubs = identifierSubstitutions ?? new Dictionary<string, string>();
        var litSubs = literalSubstitutions ?? new Dictionary<string, string>();

        if (isExistingLet && keptInputs.Count == 0 && rewrittenCalcs.Count == 0)
        {
            var rewrittenBody = RewriteText(
                body!, activeSheet,
                BuildFormulaRefLookup(keptInputs),
                idSubs, litSubs);
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
                idSubs, litSubs);
        }
        else
        {
            // Non-LET: the original formula's content (sans leading '=') is
            // the LET body, with cell refs, promoted named ranges (PR 3) and
            // promoted literals (PR 4) rewritten to kept binding names.
            bodyText = RewriteText(
                StripLeadingEquals(originalFormula),
                activeSheet,
                BuildFormulaRefLookup(keptInputs),
                idSubs, litSubs);
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

    private static string BuildExternalRefKey(FormulaRef fr, string activeSheet)
    {
        return "extref:" + fr.DisplayAddress(activeSheet);
    }

    private static string BuildNamedRangeKey(string identifier)
    {
        return "named:" + identifier;
    }

    private static string BuildLiteralKey(string valueKey)
    {
        return "lit:" + valueKey;
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
        var sentinel = "";
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

    /// <summary>
    ///     A tiny immutable empty-set sentinel used in the non-LET path
    ///     where there are no existing binding names to exclude. Saves an
    ///     allocation per call without changing semantics.
    /// </summary>
    private sealed class ReadOnlyEmptySet<T> : ISet<T>
    {
        public static readonly ReadOnlyEmptySet<T> Instance = new();
        public IEnumerator<T> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public int Count => 0;
        public bool IsReadOnly => true;
        public bool Contains(T item) => false;
        public void CopyTo(T[] array, int arrayIndex) { }
        public bool Add(T item) => throw new NotSupportedException();
        void System.Collections.Generic.ICollection<T>.Add(T item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Remove(T item) => throw new NotSupportedException();
        public void ExceptWith(IEnumerable<T> other) => throw new NotSupportedException();
        public void IntersectWith(IEnumerable<T> other) => throw new NotSupportedException();
        public bool IsProperSubsetOf(IEnumerable<T> other) => other?.Any() ?? false;
        public bool IsProperSupersetOf(IEnumerable<T> other) => false;
        public bool IsSubsetOf(IEnumerable<T> other) => true;
        public bool IsSupersetOf(IEnumerable<T> other) => !(other?.Any() ?? false);
        public bool Overlaps(IEnumerable<T> other) => false;
        public bool SetEquals(IEnumerable<T> other) => !(other?.Any() ?? false);
        public void SymmetricExceptWith(IEnumerable<T> other) => throw new NotSupportedException();
        public void UnionWith(IEnumerable<T> other) => throw new NotSupportedException();
    }
}
