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

- **Stores:** one `StoreProvider` subclass per store in `src/GamePriceHunter.Core/Providers`, registered in `ProviderCatalog`. Keep parsing in an `internal Parse(...)` method and add a fixture test.
- **Languages:** copy `src/GamePriceHunter.App/Localization/Strings.resx` to `Strings.<culture>.resx`, then add the culture to `Loc.Languages` and `SatelliteResourceLanguages`.
- **Portable mode:** an empty `portable` file next to the exe keeps all data in `Data\`.
- **Not used:** Epic, Humble, GMG and GameBillet reject the .NET client and come through CheapShark/IsThereAnyDeal instead. Kinguin, G2A, Gamivo, HRK and CDKeys are behind bot protection.
- **Rate limits:** Eneba answers a burst of searches (about six in a few seconds) with a Cloudflare challenge (HTTP 429). It shows as Blocked and clears after a short wait, so don't run the CLI against it in a tight loop.

## CI

The `Build` workflow builds and tests every push and pull request. It uploads the exe and the installer as an artifact. `main` requires its `Build and test` check. Pull requests from this repository's branches merge automatically (squash) once the check passes, and their branch is deleted. Open a pull request as a draft to hold it back.

## Release

1. Set `<Version>` in `Directory.Build.props` and merge that to `main`.
2. Tag it, putting a few lines for players in the tag message, and push the tag:

```powershell
git tag -a v0.2.0 -m "What changed, in a few lines for players."
git push origin v0.2.0
```

3. The `Build` workflow creates a draft release with both files and the tag message as notes. Check it on GitHub and publish it.

To build a release locally, run `.\build\publish.ps1 -Version 0.2.0`. It needs [Inno Setup 6](https://jrsoftware.org/isinfo.php): `winget install --id JRSoftware.InnoSetup -e`.

The app finds new versions through the redirect of `/releases/latest` to `/releases/tag/vX.Y.Z`, not the GitHub API, which rate-limits per IP. It checks at most once a day. So keep the `vX.Y.Z` tags and the `GamePriceHunter-Setup.exe` asset name. Installed copies download that asset and run it with `/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RELAUNCH=1`. Portable and stand-alone copies only link to the release page. The installer is per-user (`%LOCALAPPDATA%\Programs\Game Price Hunter`), and its `AppId` in `build\installer\GamePriceHunter.iss` must never change.
