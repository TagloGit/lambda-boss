# CLAUDE.md — lambda-boss

Excel add-in for accessing GitHub Lambda libraries

## Repo purpose

- ExcelDNA add-in
- Library of Lambdas
- Design and test harness for use by Claude Code in the creation of new Lambdas

## Tech stack

- .NET 6 / C# 10 with ExcelDNA (ExcelDna.AddIn 1.x)
- WPF for popup UI (floating window on dedicated STA thread)
- Taglo.Excel.Common shared package (GitHub Packages)
- xUnit for testing

## Build & test

```bash
# Restore + build
dotnet build addin/lambda-boss.slnx

# Unit tests (no Excel required)
dotnet test addin/lambda-boss.Tests/lambda-boss.Tests.csproj

# AddIn smoke tests (requires Excel installed, run locally)
dotnet test addin/lambda-boss.AddinTests/lambda-boss.AddinTests.csproj
```

## Conventions

- Default branch: `main`
- **Never use compound Bash commands** (no `&&`, `;`, or `|` chaining). Use separate Bash tool calls instead — independent calls can run in parallel. Compound commands trigger extra permission prompts.
- **Never prefix Bash commands with `cd`**. The working directory is already the project root. All commands (`gh`, `git`, `npm`, etc.) work without `cd`.

## Publishing a release

End-to-end release publishing is handled by `scripts/publish-release.ps1`. Run it from a clean `main` branch; it walks through version bump, build, test, code-signing, installer compilation, PR merge, tagging, and draft GitHub Release creation.

```powershell
# Interactive (prompts for version, cert path, cert password)
.\scripts\publish-release.ps1

# Dry run end-to-end without any destructive steps (no PR, no tag, no release)
.\scripts\publish-release.ps1 -DryRun -SkipSign
```

Before running:

1. Drop the .NET 6 Desktop Runtime installer in `installer/bundled-runtime/` — see that directory's README.
2. Ensure [InnoSetup 6](https://jrsoftware.org/isdl.php) is installed at the default location.
3. For signed releases, have the Sectigo `.pfx` certificate path and password ready.

The version bump only lands on `main` after the build and signing steps succeed, so a failed release never leaves `main` in a bumped state.
