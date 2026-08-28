# 0010 — Gather: spill-aware interim references — Implementation Plan

## Overview

Eight PR-sized slices. The shape is deliberately front-loaded with two
zero-risk PRs — a COM spike that swaps `HasSpill` for a geometry probe with no
behaviour change, and a pure, heavily-tested slice-expression generator that
nothing calls yet — so that **PR 3 is a tracer bullet**: Tim's REGEXEXTRACT case
works end-to-end in Excel with everything downstream still to come.

**Why this order.** The single hard dependency in the whole feature is the COM
semantics of spill children, and it is the one thing that cannot be established
from the desk. PR 1 answers it and does nothing else, so if the answer is
surprising the blast radius is one small PR and a spec amendment, not a
half-built feature. PR 2 is the algorithmic core and is pure — it can be
reviewed and tested to exhaustion without any of the walker/engine wiring around
it. By PR 3 both risks are retired and the remaining slices are integration.

**Engine vs UI split** follows spec 0005's plan unchanged: domain logic in pure
C# under `addin/lambda-boss/`, COM behind `ICellSource`, `GatherWindow` thin.
The slice generator is a new pure class with no dependency on the walker, the
engine, or COM.

**Row kinds.** The dialog already carries two special row kinds beyond ordinary
bindings — `IsExpansion` (inner-LET rows, no Include checkbox, host owns the
toggles) and `IsOrphan` (`OrphanedRowTracker`, always muted, not name-editable).
Slice rows are a third, with their own combination: Include checkbox **yes**,
role toggle **no**, name-editable **yes**. This resolves spec 0010's last open
question — they stay separate kinds rather than unifying, because no two of the
three share a rule set.

**Branching.** An epic branch `epic/0010-spill-aware-gather` off `main`; each
child PR targets the epic branch. The epic→`main` PR is a rebase merge once all
eight are in, so each reviewed child lands as its own commit on `main`.

## Files to Touch

### New (engine)

- `addin/lambda-boss/SpillSliceBuilder.cs` — the pure slice-expression
  generator. Input: spill dimensions (R, C), the reference rectangle
  (r1, r2, c1, c2), and the reference shape (spill ref / single cell / range).
  Output: the expression string (`arr`, `INDEX(arr,r,c)`, `TAKE(...)`,
  `TAKE(DROP(...))`) given a binding name. No COM, no engine types beyond the
  name string.

### New (tests)

- `addin/lambda-boss.Tests/SpillSliceBuilderTests.cs` — the ladder matrix.
- `addin/lambda-boss.Tests/GatherEngineSpillSliceTests.cs` — engine-level
  behaviour: slice rows, naming, discovery, cascading, diagnostics.

### Modified (engine)

- `addin/lambda-boss/GatherTypes.cs` — add `SpillInfo` record; replace
  `ICellSource.HasSpill(CellRef)` with `SpillInfo? GetSpill(CellRef)`; add
  `BindingRow.SliceOf`; add `GatherDiagnosticKind.SpillChildSink`; add the
  straddle-warning carrier on the range row.
- `addin/lambda-boss/CellGraphWalker.cs` — remove `NormaliseSpillFlag` so `A1`
  and `A1#` stay distinct precedents; resolve each precedent through
  `GetSpill` and recurse into the **anchor**, never a child; carry the
  discovered anchor as a synthetic precedent.
- `addin/lambda-boss/GatherEngine.cs` — slice-row construction and ordering,
  anchor-derived naming fallback in `AssignNames`, range-promotion precedence,
  anchor exemption from range coverage, Include cascading in `Recompute`,
  selection normalisation, the `SpillChildSink` check.
- `addin/lambda-boss/Commands/GatherCommand.cs` — `LiveCellSource.GetSpill` via
  `Range.SpillParent` / `Range.SpillingToRange`.

### Modified (UI)

- `addin/lambda-boss/UI/GatherWindow.xaml` / `.xaml.cs` — slice row rendering
  (indent, no role toggle), straddle warning marker + tooltip, the
  fixed-position note.

### Modified (tests)

- `addin/lambda-boss.Tests/StubCellSource.cs` — `WithSpill(anchor, rows, cols)`
  replacing the current anchor-only `WithSpill(a1)`; implements `GetSpill`.
- `addin/lambda-boss.Tests/GatherEngineTests.cs`,
  `CellRefExtractorSpillTests.cs` — migrate to the new source contract.

## Order of Operations

### 1. COM spike + `GetSpill` plumbing — no behaviour change

- **Spike first**, in a scratch workbook via the AddinTests harness: record
  `HasSpill`, `HasFormula`, `Formula2`, `SpillParent`, `SpillingToRange` for
  (a) a spill anchor, (b) a spill child, (c) a plain formula cell, (d) a plain
  literal cell. Write the findings into spec 0010's *Open Questions* as
  resolved fact.
- If `HasFormula` is **true** on children (returning the anchor's formula text),
  amend spec 0010's *Problem* section: today's failure mode is a duplicated
  step, not a stray input, and the "broken `B1#` RHS" claim is withdrawn. The
  rest of the design is unaffected either way.
- Add `SpillInfo`; replace `HasSpill` with `GetSpill` across `ICellSource`, the
  live adapter, `StubCellSource`, `CellGraphWalker`, `GatherEngine`.
  `cell.HasSpill` becomes `GetSpill(cell)?.Anchor == cell` at the one call site
  that appends `#`.
- If `SpillingToRange` errors on a child, `GetSpill` resolves the anchor first
  and reads the rectangle from there — note the extra COM hop's cost.
- Tests: the entire existing gather suite stays green, unchanged in behaviour.

*Rationale: retires the only unknowable risk, in isolation, before anything
depends on it.*

### 2. `SpillSliceBuilder` — pure generator, not yet wired

- Implement the ladder exactly as specced: single-cell rule **first**, then
  whole-array, then per-axis band/block selectors composed into at most one
  `DROP` and one `TAKE`.
- Argument omission: trailing omissions drop the argument, interior omissions
  render as a bare comma (`TAKE(arr,,-1)`).
- Tests — the full matrix, as a pure function: 1×1 / 1×N / N×1 / N×M spills ×
  every reference shape; both flush edges per axis; interior bands; blocks
  constrained on both axes; the negative-`TAKE`-with-cross-axis-`DROP`
  composition; and the invariant that **no input produces a 1×1 array**.
- Nothing calls it yet — this PR is additive and cannot regress gather.

*Rationale: the algorithmic core, reviewable and exhaustible on its own.*

### 3. Single-cell slices, end-to-end — **tracer bullet**

The first PR that changes what Tim sees. Covers both single-cell cases at once,
because they are one code path (a single-cell ref landing inside a spill):
`B1` (child) and `A1` (scalar ref to a spilling anchor).

- Walker: drop `NormaliseSpillFlag`; register both `A1` and `A1#` keys; resolve
  precedents through `GetSpill`; recurse into the anchor; pull in an anchor that
  nothing else references (anchor discovery).
- Engine: build slice binding rows ordered immediately after their anchor's row;
  set `BindingRow.SliceOf`; `Role = Input`, `CanToggleRole = false`.
- Naming: cell-above → cell-left → `<anchorName>_<rowMajorIndex>` in
  `AssignNames`, with the existing collision suffixing on top.
- Dialog: slice rows render as ordinary rows for now (address = the child cell,
  role reads "slice", no role toggle). Indentation and the note come in PR 8.
- Fixes the pre-existing scalar-widening bug as a side effect.
- Tests: the spec's canonical REGEXEXTRACT case end-to-end; a slice referenced
  from several steps producing one row; anchor discovery; 1×1 spill giving `arr`
  for `A1#` and `INDEX(arr,1,1)` for both `A1` and `A1:A1`.

*Rationale: Tim can drive the actual feature in Excel from here; everything
after is widening.*

### 4. Range slices

- Wire PR 2's generator to range refs: exact-spill ranges rewrite to the
  anchor's binding name with no new row; sub-block ranges become slice rows.
- Range-promotion precedence: a range wholly inside a spill never promotes to a
  range input.
- A spill anchor is never dropped from the bindings by range coverage.
- Tests: exact-spill range; row band; column band flush to the end (asserting
  `TAKE(arr,,-1)`, not a counted `CHOOSECOLS`); interior block; the degenerate
  single-cell range taking the scalar path even when the spill is 1×1.

### 5. Straddling ranges + warning surface

- A range partly inside and partly outside a spill promotes to a literal range
  input exactly as today, flagged.
- Dialog: warning marker + tooltip *"Partly inside A1's spill range — left as a
  cell reference."*
- Explicitly **not** a diagnostic and **not** a refusal — the LET stays correct.
- Tests: straddle detection on each edge; the LET still evaluates against the
  live cells; no diagnostic raised.

### 6. Include cascading, demotion, and the `Recompute` path

Separate slice because `Recompute` is a distinct code path from the initial
`Gather` and is where row-state interactions actually live.

- Excluding a slice row reverts that reference to a literal cell ref in the
  referencing steps.
- Excluding an anchor drops every slice row of that anchor and reverts all of
  them.
- Demoting an anchor to an input (RHS `A1#`) keeps every slice working.
- Tests: each cascade direction, and the re-entrancy case of toggling a row off
  and back on.

### 7. Selection normalisation + `SpillChildSink` diagnostic

- Any spill child in a multi-selection maps to its anchor **before** the
  multi-sink check and **before** `restrictTo` is built — so dragging across a
  spill range selects the calculation, not its output.
- A sink that is itself a spill child produces `SpillChildSink` naming the
  anchor: *"D4 is inside A1's spill range. Gather from A1 instead."* Replaces
  spec 0005's silent no-op.
- Tests: drag-select over a spill range; spill-child sink; the multi-sink check
  not mis-firing on a normalised selection.

### 8. Dialog polish + regression sweep

- Slice rows indented under their anchor; the one-line note about slice
  positions being fixed at gather time, shown only when a slice row exists.
- Confirm the three row kinds (`IsExpansion`, `IsOrphan`, slice) render
  coherently together in one list.
- Regression: `/Refactor` and `/Unnest` output unchanged — their suites are the
  gate, since `CellRefExtractor` is shared and the spilled→non-spilled fallback
  in `Rewrite` stays for their benefit.
- Full unit suite plus the AddinTests harness before the epic→`main` PR.

## Testing Approach

- **Unit (`lambda-boss.Tests`, no Excel)** carries essentially all of it.
  `SpillSliceBuilderTests` is table-driven over the ladder; the engine tests use
  `StubCellSource` with seeded spill geometry.
- **`StubCellSource.WithSpill(anchor, rows, cols)`** replaces the anchor-only
  flag. Every existing call site is a one-line migration to `WithSpill(a1, 1, 1)`
  or the real geometry the test intends.
- **AddinTests (requires Excel, local)** carries only the PR 1 spike and a
  smoke test that a gathered LET containing `INDEX`/`TAKE` slices evaluates to
  the same values as the original cell graph.
- **Regression gate**: the existing `GatherEngineTests`,
  `CellRefExtractorTests`, `CellRefExtractorSpillTests`, and the `/Refactor` and
  `/Unnest` suites must stay green throughout. PR 1 and PR 2 must not change a
  single existing assertion.
- The `LAMBDA_FILTER` workflow does not apply here — that targets the
  `*.tests.yaml` harness, and this work touches none of it.

## Open Questions

- **PR 1's spike may amend the spec.** The design holds either way, but the
  *Problem* statement and PR 3's regression tests differ depending on whether
  spill children report `HasFormula` true or false. Flagged in PR 1 rather than
  left to be discovered in PR 3.
- **Straddle warning wording.** The plan carries the spec's phrasing verbatim;
  worth a look in the real dialog at PR 5 alongside the existing orphan hint,
  which is the nearest precedent for tone.
- **`SpillingToRange` cost on child-heavy graphs.** If the child path needs the
  extra anchor hop, a graph referencing many cells of one large spill pays it
  per reference. A memo keyed on the anchor inside `LiveCellSource` is the
  obvious fix; PR 1 should measure before deciding whether it is warranted.
