<p align="center">
  <img src="assets/icon/AppIcon.svg" width="128" alt="AppManager icon" />
</p>

<h1 align="center">AppManager</h1>

<p align="center">
  Find out which apps are really eating your disk space on Windows.
</p>

---

Windows apps scatter data all over your drives: dot-folders in your home directory (`.gradle`, `.cargo`, `.ollama`, ...), `AppData\Local`, `AppData\Roaming`, `LocalLow`, `ProgramData`, `Documents`, and of course their install directories. Classic disk analyzers show you *where* the bytes are, but not *who* put them there.

AppManager scans those locations and **attributes every folder to an installed program**, so you get one total per app: install size plus all of its data folders, with the app's real icon.

AppManager only reads and analyzes. It never deletes or moves anything.

## Features

- **Per-app totals**: install directory + AppData + home-directory + Documents folders combined, split into *install* and *data*, with a per-drive breakdown.
- **Covers all fixed drives**: games on `D:` and their saves on `C:` show up under the same app.
- **Installed-app inventory** from the registry (HKLM 64/32-bit, HKCU) and MSIX packages (`PackageManager`), including install-location recovery via `DisplayIcon`, `UninstallString`, App Paths and Start Menu shortcuts. The registry `EstimatedSize` is used as a fallback when no install folder can be measured.
- **Attribution engine** with confidence levels (high / medium / low) and a visible reason for every match:
  - MSIX package data (`Local\Packages\<PackageFamilyName>`)
  - Install-location equality or containment
  - Built-in rules for ~90 well-known tools and caches (Gradle, Maven, NuGet, Rust, Node.js, Python/uv, Docker, Ollama, JetBrains, VS Code, NVIDIA shader caches, WeChat/QQ files, ...), so CLI tools that never show up in "Installed apps" are still identified
  - Name heuristics against app name, publisher, install-folder name and executable name
- **Unattributed folders** view: leftovers from uninstalled apps, portable tools, etc. Folders untouched for 180+ days are flagged as *possible leftovers*.
- **Safe scanning**: junctions and symlinks are not followed (no loops, no double counting), and cloud placeholder files (OneDrive "online-only") count as 0 bytes.
- **CSV export** of the current view, **custom scan directories**, light/dark theme.
- The last scan result is cached locally, so the app opens with the last scan already shown.

## Requirements

- Windows 10 version 1809 (build 17763) or later, x64 / x86 / ARM64
- To build: [.NET 11 SDK](https://dotnet.microsoft.com/), Windows App SDK 2.7 (restored from NuGet), and optionally Visual Studio 2026 with the *WinUI application development* workload

## Build and run

```powershell
# Build
dotnet build AppManager.slnx -p:Platform=x64

# Run tests
dotnet test tests/AppManager.Core.Tests

# Run the packaged app (registers a development package with identity)
dotnet run --project src/AppManager -p:Platform=x64
```

In Visual Studio: open `AppManager.slnx`, set **AppManager** as the startup project, pick `Debug | x64` and the **AppManager (Package)** launch profile, then press F5.

### Debug-only environment variables

| Variable | Effect |
|---|---|
| `APPMANAGER_AUTOSCAN=1` | Start a scan on launch |
| `APPMANAGER_START_PAGE` | `overview`, `apps`, `unattributed` or `settings` |
| `APPMANAGER_SELECT_KEY` | Pre-select an app group on the Apps page by its key |

## Project layout

```
AppManager.slnx
src/
  AppManager.Core/        Scanning, inventory and matching (no UI dependencies)
    Inventory/            Registry + MSIX app sources, install-location fallbacks
    Scanning/             Folder discovery, size measurement, grouping, CSV, persistence
    Matching/             AppMatcher, NameNormalizer, KnownTools.json rules
  AppManager/             WinUI 3 app (pages, view models, services)
tests/
  AppManager.Core.Tests/  xUnit tests for the core library
assets/icon/              Icon source SVGs and the asset render script
```

### Adding a known-tool rule

Rules live in `src/AppManager.Core/Matching/KnownTools.json`:

```json
{
  "id": "gradle",
  "displayName": "Gradle",
  "folders": [".gradle"],
  "scopes": ["UserHomeDot"],
  "keywords": ["Gradle", "Android Studio"],
  "description": "Gradle 构建缓存与依赖",
  "category": "DevTool"
}
```

- `folders`: folder names, matched case-insensitively.
- `scopes`: optional; limits the rule to certain locations (empty means any location).
- `parent`: optional; the rule only applies when the folder sits under a parent folder with this name.
- `keywords`: used to attach the folder to a matching installed app. With no keywords, or no matching app, the folder shows up as a standalone "known tool" group.

## Privacy

Everything runs locally. AppManager does not send scan results or file paths anywhere. The app talks to the network only through the Microsoft Store licensing APIs, and that code is not wired up yet.

## Microsoft Store

The app is packaged as MSIX (single-project packaging) and runs as a full-trust desktop app. It needs no restricted capabilities beyond `runFullTrust`. A Store licensing service based on `Windows.Services.Store.StoreContext` lives in `src/AppManager/Services/LicenseService.cs`. Paid add-ons are currently disabled; to enable them later, set `StoreConfig.ProAddOnStoreId` to the add-on's Store ID from Partner Center.

## Status

Early development. The UI is currently Simplified Chinese only.
