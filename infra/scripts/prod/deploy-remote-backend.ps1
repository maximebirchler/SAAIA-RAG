param(
  [string]$Server            = 'maxime@100.80.213.61',
  [string]$ContextName       = 'saaia-server',
  [string]$ComposeFile       = 'infra/docker-compose.prod.yml',
  [string]$EnvFile           = 'infra/.env.server-linux',
  [string]$DeployWorkDirRel  = 'out/remote-deploy',
  [switch]$WithDependencies,
  [switch]$SkipRemoteProvision,
  [switch]$SkipConfigSync,
  [switch]$SkipReadyCheck,
  [switch]$NoCache
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_common.ps1')

function Parse-Bool([string]$raw, [bool]$default) {
  if ([string]::IsNullOrWhiteSpace($raw)) { return $default }
  $x = $raw.Trim().ToLowerInvariant()
  if ($x -in @('1','true','yes','y','on')) { return $true }
  if ($x -in @('0','false','no','n','off')) { return $false }
  return $default
}

function Require-Command([string]$Name) {
  $cmd = Get-Command $Name -ErrorAction SilentlyContinue
  if ($null -eq $cmd) {
    throw "Missing command '$Name' on this laptop. Install it and retry."
  }
}

function Ensure-DockerContext([string]$Name, [string]$ServerSpec) {
  $exists = $false
  try {
    & docker context inspect $Name *> $null
    if ($LASTEXITCODE -eq 0) { $exists = $true }
  } catch { $exists = $false }

  if (-not $exists) {
    Write-Host "== Create docker context '$Name' ==" -ForegroundColor Cyan
    & docker context create $Name --docker "host=ssh://$ServerSpec"
    if ($LASTEXITCODE -ne 0) { throw "docker context create failed ($LASTEXITCODE)" }
  }
}

function Wait-BackendReady([string]$ServerSpec, [string]$Port, [int]$MaxTries = 40, [int]$DelaySeconds = 2) {
  Write-Host "== Verify backend /ready on remote server ==" -ForegroundColor Cyan
  for ($i = 1; $i -le $MaxTries; $i++) {
    $cmd = "curl -fsS http://127.0.0.1:$Port/ready >/dev/null && echo READY || true"
    $out = & ssh $ServerSpec $cmd
    if ($LASTEXITCODE -eq 0 -and ($out -match 'READY')) {
      Write-Host "/ready => OK (try $i/$MaxTries)" -ForegroundColor Green
      return
    }
    Start-Sleep -Seconds $DelaySeconds
  }
  Write-Warning "Backend /ready did not become healthy in time. Check remote logs:"
  Write-Warning "docker --context $ContextName compose -f $ComposeFile --env-file $EnvFile logs -f backend"
}

$repo = Get-RepoRoot
$envPath = Resolve-PathFromRepo $repo $EnvFile
$composePath = Resolve-PathFromRepo $repo $ComposeFile
$envMap = Read-DotEnv $envPath

Require-Command 'docker'
Require-Command 'ssh'
Require-Command 'scp'
Require-Command 'dotnet'

$serverUser = $Server
if ($Server -match '^(?<u>[^@]+)@(?<h>.+)$') {
  $serverUser = $Matches['u']
}

if (-not $envMap.ContainsKey('SAAIA_INSTALL_ROOT') -or [string]::IsNullOrWhiteSpace($envMap['SAAIA_INSTALL_ROOT'])) {
  throw "SAAIA_INSTALL_ROOT is required in $EnvFile for remote deploy."
}

$installRoot = ([string]$envMap['SAAIA_INSTALL_ROOT']).Trim()

$deployDirName = if ($envMap.ContainsKey('SAAIA_DEPLOY_DIR') -and -not [string]::IsNullOrWhiteSpace($envMap['SAAIA_DEPLOY_DIR'])) {
  ([string]$envMap['SAAIA_DEPLOY_DIR']).Trim()
}
else {
  'deploy'
}

if ($deployDirName.StartsWith('/')) {
  $deployDirRemote = $deployDirName
}
else {
  $deployDirRemote = "$($installRoot.TrimEnd('/'))/$deployDirName"
}
$backendPort = if ($envMap.ContainsKey('BACKEND_HOST_PORT') -and -not [string]::IsNullOrWhiteSpace($envMap['BACKEND_HOST_PORT'])) { [string]$envMap['BACKEND_HOST_PORT'] } else { '5122' }

Write-Host "== Remote deploy target ==" -ForegroundColor Cyan
Write-Host "Server:       $Server"
Write-Host "ContextName:  $ContextName"
Write-Host "InstallRoot:  $installRoot"
Write-Host "DeployDir:    $deployDirRemote"
Write-Host "ComposeFile:  $composePath"
Write-Host "EnvFile:      $envPath"

# Ensure remote runtime directories exist.
if (-not $SkipRemoteProvision) {
  $mkdirCmd = @(
    "sudo mkdir -p '$installRoot'",
    "sudo mkdir -p '$deployDirRemote'",
    "sudo mkdir -p '$installRoot/documents'",
    "sudo mkdir -p '$installRoot/data/backend'",
    "sudo mkdir -p '$installRoot/data/postgres'",
    "sudo mkdir -p '$installRoot/data/qdrant'",
    "sudo mkdir -p '$installRoot/cache/tei'",
    "sudo chown -R '$serverUser':'$serverUser' '$installRoot'"
  ) -join ' && '

  Write-Host "== Ensure remote runtime directories ==" -ForegroundColor Cyan
  & ssh $Server $mkdirCmd
  if ($LASTEXITCODE -ne 0) { throw "Remote directory provisioning failed ($LASTEXITCODE)" }
}
else {
  Write-Host "== Skip remote runtime directory provisioning ==" -ForegroundColor Yellow
}

# Optional: always (re)generate and sync signed config locally, but never copy the private key.
if (-not $SkipConfigSync) {
  Write-Host "== Generate + sign deployment config locally ==" -ForegroundColor Cyan

  Require $envMap @('POSTGRES_PASSWORD','SAAIA_AUTH_PEPPER','SAAIA_CONFIG_PRIVATE_KEY_PATH')
  $bootEnabled = $true
  if ($envMap.ContainsKey('SAAIA_BOOTSTRAP_ENABLED')) {
    $bootEnabled = Parse-Bool ([string]$envMap['SAAIA_BOOTSTRAP_ENABLED']) $true
  }
  if ($bootEnabled) { Require $envMap @('SAAIA_BOOTSTRAP_API_KEY') }

  $deployLocal = Resolve-PathFromRepo $repo $DeployWorkDirRel
  if (!(Test-Path $deployLocal)) { New-Item -ItemType Directory -Force -Path $deployLocal | Out-Null }

  $cfg = New-SignedConfig `
    -RepoRoot $repo `
    -env $envMap `
    -TemplateRel 'infra/config/deployment.config.prod.template.json' `
    -DeployDirRel $DeployWorkDirRel

  Write-Host "== Copy signed config to remote server ==" -ForegroundColor Cyan
  & scp $cfg.ConfigPath "${Server}:$deployDirRemote/deployment.config.json"
  if ($LASTEXITCODE -ne 0) { throw "scp deployment.config.json failed ($LASTEXITCODE)" }

  & scp $cfg.SigPath "${Server}:$deployDirRemote/deployment.config.sig"
  if ($LASTEXITCODE -ne 0) { throw "scp deployment.config.sig failed ($LASTEXITCODE)" }
}
else {
  Write-Host "== Skip config sync ==" -ForegroundColor Yellow
}

Ensure-DockerContext -Name $ContextName -ServerSpec $Server

if ($NoCache) {
  Write-Host "== Remote rebuild backend (no cache) ==" -ForegroundColor Cyan
  $buildArgs = @('--context', $ContextName, 'compose', '-f', $composePath, '--env-file', $envPath, 'build', '--no-cache', 'backend')
  & docker @buildArgs
  if ($LASTEXITCODE -ne 0) { throw "Remote backend build failed ($LASTEXITCODE)" }
}

Write-Host "== Remote deploy ==" -ForegroundColor Cyan

if ($WithDependencies) {
  $upArgs = @('--context', $ContextName, 'compose', '-f', $composePath, '--env-file', $envPath, 'up', '-d', '--build', 'backend', 'postgres', 'qdrant', 'tei')
}
else {
  $upArgs = @('--context', $ContextName, 'compose', '-f', $composePath, '--env-file', $envPath, 'up', '-d', '--build', '--no-deps', 'backend')
}

& docker @upArgs
if ($LASTEXITCODE -ne 0) { throw "Remote deploy failed ($LASTEXITCODE)" }

Write-Host "== Remote compose status ==" -ForegroundColor Cyan
& docker --context $ContextName compose -f $composePath --env-file $envPath ps
if ($LASTEXITCODE -ne 0) { throw "Remote compose ps failed ($LASTEXITCODE)" }

if (-not $SkipReadyCheck) {
  Wait-BackendReady -ServerSpec $Server -Port $backendPort
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "If SAAIA_BIND_ADDR=127.0.0.1 on the server, you can tunnel the backend with:" -ForegroundColor Cyan
Write-Host "ssh -L $backendPort`:127.0.0.1`:$backendPort $Server"
