param(
  [Parameter(Mandatory=$true)][string]$InDir,
  [string]$ComposeFile = 'infra/docker-compose.prod.yml',
  [string]$EnvFile = 'infra/.env',
  [int]$ReadyRetries = 60,
  [int]$PostgresRetries = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/_common.ps1"

$repo = Get-RepoRoot
$envPath = Resolve-PathFromRepo $repo $EnvFile
$env = Read-DotEnv -EnvPath $envPath

# Ensure docker uses the right bind-mount root
$installRoot = Get-InstallRoot -RepoRoot $repo -Env $env
$env:SAAIA_INSTALL_ROOT = Convert-ToDockerPath $installRoot

$composePath = Resolve-PathFromRepo $repo $ComposeFile
$inPath = (Resolve-Path $InDir).Path
$dump = Join-Path $inPath 'postgres_dump.sql'
$qTar = Join-Path $inPath 'qdrant_storage.tgz'
if (!(Test-Path $dump)) { throw "Missing $dump" }
if (!(Test-Path $qTar)) { throw "Missing $qTar" }

# DB params
$db = 'saaia'; if ($env.ContainsKey('POSTGRES_DB') -and -not [string]::IsNullOrWhiteSpace($env['POSTGRES_DB'])) { $db = [string]$env['POSTGRES_DB'] }
$usr = 'saaia'; if ($env.ContainsKey('POSTGRES_USER') -and -not [string]::IsNullOrWhiteSpace($env['POSTGRES_USER'])) { $usr = [string]$env['POSTGRES_USER'] }
$pwd = '';    if ($env.ContainsKey('POSTGRES_PASSWORD')) { $pwd = [string]$env['POSTGRES_PASSWORD'] }

Write-Host "== Restore ==" -ForegroundColor Cyan
Write-Host "InstallRoot: $installRoot"
Write-Host "From:        $inPath"

function Get-ComposeContainerId([string]$service) {
  # When a service is stopped (e.g. qdrant during restore), `docker compose ps -q` may return
  # nothing. Use `-a` to include stopped containers and coerce output to a string
  # (PowerShell returns $null for empty native output).
  $args = @('compose','-f',$composePath,'--env-file',$envPath,'ps','-a','-q',$service)
  $out = (& docker @args 2>$null)
  $line = $out | Select-Object -First 1
  if ($null -eq $line) { $line = '' }
  $id = ([string]$line).Trim()
  if (-not $id) { throw "Cannot find container id for service '$service'" }
  return $id
}

function Wait-Postgres([int]$retries) {
  # WinPS 5.1 turns native stderr into ErrorRecords; with $ErrorActionPreference='Stop' that becomes terminating.
  # Use pg_isready (quiet) + temporarily relax error action to avoid stopping while postgres is still booting.
  $oldEap = $ErrorActionPreference
  $ErrorActionPreference = 'SilentlyContinue'
  try {
    for ($i=1; $i -le $retries; $i++) {
      & docker compose -f $composePath --env-file $envPath exec -T postgres pg_isready -q -U $usr -d $db 2>$null | Out-Null
      if ($LASTEXITCODE -eq 0) {
        Write-Host ("Postgres ready (try {0}/{1})" -f $i,$retries)
        return $true
      }
      Start-Sleep -Seconds 1
    }
    return $false
  } finally {
    $ErrorActionPreference = $oldEap
  }
}

function Wait-Ready([int]$retries) {
  for ($i=1; $i -le $retries; $i++) {
    $code = (& curl.exe -s -o NUL -w "%{http_code}" http://localhost:5122/ready)
    if ($code -eq '200') {
      Write-Host ("/ready => 200 (try {0}/{1})" -f $i,$retries)
      return $true
    }
    Start-Sleep -Seconds 1
  }
  $body = (& curl.exe -s http://localhost:5122/ready)
  Write-Warning "/ready still not 200 after $retries tries"
  Write-Host $body
  return $false
}

# Optional: restore deploy/documents from backup
$deploySrc = Join-Path $inPath 'deploy'
$docsSrc   = Join-Path $inPath 'documents'
if (Test-Path $deploySrc) {
  Write-Host "== Restore deploy config ==" -ForegroundColor Cyan
  $deployDest = Get-DeployDir -InstallRoot $installRoot -Env $env
  if (Test-Path $deployDest) { Remove-Item -Recurse -Force $deployDest }
  Copy-Item -Recurse -Force -Path $deploySrc -Destination $deployDest
}
if (Test-Path $docsSrc) {
  Write-Host "== Restore documents ==" -ForegroundColor Cyan
  $docsDest = Join-Path $installRoot 'documents'
  if (Test-Path $docsDest) { Remove-Item -Recurse -Force $docsDest }
  Copy-Item -Recurse -Force -Path $docsSrc -Destination $docsDest
}

Write-Host "== Stop stack (including volumes) ==" -ForegroundColor Cyan
# IMPORTANT: for a true restore we must wipe volumes, otherwise you get "relation already exists" / duplicate keys.
& docker compose -f $composePath --env-file $envPath down -v | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker compose down -v failed ($LASTEXITCODE)" }

Write-Host "== Start postgres + qdrant ==" -ForegroundColor Cyan
& docker compose -f $composePath --env-file $envPath up -d postgres qdrant | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker compose up postgres qdrant failed ($LASTEXITCODE)" }

if (-not (Wait-Postgres $PostgresRetries)) {
  throw "Postgres did not become ready after $PostgresRetries seconds"
}

# Stop qdrant while restoring its storage (avoids file locks)
& docker compose -f $composePath --env-file $envPath stop qdrant | Out-Null

Write-Host "== Restore qdrant storage ==" -ForegroundColor Cyan
$qid = Get-ComposeContainerId 'qdrant'
$inDock = Convert-ToDockerPath $inPath
$mount = "${inDock}:/backup"
& docker run --rm --volumes-from $qid -v $mount alpine:3.20 sh -lc "rm -rf /qdrant/storage/* && tar -xzf /backup/qdrant_storage.tgz -C /qdrant/storage" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Qdrant restore failed ($LASTEXITCODE)" }

# Start qdrant again
& docker compose -f $composePath --env-file $envPath start qdrant | Out-Null

Write-Host "== Restore postgres ==" -ForegroundColor Cyan
$cid = Get-ComposeContainerId 'postgres'
& docker cp "$dump" "${cid}:/tmp/restore.sql" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker cp restore.sql failed ($LASTEXITCODE)" }

# DB reset is handled by pg_dump --clean --if-exists in backup.

& docker compose -f $composePath --env-file $envPath exec -T -e ("PGPASSWORD=$pwd") postgres psql -v ON_ERROR_STOP=1 -U $usr -d $db -f /tmp/restore.sql
if ($LASTEXITCODE -ne 0) { throw "psql restore failed ($LASTEXITCODE)" }

Write-Host "== Start full stack ==" -ForegroundColor Cyan
& docker compose -f $composePath --env-file $envPath up -d --build | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed ($LASTEXITCODE)" }

# Wait for readiness (TEI can take a few seconds)
$ok = Wait-Ready $ReadyRetries
if (-not $ok) { throw "Restore completed but /ready is not healthy" }

Write-Host "DONE"
