# 0002 — Optional LAMBDA input parameters

## Problem

When a `=LET(...)` formula is converted to a LAMBDA, every kept binding becomes a required parameter on the generated LAMBDA. In practice, many of these bindings have a sensible "current" value (the one just used in the source cell) that the author would like to use as a default when the caller doesn't supply the argument. Today the author has to hand-edit the LAMBDA after generation to wrap the parameter with `IF(ISOMITTED(...), defaultExpr, param)` — tedious, error-prone, and easy to skip.

## Proposed Solution

Add an "Optional" checkbox on each kept input row in the `LetToLambdaWindow`. When a parameter is marked optional:

- It stays declared as a normal LAMBDA parameter (Excel has no optional-parameter syntax — any parameter can be checked with `ISOMITTED`).
- The generated LAMBDA body wraps each use of the parameter via a LET binding of the form `IF(ISOMITTED(param), defaultExpr, param)`, so downstream references resolve to the default when the caller omits the argument.
- The **default expression is the RHS text of the original LET binding** — i.e. the formula that produced the value in the current cell. Not the evaluated result.

### Example

Given:

```excel
=LET(x, 10, y, A1, x + y)
```

With the author keeping both bindings as inputs, renaming `y` → `offset`, and marking `offset` as optional:

```excel
=LAMBDA(x, offset, LET(offset, IF(ISOMITTED(offset), A1, offset), x + offset))
```

If the author also marks `x` as optional:

```excel
=LAMBDA(x, offset, LET(x, IF(ISOMITTED(x), 10, x), offset, IF(ISOMITTED(offset), A1, offset), x + offset))
```

The `IF(ISOMITTED(...))` bindings are added as the first bindings in the inner LET, before any internal (unchecked) LET bindings from the original formula. This ensures their rewritten values are visible to anything that references them.

## User Stories

- As a model author, I want to mark a LAMBDA parameter as optional and have its current value preserved as the default, so that callers can omit it and get the same behaviour as the source cell.
- As a model author, I want optional parameters to work alongside renaming and reordering, so that I can shape the full calling convention in one pass.

## Acceptance Criteria

- [ ] Each kept input row in `LetToLambdaWindow` has an "Optional" checkbox.
- [ ] The checkbox is disabled (and unchecked) when the row's "Keep" checkbox is unchecked.
- [ ] When at least one parameter is optional, the generated LAMBDA wraps each optional parameter via a LET binding using `IF(ISOMITTED(param), <original RHS text>, param)`.
- [ ] Optional-parameter LET bindings appear **before** any internal LET bindings from the original formula, so internal bindings can reference optional parameters without hitting un-defaulted values.
- [ ] When no parameters are optional, the generated LAMBDA is byte-for-byte identical to today's output.
- [ ] Default expressions preserve the original RHS text verbatim (including references like `A1`, other names, etc.), subject to existing rename rewrites applied to other kept params referenced inside the RHS.
- [ ] Optional works correctly in combination with rename and reorder from spec 0001.
- [ ] Validation: a row cannot be both "optional" and "unchecked" (Keep=false). The UI enforces this by disabling Optional when Keep is off.

## Out of Scope

- Marking internal (unchecked) bindings as optional.
- Excel's newer named-argument syntax or any formal "optional" declaration beyond `ISOMITTED`.
- Automatically reordering optional params to the end of the signature. The author controls order via spec 0001; default order is source order.

## Open Questions

- Should the `IF(ISOMITTED(...))` LET binding reuse the parameter name (shadowing the LAMBDA parameter), or use a distinct name like `paramName_`? Reusing the name is cleaner and matches typical hand-written patterns; the plan should confirm it works under Excel's scoping and that renames cascade correctly. Tentative answer: reuse the parameter name.
