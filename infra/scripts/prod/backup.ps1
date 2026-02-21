param(
  [string]$ComposeFile = 'infra/docker-compose.prod.yml',
  [string]$EnvFile = 'infra/.env',
  [string]$OutDir = 'backups',
  [switch]$IncludeDocuments
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

# Output folder base
$outBase = $OutDir
if (-not [System.IO.Path]::IsPathRooted($outBase)) {
  $outBase = Join-Path $installRoot $outBase
}
$ts = Get-Date -Format "yyyyMMdd_HHmmss"
$out = (New-Item -ItemType Directory -Force -Path (Join-Path $outBase $ts)).FullName

# Resolve compose paths
$composePath = Resolve-PathFromRepo $repo $ComposeFile

Write-Host "== Backup ==" -ForegroundColor Cyan
Write-Host "InstallRoot:   $installRoot"
Write-Host "Backup folder: $out"

function Get-ComposeContainerId([string]$service) {
  $args = @('compose','-f',$composePath,'--env-file',$envPath,'ps','-q',$service)
  $id = (& docker @args).Trim()
  if (-not $id) { throw "Cannot find container id for service '$service'" }
  return $id
}

# 1) Postgres dump
$db = 'saaia'; if ($env.ContainsKey('POSTGRES_DB') -and -not [string]::IsNullOrWhiteSpace($env['POSTGRES_DB'])) { $db = [string]$env['POSTGRES_DB'] }
$usr = 'saaia'; if ($env.ContainsKey('POSTGRES_USER') -and -not [string]::IsNullOrWhiteSpace($env['POSTGRES_USER'])) { $usr = [string]$env['POSTGRES_USER'] }
$pwd = '';    if ($env.ContainsKey('POSTGRES_PASSWORD')) { $pwd = [string]$env['POSTGRES_PASSWORD'] }

Write-Host "== Postgres dump ==" -ForegroundColor Cyan
$cid = Get-ComposeContainerId 'postgres'
$dumpFile = Join-Path $out 'postgres_dump.sql'

# Use pg_dump flags to make restore idempotent if volumes aren't wiped.
# IMPORTANT: do NOT pass the password on the docker command line (would leak if echoed).
# We rely on POSTGRES_PASSWORD already present inside the container.
$cmd = 'export PGPASSWORD="$POSTGRES_PASSWORD"; pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner --no-privileges -f /tmp/postgres_dump.sql'

& docker compose -f $composePath --env-file $envPath exec -T postgres sh -lc $cmd | Out-Null
if ($LASTEXITCODE -ne 0) { throw "pg_dump failed ($LASTEXITCODE)" }

& docker cp "${cid}:/tmp/postgres_dump.sql" "$dumpFile" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker cp postgres_dump.sql failed ($LASTEXITCODE)" }
Write-Host "Wrote $dumpFile"

# 2) Qdrant volume archive
Write-Host "== Qdrant archive ==" -ForegroundColor Cyan
$qid = Get-ComposeContainerId 'qdrant'
$outDock = Convert-ToDockerPath $out
$mount = "${outDock}:/backup"
& docker run --rm --volumes-from $qid -v $mount alpine:3.20 sh -lc "tar -czf /backup/qdrant_storage.tgz -C /qdrant/storage ." | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Qdrant archive failed ($LASTEXITCODE)" }
Write-Host "Wrote $(Join-Path $out 'qdrant_storage.tgz')"

# 3) deploy/ config copy (from install root)
Write-Host "== Deploy config copy ==" -ForegroundColor Cyan
$deployDir = Get-DeployDir -InstallRoot $installRoot -Env $env
if (Test-Path $deployDir) {
  Copy-Item -Recurse -Force -Path $deployDir -Destination (Join-Path $out 'deploy')
}

# 4) documents (optional)
if ($IncludeDocuments) {
  Write-Host "== Documents copy ==" -ForegroundColor Cyan
  $docs = Join-Path $installRoot 'documents'
  if (Test-Path $docs) {
    Copy-Item -Recurse -Force -Path $docs -Destination (Join-Path $out 'documents')
  }
}

Write-Host "DONE => $out"
Write-Host "Restore with: powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\restore.ps1 -InDir `"$out`""
