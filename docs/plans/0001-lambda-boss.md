# 0001 — Lambda Boss — Implementation Plan

## Overview

Build the Lambda Boss ExcelDNA add-in using a tracer bullet approach: get end-to-end functionality working as early as possible, then iterate. Reuse battle-tested infrastructure from Formula Boss via a new shared NuGet package (`Taglo.Excel.Common`).

**Spec:** `docs/specs/0001-lambda-boss.md`

## Architecture Decisions

- **UI:** Floating WPF window on a dedicated STA thread (same pattern as Formula Boss). Optimized for keyboard speed: shortcut → type search → arrow keys → Enter → window closes.
- **Shared infrastructure:** Extracted to a new `taglo-excel-common` repo, published as a NuGet package to GitHub Packages. Both Formula Boss and Lambda Boss consume it.
- **GitHub fetching:** raw.githubusercontent.com for v1 (simpler, no rate limits, no auth needed for public repos).
- **Name Manager injection:** COM interop via `Workbook.Names.Add(name, formula)`.
- **Target framework:** .NET 6 / C# 10 (same as Formula Boss for ExcelDNA 1.9 compatibility).

## Step 0 — Extract `taglo-excel-common` shared package

**Goal:** Single source of truth for ExcelDNA infrastructure reused across add-ins.

**New repo:** `TagloGit/taglo-excel-common`

### Files to extract from Formula Boss

| Source (formula-boss) | Target (taglo-excel-common) | Changes |
|---|---|---|
| `UI/NativeMethods.cs` | `NativeMethods.cs` | Namespace → `Taglo.Excel.Common`, visibility `public` |
| `UI/CellPositioner.cs` | `CellPositioner.cs` | Namespace → `Taglo.Excel.Common` |
| `UI/WindowPositioner.cs` | `WindowPositioner.cs` | Namespace → `Taglo.Excel.Common` |
| `Logger.cs` | `Logger.cs` | Namespace → `Taglo.Excel.Common`, parameterize app name (folder/filename) |
| `Updates/UpdateChecker.cs` | `UpdateChecker.cs` | Namespace → `Taglo.Excel.Common`, parameterize repo URL + user-agent |

### Package structure

```
taglo-excel-common/
  taglo-excel-common.sln
  src/
    Taglo.Excel.Common/
      Taglo.Excel.Common.csproj    # net6.0-windows, ExcelDna.Integration ref
      NativeMethods.cs
      CellPositioner.cs
      WindowPositioner.cs
      Logger.cs
      UpdateChecker.cs
```

### Changes needed for parameterization

- **Logger:** Constructor or `Initialize(string appName)` instead of hardcoded "FormulaBoss" paths. Derives log directory from `%LOCALAPPDATA%\{appName}\logs\{appName}.log`.
- **UpdateChecker:** Accept repo URL and user-agent string via static `Initialize(string repoUrl, string userAgent)` or similar. Keep the event-based notification pattern.

### Formula Boss migration

- Replace the extracted files with a `PackageReference` to `Taglo.Excel.Common`.
- Update `using` statements (`Taglo.Excel.Common` instead of `FormulaBoss.UI` / `FormulaBoss`).
- Update `Logger.Initialize()` → `Logger.Initialize("FormulaBoss")`.
- Update `UpdateChecker` initialization to pass repo URL.
- Verify all existing tests still pass.

### CI/CD

- **CI workflow** (`.github/workflows/ci.yml`): On every push/PR — `dotnet build` + `dotnet test`. Ensures shared code doesn't regress.
- **Publish workflow** (`.github/workflows/publish.yml`): On tag push (`v*`) — `dotnet pack` → push to GitHub Packages.
- Consumer repos add `nuget.config` pointing to `https://nuget.pkg.github.com/TagloGit/index.json`.

### Tests

- Unit tests for `Logger` (initialize, write, rotation).
- Unit tests for `UpdateChecker.ParseVersion` (already exists in FB, move over).
- CellPositioner/WindowPositioner are hard to unit test (Win32 calls) — covered by consumer AddIn tests.

## Step 1 — Tracer bullet: shortcut → popup → hardcoded LAMBDA → Name Manager

**Goal:** Prove end-to-end functionality. Press Ctrl+Shift+L, see a popup, press Enter, and a LAMBDA appears in the workbook's Name Manager.

### Repo structure

```
lambda-boss/
  .github/
    workflows/
      ci.yml                      # dotnet build + test on every push/PR
  addin/
    lambda-boss.slnx
    nuget.config                  # GitHub Packages source for Taglo.Excel.Common
    lambda-boss/
      lambda-boss.csproj
      lambda-boss.dna
      AddIn.cs                    # ExcelDNA lifecycle (adapted from FB)
      LambdaLoader.cs             # Name Manager injection via COM
      UI/
        LambdaPopup.xaml          # Minimal WPF window
        LambdaPopup.xaml.cs
      Resources/
        logo32.png                # Placeholder icon
    lambda-boss.Tests/
      lambda-boss.Tests.csproj
      LambdaLoaderTests.cs        # Unit tests for loader logic
    lambda-boss.AddinTests/
      lambda-boss.AddinTests.csproj
      SmokeTests.cs               # Excel integration: load add-in, verify shortcut works
  lambdas/
    test/
      _library.yaml               # name: Test, default_prefix: tst
      Double.lambda                # DOUBLE = LAMBDA(x, x*2)
      Triple.lambda                # TRIPLE = LAMBDA(x, x*3)
      AddN.lambda                  # ADDN = LAMBDA(x, n, x+n) — tests multi-param
  docs/
```

### CI

- **`.github/workflows/ci.yml`**: On push/PR — `dotnet build addin/lambda-boss.slnx` + `dotnet test addin/lambda-boss.slnx` (unit tests only; AddIn tests require Excel and run locally or on a self-hosted runner).

### Files to create

- **`AddIn.cs`** — Adapted from FB. Lifecycle skeleton: `AutoOpen` (Logger init, ShutdownMonitor, keyboard shortcut `^+L`, deferred init via QueueAsMacro), `Dispose` (cleanup). No pipeline/compiler infrastructure.
- **`LambdaLoader.cs`** — Static class with `InjectLambda(string name, string formula)` that calls `Workbook.Names.Add` via COM. Hardcode one test LAMBDA for the tracer bullet (e.g. `DOUBLE = LAMBDA(x, x*2)`).
- **`LambdaPopup.xaml`** — Minimal WPF window: TextBox for search, ListBox for results, Enter to select. Hardcoded list of 2-3 dummy LAMBDAs. Styled but minimal.
- **`LambdaPopup.xaml.cs`** — Keyboard handling: Escape closes, Up/Down navigate, Enter loads selected LAMBDA and closes window. Capture target workbook reference on open.
- **`lambda-boss.csproj`** — .NET 6, UseWPF, ExcelDna.AddIn, PackageReference to Taglo.Excel.Common.
- **`lambda-boss.dna`** — ExternalLibrary with Pack="false" and logo image.

### WPF thread pattern (from FB)

Reuse the dedicated STA thread approach from `ShowFloatingEditorCommand`:
1. First invocation creates background STA thread + Dispatcher.
2. WPF window created/shown on that thread.
3. Toggle behavior: Ctrl+Shift+L shows/hides.
4. Shutdown: `InvokeShutdown()` + `Thread.Join(2s)`.

### AddIn test infrastructure

Adapt from Formula Boss's `AddinTests` project:
- Test fixture that launches Excel, loads the XLL, runs assertions, and shuts down.
- Smoke test: verify add-in loads without error.
- Smoke test: trigger Ctrl+Shift+L, verify popup appears (window handle check).
- Smoke test: inject a LAMBDA, verify it appears in `Workbook.Names`.

### Discovery: Name Manager COM calls

Prototype and verify these COM calls during this step:

```csharp
// Inject a LAMBDA into the active workbook
dynamic app = ExcelDnaUtil.Application;
dynamic workbook = app.ActiveWorkbook;
workbook.Names.Add("DOUBLE", "=LAMBDA(x, x*2)");

// Verify it works: =DOUBLE(5) should return 10
```

Questions to answer:
- Does ExcelDNA have any built-in Name Manager helpers? (Check ExcelDNA docs/source)
- Does `Names.Add` require a macro context (`QueueAsMacro`)?
- Does the formula string need the `=` prefix?
- How to handle existing names (update vs error)?

## Step 2 — LAMBDA file parsing and local loading

**Goal:** Parse `.lambda` files from disk, apply prefix, inject into Name Manager.

### Files to create/modify

- **`LambdaParser.cs`** — Parse `.lambda` file format: extract name (from `Name = LAMBDA(...)` pattern), extract full formula body. Handle the self-documenting Help format.
- **`LibraryMetadata.cs`** — Deserialize `_library.yaml`: name, description, default_prefix.
- **`PrefixRewriter.cs`** — Given a set of known names and a chosen prefix, perform text replacement `Foo(` → `prefix.Foo(` in formula text, skipping string literals (`"..."`).
- **`LambdaLoader.cs`** — Extend to accept a library folder path, parse all `.lambda` files, apply prefix, inject all into Name Manager.

### Dependencies

- `YamlDotNet` package for `_library.yaml` parsing.

### Tests

- Unit tests for `LambdaParser`: various `.lambda` file formats, edge cases.
- Unit tests for `PrefixRewriter`: basic replacement, string-literal exclusion, doubled quotes.
- AddIn test: load a test library from a local `testdata/` folder, verify all names appear in Name Manager with correct prefix.

## Step 3 — GitHub fetching and caching

**Goal:** Fetch LAMBDA libraries from GitHub repos at runtime.

### Files to create/modify

- **`GitHubSource.cs`** — Fetch library contents from raw.githubusercontent.com. Discover libraries by fetching the `lambdas/` directory listing via GitHub API (`GET /repos/{owner}/{repo}/contents/lambdas`), then fetch individual files via raw URLs.
- **`SourceCache.cs`** — Cache fetched files to `%LOCALAPPDATA%\LambdaBoss\cache\{repo-hash}\`. Serve from cache when available. Invalidate on explicit refresh.
- **`RepoConfig.cs`** — Model for a configured repo source (URL, enabled/disabled, last-fetched timestamp).

### Fetching strategy

1. Directory listing: `GET https://api.github.com/repos/{owner}/{repo}/contents/lambdas` → JSON array of folder names.
2. Library metadata: `GET https://raw.githubusercontent.com/{owner}/{repo}/main/lambdas/{library}/_library.yaml`
3. LAMBDA files: `GET https://raw.githubusercontent.com/{owner}/{repo}/main/lambdas/{library}/{name}.lambda`

Rate limiting: GitHub API allows 60 requests/hour unauthenticated. Directory listing is 1 request per repo; raw.githubusercontent.com has no rate limit. So: use API for discovery, raw URLs for file content.

### Tests

- Unit tests for `GitHubSource` with mocked HTTP responses.
- Unit tests for `SourceCache` (write/read/invalidate).
- AddIn test: configure this repo as a source, fetch and load a test library.

## Step 4 — Full popup UI

**Goal:** Replace the minimal tracer-bullet popup with the full search/browse experience.

### Files to modify

- **`LambdaPopup.xaml`** — Two modes:
  1. **Library mode** (default): list of available libraries grouped by repo. Select → prefix prompt → load all.
  2. **Search mode**: type to filter across all LAMBDAs. Select → loads containing library.
- **`LambdaPopup.xaml.cs`** — Keyboard-first UX:
  - Type to search (auto-switch to search mode).
  - Up/Down to navigate results.
  - Enter to load selected item.
  - Tab to switch between library/search modes.
  - Escape to close.
  - Prefix prompt: inline TextBox pre-filled with `default_prefix`, editable, Enter to confirm.

### Window positioning

- Use `WindowPositioner.CenterOnExcel` from `Taglo.Excel.Common` on first open.
- Remember position within session (same pattern as FB's `_hasBeenPositioned`).

## Step 5 — Settings and multi-repo support

**Goal:** Persist settings, manage multiple repo sources.

### Files to create/modify

- **`Settings.cs`** — JSON persistence to `%APPDATA%\LambdaBoss\settings.json`. Properties: `Repos` (list of RepoConfig), `KeyboardShortcut` (string, default `^+L`), `CacheTtlMinutes`.
- **`LambdaPopup.xaml`** — Add settings gear icon / section for managing repos (add URL, toggle enable/disable, remove).
- **Ribbon** — `RibbonController.cs` with Lambda Boss tab: Load Library button, Settings, About, Update notification.

## Step 6 — Update capability

**Goal:** Re-fetch and overwrite already-loaded LAMBDAs from GitHub.

### Files to create/modify

- **`WorkbookTracker.cs`** — Track which LAMBDAs were loaded into which workbook, from which repo/library, with which prefix. Store in a `Dictionary<string, LoadedLibraryInfo>` keyed by workbook name (session-only, not persisted).
- **`LambdaLoader.cs`** — Add `UpdateLibrary` method: re-fetch from GitHub, overwrite Name Manager definitions, return diff (new/updated/unchanged).
- **`LambdaPopup.xaml`** — Add "Update" action for loaded libraries. Show change summary after update completes.

### Tests

- AddIn test: load library, modify a `.lambda` file (or mock), trigger update, verify Name Manager reflects changes.

## Order of Operations (PR sequence)

| PR | Step | Repo | Description | Depends on |
|---|---|---|---|---|
| 1 | 0 | `taglo-excel-common` | Create repo, extract shared code, CI + publish workflow, tests, publish v0.1.0 | — |
| 2 | 0 | `taglo-formula-boss` | Migrate Formula Boss to consume `Taglo.Excel.Common` package | PR 1 |
| 3 | 1 | `lambda-boss` | Tracer bullet: add-in skeleton + popup + hardcoded LAMBDA + Name Manager + test library + CI + AddIn tests | PR 1 |
| 4 | 2 | `lambda-boss` | LAMBDA file parsing, prefix rewriting, local library loading | PR 3 |
| 5 | 3 | `lambda-boss` | GitHub fetching and caching | PR 4 |
| 6 | 4 | `lambda-boss` | Full popup UI (search + browse + prefix prompt) | PR 5 |
| 7 | 5 | `lambda-boss` | Settings persistence and multi-repo management | PR 6 |
| 8 | 6 | `lambda-boss` | Update capability (re-fetch and overwrite loaded LAMBDAs) | PR 7 |

## Testing Approach

- **Unit tests** (`lambda-boss.Tests`): Parser, prefix rewriter, cache, settings — all pure logic, no Excel dependency.
- **AddIn tests** (`lambda-boss.AddinTests`): Launch real Excel, load XLL, exercise end-to-end flows. Adapted from Formula Boss's proven AddIn test infrastructure.
- Each PR includes tests for the functionality it introduces.
