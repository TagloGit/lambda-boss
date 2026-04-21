# 0001 — Reorder LAMBDA input parameters

## Problem

When converting a `=LET(...)` formula to a LAMBDA via the "LET to LAMBDA" command, the order of the resulting LAMBDA parameters is fixed to the source order of the LET bindings. Authors often want a different calling convention (for example, required args first, then optional; or ordering by conceptual importance) than the order that happened to be convenient when writing the LET. Today the only workaround is to rewrite the LET formula first, which is friction.

## Proposed Solution

Add up/down reorder controls on each "kept" (checked) input row in the `LetToLambdaWindow`. The order shown in the UI is the order the parameters appear in the generated `=LAMBDA(...)` signature.

Unchecked bindings (those being kept as internal `LET` bindings inside the LAMBDA body) remain in their original source order — they are not reorderable, because their RHS expressions may reference earlier bindings.

## User Stories

- As a model author, I want to reorder the input parameters of the generated LAMBDA, so that the LAMBDA's calling convention matches how I want callers to use it.
- As a model author, I want the reorder controls to be keyboard-accessible, so that I can work through the dialog without reaching for the mouse.

## Acceptance Criteria

- [ ] Each kept input row in `LetToLambdaWindow` has "move up" and "move down" buttons.
- [ ] Buttons are disabled at the boundaries (first row's "up" disabled, last row's "down" disabled).
- [ ] Reordering only moves the row among other kept rows; unchecked rows stay in source order and are not affected.
- [ ] Unchecking a row removes it from the reorderable set; rechecking it re-inserts it at the end of the kept rows.
- [ ] The generated `=LAMBDA(...)` signature reflects the UI order of kept rows.
- [ ] Internal LET bindings (unchecked rows and calculations) still appear in source order in the generated body.
- [ ] Rename behaviour is unchanged: if a kept row's param name differs from the original binding name, references in the body and other bindings are renamed.
- [ ] Keyboard access: when a row has focus, Alt+Up / Alt+Down move it (or equivalent — confirmed in plan).

## Out of Scope

- Drag-and-drop reordering. Buttons only for v1.
- Reordering internal (unchecked) bindings.
- Persisting a preferred ordering across sessions.

## Open Questions

- None — keyboard shortcut choice (Alt+Up/Down vs Ctrl+Up/Down) to be decided in the plan.
