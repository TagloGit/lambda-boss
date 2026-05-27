# 0008 — Refactor to LET

## Problem

Today, the path to a registered LAMBDA requires the author to hand-write a `=LET(...)` first, with every input they want exposed as a LAMBDA parameter already pulled into its own value binding. `LET to LAMBDA` (spec 0001/0002) then only treats those value bindings as candidate parameters. This is friction in two distinct situations:

1. **No LET at all.** The author has a working but messy expression like `=IF(A1<10, IF(A1>2, SUM(B1:B5), 0), SUM(B2:B6))` and wants a LAMBDA. They must mechanically rewrite the formula as a LET, identifying the cell refs/ranges by eye, picking names, and rewriting every occurrence.
2. **Messy LET.** The author has a LET, but the inputs they care about aren't standalone value bindings — they're embedded inside calculation bindings or the body. Or the LET has two value bindings with the same RHS (e.g. `LET(a, A1, b, A1, ...)`), which today produces two LAMBDA parameters where the author wanted one.

Both situations have the same underlying need: hoist every reference to an in-cell input — cell refs, ranges, optionally named ranges/literals — into its own value binding, dedupe by canonical address, and rewrite the rest of the formula to use the new names. `/Gather` (spec 0005) already does a related thing across cells; this spec is its within-cell counterpart.

## Proposed Solution

A new slash command, `/Refactor` (full name **Refactor to LET**), that takes the active cell's formula — LET or not — and rewrites it as a tidy LET where every cell ref and range appears as a single value binding. The author then runs `LET to LAMBDA` as the next step. The two commands stay separate; chaining is a deliberate user action, mirroring the `/Gather → /LET to LAMBDA` pattern.

### Worked example — non-LET formula

Active cell:

```excel
=IF(A1<10, IF(A1>2, SUM(B1:B5), 0), SUM(B2:B6))
```

After `/Refactor` (with default auto-names, all promotable rows left un-promoted):

```excel
=LET(
    input1, A1,
    input2, B1:B5,
    input3, B2:B6,
    IF(input1<10, IF(input1>2, SUM(input2), 0), SUM(input3)))
```

The author renames in the dialog before Save (or after, by hand). Then `/LET to LAMBDA` does its existing work.

### Worked example — messy LET (full refactor)

Active cell:

```excel
=LET(
    a, A1,
    b, A1,
    getMax, MAX(B1:B5),
    IF(a<10, getMax, b))
```

After `/Refactor`:

- `b`'s RHS matches `a`'s RHS (`A1`) — merged into `a` (rule: keep the first binding's name; rewrite later refs to use it).
- `MAX(B1:B5)` contains `B1:B5`, which gets hoisted as a new input binding `input1`.
- `getMax` stays as a calculation binding but its RHS is rewritten to use `input1`.
- The body's reference to `b` becomes `a`.

Result:

```excel
=LET(
    a, A1,
    input1, B1:B5,
    getMax, MAX(input1),
    IF(a<10, getMax, a))
```

The dialog shows a `merged b ← a` note so the author knows what happened.

### Activation

- Open the main popup, type `/Refactor`, press Enter.
- The popup closes and a new `RefactorToLetWindow` opens, with the active cell's formula as the input.
- If the active cell is empty or holds a literal value (no `=`-prefixed formula), the command does nothing — no dialog, no error message. Same pattern as `/Gather`.

### Token extraction

The refactor walks the formula's token stream, skipping over string literals (using the same `SkipString` semantics as `LetParser`), and classifies each token:

| Token shape | Default action | Notes |
|---|---|---|
| A1-style cell ref: `A1`, `$A$1`, `Sheet1!A1` | Extract as input | Canonical form for dedupe: row/column letters uppercased, `$` dropped, sheet qualifier normalised (see below). `A1` and `$A$1` and `$A1` collapse to the same binding (first occurrence wins for RHS text). |
| Range ref: `A1:B5`, `B:B`, `Sheet1!A1:B5`, `$A:$A` | Extract as input | Same canonicalisation rules; range bounds are normalised so `A1:B5` and `B5:A1` collapse. |
| Spilled ref: `A1#` | Extract as input | Binding RHS keeps the `#`. Dedupe key is distinct from `A1` — the `#` matters for dynamic-array semantics. Mirrors `/Gather`'s rule. |
| External workbook ref: `[Other.xlsx]Sheet1!A1` | Show in dialog as promotable, default off | When left un-promoted, the ref is inlined in the body; the author opts in if they want it as a LAMBDA input. |
| Named range / defined name (workbook or sheet scope) | Show in dialog as promotable, default off | Detection: any identifier in the formula that resolves to a workbook `Name` whose `RefersTo` does **not** start with `=LAMBDA(`. LAMBDA names are function calls, not inputs, and are excluded from promotion. The dialog reads `workbook.Names` **once** on open and builds an `OrdinalIgnoreCase` lookup; identifier tokens are checked against this lookup as we walk. |
| Numeric literal, string literal, boolean literal | Show in dialog as promotable, default off | Literals stay inline by default. Dedupe by value equality (numeric / string / boolean equality), so two occurrences of `10` collapse to one promotable row and a single Promote toggle replaces every occurrence with the binding name. |
| Function calls, operators, parentheses, commas | Left as-is | The refactor never extracts sub-expressions. |
| LET binding names from an existing LET | Reused, not re-extracted | When the input is already a LET, existing bindings appear as rows in the dialog and can be renamed / dropped / reordered like any other row. |

**Sheet-qualifier normalisation.** An unqualified ref (`A1`) is treated as referring to the active cell's sheet, so `A1` and `Sheet1!A1` collapse to the same binding when the active cell is on `Sheet1`. Refs to other sheets keep their qualifier and stay distinct. The canonical dedupe key uses the fully sheet-qualified form (with the active cell's sheet name filled in for unqualified refs). The binding's RHS text preserves whichever form appeared **first** in the original formula, so the LET reads naturally relative to its source.

For each extracted token, the **dedupe key** is the canonical form. The first occurrence's text becomes the binding RHS; all later occurrences are rewritten to the binding name.

### Existing-LET handling

When the active cell's formula starts with `=LET(...)`, `/Refactor` performs a **full refactor**:

1. Parse the LET via the existing `LetParser`.
2. Treat each existing **value binding** as a row in the dialog. Pre-populate name and RHS.
3. Walk each existing **calculation binding**'s RHS and the **body** for extractable tokens, hoisting per the rules above.
4. **Merge value bindings with equivalent RHS.** Two value bindings whose RHS canonicalises to the same key are merged: the first binding's name wins; later bindings are removed from the row list and their references in calculation bindings / body / other value bindings are rewritten to the surviving name. The dialog shows a `merged <old> ← <kept>` note next to the surviving row.
5. **Calculation bindings stay calculation bindings** — they keep their original name and order, but their RHS is rewritten to use input bindings. The refactor does not convert calculations into inputs.
6. **Ordering**: the synthesised LET puts all value bindings first (in the order the dialog shows), then calculation bindings (in their original source order). This matches the canonical shape that `LET to LAMBDA` expects.

**Round-tripped LAMBDAs (spec 0002 optional params).** When the input LET came from `/EditLambda` on a LAMBDA that was originally created with optional parameters, its body contains `IF(ISOMITTED(paramName), default, paramName)` wrappers. These wrappers are walked like any other expression: `paramName` is already a value binding (so it's reused), and the `default` sub-expression is walked for extractable refs in the normal way. No special-case logic is needed — the existing-LET rules above cover it naturally. A test case will confirm.

If the existing LET parses cleanly via `LetParser` but contains no extractable tokens after merge (already perfectly tidy), the dialog still opens, shows the existing bindings, and lets the author rename or reorder. Save is a no-op rewrite if nothing changed.

If parsing fails (`FormatException` from `LetParser`), refuse with the same error wording `ConvertLetToLambdaCommand` uses.

### Naming

- **Auto-name format**: `input1`, `input2`, ... assigned in the order tokens are first encountered in the formula.
- **Collision avoidance**: if the active cell's formula or workbook context already binds `input1` (existing LET binding name, or a defined name on the workbook), the auto-namer skips that number and tries the next.
- **Rename**: every row in the dialog has a name editor with live validation (same `ExcelNameValidator` and collision rules as `LetToLambdaWindow`).
- **No label-from-cell heuristic**: `/Gather` reads neighbouring cells to seed names; `/Refactor` does not, because it operates on a single cell. Authors who want meaningful names rename in the dialog.

### Dialog (`RefactorToLetWindow`)

A new WPF window modelled on `LetToLambdaWindow`. Layout:

- **Active cell address and original formula** (read-only header).
- **Inputs section** — a reorderable list of rows. Each row shows:
  - **Binding name** (editable, live-validated).
  - **RHS** (read-only).
  - **Source** badge — "extracted", "existing LET binding", or "merged ← otherName" for merge survivors.
  - **Include** checkbox — when off, the binding is dropped and its inline occurrences are restored to the original text everywhere they occurred, with no warning even if that re-introduces duplication. The preview pane is the source of truth for what will be written. For value bindings only; calculation bindings (in the existing-LET case) cannot be dropped here (use a separate flow if you want to inline a calculation step).
  - **Drag handle** for reorder (kept rows only, matching `LetToLambdaWindow`).
- **Promote-to-input section** — a separate list of *candidate* rows for named ranges, external refs, and literals found in the formula. Each row has:
  - **Token text** (read-only).
  - **Occurrences count** — helpful for deciding whether promoting saves work.
  - **Promote** checkbox (default off). When on, the row moves into the Inputs section with an auto-name and an editable name field, just like an extracted ref.
- **Calculation bindings** (existing-LET case only) — shown read-only with their (rewritten) RHS, so the author can verify the rewrites look correct. Not reorderable, not droppable.
- **Preview pane** — read-only, shows the synthesised LET via the existing `FormulaFormatter`. Updates live as the author edits rows.
- **Save** — writes the synthesised LET to the active cell. **Cancel** — closes with no change.

Keyboard semantics match other Lambda Boss dialogs: Escape cancels, Ctrl+Enter saves, Alt+Up / Alt+Down reorder a focused input row.

### Output

On Save, the active cell's formula is overwritten with the synthesised `=LET(...)`. Same single-Excel-operation pattern as `/Gather` — no separate snapshot; relies on Excel's native undo stack.

After Save, the author typically opens the popup and runs `/LET to LAMBDA` as the second step. The two commands chain naturally but are not automatically linked.

## User Stories

- As a model author, I want to refactor a messy single-cell formula into a tidy LET, so that I can run LET to LAMBDA without writing the LET by hand.
- As a model author with an existing LET whose inputs aren't standalone value bindings, I want a one-shot tidy that hoists cell refs out of calculation bindings and merges duplicate value bindings, so that LET to LAMBDA exposes the right parameters.
- As an author refactoring a formula, I want to see all named ranges and literals the formula touches, and choose which ones to promote to LAMBDA inputs, so that I can expose the constants I want to parameterise without having every magic number become a binding by default.

## Acceptance Criteria

- [ ] A new slash command `/Refactor` registered in the main popup.
- [ ] When invoked with the active cell holding a formula (LET or not), opens `RefactorToLetWindow` with the cell's formula as input.
- [ ] When invoked with the active cell empty or a literal value, the popup closes silently (no dialog, no error).
- [ ] Token extraction handles A1-style cell refs and ranges (with sheet qualifier, `$`-prefixed, and spilled `A1#` forms), and dedupes by canonical form (`A1` and `$A$1` merge into one binding; `A1` and `A1#` stay separate).
- [ ] Range bounds normalise so `A1:B5` and `B5:A1` collapse to the same binding.
- [ ] String literals are skipped during token extraction (no extraction inside `"..."`).
- [ ] Existing LET formulas are parsed via `LetParser`; on `FormatException`, refuse with a clear error and no dialog.
- [ ] Existing value bindings with equivalent canonical RHS are merged, with the first binding's name kept and later refs rewritten. The dialog surfaces each merge with a visible note.
- [ ] Existing calculation bindings are preserved (name + order); their RHS is rewritten to use input bindings, but they themselves are not extracted or dropped.
- [ ] Named ranges, external workbook refs, and literals appear in a separate "Promote to input" section with default-off toggles. When promoted, they become input bindings with auto-names.
- [ ] LAMBDA names (workbook names whose `RefersTo` starts with `=LAMBDA(`) are **not** offered as promotable.
- [ ] Auto-names follow `inputN` sequential format, skipping any number already in use as a binding name in the formula or as a workbook name.
- [ ] Each input row exposes a live-validated name editor, an Include checkbox, and reorder controls (Alt+Up/Alt+Down + drag handle). Validation reuses `ExcelNameValidator` and the same collision rules as `LetToLambdaWindow`.
- [ ] The Preview pane shows the synthesised LET formatted via `FormulaFormatter`, updating as the author edits the dialog.
- [ ] Save writes the synthesised LET into the active cell. Cancel closes the dialog with no change.
- [ ] The synthesised LET parses cleanly via `LetParser` (round-trip safety) and, when fed straight into `LetToLambdaBuilder`, produces a working LAMBDA.
- [ ] Unit tests cover: non-LET formula, existing-tidy LET (no-op rewrite), existing-messy LET (merge + hoist), spill refs preserved, sheet-qualified refs, range normalisation, named-range promotion, literal promotion, external-ref promotion, auto-name collision avoidance, and full round-trip through `LetToLambdaBuilder`.

## Out of Scope

- Producing a LAMBDA. The author runs `/LET to LAMBDA` after Save. The dialog has no "Save and convert" shortcut button.
- Sub-expression extraction. If `SUM(B1:B5)` appears twice, only `B1:B5` is shared as a binding; the `SUM(...)` call remains inline in both places. Common-subexpression elimination is a separate, harder problem.
- Walking precedent cells. That's `/Gather`'s job; `/Refactor` operates on a single cell's formula text.
- Auto-promoting named ranges or literals based on heuristics. They appear in the dialog and stay un-promoted unless the author opts in.
- **Demoting calculation bindings to inputs** (or vice versa) in the existing-LET case. Example: `=LET(getMax, MAX(A1:A10), getMax + 5)` — today `A1:A10` becomes the input and `getMax` stays as a calculation binding rewritten to `MAX(input1)`. Demoting `getMax` to an input would *discard* the `MAX(A1:A10)` calculation and turn `getMax` into a direct LAMBDA parameter the caller supplies pre-computed. That changes semantics and is a different feature; out of scope here.
- Persistence of dialog state across opens; each `/Refactor` invocation starts fresh.
- Detecting and inlining a single LAMBDA call in the active cell. Use `/EditLambda` (spec 0003) first, then `/Refactor`.
- Reverse direction (LET back into a non-LET expression with everything inlined).
- Warning on volatile functions, errors in cell values, or external-workbook reachability — same as `/Gather`, those pass through silently.

## Open Questions

- **Worksheet-scoped names.** Excel lets a `Name` be scoped to a worksheet rather than the workbook. The defined-name lookup built from `workbook.Names` includes workbook-scoped names; worksheet-scoped names live on `worksheet.Names`. Should `/Refactor` also read the active sheet's `Names` collection and treat those as promotable? Tentative: yes, union the active sheet's names into the lookup so worksheet-scoped names behave the same as workbook-scoped ones. Confirm during plan.
- **LAMBDA identifier without a trailing `(`.** A defined name whose `RefersTo` starts with `=LAMBDA(` is excluded from promotion. But a malformed/in-progress formula could reference such a name without a `(` after it (e.g. an incomplete edit). Tentative: still treat it as a LAMBDA-name reference and skip promotion — there's no scenario where the author wants to promote a LAMBDA name into a binding. Confirm during plan.
