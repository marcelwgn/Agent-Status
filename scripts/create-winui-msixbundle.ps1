<#
.SYNOPSIS
    Builds AgentStatus and creates an unsigned .msixbundle for Microsoft Store submission.
.DESCRIPTION
    1. Builds AgentStatus for each target architecture (x64, ARM64).
    2. Creates an AppX layout directory for each build (output + manifest + images).
    3. Packs each layout into an individual .msix using makeappx.exe.
    4. Bundles all .msix files into a single .msixbundle.
    5. Output is unsigned — the Microsoft Store handles signing.
.PARAMETER Architecture
    Which architectures to include: x64, ARM64, or All (default).
.PARAMETER Configuration
    Build configuration. Default is Release.
.EXAMPLE
    .\create-msixbundle.ps1
    .\create-msixbundle.ps1 -Architecture x64
    .\create-msixbundle.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64', 'All')]
    [string]$Architecture = 'All',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
$projectDir = Join-Path $repoRoot 'AgentStatus'
$csproj = Join-Path $projectDir 'AgentStatus.csproj'
$manifestPath = Join-Path $projectDir 'Package.appxmanifest'
$imagesDir = Join-Path $projectDir 'Images'
$publishDir = Join-Path $repoRoot 'publish'
$tfm = 'net10.0-windows10.0.26100.0'

# --- Validate prerequisites ---
if (-not (Test-Path $csproj)) {
    Write-Error "Project not found: $csproj"
    exit 1
}
if (-not (Test-Path $manifestPath)) {
    Write-Error "Package manifest not found: $manifestPath"
    exit 1
}

# --- Locate SDK tools ---
function Find-SdkTool([string]$toolName) {
    $sdkBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $sdkBin) {
        $found = Get-ChildItem $sdkBin -Recurse -Filter $toolName -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -match 'x64' } |
            Select-Object -Last 1
        if ($found) { return $found.FullName }
    }

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
Write-Host "Using MakeAppx: $makeAppx" -ForegroundColor Gray

# --- Clean publish directory ---
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item $publishDir -ItemType Directory | Out-Null

$tempRoot = Join-Path $publishDir '_temp'
New-Item $tempRoot -ItemType Directory | Out-Null

$msixDir = Join-Path $tempRoot 'msix'
New-Item $msixDir -ItemType Directory | Out-Null

# --- Determine architectures ---
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

# --- Build and pack each architecture ---
foreach ($arch in $architectures) {
    $platform = $arch.Platform
    $rid = $arch.RID
    $label = $arch.Label

    Write-Host "`n========== Building for $label ==========" -ForegroundColor Cyan

    dotnet build $csproj `
        -c $Configuration `
        -p:Platform=$platform `
        -r $rid `
        --self-contained

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Build failed for $label. Skipping."
        continue
    }

    # Locate build output
    $buildOutput = Join-Path $projectDir "bin\$platform\$Configuration\$tfm\$rid"
    if (-not (Test-Path $buildOutput)) {
        Write-Warning "Build output not found at $buildOutput. Skipping $label."
        continue
    }

    # Create AppX layout directory
    $layoutDir = Join-Path $tempRoot "layout_$label"
    New-Item $layoutDir -ItemType Directory | Out-Null

    Write-Host "Creating AppX layout for $label..." -ForegroundColor Cyan

    # Copy build output (includes correct AppxManifest.xml and resources.pri)
    Copy-Item "$buildOutput\*" $layoutDir -Recurse -Force

    # Copy store images into layout
    $layoutImages = Join-Path $layoutDir 'Images'
    if (-not (Test-Path $layoutImages)) {
        New-Item $layoutImages -ItemType Directory | Out-Null
    }
    Copy-Item "$imagesDir\*" $layoutImages -Force

    # Create unqualified copies from scale-100 variants so the manifest references resolve
    Get-ChildItem $layoutImages -Filter '*.scale-100.png' | ForEach-Object {
        $baseName = $_.Name -replace '\.scale-100\.png$', '.png'
        Copy-Item $_.FullName (Join-Path $layoutImages $baseName)
    }

    # Pack into MSIX (unsigned)
    $msixPath = Join-Path $msixDir "AgentStatus_$label.msix"
    Write-Host "Packing $label MSIX..." -ForegroundColor Cyan
    & $makeAppx pack /d $layoutDir /p $msixPath /o
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "MakeAppx pack failed for $label. Skipping."
        continue
    }

    Write-Host "$label MSIX created: $msixPath" -ForegroundColor Green
    $builtCount++
}

if ($builtCount -eq 0) {
    Write-Error "No architectures built successfully."
    exit 1
}

# --- Create bundle ---
Write-Host "`n========== Creating MSIX Bundle ==========" -ForegroundColor Cyan

# Read version from the first layout's manifest so the bundle version matches
$firstLayout = Get-ChildItem $tempRoot -Directory -Filter 'layout_*' | Select-Object -First 1
[xml]$layoutManifest = Get-Content (Join-Path $firstLayout.FullName 'AppxManifest.xml')
$bundleVersion = $layoutManifest.Package.Identity.Version

$bundlePath = Join-Path $publishDir 'AgentStatus.msixbundle'
& $makeAppx bundle /d $msixDir /p $bundlePath /bv $bundleVersion /o
if ($LASTEXITCODE -ne 0) {
    Write-Error "MakeAppx bundle failed."
    exit 1
}

# --- Cleanup temp ---
Remove-Item $tempRoot -Recurse -Force

# --- Summary ---
Write-Host "`n========== Done ==========" -ForegroundColor Green
Write-Host "Bundle ready: $bundlePath" -ForegroundColor Green
Write-Host "Upload this file to Partner Center for Store submission." -ForegroundColor Yellow
if ($builtCount -lt $architectures.Count) {
    Write-Host "Note: Some architectures were skipped. Install the Windows SDK for cross-compilation." -ForegroundColor Yellow
}
