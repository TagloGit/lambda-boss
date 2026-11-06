# 0011 — State-step authoring (SetVars / GetVar)

Issue: #386. Related, not absorbed: #384 (engine trace for array states), #385 (Unnest step names),
#382 (argument hints), #381 (hide Help in Edit Lambda), #387 (Help block cost, filed off this spec).

## Problem

State engines (a `REDUCE` over inputs, carrying several values from step to step) are Tim's
costliest slip kind under the clock: 10 logic slips, ~1,400 s elapsed across three recordings
(excel-esports `analysis/tim-slips.md`). He re-solved rep R1 L12 (a robot with position and
battery) with the Loops library's `SetVars`/`GetVar` and found it easy to reason about and easy to
debug. What slows him is the setup, which is rote and deterministic:

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
L12, log lines 205–264) shows where the time goes in the version he wrote with `HSTACK`:

- **Pointing the prototype state** at the wrong row (`HSTACK(H268, I269)`, n206) — a reference slip.
- **Turning the step into a LAMBDA** (n224–229, ~30 s): LET to LAMBDA only offers value bindings as
  parameters, and `a` was bound to a formula, so he rewrote it through `input1`/`input2`, then bound
  `a` and `v` to constants by hand, then named it in Name Manager.
- **Debugging** (n240–264, ~5 min): SCAN over the `HSTACK` state raised Excel's nested-array message,
  so he built a step table by hand, then inlined the LAMBDA body into it ("a LAMBDA can't be
  stepped"), fixed the else-branch, and updated the definition in Name Manager twice.

Every step above is mechanical. This spec makes `SetVars`/`GetVar` the fastest way to write a state
engine under the clock: the add-in writes the scaffold, edits the state lines together, converts
the step to `LAMBDA(a, v, …)` and back, and the library gives a one-call trace.

## Terms

- **State step** — a `=LET(...)` with a *state binding* read by `GetVar(state, "name")` lines and a
  body that is a `SetVars(...)` call. The shape above.
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
- `LetToLambdaBuilder.ApplyRenames` for identifier renames (word-boundary, string-skipping).
- `FormulaFormatter.AppendLet` for output layout (one binding per line, 4-space indent).
- `ExcelNameValidator` + `LetNameSanitizer` for binding names.
- `EditLambdaCommand.BuildExpandedLet` for the LAMBDA → LET direction.

**Recognition rules.** A `LET` is a state step when:

1. its body is a call to `SetVars` (any library prefix: `SetVars`, `loops.SetVars`, …) whose name
   arguments are all string literals; and
2. some binding `S` is the first argument of at least one `GetVar(S, "<literal>")` call in another
   binding's RHS. `S` is the state binding. If several bindings qualify, the one read by the most
   `GetVar` calls wins; ties go to the earliest.

The **input binding** is the binding, other than `S`, that sits before the first `GetVar` line; when
there are several, the one nearest the `GetVar` lines (the scaffold puts `v` there). When there are
none, the step has no input binding (rare; LET to LAMBDA then offers only `S`).

A *canonical* GetVar line is a binding whose whole RHS is `GetVar(S, "k")`. A canonical next line is
a binding passed as a value in the output `SetVars`. Non-canonical lines (`p, GetVar(a, "p") + 0`,
a `GetVar` inline in an expression) are left untouched by every edit except a key rename, which
rewrites the key string in every `GetVar(S, …)` / `SetVars` / `SetVar` call on the state.

**Array family (parts 3 and 4 only).** The same rules with `HSTACK`/`VSTACK` in place of `SetVars`
and `INDEX(S, k)` / `NTH(S, k)` / `CHOOSECOLS(S, k)` in place of `GetVar(S, "k")`. Tim still writes
array states (the L12 slip above was one), and parts 3 and 4 cost nothing extra for them. Parts 1,
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
(through `LetNameSanitizer`), values as references to the cells below. Tim's L12 layout (`p`, `b`
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

- The prototype state **is the seed**, so the step evaluates from the first keystroke, on step 1's
  state. Every next line starts as the identity (`nextP, p`): the cell shows a valid state at once,
  and Tim replaces each RHS with the rule. No `#NAME?` or `#VALUE!` while the step is half-written.
- Next-line names are `next` + the variable with its first letter upper-cased (`p` → `nextP`,
  `best` → `nextBest`), matching Tim's own convention.
- Enter saves, Escape cancels (same keys as the other dialogs). One Excel operation (`Formula2`),
  native undo.

**Edge cases.**

- *Binding names Excel rejects.* `c`, `r`, `C`, `R` and anything that reads as a cell reference
  (`b4`, `x1`, `ab12`) are invalid LET names. The binding is suffixed with `_` (`c_`, `b4_`); the
  state key stays as typed (`GetVar(a, "c")`). The preview shows it.
- *Clashes.* A name typed twice → error in the dialog. A `nextX` that equals another state name →
  `nextX2`. Names are compared case-insensitively (GetVar's own match is case-insensitive).
- *More than 10 variables.* `SetVars` takes 10 pairs. The 11th onward chain through `SetVar`:
  `SetVar(SetVars(…10 pairs…), "k", nextK)`.
- *Library not loaded.* The command looks for workbook names ending in `SetVars`, `SetVar`, `GetVar`
  whose `RefersTo` starts with `=LAMBDA(` and emits the names as found (bare names preferred, else
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
`a, SetVar(I266, "c", 0)`.

**Remove** `b`: deletes `b`'s GetVar line, its next line, its pair in the output and in `S`. If
another line still reads `b` or `nextB`, the dialog lists those lines and keeps Save disabled; the
tool never leaves a dangling name.

**Rename** `b` → `bat`: renames the bindings (`b` → `bat`, `nextB` → `nextBat`) through
`ApplyRenames`, so every expression that reads them follows, and rewrites the key string in every
`GetVar(S, …)`, the output `SetVars` and `S`'s `SetVars`. Strings elsewhere (`SWITCH(v, "b", …)`)
are not touched.

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
- The dialog shows one more read-only line, the **fold**, ready to copy:
  `=REDUCE(SetVars("p", 0, "b", 4), , moveRobot)`. The seed is the state binding's RHS when that is
  a `SetVars(...)`/`HSTACK(...)` of constants (the scaffold's form); otherwise the slot is empty.
  The inputs slot is always empty. A **Write fold to** cell box (blank by default) writes it there
  after registering. A fold with an empty slot is no use, so it is written only when both slots
  are filled in.

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

**Edge cases.** The step called through a prefix (`loops.REDUCEUNTIL`) or a library fold (`FOLDROWS`,
`REDUCEUNTIL`) — out of scope v1; only native `REDUCE`/`SCAN`. More than one fold in the cell →
the existing error. An inline `LAMBDA(a, v, …)` as the third argument → out of scope v1 (writing it
back would mean editing the fold).

## Part 5 — Trace: in, as a library LAMBDA (`StateTable`)

**Decision: in, as a Loops LAMBDA, no add-in code.** Because a SetVars state is a single text value,
`SCAN(seed, inputs, step)` already returns one state per step in a column. What's missing is a way
to read that column as a table. `StateTable(states)` returns a header row of variable names and one
row per state:

```excel
=StateTable(VSTACK(seed, SCAN(seed, EXPLODE(G270), moveRobot)))
```

| p | b |
|---|---|
| 0 | 4 |
| 1 | 3 |
| … | … |

- Variable names come from the first state, in order. Array-valued variables show as their
  `TEXTJOIN(",")` text. A key missing from a row gives `#N/A` in that cell.
- Reasons for in: the debugging stretch (the step table built by hand, ~5 min at L12) is the costliest
  part of Tim's state-engine slips; this is one LAMBDA (small, testable in the harness) and needs no
  new add-in UI. Array (`HSTACK`) states can't do this, which is #384's problem (FOLDROWS or a
  trace for array states); this spec doesn't absorb it.

**Out of scope.** A trace view in the add-in; traces for array states (#384).

## Part 6 — GetVar's hot-loop cost (library)

Measured in the appendix: at 10,000 steps, `SetVars`/`GetVar` costs **34×** (2 values) to **52×**
(5 values) a step against `HSTACK` + `INDEX`; in absolute terms, about 0.4 s and 1 s per 10,000
steps. That's fine for one fold of a few thousand steps and costly for a fold per row over long
inputs. What costs:

1. **The inline Help block** (about half). Every library LAMBDA binds `Help, TEXTSPLIT(...)` in its
   LET, and Excel evaluates it on every call. Moving the TEXTSPLIT into the `IF(Help?, …)` branch
   removes the cost entirely (measured). Library-wide: filed as #387.
2. **GetVar's parse.** `TEXTSPLIT` of the whole state, then `FILTER` with `LEFT` over every entry, on
   each read. `TEXTAFTER`/`TEXTBEFORE` on the delimiters finds one entry without building arrays.
3. **SetVars' rebuild.** Ten chained `SetVar` calls, each splitting, filtering and re-joining the
   growing state. A fresh state needs one `TEXTJOIN` of the encoded pairs.

**Decision: include the faster form, same API and state format.** A `GetVar` that finds its entry
with `TEXTAFTER(us & s, us & name & rs, -1, 1)` then `TEXTBEFORE(… & us, us)` (case-insensitive like
today's `LEFT(…) = name & rs`), and a `SetVars` that encodes all pairs in one `TEXTJOIN`, both with
the Help block in the IF branch. Today `SetVars("x", 1, "x", 2)` keeps only the last `x`; the fast
`SetVars` keeps both entries, so `GetVar` reads the **last** match (instance `-1`) to give the same
answer. The broadcast path (a column of states, via `MAP`) and the array-value path are unchanged.
Measured (prototypes `GetVarF`/`SetVarsF`, which read the first match and had no broadcast path):
**8.5×** (2 values) and **11×** (5 values) `HSTACK`, 4–4.6× faster than today. The remaining gap is
the per-call cost of a named LAMBDA. The implementation PR re-measures the final form. No change to
how Tim writes a step, so it lands as a library PR with the harness tests (broadcast over a column
of states, 1×1 arrays from #377, types) unchanged.

**Guidance this gives** (for excel-esports `docs/wisdom/tools.md`): SetVars is the default state
form for authoring and debugging. Switch to `HSTACK` + `INDEX` (or a packed number) when the run is
over ~100,000 steps per recalc or the fold runs per row over long inputs.

## Order by value per effort, and the first slice

| # | Part | Effort | Value under the clock |
|---|---|---|---|
| 1 | **Part 3** — LET to LAMBDA state-aware (both families) + fold line | S | High: removes the `input1`/`input2` detour and the seed retype |
| 2 | **Part 1** — `/State` scaffold | M | High: removes the rote typing and the typos in GetVar/SetVars lines |
| 3 | **Part 5** — `StateTable` (library) | S | High for debugging: the hand-built step table was ~5 min at L12 |
| 4 | **Part 6** — #387 lazy Help + fast GetVar/SetVars (library) | S | Medium: matters only on long runs; free speed everywhere |
| 5 | **Part 4** — Edit Lambda from a fold cell, with the `k` stepper | M | Medium: debugging mid-run; StateTable covers much of it |
| 6 | **Part 2** — edit state | M–L | Medium: adding a rule's variable mid-level (the L12 `C` rule) |

**First slice: parts 3 and 1 in one add-in PR, with `StateTable` alongside as a library PR.** That
covers one full loop under the clock: `/State` writes the step, Tim fills in the rules, LET to
LAMBDA gives `LAMBDA(a, v, …)` and the fold, and `StateTable(SCAN(…))` shows every step if the
answer is wrong. #387 and the fast GetVar/SetVars are independent library work and can go first.
Parts 4 and 2 follow once Tim has used the first slice in a rep set.

## Acceptance criteria

- [ ] `StateStepParser` recognises SetVars-family and array-family state steps per the rules above,
      with any library prefix, and returns the state binding, input binding, state variables (key,
      binding name, GetVar line, next line) and output; unit tests include non-canonical lines,
      a cell-bound state, comments-as-bindings, a nested LET in a next line, and non-steps.
- [ ] `/State` (scaffold): popup command; dialog with State / Input / Write-to; selection prefill
      from a 2×k label/value block; exact output text as above; `_`-suffixed binding names for
      `c`/`r`/cell-like names; `nextX2` on clash; SetVar chaining past 10; prefix detection and
      library auto-load; refuses a non-empty target.
- [ ] `/State` (edit): add / remove / rename per part 2, including `SetVar` layering on a non-SetVars
      sample state and the dangling-reference block on remove.
- [ ] LET to LAMBDA: state binding first (badge `state`), input second, other value bindings Keep
      off; builder accepts the state binding though it is a calculation; sampling-only bindings
      dropped; fold line shown; Write fold to writes only when complete. Non-state LETs unchanged
      (existing tests pass untouched).
- [ ] Edit Lambda on a fold cell: one REDUCE/SCAN with a named two-parameter LAMBDA anywhere in the
      formula; SetVars prototype with `k` stepper, array prototype on the seed; target cell rule;
      fold untouched; LET to LAMBDA round-trips the prototype back to the same LAMBDA text.
- [ ] `StateTable` in `lambdas/loops/` with harness tests (2 and 5 variables, array value, missing
      key, a SCAN column).
- [ ] Faster `GetVar`/`SetVars` pass the existing harness tests unchanged; the appendix benchmark
      re-run shows the new numbers.

## Out of scope

- An `HSTACK` scaffold or edit-state for array states.
- Library folds (`FOLDROWS`, `REDUCEUNTIL`, `SCANUNTIL`) in the round trip.
- Updating fold seeds or the registered LAMBDA from edit-state.
- A trace for array states (#384); better Unnest names (#385); argument hints (#382).
- Choosing state names from the level text.

## Defaults Tim can reverse

- `/State` is a slash command only (no ribbon button).
- Scaffold next lines start as the identity (`nextP, p`), not blank or `NA()`.
- Next-line names are `nextP` (camel case of `next` + name).
- The prototype state is the seed (`SetVars` of the initial values), not cells.
- A blank sample input is `""`.
- Invalid binding names get a `_` suffix (`c_`); the key stays `"c"`.
- In LET to LAMBDA on a state step, other value bindings default to Keep off.
- The fold is shown in the dialog and only written when a target cell is given.
- Edit Lambda from a fold writes the prototype to the right of the fold (then below), never over it.
- The `k` stepper is in the SetVars prototype by default.
- The trace is a library LAMBDA (`StateTable`), not add-in UI.
- The faster GetVar/SetVars replace the current ones (same API and state format) rather than living
  alongside as new names.

## Appendix — GetVar hot-loop benchmark (6 Nov 2026)

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
999 ms, within 5% of the first run). Costs scale linearly in N.

**Reading.** (1) Half of SetVars' cost is the Help block, recovered entirely by moving it into the
IF branch (#387). (2) The rest splits roughly evenly between GetVar's TEXTSPLIT/FILTER parse and
SetVars' chained rebuild; both fixes together leave 8.5–11× `HSTACK`, which is the cost of calling
named LAMBDAs per step. (3) Plain text packing is as fast as `HSTACK`, so the state format isn't the
problem; the per-call machinery is. Confidence: seen, one machine (ARM64 VM; absolute times on Tim's
x64 PC will differ, ratios should hold).

**Harness.** `scripts/bench/state-engine/` holds the generator (`gen.py`: builds the names and the
case formulas from `lambdas/loops/`) and the runner (`bench.ps1`: COM Excel, timings to a TSV). See
its README to re-run after #387 or the fast forms land.
