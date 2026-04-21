# Bundled .NET 6 Desktop Runtime

Drop the .NET 6 Desktop Runtime installer here before running `scripts/publish-release.ps1`.

## Expected filename

```
windowsdesktop-runtime-6.0.<patch>-win-x64.exe
```

For example: `windowsdesktop-runtime-6.0.36-win-x64.exe`

## Download

<https://dotnet.microsoft.com/en-us/download/dotnet/6.0>

Pick the **Desktop Runtime** (not the SDK, not the ASP.NET Core runtime) for **Windows x64**.

## Why this isn't committed

The runtime installer is ~60 MB and Microsoft publishes new patch releases regularly. Pulling a fresh copy at release time keeps the repo small and ensures we ship with an up-to-date runtime.

## Keep the version in sync

`installer/lambda-boss.iss` has a hardcoded reference to the exact filename in the `[Run]` section:

```
Filename: "{tmp}\windowsdesktop-runtime-6.0.36-win-x64.exe"; ...
```

If you drop a different patch version here, update that line to match. Otherwise the installer will skip the runtime install step silently.
