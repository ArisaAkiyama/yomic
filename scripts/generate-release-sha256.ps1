<#
.SYNOPSIS
    Computes SHA-256 checksums for built Yomic Windows Setup installers and formats a release-notes snippet
    compatible with UpdateService.ExtractExpectedSha256.

.EXAMPLE
    .\scripts\generate-release-sha256.ps1 -InstallerPath ".\Installer\Output\Yomic_Setup_v2.2.1.exe"
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$InstallerPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $searchDirs = @(
        (Join-Path $PSScriptRoot "..\dist"),
        (Join-Path $PSScriptRoot "..\Installer\Output")
    )
    $candidates = $searchDirs |
        Where-Object { Test-Path $_ } |
        ForEach-Object { Get-ChildItem -Path $_ -Filter "Yomic_Setup_*.exe" -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTimeUtc -Descending
    if (-not $candidates -or $candidates.Count -eq 0) {
        Write-Error "No Yomic_Setup_*.exe found in dist\ or Installer\Output. Pass -InstallerPath explicitly."
        exit 1
    }
    $InstallerPath = $candidates[0].FullName
}

if (-not (Test-Path $InstallerPath)) {
    Write-Error "Installer file not found: $InstallerPath"
    exit 1
}

$item = Get-Item $InstallerPath
$hash = (Get-FileHash -Path $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$shaFile = "$($item.FullName).sha256"
"$hash  $($item.Name)" | Set-Content -Path $shaFile -Encoding ascii

Write-Host ""
Write-Host "=== Yomic Release Integrity Checksum ===" -ForegroundColor Cyan
Write-Host "File   : $($item.Name)"
Write-Host "Size   : $([Math]::Round($item.Length / 1MB, 2)) MB"
Write-Host "SHA-256: $hash"
Write-Host "Saved  : $shaFile"
Write-Host ""
Write-Host "Paste into GitHub Release Notes (auto-verified by Yomic UpdateService):" -ForegroundColor Green
Write-Host "### Integrity Checksum"
Write-Host "SHA-256: ``$hash``"
Write-Host ""
