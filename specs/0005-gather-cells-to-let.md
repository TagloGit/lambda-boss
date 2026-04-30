# 0005 — Gather cells into a LET

## Problem

Authors often build a calculation in stages, with each step in its own cell so the intermediate values are visible while iterating. Today there is no way to roll those separate cells up into a single self-contained formula. To get to a LAMBDA, the author must first hand-write a LET that mirrors their cell graph — copying formulas, renaming cell refs to identifiers, ordering bindings — and only then can they use LET to LAMBDA (spec 0001/0002) to register it. The hand-conversion is tedious and error-prone, especially for graphs of more than a handful of cells.

## Proposed Solution

Add a `/Gather` slash command (spec 0004) that takes a sink cell and synthesises a single `=LET(...)` formula equivalent to the calculation graph rooted at that cell. The resulting LET is written back into the sink cell only; intermediate cells are left untouched. The author then runs LET to LAMBDA on the sink cell as a separate step.

The command is intentionally narrow: it produces a LET, nothing more. Optional-parameter handling, parameter renaming, and Lambda registration are all out of scope here — they're already covered by LET to LAMBDA.

### Activation

- Open the main popup (existing keyboard shortcut), type `/Gather`, press Enter.
- The popup closes and a new `GatherWindow` opens, with the **active cell at trigger time** as the sink.
- If the active cell is empty or holds a literal value (no formula), the command does nothing — no dialog, no error message.

### Walking the graph

Starting from the sink, the gatherer walks **precedents transitively**:

- A cell whose formula references no in-scope cells is a **leaf**.
- A cell whose formula references at least one in-scope cell is a **step**.
- A cell with no formula (literal value, or empty) that is referenced by a step is a **leaf**.

The walk stops at leaves and at cells outside the active workbook (out-of-workbook refs become leaves regardless).

**Cross-sheet** references are in scope; their LET binding RHS uses the sheet-qualified address (`Sheet1!A1`).

**External workbook** references are treated as leaves.

**Cycles** in the cell graph are an error — Excel itself surfaces these as circular references, but the gatherer detects the cycle independently and refuses with a clear message that lists the cells involved.

**Multiple sinks** (the case where the user multi-selects cells and more than one of the selected cells has no in-scope dependent) are an error. Refuse without listing — the situation is obvious to the author.

### Range and spill references

When a step's formula references a **range** (e.g. `A1:A3`) that covers one or more in-scope cells, the range itself is promoted to a single leaf input. The cells covered by the range are **dropped from the walk**, even if they would otherwise have been steps. Partial coverage (range covers some in-scope cells and some not) follows the same rule.

Worked example:

```
A1   =RAND()
A2   =RAND()
A3   =RAND()
B1   =SUM(A1:A3)         ← sink
```

→ `=LET(input, A1:A3, SUM(input))`. A1/A2/A3 do **not** appear as steps.

When a step references a **spilled** cell via the `#` operator (e.g. `A1#`), the walk continues into the anchor cell normally. The binding name comes from the anchor's label rules below; the `#` is dropped from the binding because the binding *is* the array.

```
A1   =SEQUENCE(10)
B1   =SUM(A1#)            ← sink
```

→ `=LET(numbers, A1, SUM(numbers))` (assuming A1's label is `numbers`).

The cell-ref RHS for a spilling cell is `A1#`, not `A1`, so that the LET keeps the dynamic-array semantics. Spill detection uses `Range.HasSpill` via COM.

### Inputs vs steps

After the walk, every collected cell is classified:

| Cell shape | Classification | Default LET binding RHS |
|---|---|---|
| No formula, referenced by a step | input | `A1` |
| Formula, references no in-scope cells, doesn't spill | input | `A1` |
| Formula, references no in-scope cells, spills | input | `A1#` |
| Formula, references at least one in-scope cell | step | rewritten formula (see below) |

A range-promoted leaf (previous section) is always an input; its RHS is the literal range (e.g. `A1:A3`).

### Reference rewriting in step formulas

For every step, references to **in-scope cells** are rewritten to the binding/input name. References to out-of-scope cells, named ranges, and other LAMBDAs are left as-is.

Worked example:

```
A1  Numbers      30
B1  Items        =SEQUENCE(A1)
C1  Doubled      =B1*2
D1  Sink         =SUM(C1)
```

→
```
=LET(
  numbers, A1,
  items,   SEQUENCE(numbers),
  doubled, items*2,
  SUM(doubled))
```

### Nested LETs

A step's formula may itself be a `=LET(...)`. The gatherer expands it inline: the inner LET's bindings are spliced into the outer LET in order, and the inner LET's body becomes the step's RHS (or, if the step is the sink, the outer LET's body).

If an inner-LET binding name collides with a name already in scope (from a label, a prior expansion, or another inner LET), the gatherer auto-suffixes (`x` → `x_2`) and rewrites references inside the inner LET accordingly. The collision is shown in the preview so the author can rename if they prefer.

### Naming bindings

For each step and each input, the binding name is derived by:

1. **Cell above** the cell in question — if non-empty and a string, sanitize via the LET-name sanitizer.
2. Otherwise **cell to the left** — same treatment.
3. Otherwise auto-name `step_1`, `step_2`, … in topological order.

The LET-name sanitizer is the existing rule used by LET to LAMBDA: trim, lowercase initial letter, replace runs of non-identifier characters with `_`, prefix with `_` if the result starts with a digit, etc. Final collisions after sanitization (two cells produce the same name) are resolved by suffixing `_2`, `_3`, …

The dialog lets the author rename any binding; renames are validated live against the same sanitizer + collision rules.

### Promote-to-step toggle

Each input row in the dialog has a **Promote to step** toggle. When enabled, the binding's RHS becomes the cell's formula contents (with refs rewritten if any are in scope) instead of the cell ref. This is the only supported way to bake an input's formula into the LET.

For example, with `A1: =SEQUENCE(30)` labelled `Numbers`:
- Default (input): `LET(numbers, A1, …)`
- Promoted to step: `LET(numbers, SEQUENCE(30), …)`

There is intentionally **no** toggle for "hardcode the spill ref `A1#` literally". Authors who want that can hand-edit afterwards.

### Selection-restricted walk

If the author multi-selects cells before triggering `/Gather`, and the multi-selection contains the active cell plus other cells, the gatherer interprets it as: **walk precedents from the active cell, but only include cells that are also in the multi-selection** as steps. Out-of-selection precedents that would otherwise have been steps are demoted to leaves (their cell ref appears as an input on the boundary).

A single-cell selection (the common case) means "walk freely".

### Dialog

`GatherWindow` shows:

- Sink cell address and original formula (read-only).
- A reorderable list of bindings (inputs first, then steps, in topological order). Each row exposes:
  - Cell address (read-only).
  - Source label (read-only, from cell-above/left detection).
  - **Binding name** (editable, validated live).
  - **Role** — input | step (toggleable for cells where both are valid; inputs that came from "no precedents" cells can be promoted to step here; steps with in-scope precedents cannot be demoted to input — that would orphan the precedents).
  - **Include** checkbox — when off, the cell is dropped from the LET (the calling step keeps the cell-ref) and any of *its* precedents that would otherwise have been included only via this step are also dropped.
- A **Preview** pane (read-only) showing the synthesised LET, formatted via the existing `FormulaFormatter`.
- **Save** button writes the LET into the sink cell. **Cancel** closes the dialog with no change.

The sink cell cannot be re-picked from inside the dialog. To target a different sink, the author closes the dialog, selects the new sink, and re-runs `/Gather`.

### Output

On Save, the sink cell's formula is overwritten with the synthesised `=LET(...)`. Intermediate and leaf cells are not modified. The author copies the sink cell first if they want a one-step undo path; the add-in does not snapshot, because the in-place overwrite is a single Excel operation that fits in Excel's native undo stack.

After Save, the author typically re-opens the popup and runs `/LetToLambda` to register the LET as a LAMBDA — the standard downstream flow.

## User Stories

- As a model author, I want to gather a series of cell-by-cell calculation steps into a single LET formula, so that I can hand it off to LET to LAMBDA without writing the LET manually.
- As an author iterating on a model, I want to keep my intermediate cells visible after gathering, so that I can re-gather (or undo) without losing my working state.
- As an author with mixed input/calculation cells, I want sensible defaults for what becomes a LAMBDA-bound input vs an inlined step, with the ability to override per cell, so that the resulting LET reflects my intent.

## Acceptance Criteria

- [ ] A new slash command `/Gather` registered in the main popup (spec 0004 command list).
- [ ] When invoked with the active cell as a formula cell, opens `GatherWindow` with the active cell as the sink.
- [ ] When invoked with the active cell **not** a formula cell, the popup closes silently (no dialog, no error).
- [ ] The gatherer walks precedents transitively from the sink, classifying each cell as input or step per the rules in *Inputs vs steps*.
- [ ] Cross-sheet refs are in scope; binding RHS for inputs uses the sheet-qualified address.
- [ ] External-workbook refs are leaves (inputs); their RHS is the original external ref.
- [ ] Cycles in the precedent graph produce a clear error listing the cells involved; no LET is produced.
- [ ] Multi-sink selections (more than one cell in the multi-selection has no in-scope dependent) produce a clear error; no LET is produced.
- [ ] Range refs covering in-scope cells promote the range to a single input and drop those cells from the walk.
- [ ] Spill refs (`A1#`) continue the walk into the anchor cell; the anchor's input RHS is `A1#` (detected via `Range.HasSpill`).
- [ ] Step formulas have all in-scope cell refs rewritten to the corresponding binding/input names; out-of-scope refs are left untouched.
- [ ] When a step's formula is itself a `=LET(...)`, the inner LET's bindings are spliced into the outer LET in order; the inner body becomes the step's RHS.
- [ ] Inner-LET binding-name collisions during expansion are resolved by `_N` suffixing, with the renamed bindings reflected throughout the inner LET.
- [ ] Binding names are derived from cell-above first, cell-left next, then auto-name `step_N`. The LET-name sanitizer is applied. Final collisions are suffixed.
- [ ] Each row in `GatherWindow` exposes a binding-name editor (live validation), an Include checkbox, and a Promote-to-step toggle for inputs.
- [ ] The Preview pane shows the synthesised LET formatted via `FormulaFormatter`, and updates as the author edits the dialog.
- [ ] Save writes the LET into the sink cell; intermediate and leaf cells are unchanged.
- [ ] If the multi-selection contains the active cell plus others, the walk is restricted to cells in the selection; out-of-selection precedents collapse to cell-ref inputs.
- [ ] The synthesised LET parses cleanly via `LetParser` (round-trip safety).
- [ ] Unit tests cover: simple chain, branched graph, cycle rejection, range promotion, spill walk, nested-LET expansion with and without collision, label fallback (above → left → auto), promote-to-step, selection-restricted walk, cross-sheet refs.

## Out of Scope

- Producing a LAMBDA. The author runs the existing `/LetToLambda` after Save.
- Optional-parameter (`ISOMITTED`) handling. Owned by LET to LAMBDA (spec 0002).
- Reverse direction (LET back into individual cells).
- Re-gathering an existing LET in the sink cell to refresh from the current state of source cells.
- Warning on volatile functions (`NOW`, `RAND`, etc.) — they pass through silently.
- Warning on external workbook refs — they pass through as inputs.
- Persistence of the dialog state across opens; each `/Gather` invocation starts fresh.
- Sinks that are spill children (i.e. cells inside another cell's spill range, not the anchor). The active cell must be the formula source.
- Editing the preview pane directly. All edits happen via the row controls.
- Detecting when a leaf input's value is `#N/A` or another error — Excel will surface the error when the LET evaluates; the gatherer doesn't pre-validate input values. (A leaf input cell that's literally **empty** is allowed and produces an empty/zero binding — same as referring to the cell directly today.)

## Open Questions

- **Inner-LET expansion: silent vs surfaced collisions.** Tentative: auto-suffix silently and reflect in the preview; the author sees the rename and can edit if they want. Alternative is to require manual resolution before Save. Confirm during plan.
- **Selection-restricted walk discoverability.** There is no UI affordance distinguishing "auto-walk" mode from "user-restricted walk" mode. Tentative: add a single-line hint at the top of `GatherWindow` ("Walking 7 cells from D1" vs "Walking 4 of 7 cells from D1 — restricted by selection"). Plan should include the exact wording.
- **Demoting a step to an input.** A step's formula references in-scope cells; demoting it to an input would orphan those precedents (they'd no longer appear in the LET). v1 forbids the demotion. If feedback shows authors want this, a follow-up could auto-drop the orphaned precedents on demotion, with confirmation.
- **`Range.HasSpill` reliability across Excel versions.** Spill detection depends on the COM property; older Excel builds without dynamic arrays don't expose it. Plan should document the minimum Excel version (matches existing add-in baseline) and the fallback (treat as non-spilling).
- **Sink cells that are themselves a single LAMBDA call.** Today, Edit Lambda (spec 0003) inlines a LAMBDA call to a LET. `/Gather` could either refuse (let the author run `/EditLambda` first) or auto-Edit-Lambda before walking. Tentative: refuse with a hint pointing at `/EditLambda`. Confirm during plan.
