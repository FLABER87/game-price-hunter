<#
.SYNOPSIS
    Builds, tests and publishes Game Price Hunter as a single self-contained exe plus its installer.

.DESCRIPTION
    The exe goes to artifacts\publish\<runtime>, the installer (Inno Setup 7) to artifacts\installer.
    Without -Version the version comes from Directory.Build.props. The installer is built with the
    ISCC.exe given by -InnoSetupCompiler (CI passes the Inno Setup it installs), or else with an installed
    Inno Setup 7.1 or later, and skipped with a warning when there is none.

    When UPDATE_SIGNING_KEY holds the release signing key (PEM), the installer is also signed for the
    in-app updater (GamePriceHunter-Setup.exe.sig, for the tag v<Version>), and the signature is checked
    against the public key the app is built with. CI does this for tags; see docs\DEVELOPMENT.md.

.EXAMPLE
    .\build\publish.ps1                    # win-x64 exe + GamePriceHunter-Setup.exe
    .\build\publish.ps1 -Version 0.2.0     # release build with an explicit version
    .\build\publish.ps1 -Runtime win-arm64
    .\build\publish.ps1 -SkipTests -SkipInstaller
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$InnoSetupCompiler,
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$command = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($command) { $command.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }
$output = Join-Path $root "artifacts\publish\$Runtime"
$installerOutput = Join-Path $root 'artifacts\installer'
# A relative path means the caller's folder, not the repository root the script moves to.
if ($InnoSetupCompiler) { $InnoSetupCompiler = (Resolve-Path -LiteralPath $InnoSetupCompiler).ProviderPath }

# Taken out of the environment at once, so that the tests and the build never see the signing key.
$signingKey = $env:UPDATE_SIGNING_KEY
Remove-Item Env:UPDATE_SIGNING_KEY -ErrorAction SilentlyContinue

# Inno Setup 7 installs next to Inno Setup 6, whose ISCC may come first on PATH, so every candidate is
# asked for its version. --version is new in 7.1, the oldest version the installer script accepts.
function Find-InnoSetupCompiler {
    $ErrorActionPreference = 'Continue'
    $candidates = @(
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe"
    )
    foreach ($key in @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1')) {
        $location = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
        if ($location) { $candidates += Join-Path $location 'ISCC.exe' }
    }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }

    foreach ($candidate in $candidates | Where-Object { $_ -and (Test-Path $_) }) {
        $version = & $candidate --version 2>$null | Select-Object -First 1
        if ($LASTEXITCODE -eq 0 -and $version -match '^(\d+\.\d+)\.' -and [version]$Matches[1] -ge [version]'7.1') { return $candidate }
    }
}

Push-Location $root
try {
    if (-not $Version) {
        $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw -Encoding UTF8)
        $Version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' must look like 1.2.3 (it becomes the v1.2.3 release tag)." }

    if (-not $SkipTests) {
        & $dotnet test tests\GamePriceHunter.Core.Tests -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    & $dotnet publish src\GamePriceHunter.App -c Release -r $Runtime -o $output --nologo "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    $exe = Join-Path $output 'GamePriceHunter.exe'
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Published $exe ($size MB, v$Version)" -ForegroundColor Green

    if (-not $SkipInstaller) {
        $iscc = if ($InnoSetupCompiler) { $InnoSetupCompiler } else { Find-InnoSetupCompiler }
        if ($iscc) {
            $arch = $Runtime.Substring(4)
            & $iscc /Q "/DAppVersion=$Version" "/DArch=$arch" "/DPublishDir=$output" "/O$installerOutput" build\installer\GamePriceHunter.iss
            if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }

            $setup = Get-ChildItem $installerOutput -Filter 'GamePriceHunter-Setup*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            $size = [math]::Round($setup.Length / 1MB, 1)
            Write-Host "Installer $($setup.FullName) ($size MB)" -ForegroundColor Green

            # A signature left from an earlier build would not match the new installer.
            Remove-Item "$($setup.FullName).sig" -ErrorAction SilentlyContinue
            if ($signingKey) {
                $tools = Join-Path $root 'artifacts\tools'
                & $dotnet build src\GamePriceHunter.Cli -c Release -o $tools --nologo -v q
                if ($LASTEXITCODE -ne 0) { throw "CLI build failed." }
                $gph = Join-Path $tools 'gph.exe'

                $env:UPDATE_SIGNING_KEY = $signingKey
                try { & $gph sign $setup.FullName --tag "v$Version" }
                finally { Remove-Item Env:UPDATE_SIGNING_KEY }
                if ($LASTEXITCODE -ne 0) { throw "Signing failed." }

                & $gph verify $setup.FullName --tag "v$Version"
                if ($LASTEXITCODE -ne 0) { throw "The signature does not match ReleasePublicKey in UpdateSignature.cs: UPDATE_SIGNING_KEY must be the key build\new-signing-key.ps1 created." }
            }
        }
        else {
            Write-Warning "Inno Setup 7.1 or later was not found, so the installer was skipped. Install it with: winget install --id JRSoftware.InnoSetup.7 -e"
        }
    }
}
finally {
    Pop-Location
}
