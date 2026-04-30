# 0005 — Gather cells into a LET — Implementation Plan

## Overview

Build the `/Gather` slash command in 12 sequenced PR-sized slices. **PR 1 is a tracer bullet** that closes the loop end-to-end on the simplest case — a chain of 2-3 formula cells with no ranges, spills, nested LETs, or error paths — so Tim can drop into Excel and exercise the feature from day one. Subsequent slices each add one capability, independently testable in Excel.

**Engine vs UI split.** Domain logic lives in pure C# classes (`CellGraphWalker`, `GatherEngine`, plus helpers) under `addin/lambda-boss/`, mirroring the existing `LetParser` / `LetToLambdaBuilder` separation. `GatherWindow` and its row view-models stay in `addin/lambda-boss/UI/`. Code-behind is kept thin: it wires controls and forwards user input to the engine, which returns the synthesised LET text and the binding-list view-model. COM is abstracted behind an `ICellSource` interface so the engine is unit-testable without Excel.

**Branching.** Implementation branches descend from `claude/lambda-from-formulas-wsF4Q` (PR #129, spec only). Each slice targets that branch and merges into it. When all slices are in, the spec branch is squash-merged to `main` as the feature ships. Slices are sequential — PR N branches from the spec branch only after PR N-1 has merged, so each PR's diff stays small and reviewable.

## Files to Touch

### New (engine)

- `addin/lambda-boss/GatherEngine.cs` — top-level entry. Takes a sink + selection + `ICellSource`, returns a `GatherResult` (binding list + synthesised LET text + diagnostics).
- `addin/lambda-boss/CellGraphWalker.cs` — precedent walker. Returns the in-scope cell graph rooted at the sink. Decoupled from COM via `ICellSource`.
- `addin/lambda-boss/CellRefExtractor.cs` — pulls A1-style refs (with optional sheet qualification, range, and `#` spill marker) out of a formula string, skipping string literals. Reuses `LetParser.SkipString` semantics.
- `addin/lambda-boss/LetNameSanitizer.cs` — extract the sanitizer rule already used by `LetToLambdaBuilder` into a standalone class so both call sites use the same code.
- `addin/lambda-boss/GatherTypes.cs` — shared records: `CellRef`, `WalkedCell`, `BindingRow`, `GatherResult`, `GatherDiagnostic`, `ICellSource`.

### New (UI)

- `addin/lambda-boss/UI/GatherWindow.xaml` — dialog: sink header, binding list, preview pane, Save/Cancel.
- `addin/lambda-boss/UI/GatherWindow.xaml.cs` — wiring + `GatherRow` view-model with `INotifyPropertyChanged`. Mirrors `LetToLambdaWindow`.

### New (command)

- `addin/lambda-boss/Commands/GatherCommand.cs` — slash-command handler. Reads active cell + selection, builds an `ICellSource` adapter over the live workbook, runs the engine, opens `GatherWindow`, writes the LET back to the sink on Save.

### New (tests)

- `addin/lambda-boss.Tests/CellRefExtractorTests.cs`
- `addin/lambda-boss.Tests/CellGraphWalkerTests.cs` — uses an in-memory `ICellSource` stub.
- `addin/lambda-boss.Tests/GatherEngineTests.cs` — covers the public engine surface with a stub workbook.
- `addin/lambda-boss.Tests/LetNameSanitizerTests.cs`

### Modified

- `addin/lambda-boss/UI/LambdaPopup.xaml.cs` — register the `/Gather` command in `BuildCommandRegistry()`.
- `addin/lambda-boss/LetToLambdaBuilder.cs` — refactor sanitizer call site to use `LetNameSanitizer` (no behaviour change).

## Order of Operations

Each step is one PR-sized issue. All branch from the spec branch; PR N branches after PR N-1 has merged.

1. **Tracer-bullet — chain end-to-end.**
   - Register `/Gather` slash command. On a non-formula sink, popup closes silently; on a formula sink, opens `GatherWindow`.
   - `CellGraphWalker` walks `Range.DirectPrecedents` transitively and builds a cell graph (single sheet, no ranges, no spills, no cycle handling — assume well-formed input).
   - `CellRefExtractor` v1: A1-style refs only, same sheet as sink.
   - `GatherEngine` classifies inputs/steps (only the rules needed for a chain) and builds the LET text via `FormulaFormatter`. Binding names: cell-above if non-empty and matches the identifier regex `^[A-Za-z_][A-Za-z0-9_.]*$`; otherwise `step_N` in topological order. (Cell-left fallback, sanitizer, and collision suffixing land in PR 2.)
   - `GatherWindow` shows: sink + original formula (read-only), binding list (read-only), preview (read-only), Save/Cancel.
   - Save writes the synthesised LET to the sink cell.
   - Tests: simple chain (3 cells), branched graph, label-above naming, no-formula sink (silent no-op).

2. **Naming completeness.**
   - Cell-left label fallback (slots between cell-above and `step_N`).
   - Extract `LetNameSanitizer` from `LetToLambdaBuilder`; `GatherEngine` uses it (so labels like "Customer ID" become `customerId` rather than falling through to `step_N`); `LetToLambdaBuilder` is refactored to call the same class (no behaviour change there).
   - Final-collision suffixing (`_2`, `_3`) when two cells produce the same name after sanitization.
   - Tests: each fallback level, sanitizer application, collision resolution.

3. **Cross-sheet refs.**
   - `CellRefExtractor` parses `Sheet1!A1` and quoted `'My Sheet'!A1`.
   - In-scope check spans the active workbook's sheets.
   - Binding RHS uses sheet-qualified address; refs in step formulas are rewritten in place.
   - Tests: sink and precedents across sheets, quoted sheet names, external-workbook ref left untouched.

4. **Range promotion.**
   - `CellRefExtractor` recognises `A1:A3` and `Sheet1!A1:A3`.
   - Walker promotes any range that overlaps in-scope cells to a single input; covered cells are dropped from the walk.
   - Binding RHS is the literal range text.
   - Tests: full coverage, partial coverage, multi-row range, cross-sheet range.

5. **Spill walk (`A1#`).**
   - `CellRefExtractor` recognises the `#` suffix.
   - `ICellSource` exposes `HasSpill(cell)`; live adapter reads `Range.HasSpill`. Modern Excel 365 only — no fallback.
   - Walk continues into the anchor; anchor's input RHS keeps `#`. In step formulas, `A1#` is rewritten to the binding name (without `#`).
   - Tests: spill anchor referenced via `#`, anchor formula with no in-scope refs, anchor inside a chain.

6. **Nested-LET expansion.**
   - When a step's formula is `=LET(...)` (detected via `LetParser.IsLetFormula` on the formula text), splice its bindings into the outer LET in order. Inner body becomes the step's RHS.
   - Auto-suffix collisions silently (`x` → `x_2`); rewrite inner refs to the suffixed name throughout the inner LET. The collision is reflected in the preview so the author can rename in the dialog if they prefer.
   - Tests: nested LET with no collision, with collision, multiple nested LETs in one walk, inner LET references outer-scope cells.

7. **Cycle + multi-sink rejection.**
   - Walker detects cycles via DFS colouring; on detect, returns a `GatherDiagnostic` listing the cells involved.
   - Multi-sink check: more than one cell in the multi-selection has no in-scope dependent → diagnostic with no list.
   - `GatherCommand` shows the diagnostic via `MessageBox` and does not open the dialog.
   - Tests: 2-cycle, 3-cycle, multi-sink with two disconnected sinks, single-cell selection (allowed).

8. **LAMBDA-call sink rejection.**
   - Detect via `EditLambdaCommand.TryParseLambdaCall` (already exists). On hit, refuse with: *"This cell is a LAMBDA call. Run /EditLambda first to expand it into a LET, then re-run /Gather."*
   - Sinks like `=Foo(A1) + 1` (not a pure call) walk normally — the call is a reference inside a larger expression.
   - Tests: pure LAMBDA call sink (refused), call-plus-expression sink (walks normally).

9. **Selection-restricted walk + header hint.**
   - When the multi-selection contains the active cell + others, restrict the walk: out-of-selection precedents collapse to cell-ref inputs (i.e. become leaves on the boundary).
   - `GatherWindow` header hint string:
     - Free walk: `Walking N cells from <addr>`
     - Restricted: `Walking M of N cells from <addr> — restricted by selection`
   - Tests: single-cell selection (free walk), multi-selection covering full chain, multi-selection partial coverage.

10. **Include checkbox + orphan drop.**
    - Each binding row in the dialog has an Include checkbox. When toggled off, the engine re-runs reachability: the cell is dropped, and any precedents reachable only via this cell also drop.
    - Engine exposes a `Recompute(rows)` method that returns a fresh `GatherResult` from the current row state.
    - Preview pane updates live on every checkbox change.
    - Tests (engine): drop-leaf, drop-step (cascading drop of upstream-only-reached precedents), drop-branch (other branches preserved).

11. **Promote/demote role toggle.**
    - Each row has a role toggle (input ↔ step). Toggling reuses the reachability re-run from step 10.
    - **Promote (input → step)**: RHS becomes the cell's formula (with in-scope refs rewritten). Walks into precedents that the formula references.
    - **Demote (step → input)**: RHS becomes the cell-ref. Orphans drop silently — preview pane shows the result.
    - Tests (engine): promote a leaf with `=SEQUENCE(30)`, demote a step with one and two precedents (orphans drop), promote-then-demote round-trip.

12. **Live binding-name editor + validation.**
    - Each row's binding name is editable. On change: sanitize (using `LetNameSanitizer`), check for collision against other rows, set a per-row `IsNameValid` flag. Save button disabled while any row is invalid.
    - Preview pane re-renders on every valid change.
    - Tests (engine + view-model): valid rename, name that sanitizes to itself, name that collides, name that becomes empty after sanitization.

## Testing Approach

**Unit tests (xUnit, runs without Excel).**
Engine classes (`CellGraphWalker`, `CellRefExtractor`, `LetNameSanitizer`, `GatherEngine`) tested via in-memory `ICellSource` stubs that return formulas/values for given cell addresses. Pattern follows `LetParserTests` / `LetToLambdaBuilderTests`. View-model tests on `GatherRow` for live validation; no UI thread required.

**Round-trip safety.**
`GatherEngineTests` asserts that the synthesised LET parses cleanly via `LetParser.Parse(...)` for every test fixture — catches malformed output early.

**Manual Excel tests.**
Each PR's issue body lists a 2-3 step Excel scenario Tim can run after a build to verify the slice. PR 1's: type three formulas in a chain, run `/Gather`, save, confirm the LET evaluates. Later PRs add cells of the new shape (range, spill, nested LET, etc.) and verify the dialog/output.

The existing `addin/lambda-boss.AddinTests/` project covers ribbon-level Excel automation; no new add-in tests required for this feature unless the manual smoke checks find a gap the unit tests miss.

## Open Questions

None at plan time. All five spec-level open questions are resolved:

- **Inner-LET collisions** — auto-suffix silently, surface in preview.
- **Selection-restricted hint wording** — as listed under step 9.
- **Demote-to-input** — allowed in v1; orphans auto-drop silently (reuses step 10 machinery).
- **`Range.HasSpill`** — modern Excel 365 only, no legacy fallback.
- **Sink is a LAMBDA call** — refuse with hint pointing at `/EditLambda`.
