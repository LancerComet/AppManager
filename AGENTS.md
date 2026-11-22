# AppManager

WinUI 3 (Windows App SDK 2.7, .NET 11) app that measures disk usage of app-created folders and attributes them to installed programs.

## Layout
- `src/AppManager.Core` — scanning, installed-app inventory (registry + MSIX), matching (`AppMatcher`, `KnownTools.json`), no UI deps
- `src/AppManager` — WinUI app (pages, view models, `StoreLicenseService`)
- `tests/AppManager.Core.Tests` — xUnit tests for Core

## Commands
- Build: `dotnet build AppManager.slnx -p:Platform=x64`
- Test: `dotnet test tests/AppManager.Core.Tests`
- Run (packaged, with identity): `dotnet run --project src/AppManager -p:Platform=x64`
  - DEBUG hooks: `APPMANAGER_AUTOSCAN=1`, `APPMANAGER_START_PAGE=overview|apps|unattributed|settings`

## Notes
- Store add-on ID placeholder: `StoreConfig.ProAddOnStoreId` in `src/AppManager/Services/LicenseService.cs`
- PublishTrimmed is disabled on purpose (WinUI/CsWinRT/reflection).
- UI strings are hardcoded Simplified Chinese.
