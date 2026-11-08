# 0011 — State-step authoring (SetVars / GetVar)

Issue: #386. Related, not absorbed: #384 (engine trace for array states), #385 (Unnest step names),
#382 (argument hints), #381 (hide Help in Edit Lambda), #387 (Help block cost, filed off this spec).

## Problem

State engines (a `REDUCE` over inputs, carrying several values from step to step) are Tim's
costliest slip kind under the clock: 10 logic slips, ~1,400 s elapsed across three recordings
(excel-esports `analysis/tim-slips.md`). He re-solved rep R1 L12 (a robot with position and
battery) with the Loops library's `SetVars`/`GetVar` and found it easy to reason about and easy to
debug. What slows him is the setup, which is rote and deterministic (his rebuild, from the
screenshots on #386):

```excel
=LET(
    a, SetVars("p", I266, "b", I267),          ← prototype state (or a, <cell holding a state>)
    v, I268,                                   ← one sample input
    p, GetVar(a, "p"),
    b, GetVar(a, "b"),
    nextP, p + IF(b > 0, SWITCH(v, "F", 1, "B", -1, 0)),
    nextB, MEDIAN(0, 6, IF(v = "C", b + 2, b - 1)),
    SetVars("p", nextP, "b", nextB)
)
```

then `LET to LAMBDA` as `moveRobot`, then `=REDUCE(SetVars("p", 0, "b", 4), EXPLODE(G270), moveRobot)`.

The R1 recording (excel-esports `cases/2026-11-05-reps-r1/videos/20261105-r1-reps-attempt-1.log.md`,
L12, rows n205–264) shows where the time goes in the version he wrote with `HSTACK`:

- **Pointing the prototype state** at the wrong row (`HSTACK(H268, I269)`, n206) — a reference slip.
- **Turning the step into a LAMBDA** (n224–229, ~30 s): LET to LAMBDA only offers value bindings as
  parameters, and `a` was bound to a formula, so he rewrote it through `input1`/`input2`, then bound
  `a` and `v` to constants by hand, then named it in Name Manager.
- **Debugging** (n240–264, ~5 min): SCAN over the `HSTACK` state raised Excel's nested-array message,
  so he built a step table by hand, then inlined the LAMBDA body into it ("a LAMBDA can't be
  stepped"), fixed the else-branch, and updated the definition in Name Manager twice.

Every step above is mechanical. The debugging stretch is the bigger lever: `tim-slips.md` says
each of the 10 logic slips was found "by tracing a step table by hand". A scaffold prevents typos,
not logic slips; a trace shortens the search for them. This spec makes `SetVars`/`GetVar` the
fastest way to write *and debug* a state engine under the clock: a one-call trace (which mostly
exists already), the step converted to `LAMBDA(a, v, …)` and back, a scaffold, and state edits
that keep all the lines together.

## Terms

- **State step** — a `=LET(...)` with a *state binding* read by `GetVar(state, "name")` lines and a
  body that is a `SetVars(...)` call (or a `SetVar` chain on the state). The shape above.
- **State binding** (`a`) — the binding the `GetVar` lines read. Its RHS is the *sample state*:
  a `SetVars(...)` call, a cell holding a state, or (round trip) an `INDEX(SCAN(...))`.
- **Input binding** (`v`) — the one sample input the step consumes.
- **State variables** — the names read by the `GetVar` lines, in order: `p`, `b`.
- **Next lines** — `nextP`, `nextB`: the bindings passed as values to the output `SetVars`.
- **Seed** — the state REDUCE starts from: `SetVars("p", 0, "b", 4)`.
- **Fold** — the cell formula that runs the step: `REDUCE(seed, inputs, stepName)`.

## Shared engine: `StateStepParser` (pure text, unit-testable)

All four add-in parts read and write the same shape, so they share one recogniser and one writer,
built on what exists:

- `LetParser.Parse` for the top-level bindings and body (it already splits at top-level commas and
  skips strings).
- `FormulaParser` (spec 0009's AST) to recognise calls inside a binding's RHS: `GetVar(S, "k")`,
  the body's `SetVars("k1", e1, …)`, and (round trip) a `REDUCE`/`SCAN` call anywhere in a cell
  formula.
- `LetToLambdaBuilder.ApplyRenames` for identifier renames (on whole tokens, so strings and
  references are skipped; #403).
- `FormulaFormatter.AppendLet` for output layout (one binding per line, 4-space indent).
- `ExcelNameValidator` + `LetNameSanitizer` for binding names.
- `EditLambdaCommand.BuildExpandedLet` for the LAMBDA → LET direction.

**Recognition rules.** A `LET` is a state step when:

1. its body is a call to `SetVars` whose name arguments are all string literals, **or** a chain of
   `SetVar` calls rooted at the state binding (`SetVar(SetVar(a, "p", nextP), "b", nextB)`, the
   partial-update form SetVars' own help points to); and
2. some binding `S` is the first argument of at least one `GetVar(S, "<literal>")` call in another
   binding's RHS. `S` is the state binding. If several bindings qualify, the one read by the most
   `GetVar` calls wins; ties go to the earliest.

Library names match only bare or prefixed exactly: `SetVars` or `<prefix>.SetVars` (so `MySetVars`
is not a match); the same for `SetVar` and `GetVar`.

The **input binding** is found in this order: a binding named `v`; else, among the bindings that are
not `S`, not a GetVar line, do not read `S` (directly or through a GetVar line) and are read by a
next line, the one whose RHS is a single cell reference or `INDEX(…)`; else the first of those in
LET order. Position doesn't matter: Tim's L12 drafts had `v` after the GetVar lines (n212–217). When
there is no candidate, the step has no input binding (rare; LET to LAMBDA then offers only `S`).
The LET to LAMBDA dialog shows which binding was picked and lets Tim move the `input` badge.

A SetVar-chain output is recognised for parts 3 and 4 (LET to LAMBDA, round trip); parts 1 and 2
write and edit only the `SetVars` form, and refuse a chain with a message.

A *canonical* GetVar line is a binding whose whole RHS is `GetVar(S, "k")`. A canonical next line is
a binding passed as a value in the output `SetVars`. Non-canonical lines (`p, GetVar(a, "p") + 0`,
a `GetVar` inline in an expression) are left untouched by every edit except a key rename, which
rewrites the key string in every `GetVar(S, …)` / `SetVars` / `SetVar` call on the state.

**Array family (parts 3 and 4 only).** The same rules with `HSTACK`/`VSTACK` in place of `SetVars`
and `INDEX(S, k)` / `NTH(S, k)` / `CHOOSECOLS(S, k)` in place of `GetVar(S, "k")`. Tim still writes
array states (the L12 slip above was one), and parts 3 and 4 cost little extra for them. Parts 1,
2 and 5 are SetVars-only.

## Part 1 — Scaffold a new state step (`/State`)

**Entry point.** A new popup slash command, `State` ("New state step: GetVar lines, next lines and
SetVars output from names and initial values"). Slash command only, no ribbon button: Tim reaches
for the popup under the clock, and the ribbon's Generate group holds the two LAMBDA commands.

**Input.** A one-line dialog (`StateStepWindow`) with three fields and a live preview:

- **State** — `name value` pairs separated by commas: `p 0, b 4` (also accepts `p=0, b=4`). Values
  are any formula text: `0`, `"N"`, `H268`, `{1;2}`.
- **Input** — the sample input (optional): `H270`, `"F"`. Blank → `""`.
- **Write to** — the target cell, defaulting as below.

**Prefill from the selection** (the fast path). When the selection is a block of 2 rows × k columns
(k ≤ 10) whose top row is text, the State field is prefilled from it: names from the top row
(through `LetNameSanitizer`, so "Battery Level" becomes `batteryLevel`; the sanitised name is used
for both the binding and the state key), values as references to the cells below. Tim's L12 layout (`p`, `b`
over `0`, `4` in H267:I268) gives `p H268, b I268`. Write-to then defaults to the first empty cell
below the block in its first column. With a single cell selected, the State field is empty and
Write-to is the active cell, which must be empty (else the dialog says so and keeps Save disabled
until a different target is typed).

**Output** (exact text, for `p 0, b 4`, input `H270`, no prefix loaded):

```excel
=LET(
    a, SetVars("p", 0, "b", 4),
    v, H270,
    p, GetVar(a, "p"),
    b, GetVar(a, "b"),
    nextP, p,
    nextB, b,
    SetVars("p", nextP, "b", nextB)
)
```

- With typed values the prototype state **is the seed**; with a selection prefill it points at the
  value cells. Either way the step evaluates from the first keystroke. Every next line starts as the identity (`nextP, p`): the cell shows a valid state at once,
  and Tim replaces each RHS with the rule. No `#NAME?` or `#VALUE!` while the step is half-written.
- Next-line names are `next` + the variable with its first letter upper-cased (`p` → `nextP`,
  `best` → `nextBest`), matching Tim's own convention.
- Enter saves, Escape cancels (same keys as the other dialogs). One Excel operation (`Formula2`).
  It doesn't get native undo: a COM write clears Excel's undo stack, as with every add-in command
  (checked in the VM: Ctrl+Z left the step in place).

**Edge cases.**

- *Binding names Excel rejects.* `c`, `r`, `C`, `R`, A1-style references (`b4`, `x1`, `ab12`) and
  R1C1-style ones (`R1C1`, `RC`, `R1C`, `RC1`) are invalid LET names. `ExcelNameValidator` is the
  source of truth for A1 and `RnCn`; the scaffold adds the `RC`/`RnC`/`RCn` forms.
  The binding is suffixed with `_` (`c_`, `b4_`); the state key stays as typed (`GetVar(a, "c")`).
  The preview shows it.
- *Clashes.* A name typed twice → error in the dialog. A `nextX` that equals another state name →
  `nextX2`. Names are compared case-insensitively (GetVar's own match is case-insensitive).
- *More than 10 variables.* `SetVars` takes 10 pairs. The 11th onward chain through `SetVar`:
  `SetVar(SetVars(…10 pairs…), "k", nextK)`.
- *Library not loaded.* The command looks for workbook names `SetVars`, `SetVar`, `GetVar` or
  `<prefix>.SetVars` etc. whose `RefersTo` starts with `=LAMBDA(` and emits the names as found (bare names preferred, else
  the prefix in use, e.g. `loops.GetVar`). If none are loaded it loads the Loops library's
  `SetVars`, `SetVar`, `GetVar` and `NewState` through the existing library-load path with the
  library's default prefix (`loops`) and uses that; if loading fails it refuses with a message.
- *Values that are formulas.* Taken verbatim (`p H268+1` → `SetVars("p", H268+1, …)`).

**Out of scope.** An `HSTACK` scaffold; writing the fold (part 3 does); picking names from the level
text.

## Part 2 — Edit state (add, remove, rename a variable)

**Entry point.** The same `/State` command with the active cell on a recognised state step. The
dialog opens in edit mode with one row per state variable (name, and the sample value when `S` is a
`SetVars(...)` call). Not a state step → the scaffold mode as in part 1.

**Add** `c 0`:

```excel
=LET(
    a, SetVars("p", 0, "b", 4, "c", 0),
    v, H270,
    p, GetVar(a, "p"),
    b, GetVar(a, "b"),
    c_, GetVar(a, "c"),
    nextP, p,
    nextB, b,
    nextC, c_,
    SetVars("p", nextP, "b", nextB, "c", nextC)
)
```

The GetVar line goes after the last GetVar line, the next line after the last next line, the pair
at the end of the output and of `S`. When `S` is not a `SetVars(...)` call (a cell holding a state,
an `INDEX(SCAN(...))`), the new key is layered on top so the sample state carries it:
`a, SetVar(I266, "c", 0)`. An 11th variable chains through `SetVar` in the output and in `S`, as in
part 1.

**Remove** `b`: deletes `b`'s GetVar line, its next line, its pair in the output and in `S`. If
another line still reads `b` or `nextB`, the dialog lists those lines and keeps Save disabled; the
tool never leaves a dangling name.

**Rename** `b` → `bat`: renames the bindings (`b` → `bat`, `nextB` → `nextBat`) through
`ApplyRenames`, so every expression that reads them follows, and rewrites the key string in every
`GetVar(S, …)`, the output `SetVars` and `S`'s `SetVars`. Strings elsewhere (`SWITCH(v, "b", …)`)
are not touched. `ApplyRenames` matches case-insensitively, which is right for Excel names (`b` and `B` are the same binding), so a rename that
would collide with another binding differing only in case is blocked like any clash. Tests cover
text near references: `$B$2`, `B2`, `Sheet1!B2`, `t[b]` must not change when `b` is renamed.

**Edge cases.** Lines that aren't canonical keep their place and text (only key renames reach
them). Bindings that are neither state nor next lines (`_note, "battery caps at 6"`, `maxB, $K$2`,
a nested `LET` in a next line's RHS) are preserved verbatim and in order. Reordering variables is
out of scope.

**Out of scope.** Updating the registered LAMBDA or any fold's seed: the step is re-registered by
LET to LAMBDA (part 3), and seeds in other cells are Tim's to edit.

## Part 3 — LET to LAMBDA that understands state

**Entry point.** The existing `LET to LAMBDA` command (ribbon and popup). No new command.

**Today.** `LetToLambdaWindow` offers only value bindings (`!IsCalculation`) as parameters. `a`,
bound to `SetVars(...)` or `HSTACK(...)`, is a calculation, so it stays inside the LAMBDA and only
`v` becomes a parameter. Tim rewrites `a` by hand first (the L12 `input1`/`input2` detour).

**Change.** When the LET is a state step (either family):

- The state binding appears as the **first input row**, badged `state`, Keep on, whatever its RHS.
  The input binding is the **second**, badged `input`, Keep on. That is the order REDUCE and SCAN
  call a step in.
- Every other value binding defaults to **Keep off** (hard-coded into the LAMBDA, with refs made
  absolute as today), because REDUCE passes exactly two arguments. Tim can tick one back on.
- Bindings that only fed the sample state or input (e.g. `k` in part 4's stepper) and are
  referenced by nothing else are dropped from the LAMBDA body.
- The dialog shows one more **editable** line, the **fold**:
  `=REDUCE(SetVars("p", $H$268, "b", $I$268), ▮, moveRobot)`, with the caret in the inputs slot.
  The seed is prefilled from the state binding's RHS when that is a `SetVars(...)`/`HSTACK(...)`
  call, with cell references made absolute (`AbsolutizeCellRefs`), so the scaffold's prefilled
  `SetVars("p", H268, "b", I268)` gives Tim's own `$H$268`/`$I$268` seed. When the state binding is a
  cell or an `INDEX(SCAN(...))`, the seed slot is empty. Below it, a **Write fold to** cell box
  (blank by default). If it holds a cell, the fold is written there after registering, as long as
  both slots are filled in; otherwise nothing is written and Ctrl+C copies the fold line.
- The `input` badge can be moved to another row (when the recognition picked the wrong binding).

Result for the step above, named `moveRobot`:

```excel
=LAMBDA(
    a,
    v,
    LET(
        p, GetVar(a, "p"),
        b, GetVar(a, "b"),
        nextP, p + IF(b > 0, SWITCH(v, "F", 1, "B", -1, 0)),
        nextB, MEDIAN(0, 6, IF(v = "C", b + 2, b - 1)),
        SetVars("p", nextP, "b", nextB)
    )
)
```

(Layout as `FormulaFormatter.AppendLambda` emits it today.)

**Builder change.** `LetToLambdaBuilder` accepts an `InputChoice` for a calculation binding when the
request marks it as the state binding; the binding is then a parameter like any value binding.
Today the builder's `bindingsByName` holds only value bindings, so a calculation's choice would throw;
it must include the marked state binding. The sampling-only drop rule is new builder logic too.
Everything else (optional params, renames, absolutising) is unchanged. A LET that isn't a state step
behaves exactly as today.

**Edge cases.** State binding bound to a cell (`a, I266`) — already a value binding; it just gains
the badge and the first slot. State binding named something other than `a` (`s`, `state`) — names
are kept. Both `a` and `v` marked optional — allowed (spec 0002 behaviour), though REDUCE never
omits them.

**Out of scope.** Promoting arbitrary calculation bindings to parameters in non-state LETs (a
general feature; it would need its own spec).

## Part 4 — Round trip: Edit Lambda on a step

**Entry point.** The existing `Edit Lambda` command (ribbon and popup).

**Today.** Edit Lambda works only when the cell is exactly a call, `=moveRobot(J268, L269)`. That
already round-trips a step from a step table (Tim did it by hand at n256). It refuses a fold cell
(`=REDUCE(…, moveRobot)`), which is where Tim is when the answer is wrong.

**Change.** When the active cell's formula contains exactly one `REDUCE` or `SCAN` call whose third
argument is a bare name resolving to a LAMBDA with two parameters (found anywhere in the formula,
so `="P"&TEXTJOIN(" B",,REDUCE(…, moveRobot))` qualifies), Edit Lambda writes a **prototype** of the
step to a neighbouring cell and leaves the fold untouched. For a SetVars step, the prototype is a
stepper:

```excel
=LET(
    k, 1,
    a, IF(k = 1, SetVars("p", 0, "b", 4), INDEX(SCAN(SetVars("p", 0, "b", 4), EXPLODE(G270), moveRobot), k - 1)),
    v, INDEX(EXPLODE(G270), k),
    p, GetVar(a, "p"),
    b, GetVar(a, "b"),
    nextP, p + IF(b > 0, SWITCH(v, "F", 1, "B", -1, 0)),
    nextB, MEDIAN(0, 6, IF(v = "C", b + 2, b - 1)),
    SetVars("p", nextP, "b", nextB)
)
```

Change `k` to step through the run: the cell shows the state after step `k`. This works because a
SetVars state is one text value, so `SCAN` can carry it without the nested-array error that stopped
Tim at L12. For an array-family step, `SCAN` can't, so the prototype binds `a` to the seed and `v` to
`INDEX(inputs, 1)` (no `k`).

- The LAMBDA body comes from `BuildExpandedLet` (it already folds a LET body into the outer LET).
- **Target cell:** the first empty cell to the right of the fold on its row, within 10 columns;
  else the first empty cell below, within 10 rows; else refuse with a message. The fold stays,
  so Tim can compare.
- Re-registering: LET to LAMBDA with the same name (part 3) turns the prototype back into
  `LAMBDA(a, v, …)`, dropping `k`, and overwrites the definition (the existing update path).
- **The stepper's `a` comes from the registered step.** After Tim edits a rule in the prototype,
  `k = 1` shows the new rule, but for `k ≥ 2` the incoming state was still computed by the old
  `moveRobot`. The prototype is for finding the bad step; to check a fix across the run,
  re-register (LET to LAMBDA) and look at the trace (part 5). The Edit Lambda description in the
  popup's command list says so.

**Edge cases.** A library fold (`FOLDROWS`, `REDUCEUNTIL`, prefixed or not) — out of scope v1; only
native `REDUCE`/`SCAN`. More than one REDUCE/SCAN with a named step in the cell → refuse with
"Edit Lambda found more than one REDUCE/SCAN with a named step in this cell; put the one to edit in
its own cell." An inline `LAMBDA(a, v, …)` as the third argument → out of scope v1 (writing it
back would mean editing the fold).

## Part 5 — Trace: in; it already exists as `SplitState`

**Decision: in, as the existing Loops LAMBDA `SplitState`, made key-aligned. No add-in code.**
Because a SetVars state is a single text value, `SCAN(seed, inputs, step)` already returns one state
per step in a column, and `lambdas/loops/SplitState.lambda` already turns a column of states into a
table with an optional header row ("Pair with SCAN to inspect REDUCE state at every iteration"):

```excel
=SplitState(VSTACK(SetVars("p", 0, "b", 4), SCAN(SetVars("p", 0, "b", 4), EXPLODE(G270), moveRobot)), TRUE)
```

| p | b |
|---|---|
| 0 | 4 |
| 1 | 3 |
| … | … |

This works **today** for every step written in this spec's shape, because the output `SetVars`
rebuilds the state in the same key order each step (and the scaffold writes the seed in that order
too; a hand-typed seed in a different order misaligns its row until the key-aligned change below). Tim didn't reach for it at L12 because his
state was an `HSTACK`, which SCAN can't carry. So the first move is recall, not code (training
note, excel-esports#107).

**The one change: read by key, not position.** `SplitState` takes column `c` of each row
positionally (`INDEX(parts, 1, c)`). A step whose output is a `SetVar` chain moves a re-set key to
the end, so columns misalign from the second row. `SplitState` should take the keys from the first
state and read each row's value by key (missing key → `#N/A`), with the same array preview
(`[a, b, c]`) and type coercion as today. Same name and signature; no new LAMBDA.

**Out of scope.** A trace view in the add-in; traces for array (`HSTACK`) states, which is #384's
problem (FOLDROWS or a trace for array states); this spec doesn't absorb it.

## Part 6 — GetVar's hot-loop cost (library)

Measured in the appendix: at 10,000 steps, `SetVars`/`GetVar` costs **34×** (2 values) to **52×**
(5 values) a step against `HSTACK` + `INDEX`; in absolute terms, about 0.4 s and 1 s per 10,000
steps. That's fine for one fold of a few thousand steps and costly for a fold per row over long
inputs. What costs:

1. **The inline Help block: ~2.3×.** Every library LAMBDA bound `Help, TEXTSPLIT(...)` in its LET
   (until #387), and Excel evaluates a binding on every call. Moving the TEXTSPLIT into the `IF(Help?, …)` branch brings
   the cost down to the Help-deleted level, within noise (measured). Library-wide: filed as #387.
2. **GetVar's parse and SetVars' rebuild together: ~1.7×** (Help-free current code 187 ms → Help-free
   fast form 110 ms, 2 values). GetVar does `TEXTSPLIT` of the whole state then `FILTER` with `LEFT`
   over every entry on each read; `TEXTAFTER`/`TEXTBEFORE` on the delimiters finds one entry without
   building arrays. SetVars chains up to ten `SetVar` calls, each splitting, filtering and
   re-joining the growing state; a fresh state needs one `TEXTJOIN`. The benchmark doesn't split
   this 1.7× between the two (its single-fix rows also dropped Help).

**Decision: include the faster form, same API and same state format.** `GetVar` finds its entry with
`TEXTAFTER(us & s & key, key, 1, 1)` then `TEXTBEFORE(… & us, us)`, where `key` is `us & name & rs`
(case-insensitive like today's `LEFT(…) = name & rs`; first match like today's `@FILTER`). The key on
the end of the text makes the search always succeed: TEXTAFTER gives `#VALUE!` when the text is
shorter than the delimiter, even with `if_not_found`, so a missing name reads as an empty entry and
returns `#N/A`. The single-state path is inline; a column of states goes through `MAP` and a
recursive call, because building a helper LAMBDA on every call cost ~20% of a step.

`SetVars` encodes all pairs in one `TEXTJOIN`. The **unique-key invariant stays**: a triangle of
comparisons over the 10 slots spots a repeated name, and a repeat takes the old `SetVar` chain, so the
text is what it always was and the positional readers (`SplitState` today, `GetVarAt`) see no change.
The chain also catches anything TEXTJOIN can't do (`IFERROR`): a state over 32,767 characters, an
error value. The broadcast path (a column of states) and the array-value path are kept.

**Final form: 3.5× (2 values) and 3.7× (5 values) cheaper than before #387, at 9× and 15× `HSTACK`.**
The prototypes reached 4–4.6×; they had no repeat check (4–8%), no `Help?` or column check, and made no
promise on a miss. The remaining gap to `HSTACK` is probably the per-call cost of invoking named
LAMBDAs (inferred, not measured). No change to how Tim writes a step.

**Guidance this gives** (for excel-esports `docs/wisdom/tools.md`): SetVars is the default state
form for authoring and debugging. Switch to `HSTACK` + `INDEX` (or a packed number) when the run is
over ~100,000 steps per recalc or the fold runs per row over long inputs.

## Order by value per effort, and the first slice

| # | Part | Effort | Value under the clock |
|---|---|---|---|
| 0 | **Part 5** — use `SplitState` with SCAN (recall, no code) | none | High: the hand-built step table was ~5 min at L12, and tracing is how every logic slip was found |
| 1 | **Part 5** — `SplitState` reads by key | S | Low–medium: only SetVar-chain steps misalign today |
| 2 | **Part 6** — #387 lazy Help + fast GetVar/SetVars (library) | S | Medium: matters on long runs; free speed everywhere |
| 3 | **Part 3** — `StateStepParser` + state-aware LET to LAMBDA + fold line | M | High: removes the `input1`/`input2` detour and the seed retype |
| 4 | **Part 1** — `/State` scaffold | M | High: removes the rote typing and the GetVar/SetVars typos |
| 5 | **Part 4** — Edit Lambda from a fold cell, with the `k` stepper | M | Medium: finds the bad step; SplitState covers much of it |
| 6 | **Part 2** — edit state | M–L | Medium: adding a rule's variable mid-level (the L12 `C` rule) |

Part 3 is M, not S: it carries the whole `StateStepParser` (both families, prefixes, the input
rule), the builder change, the dialog badges, the drop rule and the fold line.

**First slice: Part 3 as one add-in PR (parser + LET to LAMBDA), then Part 1 as a second PR.**
Alongside, with no add-in dependency: the training note puts `SplitState(VSTACK(seed, SCAN(…)), TRUE)`
in Tim's rep set now, and #387 + the fast GetVar/SetVars go as library PRs. After the two add-in
PRs, one loop under the clock is covered: `/State` writes the step, Tim fills in the rules, LET to
LAMBDA gives `LAMBDA(a, v, …)` and the fold, and SplitState shows every step when the answer is
wrong. Parts 4 and 2 follow once Tim has used that in a rep set.

## Acceptance criteria

- [x] `StateStepParser` recognises SetVars-family (incl. SetVar-chain output) and array-family state
      steps per the rules above, with bare or `<prefix>.` names only, and returns the state binding,
      input binding, state variables (key, binding name, GetVar line, next line) and output; unit
      tests include non-canonical lines, a cell-bound state, `v` after the GetVar lines, an extra
      constant binding (`maxB, $K$2`), comments-as-bindings, a nested LET in a next line, and non-steps.
- [x] `/State` (scaffold): popup command; dialog with State / Input / Write-to; selection prefill
      from a 2×k label/value block; exact output text as above; `_`-suffixed binding names for
      `c`/`r`/A1/R1C1-like names; `nextX2` on clash; SetVar chaining past 10; prefix detection and
      library auto-load; refuses a non-empty target.
- [x] `/State` (edit): add / remove / rename per part 2, including `SetVar` layering on a non-SetVars
      sample state, chaining past 10, the dangling-reference block on remove, and rename tests near
      references (`$B$2`, `Sheet1!B2`, `t[b]`) and case collisions.
- [x] LET to LAMBDA: state binding first (badge `state`), input second (badge movable), other value
      bindings Keep off; builder accepts the state binding though it is a calculation; sampling-only
      bindings dropped; editable fold line with an absolutised seed; Write fold to writes only when
      complete. Non-state LETs unchanged (existing tests pass untouched).
- [x] Edit Lambda on a fold cell: one REDUCE/SCAN with a named two-parameter LAMBDA anywhere in the
      formula; SetVars prototype with `k` stepper, array prototype on the seed; target cell rule;
      fold untouched; the new several-folds message; LET to LAMBDA round-trips the prototype back to
      the same LAMBDA text.
- [ ] `SplitState` reads by key from the first state's keys; harness tests add a SetVar-reordered
      column and a missing key; existing tests unchanged.
- [ ] Faster `GetVar`/`SetVars` pass the existing harness tests unchanged, keep unique keys (test:
      `SetVars("x", 1, "x", 2)` gives the same text as today); the appendix benchmark re-run shows
      the new numbers.

## Out of scope

- An `HSTACK` scaffold or edit-state for array states; editing a SetVar-chain output.
- Library folds (`FOLDROWS`, `REDUCEUNTIL`, `SCANUNTIL`) in the round trip.
- Updating fold seeds or the registered LAMBDA from edit-state.
- A trace for array states (#384); better Unnest names (#385); argument hints (#382).
- Choosing state names from the level text.

## Defaults Tim can reverse

- `/State` is a slash command only (no ribbon button).
- Scaffold next lines start as the identity (`nextP, p`), not blank or `NA()`.
- Next-line names are `nextP` (camel case of `next` + name).
- Typed initial values go into the prototype as the seed (`SetVars("p", 0, …)`); a selection prefill
  points the prototype at the cells (`SetVars("p", H268, …)`).
- Labels from a selection are sanitised and the sanitised name is also the state key.
- A blank sample input is `""`.
- Invalid binding names get a `_` suffix (`c_`); the key stays `"c"`.
- In LET to LAMBDA on a state step, other value bindings default to Keep off.
- The fold line is editable in the dialog and only written when a target cell is given.
- Edit Lambda from a fold writes the prototype to the right of the fold (then below), never over it.
- The `k` stepper is in the SetVars prototype by default.
- The trace is the existing `SplitState` made key-aligned, not a new LAMBDA or add-in UI.
- The faster GetVar/SetVars replace the current ones (same API and state format) rather than living
  alongside as new names.
- First slice is the add-in work in two PRs (Part 3, then Part 1).

Taken while building Part 3 (Labs switch `state-let-to-lambda`, "State steps in LET to LAMBDA"):

- A `SetVar` chain on a `SetVars` call counts as an output, as well as a chain on the state, because
  Part 1 writes that shape past ten variables.
- Array reads also accept the two-index forms of a row or column state: `INDEX(S, 1, k)`,
  `INDEX(S, , k)`, `INDEX(S, k, 1)`. The position must be a number literal within the output's size,
  and the output needs at least two values, so an ordinary `INDEX` of a range plus `HSTACK` rarely
  reads as a step. An array step also needs an input its rules read: without one it's a lookup, and
  LET to LAMBDA shows the ordinary dialog.
- State variables also list keys read inline (no canonical line) and keys only the output sets, after
  the read ones.
- The input rule counts bindings read through a helper line or directly in the output's own
  expressions as "read by a next line", and tries a relative cell before a `$`-anchored one, so
  `maxB, $K$2` loses to `inp, H270`. A binding named `v` that reads the state isn't the input. A
  binding the rules read beats one only passed straight to the output (`SetVars("last", inp)`), which
  is the input only if it's a single cell or `INDEX` and nothing else qualifies. Names bound inside an
  inner `LET` or `LAMBDA` don't count as reads here.
- An input bound to a calculation (`v, INDEX(G1:G9, 1)`, the part 4 stepper) is a parameter too, so
  the builder accepts any calculation the dialog lists on a state step. One left Keep off is made
  absolute, like any excluded input.
- The seed writes other LET bindings in place (`input1`, `k`), since a cell can't see them, and is
  flattened to one line. It's rewritten on the formula tree. Its cell references go through
  `LetToLambdaBuilder.AbsolutizeReference`, the same code as `AbsolutizeCellRefs`.
- The fold line also takes `SCAN` and a wrapped fold (`=TEXTJOIN(" ",,REDUCE(…))`). Renaming the
  LAMBDA rewrites only a step argument that is empty or the previous name, so Tim's edits survive.
- **Write fold to** takes a cell on the active sheet and never overwrites a filled cell: the LAMBDA
  is registered and a message says the fold wasn't written.
- Tab order in the dialog is name, fold, target, then the input rows, and Alt+F jumps to the fold.
  Ctrl+C copies the fold unless text is selected in a box.
- The `input` badge moves with a **make input** button on the other rows. The row that loses it goes
  Keep off.

Taken while building Part 1 (Labs switch `state-scaffold`, "/State: new state step"):

- `/State` is hidden from the popup's command list while the switch is off.
- A variable named `a` or `v` takes the `_` suffix like any rejected name, so the step's own bindings
  keep their names (`a_, GetVar(a, "a")`).
- Two keys that land on one binding stay apart with another `_` (`c` gives `c_`, `c_` gives `c__`).
- A key with a dot keeps its key, but its binding goes through `LetNameSanitizer`, because Excel refuses
  a dot in a LET name (`a.b` gives `aB`). A key that can't start a name (`1st`) does the same.
- A name with no value is an error in the dialog (`p needs a value`), not a blank.
- A selection that isn't a 2×k block with text in every label counts as no prefill, as with a single
  cell: State is empty and Write to is the active cell.
- After a prefill the caret starts in Input, the only field left to fill. After Save the target is
  selected, ready for F2.
- The library counts as loaded only when `SetVars`, `SetVar` and `GetVar` are all there under one
  prefix. With several prefixes, the first by ordinal order wins.
- The dialog can't ask Excel about a typed target, because Excel's thread is waiting on it. Save checks
  the cell, and if it's filled the dialog re-opens with what was typed and Save disabled for that cell.
- The first empty cell below a block is searched for within 1,000 rows.
- `RC`, `RnC` and `RCn` are suffixed by the scaffold alone, not in `ExcelNameValidator` as this spec
  first said: Excel takes `rc` and `R1C` as LET names, so changing the validator would have refused them
  in Refactor, Unnest and Gather for everyone. `RC1` itself is refused by LET in this build.
- A variable named like a library call (`SetVars`, `SetVar`, `GetVar`) is suffixed too, so it can't hide
  the call.
- An Input may start with `=`. A value with a literal, a space and more (`p 0 b 4`) is an error, not an
  intersection: it's a missing comma.
- While the library loads Excel is free, so Save checks the workbook and the target again before
  changing anything, and refuses with a message if either moved on.

Taken while building Part 4 (Labs switch `state-edit-lambda`, "Edit Lambda on a fold"):

- With the switch on, the fold check runs first, so `=GetVar(REDUCE(…, moveRobot), "p")` steps the
  fold rather than expanding GetVar. A cell with no fold gets today's Edit Lambda.
- Only a step that's a workbook name counts. A step bound in a LET around the fold, an inline
  LAMBDA, a sheet-qualified name or a LAMBDA without exactly two parameters is no fold, and the
  ordinary command runs.
- The fold's seed and inputs are written in the prototype as typed (`G270`, not `$G$270`): it sits
  on the same sheet. LET bindings around the fold are written in place. A name bound twice by
  nested LETs, a parameter of a LAMBDA around the fold, or an empty seed or inputs slot is refused
  with a message.
- A step name the fold's expressions also use (a cell like `h1`, a whole column, a defined name)
  gets a `_` in the prototype, because Excel would let it capture the reference. In build 20527
  Excel refuses such a name inside a defined LAMBDA anyway, so this only guards other builds.
  `k` takes a `_` when the step or the fold already uses `k`.
- A step the parser doesn't recognise (a body that isn't a plain `SetVars` call, such as Airplane's
  `SINGLE(IF(…))` walk) gets the stepper unless its seed is an `HSTACK`, `VSTACK` or array constant.
- The target is empty only when it has no formula and isn't in a spill. After writing, the
  prototype is selected.
- A prototype over 8,192 characters is refused.

Taken while building Part 2 (Labs switch `state-edit`, "/State: edit a state step"):

- `state-edit` shows `/State` by itself. With it on and `state-scaffold` off, a cell that isn't a step
  still gets the scaffold, since the spec makes that the other half of the command.
- An array step or a `SetVar` chain on the state is refused with a message, not sent to the scaffold.
  A `SetVar` chain on a `SetVars` call can be edited, because Part 1 writes it past ten variables.
- The caret starts in the blank last row: adding is the usual edit. A blank name on an existing row
  removes it, and the ✕ button does the same. Each row remembers its key, so a new name is a rename.
- Sample values are shown, not edited, for existing variables; change them in the cell.
- One Save applies removals, then renames, then additions. A rename onto a name still in use is
  blocked, so swapping two names takes two edits.
- Saving rewrites the cell in the one-binding-per-line layout, with each binding's text kept.
- A rename always renames the GetVar line, but the next line only when it follows the convention
  (`nextB`, `nextB2`). `np` keeps its name.
- A rename when `S` holds the state elsewhere carries the value across, so the sample keeps working:
  `a, SetVar(I266, "bat", GetVar(I266, "b"))`. The spec only covered `S`'s `SetVars`.
- Remove also blocks on an inline `GetVar(a, "b")` and on a second line reading the same key, which
  would otherwise give `#N/A` when run. A name an inner `LET` binds counts as a read, so it can block
  needlessly; it never lets a dangling name through.
- A next line another key still passes, or the input, stays when its variable goes.
- A new binding avoids every name already in the step, cells and calls included (`maxB` gives
  `maxB_`). A rename onto one is blocked, because Tim typed that name on purpose.
- A cell where `LET` is only part of the formula (`=LET(…)&"x"`) isn't a step here: rewriting from the
  parts would drop the rest.

Taken while fixing #403 (`ApplyRenames` and `AbsolutizeCellRefs` on tokens). Excel 16.0 build 20527
in `taglo-win` settled these:

- `b:b` and `b:c` follow a LET name: `LET(b, B2, c, B3, COLUMNS(b:c))` is 1, not 2. They're renamed.
- A built-in wins over a LET name: `LET(max, 5, MAX(max, 1))` is 5, and `LET(sum, LAMBDA(x, x+1), sum(1))`
  is entered as `SUM(1)`, giving 1. Excel writes a binding's call in the binding's case (`FOO(1)` comes
  back `foo(1)`), so a call is renamed only when it matches the binding's case exactly. A binding in
  capitals spelt like a built-in (`LET(MAX, …)`) still renames the call, which shows as `#NAME?`.
- `A0`, `XFE1` and `A1048577` are names, not cells, so they aren't made absolute.
- `Data!A:B` stays: `LET(b, 2, COLUMNS(Sheet1!A:b))` is 2, so a qualified range's end isn't a LET name.
- This build lets a LET name shadow a cell (`LET(x1, 5, x1)` is 5, as are `c` and `H1`). Excel then
  writes every other use of that cell in the formula as the name `'H1'`, which gives `#NAME?`. Names
  bound in the LET are left alone when refs are made absolute. Part 1 still suffixes these, as
  specified.
- Excel refuses `LOG10 (100)`, so a `(` after a space is an intersection, not a call.
- Text the lexer can't read (an unterminated string) comes back unchanged. Excel refuses it anyway.

## Appendix — GetVar hot-loop benchmark (6 Nov 2026)

**Final numbers** (after #387 and Part 6). Same method as below, re-run with
`scripts/bench/state-engine/`: all versions as labelled names in one workbook, 10,000 steps, the
median of 3 rounds of 7 recalcs.

| Library | 2 values, ms | × today | × (b) | 5 values, ms | × today | × (b) |
|---|---|---|---|---|---|---|
| today (`6e01e21`) | 453 | 1 | 31× | 1,120 | 1 | 56× |
| lazy Help (#387) | 198 | 2.3× cheaper | 13× | 528 | 2.1× cheaper | 26× |
| lazy Help + fast GetVar/SetVars | 131 | 3.5× cheaper | 8.9× | 302 | 3.7× cheaper | 15× |
| (b) HSTACK + INDEX | 14.7 | | 1× | 20.1 | | 1× |

Where the prototypes' lead went (2 / 5 values, ms, two rounds): final `GetVar` with a helper LAMBDA
171 / 377, inlined 139 / 305; prototype pair 108 / 244. Dropping the repeat check would save 4–8%
(134 / 281), and comparing names without `& ""` ~4%, but that would miss `1` against `"1"`. A
`UNIQUE(VSTACK(…))` check was slower (171 / 329).

A one-off probe of old against new on 377 calls in one workbook (SetVars, GetVar, odd states and
names; not kept in the repo) gave the same value, type and shape except: a value holding an empty
`CHAR(31)` field, at its end or doubled (SetVars no longer strips it; the old chain did so by accident); an array `name` (GetVar takes the first, as SetVar does; a 1×1 array name now
gives a true scalar, and a 2×1 name no longer errors); an error `name` on an empty state (the name's
error, not `#N/A`). States over 32,767 characters are cut when read, before and after; at exactly 32,767 the new read
loses the last character too (the leading separator pushes it over), and at 32,766 or fewer it
doesn't (checked in real Excel).

The sections below are the original measurement that led to Part 6.

**Question.** What does a `SetVars`/`GetVar` state cost per REDUCE step, against `HSTACK` + `INDEX`
and packed states, and what in its implementation costs?

**Method.** Real Excel: Microsoft 365, Excel 16.0 build 20527, Windows 11 on ARM64 (the `taglo-win`
Parallels VM on the Mac mini, 4 vCPU), driven over COM from PowerShell, hidden, in a fresh workbook.
The four Loops LAMBDAs were defined as workbook names from `lambdas/loops/*.lambda` at `main`
`a5429dd` (after #377), comments stripped. Calculation manual; each case is one cell; the time is
`Range.Calculate` on that cell with a stopwatch, 7 runs, median reported (first run dropped by the
median; the first run of the slow cases is 1.5–3× the rest). Every variant returns the same answer
for the same case (checked: `1` for 2 values, `3603`/`35998` for 5 values).

Same step logic in every variant; only the state carrier differs. Inputs `SEQUENCE(N)`:

- 2 values: `nextP = p + IF(b>0, MOD(v,3)-1, 0)`, `nextB = MEDIAN(0, 6, IF(MOD(v,5)=0, b+2, b-1))`,
  start `p=0, b=4`.
- 5 values: those plus `nextC = c + MOD(v,7)`, `nextD = MAX(d,p)`, `nextE = e + (b=0)`, start 0.

Variants:

- **(a)** `REDUCE(SetVars("p",0,"b",4), SEQUENCE(N), LAMBDA(a, v, LET(p, GetVar(a,"p"), b, GetVar(a,"b"), nextP, …, nextB, …, SetVars("p", nextP, "b", nextB))))`, read with `GetVar`.
- **(b)** `REDUCE(HSTACK(0,4), …, LAMBDA(a, v, LET(p, INDEX(a,1), b, INDEX(a,2), …, HSTACK(nextP, nextB))))`.
- **(c) text** `REDUCE("0,4", …, LAMBDA(a, v, LET(s, --TEXTSPLIT(a, ","), p, INDEX(s,1), …, TEXTJOIN(",", FALSE, nextP, nextB))))`.
- **(c) packed number** (2 values; `b` stays in 0..6) `REDUCE(4, …, LAMBDA(a, v, LET(b, MOD(a,10), p, (a-b)/10, …, nextP*10 + nextB)))`.
- **lean**: (a) with the Help binding deleted from all four LAMBDAs.
- **lazy Help**: (a) with the Help TEXTSPLIT moved into `IF(Help?, TEXTSPLIT(…), result)`.
- **fast**: (a) with prototypes `GetVarF` (TEXTAFTER/TEXTBEFORE read of the first match, single
  state only, no broadcast path) and `SetVarsF` (one TEXTJOIN of encoded pairs, no dedupe), no Help.

**Results** (ms per recalc, median of 7; µs per step = ms × 1000 / N):

| Variant | 2 val, 1k | 2 val, 10k | µs/step | × (b) | 5 val, 1k | 5 val, 10k | µs/step | × (b) |
|---|---|---|---|---|---|---|---|---|
| (b) HSTACK + INDEX | 2.1 | 12.9 | 1.3 | 1× | 2.4 | 19.8 | 2.0 | 1× |
| **(a) SetVars / GetVar** | 47 | 435 | **43.5** | **34×** | 99 | 1,030 | **103** | **52×** |
| lean (no Help) | 19.6 | 187 | 18.7 | 14× | 54 | 505 | 50 | 25× |
| lazy Help | 22.0 | 187 | 18.7 | 14× | 59 | 513 | 51 | 26× |
| fast GetVar + fast SetVars | 12.0 | 110 | 11.0 | 8.5× | 25 | 225 | 22.5 | 11× |
| fast GetVar only | 29.9 | 295 | 29.5 | 23× | 66 | 632 | 63 | 32× |
| fast SetVars only | 23.1 | 245 | 24.5 | 19× | 56 | 604 | 60 | 30× |
| (c) text packed | 2.5 | 12.3 | 1.2 | 0.95× | 3.3 | 27 | 2.7 | 1.4× |
| (c) packed number | 1.5 | 5.3 | 0.5 | 0.41× | – | – | – | – |

The lazy-Help row is from a second run (same session pattern; (a) re-measured there at 414 ms and
999 ms, within 5% of the first run). The slow variants scale linearly in N; the fast ones carry a
fixed per-recalc overhead (`HSTACK` 2.1 ms at 1k but 12.9 ms at 10k, not 21), which inflates the
1k ratios, so the µs/step and ratio columns use the 10k runs. The "fast GetVar only" and "fast
SetVars only" rows are confounded: the prototypes have no Help block, so each row also removes that
LAMBDA's Help cost. They are reported, not used for the split below.

**Reading.** (1) The Help block costs ~2.3× (current 435 → Help-free 187 ms, 2 values; 1,030 → 505,
5 values). Moving it into the IF branch gets to within noise of deleting it (#387). (2) The parse
and rebuild fixes together are worth ~1.7× (Help-free 187 → fast 110 ms; 505 → 225 ms); this run
doesn't split that between GetVar and SetVars. Both fixes together leave 8.5–11× `HSTACK`, most
likely the per-call cost of named LAMBDAs (inferred; not measured separately). (3) Plain text
packing is as fast as `HSTACK`, so the state format isn't the problem; the per-call machinery is. Confidence: seen, one machine (ARM64 VM; absolute times on Tim's
x64 PC will differ, ratios should hold).

**Harness.** `scripts/bench/state-engine/` holds the generator (`gen.py`: builds the names and the
case formulas from `lambdas/loops/`) and the runner (`bench.ps1`: COM Excel, timings to a TSV). See
its README to re-run after #387 or the fast forms land.
