# 0003 — Edit Lambda (round-trip LAMBDA to LET)

## Problem

Once a LAMBDA has been registered in the Name Manager (via the LET to LAMBDA command or imported from a library), there is no convenient way to edit it. Authors who want to tweak the implementation must open Name Manager, find the definition, and hand-edit the formula inline — a poor editing experience for anything non-trivial. This blocks the natural authoring loop of "try it, tune it, save it" that authors already get while working in LET form.

## Proposed Solution

Add an "Edit Lambda" ribbon command that performs the inverse of LET to LAMBDA: given a cell whose formula is a single call to a registered LAMBDA, replace the cell's formula with an equivalent `=LET(...)` that inlines the LAMBDA's parameters (bound to the call-site arguments) followed by the LAMBDA's body. The author edits the LET freely, then rounds-trips back to LAMBDA form via the existing "LET to LAMBDA" command — reusing the same name overwrites the prior definition.

### Example

LAMBDA registered as `MyCalc`:

```excel
=LAMBDA(x, y, x * y + 1)
```

Active cell:

```excel
=MyCalc(A1, B1 + 2)
```

After Edit Lambda, the cell becomes:

```excel
=LET(x, A1, y, B1 + 2, x * y + 1)
```

The `MyCalc` name definition is left untouched. The author edits, then runs LET to LAMBDA with the name `MyCalc` again to overwrite.

### Detection rules

The command only triggers when the active cell's formula is **exactly** a single Lambda call:

- Formula starts with `=`, followed by an identifier that resolves to a workbook-scoped name whose `RefersTo` starts with `=LAMBDA(`.
- Followed by `(args...)` where the closing paren matches the outer open paren and there is no trailing content.

Any other shape (e.g. `=MyCalc(A1) + 5`, `=IF(cond, MyCalc(A1), 0)`, `=LET(...)`, a value literal) triggers a clear error and makes no change. The author can always wrap their expression manually if they want to edit just a nested call.

### Argument count

- If the number of call-site arguments is less than the number of LAMBDA parameters, trailing parameters are bound to `ISOMITTED()` style — for v1 we just leave them unbound, which means the generated LET will have fewer bindings than LAMBDA has params. The resulting LET may fail to evaluate until the author fills them in; that's acceptable feedback.
- If there are more call-site arguments than LAMBDA parameters, refuse with a clear error.

## User Stories

- As a model author, I want to expand a LAMBDA call back into its LET form in the active cell, so that I can edit the implementation using the same tools I used to author it.
- As a model author, I want to round-trip by rerunning LET to LAMBDA with the same name, so that my edits overwrite the prior definition without extra steps.

## Acceptance Criteria

- [ ] A new ribbon button, "Edit Lambda", in the Generate group of the Lambda Boss tab.
- [ ] When the active cell's formula is exactly `=LambdaName(arg1, arg2, ...)` and `LambdaName` is a workbook name whose `RefersTo` starts with `=LAMBDA(`, the command replaces the cell's formula with `=LET(param1, arg1, param2, arg2, ..., body)` where `param1..N` and `body` come from parsing the LAMBDA definition.
- [ ] Arguments are split at top-level commas only (respecting nested parens, quoted strings, and brackets) — reuse the same splitter used by `LetParser`.
- [ ] The LAMBDA name definition is **not** modified or deleted.
- [ ] If the active cell is not exactly a single Lambda call, the command shows a clear error: "Edit Lambda requires a cell whose formula is exactly a call to a LAMBDA (e.g. `=MyLambda(A1, B1)`)."
- [ ] If the number of call-site arguments exceeds the LAMBDA's parameter count, the command shows a clear error stating the mismatch.
- [ ] Round-trip: running LET to LAMBDA on the resulting cell with the same name (e.g. `MyCalc`) replaces the existing definition via the `LambdaLoader.InjectLambda` "update existing" path.
- [ ] A new `LambdaSignatureParser` (or similar) extracts `(param1, ..., paramN, body)` from a `=LAMBDA(...)` `RefersTo` string, mirroring `LetParser`.

## Out of Scope

- Editing LAMBDAs that are inside a larger expression (e.g. `=MyLambda(A1) + 5`).
- Multi-cell arrays, or cells with dynamic-array spill.
- Editing a LAMBDA without a call site (e.g. from Name Manager directly, with no reference cell).
- Preserving comments or provenance when the name is later overwritten — handled by LambdaLoader today and unchanged here.
- Detecting when the LAMBDA references other LAMBDAs that would also need expanding.

## Open Questions

- When the LAMBDA was originally generated with optional-parameter wrapping (spec 0002), its body will contain `IF(ISOMITTED(...))` bindings. Edit Lambda inlines the body verbatim, so those wrappers remain in the LET. Round-tripping through LET to LAMBDA with the same "Optional" choices should produce an equivalent result, but double-wrapping could occur if the author re-checks "Optional". The plan should confirm behaviour and either document the gotcha or strip the wrappers on detection. Tentative answer: document the gotcha in v1; no automatic stripping.
