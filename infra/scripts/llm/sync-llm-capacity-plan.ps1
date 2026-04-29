param(
  [string]$InstallRoot = $(if ($env:SAAIA_INSTALL_ROOT) { $env:SAAIA_INSTALL_ROOT } else { 'C:\SAAIA' }),
  [int]$LicenseSeats = $(if ($env:SAAIA_LICENSE_SEATS -as [int]) { [int]$env:SAAIA_LICENSE_SEATS } else { 1 }),
  [string]$BindAddr = '127.0.0.1',
  [int]$HostPort = 1234,
  [int]$ContainerPort = 8080,
  [switch]$Force,
  [switch]$PlanOnly,
  [switch]$NoWait
)

$ErrorActionPreference = 'Stop'

function Info($m) { Write-Host "[INFO] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[ OK ] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[WARN] $m" -ForegroundColor Yellow }

$seats = [Math]::Max(1, $LicenseSeats)
$deployDir = Join-Path $InstallRoot 'deploy'
$planPath = Join-Path $deployDir 'llm.capacity-plan.json'
$installer = Join-Path $PSScriptRoot 'install-llm.ps1'

if (!(Test-Path $installer)) {
  throw "Missing LLM installer: $installer"
}

$currentSeats = $null
if (Test-Path $planPath) {
  try {
    $plan = Get-Content -Raw $planPath | ConvertFrom-Json
    if ($null -ne $plan.licenseSeats) {
      $currentSeats = [int]$plan.licenseSeats
    }
  } catch {
    Warn "Existing capacity plan is unreadable and will be regenerated: $planPath"
  }
}

if (-not $Force -and $null -ne $currentSeats -and $currentSeats -eq $seats) {
  Ok "LLM capacity plan already matches license seats ($seats)."
  exit 0
}

if ($null -eq $currentSeats) {
  Info "No valid LLM capacity plan found. Generating one for seats=$seats."
} else {
  Info "License seats changed: plan=$currentSeats current=$seats. Recalculating LLM capacity."
}

$args = @(
  '-InstallRoot', $InstallRoot,
  '-LicenseSeats', $seats,
  '-BindAddr', $BindAddr,
  '-HostPort', $HostPort,
  '-ContainerPort', $ContainerPort,
  '-AutoPlan'
)

if ($PlanOnly) { $args += '-NoDockerUp' }
if ($NoWait) { $args += '-NoWait' }

& $installer @args
if ($LASTEXITCODE -ne 0) {
  throw "install-llm.ps1 failed ($LASTEXITCODE)"
}

Ok "LLM capacity plan synchronized with license seats ($seats)."
