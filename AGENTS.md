# AppManager

WinUI 3 (Windows App SDK 2.7, .NET 11) app that measures disk usage of app-created folders and attributes them to installed programs.

## Layout
- `src/AppManager.Core` — scanning, installed-app inventory (registry + MSIX), matching (`AppMatcher`, `KnownTools.json`), catalog-driven tool scanner (`Harnesses/HarnessCatalog.json` = AI tools, `Harnesses/DevToolsCatalog.json` = developer tools), no UI deps
- `src/AppManager` — WinUI app (pages, view models, `StoreLicenseService`)
- `tests/AppManager.Core.Tests` — xUnit tests for Core

## Commands
- Build: `dotnet build AppManager.slnx -p:Platform=x64`
- Test: `dotnet test tests/AppManager.Core.Tests`
- Run (packaged, with identity): `dotnet run --project src/AppManager -p:Platform=x64`
  - DEBUG hooks: `APPMANAGER_AUTOSCAN=1`, `APPMANAGER_START_PAGE=overview|apps|harnesses|devtools|unattributed|settings`, `APPMANAGER_SELECT_KEY=<group key or catalog id>`

## Notes
- Store add-on ID placeholder: `StoreConfig.ProAddOnStoreId` in `src/AppManager/Services/LicenseService.cs`
- PublishTrimmed is disabled on purpose (WinUI/CsWinRT/reflection).
- UI strings live in `src/AppManager/Strings/{zh-Hans,zh-Hant,ja-JP,en-US}/Resources.resw` (key parity checked by `LocalizationTests`); use `{l:Loc Key=...}` in XAML and `AppServices.Loc` in code, never `x:Uid` (it doesn't re-resolve on live language switch).
