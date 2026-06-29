# Spec 0010 (spike) — Debug Nested

Status: **spike for feedback** (issue #279). Not an approved spec — this documents
what the spike branch builds so Tim can try it and steer.

## The question #279 actually asks

`/Unnest` (spec 0009) answers *"this formula is a deeply nested blob of **static**
sub-expressions — show me each step as a named, inspectable value"*. Every
sub-expression there has **one** value, so naming it in a `LET` makes it
inspectable.

`#279` is a **different** question. When a `LAMBDA(...)` is applied over an array —
`BYROW(arr, LAMBDA(r, …))`, a custom `PAIROP(r, LAMBDA(a, b, …))` — the body runs
**once per element** with its parameters bound dynamically. There is no single
value for `SQRT(SUMSQ(PAIROP(XV(VSTACK(a, b), …))))` — there's one per iteration.
The issue's filed "Option 3" (nested-LET-in-LAMBDA) only makes the body more
**readable**; it surfaces no **values**. So the debugging question needs a
different mechanism, and likely a different command.

## Direction (after spike feedback): extract to a scratch sheet — `/Debug Lambda`

The first spike built an in-popup "pin & watch" window (`/Debug Nested`, below).
On review, the better fit for real debugging is to **materialise the inner lambda
as an editable `=LET(...)` on a fresh scratch worksheet, wired to sample inputs** —
so the lambda is debugged in real cells (formula bar, spill, F9), then converted
back with `/LET to LAMBDA` and pasted into the original formula. The workflow:

> write formula → `/Unnest` to a LET → narrow the bug to an inner LAMBDA →
> **`/Debug Lambda`** (extract it to a scratch sheet with dummy inputs) → play
> until it works → `/LET to LAMBDA` → paste the named lambda back.

### How `/Debug Lambda` works

1. **Pick** the lambda scope + a 1-based example index (modal picker).
2. **Classify** the body's free names into the *minimum* input set
   (`AnalyzeInputs`): the scope's own params, enclosing-lambda params,
   enclosing-`LET` bindings (e.g. `convert` — carried with its real definition),
   and externals (tables / workbook names, left alone).
3. **Capture** each input's value by evaluating it in the lambda's enclosing
   context **on the source sheet** (so sheet-local refs and tables resolve), via a
   scratch cell. A recognised iterator's param is sliced (`CHOOSEROWS(both, k)`);
   a param under a custom HOF is left blank to fill in (probe-capture is the next
   step). Snapshots — not live formulas — so there's no ref-qualification problem
   and the dummies are stable to edit.
4. **Generate** a fresh `LB Debug N` sheet: each input written as a value block
   under a **sheet-scoped** defined name (own params as `name_in`, seeded into the
   LET so `/LET to LAMBDA` lifts exactly them; enclosing context under its own
   name, referenced freely), then the body as a multiline `=LET(...)` decomposed
   into steps (`BuildDebugLet`, reusing `UnnestEngine`). Deleting the sheet removes
   its scoped names — total cleanup.

### Status of this direction

- Pure engine (tested): `AnalyzeInputs`, `BuildDebugLet`, `BuildCaptureFormula`.
- `DebugLambdaCommand` + `DebugScopePickerWindow` (COM / sheet generation) —
  compiles; **not yet exercised in Excel**.
- The `/Debug Nested` watch window below is **retained until `/Debug Lambda` is
  proven**, then retired.

### Known gaps / next steps

- **Probe-capture for custom HOFs** (e.g. `PAIROP`) — return each param
  individually, run the host in context, read the chosen element. Until then those
  params are blank cells to fill in by hand.
- **Deep nesting** capture (pinning enclosing-lambda params through a custom-HOF
  ancestor) is only partial.
- **External sheet-local cell refs** in a body (rare) would resolve to the scratch
  sheet — not yet qualified back to the source sheet.

---

## First spike (superseded): in-popup "pin & watch" (`/Debug Nested`)

## Approach taken: "Pin & Watch" (spike options A + C)

1. **Pin one example (A).** Pick a lambda scope; bind each in-scope parameter
   (the scope's own params **and** every enclosing param) to a concrete example
   value. For a recognised single-source iterator the pin defaults to a slice
   (`CHOOSEROWS(arr, k)` for `BYROW`, `CHOOSECOLS` for `BYCOL`); for a custom
   higher-order function (`PAIROP`) or an accumulator iterator (`SCAN`/`REDUCE`)
   the pin is left blank for the user to fill. Nesting is handled by listing every
   ancestor scope's params, so the doubly-dynamic trip formula (outer `BYROW` →
   inner `PAIROP`) is reachable by drilling to the inner scope.

2. **Watch live values (C).** Once pinned, the lambda body is decomposed into
   steps by **reusing `UnnestEngine`** (which already keeps a deeper nested
   `LAMBDA`/`LET` opaque, so a step containing an inner lambda stays inline and the
   user drills into it separately). For each step the engine emits a self-contained
   `=LET(<pins>, <preceding steps>, <this step>)`, and the add-in evaluates it
   against the **live grid** via a scratch cell beyond the used range, reading the
   value back. Scalars show directly; spilled arrays show a compact `{r×c} …`
   preview; Excel errors (e.g. an unfilled pin) show as `#…` in red.

Nothing is written to the active cell — this is a **read-only debugging view**, not
a refactor. The window is **modeless** (unlike `/Unnest`'s modal dialog) so Excel
stays responsive for the live evaluation, which is marshalled onto the macro thread.

## What's in the branch

- `DebugNestedEngine` + `DebugNestedTypes` — pure, unit-tested (scope discovery,
  default-pin suggestion, evaluable-formula assembly).
- `DebugNestedCommand` + a scratch-cell evaluator (mechanism C).
- `DebugNestedWindow` (modeless) — scope picker, example index, editable pins,
  steps-with-values.
- Registered as `/Debug Nested` in the popup command list.

## Known limits (spike — deliberately scoped)

- **Default pins only for `BYROW`/`BYCOL`** single-source iterators. `MAP` (multi-
  array), `SCAN`/`REDUCE` (accumulator), `MAKEARRAY`, and **all custom HOFs** need a
  hand-typed pin. (Capturing those automatically — probe-evaluating the host to read
  the actual bound value — is the obvious v2.)
- **Scratch-cell evaluation** writes/clears a cell beyond the used range on the
  active sheet; it adds undo entries and assumes that region is free. Fine for
  interactive use; not invisible.
- **Array values** are previewed (first 12 cells), not shown in full.
- Only **`LAMBDA`** scopes are debugged; a nested `LET` is out of scope here
  (that's closer to `/Unnest`'s existing-LET path).
- No example-pinning UI for `MAKEARRAY`'s `(i, j)` indices beyond blank defaults.

## Open questions for Tim

1. Is "pin one example + watch live values" the right shape, or do you also want
   the **whole-array trace table** (option B — one row per iteration, one column
   per step) as a complementary view?
2. Should auto-pin-capture (probe the host to read the real bound value for custom
   HOFs like `PAIROP`) be the priority for v2? It's what makes the inner scope
   "just work" without hand-typing.
3. Is a separate `/Debug Nested` command the right home, or should this fold into
   `/Unnest`?
