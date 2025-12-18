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
    # Stored as SecureString; converted to plain text only transiently when passing to MSBuild.
    [SecureString]$PfxPassword,

    # Optional timestamp server (recommended for long-term trust). Leave empty to skip timestamping.
    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    # CI can set this to fail fast if FFmpeg isn't bundled into the package.
    [switch]$RequireFfmpeg,

    # Optional: create a "friend-proof" release folder containing:
    # - the published .msix
    # - Dependencies\<arch>\* (Windows App SDK runtime dependencies)
    # - PrismCapture_Distribution.cer (public cert extracted from the PFX)
    # - InstallPrismCapture.cmd + InstallPrismCaptureRelease.ps1
    # If -Zip is set, produces a zip next to the output folder.
    [string]$OutDir,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ScreenRecorder.App\ScreenRecorder.App.csproj'
$manifest = Join-Path $repoRoot 'src\ScreenRecorder.App\Package.appxmanifest'

function Get-RidArch {
    param([Parameter(Mandatory = $true)][string]$Platform)
    switch ($Platform) {
        'ARM64' { return 'arm64' }
        'x86' { return 'x86' }
        default { return 'x64' }
    }
}

function Get-LatestMsixFolder {
    param(
        [Parameter(Mandatory = $true)][string]$AppPackagesDir,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$Version
    )

    if (-not (Test-Path $AppPackagesDir)) {
        throw "AppPackages folder not found: $AppPackagesDir"
    }

    $ridArch = Get-RidArch -Platform $Platform
    $candidates = Get-ChildItem -Path $AppPackagesDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "_${ridArch}_" -or $_.Name -match "_${Platform}_" } |
        Sort-Object LastWriteTime -Descending

    if (-not $candidates -or $candidates.Count -eq 0) {
        $candidates = Get-ChildItem -Path $AppPackagesDir -Directory |
            Sort-Object LastWriteTime -Descending
    }

    return ($candidates | Select-Object -First 1)
}

function Write-PublicCerFromPfx {
    param(
        [Parameter(Mandatory = $true)][string]$PfxPath,
        [Parameter(Mandatory = $true)][string]$PfxPasswordPlain,
        [Parameter(Mandatory = $true)][string]$OutCerPath
    )

    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
    $cert.Import($PfxPath, $PfxPasswordPlain, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)

    $bytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    [System.IO.File]::WriteAllBytes($OutCerPath, $bytes)
}

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

$publishProfile = switch ($Platform) {
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
    if ($env:PRISMCAPTURE_PFX_PASSWORD) {
        $PfxPassword = ConvertTo-SecureString -String $env:PRISMCAPTURE_PFX_PASSWORD -AsPlainText -Force
    }
}

if (-not $PfxPassword) {
    $PfxPassword = Read-Host "Enter PFX password" -AsSecureString
}

Write-Host "Publishing signed MSIX ($publishProfile)" -ForegroundColor Cyan
Write-Host "  Platform:  $Platform"
Write-Host "  Version:   $Version"
Write-Host "  PFX:       $PfxPath"

# Convert password to plain text only for MSBuild and certificate export.
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword)
try {
    $PfxPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

$props = @(
    "-p:PublishProfile=$publishProfile",
    "-p:PackageVersion=$Version",
    "-p:PackageCertificateKeyFile=$PfxPath",
    "-p:PackageCertificatePassword=$PfxPasswordPlain"
)

if ($TimestampUrl) {
    $props += "-p:AppxPackageSigningTimestampServerUrl=$TimestampUrl"
}

# The profile already sets Release + RID + GenerateAppxPackageOnBuild.
# This will emit an .msix under src\ScreenRecorder.App\AppPackages\...

dotnet publish $project @props |
    Write-Output

$appPackages = Join-Path $repoRoot 'src\ScreenRecorder.App\AppPackages'

if ($OutDir) {
    $ridArch = Get-RidArch -Platform $Platform
    $latestFolder = Get-LatestMsixFolder -AppPackagesDir $appPackages -Platform $Platform -Version $Version
    Write-Host "" 
    Write-Host "Creating release bundle..." -ForegroundColor Cyan
    Write-Host "  Source: $($latestFolder.FullName)"

    if (-not (Test-Path $OutDir)) {
        New-Item -ItemType Directory -Path $OutDir | Out-Null
    }

    $msix = Get-ChildItem -Path $latestFolder.FullName -Filter '*.msix' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $msix) {
        throw "No .msix found under: $($latestFolder.FullName)"
    }

    Copy-Item -LiteralPath $msix.FullName -Destination (Join-Path $OutDir $msix.Name) -Force

    $depsDir = Join-Path $latestFolder.FullName 'Dependencies'
    if (Test-Path $depsDir) {
        $depsArchDir = Join-Path $depsDir $ridArch
        if (-not (Test-Path $depsArchDir) -and $ridArch -eq 'x86') {
            $depsArchDir = Join-Path $depsDir 'win32'
        }
        if (Test-Path $depsArchDir) {
            $targetDeps = Join-Path $OutDir (Join-Path 'Dependencies' $ridArch)
            New-Item -ItemType Directory -Path $targetDeps -Force | Out-Null
            Copy-Item -Path (Join-Path $depsArchDir '*') -Destination $targetDeps -Force
        }
    }

    $cerOut = Join-Path $OutDir 'PrismCapture_Distribution.cer'
    Write-PublicCerFromPfx -PfxPath $PfxPath -PfxPassword $PfxPasswordPlain -OutCerPath $cerOut

    $installerPs1 = Join-Path $repoRoot 'scripts\InstallPrismCaptureRelease.ps1'
    if (-not (Test-Path $installerPs1)) {
        throw "Missing installer script (expected to be in repo): $installerPs1"
    }
    Copy-Item -LiteralPath $installerPs1 -Destination (Join-Path $OutDir 'InstallPrismCaptureRelease.ps1') -Force

    $cmd = @'
@echo off
setlocal

rem Prism Capture installer (from GitHub Release bundle)
rem - Imports PrismCapture_Distribution.cer into CurrentUser trust stores
rem - Installs the MSIX + Dependencies

set "ROOT=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%InstallPrismCaptureRelease.ps1" -BundlePath "%ROOT%" -InstallCert
if errorlevel 1 (
  echo.
  echo Install failed.
  pause
  exit /b 1
)

echo.
echo Installed. Open Start and search for "Prism Capture".
pause
exit /b 0
"'@
    Set-Content -LiteralPath (Join-Path $OutDir 'InstallPrismCapture.cmd') -Value $cmd -Encoding ASCII

    if ($Zip) {
        $zipPath = "$OutDir.zip"
        if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
        Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $zipPath -Force
        Write-Host "  Zip: $zipPath" -ForegroundColor Green
    }

    Write-Host "  Bundle: $OutDir" -ForegroundColor Green
}

Write-Host "" 
Write-Host "Done. MSIX output is under:" -ForegroundColor Green
Write-Host "  $appPackages"
