# 0018 — /State: answer outputs in order, and the fold inside an outer LET

Issues: #461 (answer outputs), #460 (outer LET). Builds on [0015](0015-state-from-any-form.md). Same
Labs switch, `state-any-form`: both are part of /State.

## Problem

Tim's R4 State Reps v2 session (12 Nov 2026), building every level single-cell with /State and
the Debug round trip:

- **L2** wants `C<cash> S<crates> P<pitch>`. The state also holds a tip count. He split the output
  into helper columns and referenced the cells: three `GetVar`s round the fold felt too slow to type.
- **L1** wants `C<cash> S<crates>`, and his state was `crates, cash`. REVERSEARRAY was quicker than
  reordering the state.
- **Constants** (a cost, a price list) he hardcoded or carried as state. They belong in an outer
  LET, and he found the Debug round trip still works there. What else does?

## Answer outputs (#461)

Each variable row in the /State dialog has a tick. Ticked rows are the formula's answer, top to
bottom. Save writes the fold's result as those variables, in that order:

```excel
=LET(
    cost, 3,
    price, 5,
    GetVars(REDUCE(SetVars("crates", 0, "cash", 20), TEXTSPLIT(G55, ","), LAMBDA(a, v, LET(
        …rules…
    ))), {"cash","crates"})
)
```

One state gives one row (`25 | 5`); a column of states gives one row per state. Tim wraps it as
the level asks: `TEXTJOIN(" ", , {"C","S"} & …)`.

**Keys.** Alt+A ticks or unticks the row being typed in. Alt+Up and Alt+Down move it. Alt+Up/Down
is "move line" in VS Code and Visual Studio, so the hand already knows it, and a WPF text box does
nothing with it. Alt+A is A for answer; the dialog's only other access key is Alt+D (Debug). The
tick's label shows the place in the answer (1, 2, 3). The tick is not a Tab stop, so Tab still goes
name to name.

**Defaults taken:**
- On open, the ticked rows come first in answer order, then the rest in state order.
- Moving a row changes only the answer's order. The state's own order (seed, GetVar lines, next
  lines, output) never changes: the rules are never reordered, and REDUCE doesn't care.
- No ticks: no wrapper, exactly as before. Unticking every row takes the wrapper off and gives back
  the text it had.
- An answer that hasn't changed leaves the cell's text alone (Tim's own spacing in the array
  stays).
- A rename follows into the answer; removing a ticked variable unticks it.
- The LET step can't carry an answer: unfolding with ticks says "Untick them, or pick a fold."
- Ticking needs `GetVars`. A workbook whose Loops predates it gets Loops reloaded on Save.

**`GetVars(state, names)`** is new in Loops: the named variables as one row, in the order asked;
over a column of states, one row per state. A state is one column, so an input with more than one
column is taken to be SplitState's debug table and passed through unchanged. That is what makes
Debug work: Debug on puts `SplitState(SCAN(…), 1)` directly round the fold, inside `GetVars`, and
the cell shows the whole debug table. Debug off takes it out again, and the text comes back
exactly (#447). Nothing about the toggle changed.

## The fold inside an outer LET (#460)

/State keeps the text around the fold as it is, so most of this already worked. Tested in the
engine (`StateAnswerTests`) and in real Excel:

| In the outer LET, or where the fold sits | Add, rename, remove | Debug on/off | Name the step | Unfold to LET step |
|---|---|---|---|---|
| Constants, ranges, lookups (`cost, 3`, `price, XLOOKUP(…)`) | safe | safe, exact text back | not supported when the step reads them: refused with a message | not supported |
| A LAMBDA defined outside and called in the step | safe | safe | not supported (as above) | not supported |
| LETs nested any depth, fold the last expression | safe | safe | as above | not supported |
| Answer outputs ticked (`GetVars` round the fold) | safe | safe | as above | not supported (says untick) |
| Fold bound to a name, last line reads it (`s, REDUCE(…), "C" & GetVar(s, "cash")`) | safe: a rename follows the `GetVar` reads; a remove is blocked while one reads it | with care: the debug table lands in `s`, so the last line shows nonsense until Debug off; tick answer outputs instead | as above | not supported |
| The seed defined outside (`start, SetVars(…)`, `REDUCE(start, …)`) | with care: add writes `SetVar(start, "c", "")`; rename writes `SetVar(start, "money", GetVar(start, "cash"))`, so the old key stays in the state | safe | as above | not supported |
| Two folds in one formula | with care: /State edits the first and says so | with care: first fold only | as above | not supported |
| Fold inside MAP or BYROW over games | safe (inferred: same text handling) | not supported (inferred): a table per game is a nested array, #CALC! | refused when the step reads the LAMBDA's parameter | not supported |

**Fixed here (cheap gaps):**
- **Name the step** when it reads a name from round the fold (a LET above it, or a LAMBDA
  parameter): a named LAMBDA can't see them, so every cell calling it would show #NAME?. Now
  refused: "The step reads cost and price from the LET around the fold…". A step binding of the
  same name is its own and doesn't count.
- **Rename** follows `GetVar(…, "key")` and `GetVars(…, {…})` reads outside the fold.
- **Remove** is blocked while one of those reads the key: "The formula reads cash from the fold's
  result, outside the step. Take that read out first."

**Left as they are:** unfolding inside a bigger formula (refused, as 0015 says: Debug is the
round trip Tim uses), Debug on a fold bound to a name, and a seed defined outside.

## Doubts

- A rename follows every `GetVar`/`GetVars` key outside the fold, including one reading a second
  fold's state with the same key. Rare (one engine a cell), and the alternative is following none.
- `GetVars` pads an array-valued variable with #N/A, as HSTACK does. Answers are scalars.
- The dialog sends the answer only when the ticks change, so a key with no row to tick (typed by
  hand) survives a plain save or Debug toggle. Changing the ticks rewrites the array from the
  ticked rows, and such a key goes.
- The outer-name check is coarse: a LAMBDA parameter or LET name nested deeper in the step that
  shadows an outer name still counts, so naming is refused. The safe direction.
- `GetVars` tells a debug table from states by its first cell: a state always holds `CHAR(30)`. An
  empty state passes through as `""`.

## Acceptance

- [x] Tick, untick, reorder, rename and remove a ticked variable, Debug on/off with an answer, a new
      engine with an answer: unit tests (`StateAnswerTests`).
- [x] Outer LET: naming refused when the step reads outer names (LET and LAMBDA parameter), not
      when a step binding shadows them; rename follows and remove blocks on reads outside.
- [x] `GetVars` harness cases, including the debug table passing through.
- [x] VM: R4 v2 L1 and L2 single-cell with an outer LET for the constants, answer ticked and
      reordered in the dialog, matching the key; Debug on and off give the text back exactly.

## VM proof (12 Nov 2026)

Excel 16.0 build 20527 in `taglo-win`, the PR's CI XLL (after the review fixes), Labs channel,
driven through the popup (Ctrl+Shift+J, `State`) and the dialog by keys (UI Automation focus,
SendKeys). A copy of `R4 State Reps v2.xlsx`, a new sheet. Develop's Loops has no `GetVars` yet, so
the branch's definition was added by hand after /State loaded Loops bare.

- **L1.** /State on an empty cell, `crates` then `cash`: Loops loaded bare, the REDUCE scaffold
  written. The rules typed over it with `cost, 3` and `price, 5` in an outer LET. /State again:
  Alt+A on both rows, Alt+Up on `cash` ([01](assets/0018-01-l1-ticked-reordered.png)). Save wrote
  `GetVars(REDUCE(…), {"cash","crates"})` inside the LET; the cell spills `1 | 19`. Wrapped as
  `TEXTJOIN(" ", , {"C","S"} & …)` for games 1–6: **6/6 match the key**. Alt+D: the debug table
  inside GetVars ([02](assets/0018-02-l1-debug-inside-getvars.png)); Alt+D again: the text back,
  ordinal equal.
- **L2.** Same route, `crates, cash, pitch, count`; outer LET `cost, 3`, `prices, {5,8,6}`,
  `moveCost, 2`. Ticked crates, cash and pitch, `cash` moved up
  ([03](assets/0018-03-l2-ticked-reordered.png)): `{"cash","crates","pitch"}`, `77 | 7 | 1` for game
  7. Games 7–12: **6/6 match the key**. Debug on through the dialog, off through the popup's `Debug`
  command: the text back, ordinal equal.
- **Name refused** in the dialog: "The step reads cost, moveCost and prices from the LET around the
  fold…".
- `GetVars` cases in Excel: all as the harness file says, the one-column debug table included. A
  missing name gives `#N/A` for the whole row: GetVar's `#N/A` spreads through HSTACK (even
  `HSTACK(1, GetVar(s, "zz"))` is one `#N/A`).

**Found, not from this change:** with `TEXTSPLIT(log, ",")` as the inputs (a row), SCAN gives a row
of states and SplitState reads only the first, so the debug table shows step 1 only (screenshot
02). Filed as #463.
