<#
.SYNOPSIS
    Builds and packages AIStatusTray as signed MSIX for distribution.
.DESCRIPTION
    1. Creates a self-signed certificate (30-day expiry) matching the Package.appxmanifest publisher.
    2. Builds for the current machine's architecture (and optionally others if SDK is installed).
    3. Packages each build as a signed .msix using the AppX layout output.
    4. Exports the .cer certificate for the recipient.
    5. Copies the install script and zips everything into AIStatusTray.zip.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64', 'All')]
    [string]$Architecture = 'All'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
$projectDir = Join-Path $repoRoot 'AIStatusTray'
$csproj = Join-Path $projectDir 'AIStatusTray.csproj'
$manifestPath = Join-Path $projectDir 'Package.appxmanifest'
$imagesDir = Join-Path $projectDir 'Images'
$publishDir = Join-Path $repoRoot 'publish'
$publisher = 'CN=marcelwagner'
$tfm = 'net9.0-windows10.0.26100.0'

# --- Locate SDK tools ---
function Find-SdkTool([string]$toolName) {
    # Try Windows SDK install
    $sdkBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $sdkBin) {
        $found = Get-ChildItem $sdkBin -Recurse -Filter $toolName -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -match 'x64' } |
            Select-Object -Last 1
        if ($found) { return $found.FullName }
    }

    # Try NuGet cached SDK build tools
    $nugetPkg = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
    if (Test-Path $nugetPkg) {
        $found = Get-ChildItem $nugetPkg -Recurse -Filter $toolName -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -match 'x64' } |
            Select-Object -Last 1
        if ($found) { return $found.FullName }
    }

    Write-Error "Could not find $toolName. Install the Windows SDK or ensure Microsoft.Windows.SDK.BuildTools NuGet package is restored."
    exit 1
}

$makeAppx = Find-SdkTool 'makeappx.exe'
$signTool = Find-SdkTool 'signtool.exe'
Write-Host "Using MakeAppx: $makeAppx" -ForegroundColor Gray
Write-Host "Using SignTool: $signTool" -ForegroundColor Gray

# --- Clean publish directory ---
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item $publishDir -ItemType Directory | Out-Null

# --- Create self-signed certificate (30-day expiry) ---
Write-Host "`nCreating self-signed certificate ($publisher, 30-day expiry)..." -ForegroundColor Cyan
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $publisher `
    -KeyUsage DigitalSignature `
    -FriendlyName 'AIStatusTray Signing Certificate' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddDays(30) `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")

$certThumbprint = $cert.Thumbprint
Write-Host "Certificate created: $certThumbprint" -ForegroundColor Green

# Export .cer for distribution
$cerPath = Join-Path $publishDir 'AIStatusTray.cer'
Export-Certificate -Cert "Cert:\CurrentUser\My\$certThumbprint" -FilePath $cerPath | Out-Null
Write-Host "Certificate exported: $cerPath" -ForegroundColor Green

# --- Determine architectures to build ---
$allArchitectures = @(
    @{ Platform = 'x64';   RID = 'win-x64';   Label = 'x64' },
    @{ Platform = 'ARM64'; RID = 'win-arm64'; Label = 'ARM64' }
)

if ($Architecture -eq 'All') {
    $architectures = $allArchitectures
} else {
    $architectures = $allArchitectures | Where-Object { $_.Label -eq $Architecture }
}

$builtCount = 0

# --- Build and package for each architecture ---
foreach ($arch in $architectures) {
    $platform = $arch.Platform
    $rid = $arch.RID
    $label = $arch.Label

    Write-Host "`nBuilding for $label..." -ForegroundColor Cyan

    # Build the project directly
    dotnet build $csproj `
        -c Debug `
        -p:Platform=$platform `
        -r $rid `
        --self-contained

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Build failed for $label (Windows SDK may be required for cross-compilation). Skipping."
        continue
    }

    # Create AppX layout from build output
    $buildOutput = Join-Path $projectDir "bin\$platform\Debug\$tfm\$rid"
    $appxDir = Join-Path $publishDir "_layout_$label"
    if (Test-Path $appxDir) { Remove-Item $appxDir -Recurse -Force }
    New-Item $appxDir -ItemType Directory | Out-Null
    Copy-Item "$buildOutput\*" $appxDir -Recurse -Force
    Copy-Item $manifestPath (Join-Path $appxDir 'AppxManifest.xml') -Force
    $layoutImages = Join-Path $appxDir 'Images'
    if (-not (Test-Path $layoutImages)) { New-Item $layoutImages -ItemType Directory | Out-Null }
    Copy-Item "$imagesDir\*" $layoutImages -Force

    if (-not (Test-Path $appxDir)) {
        Write-Warning "AppX layout not found at: $appxDir. Skipping $label."
        continue
    }

    # Package as MSIX
    $msixPath = Join-Path $publishDir "AIStatusTray_$label.msix"
    Write-Host "Packaging $label MSIX..." -ForegroundColor Cyan
    & $makeAppx pack /d $appxDir /p $msixPath /o
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "MakeAppx failed for $label. Skipping."
        continue
    }

    # Sign the MSIX
    Write-Host "Signing $label MSIX..." -ForegroundColor Cyan
    & $signTool sign /fd SHA256 /sha1 $certThumbprint /td SHA256 $msixPath
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "SignTool failed for $label. Skipping."
        continue
    }

    Write-Host "$label package ready: $msixPath" -ForegroundColor Green
    $builtCount++
}

if ($builtCount -eq 0) {
    Remove-Item "Cert:\CurrentUser\My\$certThumbprint" -ErrorAction SilentlyContinue
    Write-Error "No architectures built successfully."
    exit 1
}

# --- Copy install script ---
$installScript = Join-Path $repoRoot 'scripts' 'install.ps1'
Copy-Item $installScript $publishDir

# --- Create zip ---
Write-Host "`nCreating AIStatusTray.zip..." -ForegroundColor Cyan
$zipPath = Join-Path $publishDir 'AIStatusTray.zip'
$filesToZip = Get-ChildItem $publishDir -File | Where-Object { $_.Name -ne 'AIStatusTray.zip' }
Compress-Archive -Path $filesToZip.FullName -DestinationPath $zipPath -Force
Write-Host "Zip created: $zipPath" -ForegroundColor Green

# --- Cleanup temp layout directories and signing cert ---
Get-ChildItem $publishDir -Directory -Filter '_layout_*' | Remove-Item -Recurse -Force
Remove-Item "Cert:\CurrentUser\My\$certThumbprint" -ErrorAction SilentlyContinue

Write-Host "`nDone! Share publish\AIStatusTray.zip with your colleague." -ForegroundColor Green
Write-Host "They should extract it and run: powershell -ExecutionPolicy Bypass -File install.ps1" -ForegroundColor Yellow
if ($builtCount -lt $architectures.Count) {
    Write-Host "`nNote: Some architectures were skipped. Install the Windows 10 SDK for cross-compilation." -ForegroundColor Yellow
}
