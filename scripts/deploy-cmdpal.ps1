# Deploy the CmdPal extension as a loose-file MSIX package for development.
# Run this after building the solution with:
#   msbuild AgentStatus.slnx /p:Platform=x64 /p:Configuration=Debug

param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $repoRoot "AgentStatus.CmdPal\bin\$Platform\$Configuration\net9.0-windows10.0.26100.0\win-$($Platform.ToLower())"
$manifestSource = Join-Path $repoRoot "AgentStatus.CmdPal\Package.appxmanifest"
$manifestDest = Join-Path $outputDir "AppxManifest.xml"
$assetsSource = Join-Path $repoRoot "AgentStatus.CmdPal\Assets"

if (-not (Test-Path (Join-Path $outputDir "AgentStatus.CmdPal.exe"))) {
    Write-Error "Build output not found at $outputDir. Build the solution first."
    exit 1
}

Write-Host "Preparing deployment layout..." -ForegroundColor Cyan

# Process the manifest (replace MSBuild tokens, fix asset paths)
$content = Get-Content $manifestSource -Raw
$content = $content -replace '\$targetnametoken\$', 'AgentStatus.CmdPal'
$content = $content -replace '\$targetentrypoint\$', 'AgentStatus.CmdPal.exe'
$content = $content -replace 'Square150x150Logo\.png', 'Square150x150Logo.scale-200.png'
$content = $content -replace 'Square44x44Logo\.png', 'Square44x44Logo.scale-200.png'
$content = $content -replace 'Wide310x150Logo\.png', 'Wide310x150Logo.scale-200.png'
$content = $content -replace 'SplashScreen\.png', 'SplashScreen.scale-200.png'
Set-Content $manifestDest $content -NoNewline

# Copy assets
$assetsDir = Join-Path $outputDir "Assets"
if (-not (Test-Path $assetsDir)) { New-Item $assetsDir -ItemType Directory -Force | Out-Null }
Copy-Item "$assetsSource\*" $assetsDir -Force

# Create Public folder
$publicDir = Join-Path $outputDir "Public"
if (-not (Test-Path $publicDir)) { New-Item $publicDir -ItemType Directory -Force | Out-Null }

# Unregister old version if present
$existing = Get-AppxPackage -Name "AgentStatus.CmdPal" -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing existing registration..." -ForegroundColor Yellow
    Remove-AppxPackage $existing
}

# Register the loose-file package
Write-Host "Registering extension..." -ForegroundColor Cyan
Add-AppxPackage -Register $manifestDest -ForceApplicationShutdown

$pkg = Get-AppxPackage -Name "AgentStatus.CmdPal"
if ($pkg -and $pkg.Status -eq "Ok") {
    Write-Host "Extension registered successfully!" -ForegroundColor Green
    Write-Host "  Name: $($pkg.Name)"
    Write-Host "  Version: $($pkg.Version)"
    Write-Host "  Location: $($pkg.InstallLocation)"
    Write-Host ""
    Write-Host "Open PowerToys Command Palette to see the Agent Status dock band." -ForegroundColor Cyan
} else {
    Write-Error "Registration failed. Check Event Viewer for details."
}
