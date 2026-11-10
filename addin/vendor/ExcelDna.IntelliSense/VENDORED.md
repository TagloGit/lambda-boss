# ExcelDna.IntelliSense — vendored and patched

- **Upstream:** https://github.com/Excel-DNA/IntelliSense, `Source/ExcelDna.IntelliSense` at tag
  `v1.9.0` (commit `cedaf76`), the code behind NuGet `ExcelDna.IntelliSense` 1.9.0.
- **Licence:** MIT, `LICENSE.txt` beside this file (copyright Govert van Drimmelen and the
  Excel-DNA contributors). Keep it with the code.
- **Why vendored:** lambda-boss#433. With the server installed at startup, opening Office's
  account/profile switcher crashed Excel in `hermes.dll` (React Native's JS engine, access
  violation), every time, on Tim's PC (Excel 16.0.20701.20002 x64). The server can't be scoped
  through its API, so the fix is a patch.

## Changes from upstream

Every change is marked `LAMBDA BOSS PATCH` in the code.

1. **No loader-lock DLL notification.** `Providers/LoaderNotification.cs` is deleted, and
   `ExcelDnaIntelliSenseProvider` no longer calls `LdrRegisterDllNotification`. Upstream ran
   managed code under the loader lock, before dynamic linking, on whatever thread loaded a DLL,
   including Office's own threads loading `hermes.dll` and React Native for the flyout (upstream's
   own comment: "WARNING! LoaderLock danger here"). It only served to notice `.xll` add-ins loaded
   after the server started. `Refresh()` now re-enumerates loaded modules instead (`AddLoadedXlls`,
   main thread, macro context), registering new XLLs and forgetting unloaded ones.
2. **WinEvent hooks on Excel's main thread only.** `UIMonitor/WinEvents.cs` passes the main
   thread's id to `SetWinEventHook` instead of 0 (every thread in the process). The windows the
   server watches (formula bar `EXCEL<`, in-cell editor `EXCEL6`, function list `__XLACOOUTER`,
   `XLToolTip`, `XLMAIN`) all belong to that thread. The out-of-context callback always ran on
   the main thread; what changes is that events from Office's other threads are no longer
   delivered to it (nor their windows queried with `GetClassName`), and no hook applies to those
   threads. This one is defence in depth: patch 1 is the one that fits Tim's bisect.
3. **Server version `1.9.0.433`** (`IntelliSenseServer.ServerVersion`), so that beside another
   add-in carrying the stock 1.9.0 server, this patched one is the one that runs (the
   co-operative scheme activates the highest version). A future upstream release ranks above it.
4. **Project file** targets net48 only, unsigned, with this folder's `Directory.Build.props`
   shielding it from the repo's analyzers and global usings.

UI Automation isn't involved: 1.9.0 makes no UI Automation calls (WinEvents and Win32 messages
to Excel's own edit windows only).

**Confidence: inferred, not reproduced.** Patch 1 fits Tim's bisect (`hermes.dll` loads once per
session, so a server installed after the first flyout never sees it load), but the VM's Excel
(16.0.20527, one account) builds its account card without `hermes.dll` and never crashed, stock
or patched. Tim's PC is the proof.

## Limits

- **Add-ins loaded later.** Lambda Boss refreshes the server once Excel is idle after startup
  (`LambdaHints.StartIfEnabled`) and whenever its hints file changes. An Excel-DNA add-in loaded
  by hand later in the session gets its hints at the next of those refreshes, not at once.
- **When this server isn't the one running**, the patch doesn't apply: hints turned off (another
  add-in's stock server may take over), or a stock server of a higher version (1.9.1 or later)
  in another add-in. Lambda Boss carries no other.
- A `DisabledVersions` setting of exactly `1.9.0` doesn't disable `1.9.0.433`; `1.9.*` does.
- Focus moving into a window on another Office thread is no longer seen, so an argument tooltip
  may stay up while a flyout or pane on another thread has focus mid-edit (cosmetic).

## Updating

Diff upstream against the tag above, re-apply the marked patches, and bump `ExcelDna.AddIn` in
`lambda-boss.csproj` to the `ExcelDna.Integration` version this project references.
