<#
.SYNOPSIS
    Creates the key that signs installers for the in-app updater.

.DESCRIPTION
    Generates an ECDSA P-256 key pair. The private key goes straight into the UPDATE_SIGNING_KEY secret of
    the GitHub repository and is not saved or shown anywhere. The public key is written to
    ReleasePublicKey in src\GamePriceHunter.Core\Updates\UpdateSignature.cs; commit that change. From then
    on CI signs the installer of every tag with the secret, and the app checks it against the public key.

    Run it once. Run it again with -Force only if the secret is lost or leaked: copies built with the old
    public key reject installers signed with the new one, so their players update once from the release page.

    Needs the GitHub CLI (gh), signed in to an account that can manage the repository's secrets.

.EXAMPLE
    .\build\new-signing-key.ps1
#>
param(
    [string]$Repository = 'FLABER87/game-price-hunter',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$command = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($command) { $command.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }
$secret = 'UPDATE_SIGNING_KEY'
$source = Join-Path $root 'src\GamePriceHunter.Core\Updates\UpdateSignature.cs'
$pattern = '(?<=ReleasePublicKey = ")[^"]*(?=")'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "The GitHub CLI is needed: winget install --id GitHub.cli -e" }
$text = [IO.File]::ReadAllText($source)
if ($text -notmatch $pattern) { throw "ReleasePublicKey was not found in $source." }

$existing = & gh secret list --repo $Repository --json name --jq '.[].name'
if ($LASTEXITCODE -ne 0) { throw "Could not list the secrets of $Repository; is gh signed in (gh auth login)?" }
if ($existing -contains $secret -and -not $Force) {
    throw "$Repository already has the $secret secret. A new key makes installed copies reject the next update; use -Force only if the old key is lost or leaked."
}

$tools = Join-Path $root 'artifacts\tools'
& $dotnet build (Join-Path $root 'src\GamePriceHunter.Cli') -c Release -o $tools --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "CLI build failed." }

# gph hands the private key to "gh secret set" itself and prints only the public key.
$publicKey = (& (Join-Path $tools 'gph.exe') new-key $Repository | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $publicKey) { throw "Could not create the key or store it in the $secret secret of $Repository." }
Write-Host "Stored the private key in the $secret secret of $Repository." -ForegroundColor Green

[IO.File]::WriteAllText($source, [regex]::Replace($text, $pattern, $publicKey), (New-Object Text.UTF8Encoding $false))
Write-Host "Public key: $publicKey"
Write-Host "Wrote it to $source; commit that before the next release." -ForegroundColor Green
