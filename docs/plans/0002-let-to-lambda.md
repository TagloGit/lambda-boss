# Plan: LET → LAMBDA generator (issue #74)

Ribbon button that converts the active cell's `=LET(...)` formula into a workbook-scoped `LAMBDA` via the Name Manager. Source cell is left untouched.

## Input detection rule

Walk the LET's binding pairs. A binding's RHS is either:

- **Value** → becomes a LAMBDA input (default param name = the LET binding name).
- **Calculation** → stays as an internal binding inside the generated LAMBDA body.

"Calculation" means the RHS, after paren/string-literal-aware tokenization, contains any of:

- A top-level operator: `+ - * / ^ & = < >` (unary minus on a literal does not count).
- An identifier immediately followed by `(` (a function call).
- Top-level whitespace between tokens (Excel intersection operator).

Otherwise it's a value: a literal (number/string/bool), a cell ref, a range, a sheet-qualified ref, or a bare identifier (named range or unbound name).

The LET body expression is never scanned for references — we only promote LET bindings.

## Removed inputs

If the user removes a row in the dialog, that LET binding is preserved as a regular internal binding in the generated LAMBDA body (wrapped in a `LET(...)` if needed to hold remaining internals).

## Generated formula shape

Given `=LET(a, A1:A10, b, MAX(a), c, 2, b + c)` where user keeps `a` and `c` as inputs:

```
=LAMBDA(a, c, LET(b, MAX(a), b + c))
```

If no internal bindings remain, the LET wrapper is omitted:

```
=LAMBDA(x, y, SUM(x, y))
```

If no inputs are kept, the LAMBDA takes zero params.

## Components

### 1. `LetParser` (new, `addin/lambda-boss/LetParser.cs`)

- Validates formula starts with `=LET(` (case-insensitive, no leading whitespace).
- Reuses paren/string-literal balancing logic from `LambdaParser`.
- Returns ordered `List<LetBinding> Bindings` + `string Body`.
- `LetBinding { Name, RhsText, IsCalculation }`.

### 2. `LetToLambdaBuilder` (new, `addin/lambda-boss/LetToLambdaBuilder.cs`)

- Input: parsed LET + user choices (lambda name, per-binding: keep-as-input with chosen param name, or remove).
- Substitutes renamed params through retained internal bindings and body.
- Emits the `=LAMBDA(...)` string.
- Substitution is token-aware (respects strings, skips function-name identifiers followed by `(`).

### 3. `ConvertLetToLambdaCommand` (new, `addin/lambda-boss/Commands/ConvertLetToLambdaCommand.cs`)

- Reads `ExcelDnaUtil.Application.ActiveCell.Formula`.
- Validates leading `=LET(` — on mismatch, shows Excel message box and aborts.
- Runs `LetParser`, opens the dialog on the existing STA thread.
- On Save: calls `LambdaLoader.InjectLambda(name, formula)` with no comment (per spec).

### 4. `LetToLambdaWindow.xaml[.cs]` (new, `addin/lambda-boss/UI/`)

- Hosted on the shared WPF STA thread in `ShowLambdaPopupCommand` (add a second window alongside `_window` / `_settingsWindow`).
- Lambda name textbox at top, live-validated:
  - Empty → red hint "Name required".
  - Invalid Excel name (whitespace, starts with digit, reserved) → red hint.
  - Collides with an existing workbook name → red hint "Name already exists in this workbook". (No suggestion.)
  - Valid → Save button enabled.
- `ItemsControl` of rows, one per LET binding whose RHS is a value:
  - `[x remove]` button, param-name TextBox (defaults to binding name), label showing original RHS text.
- Save / Cancel buttons.
- Centred on Excel via existing `WindowPositioner.CenterOnExcel`.

### 5. Ribbon wiring (modify `RibbonController.cs`)

- New `<group id="GenerateGroup" label="Generate">` on the Lambda Boss tab.
- One button: `id="LetToLambdaButton" label="LET to LAMBDA"`.
- Icon: `imageMso="FunctionWizard"` (closest built-in match; swap later if we want custom).
- Handler `OnLetToLambda` delegates to `ConvertLetToLambdaCommand.Run()`.

## Tests (`addin/lambda-boss.Tests/`)

### `LetParserTests`
- Single binding, multiple bindings, final body.
- Nested LETs (outer LET with a LET as a binding RHS → that binding is a calculation).
- String literals containing `,` and `(`.
- Rejects non-LET formulas.
- Classifies RHS correctly: literal, cell ref, range, sheet-qualified ref, named range → value; operators, function calls, intersection → calculation.
- Unary minus on literal is a value; `-SUM(x)` is a calculation.

### `LetToLambdaBuilderTests`
- All inputs kept → no internal LET wrapper.
- Mixed keep/remove → retained internal bindings wrapped in `LET(...)` inside LAMBDA.
- Renamed param substitutes through body and internal bindings but not inside string literals.
- Zero kept inputs → `LAMBDA(body)` (no params, no LET wrapper unless internals remain).
- Rename collision with existing LET binding name → builder throws (UI prevents but guard anyway).

### `ExcelNameValidatorTests` (new small helper)
- Valid / invalid / reserved names.
- Uses `workbook.Names` collision check — unit test only covers the pure syntactic validator; collision check is integration-only.

## Out of scope (confirmed)

- Rewriting the source cell.
- Named-range / cell-ref detection inside the LET body.
- Worksheet-scoped names.
- LAMBDA → LET round-trip.
- Suggesting alternative names on collision (live validation only).
- Provenance comment on generated names.

## Implementation order

1. `LetParser` + tests.
2. `LetToLambdaBuilder` + tests.
3. Excel name validator + tests.
4. `LetToLambdaWindow` XAML + view model.
5. `ConvertLetToLambdaCommand` + STA thread hookup in `ShowLambdaPopupCommand`.
6. Ribbon button.
7. Manual smoke test in Excel.
