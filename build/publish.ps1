<#
.SYNOPSIS
    Builds, tests and publishes Game Price Hunter as a single self-contained exe plus its installer.

.DESCRIPTION
    The exe goes to artifacts\publish\<runtime>, the installer (Inno Setup 6) to artifacts\installer.
    Without -Version the version comes from Directory.Build.props. The installer is skipped with a
    warning when Inno Setup 6 is not installed.

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
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$command = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($command) { $command.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }
$output = Join-Path $root "artifacts\publish\$Runtime"
$installerOutput = Join-Path $root 'artifacts\installer'

function Find-InnoSetupCompiler {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($key in @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1')) {
        $location = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
        if ($location) { $candidates += Join-Path $location 'ISCC.exe' }
    }
    return $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
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
        $iscc = Find-InnoSetupCompiler
        if ($iscc) {
            $arch = $Runtime.Substring(4)
            & $iscc /Q "/DAppVersion=$Version" "/DArch=$arch" "/DPublishDir=$output" "/O$installerOutput" build\installer\GamePriceHunter.iss
            if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }

            $setup = Get-ChildItem $installerOutput -Filter 'GamePriceHunter-Setup*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            $size = [math]::Round($setup.Length / 1MB, 1)
            Write-Host "Installer $($setup.FullName) ($size MB)" -ForegroundColor Green
        }
        else {
            Write-Warning "Inno Setup 6 was not found, so the installer was skipped. Install it with: winget install --id JRSoftware.InnoSetup -e"
        }
    }
}
finally {
    Pop-Location
}
