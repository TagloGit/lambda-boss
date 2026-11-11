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
- A fold's other arguments (a `stop` or `continue` LAMBDA) carry over by name: REDUCEUNTIL's
  `stop` goes to SCANUNTIL's `stop`, and the unfolded step keeps them as `_stop, …` lines under
  `_inputs`. One the new fold has no slot for is dropped and the dialog says so. A required one the
  step doesn't have (REDUCEWHILE's `continue`) gets a placeholder, `LAMBDA(a, v, i, TRUE)`, and a note.
- In the unfolded step, `_seed` and `_inputs` are the fold's arguments whatever `a` and `v` say, so
  editing `a`/`v` to look at another step never changes the seed or inputs on fold-back.
- An optional seed left out (FOLDROWS) stays out: adding a variable doesn't invent one.
- Only a line right after the input line holding a number or `k` (what the unfolded form writes for
  `i`) becomes a step parameter again. A rule named `i` anywhere else in a fold keeps its meaning,
  and the new parameter is `i_`.
- Changing the variables of a named step that's already stored updates it for every cell that calls
  it; the dialog says so. If Excel refuses the cell, the old definition is put back.
- Whoever had any of the four 0011 switches on gets `state-any-form` on.
- Text is canonical: one binding per line, four-space indent. A fold Tim typed in another layout
  comes back in this one; after that, every round trip gives the same text.
- Named steps have cell references made absolute, as LET to LAMBDA does, so a relative reference in
  a rule comes back absolute when the step is brought inline.
- Save refuses if the cell changed while the dialog was open (Excel is free while a library loads).
- The k stepper and the one-sample LET step are kept as Edit Lambda's and LET to LAMBDA's 0011
  behaviour, now behind the one switch.

## Step outputs: a named output and a guarded step (#438, #443)

The step's output is the SetVars call (or a SetVar chain the 0011 parser already accepts), but
it need not be the whole LET body. Recognised everywhere a state step is read: the 0011 parser,
`/State` (inline fold, named fold, LET step, library folds), Edit state, LET to LAMBDA and Edit
Lambda's `k` stepper.

- **A named output (#438).** The body is a bare name bound, in the same LET and above the body,
  to the SetVars call: `out, SetVars("visited", nextVisited, "p", nextP), out`.
- **A guarded step (#443).** The body is `IF(cond, a, SetVars(...))`, or `IF(cond, SetVars(...), a)`,
  where `a` is the step's state (the fold's state parameter; in the unfolded LET step, the state
  binding). The guard is a break clause: it hands the state back unchanged.
- **Combined.** The same guard around a name (`IF(cond, a, out)`, `IF(cond, out, a)`), or inside
  the named binding (`out, IF(cond, a, SetVars(...)), out`). A guard nested deeper
  (`IF(c1, a, IF(c2, a, SetVars(...)))`) reads too.
- **Where it sits does not matter.** A fold with such a step inside a bigger formula
  (`=SplitState(REDUCE(...), 1)`) keeps the text around it, as before.

**Edits rewrite only the SetVars call.** Adding, renaming and removing a variable change the
pairs inside the call and the lines around the step as usual. The `out` binding's name and the
bare `out` body stay, and so do the guard, its condition and the swapped branches. A rename also
reaches the guard's condition (`GetVar(a, "p")` and the renamed binding names); a removal is
blocked when the guard's condition still reads the key, like any other line.

**Defaults taken:**
- The other branch of a guard must be a bare name equal to the state. Any other `IF` is not a
  step (it could be anything).
- Only SetVars outputs are read through a name or a guard. An HSTACK/VSTACK array output still
  has to be the bare body, so no ordinary LET that ends in a name bound to an HSTACK becomes a
  step.
- A name that resolves to the state's own binding is not an output.
- Keys the output sets that nothing reads back with `GetVar` (the `items` pair in #443) are listed
  as variables like any other pair. #440 treats them as debug-only (next section).

## Debug toggle (#440)

Tim debugs an inline fold by switching `REDUCE` to `SCAN` (`REDUCEUNTIL` to `SCANUNTIL`,
`REDUCEWHILE` to `SCANWHILE`), wrapping it in `SplitState(…, 1)` (headers on) and adding temporary
outputs to the final SetVars (`"item", item, "c", c, "w", w`). The dialog does it in one go, both
ways. Logic is `StateDebug.cs` and `StateEngines.Apply`; the window only wires it.

A toggle with no other change works on the cell's own text (#447): only the fold's name, the
wrapper and the dropped debug pairs change, so Debug on then off gives back Tim's exact text,
spacing included. A toggle saved together with a variable, name or form edit is laid out afresh.

```excel
=SplitState(SCAN(SetVars("visited", G150, "p", G150), SEQUENCE(5), LAMBDA(a, v, LET(
    …rules…,
    SetVars("visited", nextVisited, "p", nextP, "item", item, "c", c, "w", w)
))), 1)
```

**The debug form.** A SCAN-family fold (name starts `SCAN`: SCAN, SCANUNTIL, SCANWHILE, SCANLEAD)
that is the first argument of `SplitState(…, 1)` (bare or with the library prefix; `TRUE` also
reads as headers on). The fold can sit inside a bigger formula, and the wrapper is found from the
text either side of the fold, so it works for inline and named steps and for the #438/#443 forms
(named output, guarded step). Anything else is not debug: a SCAN with no wrapper is Tim's own and is
left alone; a REDUCE inside `SplitState` is a normal fold; `SplitState(…, 0)` is not the toggle's.

**How the way back is remembered: it isn't.** There is no stored state. The fold kind is read off
the form: a debug SCAN goes back to REDUCE, SCANUNTIL to REDUCEUNTIL, SCANWHILE to REDUCEWHILE
(`StateDebug.Twin`; a library fold has to be one the popup has read). A SCAN-family fold with no
REDUCE twin (SCANLEAD) loses only the wrapper on the way out and stays as it is. Going in needs a
SCAN twin: a fold without one (FOLDROWS) is refused with a message.

**Debug-only outputs.** A key the final SetVars sets that is never read back (no
`GetVar(…, "key")` anywhere in the step: rules, guard, stop or continue argument) and is not in
the seed. This applies to any form, debug or not, so the #443 `items` pair is one. They are not
state variables:

- The Variables list leaves them out. The dialog shows them as a grey line under the list,
  "Debug outputs, not state variables: item, c, w." (read-only; adding them from the UI is not
  built, since Tim types them in a second).
- Add, rename and remove of real variables leave them where they are. A new real variable goes
  after them in the output. A new variable can't take a debug output's name (an error says so).
- A key that is in the seed counts as a variable even if nothing reads it.
- A key also counts as read when a string literal of that name appears in the text before or
  after the fold (`=GetVar(REDUCE(…), "ans")`): the result is read from outside the step.
- Conservative: if the step uses `GetVarAt`, or a `GetVar` whose key isn't a string literal, no
  key can be shown to be unread, so there are no debug-only keys at all.
- Renaming a variable to a debug output's name gets the same "already a debug output" error as
  adding one.

**Enter debug (Debug checkbox on, or `StateDebug.Toggle(engine, true, kinds)`).** The fold switches
to its SCAN twin and `SplitState(` … `, 1)` goes round it, spelled with the state library's prefix
(`loops.SplitState` when the step uses `loops.SetVars`). The output shows everything (next
section, #455). The seed, inputs, rules and name stay. A fold that is already the first argument of
`SplitState(…, 1)` (a REDUCE written that way) is not wrapped again: entering only switches it to
its SCAN twin and keeps the wrapper.

**Exit debug (checkbox off, or `Toggle(engine, false, kinds)`).** The wrapper comes off, the fold
goes back to its REDUCE twin, and the debug-only outputs are removed from the SetVars call. The
lines that computed them (`item, …`) stay, so the rules are never touched, and the dialog says
which outputs it dropped. Normal, then debug, then normal gives the same text back. Exit on a form
that isn't debug (a plain SCAN, or a REDUCE already in `SplitState`) changes nothing: the twin
mapping back applies only to a form that opened as debug. If exiting would drop every output the
step has, it is refused with a message.

**Defaults taken:**
- The wrapper is written `, 1)` with a space. A debug form Tim typed (`,1)`) keeps its own spacing
  until it is toggled out.
- The checkbox switches the Form box to the twin, and the Form box can still be changed afterwards.
  Unticking returns to the form the dialog opened with: the REDUCE twin only when it opened in
  debug form, otherwise the original fold (a plain SCAN stays a SCAN). The grey debug-outputs line
  adds "Save drops them." while an opened debug form is unticked.
  Debug with the form on LET step, or on a fold with no SCAN twin, shows an error and can't be saved.
- Leaving debug outputs in a normal REDUCE is allowed; only the exit command drops them.
- The Debug checkbox is under the same Labs switch, `state-any-form`.

## Debug shows everything (#455)

Tim's R4 retry (11 Nov): a SCAN in debug form showed `#VALUE!` in every cell. He typed the
intermediates into the output, then the incoming `crates` and `cash`, and only then saw the seed
was `SetVars("crates", "", "cash", "")`. The typing was the slow part. Now entering debug writes
them for him:

```excel
=SplitState(SCAN(SetVars("crates", "", "cash", ""), TEXTSPLIT("B4,S2,B9,S9,B5", ","), LAMBDA(a, v, LET(
    crates, GetVar(a, "crates"),
    cash, GetVar(a, "cash"),
    qty, --MID(v, 2, 9),
    bought, IF(LEFT(v) = "B", crates + qty, crates),
    nextCrates, IF(LEFT(v) = "S", bought - qty, bought),
    nextCash, cash + IF(LEFT(v) = "B", -3, 5) * qty,
    SetVars("in.crates", crates, "in.cash", cash, "qty", qty, "bought", bought, "crates", nextCrates, "cash", nextCash)
))), 1)
```

- **Incoming values** come first: each state variable's `GetVar(a, …)` line, under `in.<key>`
  (`in.crates`). The dot is legal in a SetVars key and in SplitState's header, can't clash with a
  LET name, and reads as "crates coming in". Only the GetVar lines: a key read inline has no
  line to show.
- **Every LET intermediate** comes next, under its binding name, in the step's order.
- **Then Tim's own outputs**, as he wrote them; any debug outputs he had typed stay after them.
- **Left out:** a line that may hold a function (a LAMBDA, a LET ending in one, or a bare name
  that isn't the step's own, such as a workbook LAMBDA: SetVars can't hold a function); the line
  that is the output (`out`, #438) and any line after it (a guard held in a later line would read
  itself); a next line already passed as an output's value (`nextCrates` is already the `crates`
  column); any key the output already sets (Tim's own `"item", item` stays where he put it); and
  any key that wouldn't read back as debug-only (a string literal outside the fold, a seed key),
  since Debug off couldn't take it out again.
- **A SetVar chain Tim wrote** on his SetVars keeps its text: the pairs go inside the SetVars.
- **Spacing** follows Tim's: the pairs go in after `SetVars(` with the separator he typed between
  his first key and value (`,` in a compact fold, `, ` otherwise).
- **Past ten pairs**, the ones that don't fit go round the call as SetVar layers, after the real
  outputs (SetVars takes ten).
- They are **debug-only outputs** (nothing reads them, they aren't in the seed), so the Variables
  list leaves them out and **Debug off removes them all**, SetVar layers included, giving back the
  cell Tim wrote character for character. He can delete any of them by hand while in debug; off
  cleans up what's left. No per-step choice (Tim: overkill).
- A **named step** gets them in its definition, so every cell calling it outputs them until Debug
  off; the dialog's "Save updates …" note says so.
- Debug on saved with a variable edit shows the new variable too (`in.days`).
- **Doubts.** Debug off drops every debug-only output, including one Tim typed before entering
  debug (as #440 always has). When the output's text also appears elsewhere in the cell (an
  output identical to the seed), the toggle lays the cell out afresh rather than keeping Tim's
  spacing (as #447 does).
- **VM proof (11 Nov, build of #457, Excel 16 in `taglo-win`).** Driven through the popup
  (Ctrl+Shift+L, `/State`) and the dialog. On an empty workbook, /State loaded Loops bare
  (`SetVars` in Name Manager, no `loops.SetVars`) and wrote `=REDUCE(SetVars("crates", "", "cash",
  ""), …)`. Tim's case: the dialog showed the `""` seed note; Debug on spilled
  `in.crates | in.cash | qty | bought | crates | cash` over `(blank) | (blank) | 4 | #VALUE! |
  #VALUE! | #VALUE!`, so the blank seed and the first error on `bought` are in one row. Debug off
  gave back the cell's text exactly (`Formula2` compared ordinal).

**A `""` seed used in arithmetic.** When a seed value is `""` and a rule adds, subtracts,
multiplies, divides or raises that variable's GetVar line, the /State dialog says so: "crates and
cash start as "" in the seed, and bought, nextCash adds or multiplies with them: "" + a number is
#VALUE!. Seed a number (0) instead." Read-only. A fresh scaffold (`nextP, p`) never trips it.

## Loops loads bare (#456)

/State on a workbook without Loops loads it with no prefix and writes bare calls (`SetVars`,
`GetVar`, `SplitState`, `REDUCEUNTIL`). Tim always works with bare names. The prefix path stays
only when the workbook already holds a prefixed Loops: that copy is the workbook's choice, and a
library fold it hasn't loaded yet is loaded under the same prefix. Other libraries keep their
default prefix. (The 10 Nov VM proof below predates this: it loaded `loops.`.)

## Exploration: sheet layouts (10 Nov 2026, for Tim to choose)

Two prototypes were built on the engine and run in `taglo-win` against the in-cell unfolded form
that shipped. Neither is in the add-in, and none is the default. The setup:

- The R1 L12 robot with a planted bug: `B` charges instead of `C`. It first shows at step 3.
- The inputs in four shapes: a column, a row, a 2-D block, and a string split into characters
  (`MID(s, SEQUENCE(LEN(s)), 1)`, which is what `EXPLODE` gives).

- **In-cell** ([x1](assets/0015-x1-in-cell.png)): the shipped LET step in the fold's own cell.
- **(i) Laid out to the right** ([x2](assets/0015-x2-layout-right.png)): a marker, a `k` cell,
  `TOCOL(inputs)` as a column, the state's starting values in cells, and a step cell reading them.
  The prototype never overwrites a filled cell: it looks for a free 16×5 block, starting two
  columns right of the fold and moving right past anything filled. It landed at `J2` because the
  walk's inputs fill column E, and at `J82` when a test put a value in `D82`. Fold-back clears
  the block.
- **(ii) One cell to the right** ([x3](assets/0015-x3-layout-one-cell.png)): the LET step alone,
  with `_for, "B2"` as its marker, the seed written out, and the first input hard-coded (`v, "F"`).

| | In-cell | (i) to the right | (ii) one cell |
|---|---|---|---|
| Unfold, Excel write time (ms) | 2–17 | 38–119 (11 cell writes; the inputs spill) | 3–22 |
| Fold-back after the fix, all four shapes | 4/4 | 4/4 | 4/4 |
| Column, row, string inputs | step 1 right | right | right |
| 2-D inputs | **`#REF!`** (`INDEX(_inputs, 1)`) | right (`TOCOL`) | right, since the first input is typed in as a literal |
| To look at step k | edit `a` and `v` by hand; named: change `k` | change `k`, but `a` stays the starting values | type `a` and `v` by hand |
| State at step k is the true one | named only (stepper) | **no**: `k`=3 gave `p`=-1, `b`=6 | no |

**Reading.**
- In this test (one engine, one planted bug, four input shapes), every form folded back correctly,
  and every write took Excel well under a quarter of a second.
- What separates them is whether you see the true state at step k. Only the named in-cell form has
  that (its `k` stepper runs the real steps). In layout (i), changing `k` shows step k applied to the
  starting values, which looks like stepping but isn't. It would need the step named or held as a
  LAMBDA in the block to be honest.
- Layout (ii) is the in-cell form in another cell with fewer `_` lines. It keeps the fold visible
  beside the step, which helps when comparing answers.

The screenshots are from the column case. x1 shows the step unfolded in `B4`, with the buggy rule,
at step 1. In x2 and x3, `B4` holds the fixed fold, because the in-cell run had already finished;
x2 has `k`=3. The formula bar shows only the first line of each step. An Excel tip covers the
bottom left of each image.

Timings are the tool's own write times. They are not how long Tim takes to find the bug; that
needs Tim, under the clock, in a rep.

**Found:** the shipped in-cell form gives `#REF!` at step 1 on a 2-D input, because
`INDEX(_inputs, 1)` needs a row number there. `INDEX(TOCOL(_inputs), 1)` would fix it but changes
the agreed text, so it's left for Tim.

## Doubts

- FOLDROWS appends the rows its step returns rather than carrying a state, so a SetVars step under
  FOLDROWS builds a column of states, and `INDEX(_inputs, 1)` on 2-D items isn't a row. It's in the
  list because the issue named it; Tim may prefer it left out.

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
- [x] VM proof through the popup on Tim's walk and R1 L12 (screenshots in `specs/assets/0015-*`).

## VM proof (10 Nov 2026)

Real Excel 16.0 build 20527 in `taglo-win`, with the PR's CI XLL and the Labs channel. Each step was
driven through the popup (Ctrl+Shift+L, `/State`, Enter), and the dialog through UI Automation.
The R1 L12 robot (`p` 0, `b` 4) ran on `G270` = `FFBCFFFXFBF`; Tim's walk `p`/`visited` ran over
`E2:E11`.

- **Empty cell → new engine** ([01](assets/0015-01-new-engine.png)). Loops wasn't in the workbook,
  so Save loaded it and wrote the issue's shape with `loops.`.
- **Empty cell → each form.** LET step ([11](assets/0015-11-empty-to-let-step.png)), REDUCE named,
  REDUCEUNTIL and FOLDROWS were all written and all evaluate.
- **Robot fold:** `p`=4, `b`=0, which matches the hand trace.
- **From the fold:** add `c` ([02](assets/0015-02-add-from-fold.png)), rename it to `charges`, then
  remove it. That gives the same text back. Removing `b` is blocked with "nextP reads b"
  ([03](assets/0015-03-remove-blocked.png)).
- **Fold → LET step** ([04](assets/0015-04-fold-to-let-step.png)). The cell shows step 1 (`p`=1,
  `b`=3). Add and remove from there ([05](assets/0015-05-add-from-let-step.png)), then refold: same
  text.
- **Inline → named** ([06](assets/0015-06-inline-to-named.png)). Then named → LET step with the
  `k` stepper ([07](assets/0015-07-named-to-let-k-stepper.png)). With `k`=3 the cell shows `p`=1,
  `b`=1, which is right. Add from there updates `moveRobot`. Refold gives the named text; clearing
  the name gives the inline text back, with the note ([08](assets/0015-08-named-to-inline.png)).
- **Library folds:** the Form list is read from the headers
  ([09](assets/0015-09-form-list.png)). SCANUNTIL writes `LAMBDA(a, v, i, …)`
  ([10](assets/0015-10-scanuntil.png)) and spills 11 rows ending at `p`=4. Back to REDUCE: same
  text.
- **Walk:** rename `visited` → `seen` and back; fold → LET step → fold; inline → named → inline.
  Every one gives the same text, and the `visited` trail stays `0,1,3,2,5,3,5,6,3,5,6`.
- **A cell inside a spill** gets a message ([12](assets/0015-12-spill-message.png)).

**Found in real Excel:**
- Excel writes `LAMBDA(a, v,` as `LAMBDA(a,v,`, so "the same text" means the text as Excel holds it.
  Repeated round trips are stable.
- Because of that spacing, the "Save updates moveRobot" note was showing when nothing had changed.
  Definitions are now compared by tokens, not layout. That fix came after the run.

**Not driven in the VM:**
- An empty cell → SCAN, and the HSTACK refusal. A message box left open by an earlier step blocked
  the script. Both are covered by unit tests.

## Engine fixes from Tim's 10 Nov notes (#437, #442)

**#437, a 1×1 `VSTACK` seed gives `#N/A`.** Not a bug in the current library. In real Excel (build
20527, VM), Tim's exact formula gives the right state with `develop` and with `main`. The same
formula gives `#N/A` only with the 12 Sep library (`f122534`, before #377), which matches the #407
report: that workbook held a stale copy of SetVar/GetVar. Re-load Loops into the workbook to fix
it (in Name Manager, a current `SetVar` contains `@INDEX(value, 1, 1)`). Harness cases now pin a 1×1
`VSTACK` seed, `DROP` to one row and a one-row `FILTER`, through `SetVars` and a `SetVar` chain,
under REDUCE.

**#442, errors in a state.** An error, or an array holding one, used to make the whole state an
error (TEXTJOIN can't join an error). Now:

- **Encoding.** When the plain encoding fails, the entry is tagged `e` and every element carries
  its own type: `n`, `b` or `t` before its text, or `CHAR(28)` and its `ERROR.TYPE`. A state with
  no errors is byte-for-byte what it was, and stays on SetVars' one-TEXTJOIN path.
- **GetVar** gives the error back as itself, in place (default chosen: real state behaves as
  before, so `IFERROR`/`ISNA` on a read still work). Codes 1–8 and 14 (`#CALC!`) round-trip
  exactly. `#SPILL!`, `#CONNECT!`, `#BLOCKED!`, `#UNKNOWN!` and `#FIELD!` (codes 9–13) can reach a
  state (`=A1` of a cell showing one) but can't be typed as a literal, so they come back as `#N/A`;
  SplitState still names them. A code past 14 (a newer error) also comes back as `#N/A`.
- **Caveats (review of #444).** A stored `#N/A` and a missing name both read as `#N/A`, so
  `IFNA(GetVar(…))` can't tell them apart. A mixed-type list without an error still takes its type
  from the first element (`{5;"x"}` reads back as `{5;#VALUE!}`), as before; with an error in it,
  every element keeps its type. That gap is old and left alone: closing it costs a type check per
  element on the hot path. An error adds one character per element, so a list just under 32,767
  characters can tip over the limit when it holds one.
- **SplitState** shows a list as `[B1, #VALUE!, A2]` (the error as text in place) and a single
  error as the error itself.
- **Cost.** One `ISERROR` per pair in SetVars and one tag compare in GetVar. The #405 benchmark
  (VM, 3 rounds × 7 recalcs) shows no slowdown: 10,000 steps, 5 values, `develop` 511/1028/950 ms
  against 447/384/366 ms for this change; 2 values 239/188/189 against 194/192/166. The runs were
  noisy (another job was building in the VM), so read it as "no measurable change".
- An old library reading a new state with an error entry gets wrong values (`n1`, `CHAR(28)2`), not an
  error: one more reason to re-load Loops into an old workbook.

