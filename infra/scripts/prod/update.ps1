param(
  [string]$ComposeFile = 'infra/docker-compose.prod.yml',
  [string]$EnvFile     = 'infra/.env'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_common.ps1')
$repo = Get-RepoRoot

$envPath = Resolve-PathFromRepo $repo $EnvFile
$env = Read-DotEnv $envPath

# Ensure docker uses the right bind-mount root
$installRoot = Get-InstallRoot -RepoRoot $repo -Env $env
$env:SAAIA_INSTALL_ROOT = Convert-ToDockerPath $installRoot

Write-Host "== Update stack ==" -ForegroundColor Cyan
Write-Host "InstallRoot: $installRoot"

Write-Host "== Pull images =="
Docker-Compose $repo $ComposeFile $EnvFile @('pull')

Write-Host "== Rebuild + restart =="
Docker-Compose $repo $ComposeFile $EnvFile @('up','-d','--build')

Write-Host "== Status =="
Docker-Compose $repo $ComposeFile $EnvFile @('ps')
