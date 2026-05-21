# 0007 — .NET Framework 4.8 port

> **Status: Draft.** Awaiting Tim's review.
> Supersedes [spec 0006](0006-self-contained-tournament-build.md).

## Problem

Lambda Boss targets `net6.0-windows`. That made sense when the only distribution shape was the InnoSetup installer running on developer-owned or IT-managed machines, where bundling the .NET 6 Desktop Runtime alongside the XLL is acceptable. It fails the moment we leave that environment:

- **Tournament laptops:** no admin rights, no installer, no internet, no assumption that a .NET 6 runtime is present.
- **Locked-down corporate Excel:** IT may block runtime installers, and end users cannot run an `.exe` setup.
- **Future Taglo deployments to anyone whose machine state we don't control.**

Spec 0006 originally proposed solving this by producing a *second* artifact — a self-contained, single-file packed XLL with the .NET 6 runtime embedded. The spike on issue #190 ([spec 0006 § Spike outcome](0006-self-contained-tournament-build.md#spike-outcome)) confirmed this is architecturally impossible: ExcelDNA's loader cannot use a bring-your-own-runtime per-add-in, the .NET single-file bundler requires `OutputType=Exe`, and ExcelDNA's pack step does not integrate with `dotnet publish` in self-contained mode. Govert van Drimmelen's official recommendation for this scenario is to target .NET Framework.

We need to make Lambda Boss runnable on any modern Windows + Excel machine with **zero runtime prerequisite**. The cleanest path is to take Govert's recommendation and retarget the add-in to .NET Framework 4.8, which is part of Windows itself on every supported Windows 10 / 11 machine. ExcelDNA's existing `ExcelDnaPack` produces a true single-file packed XLL under .NET Framework — the same artifact serves daily drivers, tournament use, and any future locked-down deployment.

## Proposed Solution

Migrate the entire Lambda Boss codebase from `net6.0-windows` to `net48` in a single big-bang change. The .NET 6 build is **replaced**, not paralleled — we're not maintaining two targets. At the same time, three downstream simplifications fall out:

- The existing InnoSetup installer is **deprecated and removed**. There's nothing left for it to do that a zip + README can't.
- `Taglo.Excel.Common` is **inlined** into the lambda-boss project rather than multi-targeted across repos. Usage is shallow; revisit splitting later if a second consumer appears.
- `gong-wpf-dragdrop` and `Ookii.Dialogs.Wpf` are **pinned to the last versions that still support .NET Framework 4.x**. Both libraries are mature and stable — accepting a slightly older version is a much smaller cost than replacing them.

The post-port distribution shape is a single zip:

`LambdaBoss-x.y.z.zip` containing:

- `lambda-boss64.xll` — signed, packed XLL with all managed deps embedded as Win32 resources.
- `README.txt` — load instructions.
- `unblock.cmd` — `powershell -Command "Get-ChildItem -Recurse | Unblock-File"` to clear `Zone.Identifier` ADS on USB-copied files.

The README documents three load paths, in decreasing order of "for daily drivers":

1. **`%APPDATA%\Microsoft\AddIns\` + Excel Add-Ins dialog (recommended for daily use).** Copy the XLL into the default Excel add-ins folder, then Excel → File → Options → Add-Ins → Go → tick. Loads persistently for that Excel user profile.
2. **Any folder + Excel Add-Ins dialog (tournament / portable).** Same as above but the XLL lives wherever the user wants (e.g. `Documents\Lambda Boss\` or a USB stick). The Add-Ins dialog remembers the path.
3. **Double-click (single session).** Double-click the XLL to launch Excel with Lambda Boss enabled for that session only.

No installer, no registry edits we own, no PowerShell install script, no .NET runtime installation. The XLL is the product; everything else is documentation.

### Project changes

#### `addin/lambda-boss/lambda-boss.csproj`

- `<TargetFramework>net6.0-windows</TargetFramework>` → `<TargetFramework>net48</TargetFramework>`.
- `<UseWPF>true</UseWPF>` — keep; supported on net48 via the modern SDK.
- `<ExcelDna64Bit>true</ExcelDna64Bit>` — keep.
- Add `<LangVersion>latest</LangVersion>` so file-scoped namespaces and other C# 10+ features continue to compile (the codebase uses them heavily — 69 files).
- Add `System.Text.Json` `PackageReference` (the two callers — `GitHubSource.cs`, `Settings.cs` — pick it up from the framework on net6 but need an explicit NuGet on net48).
- Drop the `Taglo.Excel.Common` `PackageReference`; inline the few helpers Lambda Boss uses into the project (see § Inlining `Taglo.Excel.Common`).
- Pin `gong-wpf-dragdrop` and `Ookii.Dialogs.Wpf` to net48-compatible versions (see § Dependencies).

#### `addin/lambda-boss.Tests/lambda-boss.Tests.csproj`

- Target net48. `xunit`, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio` at the current floors (2.6.x / 17.8.0) all support net48 — no upgrade needed.

#### `addin/lambda-boss.AddinTests/lambda-boss.AddinTests.csproj`

- Same as above, plus the explicit `YamlDotNet 16.3.0` reference (already used).

#### `Directory.Build.props`

- No changes expected (only contains `<Version>`).

### Dependencies — net48 compatibility

| Package | Current version | Net48 action |
|---|---|---|
| `ExcelDna.AddIn` | 1.* (resolves 1.9.0) | ✅ Keep current floor — net462+ is supported and net48 is the recommended target |
| `YamlDotNet` | 16.* | ✅ Keep current floor — multi-targets including netstandard2.0 |
| `gong-wpf-dragdrop` | 3.2.* | Pin to the last version that targets net4x. Implementation plan picks the exact version after a NuGet history check |
| `Ookii.Dialogs.Wpf` | 5.0.1 | Pin to the last version that targets net4x. Same triage path as `gong-wpf-dragdrop` |
| `Taglo.Excel.Common` | 0.1.* | **Remove.** Inline the helpers Lambda Boss uses |
| `System.Text.Json` | new | Add at a version supporting netstandard2.0 (8.x or 9.x) |

If older `gong-wpf-dragdrop` or `Ookii.Dialogs.Wpf` versions surface bugs that newer versions have fixed, the implementation plan can fall back to (a) in-house drag-and-drop helper or (b) `Microsoft.Win32.OpenFileDialog`-based replacement. Both are modest; we expect the older versions to be fine.

### Inlining `Taglo.Excel.Common`

Rather than multi-targeting the shared package across repos, copy whatever Lambda Boss actually uses into the lambda-boss project (likely under `addin/lambda-boss/Common/` or similar). Audit step in the implementation plan:

1. Grep for `Taglo.Excel.Common` usages.
2. Copy the source for those types directly into Lambda Boss (preserving namespaces or renaming under `LambdaBoss.Common` — implementation plan picks).
3. Remove the `PackageReference`.

The shared package is small (~15 KB DLL); the inlined surface should be a handful of types at most. If a second consumer of `Taglo.Excel.Common` emerges later, we revisit splitting it back out — but that's a future-tense problem.

### Code-level changes

A scan of the codebase found:

- **No `record` types, no `with` expressions, no `init`-only setters** — language features that need C# 9+ but compile fine on net48 with modern SDK + `<LangVersion>latest>`.
- **No `IAsyncEnumerable`** — would have needed `Microsoft.Bcl.AsyncInterfaces`.
- **`System.Text.Json`** in `GitHubSource.cs` and `Settings.cs` — covered by NuGet package.
- **File-scoped namespaces** in ~69 files — C# 10 syntax, works on net48 with `<LangVersion>latest>`.
- **Nullable reference types** assumed throughout — `<Nullable>enable</Nullable>` works on net48.

Any net6-specific BCL surface (e.g. `HttpClient` defaults, `DateOnly`, `TimeOnly`, `string.Contains(char)`) will surface as a compile error on first net48 build; the plan addresses these as they appear rather than enumerating upfront.

### `.dna` change

`addin/lambda-boss/lambda-boss.dna` flips `Pack="false"` to `Pack="true"` so the main `lambda-boss.dll` is packed into the XLL like the rest of the managed deps. The build's `*-packed.xll` becomes the only artifact we care about; loose DLLs in `bin\Release\` are intermediate output, not shipped.

### Installer deprecation

The entire installer flow is removed:

- Delete `installer/lambda-boss.iss`.
- Delete `installer/bundled-runtime/` and its README.
- Delete `installer/output/` from gitignore patterns if necessary (or repurpose for the new zip output).
- Delete `installer/logo.ico`, `installer/wizard-*.bmp` if they were only used by InnoSetup. (Keep `logo.ico` if it's also embedded in the XLL — the implementation plan checks.)

The autoload behaviour that the installer's `OPEN`-key trick used to provide is replaced by Excel's built-in Add-Ins dialog flow, documented in the README. Users who want one-click reinstall after a Windows wipe can drop the XLL into `%APPDATA%\Microsoft\AddIns\` and tick the box in Excel once.

### Distribution build — new `scripts/build-release-bundle.ps1`

Replaces the InnoSetup compilation step. Logically straightforward:

1. Locate the signed `lambda-boss64-packed.xll` from the Release build.
2. Stage into `release/staging/`: rename to `lambda-boss64.xll`, drop in `README.txt` and `unblock.cmd`.
3. Zip as `LambdaBoss-x.y.z.zip` in `release/output/`.

`README.txt` and `unblock.cmd` are new files checked into `release/` (or a similar top-level directory; implementation plan picks the layout).

### `scripts/publish-release.ps1` changes

Substantial simplification. The new flow:

1. Preflight + version bump (existing).
2. Build in Release configuration (existing).
3. Run tests (existing).
4. Sign the packed XLL (existing step, retargeted at `lambda-boss64-packed.xll`).
5. Call `build-release-bundle.ps1` to produce the zip.
6. Sign the zip is not needed — code-signing applies to the XLL inside.
7. Merge version bump PR, tag, create GitHub Release with the zip attached (replaces installer attachment).

Drop everything related to: `IsccExe` preflight, the InnoSetup compile step, the installer signing step, the bundled-runtime warning, the `installerPath` / `installerName` / "Installation" release notes section.

### Build/test plan during migration

The migration is one PR. The order of operations inside the PR:

1. Retarget the three project files to net48. Build will fail.
2. Add `System.Text.Json` NuGet and any other missing-API NuGets.
3. Inline `Taglo.Excel.Common` usages; drop the package reference.
4. Pin `gong-wpf-dragdrop` and `Ookii.Dialogs.Wpf` to net48-compatible versions.
5. Resolve any remaining compile errors from net6-specific API usage.
6. Flip `Pack="true"` in `lambda-boss.dna`.
7. `dotnet build addin/lambda-boss.slnx` clean, `dotnet test addin/lambda-boss.Tests/lambda-boss.Tests.csproj` clean.
8. Manual smoke test of the packed XLL by double-click load in a real Excel: open Lambda Boss popup, evaluate a LAMBDA, exercise `/Gather` and `/Edit` slash commands.
9. Delete the installer (`installer/lambda-boss.iss`, `installer/bundled-runtime/`, related assets).
10. Add `scripts/build-release-bundle.ps1`, `README.txt`, `unblock.cmd`.
11. Update `scripts/publish-release.ps1` to drop installer steps and call the new bundle script.
12. Update `CLAUDE.md` and any other docs that reference .NET 6 or the installer.

### Verification

Final acceptance verification, done by Tim, on a clean Windows 11 + Office 365 VM with no .NET 6 runtime installed:

- **AddIns folder path:** copy XLL into `%APPDATA%\Microsoft\AddIns\` → Excel → Add-Ins → tick → popup opens → LAMBDA evaluates → all slash commands work.
- **Arbitrary folder path:** copy XLL into `Documents\Lambda Boss\` → Excel → Add-Ins → Browse → tick → popup opens → LAMBDA evaluates.
- **Double-click path:** double-click the XLL → Excel opens with Lambda Boss enabled for that session.
- **USB / Zone.Identifier path:** copy from a USB drive (or simulated ADS), run `unblock.cmd`, load via Add-Ins dialog → works.

## User Stories

- As Tim preparing for a competitive Excel tournament, I want a single signed XLL file I can carry on a USB stick, so that I can install Lambda Boss on a provided laptop with no internet, no admin rights, and no installer.
- As Tim on the day of the tournament, I want the install flow to match the muscle memory I already have for OARobot, so that I am not learning a new procedure in a 15-30 minute pre-battle window.
- As Tim shipping daily releases, I want each release to be a single small zip, so that the publishing pipeline has one artifact and one signing step rather than three.
- As Tim maintaining Lambda Boss, I want a single build target rather than two, so that I'm not testing every release against two .NET surfaces.
- As a future Taglo user in a locked-down corporate Excel environment, I want a single-file XLL with no install prerequisites, so that I can use Lambda Boss without IT involvement.

## Acceptance Criteria

- [ ] `addin/lambda-boss/lambda-boss.csproj`, `lambda-boss.Tests.csproj`, and `lambda-boss.AddinTests.csproj` target `net48`.
- [ ] `dotnet build addin/lambda-boss.slnx` succeeds with no warnings introduced by the retargeting.
- [ ] `dotnet test addin/lambda-boss.Tests/lambda-boss.Tests.csproj` passes (all existing tests green).
- [ ] The build produces a signed, packed `lambda-boss64.xll` as the **sole runtime artifact**. No loose `lambda-boss.dll`, `*.deps.json`, `*.runtimeconfig.json`, or `Taglo.Excel.Common.dll` ships.
- [ ] On a clean Windows 11 + Office 365 VM with no .NET 6 runtime present, the release zip's XLL loads via the Add-Ins dialog and via double-click, with full functionality.
- [ ] `scripts/publish-release.ps1` produces `LambdaBoss-x.y.z.zip` in a single end-to-end release run, and it is attached to the GitHub Release draft. No installer is built or shipped.
- [ ] `installer/lambda-boss.iss`, `installer/bundled-runtime/`, and any other InnoSetup-only assets are removed from the repo.
- [ ] `Taglo.Excel.Common` is no longer a `PackageReference` of any project in the solution.
- [ ] `CLAUDE.md` and any other docs are updated to reflect net48 as the build target and the zip-only distribution shape.

## Out of Scope

- **Formula Boss .NET Framework port.** Tracked separately if/when needed. Formula Boss's `AssemblyLoadContext` / Roslyn type-identity machinery is a meaningfully different migration.
- **Splitting `Taglo.Excel.Common` back into a shared package.** Inlined here for simplicity; revisit only if a second consumer appears.
- **NativeAOT.** Future possibility per ExcelDNA docs; out of scope.
- **Multi-version Office testing matrix.** Existing matrix (Office 365 with `REGEX` support) remains the verification target.
- **Cross-add-in runtime conflicts.** Under .NET Framework, multiple ExcelDNA add-ins coexist without runtime-version concerns, so this is implicitly resolved rather than explicitly addressed.

## Open Questions

- **`Settings.cs` `JsonSerializerOptions` parity.** Net6's built-in `System.Text.Json` and the NuGet-shipped version exposed on net48 are largely the same surface, but there can be subtle differences in serialization defaults around nullability and naming policy. The implementation plan should verify round-trip equivalence — load a settings.json saved under net6, save it again under net48, diff — before treating the migration as complete.
