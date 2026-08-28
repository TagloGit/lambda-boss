# 0010 — Gather: spill-aware interim references

## Problem

`/Gather` (spec 0005) only produces a good LET when the author has built the
calculation dynamically from the start. The moment an interim step spills, and
the author references the spilled cells individually, gather falls over.

The canonical case:

```
A1   =REGEXEXTRACT(txt, pat, 1)     ← spills into A1:B1
D1   =A1*2
D2   =B1&"x"
D3   =D1&D2                          ← sink
```

`B1` is a *spill child* — it has no formula of its own, so the walker classifies
it as a leaf **input**. The author's two-capture regex, which is one calculation
producing one array, comes out of gather as an array binding plus an unrelated
cell-reference input. The LET is not self-contained and doesn't express what the
author built.

There are two further defects in the same area, both pre-existing:

- **Spill children emit a broken RHS.** `Range.HasSpill` reports true for every
  cell in a spill range, not just the anchor, so a spill-child leaf gets RHS
  `B1#` — and `#` on a non-anchor cell is `#REF!` in Excel. Gather can therefore
  emit a LET that does not evaluate. (Confirmed in the PR 1 spike — see
  *Open Questions*.)
- **A scalar reference to a spilling anchor is silently widened.** `=A1*2` where
  `A1` spills means the top-left value only. Gather binds the anchor's RHS as
  `A1#` and rewrites the `A1` token to the bare binding name, so the step now
  sees the whole array. `INDEX(arr,1,1)` is the faithful rewrite.

The root cause is that gather has exactly one notion of array-ness —
`ICellSource.HasSpill(cell)`, a single bool used in one place to append `#` to a
leaf's RHS. It has no notion of spill *geometry*: no anchor→rectangle map, no
cell→anchor map. Every case below needs geometry.

## Proposed Solution

Teach gather to recognise when a reference lands inside the spill range of a
cell that is already part of the calculation, and rewrite that reference as a
named slice of the anchor's binding.

For the canonical case above:

```
=LET(
  extracted, REGEXEXTRACT(txt, pat, 1),
  first,     INDEX(extracted,1,1),
  second,    INDEX(extracted,1,2),
  d1,        first*2,
  d2,        second&"x",
  d1&d2)
```

### The new primitive: spill geometry

`ICellSource` gains a single geometry probe:

```csharp
SpillInfo? GetSpill(CellRef cell);

public sealed record SpillInfo(CellRef Anchor, int Rows, int Columns);
```

Returns non-null when `cell` is part of a dynamic-array spill (anchor or child),
giving the anchor's address and the spill rectangle's dimensions. Null for plain
cells, external refs, and unreachable sheets. The live adapter resolves it via
`Range.SpillParent` then `Range.SpillingToRange` **on the anchor** — a child's
own `SpillingToRange` is null (spike, *Open Questions*); the existing
`HasSpill(cell)` member is subsumed and removed (`GetSpill(c)?.Anchor == c`
replaces it).

Excel guarantees spill ranges are **disjoint** — overlap is `#SPILL!` — so every
cell belongs to at most one spill range and there is no ambiguity to resolve.

Cost is unchanged from the existing `HasSpill` call on cells that don't spill,
and a few property reads more on cells that do — measured in the spike, see
*Open Questions*.

### Reference taxonomy

Let `arr` be the binding name of a spill anchor whose spill range `S` is R×C.
Every reference the extractor produces is classified against `S`:

| Ref in a step formula | Geometry vs `S` | Becomes |
|---|---|---|
| `A1#` | the whole array | `arr` |
| `A1` (the anchor, spilling) | top-left cell, scalar | `INDEX(arr,1,1)` |
| `B1` (a spill child) at (r,c) | one interior cell, scalar | `INDEX(arr,r,c)` |
| range exactly equal to `S`, spanning 2+ cells | the whole array | `arr` |
| range wholly inside `S` | a sub-block | slice ladder below |
| range straddling `S`'s boundary | inexpressible | left as a literal range, warned |
| any ref with no spill overlap | — | unchanged from spec 0005 |

The rule keys on the **shape of the reference**, not the shape of the result.
That is what keeps `INDEX` (scalar) and `TAKE` (array) from being confused: a
1×1 spill still gives `arr` for `A1#` and `INDEX(arr,1,1)` for `A1`, because a
1×1 array is not a scalar and collapses `SEQUENCE`/`REGEXEXTRACT` downstream.

### The slice ladder

For a reference covering rows `r1..r2` and columns `c1..c2` of an R×C spill
(1-based, relative to the anchor), with `h = r2-r1+1` and `w = c2-c1+1`:

1. **Single cell** (`h=w=1`) → `INDEX(arr,r1,c1)`, unconditionally. Positional,
   never shape-robust: an edge cell in the last column emits `INDEX(arr,1,2)`,
   not `INDEX(arr,1,COLUMNS(arr))`. The position freezes at gather time, which
   is exactly the staleness the original cell ref already had.

   **This rule is checked first among the geometry rules**, and that ordering
   matters. A range ref covering exactly one cell (`A2:A2`) takes the scalar
   path — including the case where the spill is itself 1×1, so `A1:A1` is
   *also* "the whole array". Excel's `=A1:A1` yields a scalar, and emitting
   `arr` there would hand downstream steps a 1×1 array, which is not a scalar
   and collapses `SEQUENCE`/`REGEXEXTRACT`. On a 1×1 spill, only an explicit
   `A1#` asks for the array.

   "First among the geometry rules" is the precise claim, and the distinction
   is load-bearing: the implementation tests the **ref shape** before it looks
   at the rectangle at all, so a `SpillRef` (`A1#`) short-circuits to `arr`
   ahead of the single-cell check. Both orderings are needed, and they don't
   conflict — the shape test is what makes `A1#` on a 1×1 spill the array while
   `A1` on the same spill is the scalar, and the single-cell test is what keeps
   `A1:A1` a scalar. Don't "fix" the implementation to check the rectangle
   first; that would collapse `A1#` on a 1×1 spill to `INDEX(arr,1,1)`.
2. **Whole array** — the ref shape is `A1#`, or a range with `h=R`, `w=C`
   spanning more than one cell → `arr`.
3. **Band or block** → decide each axis independently, then compose into at most
   one `DROP` and one `TAKE`.

Per axis, given `start`, `len`, `total`:

| Condition | Selector |
|---|---|
| `start=1` and `len=total` | *all* — contributes nothing |
| `start=1` | `TAKE +len` |
| `start+len-1 = total` (flush to the end) | `TAKE -len` — **edge-relative, no counting** |
| otherwise (interior) | `DROP start-1` then `TAKE +len` |

Composition, with `dr`/`dc` the leading drops (0 when the selector has none) and
`tr`/`tc` the signed takes:

- `dr = dc = 0` → `TAKE(arr, tr, tc)`
- otherwise → `TAKE(DROP(arr, dr, dc), tr, tc)`

Arguments for *all* axes are omitted: trailing omissions drop the argument
entirely, interior omissions render as a bare comma. So the last column of a
spill is `TAKE(arr,,-1)`, the first three rows are `TAKE(arr,3)`, rows 4–6 of 10
are `TAKE(DROP(arr,3),3)`, and a 3×1 block at (2,3) of a 5×4 spill is
`TAKE(DROP(arr,1,2),3,1)`.

Negative `TAKE` composes correctly with a `DROP` on the *other* axis, because
axis drops are independent — `TAKE(DROP(arr,,1),-1,2)` takes the last row of
everything after the first column.

Because rule 1 is checked first, `TAKE` is only ever emitted for a rectangle
with more than one cell, so no path in the ladder can produce a 1×1 array where
the reference asked for a scalar.

### Slices are binding rows

Each distinct referenced slice becomes **its own binding row** in the LET,
ordered immediately after its anchor's row:

```
extracted, REGEXEXTRACT(txt, pat, 1),
first,     INDEX(extracted,1,1),
second,    INDEX(extracted,1,2),
```

not an inline substitution into each referencing step. Three reasons: the child
cell's own label survives into the binding name, the row fits the dialog's
existing name/Include model, and a slice referenced from several steps is
written once.

A consequence worth stating explicitly: because slices are rows,
`CellRefExtractor.Rewrite` still only ever substitutes **bare identifiers** into
step formulas. Slice expressions appear solely as a slice row's own RHS, so
there are no operator-precedence or parenthesisation concerns in rewritten step
formulas.

`BindingRow` gains `SliceOf` — the anchor's `FormulaRef`, non-null on slice rows
only. It identifies the row kind for the dialog and gives it the parent for
Include cascading. `Role` stays `Input`; `CanToggleRole` is false (there is no
formula to bake, and demotion is meaningless).

### Naming slice rows

Same ladder as spec 0005, with one new fallback:

1. Cell above the slice's top-left cell, sanitized.
2. Otherwise cell to the left, sanitized.
3. Otherwise **derived from the anchor**: `<anchorName>_<n>`, where `n` is the
   top-left cell's linear index within the spill in row-major order (`A1`→1,
   `B1`→2 for a 1×2 spill; `A1`→1, `A2`→2, `A3`→3 for a 3×1 spill).

The existing collision-suffix rule (`x` → `x_2`) applies on top, and the name is
editable in the dialog like any other.

The anchor-derived form replaces the generic `step_N` fallback for slice rows
only; it reads as "part of `extracted`" and groups the slices visually with
their source in the LET.

### Straddling ranges

A range that is partly inside a spill and partly outside cannot be expressed as
a slice. It promotes to a literal range input exactly as today, and the dialog
marks the row with a warning: *"Partly inside A1's spill range — left as a cell
reference."*

The LET is still **correct** — it reads the live cells — just not fully
self-contained. Non-blocking, no diagnostic, no refusal.

As shipped, straddle detection probes the range's **four corners** rather than
every cell (a bounded COM cost instead of one probe per cell), so a range that
crosses right through a spill or wholly encloses it — no corner of it landing
inside — is not flagged, while every single-edge overflow still is. The blind
spot costs a warning marker only and never correctness, since the range
promotes identically either way.

### Interactions with existing gather behaviour

- **Anchor discovery.** When a step references a spill child and nothing
  references the anchor, the walker pulls the anchor in as a synthetic
  precedent — otherwise there is no array to slice. The walker recurses into the
  *anchor*, never the child. This can drag a sub-tree into the LET the author
  didn't select; the existing Include and demote-to-input toggles are the escape
  hatch.
- **Precedence over range promotion.** A range wholly inside a spill takes the
  slice path and never promotes to a range input. A range exactly equal to a
  spill rewrites to the anchor's binding name with no new row. A straddling
  range promotes as today, with the warning. Ranges with no spill overlap are
  unchanged.
- **A spill anchor is never dropped by range coverage.** Spec 0005's rule drops
  walked cells that fall inside a promoted range; anchors are exempt, because
  they are the source for every slice of them.
- **Exclusion cascades.** Turning off a slice row's Include reverts that
  reference to a literal cell ref in the referencing steps. Turning off the
  *anchor's* Include drops every slice row of that anchor and reverts all of
  them to literal refs.
- **Demotion is safe.** Demoting the anchor to an input gives RHS `A1#`; slices
  still work unchanged.
- **Selection normalisation.** Any spill child in a multi-selection maps to its
  anchor before the multi-sink check and before the `restrictTo` set is built,
  so dragging over a spill range selects the calculation, not its output cells.
- **Sink is a spill child.** Currently out of scope in 0005 with no message. It
  becomes an explicit `GatherDiagnosticKind.SpillChildSink` naming the anchor:
  *"D4 is inside A1's spill range. Gather from A1 instead."*
- **Frozen geometry.** All offsets are computed from the spill's shape at gather
  time. If the array later returns a different shape the emitted `INDEX`/`TAKE`
  goes stale — but so did the original cell refs, so it is no worse. The dialog
  notes this once, not per row.
- **`/Refactor` and `/Unnest` are unaffected.** They operate on a single formula
  with no in-scope anchor, so a spill-child reference correctly stays a literal
  ref. `CellRefExtractor.Rewrite`'s spilled→non-spilled fallback stays for their
  benefit; gather now registers both keys so the fallback never fires for it.

### Walker changes

`CellGraphWalker.NormaliseSpillFlag` is removed for gather: `A1` and `A1#` must
stay distinct precedents, since they map to different replacements
(`INDEX(arr,1,1)` versus `arr`). The lookup dictionary carries both keys.

### Dialog

- Slice rows render indented under their anchor, address column showing the
  child cell or block (`B1`, `A2:A3`), source label from the child's own
  cell-above/left, editable name, Include checkbox, no role toggle.
- The anchor's row is always present when any slice of it is, even if nothing
  references the anchor directly.
- Straddling range rows carry a warning marker and tooltip.
- A one-line note when any slice row exists, stating that slice positions are
  fixed at gather time.

## User Stories

- As a model author, I want a spilled interim result and the individual cells I
  read out of it to gather into one array binding plus named slices, so that my
  LET expresses the calculation I actually built.
- As an author, I want to keep laying out calculations cell-by-cell with spills
  in the middle, so that I don't have to write everything dynamically up front
  just to make gather work.
- As an author, I want the slice names to come from the labels I already wrote
  next to those cells, so that the LET reads the way my sheet reads.
- As an author, I want to be told when part of my calculation could not be made
  dynamic, so that I know which references still point at the sheet.

## Acceptance Criteria

- [ ] `ICellSource` exposes `SpillInfo? GetSpill(CellRef)` returning the anchor
      and the spill rectangle's dimensions; `HasSpill(CellRef)` is removed and
      all call sites migrated.
- [ ] The live adapter implements `GetSpill` via `Range.SpillParent` /
      `Range.SpillingToRange`, returning null for plain cells, external refs,
      unreachable sheets, and on any COM failure (logged).
- [ ] `StubCellSource` models spill geometry so the engine is testable without
      Excel.
- [ ] The slice-expression generator is a pure, independently testable function
      of (R, C, r1, r2, c1, c2, ref shape).
- [ ] `A1#`, and a range exactly equal to a spill of 2+ cells, both rewrite to
      the anchor's binding name, with no additional binding row.
- [ ] A single-cell reference to a spilling anchor (`A1`) emits
      `INDEX(arr,1,1)` — the pre-existing silent widening to the whole array is
      fixed.
- [ ] A single-cell reference to a spill child at (r,c) emits `INDEX(arr,r,c)`,
      positionally, including when the cell is flush to an edge.
- [ ] A band flush to the top or left emits positive `TAKE`; a band flush to the
      bottom or right emits negative `TAKE` with no counting; an interior band
      emits `TAKE(DROP(...))`.
- [ ] Blocks constrained on both axes compose into at most one `DROP` and one
      `TAKE`, with full-span arguments omitted (trailing dropped, interior
      rendered as a bare comma).
- [ ] A 1×1 spill emits `arr` for `A1#`, and `INDEX(arr,1,1)` for both `A1` and
      the range `A1:A1` — the single-cell rule is checked before the whole-array
      rule, so a range that is simultaneously one cell and the whole spill takes
      the scalar path.
- [ ] A degenerate single-cell range (`A2:A2`) takes the single-cell path.
- [ ] No input to the slice generator produces a 1×1 array: `TAKE` is emitted
      only for rectangles spanning more than one cell.
- [ ] Each distinct referenced slice becomes its own binding row, ordered
      immediately after its anchor's row, with `BindingRow.SliceOf` set.
- [ ] Slice rows name from cell-above, then cell-left, then
      `<anchorName>_<rowMajorIndex>`; collisions suffix as today; names are
      editable and live-validated.
- [ ] A slice referenced from several steps produces one binding row, referenced
      by name from each.
- [ ] A reference to a spill child whose anchor is not otherwise in the walk
      pulls the anchor in as a precedent; the walker recurses into the anchor,
      never the child.
- [ ] A range wholly inside a spill never promotes to a range input.
- [ ] A range straddling a spill boundary promotes to a literal range input and
      the dialog marks the row with a warning; no diagnostic, no refusal.
- [ ] A spill anchor is never dropped from the bindings by range coverage.
- [ ] Excluding a slice row reverts that reference to a literal cell ref;
      excluding an anchor drops all its slice rows and reverts all of them.
- [ ] Demoting an anchor to an input keeps every slice of it working.
- [ ] Spill children in a multi-selection normalise to their anchor before the
      multi-sink check and before `restrictTo` is built.
- [ ] A sink that is a spill child produces a `SpillChildSink` diagnostic naming
      the anchor; no LET is produced.
- [ ] `CellGraphWalker` no longer normalises the spill flag away; `A1` and `A1#`
      remain distinct precedents with distinct lookup entries.
- [ ] Step formulas continue to receive only bare identifiers from
      `CellRefExtractor.Rewrite` — no slice expression is ever substituted into
      a step formula.
- [ ] `/Refactor` and `/Unnest` output is unchanged by this work (regression
      tests over their existing suites).
- [ ] The synthesised LET parses cleanly via `LetParser` in every case above.
- [ ] The dialog shows slice rows indented under their anchor, with no role
      toggle, and a one-line note about fixed slice positions when any slice row
      exists.

## Out of Scope

- **Fill-down blocks.** `C2:C10` each holding `=B2*2`, summed at the sink, still
  promotes to a single range input rather than being recognised as
  `MAP(range, LAMBDA(...))`. Different problem — pattern-matching R1C1-identical
  formula blocks, with its own failure modes (non-uniform blocks, absolute refs,
  multi-column fills). Revisit once this ships and the fill-down case has been
  felt in anger.
- **Legacy CSE array formulas.** Ctrl+Shift+Enter blocks have the same shape as
  a spill but are detected via `Range.HasArray` / `Range.CurrentArray`, not
  `SpillParent`. The slice machinery would carry over; the probe would not.
- **Implicit intersection.** `@A1#` and `@A1` resolve positionally against the
  formula's own row/column and could in principle be rewritten to
  `INDEX(arr,k,1)` at gather time, but `CellRefExtractor` does not capture the
  `@` prefix and widening the regex is its own piece of work.
- **Shape-robust slice expressions.** `INDEX(arr,1,COLUMNS(arr))` and friends.
  Positions freeze at gather time, deliberately.
- **A defensive `@` prefix on single-cell slices.** `INDEX(arr,r,c)` with two
  positive literal indices already returns a scalar; `INDEX` only widens when an
  index is 0, omitted, or itself an array, and the generator emits none of
  those. `@` would therefore be redundant in every form v1 produces. It would
  also *mask* generator bugs: a mis-computed rectangle currently fails loudly (a
  spill, an array where a scalar was expected), whereas `@INDEX(...)` would
  quietly return the top-left element and look plausible all the way through
  `/LetToLambda`. Rule 1's ordering above is what actually guarantees the scalar
  — `@` would be belt over a working brace.
- **Reference-only contexts.** A binding name is a value, not a reference, so
  `ROW(B1)`, `OFFSET(B1,…)`, and `COUNTIF(B1:B3,…)` break when their argument
  becomes a binding. This limitation already exists in spec 0005 for ordinary
  bindings and is not made worse here; documented, not detected.
- **Non-contiguous / multi-area references** (`(A1,B3)`) and structured table
  references (`Table1[Col]`) — existing extractor limitations, unchanged.
- **Re-gathering an existing LET** to refresh from the current state of the
  source cells.

## Open Questions

- ~~**COM semantics spike — must run first.**~~ **Resolved (PR 1, #353).** Run
  through the AddinTests harness against a live Excel 365; the probe is kept as
  a regression guard in
  `addin/lambda-boss.AddinTests/SpillComSemanticsTests.cs`. Sheet: `A1` holds
  `=SEQUENCE(2,3)`, spilling into `A1:C2`; `E1` holds `=A1*2`; `E2` holds the
  literal `42`.

  | Cell | Kind | `HasSpill` | `HasFormula` | `Formula2` | `SpillParent` | `SpillingToRange` |
  |---|---|---|---|---|---|---|
  | `A1` | spill anchor | `True` | `True` | `=SEQUENCE(2,3)` | `A1` | `A1:C2` (2×3) |
  | `B1` | spill child | `True` | `False` | `""` | `A1` | `null` |
  | `C2` | spill child (corner) | `True` | `False` | `""` | `A1` | `null` |
  | `E1` | plain formula | `False` | `True` | `=A1*2` | `null` | `null` |
  | `E2` | plain literal | `False` | `False` | `42` | `null` | `null` |

  `HasArray` is `False` on all five — dynamic-array spills are not legacy CSE
  arrays, as *Out of Scope* assumes.

  Every assumption in the design holds:

  - **`HasFormula` is false on a spill child**, and `Formula2` returns the empty
    string rather than the anchor's formula text. The *Problem* section stands
    as written: a child presents to the walker as a leaf **input**, not as a
    duplicated step, and the "broken `B1#` RHS" defect is real — `HasSpill` is
    `True` on children too, so today's `#` suffix does land on a non-anchor
    cell.
  - **`SpillParent` resolves the anchor from both the anchor and any child**,
    and returns **`null`** (rather than raising a COM error) on cells that
    aren't part of a spill. `GetSpill` therefore branches on null; the
    `try`/`catch` is only a safety net for pre-365 builds where the property
    doesn't exist at all.
- ~~**`SpillingToRange` on a child.**~~ **Resolved (PR 1, #353).**
  `SpillingToRange` returns **`null`** on a spill child (it does not error, and
  it does not return the parent's rectangle). The anchor hop is therefore
  **mandatory, not a fallback**: `GetSpill` reads `SpillParent`, then reads
  `SpillingToRange` from the anchor range.

  Measured cost, 200 iterations each, driving Excel **out of process** from the
  test harness — the shipped XLL is in-process and pays materially less per
  property, so these are an upper bound and only the ratios matter:

  | Probe | ms/op |
  |---|---|
  | child, via the anchor hop (what `GetSpill` does) | 7.9 |
  | anchor, its own rectangle | 6.3 |
  | child, if the anchor's rectangle were memoed | 2.8 |
  | plain cell (`SpillParent` → null) | 1.3 |
  | today's `HasSpill` alone | 1.3 |

  **Memoised per walk** (amended after implementation; v1 planned no memo).
  The original reasoning was that the hop itself is cheap (7.9 vs 6.3 — the
  geometry read dominates, not the extra dereference) and that the walker
  probes each cell exactly once, so an anchor-keyed memo would only pay when
  two or more children of the same anchor appear in one walk. The second half
  of that was wrong: the precedent loop probes once per precedent
  *occurrence*, not once per cell, so a cell referenced by three steps was
  paid for three times — on spill-free sheets as much as spilling ones. The
  delivered walker therefore memoises `ICellSource.GetSpill` for the duration
  of a single `Walk` (`CellGraphWalker.GetSpillMemo`). Staleness is a
  non-issue: one walk is already a single snapshot of the grid. Note also that
  `GetSpill` is no more expensive than `HasSpill` on plain cells (both ~1.3
  ms) — the extra cost is confined to cells that actually spill.
- **Slice rows for a spill the author never sliced.** If every reference to an
  anchor is `A1#`, no slice rows appear — correct. But if an author references
  `A1#` *and* `B1`, the LET carries both `arr` and `INDEX(arr,1,2)`. That's
  right, just worth eyeballing in the preview once it's real.
- **Dialog indentation vs the existing `IsExpansion` rows.** *Resolved during
  planning: they stay separate — that is what shipped.* Inner-LET expansion
  rows already hide their Include checkbox and belong to a host row. Slice rows
  are a second kind of child row with slightly different rules (they *do* have
  an Include checkbox), and orphan rows are a third. The plan kept all three as
  distinct row kinds rather than unifying them into one "child row" concept in
  the view model, because no two of the three share a rule set.
