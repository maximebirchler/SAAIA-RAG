param(
  [string]$ComposeFile = 'infra/docker-compose.prod.yml',
  [string]$EnvFile     = 'infra/.env',
  [string]$OutDir      = '',
  [switch]$ResignOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Make console/file output predictable (Windows PowerShell 5.1 often defaults to OEM codepages)
try {
  $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
  [Console]::OutputEncoding = $utf8NoBom
  $OutputEncoding = $utf8NoBom
  $PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
} catch {
  # ignore
}

. (Join-Path $PSScriptRoot '_common.ps1')
$repo = Get-RepoRoot
$env = Read-DotEnv (Resolve-PathFromRepo $repo $EnvFile)

# Ensure docker uses the right bind-mount root
$installRoot = Get-InstallRoot -RepoRoot $repo -Env $env
$env:SAAIA_INSTALL_ROOT = Convert-ToDockerPath $installRoot
$deployDirExpected = [System.IO.Path]::GetFullPath((Join-Path $installRoot 'deploy'))

if ($ResignOnly) {
  Require $env @('POSTGRES_PASSWORD','SAAIA_AUTH_PEPPER','SAAIA_CONFIG_PRIVATE_KEY_PATH')

  # Bootstrap key only required if bootstrap is enabled (default true).
  $bootEnabled = $true
  $bootRaw = ''
  if ($env.ContainsKey('SAAIA_BOOTSTRAP_ENABLED') -and $null -ne $env['SAAIA_BOOTSTRAP_ENABLED']) {
    $bootRaw = [string]$env['SAAIA_BOOTSTRAP_ENABLED']
  }
  $bootRaw = $bootRaw.Trim().ToLowerInvariant()
  if ($bootRaw -in @('0','false','no','n','off')) { $bootEnabled = $false }

  if ($bootEnabled) {
    Require $env @('SAAIA_BOOTSTRAP_API_KEY')
  }

  New-SignedConfig -RepoRoot $repo -env $env -TemplateRel 'infra/config/deployment.config.prod.template.json' -DeployDirRel $deployDirExpected | Out-Null
  Write-Host "Re-signed config in: $deployDirExpected. Restart backend to apply."
  exit 0
}

$target = $null
if ($OutDir -and $OutDir.Trim() -ne '') {
  $ts = Get-Date -Format "yyyyMMdd_HHmmss"

  $base = $OutDir
  if (-not [System.IO.Path]::IsPathRooted($base)) {
    $base = Join-Path $installRoot $base
  }

  $target = (New-Item -ItemType Directory -Force -Path (Join-Path $base $ts)).FullName
  Write-Host "Diag out: $target"
}

Write-Host "== docker compose ps ==" -ForegroundColor Cyan
$psLines = (docker compose -f (Resolve-PathFromRepo $repo $ComposeFile) --env-file (Resolve-PathFromRepo $repo $EnvFile) ps)
$psText = ($psLines | Out-String).TrimEnd()
Write-Host $psText
if ($target) { $psText | Out-File (Join-Path $target 'compose_ps.txt') }

Write-Host "== /ready ==" -ForegroundColor Cyan
$port = '5122'
if ($env.ContainsKey('BACKEND_HOST_PORT') -and -not [string]::IsNullOrWhiteSpace($env['BACKEND_HOST_PORT'])) { $port = $env['BACKEND_HOST_PORT'] }

try {
  $r = Invoke-WebRequest -UseBasicParsing "http://localhost:$port/ready" -TimeoutSec 10
  Write-Host "/ready => $($r.StatusCode)"
  if ($target) { $r.Content | Out-File (Join-Path $target 'ready.json') -Encoding utf8 }
} catch {
  Write-Warning "Cannot reach /ready"
}

foreach ($svc in @('backend','qdrant','tei','postgres')) {
  Write-Host "== logs: $svc (last 200) ==" -ForegroundColor Cyan
  $logLines = (docker compose -f (Resolve-PathFromRepo $repo $ComposeFile) --env-file (Resolve-PathFromRepo $repo $EnvFile) logs --no-color --tail 200 $svc)
  $logText = ($logLines | Out-String).TrimEnd()
  if ($target) { $logText | Out-File (Join-Path $target "logs_$svc.txt") } else { Write-Host $logText }
}

Write-Host "DONE"
