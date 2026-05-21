# 0006 — Self-contained tournament build

> **Status: Superseded by spike findings (2026-05-21).** See [Spike outcome](#spike-outcome) below. The proposed approach is architecturally impossible. Superseded by spec [0007 — .NET Framework 4.8 port for tournament-ready single-file XLL](0007-net-framework-port.md).

## Problem

Lambda Boss is currently distributed as an InnoSetup installer (`LambdaBoss-x.y.z-Setup.exe`) that copies an XLL plus ~5 side-car DLLs and 3 config JSONs into `%LOCALAPPDATA%\LambdaBoss\`, sets an `OPEN` registry key under `HKCU\Software\Microsoft\Office\16.0\Excel\Options` so Excel autoloads the add-in, and silently installs the .NET 6 Desktop Runtime if it isn't already present. This is the right shape for normal users on their own machines.

It does not work for competitive Excel tournaments. At a tournament like the MEWC European Open, competitors are given laptops they did not previously have access to and must set up their environment in a 15-30 minute pre-battle window. They can bring files on a USB stick but cannot run installers, cannot assume admin rights, cannot assume the .NET 6 Desktop Runtime is present, and cannot assume any network access. The OARobot creator has confirmed that **a packed XLL with no installer prerequisite** is the accepted distribution shape at MEWC — competitors copy the XLL into a folder of their choosing (OARobot users typically use `Documents\OA Robot\`) and either double-click it to launch Excel with the add-in temporarily enabled, or load it persistently through Excel's Add-Ins dialog. To run multiple add-ins (e.g. OARobot alongside Lambda Boss) in the same Excel session, the Add-Ins dialog flow is required.

The existing Lambda Boss installer fails this test on three counts:

1. It requires running an `.exe` installer.
2. It assumes the .NET 6 Desktop Runtime is either present or can be installed.
3. It ships the XLL alongside loose DLLs, which makes USB distribution clumsier than it needs to be — a competitor has to keep a folder structure intact rather than carry a single file.

We need a second distribution artifact suitable for tournament use: a single self-contained `.xll` file that loads in Excel with no further dependencies.

## Proposed Solution

Produce a **tournament build** of Lambda Boss as a parallel artifact to the regular installer. The tournament build is a single `lambda-boss64.xll` file that:

- **Embeds the .NET 6 Desktop Runtime** via ExcelDNA's self-contained publish path (`<SelfContained>true</SelfContained>` + `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`).
- **Packs all managed dependencies** (`lambda-boss.dll`, `Taglo.Excel.Common.dll`, `YamlDotNet.dll`, `GongSolutions.WPF.DragDrop.dll`, `Ookii.Dialogs.Wpf.dll`) into the XLL via ExcelDNA's `Pack="true"` mechanism.
- **Is code-signed** with the same Sectigo certificate the regular installer uses.

The resulting artifact is a single file in the 50-100 MB range that a competitor can carry on a USB stick, copy onto a tournament laptop, unblock if necessary, and load into Excel.

### Distribution shape

The tournament artifact is a small zip file (`LambdaBoss-Tournament-x.y.z.zip`) containing:

- `lambda-boss64.xll` — the signed, self-contained, packed XLL.
- `README.txt` — load instructions.
- `unblock.cmd` — a one-line helper that runs `powershell -Command "Get-ChildItem -Recurse | Unblock-File"` to clear `Zone.Identifier` ADS from USB-copied files. Optional; the README explains the manual right-click → Properties → Unblock alternative.

The README documents two load paths:

1. **Add-Ins dialog (default, recommended for tournament use):** copy the XLL into a folder of the competitor's choice (e.g. `Documents\Lambda Boss\`), then Excel → File → Options → Add-Ins → Manage: Excel Add-Ins → Go → Browse → tick. Loads persistently for that Excel user profile, and works alongside OARobot or any other add-in.
2. **Double-click (fast path, single session):** double-click the XLL to launch Excel with Lambda Boss enabled for that session only. Quicker but mutually exclusive with other double-click-loaded add-ins.

No registry edits, no `OPEN` key autoload, no PowerShell install script. Add-in registration is handled through Excel's standard UI — the same flow competitors already use for OARobot.

### Build-time `.dna` handling

The canonical `lambda-boss.dna` continues to use `Pack="false"` for compatibility with the regular installer's loose-DLL layout. The tournament build script generates a derived `.dna` at build time by copying the canonical file and flipping the `Pack` attribute to `"true"`. The derived `.dna` is a build output, not a checked-in file. This keeps a single source of truth (no risk of drift when managed dependencies change) while allowing the two builds to differ in packing behaviour.

### Build integration

The tournament artifact is produced by a new script `scripts/build-tournament-bundle.ps1` that:

1. Invokes `dotnet publish` on the addin project with self-contained + single-file settings.
2. Code-signs the resulting XLL.
3. Stages the XLL, README, and `unblock.cmd` into an output folder.
4. Zips the folder.
5. Drops the zip alongside the regular installer in `installer/output/`.

The existing `scripts/publish-release.ps1` is extended to call this script as an additional step, so a release produces both artifacts. The regular installer path is untouched — the tournament build is an addition, not a replacement.

### Verification

Before declaring the tournament build "tournament-ready," Tim verifies it manually on a clean Windows 11 + Office 365 virtual machine with:

- No .NET 6 Desktop Runtime installed.
- No admin rights on the test user account.
- Default Trust Center settings.
- The XLL copied via USB (or virtual USB), retaining the `Zone.Identifier` ADS that a real tournament transfer would produce.

The verification must demonstrate the full happy-path flow — copy → unblock → load via Excel Add-Ins dialog → exercise core Lambda Boss functionality — by a user following only the bundled README.

## User Stories

- As Tim preparing for a competitive Excel tournament, I want a single signed XLL file I can carry on a USB stick, so that I can install Lambda Boss on a provided laptop with no internet, no admin rights, and no installer.
- As Tim on the day of the tournament, I want the install flow to match the muscle memory I already have for OARobot, so that I am not learning a new procedure in a 15-30 minute pre-battle window.
- As Tim post-tournament, I want the tournament build to remain a normal product of our release process, so that future tournaments do not require bespoke prep.
- As a future Taglo user in a locked-down corporate Excel environment, I want a single-file XLL with no install prerequisites, so that I can use Lambda Boss without IT involvement.

## Acceptance Criteria

- [ ] `scripts/build-tournament-bundle.ps1` produces a `LambdaBoss-Tournament-x.y.z.zip` containing the signed self-contained XLL, a README, and `unblock.cmd`.
- [ ] The produced XLL is a single file, code-signed with the existing Sectigo certificate, and contains no loose side-car DLLs in the bundle.
- [ ] On a clean Windows 11 + Office 365 VM with no .NET 6 Desktop Runtime installed and no admin rights, following only the README, the XLL loads in Excel via the standard Add-Ins dialog.
- [ ] The double-click load path also works on the same VM (single-session enablement, faster path).
- [ ] All Lambda Boss functionality available in the regular installer build is also available in the tournament build (popup window, LAMBDA library, all existing slash commands, the gather-cells-to-LET command from spec 0005 if shipped, etc.).
- [ ] `scripts/publish-release.ps1` produces both the regular installer and the tournament zip in a single end-to-end release run.
- [ ] The XLL size is documented and is under 150 MB (a sanity bound — actual size expected to be 50-80 MB).

## Out of Scope

- **Formula Boss tournament build.** Formula Boss has architectural constraints around `AssemblyLoadContext` and Roslyn type identity that make a self-contained / packed build more involved. Out of scope here; covered separately if and when needed.
- **.NET Framework 4.8 port of Lambda Boss.** A longer-term strategic option to eliminate the runtime question entirely. Out of scope; tracked as a possible future spec.
- **Shared loader infrastructure in `taglo-excel-common`.** If both Lambda Boss and Formula Boss eventually need tournament builds, common packaging logic can be hoisted. Out of scope for this spec; revisit when the second add-in needs the same treatment.
- **Autoload via registry / Trusted Locations.** Compelling for a daily-driver user but unnecessary for the tournament use case, where competitors load the XLL once per session via the Add-Ins dialog (same as OARobot). Adds complexity, no tournament benefit.
- **Multi-version Office support testing.** The tournament target is Office 365 (recent enough to support `REGEX`). Excel 2019 / 2021 compatibility is not regressed but is not part of the verification matrix.

## Open Questions

- **Self-contained publish + ExcelDNA AddIn package interaction.** ExcelDNA's `PublishExcelDnaAddIn` MSBuild target is the supported path for producing a packed XLL, but its interaction with `<SelfContained>true</SelfContained>` + `<PublishSingleFile>true</PublishSingleFile>` needs verification — there are known edge cases around how the embedded runtime sits inside the XLL. The implementation plan should begin with a short spike that confirms a hello-world Lambda Boss build loads as a single self-contained XLL on the verification VM before any further build-script work. If the spike uncovers blockers, the spec gets revisited before the rest of the plan proceeds.

## Spike outcome

The spike called out in the open question above (issue #190, branch `issue-190-spike-self-contained-xll`, 2026-05-21) **uncovered a blocker**. The proposed approach is not implementable — ExcelDNA architecturally precludes self-contained / single-file publish for .NET 6+ XLL add-ins.

### Empirical evidence

ExcelDNA.AddIn 1.9.0, .NET 6 (runtime 6.0.36 present locally).

**Attempt 1 — full proposed combo:**

```
dotnet publish addin/lambda-boss/lambda-boss.csproj -c Release \
  -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Fails with:

> `error NETSDK1099: Publishing to a single-file is only supported for executable applications.`

`PublishSingleFile` requires `<OutputType>Exe</OutputType>`. Lambda Boss is a class library — the XLL itself is a native loader copied from ExcelDNA's NuGet tools folder, with managed assemblies appended as Win32 resources. There is no managed entry point for the apphost-bundle mechanism to wrap. Hard .NET SDK constraint, not configurable.

**Attempt 2 — drop `PublishSingleFile`, keep self-contained:**

```
dotnet publish addin/lambda-boss/lambda-boss.csproj -c Release \
  -r win-x64 --self-contained true
```

Succeeds, but produces 287 files totalling 156 MB with **no `.xll` file produced**. ExcelDNA's `PublishExcelDnaAddIn` / `PackExcelAddIn` targets do not integrate with `dotnet publish` in self-contained mode — the publish output is plain managed DLLs + native runtime, with no native XLL bootstrapper.

### Primary-source confirmation

ExcelDNA author Govert van Drimmelen on [Discussion #607](https://github.com/Excel-DNA/ExcelDna/discussions/607):

> We don't support the bring-your-own-runtime option of .NET Core, because the add-in isolation mechanism we use (the AssemblyLoadContext etc.) doesn't support that. … only one version of a .NET Core runtime can be loaded into the Excel process.

Official [ExcelDNA .NET runtime support docs](https://excel-dna.net/docs/guides-basic/dotnet-runtime-support/):

> If your add-in will be distributed outside your organization, or on machines that you don't have much control over, then I would suggest targeting .NET Framework.

The [ExcelDNA 1.9.0 release notes](https://excel-dna.net/docs/release-notes-1-9/) introduce extended registration, async, object handles, and native-dependency packing — but no self-contained / single-file publish support.

### Why ExcelDNA architecturally precludes this

XLL files are native DLLs loaded by Excel via the XLL Manager. ExcelDNA's loader (`ExcelDna64.xll`) is a small native bootstrapper that locates an installed .NET runtime via the standard hostfxr resolution path, then hosts CoreCLR in-process. The .NET 6 single-file bundler is built around `apphost.exe` and a managed entry-point — neither concept maps onto a native XLL. The `AssemblyLoadContext`-based isolation ExcelDNA uses to keep multiple add-ins from conflicting also depends on a single shared runtime in the process.

### Decision

This spec is **superseded** by a .NET Framework 4.8 port. Under .NET Framework, the packed XLL produced by `ExcelDnaPack` is a true single-file artifact because the .NET Framework runtime ships as part of Windows — no runtime prerequisite, no self-contained machinery needed. Tim has elected to replace the current .NET 6 build entirely with a net48 build rather than maintain both in parallel.

See spec [0007 — .NET Framework 4.8 port](0007-net-framework-port.md). The sub-issues attached to this spec (#190–#194) are closed; new issues track the port work.
