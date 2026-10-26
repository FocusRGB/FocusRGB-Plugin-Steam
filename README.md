# FocusRGB Steam plugin

This official [FocusRGB](https://github.com/FocusRGB/FocusRGB) discovery plugin reads the current
Windows user's local Steam library metadata and app manifests. It returns installed games and the
file names of executables found below each game's installation directory through the FocusRGB 1.2
`catalog/list(game)` contract.

The plugin does not start Steam, launch games, inspect running processes or memory, collect
telemetry, or control lighting. Local installation paths never leave the plugin. Its manifest
therefore declares only `filesystem.read`.

Catalog requests share one background scan with a three-second response budget, below the host's
five-second timeout. The scan continues to completion after the caller stops waiting. Slow scans
return cached or partial games with `catalog-scan-incomplete` and no fresh timestamp. The last
complete catalog is saved atomically as `catalog-cache.json` in `FOCUSRGB_PLUGIN_DATA_DIRECTORY`
and restored on cold startup, merged with fresh results until a full scan reconciles removals.
Unreadable library metadata retains cached games rather than declaring them uninstalled.
Cancellation stops waiting immediately, and plugin shutdown cancels the shared scan. A blocked
filesystem call cannot be forcibly interrupted, but it never spawns duplicate scans or holds up
a catalog response beyond its budget.

## Build and test

`FocusRgb.PluginSdk` 1.2.0 restores from nuget.org without repository credentials:

```powershell
dotnet restore FocusRGB-Plugin-Steam.slnx
dotnet build FocusRGB-Plugin-Steam.slnx -c Release --no-restore --disable-build-servers
$env:MSBUILDDISABLENODEREUSE = "1"
dotnet test --solution FocusRGB-Plugin-Steam.slnx -c Release --no-build
dotnet build-server shutdown
```

Tests use synthetic Steam library and app-manifest data. Do not commit data copied from a user's
Steam installation.

## Releases and discovery

The repository must use the `focusrgb-plugin` GitHub topic for official discovery. Each stable
release publishes one installable zip, the identical root `focusrgb-plugin.json`, and
`SHA256SUMS` covering both assets. Packaging changes run the same path as a dry run in pull
requests; tags and releases are owner-only.

Assisted-by: OpenAI Codex
