[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('x86','x64','ARM64')]
    [string]$Platform,

    [switch]$Force,

    # Optional: if FFmpeg isn't already bundled under src\ScreenRecorder.App\External\ffmpeg\,
    # attempt to install it via winget and/or copy it from PATH into that folder so it gets
    # bundled into the MSIX (recommended for offline / no-PATH installs).
    [switch]$InstallFfmpeg,

    # Alternative to -InstallFfmpeg for machines without winget:
    # provide a direct path to ffmpeg.exe (and optionally ffprobe.exe) to be copied into
    # src\ScreenRecorder.App\External\ffmpeg\ before building the MSIX.
    [string]$FfmpegPath,
    [string]$FfprobePath,

    # CI can set this to fail fast if FFmpeg isn't bundled into the package.
    [switch]$RequireFfmpeg
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ScreenRecorder.App\ScreenRecorder.App.csproj'
$manifest = Join-Path $repoRoot 'src\ScreenRecorder.App\Package.appxmanifest'

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}
if (-not (Test-Path $manifest)) {
    throw "Manifest not found: $manifest"
}

$bundledFfmpeg = Join-Path $repoRoot 'src\ScreenRecorder.App\External\ffmpeg\ffmpeg.exe'

function Get-FirstOnPath {
    param(
        [Parameter(Mandatory = $true)][string]$ExeName
    )

    try {
        $out = & where.exe $ExeName 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        if (-not $out) { return $null }
        return ($out | Select-Object -First 1)
    }
    catch {
        return $null
    }
}

function Ensure-BundledFfmpeg {
    param(
        [Parameter(Mandatory = $true)][string]$BundledFfmpegPath
    )

    $ffmpegDir = Split-Path -Parent $BundledFfmpegPath
    if (-not (Test-Path $ffmpegDir)) {
        New-Item -ItemType Directory -Path $ffmpegDir | Out-Null
    }

    if (Test-Path $BundledFfmpegPath) {
        return
    }

    $ffmpegOnPath = Get-FirstOnPath -ExeName 'ffmpeg.exe'
    if ($ffmpegOnPath) {
        Write-Host "Bundling FFmpeg from PATH: $ffmpegOnPath" -ForegroundColor Cyan
        Copy-Item -LiteralPath $ffmpegOnPath -Destination $BundledFfmpegPath -Force

        $ffprobeOnPath = Get-FirstOnPath -ExeName 'ffprobe.exe'
        if ($ffprobeOnPath) {
            $bundledFfprobe = Join-Path $ffmpegDir 'ffprobe.exe'
            try { Copy-Item -LiteralPath $ffprobeOnPath -Destination $bundledFfprobe -Force } catch { }
        }
    }
}

function Ensure-BundledFromExplicitPaths {
    param(
        [Parameter(Mandatory = $true)][string]$BundledFfmpegPath,
        [Parameter(Mandatory = $true)][string]$ExplicitFfmpegPath,
        [string]$ExplicitFfprobePath
    )

    if (-not (Test-Path -LiteralPath $ExplicitFfmpegPath)) {
        throw "-FfmpegPath does not exist: $ExplicitFfmpegPath"
    }

    $ffmpegDir = Split-Path -Parent $BundledFfmpegPath
    if (-not (Test-Path $ffmpegDir)) {
        New-Item -ItemType Directory -Path $ffmpegDir | Out-Null
    }

    Write-Host "Bundling FFmpeg from explicit path: $ExplicitFfmpegPath" -ForegroundColor Cyan
    Copy-Item -LiteralPath $ExplicitFfmpegPath -Destination $BundledFfmpegPath -Force

    $bundledFfprobe = Join-Path $ffmpegDir 'ffprobe.exe'
    $candidateFfprobe = $null
    if ($ExplicitFfprobePath) {
        $candidateFfprobe = $ExplicitFfprobePath
        if (-not (Test-Path -LiteralPath $candidateFfprobe)) {
            throw "-FfprobePath does not exist: $candidateFfprobe"
        }
    } else {
        $candidateFfprobe = Join-Path (Split-Path -Parent $ExplicitFfmpegPath) 'ffprobe.exe'
        if (-not (Test-Path -LiteralPath $candidateFfprobe)) {
            $candidateFfprobe = $null
        }
    }

    if ($candidateFfprobe) {
        try { Copy-Item -LiteralPath $candidateFfprobe -Destination $bundledFfprobe -Force } catch { }
    }
}

function Ensure-FfmpegInstalledViaWinget {
    if (Get-FirstOnPath -ExeName 'ffmpeg.exe') {
        return
    }

    $winget = $null
    try { $winget = (Get-Command winget -ErrorAction SilentlyContinue).Source } catch { }
    if (-not $winget) {
        throw "FFmpeg is missing and 'winget' was not found. Either install FFmpeg manually and re-run with -FfmpegPath (optionally -FfprobePath), or install App Installer (winget) and re-run with -InstallFfmpeg."
    }

    Write-Host 'Installing FFmpeg via winget (Gyan.FFmpeg)...' -ForegroundColor Cyan
    & $winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements | Write-Output
    if ($LASTEXITCODE -ne 0) {
        throw "winget install failed (exit code $LASTEXITCODE)."
    }
}

if ($FfmpegPath) {
    Ensure-BundledFromExplicitPaths -BundledFfmpegPath $bundledFfmpeg -ExplicitFfmpegPath $FfmpegPath -ExplicitFfprobePath $FfprobePath
}

if (-not (Test-Path $bundledFfmpeg)) {
    if ($InstallFfmpeg) {
        try {
            # Prefer bundling (offline/no-PATH) by copying from PATH.
            Ensure-BundledFfmpeg -BundledFfmpegPath $bundledFfmpeg

            # If still not bundled, try to install and then bundle.
            if (-not (Test-Path $bundledFfmpeg)) {
                Ensure-FfmpegInstalledViaWinget
                Ensure-BundledFfmpeg -BundledFfmpegPath $bundledFfmpeg
            }
        }
        catch {
            Write-Warning "FFmpeg auto-install/bundle failed. Error: $($_.Exception.Message)"
        }

        if (-not (Test-Path $bundledFfmpeg)) {
            throw "-InstallFfmpeg was specified, but ffmpeg.exe could not be bundled at: $bundledFfmpeg`n`n" +
                  "Fix options:`n" +
                  "  1) Install App Installer (winget) and re-run this command, or`n" +
                  "  2) Download FFmpeg manually and re-run with -FfmpegPath (optionally -FfprobePath), or`n" +
                  "  3) Download FFmpeg manually and place ffmpeg.exe at:`n" +
                  "     src\\ScreenRecorder.App\\External\\ffmpeg\\ffmpeg.exe`n" +
                  "     (ffprobe.exe optional), then re-run the installer with -Force."
        }
    }
}

if (-not (Test-Path $bundledFfmpeg)) {
    $msg = "FFmpeg is not present at: $bundledFfmpeg`n" +
           "This MSIX install will rely on 'ffmpeg' being available on PATH (or via PRISMCAPTURE_FFMPEG).`n" +
           "To bundle FFmpeg into the MSIX, either pass -FfmpegPath (optionally -FfprobePath) to this installer, or place ffmpeg.exe under src\\ScreenRecorder.App\\External\\ffmpeg\\ and rebuild/install."
    if ($RequireFfmpeg) { throw $msg }
    Write-Warning $msg
}

if (-not $Platform) {
    switch -Regex ($env:PROCESSOR_ARCHITECTURE) {
        '^ARM64$' { $Platform = 'ARM64'; break }
        '^AMD64$' { $Platform = 'x64'; break }
        default  { $Platform = 'x86'; break }
    }
}

$ridArch = switch ($Platform) {
    'x64'   { 'x64' }
    'ARM64' { 'arm64' }
    default { 'x86' }
}
$rid = "win-$ridArch"

# MSIX signing: certificate Subject must match the Publisher in Package.appxmanifest.
# For developer installs, create & trust a self-signed code-signing cert in CurrentUser stores.
$devCertSubject = 'CN=PrismCapture'
$devCerPath = Join-Path $repoRoot 'certs\PrismCapture_Dev.cer'

if ($Force) {
    try {
        $installed = Get-AppxPackage -Name 'PrismCapture' -ErrorAction SilentlyContinue
        if ($installed) {
            Write-Host "Removing existing package: $($installed.PackageFullName)" -ForegroundColor Yellow
            Remove-AppxPackage -Package $installed.PackageFullName -ErrorAction Stop | Out-Null
        }
    }
    catch {
        Write-Warning "Failed to remove existing PrismCapture package. Continuing. Error: $($_.Exception.Message)"
    }
}

function Ensure-DevSigningCertificate {
    param(
        [Parameter(Mandatory = $true)][string]$Subject,
        [Parameter(Mandatory = $true)][string]$CerPath
    )

    if (-not (Get-Command New-SelfSignedCertificate -ErrorAction SilentlyContinue)) {
        throw 'New-SelfSignedCertificate is not available. Install Windows/PowerShell prerequisites or provide a signing certificate.'
    }

    $codeSigningOid = '1.3.6.1.5.5.7.3.3'

    $existing = Get-ChildItem -Path 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $Subject } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    $needsNew = $true
    if ($existing) {
        $hasCodeSigningEku = $false
        $hasBasicConstraints = $false
        foreach ($ext in $existing.Extensions) {
            if ($ext.Oid -and $ext.Oid.Value -eq '2.5.29.19') { $hasBasicConstraints = $true }
            if ($ext -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
                foreach ($oid in $ext.EnhancedKeyUsages) {
                    if ($oid.Value -eq $codeSigningOid) { $hasCodeSigningEku = $true; break }
                }
            }
        }
        if ($hasCodeSigningEku -and $hasBasicConstraints) {
            $needsNew = $false
        }
    }

    if ($needsNew) {
        Write-Host "Creating dev signing certificate ($Subject)..." -ForegroundColor Cyan
        $certParams = @{
            Type              = 'Custom'
            Subject           = $Subject
            KeyAlgorithm      = 'RSA'
            KeyLength         = 2048
            HashAlgorithm     = 'SHA256'
            KeyUsage          = 'DigitalSignature'
            KeyExportPolicy   = 'Exportable'
            CertStoreLocation = 'Cert:\CurrentUser\My'
            FriendlyName      = 'PrismCapture Dev MSIX'
            NotAfter          = (Get-Date).AddYears(5)
            TextExtension     = @(
                '2.5.29.19={text}CA=false',
                "2.5.29.37={text}$codeSigningOid"
            )
        }
        $cert = New-SelfSignedCertificate @certParams
    } else {
        $cert = $existing
    }

    $cerDir = Split-Path -Parent $CerPath
    if ($cerDir -and -not (Test-Path $cerDir)) {
        New-Item -ItemType Directory -Path $cerDir | Out-Null
    }
    Export-Certificate -Cert $cert -FilePath $CerPath | Out-Null

    try {
        Import-Certificate -FilePath $CerPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
    } catch {
        # If already trusted, ignore.
    }

    # Some systems validate MSIX signatures against Trusted Root even for self-signed certs.
    try {
        Import-Certificate -FilePath $CerPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
    } catch {
        # If already trusted, ignore.
    }

    # If we're elevated, also trust machine-wide (some setups ignore CurrentUser trust for app package signatures).
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal($id)
        $isAdmin = $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        $isAdmin = $false
    }
    if ($isAdmin) {
        try { Import-Certificate -FilePath $CerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null } catch { }
        try { Import-Certificate -FilePath $CerPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null } catch { }
    }

    return @{ Thumbprint = $cert.Thumbprint; CerPath = $CerPath }
}

$devSigning = Ensure-DevSigningCertificate -Subject $devCertSubject -CerPath $devCerPath

function Test-IsAdmin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal($id)
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

Write-Host "Building MSIX ($Configuration / $Platform / $rid)..." -ForegroundColor Cyan

dotnet build $project -c $Configuration -p:Platform=$Platform -r $rid `
    -p:WindowsPackageType=MSIX `
    -p:GenerateAppxPackageOnBuild=true `
    -p:PublishTrimmed=false `
    -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint="$($devSigning.Thumbprint)" |
    Write-Output

if ($LASTEXITCODE -ne 0) {
    throw "MSIX build failed (exit code $LASTEXITCODE)."
}

$appPackages = Join-Path $repoRoot 'src\ScreenRecorder.App\AppPackages'
if (-not (Test-Path $appPackages)) {
    throw "MSIX output folder not found: $appPackages"
}

$patterns = @(
    "*_${Platform}_${Configuration}_*",
    "*_${ridArch}_${Configuration}_*"
)

$packageFolder = $null
foreach ($pattern in $patterns) {
    $packageFolder = Get-ChildItem -Path $appPackages -Directory -Filter $pattern -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($packageFolder) { break }
}

if (-not $packageFolder) {
    $packageFolder = Get-ChildItem -Path $appPackages -Directory |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

if (-not $packageFolder) {
    throw "No AppPackages subfolders found under: $appPackages"
}

Write-Host "Installing package from:" -ForegroundColor Cyan
Write-Host "  $($packageFolder.FullName)"

$msix = Get-ChildItem -Path $packageFolder.FullName -Filter '*.msix' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $msix) {
    throw "No .msix found under: $($packageFolder.FullName)"
}

$dependencyPath = @()
$depsDir = Join-Path $packageFolder.FullName 'Dependencies'
if (Test-Path $depsDir) {
    # Do NOT recurse into all architectures. Passing x86/x64/arm64 together can lead to
    # duplicate dependency packages (e.g., Windows App Runtime) and installs failing.
    $depsArchDir = Join-Path $depsDir $ridArch
    if (-not (Test-Path $depsArchDir) -and $ridArch -eq 'x86') {
        # Some packaging outputs use a 'win32' folder for x86 dependencies.
        $depsArchDir = Join-Path $depsDir 'win32'
    }

    if (Test-Path $depsArchDir) {
        $dependencyPath = Get-ChildItem -Path $depsArchDir -File -Include *.appx,*.msix |
            Select-Object -ExpandProperty FullName
    }
}

try {
    if ($dependencyPath -and $dependencyPath.Count -gt 0) {
        Add-AppxPackage -Path $msix.FullName -DependencyPath $dependencyPath -ForceApplicationShutdown -ErrorAction Stop | Out-Null
    } else {
        Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ErrorAction Stop | Out-Null
    }
} catch {
    $err = $_.Exception.Message
    if ($err -match '0x800B0109') {
        if (Test-IsAdmin) {
            Write-Warning 'MSIX signature trust failed (0x800B0109). Importing dev certificate into LocalMachine Root/TrustedPeople and retrying...'
            try { Import-Certificate -FilePath $devSigning.CerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null } catch { }
            try { Import-Certificate -FilePath $devSigning.CerPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null } catch { }

            if ($dependencyPath -and $dependencyPath.Count -gt 0) {
                Add-AppxPackage -Path $msix.FullName -DependencyPath $dependencyPath -ForceApplicationShutdown -ErrorAction Stop | Out-Null
            } else {
                Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ErrorAction Stop | Out-Null
            }
        } else {
            throw "Add-AppxPackage failed with 0x800B0109 (certificate trust). Re-run this script from an elevated PowerShell (Run as Administrator) so it can trust the dev certificate machine-wide. Underlying error: $err"
        }
    }

    throw "Add-AppxPackage failed. If this is a dev machine, ensure Windows Settings -> For developers -> Developer Mode is enabled. Underlying error: $err"
}

Write-Host "" 
Write-Host "Installed. Launch it from Start: search for 'Prism Capture'." -ForegroundColor Green
