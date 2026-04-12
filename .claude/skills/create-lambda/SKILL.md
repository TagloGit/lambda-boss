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

### Step 3 — Create the Lambda File

1. Check if `lambdas/<category>/` exists; if not, create it with `_library.yaml`
2. Write the `.lambda` file following the template exactly
3. Use `\n` line endings — write with the Write tool (it preserves line endings)

### Step 4 — Create the Tests File

1. Write the `.tests.yaml` file with test cases
2. Include the help text test case

### Step 5 — Validate Format

Run the format validation tests to confirm the file is correctly formatted:

```bash
dotnet test addin/lambda-boss.Tests/lambda-boss.Tests.csproj --filter "FullyQualifiedName~LambdaFormatTests" --no-build
```

If this fails, first try building:

```bash
dotnet build addin/lambda-boss.slnx
```

Then re-run the test. If validation fails, read the error output, fix the `.lambda` file, and re-run until all format tests pass.

### Step 6 — Run Functional Tests

Excel is available in this environment. Run the test harness to verify the LAMBDA works end-to-end:

```bash
dotnet test addin/lambda-boss.AddinTests/lambda-boss.AddinTests.csproj --filter "LambdaHarnessTests"
```

If tests fail, read the output, fix the `.lambda` file or `.tests.yaml`, and re-run until all tests pass.

### Step 7 — Report

Tell the user:
- What files were created
- That format validation and functional tests both passed

## Common Mistakes to Avoid

- **Carriage returns:** The Write tool on Windows may produce `\r\n`. After writing, verify with format tests. If CR errors occur, re-write the file content ensuring `\n`-only line endings.
- **Missing trailing comma:** Every LET variable line (including `Help?` and `result`) must end with a comma.
- **Help string alignment:** Keep the `→` and `¶` delimiters consistent. The last Help string line must NOT end with `¶" &` — it ends with just `",`.
- **Parameter brackets:** ALL parameters must be `[param]`, not `param`.
- **Tab characters:** Never use tabs. Use spaces for all indentation.
