# 0019 — Gather, next: match and beat OA Robot's Generate LET

Labs switch `gather-next`. With it off, `/Gather` is exactly as specs 0005 and 0010 left it.

## Problem

Tim's aim for the MEWC finals (1–2 Dec): match, then beat, OA Robot's **Generate LET Statement**
and its **Save Lambda** flow, which most top players use. OA Robot works in two steps: mark the
input cells (Ctrl+Shift+M, a yellow-fill format), then run the command on the result cell. Gather
should need no marking step: inputs are chosen in its one dialog.

What OA Robot does is recorded from six of Diarmuid Early's videos in excel-esports
`analysis/2026-11-13-oa-robot-generate-let.md` (13 observed uses). In short: it walks from the
result cell to the marked cells; names come from the nearest label above (skipping blank rows);
the result is `_Result`; Generate LET gives a LET whose inputs are relative cell refs, which Dim
pastes into his answer cell and fills down; Save Lambda gives
`=LAMBDA(params, LET(...))(args)`, then a named LAMBDA, and the cell becomes `=name(args)`.

We replicated five of those uses in real Excel with today's Gather (dev-latest 225db52, VM
`taglo-win`, 12 Nov). Gather was correct on all five once used well, and better in places (named
ranges and table refs stay as written; general slices; dependency order; the LET lands in the
answer cell, so filling down needs no cut and paste). It lost on four things:

1. **A range over formula cells stops the walk.** Dim's own last step, `=MAX(AL83:AM83)` over two
   helper cells, gathered to `LET(step_1, AO83:AP83, MAX(step_1))`: nothing gathered. OA Robot
   walked into the range (`MAX(HSTACK(_Step10, _Step11))`). Spec 0005 promotes any range to one
   input, which is right for data and wrong for a block of steps.
2. **Names.** Every unlabelled cell is `step_N`, inputs included. A label two rows up ("Trump
   Suit" over a blank row) is missed. Worse, a slice took its name from a card value in the
   cell to its left (`kH`), because the left neighbour was the spill's own data. A slice under
   its anchor's label came out `coords_2`, `coords_2_2`.
3. **LAMBDA takes two dialogs and typing.** Gather → Save → `/LET to LAMBDA` → name → Save →
   type `=name(args)` by hand. Dim: one Parameter Setup dialog, one name box, and the cell
   becomes the call.
4. **A marked input that is a formula.** Dim marked `X119` (`=fromPaper(G134,0,7)`) as `arr`;
   Gather walked through it and made `G134` the input. The row's role toggle fixes it in one
   click (tested), so this needs only a keyboard path, not a new mechanism.

## Proposed solution

All behind `gather-next`.

### 1. Ranges over formula cells are walked

When a step references a range (not a spill, not inside a spill) and at least one cell in the
range holds a formula that is in scope, the range is **not** promoted. Each cell becomes a
precedent like a single-cell reference (a formula cell is a step, a literal cell is an input),
and the reference is rewritten to a stack of the cells' bindings:

- one row → `HSTACK(a, b, c)`; one column → `VSTACK(a, b, c)`;
- a block → `VSTACK(HSTACK(r1c1, r1c2), HSTACK(r2c1, r2c2))`.

The stack is its own binding row, always named `<first cell's name>_s` (its label would be its first cell's own label, which only collides), so
step formulas still receive bare names. Limit: 64 cells; beyond that the range promotes as today.
A range whose cells are all literals promotes exactly as today (data stays a range input).

### 2. Names

- **Label above, skipping blanks:** the nearest non-empty cell above, up to three rows, if it is
  text and **not part of the calculation** (not a walked cell, not inside a walked spill, not
  inside an input range). Else the cell to the left under the same rule. Else the fallback.
- **Fallbacks** say what the row is: `input_N` for inputs, `step_N` for steps (numbered
  separately, no gaps).
- **Slices:** a slice whose label would be its own anchor's label (the same cell) takes the
  anchor-derived name `<anchor>_<n>` (spec 0010), never a collision suffix.
- The sink keeps today's rule (it is the LET body, unnamed).

### 3. LAMBDA from the same dialog

A **Lambda name** box at the top of the Gather dialog, empty by default.

- Empty: Save writes the LET into the sink, as today.
- A name: Save registers `=LAMBDA(<inputs in list order>, LET(<steps>, <body>))` under that
  name (through `LetToLambdaBuilder` and `LambdaLoader.InjectLambda`, as `/LET to LAMBDA` does)
  and writes `=name(<input refs>)` into the sink, so the cell evaluates on the example at once.
  Input refs keep their written form (`K287#`, `Q57:T60`, relative).
- Parameter order is the input rows' order. **Alt+Up / Alt+Down** on an input row moves it
  among the inputs (steps keep topological order).
- The name is validated live like `/LET to LAMBDA`'s (Excel name rules, existing LAMBDA names
  overwrite with the same warning).
- Name box first in tab order, so `Ctrl+Shift+J`, `ga`, `Enter`, type the name, `Ctrl+Enter`
  is the whole LAMBDA flow.

### 4. Keyboard role toggle

On a focused row, **Alt+I** toggles input/step (the existing toggle, where the row allows it).

## Acceptance criteria

- [ ] `Labs.GatherNext` (`gather-next`) in `Labs.All`; off = today's output for every existing
      Gather test.
- [ ] Range over in-scope formula cells: walked, rewritten to an `HSTACK`/`VSTACK` stack row;
      Dim's `=MAX(AO83:AP83)` layout gathers the whole calculation and evaluates the same.
- [ ] All-literal ranges, spill ranges and ranges over 64 cells: unchanged.
- [ ] Labels: skip up to two blank rows; reject cells that are part of the calculation; inputs
      fall back to `input_N`; slice names never collide with their own anchor's label.
- [ ] Lambda name box: empty keeps today's Save; a name registers the LAMBDA and writes
      `=name(args)`; Alt+Up/Down reorders inputs = parameters; invalid names block Save.
- [ ] Alt+I toggles the focused row's role.
- [ ] Unit tests for each; the VM trials (five Dim layouts) rerun with the switch on.

## Out of scope

- A marking command or formatting (Tim: inputs are chosen in the one dialog).
- Optional parameters (`ISOMITTED`): Dim never used OA Robot's → Optl in 13 uses; `/LET to
  LAMBDA` still does it.
- Hoisting table columns and workbook names into steps (OA Robot does; it adds noise, e.g. a
  40-character name).
- Writing the LET to another cell than the sink. Building the result in the answer cell makes the
  LET land there; listed below as an option if the trials show the regenerate loop hurts.

## Open options (decided by the VM trials, not by Tim unless they tie)

- **Regenerate after a fix.** Dim regenerates the LET three times while debugging (helpers stay
  live). Gather overwrites its sink. Options: (a) as today, Ctrl+Z then re-gather; (b) a
  "Write to" box defaulting to the sink. Default (a) unless the trials show (b) saves real time.
