# 0008 — Refactor to LET — Implementation Plan

## Overview

Build the `/Refactor` slash command in 4 sequenced PR-sized slices. **PR 1 is a tracer bullet** that closes the loop end-to-end on the simplest case — a non-LET formula with cell refs and ranges — so Tim can drop into Excel and exercise the feature from day one. Subsequent slices each add one capability: existing-LET handling, then the promotable section in two halves.

**Engine vs UI split.** Domain logic lives in pure C# classes under `addin/lambda-boss/`, mirroring `LetParser` / `LetToLambdaBuilder` / `GatherEngine`. `RefactorToLetWindow` and its row view-models stay in `addin/lambda-boss/UI/`. Code-behind is kept thin: it wires controls and forwards user input to the engine, which returns the synthesised LET text and the binding-list view-model. Workbook reads (for the defined-name lookup, only) are abstracted behind an `IWorkbookContext` interface so the engine is unit-testable without Excel.

**Reuse from spec 0005 (`/Gather`).** `/Gather` shipped a lot of infrastructure that maps directly:

| Component | What `/Refactor` reuses |
|---|---|
| `CellRefExtractor.Extract` | Walks formula text, returns unique `FormulaRef`s in first-seen order. Used as-is. |
| `CellRefExtractor.Rewrite` | Rewrites refs to binding names via a `FormulaRef → name` lookup. Used as-is. |
| `CellRef` / `FormulaRef` | Already case-insensitive on sheet equality. `DisplayAddress(hostSheet)` already renders bare for in-sheet refs and qualified otherwise — exactly the spec's sheet-qualifier dedupe behaviour. |
| `LetNameSanitizer` | Used directly for any user-entered renames; auto-names just emit `inputN` so no sanitisation needed there. |
| `LetParser` | Used to detect and parse existing LETs in PR 2. `IsLetFormula` / `Parse` / `SplitTopLevelCommas` / `FindMatchingClose` / `SkipString` semantics all apply. |
| `LambdaSignatureParser.IsLambdaFormula` | Used in PR 3 to exclude LAMBDA-bound workbook names from the promotable list. |
| `FormulaFormatter.AppendLet` | Used to render the synthesised LET. |
| `ExcelNameValidator` | Used for live validation of user-entered binding names in the dialog. |

**Shared-infra change in PR 1: spill flag on `FormulaRef`.** Today `CellRefExtractor`'s regex matches the trailing `#` on `A1#` (via the `(?<spill>\#)` group) but `BuildFormulaRef` discards it — both `A1` and `A1#` produce identical `FormulaRef`s. `/Refactor` needs them to dedupe as distinct bindings (per spec). The cleanest fix is to add a `bool IsSpilled` field to `FormulaRef`, include it in equality + hash, and populate it from the regex group. `/Gather` is not expected to break: its precedent walk uses `WalkedCell.Ref` (a `CellRef`, no spill flag) for graph identity, and the binding-RHS spill marker comes from `ICellSource.HasSpill`, not from `FormulaRef`. The new flag is purely informational for `/Gather`. Risk + mitigation called out in PR 1 below.

**Branching.** Implementation branches from `main`. Each slice is its own PR, merged into `main` in sequence — `/Gather` used a long-lived spec branch because there were 12 slices; with 4 here, merging straight to `main` is simpler and keeps history flatter. PR N branches from `main` after PR N-1 has merged.

## Files to Touch

### New (engine)

- `addin/lambda-boss/RefactorEngine.cs` — top-level entry. Takes formula text + active sheet + (PR 3+) workbook-name lookup, returns a `RefactorResult` (binding rows + promotable rows + synthesised LET + diagnostics).
- `addin/lambda-boss/RefactorTypes.cs` — shared records: `RefactorInputRow`, `RefactorPromotableRow`, `RefactorCalcBindingRow`, `RefactorRowState`, `RefactorResult`, `RefactorDiagnostic`, `IWorkbookContext`.

### New (UI)

- `addin/lambda-boss/UI/RefactorToLetWindow.xaml` — dialog: original formula header, inputs section, promotable section (PR 3+), read-only calc-binding section (PR 2+), preview pane, Save/Cancel.
- `addin/lambda-boss/UI/RefactorToLetWindow.xaml.cs` — wiring + row view-models with `INotifyPropertyChanged`. Mirrors `LetToLambdaWindow` / `GatherWindow`.

### New (command)

- `addin/lambda-boss/Commands/RefactorCommand.cs` — slash-command handler. Reads active cell, builds an `IWorkbookContext` adapter over the live workbook (PR 3+), runs the engine, opens `RefactorToLetWindow`, writes the LET back to the active cell on Save. Pattern mirrors `GatherCommand`.

### New (tests)

- `addin/lambda-boss.Tests/RefactorEngineTests.cs` — covers the public engine surface with an in-memory `IWorkbookContext` stub. Round-trip safety: every synthesised LET is asserted to parse cleanly via `LetParser.Parse` and feed cleanly into `LetToLambdaBuilder.Build`.
- `addin/lambda-boss.Tests/CellRefExtractorSpillTests.cs` (PR 1) — focused tests for the new `IsSpilled` propagation; complements existing `CellRefExtractorTests`.

### Modified

- `addin/lambda-boss/GatherTypes.cs` — add `bool IsSpilled` to `FormulaRef` (with equality + hash). PR 1.
- `addin/lambda-boss/CellRefExtractor.cs` — `BuildFormulaRef` reads the existing `(?<spill>\#)` group and sets `IsSpilled` accordingly. Range matches (where `col2` is set) always have `IsSpilled = false` (Excel has no `A1:B5#` syntax). PR 1.
- `addin/lambda-boss/UI/LambdaPopup.xaml.cs` — register the `/Refactor` command in `BuildCommandRegistry()`. PR 1.

## Order of Operations

Each step is one PR-sized issue. Each branches from `main` after the previous PR has merged.

1. **Tracer bullet — non-LET formula, refs + ranges + spill, end-to-end.**
   - Add `IsSpilled` to `FormulaRef` (equality + hash + `DisplayAddress` rendering); `CellRefExtractor.BuildFormulaRef` populates it. Verify existing `CellRefExtractorTests` and `GatherEngineTests` still pass — if any `/Gather` test fails because of double-counting `A1` vs `A1#`, fall back to keeping `FormulaRef` unchanged and tracking spill in a `RefactorEngine`-local key wrapper instead.
   - Register `/Refactor` slash command in `LambdaPopup.xaml.cs`. On an empty / literal active cell, popup closes silently (matches `/Gather`'s pattern). On a formula cell, the command runs.
   - `RefactorEngine.Refactor(string formula, string activeSheet)` walks the formula via `CellRefExtractor.Extract`, dedupes by `FormulaRef` (which now distinguishes spill from non-spill), assigns `inputN` auto-names, rewrites refs to binding names via `CellRefExtractor.Rewrite`, and emits the synthesised LET via `FormulaFormatter.AppendLet`.
   - **Existing LET refused** in this PR — `RefactorEngine` checks `LetParser.IsLetFormula` and returns a `RefactorDiagnostic` of kind `ExistingLet` with message *"Refactor on existing LET formulas is coming in PR 2 (spec 0008)."* The slash command shows the diagnostic via `MessageBox`. This refusal is removed in PR 2.
   - **External refs handled in-line for tracer**: in PR 1, external refs (`FormulaRef.IsExternal == true`) are extracted as inputs alongside same-workbook refs — same behaviour as today's `/Gather`. PR 3 moves them into the promotable section (default off).
   - `RefactorToLetWindow`: original-formula header (read-only), inputs section with rename + Include checkbox + reorder (Alt+Up/Down + drag, reusing `KeptRowReorderHandler` from `LetToLambdaWindow`), preview pane via `FormulaFormatter`, Save/Cancel. No promotable section, no calc-binding section.
   - Live name validation: `ExcelNameValidator` for shape, collision check across rows. Save disabled when invalid.
   - Tests (`RefactorEngineTests`): single cell ref, multiple refs deduped, range, spill-distinct (`A1` and `A1#` produce two bindings), sheet-qualified ref dedupe (`A1` and `Sheet1!A1` collapse when active sheet is `Sheet1`), cross-sheet ref stays distinct, rename through body, round-trip via `LetParser.Parse` + `LetToLambdaBuilder.Build`.
   - Manual Excel test: type `=IF(A1<10, IF(A1>2, SUM(B1:B5), 0), SUM(B2:B6))` into a cell, run `/Refactor`, save, confirm the resulting LET evaluates identically.

2. **Existing-LET handling — full refactor.**
   - Remove the PR 1 refusal. When `LetParser.IsLetFormula(formula)` is true, parse via `LetParser.Parse`.
   - **Treat existing value bindings as input rows.** Pre-populate the dialog with one input row per existing value binding (name, RHS, source = "existing LET binding"). Calculation bindings populate a separate read-only section.
   - **Walk for new refs**: run `CellRefExtractor.Extract` over each calculation binding's RHS and the body. Any ref not already represented by an existing value binding (matched by canonical `FormulaRef`) becomes a new input row with an auto-name. Auto-name allocator skips numbers already in use as existing binding names.
   - **Merge duplicate value bindings**: for each pair of value bindings with equivalent canonical RHS (compare via `FormulaRef` parsed from the RHS; falls back to literal string compare if the RHS isn't a single ref), keep the first binding's name, drop the others, rewrite references to the dropped name in calc bindings + body + later value bindings. The surviving row displays a `merged ← {oldName}` note.
   - **Rewrite all references**: after merge + extraction, run `CellRefExtractor.Rewrite` over each calc binding RHS and the body using the full `FormulaRef → bindingName` lookup.
   - **Reorder**: synthesised LET emits all value bindings first (in dialog order), then calc bindings (in original source order).
   - **ISOMITTED interaction (spec note)**: no special-case code; existing-LET rules above cover it naturally. Test case: feed in a LET that came from round-tripping an optional-param LAMBDA, verify the `IF(ISOMITTED(...))` wrappers survive and any cell refs inside the default expression get extracted.
   - Dialog gains a read-only "Calculation bindings" section under the inputs, showing each calc binding's name + rewritten RHS. Not reorderable, not droppable. Visually distinguish via a section header.
   - Tests: messy LET with duplicate value bindings (merge), refs inside calc bindings (extract), refs inside body (extract), ISOMITTED wrapper survives, already-tidy LET produces a no-op rewrite (preview matches original modulo whitespace), reorder of existing bindings via UI.
   - Manual Excel test: paste a messy LET (e.g. `=LET(a, A1, b, A1, getMax, MAX(B1:B5), IF(a<10, getMax, b))`), run `/Refactor`, save, confirm the resulting LET evaluates identically and has the expected shape.

3. **Promotable section: named ranges + external refs.**
   - New `IWorkbookContext` interface: `IReadOnlyDictionary<string, string> WorkbookNames { get; }` mapping name → `RefersTo`. Lookup is `OrdinalIgnoreCase`. The live adapter (`RefactorCommand`) populates this from `workbook.Names` and the active sheet's `worksheet.Names` (unioned) once when the dialog opens.
   - LAMBDA-name detection: a name with `RefersTo` starting with `=LAMBDA(` (via `LambdaSignatureParser.IsLambdaFormula`) is excluded from the promotable list. The engine treats those identifiers as function calls and leaves them inline.
   - Identifier tokenizer: a small new helper that walks formula text (skipping strings, mirroring `CellRefExtractor`'s `SkipString` rule) and yields `(identifier, position)` for each bare identifier that isn't a function call (i.e. not immediately followed by `(`). Used to find candidate defined-name references. Lookbehind / trailing guards match `CellRefExtractor`'s identifier-boundary rules to avoid splitting `Help?Foo` style names.
   - For each unique candidate identifier present in `WorkbookNames` and not a LAMBDA, emit a `RefactorPromotableRow(kind: NamedRange, token: identifier, occurrences: N, promote: false)`.
   - External refs: in PR 1 they extract as inputs by default. In this PR, change the engine so refs where `FormulaRef.IsExternal == true` emit as `RefactorPromotableRow(kind: ExternalRef, token: original-text, occurrences: N, promote: false)` instead — same default-off behaviour. Authors can still promote them.
   - Dialog gains a "Promote to input" section below the inputs (and above the calc-binding section). Each promotable row shows token text, occurrences count, and a Promote checkbox. Promoting moves the row up into the inputs section with an auto-name and editable name field; un-promoting moves it back.
   - Rewrite step: when a named range is promoted, the rewrite pass adds the identifier (with its positions) to a separate rewrite map that swaps the bare name for the binding name. Falls back to a second-pass identifier rewriter rather than extending `CellRefExtractor.Rewrite` (which is tightly coupled to A1-shaped patterns).
   - Tests: workbook-scoped named range promoted (rewrites everywhere; binding RHS is the original identifier text), worksheet-scoped named range promoted, LAMBDA name excluded from promotables, named range left un-promoted (stays inline, no binding), external ref defaults to promotable + promotable when toggled on, identifier-boundary correctness (`Help?Foo` not split).
   - Manual Excel test: define a workbook name `Tax_Rate = 0.2`, type `=A1 * Tax_Rate + Tax_Rate`, run `/Refactor`, verify `Tax_Rate` appears in the promotable section with occurrences=2; promote it, save, confirm the LET evaluates.

4. **Promotable section: literals + drop-input UX polish.**
   - Literal tokenizer: walks formula text (skipping strings and cell-ref tokens recognized by `CellRefExtractor`'s pattern; we can pre-mask those ranges before walking) and yields:
     - **Numeric literals**: optional sign + digits + optional decimal + optional scientific exponent. Dedupe by parsed numeric value.
     - **String literals**: the content between matched `"`s with embedded `""` un-escaped. Dedupe by string value.
     - **Boolean literals**: `TRUE` / `FALSE` (case-insensitive) when not followed by `(` (so we don't catch user-defined functions named `TRUE` — unlikely but defensive). Dedupe by boolean value.
   - For each unique literal value, emit a `RefactorPromotableRow(kind: Literal, token: original-text-of-first-occurrence, occurrences: N, promote: false)`. Promoting replaces every occurrence with the binding name; binding RHS is the original token text (preserves formatting like `0.20` vs `0.2`).
   - Drop-input UX (called out in spec): unticking Include on an extracted input row restores the original token everywhere it occurred. No warning shown even if that re-introduces duplication. Implementation: the engine's rewrite pass simply omits the dropped row from the rewrite map, and `CellRefExtractor.Rewrite` / the identifier rewriter / the literal rewriter leave unmapped tokens as-is.
   - Status text refinements: status bar at the bottom of the dialog shows merge notes, validation errors, and a summary count ("3 inputs, 2 promotable, 1 calculation binding").
   - Tests: numeric literal promoted (one `10` replaced everywhere), string literal promoted (with embedded `""`), boolean literal promoted, two distinct numerics stay distinct, drop-input restores literal token, drop-input restores named-range identifier, status text rendering.
   - Manual Excel test: type `=IF(A1 > 100, "high", IF(A1 > 50, "medium", "low")) + IF(B1 > 100, 1, 0)`, run `/Refactor`, verify the promotable section shows `100`, `"high"`, `"medium"`, `"low"`, `50`, `1`, `0`; promote `100` only, save, confirm both occurrences of `100` were replaced.

## Testing Approach

**Unit tests (xUnit, runs without Excel).** `RefactorEngine` is tested via an in-memory `IWorkbookContext` stub returning a fixed names → `RefersTo` map. No active-cell state is needed — the engine takes formula text + active sheet name as plain strings.

**Round-trip safety.** `RefactorEngineTests` asserts that every synthesised LET parses cleanly via `LetParser.Parse(...)` *and* feeds cleanly through `LetToLambdaBuilder.Build(...)` (with a default `LambdaGenerationRequest` keeping all inputs) for every fixture. This catches both LET-malformed output and LET-that-LetToLambdaBuilder-can't-stomach output early.

**View-model tests.** Row view-models (`RefactorInputRow`, `RefactorPromotableRow`) tested for `INotifyPropertyChanged` semantics and live validation. Pattern mirrors `LetToLambdaWindow`'s existing row tests.

**Manual Excel tests.** Each PR's issue body lists a 2-3 step Excel scenario Tim can run after a build to verify the slice (scenarios listed under each PR above).

The existing `addin/lambda-boss.AddinTests/` project covers ribbon-level Excel automation; no new add-in tests required for this feature unless the manual smoke checks find a gap the unit tests miss.

## Open Questions

All resolved at spec time. Two carried into this plan, both with clear tentative answers:

- **Worksheet-scoped names**: PR 3 unions `workbook.Names` and the active sheet's `worksheet.Names` into one lookup, so worksheet-scoped names behave the same as workbook-scoped ones. Confirmed during plan — the tentative answer in the spec is correct.
- **LAMBDA identifier without a trailing `(`**: PR 3 treats any identifier matching a workbook name whose `RefersTo` starts with `=LAMBDA(` as a LAMBDA name and excludes it from promotables, regardless of whether the formula text has a trailing `(`. Malformed in-progress formulas are rare and the safer behaviour is to never promote a LAMBDA name. Confirmed during plan — the tentative answer in the spec is correct.

One plan-only open question:

- **Spill flag on `FormulaRef` vs. local key in `RefactorEngine`.** PR 1's first step is to add `IsSpilled` to `FormulaRef`. If existing `CellRefExtractorTests` or `GatherEngineTests` fail because of double-counted `A1` vs `A1#` entries (i.e. `/Gather`'s working sets dedupe FormulaRefs in a way that now sees two distinct entries where it used to see one), we fall back to keeping `FormulaRef` unchanged and tracking spill in a `(FormulaRef, bool)` key wrapper inside `RefactorEngine`. The fallback is contained — no shared-infra change, slightly more local code in the engine. The risk is real but small: skimming `GatherEngine.cs` suggests its working sets are keyed on `CellRef` for graph identity and `FormulaRef` only for range/exclusion bookkeeping, neither of which should be affected by the new flag. Confirm by running the full test suite as the first step of PR 1.
