[CmdletBinding()]
param(
    [string]$LocalAppDataRoot = $env:LOCALAPPDATA,
    [string]$BackupRoot = "",
    [switch]$Apply
)

$ErrorActionPreference = "Stop"

function Get-TextSha256Hex {
    param([string]$Path)
    $text = [System.IO.File]::ReadAllText($Path)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hashBytes)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

$auditScript = Join-Path $PSScriptRoot "verify-governance-artifacts.ps1"
$auditJson = powershell -ExecutionPolicy Bypass -File $auditScript -LocalAppDataRoot $LocalAppDataRoot -AsJson | ConvertFrom-Json

$repairable = @(
    @($auditJson.governance.items) + @($auditJson.llm.items) |
        Where-Object { $_.exists -and ($_.checksumStatus -eq "mismatch" -or $_.checksumStatus -eq "missing_sidecar") }
)

$missingFiles = @(
    @($auditJson.governance.items) + @($auditJson.llm.items) |
        Where-Object { $_.checksumStatus -eq "missing_file" }
)

Write-Host "SAAIA governance artifact repair helper"
Write-Host "LocalAppData root: $LocalAppDataRoot"
Write-Host ""

if ($repairable.Count -eq 0) {
    Write-Host "No sidecar repair candidate detected."
}
else {
    Write-Host "Repairable sidecar issues:"
    $repairable |
        Select-Object relativePath, checksumStatus, fullPath |
        Format-Table -AutoSize
}

if ($missingFiles.Count -gt 0) {
    Write-Host ""
    Write-Host "Missing JSON artifacts are not created by this helper:"
    $missingFiles |
        Select-Object -ExpandProperty relativePath |
        ForEach-Object { Write-Host " - $_" }
    Write-Host ""
    Write-Host "Next step for missing files: launch the SAAIA client/governance init flow, then rerun verify-governance-artifacts.ps1."
}

if (-not $Apply) {
    Write-Host ""
    Write-Host "Dry run only. Re-run with -Apply to rewrite sidecar files for the repairable items above."
    exit 0
}

if ($repairable.Count -eq 0) {
    Write-Host ""
    Write-Host "Nothing to repair."
    exit 0
}

$effectiveBackupRoot = if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    Join-Path $LocalAppDataRoot ("SAAIA\\support\\governance-sidecar-backup_" + (Get-Date -Format "yyyyMMdd_HHmmss"))
}
else {
    $BackupRoot
}

New-Item -ItemType Directory -Force -Path $effectiveBackupRoot | Out-Null

foreach ($item in $repairable) {
    $sidecarPath = $item.fullPath + ".sha256"
    if (Test-Path -LiteralPath $sidecarPath -PathType Leaf) {
        $backupName = ($item.relativePath -replace '[\\\\/:*?\"<>|]', '_') + ".sha256.bak"
        Copy-Item -LiteralPath $sidecarPath -Destination (Join-Path $effectiveBackupRoot $backupName) -Force
    }

    $checksum = Get-TextSha256Hex -Path $item.fullPath
    Set-Content -LiteralPath $sidecarPath -Value $checksum -Encoding UTF8
}

Write-Host ""
Write-Host "Sidecar repair applied. Backup directory: $effectiveBackupRoot"
Write-Host "Re-running audit..."
Write-Host ""

powershell -ExecutionPolicy Bypass -File $auditScript -LocalAppDataRoot $LocalAppDataRoot
