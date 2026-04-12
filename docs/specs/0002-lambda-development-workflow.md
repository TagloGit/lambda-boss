# 0002 — LAMBDA Development Workflow

## Problem

Lambda Boss v1 delivers a complete retrieval and deployment experience, but the *authoring* side of the LAMBDA lifecycle is manual and disconnected. Specific pain points:

1. **Idea capture is ad hoc.** LAMBDA ideas arise while working in Excel, but there's no structured path from "I need a function that does X" to a tracked backlog item. The user has to context-switch out of Excel to create a GitHub Issue.

2. **No automated testing.** Claude Code can write a `.lambda` file, but verifying it works requires the user to manually load it in Excel and eyeball the results. There's no programmatic feedback loop — Claude can't iterate autonomously.

3. **Testing requires merge.** LAMBDAs are only available in Lambda Boss after they're merged to `main` on GitHub. There's no way to load in-development LAMBDAs from a local branch for manual testing and feedback before merge.

4. **Format compliance is manual.** The self-documenting `.lambda` format (Help pattern, header comments, no tabs/CRs) has no automated enforcement. Malformed files only surface when something breaks at runtime.

5. **Help? pattern is broken.** The `.lambda` file format uses `Help?` as if it were valid Excel syntax, but it isn't. The `LambdaParser.TransformHelpPattern` method papers over this by rewriting `Help?` → `ISOMITTED(first_param)` and wrapping all parameters in `[]` at load time. This was based on a misreading of a reference LAMBDA — the original format defines `Help?` as a LET variable in a "Check inputs" section (e.g. `Help?, ISOMITTED(Dimensions)`). The `.lambda` files should be valid Excel as written, with no load-time transformation needed.

## Proposed Solution

A five-part system that creates an end-to-end LAMBDA development workflow:

1. **Fix Help? pattern** — Update the `.lambda` file format so that `Help?` is a proper LET variable, remove `TransformHelpPattern` from the parser, and update all existing LAMBDAs.
2. **Idea capture via Notion bridge** — Claude for Excel creates structured idea pages in Notion; Claude Code syncs them to GitHub Issues.
3. **Local directory sources** — Lambda Boss can load LAMBDAs from local filesystem paths alongside GitHub sources, enabling pre-merge testing.
4. **Automated test harness** — A test runner that injects LAMBDAs into Excel and validates them against `.tests.yaml` test cases, runnable by Claude Code via `dotnet test`.
5. **Format validation** — A unit test that checks all `.lambda` files conform to the required format, runnable in CI without Excel.

## Detailed Design

### 0. Fix Help? Pattern

**Current (broken):** `.lambda` files use `Help?` as pseudo-syntax. `LambdaParser.TransformHelpPattern` rewrites this at load time by:
1. Wrapping all LAMBDA parameters in `[]` to make them optional
2. Replacing `Help?` with `ISOMITTED(first_param)`

**New (correct):** `.lambda` files are valid Excel as written. The LAMBDA format includes a "Check inputs" section in the LET body that defines `Help?` as a variable:

```
Clamp = LAMBDA(
//  Parameter Declarations
    [value],
    [min_val],
    [max_val],
//  Help
    LET(Help, TEXTSPLIT(
                "FUNCTION:      →Clamp(value, min_val, max_val)¶" &
                ...
                "→", "¶"
                ),
    //  Check inputs
        Help?, ISOMITTED(value),
    //  Procedure
        result, MIN(MAX(value, min_val), max_val),
        IF(Help?, Help, result)
)
);
```

Key changes:
- All LAMBDA parameters are wrapped in `[]` in the file itself (since any LAMBDA with Help support must accept zero arguments)
- `Help?` is defined as a LET variable: `Help?, ISOMITTED(first_required_param)`
- Additional input checks can live in the same section (e.g. `Delimit?, NOT(ISOMITTED(Delimiter))`)
- `LambdaParser.TransformHelpPattern` is removed entirely — the parser just extracts the formula as-is
- All existing `.lambda` files are updated to the new format

This must be done first as it changes the canonical `.lambda` format that all subsequent work (format validation, test harness, create-lambda skill) depends on.

### 1. Idea Capture — Notion Bridge

**Claude for Excel → Notion:**

A Claude for Excel skill (`create-LAMBDA-idea`) that:
- Examines the current sheet context to understand the problem
- Creates a child page under the "LAMBDA ideas" Notion page (ID: `3407b3d23d2f80aa9ac4e4903868d7c8`)
- Uses a recommended template (flexible based on context):
  - Problem
  - Proposed LAMBDA (formula sketch)
  - Parameters (table)
  - Returns
  - Example (with sample data)
  - Notes

**Claude Code → GitHub:**

A Claude Code skill (`manage-ideas`) that:
- Reads child pages under the "LAMBDA ideas" Notion page
- For each page not yet synced: creates a GitHub Issue on `TagloGit/lambda-boss` with `enhancement` + `lambda-idea` + `status: backlog` labels
- Moves the synced Notion page to a "Synced to GitHub" child page under "LAMBDA ideas" for audit trail
- Idempotent — safe to run repeatedly

**GitHub label:** A new `lambda-idea` label on the lambda-boss repo for filtering LAMBDA idea issues.

### 2. Local Directory Sources

Lambda Boss already supports GitHub repo sources with enable/disable and reload. Local directory sources are simply another source type:

- **Settings:** A new source type in `settings.json`:
  ```json
  {
    "sources": [
      { "type": "github", "url": "https://github.com/TagloGit/lambda-boss", "enabled": true },
      { "type": "local", "path": "C:\\Users\\trjac\\repositories\\lambda-boss\\lambdas", "enabled": true }
    ]
  }
  ```
- **Loading:** Local sources read `.lambda` files and `_library.yaml` directly from disk. No caching — always reads fresh on load/reload.
- **UI:** Local sources appear in the Lambda Boss popup alongside GitHub sources, visually distinguished (e.g. folder icon vs GitHub icon). Display name is the folder name. Same load/reload UX.
- **Update/reload:** Reload re-reads from disk. Instant, no network fetch.

### 3. Automated Test Harness

**Test case format:** Each LAMBDA can have a companion `.tests.yaml` file:

```
lambdas/
  string/
    Split.lambda
    Split.tests.yaml
    PadLeft.lambda
    PadLeft.tests.yaml
```

`.tests.yaml` structure:

```yaml
tests:
  - name: basic split
    args: ["Hello World", " "]
    expected: [["Hello", "World"]]

  - name: split with ignore empty
    args: ["a,,b", ",", true]
    expected: [["a", "b"]]

  - name: help text
    args: []
    expected_type: array  # Just check it returns a 2D array (the Help table)

  - name: single delimiter
    args: ["no-delim", ","]
    expected: [["no-delim"]]
```

Test value types:
- Scalars: numbers, strings, booleans
- 2D arrays: nested arrays `[[row1col1, row1col2], [row2col1]]` (Excel's native return format)
- `expected_type`: for cases where exact value matching isn't practical (e.g. Help output), assert on the return type/shape instead
- Floating-point tolerance: `1e-10` for numeric comparisons (within Excel's 15-digit precision)

**Test runner:** A new test class `LambdaHarnessTests` in the existing `lambda-boss.AddinTests` project:
- Uses the shared `ExcelAddinFixture` (no extra Excel startup cost)
- Discovers all `.tests.yaml` files in the `lambdas/` directory
- For each test file: parses the companion `.lambda` file, injects it into the Name Manager, evaluates each test case by writing a formula to a cell and reading the result, asserts against expected values
- xUnit `[Theory]` with `[MemberData]` for individual test case reporting
- Claude Code runs: `dotnet test addin/lambda-boss.AddinTests --filter LambdaHarnessTests`

**Test lifecycle per LAMBDA:**
1. Parse `.lambda` file → extract name + formula
2. Inject into Name Manager via `Workbook.Names.Add`
3. For each test case: write `=Name(args...)` to a cell, wait for calc, read result
4. Assert result matches expected value (with tolerance for floating point)
5. Clean up Name Manager entry

### 4. Format Validation

**Unit test (CI):** A test class `LambdaFormatTests` in `lambda-boss.Tests` that:
- Scans all `.lambda` files in the `lambdas/` directory
- Validates each file against the required format:
  - Filename matches the LAMBDA name (e.g. `Split.lambda` defines `Split`)
  - Contains the header comment block (`FUNCTION NAME`, `DESCRIPTION`, `REVISIONS`)
  - Contains the Help `TEXTSPLIT("→","¶")` pattern
  - Contains `Help?` defined as a LET variable (not used as bare pseudo-syntax)
  - No tab characters
  - No carriage return characters (line endings must be `\n` not `\r\n`)
  - Ends with `);` (properly terminated)
  - Name assignment: first non-comment line matches `Name = LAMBDA(`
- Runs as part of `dotnet test addin/lambda-boss.Tests` — no Excel required

**Claude Code skill (`create-lambda`):** A skill that guides Claude through LAMBDA authoring:
- Enforces the self-documenting format template (with correct Help? LET variable pattern)
- Generates the Help section from parameter metadata
- Creates the `.tests.yaml` companion file with initial test cases
- Validates format before committing

## User Stories

### Idea Capture

- As a user working in Excel, I want to describe a LAMBDA idea to Claude for Excel so that it's captured as a structured Notion page without leaving my spreadsheet.
- As a developer using Claude Code, I want to sync LAMBDA ideas from Notion to GitHub Issues so that all ideas are tracked in a single backlog with consistent labels and format.

### Local Directory Sources

- As a user, I want to add a local directory as a LAMBDA source in Lambda Boss settings so that I can load and test LAMBDAs from a development branch before they're merged.
- As a user, I want to reload a local library in Lambda Boss so that I see the latest changes from disk after Claude Code commits an update to the branch.

### Automated Test Harness

- As Claude Code developing a LAMBDA, I want to run `dotnet test --filter LambdaHarnessTests` and get pass/fail results so that I can iterate on the formula without human intervention.
- As a user, I want test cases defined alongside each LAMBDA so that they serve as both automated tests and usage documentation.

### Format Validation

- As a CI pipeline, I want to reject PRs that contain malformed `.lambda` files so that format compliance is enforced automatically.
- As Claude Code authoring a LAMBDA, I want a skill that guides me through the correct format so that I get it right the first time.

## End-to-End Workflow

The complete LAMBDA development lifecycle with all pieces in place:

1. **Idea capture:** User is working in Excel → tells Claude for Excel "I need a function that does X" → Claude for Excel creates a structured idea page in Notion.

2. **Backlog sync:** User (or scheduled task) runs `manage-ideas` in Claude Code → idea synced to GitHub Issue with `lambda-idea` + `status: backlog` labels.

3. **Development:** Claude Code picks up the issue → uses `create-lambda` skill → creates `.lambda` file + `.tests.yaml` on a feature branch → runs format validation (`dotnet test lambda-boss.Tests --filter LambdaFormatTests`) → runs functional tests (`dotnet test lambda-boss.AddinTests --filter LambdaHarnessTests`).

4. **Claude iteration:** If tests fail, Claude Code reads the output, adjusts the formula, and re-runs. Repeats until all tests pass.

5. **User testing:** User checks out the feature branch (or has Lambda Boss pointed at the local repo). Lambda Boss loads the in-development LAMBDA from the local directory source. User tests in Excel, provides feedback to Claude Code.

6. **PR and merge:** Claude Code raises a PR. CI runs format validation. Tim reviews, tests, merges.

## Acceptance Criteria

### Help? Pattern Fix
- [ ] All `.lambda` files updated: parameters wrapped in `[]`, `Help?` defined as LET variable
- [ ] `LambdaParser.TransformHelpPattern` removed
- [ ] Parser passes through formulas without transformation
- [ ] All existing tests updated and passing
- [ ] AddIn tests confirm LAMBDAs still work in Excel after the format change

### Idea Capture
- [ ] `lambda-idea` label exists on `TagloGit/lambda-boss`
- [ ] Claude for Excel skill creates structured idea pages under the Notion "LAMBDA ideas" page
- [ ] Claude Code `manage-ideas` skill syncs Notion ideas to GitHub Issues with correct labels
- [ ] Synced ideas are moved to "Synced to GitHub" sub-page in Notion

### Local Directory Sources
- [ ] Lambda Boss settings support local directory sources alongside GitHub sources
- [ ] Local libraries appear in the popup and can be loaded/reloaded
- [ ] Reload reads fresh from disk (no caching)
- [ ] Local sources display the folder name in the UI

### Automated Test Harness
- [ ] `.tests.yaml` format is defined and documented
- [ ] `LambdaHarnessTests` discovers and runs all `.tests.yaml` test cases in Excel
- [ ] Tests can be run standalone via `dotnet test --filter LambdaHarnessTests`
- [ ] Scalar, 2D array, and type-check assertions are supported
- [ ] Floating-point comparisons use `1e-10` tolerance
- [ ] Claude Code can run tests and iterate on failures autonomously

### Format Validation
- [ ] `LambdaFormatTests` validates all `.lambda` files without Excel
- [ ] Validates: name match, header comments, TEXTSPLIT Help pattern, Help? as LET variable, no tabs/CRs, proper termination
- [ ] Runs in CI as part of the unit test suite
- [ ] `create-lambda` skill produces correctly formatted files with test cases

## Out of Scope

- **Claude for Excel skill implementation** — this spec defines the interface (what the skill should do and what Notion structure it targets) but the skill itself is authored in the Claude for Excel environment, not in this repo.
- **Scheduled/automatic idea sync** — `manage-ideas` is run manually for now. Automation (e.g. cron trigger) can be added later.
- **Cross-library dependencies** — remains a v3 goal.
- **Private repo support** — remains a v3 goal.
- **Named pipe / real-time test endpoint** — the `dotnet test` approach is sufficient for v2. A faster interactive endpoint can be revisited if the test cycle proves too slow.
