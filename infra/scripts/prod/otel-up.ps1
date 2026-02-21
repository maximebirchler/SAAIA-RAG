param(
  [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_common.ps1')

$repo = Get-RepoRoot

$envFileRel = 'infra/.env'
$envPath = Resolve-PathFromRepo $repo $envFileRel
if (!(Test-Path $envPath)) { throw "Missing $envFileRel" }

$envMap = Read-DotEnv $envPath

# Compute InstallRoot and export SAAIA_INSTALL_ROOT (used by compose bind mounts)
$installRoot = Get-InstallRoot -RepoRoot $repo -Env $envMap
$env:SAAIA_INSTALL_ROOT = Convert-ToDockerPath $installRoot

$composeProd = Resolve-PathFromRepo $repo 'infra/docker-compose.prod.yml'
$composeOtel = Resolve-PathFromRepo $repo 'infra/docker-compose.otel.yml'

$args = @('up','-d')
if (-not $NoBuild) { $args += @('--build') }

$full = @('compose','-f', $composeProd, '-f', $composeOtel, '--env-file', $envPath) + $args
Write-Host ('> docker ' + ($full -join ' ')) -ForegroundColor Cyan
& docker @full
if ($LASTEXITCODE -ne 0) { throw "docker compose failed ($LASTEXITCODE)" }

# Verify /ready (same port logic as install.ps1)
$port = '5122'
if ($envMap.ContainsKey('BACKEND_HOST_PORT') -and -not [string]::IsNullOrWhiteSpace($envMap['BACKEND_HOST_PORT'])) { $port = $envMap['BACKEND_HOST_PORT'] }
$readyUrl = "http://localhost:$port/ready"

Write-Host "== Verify /ready ==" -ForegroundColor Cyan
$r = Invoke-WebRequest -UseBasicParsing -Uri $readyUrl -TimeoutSec 10
Write-Host ("/ready => {0}" -f $r.StatusCode) -ForegroundColor Green

Write-Host ""
Write-Host "OTel collector logs:" -ForegroundColor Cyan
Write-Host "docker compose -f $composeProd -f $composeOtel --env-file $envPath logs -f otel-collector"
