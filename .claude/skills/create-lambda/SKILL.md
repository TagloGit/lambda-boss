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

Excel is available in this environment. Run the test harness to verify the LAMBDA works end-to-end:

```bash
dotnet test addin/lambda-boss.AddinTests/lambda-boss.AddinTests.csproj --filter "LambdaHarnessTests"
```

If tests fail, read the output, fix the `.lambda` file or `.tests.yaml`, and re-run until all tests pass.

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
- **`@` (implicit intersection) inside a LAMBDA body is unreliable; use `INDEX(..., 1, 1)` instead.** `@GROUPBY(arr, arr, ROWS,, 0, -2)` as a LET expression returns `#CALC!` / HRESULT `-2146826273` at runtime even though format tests accept it. Rewrite as `INDEX(GROUPBY(arr, arr, ROWS,, 0, -2), 1, 1)` to reliably grab the top-left scalar from an array-returning function.
- **GROUPBY needs a column-vector `row_fields`; normalise row-array inputs with `IF(ROWS(arr)=1, TRANSPOSE(arr), arr)`.** A row array like `{"A","B","B"}` passed straight into GROUPBY silently misgroups (e.g. sorts by label rather than count). Transpose to a column first so the function groups one value per row as intended.
- **GROUPBY errors on a single-row input; skip one-element test cases.** `GROUPBY({"X"}, {"X"}, ROWS,, 0, -2)` returns `#CALC!` / HRESULT `-2146826273` in Excel even after normalising to a column. Drop the single-element test case — it's not a realistic use of a frequency lambda, and there's no clean workaround without branching on `ROWS(arr)=1`.
- **SEQUENCE orientation matters for INDEX column indexing.** `INDEX(array, , SEQUENCE(n,,n,-1))` produces an nx1 vertical result even when the input array is 1xn horizontal, because `SEQUENCE(n,,n,-1)` is vertical. Use `SEQUENCE(1,n,n,-1)` (horizontal) to preserve the original 1xn orientation.
- **Avoid LET variable names that look like R1C1 references.** Names ending in `R1`/`C1` (e.g. `gR1`, `gC1`), or short forms like `cR`, `cC`, `topR`, `botR`, `leftC`, `rightC` are rejected by Excel when injecting the lambda via Names.Add — the harness reports "Could not resolve lambda dependencies". Single-letter names `r` / `c` are also risky because they resemble R1C1 row/column references. Use descriptive names like `baseRow`, `endCol`, `ctrRow`, `topRow`, `leftCol`, `winR`, `winC` instead.