# 0007 — .NET Framework 4.8 port

> **Status: Draft.** Awaiting Tim's review.
> Supersedes [spec 0006](0006-self-contained-tournament-build.md).

## Problem

Lambda Boss targets `net6.0-windows`. That made sense when the only distribution shape was the InnoSetup installer running on developer-owned or IT-managed machines, where bundling the .NET 6 Desktop Runtime alongside the XLL is acceptable. It fails the moment we leave that environment:

- **Tournament laptops:** no admin rights, no installer, no internet, no assumption that a .NET 6 runtime is present.
- **Locked-down corporate Excel:** IT may block runtime installers, and end users cannot run an `.exe` setup.
- **Future Taglo deployments to anyone whose machine state we don't control.**

Spec 0006 originally proposed solving this by producing a *second* artifact — a self-contained, single-file packed XLL with the .NET 6 runtime embedded. The spike on issue #190 ([spec 0006 § Spike outcome](0006-self-contained-tournament-build.md#spike-outcome)) confirmed this is architecturally impossible: ExcelDNA's loader cannot use a bring-your-own-runtime per-add-in, the .NET single-file bundler requires `OutputType=Exe`, and ExcelDNA's pack step does not integrate with `dotnet publish` in self-contained mode. Govert van Drimmelen's official recommendation for this scenario is to target .NET Framework.

We need to make Lambda Boss runnable on any modern Windows + Excel machine with **zero runtime prerequisite**. The cleanest path is to take Govert's recommendation and retarget the add-in to .NET Framework 4.8, which is part of Windows itself on every supported Windows 10 / 11 machine. ExcelDNA's existing `ExcelDnaPack` produces a true single-file packed XLL under .NET Framework — the same artifact serves both the daily-driver installer flow and the tournament zip.

## Proposed Solution

Migrate the entire Lambda Boss codebase from `net6.0-windows` to `net48` in a single big-bang change. The .NET 6 build is **replaced**, not paralleled — Tim has elected to drop the dual-target burden.

The post-port distribution shape:

- **One build artifact:** a signed, packed `lambda-boss64.xll` (~3–4 MB) with all managed dependencies (`lambda-boss.dll`, `Taglo.Excel.Common.dll`, `YamlDotNet.dll`, `GongSolutions.WPF.DragDrop.dll`, `Ookii.Dialogs.Wpf.dll`) embedded as Win32 resources via ExcelDNA's `Pack="true"`.
- **Two distribution channels** built around the same XLL:
  1. **Installer** (existing flow, simplified): InnoSetup `.exe` that copies the packed XLL to `%LOCALAPPDATA%\LambdaBoss\` and sets the `OPEN` registry key for autoload. No bundled runtime, no loose DLLs, no `.dna` or `deps.json` side-cars.
  2. **Tournament zip:** the same packed XLL plus a README and `unblock.cmd`, zipped as `LambdaBoss-Tournament-x.y.z.zip`. Tim copies to a USB stick, loads on the tournament laptop via the Excel Add-Ins dialog or by double-clicking.

### Project changes

#### `addin/lambda-boss/lambda-boss.csproj`

- `<TargetFramework>net6.0-windows</TargetFramework>` → `<TargetFramework>net48</TargetFramework>`.
- `<UseWPF>true</UseWPF>` — keep; supported on net48 via the modern SDK.
- `<ExcelDna64Bit>true</ExcelDna64Bit>` — keep.
- Add `<LangVersion>latest</LangVersion>` so file-scoped namespaces and other C# 10+ features continue to compile (the codebase uses them heavily — 69 files).
- Add `System.Text.Json` `PackageReference` (the two callers — `GitHubSource.cs`, `Settings.cs` — pick it up from the framework on net6 but need an explicit NuGet on net48).
- Verify other `PackageReference`s carry net48 targets (see § Dependencies).

#### `addin/lambda-boss.Tests/lambda-boss.Tests.csproj`

- Target net48. `xunit`, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio` versions in use (2.6.x / 17.8.0) all support net48 — no upgrade needed.

#### `addin/lambda-boss.AddinTests/lambda-boss.AddinTests.csproj`

- Same as above, plus the explicit `YamlDotNet 16.3.0` reference (already used).

#### `Directory.Build.props`

- No changes expected (only contains `<Version>`).

### Dependencies — net48 compatibility check

The implementation plan owns the work of verifying each dependency on net48 before code changes start. Quick triage based on public package metadata:

| Package | Current version | Net48 support |
|---|---|---|
| `ExcelDna.AddIn` | 1.* (resolves 1.9.0) | ✅ Explicitly supports net462+; net48 is the recommended target |
| `YamlDotNet` | 16.* | ✅ Multi-targets including netstandard2.0 (net48-compatible) |
| `gong-wpf-dragdrop` | 3.2.* | ⚠️ Needs verification — historically supported net4x, but recent versions may drop it |
| `Ookii.Dialogs.Wpf` | 5.0.1 | ⚠️ Needs verification — recent versions target net6/net8 only |
| `Taglo.Excel.Common` | 0.1.* | ⚠️ Currently published as net6-only; needs to be multi-targeted to add net48 |
| `System.Text.Json` | new | Add at a version supporting netstandard2.0 (8.x or 9.x) |

`Taglo.Excel.Common` is the one we own — multi-targeting it to `netstandard2.0` (which net48 can consume) is the cleanest approach and is tracked as an open question below. The two third-party packages above may need a version downgrade or a substitution if their recent releases dropped net4x.

### Code-level changes

A scan of the codebase found:

- **No `record` types, no `with` expressions, no `init`-only setters** — language features that need C# 9+ but compile fine on net48 with modern SDK + `<LangVersion>latest>`.
- **No `IAsyncEnumerable`** — would have needed `Microsoft.Bcl.AsyncInterfaces`.
- **`System.Text.Json`** in `GitHubSource.cs` and `Settings.cs` — covered by NuGet package.
- **File-scoped namespaces** in ~69 files — C# 10 syntax, works on net48 with `<LangVersion>latest>`.
- **Nullable reference types** assumed throughout — `<Nullable>enable</Nullable>` works on net48.

Any net6-specific BCL surface (e.g. `HttpClient` defaults, `DateOnly`, `TimeOnly`, `string.Contains(char)`) will surface as a compile error on first net48 build; the plan addresses these as they appear rather than enumerating upfront.

### Installer changes — `installer/lambda-boss.iss`

- `[Files]` collapses to **just the packed XLL** (`lambda-boss64-packed.xll` renamed to `lambda-boss64.xll` for install destination), the icon, and the LICENSE. Delete all loose-DLL entries, `.dna`, `deps.json`, `runtimeconfig.json` entries.
- Delete the entire `bundled-runtime` mechanism: drop the `[Files]` entry for the runtime installer, the `[Run]` install step, and the `IsDotNet6Installed` `[Code]` function. With net48 we assume Windows itself provides the runtime.
- `installer/bundled-runtime/` directory and its README can be deleted.
- Keep the `OPEN` registry key autoload, the Excel-closed precondition, and the uninstall cleanup — they are not framework-dependent.
- Update the `BuildOutput` `#define` to point at the packed XLL location (`bin\Release\net48\publish\` rather than `bin\Release\net6.0-windows\`).

### Tournament zip — new `scripts/build-tournament-bundle.ps1`

Logically straightforward now that the runtime question is gone:

1. Locate the signed `lambda-boss64-packed.xll` from the regular build.
2. Stage into `installer/output/tournament-staging/`: rename to `lambda-boss64.xll`, drop in a `README.txt` and `unblock.cmd`.
3. Zip as `LambdaBoss-Tournament-x.y.z.zip` alongside the installer.

The README documents the two load paths from spec 0006 (Add-Ins dialog vs double-click). The `unblock.cmd` is a one-liner that runs `powershell -Command "Get-ChildItem -Recurse | Unblock-File"` to clear `Zone.Identifier` ADS from USB-copied files.

### `scripts/publish-release.ps1` changes

- Drop the bundled-runtime preflight check.
- Add a step that calls `build-tournament-bundle.ps1` after the XLL is signed.
- Attach the tournament zip to the GitHub Release alongside the installer.

### `.dna` change

`addin/lambda-boss/lambda-boss.dna` flips `Pack="false"` to `Pack="true"` so the main `lambda-boss.dll` is packed into the XLL like the rest of the managed deps. (Currently `Pack="false"` because the installer expected a loose `lambda-boss.dll`; that's no longer true.)

### Build/test plan during migration

The migration is one PR. The order of operations inside the PR:

1. Retarget the three project files to net48. Build will fail.
2. Add `System.Text.Json` NuGet and any other missing-API NuGets.
3. Resolve any compile errors from net6-specific API usage.
4. Verify dependencies all resolve. If `gong-wpf-dragdrop` or `Ookii.Dialogs.Wpf` or `Taglo.Excel.Common` block on net48 incompatibility, address per § Open Questions.
5. `dotnet build addin/lambda-boss.slnx` clean, `dotnet test addin/lambda-boss.Tests/lambda-boss.Tests.csproj` clean.
6. Manual smoke test of the packed XLL by double-click load in a real Excel: open Lambda Boss popup, evaluate a LAMBDA, exercise `/Gather` and `/Edit` slash commands.
7. Update `installer/lambda-boss.iss` and rebuild the installer; verify install + Excel autoload.
8. Add `scripts/build-tournament-bundle.ps1`, the README, and `unblock.cmd`.
9. Update `scripts/publish-release.ps1`.
10. Update `CLAUDE.md`, `installer/bundled-runtime/README.md` (delete), and any other docs that reference .NET 6.

### Verification

Final acceptance verification, done by Tim:

- **Installer path:** clean Windows 11 + Office 365 VM. No .NET 6 runtime present. Run installer → Excel auto-loads Lambda Boss on next start → popup opens → a LAMBDA function evaluates.
- **Tournament path:** same VM, no installer flow. Copy `LambdaBoss-Tournament-x.y.z.zip` from USB → unblock → load via Excel Add-Ins dialog → popup opens → LAMBDA evaluates → all slash commands work.
- **Double-click path:** double-click the XLL → Excel opens with Lambda Boss enabled for that session.

## User Stories

- As Tim preparing for a competitive Excel tournament, I want a single signed XLL file I can carry on a USB stick, so that I can install Lambda Boss on a provided laptop with no internet, no admin rights, and no installer.
- As Tim on the day of the tournament, I want the install flow to match the muscle memory I already have for OARobot, so that I am not learning a new procedure in a 15-30 minute pre-battle window.
- As Tim shipping daily releases, I want the regular installer to stop bundling a 50 MB .NET runtime installer, so that Lambda Boss downloads and installs faster.
- As Tim maintaining Lambda Boss, I want a single build target rather than two, so that I'm not testing every release against two .NET surfaces.
- As a future Taglo user in a locked-down corporate Excel environment, I want a single-file XLL with no install prerequisites, so that I can use Lambda Boss without IT involvement.

## Acceptance Criteria

- [ ] `addin/lambda-boss/lambda-boss.csproj`, `lambda-boss.Tests.csproj`, and `lambda-boss.AddinTests.csproj` target `net48`.
- [ ] `dotnet build addin/lambda-boss.slnx` succeeds with no warnings introduced by the retargeting.
- [ ] `dotnet test addin/lambda-boss.Tests/lambda-boss.Tests.csproj` passes (all existing tests green).
- [ ] The build produces a signed, packed `lambda-boss64.xll` as the **sole runtime artifact**. No loose `lambda-boss.dll`, `*.deps.json`, or `*.runtimeconfig.json` ships in either distribution.
- [ ] On a clean Windows 11 + Office 365 VM with no .NET 6 runtime present, the installer installs Lambda Boss and Excel auto-loads it on next start with full functionality.
- [ ] On the same VM, the tournament zip (`LambdaBoss-Tournament-x.y.z.zip`) loads via the Add-Ins dialog and via double-click, with full functionality.
- [ ] `scripts/publish-release.ps1` produces both the installer and the tournament zip in a single end-to-end release run, and both are attached to the GitHub Release draft.
- [ ] `installer/bundled-runtime/` is deleted; `installer/lambda-boss.iss` no longer references the .NET 6 runtime.
- [ ] `CLAUDE.md` and any other docs are updated to reflect net48 as the build target.

## Out of Scope

- **Formula Boss .NET Framework port.** Tracked separately if/when needed. Formula Boss's `AssemblyLoadContext` / Roslyn type-identity machinery is a meaningfully different migration.
- **Shared loader infrastructure in `taglo-excel-common`.** If multi-targeting `Taglo.Excel.Common` is needed (see open questions), the change is local to that package — no new shared infrastructure.
- **NativeAOT.** Future possibility per ExcelDNA docs; out of scope.
- **Multi-version Office testing matrix.** Existing matrix (Office 365 with `REGEX` support) remains the verification target.
- **Cross-add-in runtime conflicts.** Under .NET Framework, multiple ExcelDNA add-ins coexist without runtime-version concerns, so this is implicitly resolved rather than explicitly addressed.

## Open Questions

- **`Taglo.Excel.Common` multi-targeting.** Currently published as net6-only. Three options: (a) multi-target it to net48 + net6.0-windows in its repo, republish, consume the net48 RID here; (b) drop it as a dependency and inline whatever Lambda Boss uses from it; (c) net48-only fork. (a) is preferred but requires a coordinated change in the `taglo-excel-common` repo. The implementation plan should start with a quick audit of how much Lambda Boss actually uses from this package, since option (b) becomes attractive if usage is shallow.
- **`gong-wpf-dragdrop` and `Ookii.Dialogs.Wpf` net48 compatibility.** Both have been published continuously; recent majors may have dropped net4x. The implementation plan should pin to whichever version still supports net48 (likely an older minor than the current `3.2.*` and `5.0.1` floors). If no acceptable version exists, candidates are: in-house drag-and-drop helper (gong) and `Microsoft.Win32.OpenFileDialog`-based replacement (Ookii) — both modest.
- **InnoSetup installer retention.** With a single packed XLL the installer is now ~10 lines of `[Files]` + the autoload registry key. We could deprecate the installer in favour of the zip distribution + a documented "drop into `%APPDATA%\Microsoft\AddIns\` and tick in Excel" flow. The current proposal keeps it as a convenience for the daily-driver case; revisit if maintenance cost outweighs the benefit. Decision deferred to the implementation plan once the new installer is built and we see what's left.
- **`Settings.cs` `JsonSerializerOptions`.** The current code likely uses some net6 JSON convenience APIs; the implementation plan should verify the `System.Text.Json` NuGet (which works on net48) exposes the same surface, or adjust code accordingly.
