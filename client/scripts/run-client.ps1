param(
  [ValidateSet("x64","x86","ARM64")]
  [string]$Platform = "x64",
  [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$Proj = Join-Path $RepoRoot "client\SAAIA.Client.WinUI\SAAIA.Client.WinUI.csproj"

Write-Host "[INFO] RepoRoot: $RepoRoot"
Write-Host "[INFO] Project:  $Proj"
Write-Host "[INFO] Platform: $Platform"

# ---- Load InstallRoot from infra/.env (dev convenience) ----
try {
  $envPath = Join-Path $RepoRoot "infra\.env"
  if (Test-Path $envPath) {
    $line = (Get-Content $envPath | Where-Object { $_ -match '^\s*SAAIA_INSTALL_ROOT\s*=' } | Select-Object -First 1)
    if ($line) {
      $val = ($line -replace '^\s*SAAIA_INSTALL_ROOT\s*=\s*','').Trim().Trim('"').Trim("'")
      if ($val) {
        $env:SAAIA_INSTALL_ROOT = $val
        Write-Host "[INFO] SAAIA_INSTALL_ROOT (from infra/.env): $val" -ForegroundColor DarkGray
      }
    }
  }
} catch {
  # ignore
}

dotnet build $Proj -c $Configuration -p:Platform=$Platform

# dotnet run needs the unpackaged profile (Package profile is for VS/MSIX tooling)
dotnet run --no-build --project $Proj -c $Configuration -p:Platform=$Platform --launch-profile "SAAIA.Client.WinUI (Unpackaged)"
