<#
.SYNOPSIS
    Builds, tests and publishes Game Price Hunter as a single self-contained exe.

.EXAMPLE
    .\build\publish.ps1                    # win-x64 into artifacts\publish\win-x64
    .\build\publish.ps1 -Runtime win-arm64
    .\build\publish.ps1 -SkipTests
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$command = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($command) { $command.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }
$output = Join-Path $root "artifacts\publish\$Runtime"

Push-Location $root
try {
    if (-not $SkipTests) {
        & $dotnet test tests\GamePriceHunter.Core.Tests -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    $versionArgs = @()
    if ($Version) { $versionArgs = @("-p:Version=$Version") }

    & $dotnet publish src\GamePriceHunter.App -c Release -r $Runtime -o $output --nologo @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    $exe = Join-Path $output 'GamePriceHunter.exe'
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Published $exe ($size MB)" -ForegroundColor Green
}
finally {
    Pop-Location
}
