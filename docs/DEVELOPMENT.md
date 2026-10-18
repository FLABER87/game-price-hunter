# Development

.NET 10 SDK, Windows. No third-party packages in the app (xUnit in tests only).

```powershell
dotnet build GamePriceHunter.slnx
dotnet test tests/GamePriceHunter.Core.Tests
.\build\publish.ps1            # artifacts\publish\win-x64\GamePriceHunter.exe (self-contained, single file)
                               # + artifacts\installer\GamePriceHunter-Setup.exe (needs Inno Setup 6)
```

Diagnostics CLI, which runs the app's search pipeline and prints every store's status:

```powershell
dotnet run --project src/GamePriceHunter.Cli -- search "elden ring" --providers steam,gog --details
dotnet run --project src/GamePriceHunter.Cli -- health
dotnet run --project src/GamePriceHunter.Cli -- update   # latest release, as the app's update check sees it
```

## Release

1. Set `<Version>` in `Directory.Build.props`, or pass `-Version` to the script.
2. `.\build\publish.ps1 -Version 0.2.0` runs the tests and builds the exe and the installer. The installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php): `winget install --id JRSoftware.InnoSetup -e`.
3. Commit, then publish both files as a release tagged `v` + version:

```powershell
gh release create v0.2.0 artifacts\installer\GamePriceHunter-Setup.exe artifacts\publish\win-x64\GamePriceHunter.exe --title "Game Price Hunter 0.2.0" --notes "What changed, in a few lines for players."
```

The app finds new versions through the redirect of `/releases/latest` to `/releases/tag/vX.Y.Z` (not the GitHub API, which rate-limits per IP), at most once a day. So keep the `vX.Y.Z` tags and the `GamePriceHunter-Setup.exe` asset name. Installed copies download that asset and run it with `/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RELAUNCH=1`; portable and stand-alone copies only link to the release page. The installer is per-user (`%LOCALAPPDATA%\Programs\Game Price Hunter`), and its `AppId` in `build\installer\GamePriceHunter.iss` must never change.

- **Stores:** one `StoreProvider` subclass per store in `src/GamePriceHunter.Core/Providers`, registered in `ProviderCatalog`. Keep parsing in an `internal Parse(...)` method and add a fixture test.
- **Languages:** copy `src/GamePriceHunter.App/Localization/Strings.resx` to `Strings.<culture>.resx`, then add the culture to `Loc.Languages` and `SatelliteResourceLanguages`.
- **Portable mode:** an empty `portable` file next to the exe keeps all data in `Data\`.
- **Not used:** Epic, Humble, GMG and GameBillet reject the .NET client and come through CheapShark/IsThereAnyDeal instead. Kinguin, G2A, Gamivo, HRK and CDKeys are behind bot protection.
