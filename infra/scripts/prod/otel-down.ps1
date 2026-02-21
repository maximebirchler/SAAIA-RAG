param(
  [switch]$RemoveVolumes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_common.ps1')

$repo = Get-RepoRoot

$envFileRel = 'infra/.env'
$envPath = Resolve-PathFromRepo $repo $envFileRel
if (!(Test-Path $envPath)) { throw "Missing $envFileRel" }

$envMap = Read-DotEnv $envPath
$installRoot = Get-InstallRoot -RepoRoot $repo -Env $envMap
$env:SAAIA_INSTALL_ROOT = Convert-ToDockerPath $installRoot

$composeProd = Resolve-PathFromRepo $repo 'infra/docker-compose.prod.yml'
$composeOtel = Resolve-PathFromRepo $repo 'infra/docker-compose.otel.yml'

$args = @('down')
if ($RemoveVolumes) { $args += @('-v') }

$full = @('compose','-f', $composeProd, '-f', $composeOtel, '--env-file', $envPath) + $args
Write-Host ('> docker ' + ($full -join ' ')) -ForegroundColor Cyan
& docker @full
if ($LASTEXITCODE -ne 0) { throw "docker compose failed ($LASTEXITCODE)" }
