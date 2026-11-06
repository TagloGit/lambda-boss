# CLAUDE.md — lambda-boss

Excel add-in for accessing GitHub Lambda libraries

## Repo purpose

- ExcelDNA add-in
- Library of Lambdas
- Design and test harness for use by Claude Code in the creation of new Lambdas

## Tech stack

- .NET Framework 4.8 / C# (latest LangVersion) with ExcelDNA (ExcelDna.AddIn 1.x)
- WPF for popup UI (floating window on dedicated STA thread)
- xUnit for testing

Net48 targets every modern Windows (the runtime is part of the OS) so the packed XLL ships as a true single-file artifact with no install prerequisite. See [spec 0007](specs/0007-net-framework-port.md) for the rationale (we previously targeted .NET 6 and tried to produce a self-contained tournament build; ExcelDNA architecturally precludes that — see [spec 0006 § Spike outcome](specs/0006-self-contained-tournament-build.md#spike-outcome)).

## Build & test

```bash
# Restore + build
dotnet build addin/lambda-boss.slnx

# Unit tests (no Excel required)
dotnet test addin/lambda-boss.Tests/lambda-boss.Tests.csproj

# AddIn smoke tests (requires Excel installed, run locally)
dotnet test addin/lambda-boss.AddinTests/lambda-boss.AddinTests.csproj
```

### Targeting a single LAMBDA

The harness has ~470+ theory cases across all `*.tests.yaml` files. xUnit's `--filter` can't narrow to a single LAMBDA (all theory cases collapse under one `LambdaTest` method), so use the `LAMBDA_FILTER` env var instead — it's a case-insensitive substring match against the lambda file path:

```powershell
# Run only CELLDIST tests (~2 s vs ~20 s for the full suite)
$env:LAMBDA_FILTER = "CELLDIST"
dotnet test addin/lambda-boss.AddinTests/lambda-boss.AddinTests.csproj --filter "FullyQualifiedName~LambdaHarnessTests"
Remove-Item Env:LAMBDA_FILTER
```

When iterating on a single LAMBDA, always use `LAMBDA_FILTER`. Run the full suite (no filter) before raising the PR.

The packed XLL lands at `addin/lambda-boss/bin/Release/net48/publish/lambda-boss64-packed.xll`. All managed dependencies (`lambda-boss.dll`, `YamlDotNet.dll`, `GongSolutions.WPF.DragDrop.dll`, `Ookii.Dialogs.Wpf.dll`, `System.Text.Json.dll`, and transitive packages) are embedded in the XLL as Win32 resources — there are no loose side-car DLLs to ship.

## Conventions

- Default branch: `develop` (see [How work ships](#how-work-ships)). `main` is what Stable users get.
- **Never use compound Bash commands** (no `&&`, `;`, or `|` chaining). Use separate Bash tool calls instead — independent calls can run in parallel. Compound commands trigger extra permission prompts.
- **Never prefix Bash commands with `cd`**. The working directory is already the project root. All commands (`gh`, `git`, `npm`, etc.) work without `cd`.
- **Passing `gh` bodies and commit messages (multi-line text with apostrophes / quotes / `@`).** The Bash tool here is **real git-bash**, so the robust default is a **bash quoted heredoc** — `<<'EOF'` disables every expansion, making apostrophes (`doesn't`), `@`, `"`, `$`, and backticks all literal. No temp file, no delete:
  ```bash
  gh issue create --title "..." --body "$(cat <<'EOF'
  Body text — apostrophes and "quotes" are safe.
  EOF
  )"
  ```
  Use the same `git commit -F - <<'EOF' … EOF` form for commit messages. Two things to avoid:
  - **Never type PowerShell `@'...'@` here-strings into the Bash tool.** Bash reads `@` as a literal and `'...'` as an ordinary quote, so it injects stray `@` lines and hard-errors on the first apostrophe. (Don't use the PowerShell tool either: PS 5.1 silently *drops embedded straight double-quotes* when handing the arg to `gh.exe` — verified lossy.)
  - **Harness command-length limit (~965 bytes).** The permission layer parses the entire command string and rejects longer commands as malformed (`Command too long for parsing`). Only when a body genuinely exceeds this: write it to a temp file **outside the repo** (e.g. under `$env:TEMP`) with the Write tool, use `gh ... --body-file`, then delete it. Never leave a body `.md` file in the repo.
  - Encoding is *not* a problem: UTF-8 (em-dashes, curly quotes) round-trips correctly through `gh` → GitHub. The ASCII-only rule is for `.ps1` *files on disk* (windows-1252), not command-line arguments.
- **Always use `Range.Formula2`, never `Range.Formula`.** The legacy `Formula` property silently wraps array refs (e.g. `A1#`) with the implicit-intersection `@` operator on write and returns `@`-prefixed text on read, which scalarises dynamic-array formulas. `Formula2` is the modern dynamic-array-aware property — use it for both read and write, regardless of whether the formula in question is array-shaped.

## Net48 polyfills

Lambda Boss targets net48 but uses modern C# language features and a few BCL types that only exist on net5+. Polyfills bridge the gap:

- **PolySharp** (NuGet) supplies compiler-required types: `System.Index`, `System.Range`, `System.Runtime.CompilerServices.IsExternalInit` (for `record` types and `init`-only setters).
- **Microsoft.Bcl.HashCode** (NuGet) supplies `System.HashCode`. When using it with `StringComparer.OrdinalIgnoreCase` in a `GetHashCode()`, coalesce nullable strings to `""` first — the polyfill on net48 throws on null where .NET 6's built-in tolerates it.
- **`addin/lambda-boss/Common/NetFrameworkPolyfills.cs`** supplies a `KeyValuePair<TKey, TValue>.Deconstruct` extension so `foreach (var (k, v) in dict)` compiles.
- **`Directory.Build.props`** lists explicit `<Using>` items for the .NET 6 implicit-using set (`System`, `System.Linq`, `System.Threading.Tasks`, etc.) since `<ImplicitUsings>enable</ImplicitUsings>` is a no-op on net48.
- `ArgumentNullException.ThrowIfNull(x)` doesn't exist on net48 and PolySharp can't polyfill static methods; use inline `if (x is null) throw new ArgumentNullException(nameof(x));` instead. CA1510 is suppressed where appropriate.

## Adding NuGet dependencies

When introducing a new `PackageReference` that produces a runtime DLL, **add a matching `<Reference Path="..." Pack="true" />` entry to `addin/lambda-boss/lambda-boss.dna`** in the same PR. Without it, the dependency won't be embedded in the packed XLL and the add-in will crash at runtime with `FileNotFoundException`.

On net48 there's no `deps.json`, so ExcelDNA's pack step can't auto-enumerate managed deps the way it did on net6. The `.dna` is the source of truth for what gets packed.

To check what a new package adds to the build, look at `addin/lambda-boss/bin/Release/net48/` after a Release build and cross-reference against the `<Reference>` list in the `.dna`. Don't forget transitive deps — most NuGets pull in several `System.*` shim assemblies on net48.

## How work ships

Leads take a feature from design to tested and refined on their own, and Tim gets finished work in Labs and one release PR now and then. [Spec 0012](specs/0012-develop-labs-release-flow.md) has the reasoning.

1. **Design.** Design talks happen with the Excel director. Write the spec in `specs/` as the lead's record; Tim doesn't review it. Where a UI choice is open, build the options side by side in Labs.
2. **Branch from `develop`, PR to `develop`.** A PR needs one independent review and green CI, then the lead merges it (squash, or rebase for an epic). No Tim approval. A merge publishes `dev-latest`.
3. **New add-in features go behind a Labs switch**, off by default: see `addin/lambda-boss/Labs.cs`. Fixes don't need one. Delete the switch and its guard once the feature ships to everyone or is dropped.
4. **Library changes** reach Labs users on merge to `develop` and Stable users at the next release.
5. **Prove it in real use.** Staging is the Parallels VM `taglo-win`: real Excel, under `~/Repositories/wt/VM-LOCK`, released straight after each run (taglo-pm `docs/windows-vm.md`). Never use real Excel on Tim's PC. Load the PR's build (the `lambda-boss-xll` artifact on its CI run) or `dev-latest`, and drive the add-in on real case or rep files. Unit tests alone aren't proof of a feature. Screenshots and what you drove go in the PR.
6. **Release** with the `/release` skill. Tim's approval on the release PR is the only gate he holds.

**Hold new library files until Tim has updated.** Add-ins older than 0.2.0 list a library's files from the repo's default branch (now `develop`) but fetch them from `main`, so a new `.lambda` or `.const` in an existing library on `develop` makes that whole library fail to load on those builds. Until #390's first release is merged and Tim has run the updater, don't merge new files into existing libraries. Then delete this paragraph.

### Library channel and Labs switches

- `Settings.LibraryChannel`: `Stable` (the default, reads `main`) or `Labs` (reads `develop`). Only this repo follows the channel; other repos stay on `main`. Each branch has its own disk cache.
- `Settings.LabsSwitchesOn`: the switches that are on. Channel and switches are independent, and both are in the settings window, which the ribbon's channel button opens.
- The ribbon's channel button shows the current channel. Check it before a competition.

### Dev builds

CI replaces the `dev-latest` pre-release on every merge to `develop`: an unsigned `lambda-boss64.xll`, with `version:` and `commit:` lines in its notes (the update check reads those). On the Labs channel the update check compares commits with `dev-latest`.

On a Windows PC with Excel closed, `scripts\update-lambda-boss.cmd` installs `dev-latest` to `%APPDATA%\Microsoft\AddIns\lambda-boss64.xll`, unblocks it and prints the version. `--stable` installs the latest release instead. The `.cmd` is also a `dev-latest` asset and works alone.

## Publishing a release

Use the `/release` skill. In short: `scripts/cut-release.sh` cuts `release/<date>` from `develop` and opens the PR to `main` with reviewer `taglotacks`; after Tim approves, an agent merges with a merge commit (never `--admin`) and pushes the tag `v<version>`; `.github/workflows/release.yml` builds, tests and publishes the unsigned zip.

**Default cadence: every week or two, on Tim's word. No release in the days before an MEWC round** (the competition freeze): Stable stays put and nothing merges to `main`.

`Directory.Build.props` holds the *next* release's version, so a tag can only match it once. The release artifact is `LambdaBoss-<version>.zip`: `lambda-boss64.xll`, `README.txt` and an `unblock.cmd` helper for clearing `Zone.Identifier` ADS on USB-copied files. Both `release/README.txt` and `release/unblock.cmd` are checked-in source files. Releases are unsigned: Tim isn't renewing the Sectigo certificate. Excel trusts its AddIns folder, so the recommended load path shows no warning.
