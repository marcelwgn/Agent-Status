#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs AgentStatus on this machine.
.DESCRIPTION
    1. Installs the self-signed certificate to the local machine Trusted People store.
    2. Detects the system architecture (x64 vs ARM64).
    3. Installs the matching .msix package.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# --- Install certificate ---
$certPath = Join-Path $scriptDir 'AgentStatus.cer'
if (-not (Test-Path $certPath)) {
    Write-Error "Certificate not found: $certPath"
    exit 1
}

Write-Host "Installing certificate..." -ForegroundColor Cyan
Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
Write-Host "Certificate installed to Trusted People store." -ForegroundColor Green

# --- Detect architecture ---
$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
switch ($arch) {
    'X64'   { $msixName = 'AgentStatus.Taskbar_x64.msix' }
    'Arm64' { $msixName = 'AgentStatus.Taskbar_ARM64.msix' }
    default {
        Write-Error "Unsupported architecture: $arch"
        exit 1
    }
}

$msixPath = Join-Path $scriptDir $msixName
if (-not (Test-Path $msixPath)) {
    Write-Error "MSIX package not found: $msixPath"
    exit 1
}

# --- Install MSIX ---
Write-Host "Installing $msixName..." -ForegroundColor Cyan
Add-AppxPackage -Path $msixPath
Write-Host "AgentStatus installed successfully!" -ForegroundColor Green
