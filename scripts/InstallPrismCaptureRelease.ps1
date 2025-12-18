[CmdletBinding()]
param(
    # Path to the extracted GitHub Release bundle folder.
    [Parameter(Mandatory = $true)]
    [string]$BundlePath,

    # Import PrismCapture_Distribution.cer into CurrentUser trust stores (and LocalMachine if elevated).
    [switch]$InstallCert
)

$ErrorActionPreference = 'Stop'

function Test-IsAdmin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal($id)
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

$bundle = (Resolve-Path -LiteralPath $BundlePath).Path

$msix = Get-ChildItem -Path $bundle -Filter '*.msix' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) {
    throw "No .msix found in bundle folder: $bundle"
}

# Determine arch from filename when possible; fallback to x64.
$ridArch = 'x64'
if ($msix.Name -match '_x86\b') { $ridArch = 'x86' }
elseif ($msix.Name -match '_arm64\b') { $ridArch = 'arm64' }
elseif ($msix.Name -match '_x64\b') { $ridArch = 'x64' }

$depsDir = Join-Path $bundle 'Dependencies'
$depsArchDir = Join-Path $depsDir $ridArch
if (-not (Test-Path $depsArchDir) -and $ridArch -eq 'x86') {
    # Some bundles use win32 for x86.
    $depsArchDir = Join-Path $depsDir 'win32'
}

$dependencyPath = @()
if (Test-Path $depsArchDir) {
    $dependencyPath = Get-ChildItem -Path $depsArchDir -File -Include *.appx,*.msix | Select-Object -ExpandProperty FullName
}

$cer = Join-Path $bundle 'PrismCapture_Distribution.cer'
if ($InstallCert) {
    if (-not (Test-Path $cer)) {
        throw "-InstallCert was specified, but cert file not found: $cer"
    }

    Write-Host "Importing signing certificate..." -ForegroundColor Cyan
    try { Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null } catch { }
    try { Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null } catch { }

    if (Test-IsAdmin) {
        try { Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null } catch { }
        try { Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null } catch { }
    }
}

Write-Host "Installing Prism Capture..." -ForegroundColor Cyan
Write-Host "  MSIX: $($msix.FullName)"
if ($dependencyPath.Count -gt 0) {
    Write-Host "  Dependencies: $($dependencyPath.Count)" 
}

try {
    if ($dependencyPath -and $dependencyPath.Count -gt 0) {
        Add-AppxPackage -Path $msix.FullName -DependencyPath $dependencyPath -ForceApplicationShutdown -ErrorAction Stop | Out-Null
    } else {
        Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ErrorAction Stop | Out-Null
    }
}
catch {
    $err = $_.Exception.Message
    if ($err -match '0x800B0109') {
        if (-not (Test-IsAdmin)) {
            throw "Install failed with certificate trust (0x800B0109). Re-run this installer as Administrator, or ensure the signing certificate is trusted. Underlying error: $err"
        }
    }

    throw "Add-AppxPackage failed. Underlying error: $err"
}

Write-Host "Installed. Launch from Start: search for 'Prism Capture'." -ForegroundColor Green
