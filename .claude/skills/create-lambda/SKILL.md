---
name: create-lambda
description: "Create a new LAMBDA with self-documenting format and test cases. Usage: /create-lambda [issue-number]"
allowed-tools: Bash, Read, Write, Edit, Glob, Grep, AskUserQuestion
---

# Create LAMBDA — lambda-boss

Guide LAMBDA authoring with the correct self-documenting format, generate Help sections, and create companion `.tests.yaml` files.

## Arguments

- `[issue-number]` — (Optional) A GitHub issue number on `TagloGit/lambda-boss` describing the LAMBDA to create. Issues with the `lambda-idea` label contain structured LAMBDA proposals.

If no issue number is provided, list all open `lambda-idea` issues and ask the user to select one:

```bash
gh issue list -R TagloGit/lambda-boss --label "lambda-idea" --state open
```

If there are no open `lambda-idea` issues, tell the user and ask them to describe the LAMBDA they want to create instead.

## File Locations

- Lambda file: `lambdas/<category>/<Name>.lambda`
- Test file: `lambdas/<category>/<Name>.tests.yaml`

If the category directory doesn't exist, create it with a `_library.yaml`:

```yaml
name: <Category>
description: <Brief description>
default_prefix: <category>
```

## Lambda File Format

The `.lambda` file MUST use this exact format. Pay close attention to spacing, line endings, and structure.

**Critical format rules:**
- Line endings must be `\n` only (no `\r\n` carriage returns)
- No tab characters — use spaces only
- File must end with `);` followed by a single newline
- All parameters must be wrapped in `[]` (optional syntax)
- `Help?` must be a LET variable, not bare pseudo-syntax

### Template

```
/*  FUNCTION NAME:      <Name>
    DESCRIPTION:*//**<One-line description.>*/
/*  REVISIONS:          Date        Developer   Description
                        <YYYY-MM-DD>  <Developer>   Initial version
*/
<Name> = LAMBDA(
//  Parameter Declarations
    [<param1>],
    [<param2>],
//  Help
    LET(Help, TEXTSPLIT(
                "FUNCTION:      →<Name>(<param1>, <param2>)¶" &
                "DESCRIPTION:   →<One-line description.>¶" &
                "VERSION:       →<Mon DD YYYY>¶" &
                "PARAMETERS:    →¶" &
                "   <param1>          →(<Required/Optional>) <Description.>¶" &
                "   <param2>          →(<Required/Optional>) <Description.>¶" &
                "EXAMPLE:       →¶" &
                "Formula        →=<Name>(<example_args>)¶" &
                "Result         →<example_result>",
                "→", "¶"
                ),
    //  Check inputs
        Help?, ISOMITTED(<first_required_param>),
    //  Procedure
        result, <formula>,
        IF(Help?, Help, result)
)
);
```

### Format Details

**Header block:**
- `FUNCTION NAME:` — matches the filename (without `.lambda`)
- `DESCRIPTION:*//**..*/` — the `*//**` and `*/` are comment delimiters that allow the description to be extracted as a doc comment
- `REVISIONS:` — date in `YYYY-MM-DD` format, developer name, description

**Parameter declarations:**
- One parameter per line, each wrapped in `[]`
- Preceded by `//  Parameter Declarations` comment
- Last parameter followed by a comma before the `//  Help` section

**Help block:**
- `LET(Help, TEXTSPLIT(` — opens the Help table definition
- Each row is `"<label>→<value>¶" &` — the `→` separates columns, `¶` separates rows
- The last row has NO `¶` and NO `&` — just the closing `",`
- Column alignment: pad labels to 15 chars, parameter names to 14 chars
- VERSION date format: `Mon DD YYYY` (e.g. `Apr 12 2026`)
- Closes with `"→", "¶"` on its own line, then `),`
- Parameter descriptions: `(Required)` or `(Optional)` prefix followed by description

**Check inputs:**
- `//  Check inputs` comment
- `Help?, ISOMITTED(<first_required_param>),` — uses the first parameter that is logically required
- Additional input checks can follow (e.g. `Delimit?, NOT(ISOMITTED(Delimiter)),`)

**Procedure:**
- `//  Procedure` comment
- `result, <formula>,` — the computation
- `IF(Help?, Help, result)` — return Help table or result

**Closing:**
- `)` closes the LET
- `);` closes the LAMBDA and assignment — this MUST be the last line

### Indentation Guide

- Parameter declarations: 4 spaces indent
- `LET(Help, TEXTSPLIT(`: 4 spaces indent
- Help string lines: 16 spaces indent (aligned under the opening `"`)
- `"→", "¶"`: 16 spaces indent
- `),`: 16 spaces indent
- `//  Check inputs`: 4+4=8 spaces indent
- LET variables (`Help?,`, `result,`): 8 spaces indent
- `IF(Help?, Help, result)`: 8 spaces indent
- Closing `)`: 0 spaces
- `);`: 0 spaces

## Array-lifting Support

Whenever a new LAMBDA has **at least one scalar-shaped input and a scalar-shaped output**, design it to lift per-parameter — i.e. passing an array of values to the scalar param returns an element-wise array of results. Without lifting, callers who point a column at the lambda silently get a single mixed-up value (the body's aggregators collapse the array). This is a silent footgun, not an error, so it's easy to miss in review.

### When to apply

| Input shape | Output shape | Lift? |
|---|---|---|
| ≥1 scalar param | scalar | ✅ Yes — apply dispatcher |
| All array-shaped params (e.g. lookup tables, grids) | scalar or array | ➖ No — inputs are arrays by design |
| Any | inherently array (region/window extractors, generators) | ➖ No — output is already array |
| No real input (constant-table helpers) | array | ➖ No — nothing to lift |

If you're unsure, ask: "does the scalar param semantically take **one** value?" If yes, lift it.

### Dispatcher pattern

Keep the scalar body inside an inline `scalar` LAMBDA within the `LET`, then branch on whether the lift param is multi-cell:

```
//  Procedure
    scalar, LAMBDA(<paramName>,
              LET(... scalar body ...,
                  <scalar result>)),
    result, IF(OR(ROWS(<liftParam>) > 1, COLUMNS(<liftParam>) > 1),
                MAP(<liftParam>, scalar),
                scalar(<liftParam>)),
    IF(Help?, Help, result)
```

The dispatcher MUST sit **after** the `Help?, ISOMITTED(...)` line so `=YourLambda()` still returns help text. Scalar callers see no behavioural change.

### Picking the lift parameter

When multiple parameters could lift, default to lifting the **primary** scalar arg (the one most callers vary). Alternatives, only if the use case clearly demands it:

- **Cartesian product** — more powerful, more complex; needs justification.
- **Equal-shape zip** — when two arrays of the same shape should be paired element-wise.

Most LAMBDAs just lift the primary arg. Document the choice in the help text.

### Help text

Add an "Also accepts an array of <param>" note to the lifted parameter's description, e.g.:

```
"   n              →(Required) 1-based position into the flattened array; negative counts from the end (-1 = last). Also accepts an array of indices — result lifts element-wise.¶" &
```

### Tests

Add at least one array-input test alongside the scalar cases. Cover both orientations if both are realistic (`vertical array of …`, `horizontal array of …`). The harness asserts the spilled result with list-of-lists:

```yaml
  - name: vertical array of indices lifts
    args: ['={10,20,30,40}', '={1;3;-1}']
    expected: [[10], [30], [40]]

  - name: horizontal array of indices lifts
    args: ['={10,20,30,40}', '={1,2,-1}']
    expected: [[10, 20, 40]]
```

### Existing examples

The `maps/` library, `NTH`, `CHARQ`, `REVERSESTRING`, and `TLOOKUP` all use this pattern — read any of them for a worked example. (Some lambdas already lift naturally via Excel's calc engine — e.g. `CONTAINS` via `XMATCH`, `CIRCPOS` via `MOD`. Those have confirmation tests but no dispatcher.)

## Tests File Format

The `.tests.yaml` file provides test cases for the automated test harness.

### Template

```yaml
tests:
  - name: <descriptive test name>
    args: [<arg1>, <arg2>]
    expected: <expected_result>

  - name: <another test>
    args: [<arg1>, <arg2>]
    expected: <expected_result>

  - name: help text
    args: []
    expected_type: array
```

### Test Case Guidelines

- Include at least 3-4 functional test cases covering:
  - Normal/happy path
  - Edge cases (zero, empty, boundary values)
  - Negative or error-prone inputs where applicable
- Always include a `help text` test case with `args: []` and `expected_type: array`
- Use `expected` for exact value matching (scalars, strings, booleans)
- Use `expected_type: array` when exact matching isn't practical (e.g. Help output)
- Floating-point results: the harness uses `1e-10` tolerance, so provide precise expected values
- String args must be quoted: `args: ["hello", " "]`
- For 2D array results: `expected: [["row1col1", "row1col2"], ["row2col1"]]`

## Workflow

### Step 1 — Load the Idea

If an issue number was provided, fetch it:

```bash
gh issue view <number> -R TagloGit/lambda-boss
```

If no issue number was provided, list open `lambda-idea` issues and use AskUserQuestion to let the user pick one. Then fetch the selected issue.

If there are no `lambda-idea` issues, ask the user to describe the LAMBDA they want.

### Step 2 — Confirm Details with User

Before writing any files, extract the key details from the issue (or user description) and present them for confirmation using AskUserQuestion. The user must approve before you proceed.

Details to confirm:
- **Function name** (PascalCase, e.g. `WeightedScore`)
- **Category** (subdirectory under `lambdas/`, e.g. `math`, `string`)
- **Parameters** — name, required/optional, description for each
- **Formula approach** — brief description of the implementation
- **Example** — sample call and expected result
- **Array-lifting** — does the LAMBDA fit the "scalar input → scalar output" shape (see Array-lifting Support above)? If yes, plan the dispatcher and name the param that will lift.

If the issue is missing key details or the approach is ambiguous, ask the user to clarify rather than guessing.

### Step 3 — Create a Branch

Create a feature branch from main:

```bash
git checkout main
```

```bash
git pull
```

```bash
git checkout -b lambda-<name-lowercase>
```

If working from an issue, update its status:

```bash
gh issue edit <number> -R TagloGit/lambda-boss --remove-label "status: backlog" --add-label "status: in-progress"
```

### Step 4 — Create the Lambda File

1. Check if `lambdas/<category>/` exists; if not, create it with `_library.yaml`
2. Write the `.lambda` file following the template exactly
3. Use `\n` line endings — write with the Write tool (it preserves line endings)

### Step 5 — Create the Tests File

1. Write the `.tests.yaml` file with test cases
2. Include the help text test case

### Step 6 — Validate Format

Run the format validation tests to confirm the file is correctly formatted:

```bash
dotnet test addin/lambda-boss.Tests/lambda-boss.Tests.csproj --filter "FullyQualifiedName~LambdaFormatTests" --no-build
```

If this fails, first try building:

```bash
dotnet build addin/lambda-boss.slnx
```

Then re-run the test. If validation fails, read the error output, fix the `.lambda` file, and re-run until all format tests pass.

### Step 7 — Run Functional Tests

Excel is available in this environment. Run the test harness scoped to this LAMBDA only — the full suite is ~470 cases and takes much longer than necessary while iterating. Use `LAMBDA_FILTER` (case-insensitive substring match against the lambda file path) to narrow the run:

```powershell
$env:LAMBDA_FILTER = "<Name>"
dotnet test addin/lambda-boss.AddinTests/lambda-boss.AddinTests.csproj --filter "FullyQualifiedName~LambdaHarnessTests"
Remove-Item Env:LAMBDA_FILTER
```

If tests fail, read the output, fix the `.lambda` file or `.tests.yaml`, and re-run until all tests pass.

Before raising the PR (Step 8), run the full suite once **without** `LAMBDA_FILTER` to confirm the new LAMBDA hasn't broken anything else (e.g. a shared helper, a name collision, or a dependency-injection ordering issue):

```bash
dotnet test addin/lambda-boss.AddinTests/lambda-boss.AddinTests.csproj --filter "FullyQualifiedName~LambdaHarnessTests"
```

### Step 8 — Commit, Push, and Create PR

Commit the new files:

```bash
git add lambdas/<category>/<Name>.lambda lambdas/<category>/<Name>.tests.yaml
```

Include `_library.yaml` if a new category was created. Commit with a descriptive message.

Push and create a PR:

```bash
git push -u origin lambda-<name-lowercase>
```

Create the PR with `Closes #<number>` in the body (if working from an issue). Update the issue status:

```bash
gh issue edit <number> -R TagloGit/lambda-boss --remove-label "status: in-progress" --add-label "status: in-review"
```

### Step 9 — Report

Tell the user:
- What files were created
- That format validation and functional tests both passed
- Link to the PR

### Step 10 - Update this skill with lessons learned on LAMBDA syntax best, if any

- If the User gives feedback on the LAMBDA, or testing reveals LAMBDA patterns or Excel synatx that doesn't work, update the best practice section of this skill (below)

## Common Mistakes to Avoid

- **Carriage returns:** The Write tool on Windows may produce `\r\n`. After writing, verify with format tests. If CR errors occur, re-write the file content ensuring `\n`-only line endings.
- **Missing trailing comma:** Every LET variable line (including `Help?` and `result`) must end with a comma.
- **Help string alignment:** Keep the `→` and `¶` delimiters consistent. The last Help string line must NOT end with `¶" &` — it ends with just `",`.
- **Parameter brackets:** ALL parameters must be `[param]`, not `param`.
- **Tab characters:** Never use tabs. Use spaces for all indentation.

## LAMBDA syntax best practice

- **XLOOKUP approximate match (match_mode 1) fails on unsorted data.** `XLOOKUP(sentinel, values, labels, , 1)` (next larger) returns #N/A when the lookup_array is not sorted. Use `INDEX(labels, MATCH(MIN(values), values, 0))` instead for finding labels of minimum values. Match_mode -1 (next smaller) works fine unsorted for finding max labels.
- **Test YAML: array constants need `=` prefix.** Excel array constants like `{1,2,3}` conflict with YAML syntax. Wrap them as strings with `=` prefix: `"={1,2,3}"`. The harness strips the `=` and passes the raw formula fragment.
- **Test YAML: string array constants need single-quote wrapping.** For string arrays like `{"A","B"}`, use YAML single quotes to avoid double-quote escaping issues: `'={"A","B","C"}'`.
- **Test YAML: boolean expected values must be capitalized.** Use `True`/`False` (not `true`/`false`) because YamlDotNet deserializes them as strings when the target type is `object`, and Excel COM returns `True`/`False`.
- **Test YAML: numeric args need `=` prefix to avoid quoting.** The harness wraps string args in quotes via `FormatArg`. A YAML integer like `42` gets deserialized as a string and quoted as `"42"`. Use `"=42"` to pass the raw number to Excel
- **Test YAML: boolean args need `=` prefix too.** YAML `True`/`False` are deserialized as strings and `FormatArg` wraps them in quotes (e.g. `"True"`), which Excel treats as text, not a boolean. Use `"=TRUE"` / `"=FALSE"` so the harness strips the `=` and passes the raw Excel boolean. Note: this applies to *args* only — *expected* values should use capitalized `True`/`False` without `=` prefix.
- **SCAN/REDUCE defaults: pass built-in functions by name, not wrapped in a LAMBDA.** Excel's higher-order functions (`SCAN`, `REDUCE`, `MAP`, etc.) accept a built-in function by name as the reducer — e.g. `SCAN(0, arr, SUM)` works because `SUM(acc, current)` is a valid 2-arg call. Don't wrap it as `LAMBDA(a, b, a + b)`; just pass `SUM` (or `PRODUCT`, `MAX`, `MIN`, etc.). Keeps default-function patterns like `IF(ISOMITTED(function), SUM, function)` concise.
- **Escape quotes in Help strings with `""`, not `\"`.** Excel string literals escape an embedded quote by doubling it. Using backslash-escape (JSON/C style) like `"=MOVECELL(E5, \"↖\")"` makes the formula fail to inject at runtime with the opaque "There's a problem with this formula" error. Use `"=MOVECELL(E5, ""↖"")"` instead.
- **Lambdas can reference each other; the harness pre-injects in dependency order.** A lambda can call any other lambda in the library (e.g. `MOVECELL` calls `ARROWMOVES`). The AddIn test harness now bulk-injects all `.lambda` files with retries until dependencies resolve, so ordering across files doesn't matter.
- **Test harness wraps each test in `=LAMBDA_NAME(args)` — you cannot wrap the call in another function via args.** Args are passed as comma-separated params inside the lambda call, not as outer expressions. Test `=INDEX(DEFAULTMOVEDELTAS(), 1, 1)` style DOES NOT WORK — the harness would produce `=DEFAULTMOVEDELTAS(INDEX(DEFAULTMOVEDELTAS(), 1, 1))`. For array-returning lambdas, assert the full spill with `expected: [[...], [...]]` (list-of-lists for 2D) or use `expected_type: array` for loose checks.
- **Zero-arg data-getter lambdas: invert the Help IF with `IF(Help?, result, Help)`.** For lambdas where the natural call takes no arguments (e.g. returning a constant table), keep the standard `Help?, ISOMITTED(ShowHelp)` LET variable but swap the branches at the end: `IF(Help?, result, Help)`. This makes bare calls like `=DEFAULTARROWS()` return the data, and explicit `=DEFAULTARROWS(TRUE)` returns help. Tests should pass `args: []` for the data case and `args: ["=TRUE"]` for the help case.
- **`ROW()`/`COLUMN()` on array constants error; wrap with `IFERROR`.** If a lambda takes a range and computes `MIN(ROW(grid))`/`MIN(COLUMN(grid))` to find its base cell, passing an array constant like `SEQUENCE(10,10)` (common in tests, since the harness can't pass real ranges) makes `ROW`/`COLUMN` return `#VALUE!`, which propagates through the whole formula. Wrap as `IFERROR(MIN(ROW(grid)), 1)` / `IFERROR(MIN(COLUMN(grid)), 1)` so array-constant inputs fall back to base (1,1) — i.e. addresses relative to A1. Real-range inputs still get their true base.
- **XMATCH is case-insensitive by default.** `XMATCH("e", {"N";"n";"E";"e"})` returns 3 (matches "E"), not 4. Use distinct lookup keys in move/direction tables, or use `EXACT`-based lookup if case sensitivity is required.
- **REGEXEXTRACT with return_mode 1 returns a horizontal (1xN) array, and collapses to a scalar on single match.** Wrap with `TRANSPOSE(...)` for a vertical spill. When only one match is possible (e.g. `CONSECGROUPS("ZZZZZ")`), Excel returns a scalar — `TRANSPOSE` of a scalar is still a scalar. In tests, assert single-match cases with `expected: "value"` (not list-of-lists), because the harness's array path (`cell.SpillingToRange`) returns null on scalars and throws NullReferenceException at `Marshal.ReleaseComObject`.
- **GROUPBY needs a column-vector `row_fields`; normalise row-array inputs with `IF(ROWS(arr)=1, TRANSPOSE(arr), arr)`.** A row array like `{"A","B","B"}` passed straight into GROUPBY silently misgroups (e.g. sorts by label rather than count). Transpose to a column first so the function groups one value per row as intended.
- **GROUPBY errors on a single-row input; skip one-element test cases.** `GROUPBY({"X"}, {"X"}, ROWS,, 0, -2)` returns `#CALC!` / HRESULT `-2146826273` in Excel even after normalising to a column. Drop the single-element test case — it's not a realistic use of a frequency lambda, and there's no clean workaround without branching on `ROWS(arr)=1`.
- **SEQUENCE orientation matters for INDEX column indexing.** `INDEX(array, , SEQUENCE(n,,n,-1))` produces an nx1 vertical result even when the input array is 1xn horizontal, because `SEQUENCE(n,,n,-1)` is vertical. Use `SEQUENCE(1,n,n,-1)` (horizontal) to preserve the original 1xn orientation.
- **`INDEX(2D_array, , col_vector)` collapses to one row — supply explicit row indices to broadcast.** Omitting `row_num` against a 2D array triggers Excel's implicit-intersection legacy behaviour and INDEX returns just the first row, even with a horizontal SEQUENCE for `col_num`. To produce a full n×m result, pass both index vectors explicitly: `INDEX(array, SEQUENCE(n), SEQUENCE(1,m,m,-1))` — INDEX then broadcasts (column-vector × row-vector) into the full grid. Same applies in reverse for omitted `column_num`. Always test 2D inputs separately from 1D inputs in the `.tests.yaml`; a passing 1D case doesn't prove the 2D path works.
- **Native broadcasting can replace a `MAP` dispatcher — and lifts on *two* params for free.** The Array-lifting section's dispatcher (`IF(OR(ROWS>1,COLUMNS>1), MAP(...), scalar(...))`) is only needed when the scalar body contains an *aggregating* function that would collapse an array (e.g. `SUMPRODUCT`, `TEXTJOIN`). If the body is built purely from functions that already lift element-wise — arithmetic, `INT`, `MOD`, `ADDRESS`, `IF`, and lambdas that themselves lift (like `CELLTOPOS`) — drop the dispatcher and let Excel broadcast. The payoff: when one param is `m×1` and another is `1×n`, Excel's `+`/`*` broadcast them to an `m×n` grid automatically, so the lambda lifts on both inputs at once with zero extra code. MOVECELL uses this — `ADDRESS(curRow + dR*steps, curCol + dC*steps, 4)` lifts on cell (column) and steps (row) simultaneously, yielding a cell×steps grid; a scalar cell + `SEQUENCE(n)` traces a path. Verify `ADDRESS` and friends lift in the harness (they do) before relying on it.
- **Avoid LET variable names that look like R1C1 references.** Names ending in `R1`/`C1` (e.g. `gR1`, `gC1`), or short forms like `cR`, `cC`, `topR`, `botR`, `leftC`, `rightC` are rejected by Excel when injecting the lambda via Names.Add — the harness reports "Could not resolve lambda dependencies". Single-letter names `r` / `c` are also risky because they resemble R1C1 row/column references. **Letter-then-digit names like `s1`, `n1`, `v1`, `r1`, `c1` (and the LAMBDA *parameter* equivalents) collide with cell references too** — Excel rejects the formula. For numbered series, use underscores: `s_1`, `n_1`, `v_1`. Use descriptive names like `baseRow`, `endCol`, `ctrRow`, `topRow`, `leftCol`, `winR`, `winC` when meaning is clearer than position.
- **Some plain-English function names are reserved by Excel and can't be a defined name — prefix with `_` to escape.** A lambda named `NEXT` fails to inject: setting `=NEXT(...)` (even the bare `=NEXT()` help call) throws COMException `0x800A03EC`, the same generic "problem with this formula" Excel raises for a syntactically rejected name. The body is fine — an identical lambda renamed `NEXTITEM` passes every case — it's purely the token. Confirmed reserved so far: `NEXT` (legacy FOR…NEXT keyword). The fix Tim prefers is a **leading underscore** (`_NEXT`): underscore-prefixed names are legal defined names, inject cleanly, and still surface in Excel's IntelliSense, so the function stays discoverable. Keep the filename, the `FUNCTION NAME:` header, the `<Name> = LAMBDA(` assignment, and every help-string self-reference all on the underscore form (`_NEXT = LAMBDA(`, `=_NEXT(...)`). If a new lambda's natural name is a bare English keyword and all tests fail with `0x800A03EC` regardless of body, suspect a reserved name and try the `_`-prefix before renaming.
- **`INDEX(arr, SEQUENCE(...), SEQUENCE(...))` and `OFFSET` are both testable; pick based on required semantics.** Historically the harness could only pass array constants (e.g. `SEQUENCE(10,10)`), which made `OFFSET` untestable — so lambdas were pushed toward `INDEX` with SEQUENCE index arrays. As of issue #85 the harness supports a `setup:` block that pre-populates cells and/or named ranges before the formula runs, so `OFFSET`-based lambdas (and any lambda that must return or consume a *range reference* rather than a spilled array) can now be exercised in tests. Choose `OFFSET` when the caller needs a true range (for data validation sources, conditional-formatting ranges, `SUBTOTAL`, further `OFFSET`, etc.); choose `INDEX` with SEQUENCE when a spilled 2D array is sufficient. Either way, keep the `IFERROR(MIN(ROW(grid)),1)` base-cell fallback so positional math stays correct for both real-range and array-constant inputs.
- **Test YAML: `setup:` block pre-populates the sheet before the formula runs.** Each test runs on a fresh worksheet. An optional `setup:` block can populate cells and/or define worksheet-scoped names used by the formula args. Shape:
    ```yaml
    - name: real range input via named range
      setup:
        cells:
          - address: "C5:G9"
            values:
              - [1, 2, 3, 4, 5]
              - [6, 7, 8, 9, 10]
              # ...
        names:
          - { name: "testGrid", refers_to: "C5:G9" }
      args: ["=testGrid", "=E7", "=3", "=3"]
      expected: [[7, 8, 9], [12, 13, 14], [17, 18, 19]]
    ```
  - `cells[].values` is a 2D list (`[[...]]`) whose shape matches `address`. Scalars are also accepted for single-cell addresses.
  - `names[].refers_to`: a bare address (e.g. `C5:G9`) is auto-qualified with the current test sheet name. To write a full formula, prefix with `=`; use `{sheet}` as a placeholder for the sheet name if needed (e.g. `={sheet}!C5:G9`).
  - Names are sheet-scoped — they live and die with the fresh worksheet, so no cleanup is required.
- **Test YAML: `\xNN` hex escapes work in double-quoted strings.** YamlDotNet honours YAML hex escapes inside double-quoted scalars, so you can express ASCII control characters directly: `expected: "x\x1Es5"` decodes to `x` + CHAR(30) + `s` + `5`. Useful when a lambda's return value embeds delimiters like CHAR(29/30/31). Doesn't work in single-quoted YAML strings (single-quote rules treat backslashes literally).
- **For combinatorial generation, extend the result matrix via broadcast + TOCOL, not per-rank REDUCE.** Per-rank generators (`MAP(SEQUENCE(total), LAMBDA(rank, unrank(rank)))` or `REDUCE(...VSTACK...)`) are correct but scale terribly because Excel re-enters the LAMBDA body for every row — `COMBININDICES(170, 3)` on the per-rank path doesn't finish at all, while a broadcast version returns 800k rows in well under a second. Build the result iteratively with one REDUCE step per output column: at step `kPrev → kPrev+1`, compute `mask = SEQUENCE(1,n) > INDEX(prev, , kPrev)` (an `m × n` boolean), then for each existing column `c` produce `TOCOL(IF(mask, INDEX(prev,,c), NA()), 2)` and HSTACK them with `TOCOL(IF(mask, SEQUENCE(1,n), NA()), 2)` for the new column. Stay numeric the whole way — string-encoded round-trips via `TEXTJOIN`/`TEXTSPLIT` add cost and (in nested MAP contexts) sometimes return a scalar error for multi-row outputs.
- **Test harness runs on an unsaved workbook — `CELL("filename", ...)` returns empty string.** Tricks like `MID(CELL("filename",A1), FIND("]",CELL("filename",A1))+1, 31)` to discover the current sheet name evaluate to `#VALUE!` under the harness because the workbook has never been saved. There is no clean in-formula way to recover the test sheet name, and `setup.cells` only writes to the test sheet — so cross-sheet behavior driven by a sheet-name string argument is currently untestable. Cover the same-sheet branch with `setup.cells` and skip the cross-sheet test case (or note it as a known gap).
- **Test YAML: Excel error values come back as COM HRESULT integers, not error strings.** When a lambda returns `NA()`, `cell.Value` is the integer `-2146826246` (the COM Variant error code for `xlErrNA`), not the string `"#N/A"`. Assert with `expected: -2146826246` not `expected: "#N/A"`. Other Excel errors map to: `#DIV/0!` = `-2146826281`, `#VALUE!` = `-2146826273`, `#REF!` = `-2146826265`, `#NAME?` = `-2146826259`, `#NUM!` = `-2146826252`, `#NULL!` = `-2146826288`. (Sources for these constants: Excel COM `xlErr*` values offset from `0x800A07D0`.)
- **Test YAML: pass LAMBDA-typed args with `=` prefix and YAML single quotes.** Higher-order lambdas (FINDFIRST, REDUCEUNTIL, etc.) take other LAMBDAs as args. Write them inline: `'=LAMBDA(cur, i, IF(cur="b", "found-" & i, ""))'`. Single-quoted YAML preserves embedded double-quotes literally; the leading `=` tells `FormatArg` to pass the raw expression instead of wrapping it in quotes. Use unambiguous LAMBDA parameter names — `cur` rather than `c`, `iter` rather than just `i` if you're worried about R1C1 confusion (single-letter `i` is fine; `r` and `c` are not).
- **State-string lambdas (Loops library): assert on the raw encoded string when you can't wrap the call.** The harness wraps each test in `=LAMBDA_NAME(args)` — you can't post-process the result with `GetVar` from outside. For a lambda that returns a state string, assert the literal `expected: "<meta>\x1Cuser\x1Eb\x1ETRUE"` — the YAML `\xNN` escapes line up with US/RS/GS/FS = `\x1F` / `\x1E` / `\x1D` / `\x1C`. Tedious but verifiable, and you get exact-encoding regression coverage as a bonus.
- **Test YAML: keep `setup.cells` clear of A1 — the harness writes the formula to A1.** `LambdaHarnessTests.LambdaTest` always sets `cellFormula` on `ws.Range["A1"]`. If your `setup.cells` populates A1 (or a range that includes A1), the formula overwrites that cell, creating a circular reference for any named range pointing at it — symptoms include the formula silently returning `0` instead of the expected value or a clean error. Place test data starting at C3 (or anywhere that excludes A1) and point `names` at the offset addresses. Same goes for any layout where the formula at A1 would consume part of the data range.
- **Test YAML: also keep `setup.cells` clear of the lambda's *spill* zone from A1.** For lambdas that return an N×M array sized to an input range (full distance grids, broadcast outputs, etc.), the spill from A1 lands at `A1:<col M><row N>`. If your `setup.cells` placed the input map at e.g. C3:E5 (a common "small grid" position), a 3×3 spill from A1 lands at A1:C3 and collides with the seeded value at C3 — Excel returns `#SPILL!`, A1 has no `SpillingToRange`, and `AssertArray` throws `NullReferenceException` at `Marshal.ReleaseComObject(spillRange)` (same symptom as a scalar return). Place the input map outside the spill rectangle — for a 3×3 output, G3:I5 (col 7+) is safe; in general, start your data at column `output_cols + 2` and row `output_rows + 2`, or further. Single-target / scalar-output cases are unaffected.
- **`ISTEXT`/`ISNUMBER`/`ISBLANK` (and other predicates) lift over a multi-cell range — guard branch-selecting tests.** A param that may be either a scalar or a range can't be branched on with `IF(ISTEXT(param), …)`: when `param` is a multi-cell range, `ISTEXT(param)` returns a 2-D boolean *array*, so the `IF` lifts element-wise and the "scalar" branch result becomes array-shaped — silently corrupting everything downstream (e.g. an `origin` row/col that should be one number becomes a grid). Two fixes: (1) if you only need the range's position/top-left, skip the predicate entirely and use an aggregator — `MIN(ROW(param))` / `MIN(COLUMN(param))` collapse a single cell *or* a whole range to a scalar top-left with no lift; (2) if you genuinely must discriminate type, reduce to one cell first (`ISTEXT(INDEX(param,1,1))`) — but beware that tests the *content* type, which is fragile (a range whose top-left holds text). Prefer (1). This is why anchor-to-geometry params (SUBGRID/REPLACEARRAY `origin`) take a reference only, decoded via `MIN(ROW)`/`MIN(COLUMN)`, rather than a polymorphic text-or-range.