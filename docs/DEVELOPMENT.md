# Development

.NET 10 SDK, Windows. No third-party packages in the app (xUnit in tests only).

```powershell
dotnet build GamePriceHunter.slnx
dotnet test tests/GamePriceHunter.Core.Tests
.\build\publish.ps1            # artifacts\publish\win-x64\GamePriceHunter.exe (self-contained, single file)
```

Diagnostics CLI, which runs the app's search pipeline and prints every store's status:

```powershell
dotnet run --project src/GamePriceHunter.Cli -- search "elden ring" --providers steam,gog --details
dotnet run --project src/GamePriceHunter.Cli -- health
```

- **Stores:** one `StoreProvider` subclass per store in `src/GamePriceHunter.Core/Providers`, registered in `ProviderCatalog`. Keep parsing in an `internal Parse(...)` method and add a fixture test.
- **Languages:** copy `src/GamePriceHunter.App/Localization/Strings.resx` to `Strings.<culture>.resx`, then add the culture to `Loc.Languages` and `SatelliteResourceLanguages`.
- **Portable mode:** an empty `portable` file next to the exe keeps all data in `Data\`.
- **Not used:** Epic, Humble, GMG and GameBillet reject the .NET client and come through CheapShark/IsThereAnyDeal instead. Kinguin, G2A, Gamivo, HRK and CDKeys are behind bot protection.
