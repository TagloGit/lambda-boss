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

- Default branch: `main`
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

## Publishing a release

End-to-end release publishing is handled by `scripts/publish-release.ps1`. Run it from a clean `main` branch; it walks through version bump, build, test, code-signing, release-zip bundling, PR merge, tagging, and draft GitHub Release creation.

```powershell
# Interactive (prompts for version, cert path, cert password)
.\scripts\publish-release.ps1

# Dry run end-to-end without any destructive steps (no PR, no tag, no release)
.\scripts\publish-release.ps1 -DryRun -SkipSign
```

The release artifact is `release/output/LambdaBoss-<version>.zip` — a small zip containing the signed `lambda-boss64.xll`, a `README.txt`, and an `unblock.cmd` helper for clearing `Zone.Identifier` ADS on USB-copied files. Both `release/README.txt` and `release/unblock.cmd` are checked-in source files; `release/output/` is gitignored.

For signed releases, have the Sectigo `.pfx` certificate path and password ready. The version bump only lands on `main` after the build and signing steps succeed, so a failed release never leaves `main` in a bumped state.
