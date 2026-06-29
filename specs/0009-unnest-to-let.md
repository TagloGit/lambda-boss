# 0009 — Unnest to LET

## Problem

During a case, authors write deeply nested formulas for brevity:

```excel
=ROUND(SQRT(SUMSQ(XLOOKUP(H94, t[City], t[[X-Coordinates]:[Y-Coordinates]]) - $I$92:$J$92)) * 100, 0)
```

That's fast to type but opaque to debug. When the answer comes out wrong, the author has no way to see the intermediate values — the distance before rounding, the vector of deltas, the result of the `XLOOKUP`. Today the only recourse is to hand-decompose the formula: copy each nested call into its own cell, or hand-write a `=LET(...)` that names each step. Both are tedious and error-prone for exactly the formulas that most need debugging (the big nested ones).

`/Refactor` (spec [0008](0008-refactor-to-let.md)) hoists a formula's **leaves** — cell refs, ranges, literals, named ranges — up into LET *value bindings* (inputs). But it deliberately leaves the formula structure intact and puts **sub-expression extraction out of scope** ("Common-subexpression elimination is a separate, harder problem"). So `/Refactor` gives you named *inputs* but not named *steps*. The intermediate calculations — the part you actually want to inspect when debugging — stay buried inside one expression.

This spec is the complementary half: hoisting each **intermediate function call and operator expression** out into its own LET *calculation binding* (a named step), so every level of the nesting becomes inspectable.

## Proposed Solution

A new slash command, `/Unnest` (full name **Unnest to LET**), that takes the active cell's nested formula and rewrites it as a `=LET(...)` where **each function call and each operator expression becomes a named step**, leaf-first, with each parent rewritten to reference its children by name.

### Worked example

Active cell:

```excel
=ROUND(SQRT(SUMSQ(XLOOKUP(H94, t[City], t[[X-Coordinates]:[Y-Coordinates]]) - $I$92:$J$92)) * 100, 0)
```

After `/Unnest` (auto-named steps, all kept):

```excel
=LET(
    xlookup1, XLOOKUP(H94, t[City], t[[X-Coordinates]:[Y-Coordinates]]),
    calc1,    xlookup1 - $I$92:$J$92,
    sumsq1,   SUMSQ(calc1),
    sqrt1,    SQRT(sumsq1),
    calc2,    sqrt1 * 100,
    ROUND(calc2, 0))
```

Each nested node is now a named, inspectable step. The author renames steps to something meaningful (`coords`, `deltas`, `dist`, …) in the dialog or afterward, runs `/Refactor` next if they want the leaves (`H94`, `$I$92:$J$92`, …) hoisted into inputs, then `/LET to LAMBDA` to register.

### Relationship to `/Refactor` — separate, composing commands

`/Unnest` and `/Refactor` are the two halves of full decomposition and stay **separate commands**, chained by deliberate user action (mirroring the `/Gather → /LET to LAMBDA` pattern):

```
/Unnest    →  explodes nested calls/operators into LET steps (leaves stay inline)
/Refactor  →  hoists the leaves out of those steps into input bindings
/LET to LAMBDA  →  registers the result as a LAMBDA
```

The composition is clean because `RefactorEngine` **already walks calculation-binding RHSs and hoists their cell refs into value bindings** (the existing-LET path — see [`RefactorEngine.cs`](../addin/lambda-boss/RefactorEngine.cs) `RefactorExistingLet`). `/Unnest` produces a LET full of calc-binding steps; `/Refactor` then finishes the input side with no new code. Each command stays single-purpose. Running `/Refactor` on the example above turns `H94`, `$I$92:$J$92`, and the structured refs into `inputN` value bindings, leaving the `/Unnest` steps as calc bindings — exactly the canonical shape `/LET to LAMBDA` expects.

### The core difference: this command needs a real parser

Every existing tool in the suite (`RefactorEngine`, `CellRefExtractor`, `LetParser`) is built on **token-stream + regex** scanning. That works for `/Refactor` because swapping a leaf token for a name never requires understanding structure. **Step extraction does** — to name `XLOOKUP(...)` as `coords` and rewrite its parent to reference it, the engine must know that node is one argument-complete subtree nested inside `SUMSQ(... - ...)`.

So `/Unnest` introduces the first **recursive-descent Excel-formula parser** in the repo, producing an expression tree (AST). This is the substantial new engine and the main risk area. It must handle:

- **Function calls** with comma-separated argument lists: `ROUND(x, 0)`, `XLOOKUP(a, b, c)`.
- **Operators** with correct Excel precedence and associativity: `^`, unary `-`/`+`, `*` `/`, `+` `-`, `&`, comparisons (`=` `<>` `<` `<=` `>` `>=`), and the reference operators `:` (range), `,` (union, inside refs), ` ` (intersection). Postfix `%` and `#` (spill), prefix `@` (implicit intersection).
- **Atomic leaves left opaque** (never descended into, never made a step): cell refs (`H94`, `$I$92:$J$92`, `Sheet1!A1`), spilled refs (`A1#`), structured table references (`t[City]`, `t[[X-Coordinates]:[Y-Coordinates]]`), named ranges/defined names, numeric/string/boolean literals, and array constants (`{1,2;3,4}`).
- **String literals** with embedded `""` escapes, and single-quoted sheet/workbook qualifiers — skipped wholesale, same `SkipString`/`SkipSingleQuoted` semantics the existing engine uses.

The parser is reusable: a future v2 of `/Refactor` could be re-based on it, but that's out of scope here.

### What becomes a step

A node is a **step candidate** iff it is:

1. A **function call** node — `SUM(...)`, `XLOOKUP(...)`, even when every argument is a leaf (e.g. `XLOOKUP(H94, t[City], …)` → one step), or
2. A **binary-operator expression** node — `a - b`, `x * 100`, `p & q`, comparisons.

Everything else is a **leaf** and stays inline in its parent step (it's `/Refactor`'s job to hoist leaves): bare cell refs, ranges, structured refs, named ranges, literals, array constants. **Unary operator expressions** (`-A1`, `+x`) and **postfix `%`/`#`** are *not* steps — they stay inline with their operand (too trivial to name).

**Maximum granularity by default, then collapse.** The engine explodes every step candidate into its own row. The dialog's **Include** toggle (per row) lets the author *inline a step back into its parent* when they don't want it named — so `calc1, xlookup1 - $I$92:$J$92` can be collapsed back into `sumsq1`'s RHS with one click. Default-on for every step; the preview is the source of truth for what gets written.

**The root node stays as the LET body**, not a named step — naming it would produce a pointless trailing `result, ROUND(...), \n result`. The outermost expression becomes the body with its child references rewritten to step names (see the worked example: `ROUND(calc2, 0)` is the body).

### No common-subexpression elimination (v1)

If the same sub-expression appears more than once, **each occurrence becomes its own step** — no dedupe. This matches `/Refactor`'s explicit punt on CSE and keeps the decomposition predictable (the step tree is exactly the syntax tree). CSE (compute-once, reference-twice) is noted as a possible follow-up.

### Naming steps

There is no neighbouring cell to read (unlike `/Gather`), so names are derived from the node itself:

- **Function-call step** → the lowercased function name as the base: `SUMSQ` → `sumsq`, `XLOOKUP` → `xlookup`, `ROUND` → `round`.
- **Operator-expression step** → a generic base `calc`.
- **Uniqueness** → append the smallest numeric suffix that avoids a collision with another step name, an existing binding name (existing-LET case), or a workbook defined name: `sqrt1`, `sqrt2`, …. A base used only once still gets its `1` suffix for consistency (`sumsq1`), so renumbering never shifts as the formula grows.

Every step row has a live-validated name editor (same `ExcelNameValidator` + collision rules as `LetToLambdaWindow` / `RefactorToLetWindow`). Authors who want `coords`/`deltas`/`dist` rename in the dialog.

### Activation

- Open the main popup, type `/Unnest`, press Enter.
- The popup closes and a new `UnnestToLetWindow` opens, with the active cell's formula as the input.
- If the active cell is empty or holds a literal value (no `=`-prefixed formula), the command does nothing — no dialog, no error. Same pattern as `/Refactor` and `/Gather`.
- If the active cell's formula has **no step candidates** (it's a single leaf, a single bare reference, or a lone function with the whole thing as root and nothing nested — e.g. `=A1+B1` has one operator node which is the root → body only, zero steps), the dialog still opens, shows zero steps, and Save is a no-op rewrite. *(Confirm the exact zero-step threshold during planning — see Open Questions.)*

### Existing-LET handling — bidirectional (issue #285)

When the active cell's formula is already a `=LET(...)`:

1. Parse the LET via the existing `LetParser` (shallow — splits top-level bindings).
2. Promote **each calculation binding** to a toggleable **binding-step** row, keyed by its existing name (its name is preserved — the author's choice survives).
3. Parse **the body** and **each calculation binding's RHS** with the expression parser, exploding any *further* nesting into extra sub-steps inserted **before the binding (or body) that first uses them**, preserving the existing bindings' names and relative order.
4. Existing **value bindings** are left untouched (they're already inputs — hoisting their leaves is `/Refactor`'s job).
5. On `FormatException` from `LetParser`, refuse with the same error wording `ConvertLetToLambdaCommand` uses — no dialog.

This makes `/Unnest` **bidirectional**: a fully-nested formula explodes into max-granularity steps (default direction), and a fully-*unnested* LET surfaces every binding as a toggleable step the author can **inline back (re-nest)**. The renderer resolves a binding reference **across scopes** — it renders to the binding's name when the binding-step is included, or inlines (renders through) its RHS when it isn't — the cross-scope analogue of the node-identity inlining already used for sub-steps. Un-including a binding inlines it at every downstream reference; un-including all of them collapses the LET back into a single nested expression (a bare formula when there are no value bindings, or inputs-plus-nested-body when there are).

**Sharing vs. duplication.** While a binding is included it stays a single *shared* binding (no duplication, even if referenced many times). Only deliberately un-including a shared binding duplicates its RHS at each use site — the same no-CSE stance the non-LET path takes, and the expected meaning of "nest it." The default state (all bindings included, nothing further to explode, no renames) returns the LET verbatim — a true no-op.

### Dialog (`UnnestToLetWindow`)

A new WPF window modelled on `RefactorToLetWindow`:

- **Active cell address and original formula** (read-only header).
- **Steps section** — one row per step, in leaf-first (topological) order, with an **Include all** tri-state checkbox in the section header (checked = all included, unchecked = all inlined, indeterminate = mixed) for one-click include-all / inline-all (the quick "nest everything" action). Each row shows:
  - **Step name** (editable, live-validated).
  - **RHS** (read-only — the node's expression with child references already substituted to step names).
  - **Origin** badge — "function: SUMSQ" or "operator: −", to orient the author.
  - **Include** checkbox (default on) — when off, the step is inlined back into its parent everywhere it's referenced, and the row's children re-parent to the grandparent. No warning.
- **Preview pane** — read-only, shows the synthesised LET via `FormulaFormatter.AppendLet`, updating live as the author renames or toggles Include.
- **Save** — writes the synthesised LET to the active cell. **Cancel** — closes with no change.

No reorder controls: step order is forced by data dependency (a step must precede its uses), so the topological order is canonical. Keyboard semantics match the other dialogs: Escape cancels, Ctrl+Enter saves.

### Output

On Save, the active cell's formula is overwritten with the synthesised `=LET(...)` via `Range.Formula2`. Same single-Excel-operation pattern as `/Refactor` / `/Gather` — no snapshot; relies on Excel's native undo stack. The synthesised LET must parse cleanly back through `LetParser` (round-trip safety) and, when fed into `/Refactor` then `/LET to LAMBDA`, produce a working LAMBDA.

## User Stories

- As a model author debugging a wrong answer, I want to unnest a deeply nested formula into a LET with one named step per function call, so that I can see each intermediate value in the grid instead of re-deriving them by hand.
- As an author who wrote a formula tersely during a case, I want each level of nesting exposed as an editable, named step, so that I can rename the steps that matter and inline the trivial ones.
- As an author, I want `/Unnest` to compose with `/Refactor` and `/LET to LAMBDA`, so that the same narrow-command workflow takes me from a terse nested formula all the way to a registered LAMBDA.

## Acceptance Criteria

- [ ] A new slash command `/Unnest` registered in the main popup.
- [ ] When invoked with the active cell holding a formula (LET or not) that contains step candidates, opens `UnnestToLetWindow` with the cell's formula as input.
- [ ] When invoked with the active cell empty or a literal value, the popup closes silently (no dialog, no error).
- [ ] A recursive-descent expression parser builds an AST honouring Excel operator precedence/associativity, function-call argument lists, postfix `%`/`#`, prefix `@`, reference operators (`:` range), array constants `{…}`, and string-literal / single-quote skipping.
- [ ] Atomic leaves — cell refs (incl. `$`-prefixed, sheet-qualified, spilled `A1#`), structured table refs (`t[City]`, `t[[X]:[Y]]`), named ranges, numeric/string/boolean literals, array constants — are never descended into and never become steps.
- [ ] Every function-call node and every binary-operator-expression node (other than the root) becomes a step; unary expressions and postfix `%`/`#` stay inline.
- [ ] The root node becomes the LET body with child references rewritten to step names; it is not emitted as a named step.
- [ ] Steps are emitted leaf-first (each step precedes every use of it).
- [ ] Repeated identical sub-expressions each get their own step (no CSE in v1).
- [ ] Function-call steps auto-name from the lowercased function name with a numeric suffix; operator steps auto-name `calcN`; suffixes avoid collisions with other steps, existing binding names, and workbook defined names.
- [ ] Each step row exposes a live-validated name editor (reusing `ExcelNameValidator` + `LetToLambdaWindow` collision rules) and an Include checkbox (default on). Un-including a step inlines its RHS into the parent and re-parents its children.
- [ ] The Preview pane shows the synthesised LET formatted via `FormulaFormatter`, updating as the author edits.
- [ ] Existing LET formulas: parsed via `LetParser`; the body and each calc binding RHS are exploded into new steps inserted before first use; existing bindings preserved; on `FormatException`, refuse with a clear error and no dialog.
- [ ] Bidirectional (issue #285): each existing calc binding is surfaced as a toggleable binding-step under its preserved name; un-including a binding inlines its RHS at every downstream reference; un-including all collapses the LET to a single nested expression; a shared binding stays shared while included and only duplicates when deliberately un-included; value bindings stay as inputs; the all-included no-edit case is a verbatim no-op.
- [ ] The Steps header has an Include-all tri-state checkbox driving include-all / inline-all and reflecting mixed state.
- [ ] Save writes the synthesised LET into the active cell via `Range.Formula2`. Cancel closes with no change.
- [ ] The synthesised LET parses cleanly via `LetParser` (round-trip), and the full chain `/Unnest → /Refactor → /LET to LAMBDA` produces a working LAMBDA for the worked example.
- [ ] Unit tests cover: the parser (precedence, function calls, nested calls, operator chains, structured refs, array constants, string literals, spill/`%`/`@`); step extraction (worked example, function-only formula, operator-only formula, single-leaf/single-root no-op); function-derived and `calcN` naming with collision suffixing; Include-toggle inlining and re-parenting; existing-LET explosion; round-trip through `LetParser`; and end-to-end through `LetToLambdaBuilder` after a `/Refactor` pass.

## Out of Scope

- **Hoisting leaves into inputs.** That's `/Refactor`'s job; `/Unnest` leaves cell refs, ranges, literals, and named ranges inline inside the step RHSs. The author runs `/Refactor` next.
- **Common-subexpression elimination.** Repeated sub-expressions each get their own step in v1.
- **Producing a LAMBDA.** The author runs `/Refactor` then `/LET to LAMBDA` afterward; no "Save and convert" shortcut.
- **Walking precedent cells.** `/Unnest` operates on a single cell's formula text. `/Gather` walks cells.
- **Naming steps from cell labels or heuristics.** No neighbouring-cell reading; names derive from the node only.
- **Reordering steps.** Order is forced by dependency; no reorder controls.
- **Demoting/promoting between steps and inputs** beyond the Include toggle's inline behaviour.
- **Collapsing value bindings back into inline leaves.** Re-nesting (issue #285) inlines *calculation* bindings; value bindings (inputs) stay as named bindings — folding their leaves back inline is `/Refactor`'s reverse and remains out of scope.
- **Common-subexpression elimination when re-nesting.** Inlining a shared binding duplicates it at each use site (no CSE), matching the forward direction.
- **Persistence of dialog state across opens** — each `/Unnest` invocation starts fresh.
- **Editing the preview pane directly** — all edits via the row controls.

## Open Questions

- **Command name.** `/Unnest` (full name "Unnest to LET") fits the suite (`/Gather`, `/Refactor`, `/EditLambda`). Alternatives considered: `/Explode`, `/Decompose`, `/Steps`. Confirm `/Unnest` during planning.
- **Operator-step naming.** `calcN` is generic. Would operator-semantic bases read better — `diffN` for `−`, `prodN` for `*`, `sumN` for `+`, `catN` for `&`? Tentative: ship `calcN` in v1 for simplicity (the author renames the ones that matter); revisit if feedback wants smarter operator names.
- **Zero-step threshold.** A formula like `=A1+B1` has exactly one operator node, which is the root → zero steps, body-only. Should `/Unnest` open the dialog (showing nothing to do) or no-op silently like an empty cell? Tentative: open the dialog so the behaviour is discoverable and consistent; confirm during planning.
- **Existing-LET scope.** Full body-and-calc-binding explosion vs body-only for v1 (see Existing-LET handling). Tentative: implement body + calc-binding explosion; fall back to body-only if the re-parenting across existing bindings proves fiddly. Confirm during planning.
- **Argument-level operator nodes inside a call.** In `ROUND(SQRT(...) * 100, 0)`, the `SQRT(...) * 100` operator node is a single argument. It becomes a step (`calcN`) and the call references it — confirmed by the worked example. No open issue, noted for the plan's test list.
- **Reference operators as steps.** A bare range like `A1:B5` is a leaf, but a computed range like `OFFSET(...):B5` (range operator over a function result) is rare and odd. Tentative: treat `:` range expressions as leaves (never steps) regardless of operands; confirm during planning.
