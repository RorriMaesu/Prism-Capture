[CmdletBinding()]
param(
    [ValidateSet('x86','x64','ARM64')]
    [string]$Platform = 'x64',

    # MSIX package version MUST be four numeric parts: Major.Minor.Build.Revision
    # Example: 1.2.3.0
    [string]$Version,

    # Path to the distribution signing certificate (.pfx). This file is intentionally not committed.
    [string]$PfxPath,

    # If not set, reads from env var PRISMCAPTURE_PFX_PASSWORD, else prompts.
    [string]$PfxPassword,

    # Optional timestamp server (recommended for long-term trust). Leave empty to skip timestamping.
    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    # CI can set this to fail fast if FFmpeg isn't bundled into the package.
    [switch]$RequireFfmpeg
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ScreenRecorder.App\ScreenRecorder.App.csproj'
$manifest = Join-Path $repoRoot 'src\ScreenRecorder.App\Package.appxmanifest'

$bundledFfmpeg = Join-Path $repoRoot 'src\ScreenRecorder.App\External\ffmpeg\ffmpeg.exe'
if (-not (Test-Path $bundledFfmpeg)) {
    $msg = "FFmpeg is not present at: $bundledFfmpeg`n" +
           "You are about to publish an MSIX that may require 'ffmpeg' to be available on PATH on the target machine.`n" +
           "To bundle FFmpeg into the MSIX, place ffmpeg.exe under src\\ScreenRecorder.App\\External\\ffmpeg\\ before publishing."
    if ($RequireFfmpeg) {
        throw $msg
    }
    Write-Warning $msg
}

if (-not (Test-Path $project)) { throw "Project not found: $project" }
if (-not (Test-Path $manifest)) { throw "Manifest not found: $manifest" }

$profile = switch ($Platform) {
    'x86'   { 'msix-x86' }
    'ARM64' { 'msix-arm64' }
    default { 'msix-x64' }
}

if (-not $Version) {
    # Default to current manifest Identity Version.
    [xml]$xml = Get-Content -LiteralPath $manifest
    $Version = $xml.Package.Identity.Version
}

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Invalid MSIX version '$Version'. Expected 'Major.Minor.Build.Revision' (e.g. 1.2.3.0)."
}

if (-not $PfxPath) {
    $PfxPath = Join-Path $repoRoot 'certs\PrismCapture_Distribution.pfx'
}

if (-not (Test-Path $PfxPath)) {
    throw "Signing certificate not found: $PfxPath`nCreate/import a .pfx with a Subject that matches Package.appxmanifest Identity Publisher."
}

if (-not $PfxPassword) {
    $PfxPassword = $env:PRISMCAPTURE_PFX_PASSWORD
}

if (-not $PfxPassword) {
    $secure = Read-Host "Enter PFX password" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $PfxPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

Write-Host "Publishing signed MSIX ($profile)" -ForegroundColor Cyan
Write-Host "  Platform:  $Platform"
Write-Host "  Version:   $Version"
Write-Host "  PFX:       $PfxPath"

$props = @(
    "-p:PublishProfile=$profile",
    "-p:PackageVersion=$Version",
    "-p:PackageCertificateKeyFile=$PfxPath",
    "-p:PackageCertificatePassword=$PfxPassword"
)

if ($TimestampUrl) {
    $props += "-p:AppxPackageSigningTimestampServerUrl=$TimestampUrl"
}

# The profile already sets Release + RID + GenerateAppxPackageOnBuild.
# This will emit an .msix under src\ScreenRecorder.App\AppPackages\...

dotnet publish $project @props |
    Write-Output

$appPackages = Join-Path $repoRoot 'src\ScreenRecorder.App\AppPackages'
Write-Host "" 
Write-Host "Done. MSIX output is under:" -ForegroundColor Green
Write-Host "  $appPackages"
