<p align="center">
  <img src="assets/icon/AppIcon.svg" width="128" alt="AppManager icon" />
</p>

<h1 align="center">AppManager</h1>

<p align="center">
  Find out which apps are really eating your disk space on Windows.
</p>

---

Windows apps scatter data all over your drives: dot-folders in your home directory (`.gradle`, `.cargo`, `.ollama`, ...), `AppData\Local`, `AppData\Roaming`, `LocalLow`, `ProgramData`, `Documents`, and of course their install directories. Classic disk analyzers show you *where* the bytes are, but not *who* put them there.

AppManager scans those locations and **attributes every folder to an installed program**, so you get one total per app: program files plus all of its data folders, with the app's real icon. Dedicated views break down what AI coding tools and developer toolchains leave behind.

Currently AppManager only reads and analyzes. It doesn't provide uninstallation or junk file cleanup feature.

## Features

- **Per-app totals**: program folder + AppData + home-directory + Documents folders combined, split into *program files* and *data*, with a per-drive breakdown.
- **Covers all fixed drives**: games on `D:` and their saves on `C:` show up under the same app.
- **Installed-app inventory** from the registry (HKLM 64/32-bit, HKCU) and MSIX packages (`PackageManager`), including install-location recovery via `DisplayIcon`, `UninstallString`, App Paths and Start Menu shortcuts. The registry `EstimatedSize` is used as a fallback when no install folder can be measured.
- **Attribution engine** with confidence levels (high / medium / low) and a visible reason for every match:
  - MSIX package data (`Local\Packages\<PackageFamilyName>`)
  - Install-location equality or containment
  - Built-in rules for ~90 well-known tools and caches (Gradle, Maven, NuGet, Rust, Node.js, Python/uv, Docker, Ollama, JetBrains, VS Code, NVIDIA shader caches, WeChat/QQ files, ...), so CLI tools that never show up in "Installed apps" are still identified
  - Name heuristics against app name, publisher, install-folder name and executable name
- **Unattributed folders** view: leftovers from uninstalled apps, portable tools, etc. Folders untouched for 180+ days are flagged as *possible leftovers*.
- **AI tools** view: state left by AI coding agents, AI IDEs and desktop chat apps (Claude Code, Codex, Antigravity, opencode, Cursor, ChatGPT, ...), split into conversations, snapshots, caches, logs, extensions and old versions. Only the current user's profile is scanned, and the tools' current program files are excluded from the totals.
- **Developer tools** view: SDKs and runtimes listed per version (.NET, JDKs, Node.js, Python, Rust, Go, Android SDK, Windows SDK, ...), package caches (NuGet, npm, pnpm, Yarn, pip, uv, Cargo, Gradle, Maven, ...), build caches, global packages and AI model caches (Hugging Face, Ollama, ...). Relocated caches are found through environment variables (`NUGET_PACKAGES`, `GRADLE_USER_HOME`, `CARGO_HOME`, ...) and config files (`.npmrc`, pnpm `rc`, `go env`, Maven `settings.xml`), and Program Files / ProgramData are included.
- **Safe scanning**: junctions and symlinks are not followed (no loops, no double counting), and cloud placeholder files (OneDrive "online-only") count as 0 bytes.
- **Fast**: directories are measured by a shared work queue across all CPU cores, so one huge folder (a 40 GB `Temp`, a Steam library) doesn't leave the other threads idle. Progress is polled by the UI, so the window stays responsive during a scan.
- **Languages**: Simplified Chinese, Traditional Chinese, Japanese and English, switchable in Settings without restarting.
- **CSV export** of the current view, **custom scan directories**, light/dark theme. White or black app icons get a contrasting tile so they stay visible in both themes.
- The last scan result and the window size/position are remembered between launches.

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
| `APPMANAGER_START_PAGE` | `overview`, `apps`, `harnesses` (AI tools), `devtools`, `unattributed` or `settings` |
| `APPMANAGER_SELECT_KEY` | Pre-select an item on the start page: an app group key on `apps`, a catalog `id` on `harnesses` / `devtools` |

## Project layout

```
AppManager.slnx
src/
  AppManager.Core/        Scanning, inventory and matching (no UI dependencies)
    Inventory/            Registry + MSIX app sources, install-location fallbacks
    Scanning/             Folder discovery, size measurement, grouping, CSV, persistence
    Matching/             AppMatcher, NameNormalizer, KnownTools.json rules
    Harnesses/            Catalog-driven scanner; HarnessCatalog.json (AI tools), DevToolsCatalog.json
    Imaging/              Icon tone detection (light/dark artwork)
    Localization/         Language resolution
  AppManager/             WinUI 3 app (pages, controls, view models, services)
    Strings/<lang>/       Resources.resw for zh-Hans, zh-Hant, ja-JP, en-US
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
- `description`: fallback text. The UI shows the localized `KnownTool_<id>_Description` string from each `Strings/<lang>/Resources.resw`, so add it in all four languages.

### Adding an AI tool or developer tool

Both views are driven by JSON catalogs in `src/AppManager.Core/Harnesses/`: `HarnessCatalog.json` (AI tools, current-user paths only) and `DevToolsCatalog.json` (developer tools, may also use `%PROGRAMFILES%`, `%PROGRAMFILES(X86)%`, `%PROGRAMDATA%` and `%DRIVES%`). Each tool lists roots:

```json
{
  "id": "npm",
  "name": "npm",
  "type": "packageManager",
  "roots": [
    {
      "path": "%LOCALAPPDATA%\\npm-cache",
      "kind": "packageCache",
      "mode": "merge",
      "sources": [
        { "env": "npm_config_cache" },
        { "ini": "%USERPROFILE%\\.npmrc", "key": "cache" }
      ],
      "children": [ { "match": "_logs", "kind": "logs" } ]
    }
  ]
}
```

- `path`: default location; the last segment may contain `*` / `?`.
- `kind`: category for the root (`sdk`, `packageCache`, `globalPackages`, `buildCache`, `cache`, `logs`, `state`, `models`, `conversations`, ...).
- `mode`: `split` (default) makes each direct child its own entry; `merge` keeps the root as one entry and splits off only the children matched by `children` rules.
- `sources`: extra candidate locations from an environment variable (`env`, `;`-separated, optional `suffix`), an ini-style file (`ini` + `key`) or an XML file (`xml` + `element`). Source paths that are drive roots or contain the user profile, AppData, Program Files, ProgramData or Windows are ignored.
- `label` / `entryPrefix`: display names for merged roots and split children.
- `children`: glob rules for direct children, first match wins; `presets` can share rule lists.

The catalog loader validates the file and the tests load both embedded catalogs, so `dotnet test` catches mistakes.

## Privacy

Everything runs locally. AppManager does not send scan results or file paths anywhere. To locate relocated caches, the Developer tools view reads single directory settings (such as `cache` or `store-dir`) from files like `.npmrc`; other lines, including auth tokens, are never read into results. The app talks to the network only through the Microsoft Store licensing APIs, and that code is not wired up yet.

## Microsoft Store

The app is packaged as MSIX (single-project packaging) and runs as a full-trust desktop app. It needs no restricted capabilities beyond `runFullTrust`. A Store licensing service based on `Windows.Services.Store.StoreContext` lives in `src/AppManager/Services/LicenseService.cs`. Paid add-ons are currently disabled; to enable them later, set `StoreConfig.ProAddOnStoreId` to the add-on's Store ID from Partner Center.

## Status

Early development.
