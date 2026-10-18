# Development

.NET 10 SDK, Windows. No third-party packages in the app (xUnit in tests only).

```powershell
dotnet build GamePriceHunter.slnx
dotnet test tests/GamePriceHunter.Core.Tests
.\build\publish.ps1            # artifacts\publish\win-x64\GamePriceHunter.exe (self-contained, single file)
                               # + artifacts\installer\GamePriceHunter-Setup.exe (needs Inno Setup 7.1+)
```

Diagnostics CLI, which runs the app's search pipeline and prints every store's status:

```powershell
dotnet run --project src/GamePriceHunter.Cli -- search "elden ring" --providers steam,gog --details
dotnet run --project src/GamePriceHunter.Cli -- health
dotnet run --project src/GamePriceHunter.Cli -- update   # latest release, as the app's update check sees it
```

- **Stores:** one `StoreProvider` subclass per store in `src/GamePriceHunter.Core/Providers`, registered in `ProviderCatalog`. Keep parsing in an `internal Parse(...)` method and add a fixture test.
- **Languages:** the UI comes in 13 languages: `Strings.resx` (English) and `Strings.<culture>.resx` next to it. To add one, translate `Strings.resx` into `Strings.<culture>.resx` and add the culture to `Loc.Languages`, to `PluralRules` if the English rule doesn't fit it, and to `[Languages]` in the installer script (with an official Inno Setup translation). `TranslationTests` fails until every string, plural form and placeholder is there. Comments in `Strings.resx` tell translators what the placeholders are.
- **Plurals:** a key with a `_one` twin in `Strings.resx` is plural. Code formats it with `Loc.P(key, count)`, which picks `Key_one`, `Key_few`, `Key_many` or `Key` (CLDR's "other") by the language's rule. Languages write exactly the forms their rule produces for whole numbers: two in English or German, four in Russian or Polish, only `Key` in Japanese or Chinese.
- **Numbers and prices** are written in `Loc.FormatCulture`, never in `CultureInfo.CurrentCulture` or the UI culture: the Windows regional format when it is in the UI language, else the UI language as written in the user's country (de-CH), else the language's main format (de-DE). `MoneyFormatter` takes the separators, the place of the symbol and the space next to it from that culture and the decimals from the currency (rubles, hryvnias, tenge, yen, won and a few more have none). A foreign currency that shares the local symbol is spelled out (US$ in Mexico, JP¥ in China). Country and currency names come from Windows' ICU (`IcuNames`) in the UI language.
- **Not translated:** store and game names, store error messages (shown after "Search failed:"), and the CLI.
- **Portable mode:** an empty `portable` file next to the exe keeps all data in `Data\`.
- **Windows versions:** setup requires Windows 10 1607, the oldest Windows .NET 10 runs on (`MinVersion` in the installer script). Anything newer has to be optional: rounded corners, snap layouts and caption colors (Windows 11), ICU names (1903), Segoe Fluent Icons and Segoe UI Variable (Windows 11; Segoe MDL2 Assets and Segoe UI stand in on Windows 10).
- **Title bar:** the main window draws its own (`TitleBar` on top of WindowChrome), so anything in its header that takes clicks needs `WindowChrome.IsHitTestVisibleInChrome`. Dialogs keep the Windows one.
- **Region:** the country chosen in Settings goes to every store that takes a country or currency in its request. Fanatical does not: its catalog follows the IP address (as at its checkout), and only its prices follow the chosen currency.
- **Not used:** Epic, Humble, GMG and GameBillet reject the .NET client and come through CheapShark/IsThereAnyDeal instead. Kinguin, G2A, Gamivo, HRK and CDKeys are behind bot protection.
- **Rate limits:** Eneba answers a burst of searches (about six in a few seconds) with a Cloudflare challenge (HTTP 429). It shows as Blocked and clears after a short wait, so don't run the CLI against it in a tight loop.

## CI

The `Build` workflow builds and tests every push and pull request. It uploads the exe and the installer as an artifact. `main` requires its `Build and test` check. Once it passes, the workflow's `Merge` job squash-merges pull requests from this repository's branches and deletes the branch. Open a pull request as a draft to hold it back; marking it ready for review builds it again and merges it. A merge made by the workflow doesn't start a build of `main`, since GitHub doesn't run workflows for changes made with the workflow token.

## Release

1. Set `<Version>` in `Directory.Build.props` and merge that to `main`.
2. Tag it, putting a few lines for players in the tag message, and push the tag:

```powershell
git tag -a v0.2.0 -m "What changed, in a few lines for players."
git push origin v0.2.0
```

3. The `Build` workflow creates a draft release with the exe, the installer, the installer's signature (`GamePriceHunter-Setup.exe.sig`) and the tag message as notes. Check it on GitHub and publish it.

To build a release locally, run `.\build\publish.ps1 -Version 0.2.0`. It needs [Inno Setup 7.1 or later](https://jrsoftware.org/isinfo.php): `winget install --id JRSoftware.InnoSetup.7 -e`. CI installs the version pinned in `.github/workflows/build.yml`; to move to a newer one, change its URL and SHA-256 there (both are on the release page of [jrsoftware/issrc](https://github.com/jrsoftware/issrc/releases)).

The app finds new versions through the redirect of `/releases/latest` to `/releases/tag/vX.Y.Z`, not the GitHub API, which rate-limits per IP. That is one HEAD request to github.com, so it checks on every start. So keep the `vX.Y.Z` tags and the `GamePriceHunter-Setup.exe` and `GamePriceHunter-Setup.exe.sig` asset names. Installed copies download both, check the signature and only then run the installer with `/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RELAUNCH=1`. Portable and stand-alone copies only link to the release page. The installer is per-user (`%LOCALAPPDATA%\Programs\Game Price Hunter`), and its `AppId` in `build\installer\GamePriceHunter.iss` must never change.

### Update signature

The app runs a downloaded installer only if it is signed with the release key, an ECDSA P-256 key whose public half is `ReleasePublicKey` in `src/GamePriceHunter.Core/Updates/UpdateSignature.cs`. The signature covers the installer's SHA-256 and the tag, so the installer of an older release can't pass for the latest one. If the signature is missing or wrong, the update banner says the update failed and links to the release page. Copies of 0.2.0 and older don't check signatures.

- On a tag, the `Test and publish` step passes the `UPDATE_SIGNING_KEY` secret to `build\publish.ps1`. The script signs the installer with `gph sign` and checks the result against `ReleasePublicKey` with `gph verify`. Without the secret the tag build fails, so an unsigned release can't be drafted.
- The key was created once with `.\build\new-signing-key.ps1`. The script puts the private key straight into the secret, so no copy exists anywhere else, and writes the public key into `UpdateSignature.cs`. If the secret is lost or leaked, run the script again with `-Force` and release a new version. Copies built with the old key can't install that version by themselves, so players download it from the release page once.
- Anyone who can push to the repository can change the workflow and sign their own file with the secret. The signature protects against a replaced release file, not against a compromised GitHub account.
