# 0015 — /State from any form

Issue: #429 (design agreed with Tim, 9 Nov 2026). Builds on [0011](0011-state-step-authoring.md)
(StateStepParser, the edit engine, LET to LAMBDA for steps, Edit Lambda's `k` stepper) and
[0013](0013-intellisense.md) (library header metadata). Labs switch: **`state-any-form`**, "/State
from any form". It replaces the four 0011 switches (`state-let-to-lambda`, `state-scaffold`,
`state-edit-lambda`, `state-edit`) and also turns on the parts of them that stay: state steps in
LET to LAMBDA and Edit Lambda's stepper on a fold.

## Problem

Tim tried 0011's `/State` on 9 Nov and it didn't fit how he works. He moves between folded and
unfolded, and between inline and named, and wants to add, rename or remove a state variable from
whichever form he's in, with no typing of values or target cells. 0011 scaffolded a LET step into
a chosen cell from typed values and edited only that LET step. Everything else (folding, naming,
unfolding to debug) was a different command, and edits there didn't reach the seed.

State engines are his costliest slips (0011, Problem). The finals are on 1–2 Dec.

## What Tim does (journeys)

The examples are his `p`/`visited` walk and the R1 L12 robot (`p` 0, `b` 4; F forward, B back, C
charges, battery between 0 and 6).

**1. Start a fold.** On an empty cell, `/State`. Type `p`, Enter, `visited`. Form says REDUCE,
Name is blank. Save. The cell holds the shape with placeholders he types over:

```excel
=REDUCE(SetVars("p", "", "visited", ""), SEQUENCE(1), LAMBDA(a, v, LET(
    p, GetVar(a, "p"),
    visited, GetVar(a, "visited"),
    nextP, p,
    nextVisited, visited,
    SetVars("p", nextP, "visited", nextVisited)
)))
```

He overwrites the two `""`, `SEQUENCE(1)` and the two next lines. When the rules are a complex LET
he'd rather feel his way through one step first, he picks **LET step** as the form instead and gets
the unfolded step (journey 3's middle form).

**2. Add a variable mid-level.** The robot needs a charge counter. With the fold's cell selected,
`/State` opens with `p`, `b` and a blank row. He types `c` there and saves. The seed gets
`"c", ""`, the step gets `c_, GetVar(a, "c")` and `nextC, c_`, and the output passes `"c", nextC`.
His rules aren't touched. Rename is typing over a name (`b` → `bat` renames the key, the GetVar
line, `nextB` and every rule that reads them); remove is clearing it or ✕ (refused, with the lines
named, while a rule still reads it). The same works from every form below.

**3. Unfold, fix, refold.** The answer is wrong. `/State` on the fold, Form → **LET step**, Save.
The cell becomes the step with the fold's seed and inputs kept beside it:

```excel
=LET(
    _seed, SetVars("p", 0, "b", 4),
    _inputs, EXPLODE(G270),
    a, _seed,
    v, INDEX(_inputs, 1),
    p, GetVar(a, "p"),
    …rules…,
    SetVars("p", nextP, "b", nextB)
)
```

The cell shows the state after step 1. He edits `a`/`v` by hand to look elsewhere and fixes the
rule. `/State` again, Form → REDUCE, Save: the fold comes back around `_seed` and `_inputs` with
the rules as they now stand. Fold → LET step → fold gives the same text back.

When the step is **named**, the unfolded form has a `k` stepper, as Edit Lambda's does:

```excel
    k, 1,
    a, IF(k = 1, _seed, INDEX(SCAN(_seed, _inputs, moveRobot), k - 1)),
    v, INDEX(_inputs, k),
```

**Inline ⇄ named.** Type a name (`moveRobot`) and save: the step is stored as a named LAMBDA and
the cell becomes `=REDUCE(SetVars("p", 0, "b", 4), EXPLODE(G270), moveRobot)`. Clear the name: the
body comes back inline, and the dialog says `moveRobot` stays in the workbook (another cell may use
it).

**Library folds.** The Form list holds LET step, REDUCE, SCAN and every library fold, read from the
headers (REDUCEUNTIL, SCANUNTIL, REDUCEWHILE, SCANWHILE, SCANLEAD, FOLDROWS today). Picking one
writes its own argument order and step parameters: REDUCEUNTIL gives `LAMBDA(a, v, i, …)`;
FOLDROWS gives `FOLDROWS(inputs, LAMBDA(a, v, i, …), seed)`.

## Design

**One command, always on the active cell.** No values, no target cell, no cell pickers. The dialog
has three fields and a live preview of the cell (and of the named LAMBDA when there is one):

- **Variables:** names only, one per row, with a blank row to add.
- **Form:** LET step (unfolded), REDUCE, SCAN, then the library folds.
- **Name:** blank is inline; a name stores the step as a named LAMBDA.

**Reading the cell (`StateEngines.Read`).** In order:

1. A whole-cell LET that is a state step (0011 recognition) is the **LET step**. Its `_seed` and
   `_inputs` lines hold the fold's arguments. A stepper state line (`IF(k = 1, seed, INDEX(SCAN(…,
   step), k - 1))`) names the step. A 0011 step (`a, SetVars(…)`, `v, H270`) also reads: its
   state line is the seed and its input line the inputs.
2. Otherwise every fold call in the formula: `REDUCE`, `SCAN`, or a library fold (bare or
   prefixed) whose step is an inline `LAMBDA(…, LET(…))` or a workbook name whose definition is
   one, and whose LET is a SetVars state step. The first one is used; when there are several the
   dialog says so. A fold inside a bigger formula keeps the text around it.
3. Empty → a new engine. Anything else is refused with a message, as is an HSTACK state.

**Variables** are edited by 0011's edit engine (`StateEdit`) on an intermediate LET whose state
binding holds the seed, so the seed's `SetVars`, the GetVar lines, the next lines and the output
move together and the rules are never rewritten (except a rename, which follows the name). A new
variable's seed value is `""`.

**Form.** Changing the fold rewrites the wrapper only. Extra step parameters come from the fold's
metadata (`i` for REDUCEUNTIL); the state and input keep their own names. Dropping one the rules
read is refused. Fold → LET step writes `_seed`, `_inputs`, then the step (stepper when named); LET
step → fold puts them back in the fold's slots.

**Name.** Set: the step is stored under the name (the LET to LAMBDA layout, cell references made
absolute) and the fold calls it. Clear: the body comes back inline; the old name stays. A name that
is already a LAMBDA is replaced (the dialog says so); a name for anything else is refused.

**Library folds** are found from the 0013 header metadata, so there's no list to keep: a fold has a
parameter named `seed`, a parameter documented as `LAMBDA(x, y, …)` (the step and its parameters)
and another required parameter (the inputs). If the workbook doesn't have the fold yet, Save loads
its library first, as it loads Loops for a new engine.

## Defaults taken (Tim can reverse)

- A new engine's form is REDUCE, inline.
- Extra parameters keep the fold's names (`i`); the state and input keep the step's (`a`, `v`).
- The unfolded inline step binds an extra parameter to `1` (`i, 1`), and the named stepper to `k`.
  An unfolded line of that name holding a number or `k` becomes the parameter again on fold-back;
  folding to a fold that doesn't pass it leaves the line as an ordinary binding.
- The named stepper for a step with extra parameters runs SCAN over `SEQUENCE(k - 1)` and calls the
  step with `INDEX(_inputs, j)` and `j`; it ignores a stop or continue argument.
- Several folds in one cell: the first (outermost, then leftmost). COM can't see where the caret is.
- A fold inside a bigger formula can change variables, fold kind and name in place, but won't
  unfold: put it in its own cell first.
- Changing to a different fold drops the old fold's other arguments (a `stop` LAMBDA), and the
  dialog lists them. Keeping the same fold keeps them.
- Text is canonical: one binding per line, four-space indent. A fold Tim typed in another layout
  comes back in this one; after that, every round trip gives the same text.
- Named steps have cell references made absolute, as LET to LAMBDA does, so a relative reference in
  a rule comes back absolute when the step is brought inline.
- Save refuses if the cell changed while the dialog was open (Excel is free while a library loads).
- The k stepper and the one-sample LET step are kept as Edit Lambda's and LET to LAMBDA's 0011
  behaviour, now behind the one switch.

## Out of scope

- HSTACK / array states (refused, as in 0011).
- Picking a fold by caret position.
- The sheet-layout variants (#429's exploration): prototyped and reported separately for Tim to
  choose; neither is the default.

## Acceptance

- [x] `StateEngines.Read` reads an inline fold, a named fold, a library fold (bare or prefixed), the
      unfolded LET step (inline and named with the stepper) and a 0011 step; refuses HSTACK and
      non-steps; picks the first of several folds and says so.
- [x] Empty cell to each form, with the issue's exact text for REDUCE.
- [x] Add, rename and remove from each form (inline fold, named fold, LET step, named LET step)
      give the same step once brought back inline.
- [x] Fold ⇄ LET step and inline ⇄ named round trips give the same text; the same for a library
      fold with `i`, inline and named.
- [x] Library folds come from the real headers (REDUCEUNTIL, SCANUNTIL, FOLDROWS) with the right
      argument order and parameters.
- [ ] VM proof through the popup on Tim's walk and R1 L12 (screenshots in `specs/assets/0015-*`).
