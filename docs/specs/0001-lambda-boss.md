# 0001 — Lambda Boss

## Overview

Lambda Boss is an Excel add-in and supporting infrastructure for building, storing, and rapidly deploying a personal library of reusable Excel LAMBDA functions during competitive Excel events.

The project has three concerns housed in a single repository:

1. **LAMBDA storage** — A structured, version-controlled library of LAMBDA definitions organized into thematic collections (libraries).
2. **Retrieval add-in** — An ExcelDNA-based Excel add-in that fetches LAMBDAs from GitHub repos and injects them into the active workbook's Name Manager.
3. **Design and test infrastructure** — Tooling to support Claude-assisted LAMBDA development and automated regression testing (v2).

## Problem Statement

Competitive Excel events require rapid access to reusable formulas. Participants need to:

- Maintain a library of battle-tested LAMBDAs (50–100+) across categories like string manipulation, map navigation, sequencing, and general utilities.
- Deploy LAMBDAs into a workbook in seconds — either bulk-loading a thematic kit at the start of a competition or pulling individual functions on demand.
- Iterate on LAMBDAs between competitions with version history and the ability to refine formulas collaboratively with Claude.

Existing tools (OA Robot, Excel Labs AFE) partially solve this but have drawbacks: OA Robot impacts Excel startup time; AFE only imports from GitHub Gists with no structured metadata, search, or multi-source support.

## LAMBDA Storage

### Repository structure

```
lambdas/
  string/
    _library.yaml
    Split.lambda
    PadLeft.lambda
    ...
  map/
    _library.yaml
    BFS.lambda
    Dijkstra.lambda
    ...
  sequence/
    _library.yaml
    ...
```

### Library metadata (`_library.yaml`)

Each library folder contains a `_library.yaml` file with library-level metadata:

```yaml
name: String Utilities
description: Common string manipulation functions
default_prefix: str
```

- `name` — Human-readable library name.
- `description` — Short description for search/browsing.
- `default_prefix` — Default prefix applied when loading (e.g. `str.Split`). Can be `none` for no prefix. User can override at load time.

### LAMBDA file format (`.lambda`)

Each `.lambda` file contains a single LAMBDA definition in the self-documenting format used by Excel Labs AFE. This format is directly compatible with AFE module import.

```
/*  FUNCTION NAME:      Split
    DESCRIPTION:*//**Splits a string by a delimiter and returns a dynamic array.*/
/*  REVISIONS:          Date        Developer   Description
                        2026-04-06  Tim Jacks   Initial version
*/
Split = LAMBDA(
//  Parameter Declarations
    Text,
    Delimiter,
    [IgnoreEmpty],
//  Help
    LET(Help, TEXTSPLIT(
                "FUNCTION:      →Split(Text, Delimiter, [IgnoreEmpty])¶" &
                "DESCRIPTION:   →Splits a string by a delimiter and returns a dynamic array.¶" &
                "VERSION:       →Apr 06 2026¶" &
                "PARAMETERS:    →¶" &
                "   Text           →(Required) The text to split.¶" &
                "   Delimiter      →(Required) The delimiter to split on.¶" &
                "   IgnoreEmpty    →(Optional) If TRUE, empty strings are excluded. Default FALSE.¶" &
                "EXAMPLE:       →¶" &
                "Formula        →=Split(""Hello World"", "" "")¶" &
                "Result         →{""Hello"", ""World""}",
                "→", "¶"
                ),
    //  Procedure
        ...actual formula logic...
        IF(Help?, Help, result)
)
);
```

Key points:

- One file per LAMBDA. Filename matches the LAMBDA name (e.g. `Split.lambda` defines `Split`).
- The self-documenting `Help` pattern (showing usage when called with no arguments) is the standard format for all LAMBDAs.
- The format is directly pasteable into AFE and compatible with AFE module import when files are concatenated.
- No tabs or carriage returns in the file (AFE restriction).

### Dependencies and prefixing

**Core mechanism: bare names with prefix-on-load**

LAMBDA source files reference other LAMBDAs using **bare names** — no prefix, no placeholder syntax:

```
BFS = LAMBDA(Grid, Start, End,
    LET(
        neighbors, GetNeighbors(Grid, Start),
        ...
    )
);
```

On load, the add-in knows every LAMBDA name in the library (from the filenames). After the user chooses a prefix (e.g. `map`), the add-in performs text replacement: for every known name `Foo` in the library, occurrences of `Foo(` in formula text are replaced with `map.Foo(`. The Name Manager entries are also prefixed (`map.BFS`, `map.GetNeighbors`). If the user chooses no prefix, no replacement occurs and bare names work as-is.

**String-literal exclusion:** The self-documenting Help section contains function names inside string literals (e.g. `"Formula →=BFS(Grid, Start)¶"`). The add-in must skip text inside `"..."` when performing replacements. Excel uses doubled quotes (`""`) for escaping — no backslashes — so the parser is straightforward.

**v1 — libraries are self-contained:**

- Loading a library loads **all** LAMBDAs in that library. This sidesteps dependency resolution entirely.
- Dependencies within a library are allowed (e.g. a recursive helper calling another LAMBDA in the same library). These resolve automatically via the prefix-on-load mechanism.
- Cross-library dependencies are not allowed in v1. Each library must be self-contained.
- Individual LAMBDA loading (via search) also loads the entire containing library, since all LAMBDAs may reference each other.

Cross-library dependencies are a v2 goal — see the Future section below.

## Retrieval Add-in

### Technology

- .NET 6 / C# 10 with ExcelDNA (same stack as Formula Boss for consistency).
- Separate ExcelDNA project within this repository (`addin/` folder).

### Core UX

**Primary access:** Keyboard shortcut `Ctrl+Shift+L` (configurable in settings) opens a task pane / popup.

**Two modes of operation:**

1. **Load library** — User types or selects a library name. All LAMBDAs in that library are injected into the workbook's Name Manager. User is prompted for prefix (pre-filled with the library's `default_prefix`, editable, can be cleared for no prefix).

2. **Search and load individual LAMBDA** — User types a search query. Results show matching LAMBDAs across all enabled repos. User selects one; it is loaded (along with any same-library dependencies) into the Name Manager.

**Injection mechanism:** LAMBDAs are written to the workbook's Name Manager as named formulas. This is the same mechanism AFE uses and makes them available as `=FunctionName()` or `=prefix.FunctionName()` in cells.

### Multi-source support

The add-in supports multiple GitHub repository sources:

- **Add repo** — User provides a GitHub repo URL (must follow the `lambdas/` folder convention). The add-in validates the structure and adds it to the source list.
- **Toggle repos** — Each repo can be enabled or disabled. Disabled repos are excluded from search and browsing.
- **Settings persistence** — Repo list and toggle state are persisted in a local settings file (location TBD — likely `%APPDATA%/LambdaBoss/settings.json`).

When multiple repos are enabled, search spans all of them. Library names are scoped by repo if there are naming conflicts.

### Fetching from GitHub

The add-in fetches LAMBDA definitions from GitHub at runtime:

- Uses the GitHub REST API or raw.githubusercontent.com (no authentication required for public repos).
- On load-library: fetches `_library.yaml` + all `.lambda` files in the library folder.
- Caches fetched data locally for offline use / faster repeated access. Cache is invalidated on explicit refresh.

### Update capability

If LAMBDAs from a repo have already been loaded into the workbook, the user can trigger an **update** to re-fetch from GitHub and overwrite the Name Manager definitions with the latest versions. This is essential for the design workflow (see below).

The update flow:

1. User triggers update (via task pane button or shortcut).
2. Add-in identifies which loaded LAMBDAs came from which repo/library.
3. Fetches latest versions from GitHub.
4. Overwrites Name Manager definitions.
5. Shows a summary of what changed (new, updated, unchanged).

### Settings

Persisted in `%APPDATA%/LambdaBoss/settings.json`:

- `repos` — List of GitHub repo sources with enabled/disabled state.
- `keyboard_shortcut` — Customizable shortcut (default `Ctrl+Shift+L`).
- Additional settings as needed (cache TTL, default prefix behaviour, etc.).

## Design Workflow (v1)

The v1 design workflow leverages Claude Code for LAMBDA authoring with the user closing the feedback loop in Excel:

1. **Idea capture** — New LAMBDA ideas are recorded as GitHub Issues on this repo with the `enhancement` label. Issues describe the use case, expected inputs/outputs, and optionally attach example Excel workbooks (`.xlsx` files uploaded to the issue).

2. **Drafting** — Claude Code (or user) creates a `.lambda` file in the appropriate library folder, following the self-documenting format.

3. **Testing** — User has the workbook open with the library loaded. After Claude commits changes, user clicks **Update** in the Lambda Boss add-in to pull the latest version. User tests in Excel and provides feedback to Claude.

4. **Iteration** — Claude refines the LAMBDA based on feedback. User updates and re-tests. Repeat until satisfied.

5. **Commit** — Final version is committed to `main`.

This workflow requires no additional infrastructure beyond the add-in's update feature and the GitHub repo.

### Future (v2)

**Automated test harness:**

A test harness that can:

- Inject a LAMBDA definition into an Excel instance's Name Manager.
- Execute test cases (input → expected output) against the LAMBDA.
- Report pass/fail results back to Claude Code.

This would enable fully autonomous LAMBDA development by Claude, including regression testing when refining existing LAMBDAs. Architecture TBD — likely builds on the AddIn test infrastructure from Formula Boss.

**Cross-library dependencies:**

Allow LAMBDAs to reference names from other libraries. `_library.yaml` would gain a `dependencies` section:

```yaml
name: Map Navigation
dependencies:
  - library: datastructures
    names: [PriorityQueue, Stack]
```

On load, the add-in checks if the referenced library is already loaded (and with what prefix), then applies the correct prefix replacement for cross-library references. If not loaded, it pulls it in with its default prefix. This also enables individual LAMBDA installation with only required dependencies pulled in.

## Out of Scope (for now)

- **In-person competition constraints** — Assumed full control of environment for v1.
- **Sharing / collaboration UX** — Multi-source support enables collaboration (share your repo URL), but there's no built-in discovery or social features.
- **AFE interoperability** — The `.lambda` format is AFE-compatible, so users can still use AFE for import if desired. No direct AFE integration in the add-in.
- **Claude Co-Work integration** — Co-Work can drive Excel directly and could be useful for testing, but integrating it with the Git-based workflow adds complexity. Revisit when Co-Work's Git capabilities mature.
- **Private repo support** — All repos assumed public for v1. Token-based auth for private repos is a v2 feature.
