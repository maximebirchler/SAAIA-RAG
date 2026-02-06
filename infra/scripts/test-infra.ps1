$ErrorActionPreference = "Stop"

$RootDir = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$EnvPath = Join-Path $RootDir "infra\.env"
if (!(Test-Path $EnvPath)) {
  throw "Missing infra\.env (copy infra\.env.example -> infra\.env)"
}

# Simple .env loader
Get-Content $EnvPath | ForEach-Object {
  if ($_ -match '^\s*$' -or $_ -match '^\s*#') { return }
  $kv = $_.Split("=",2)
  if ($kv.Count -eq 2) {
    [Environment]::SetEnvironmentVariable($kv[0].Trim(), $kv[1].Trim())
  }
}

$qdrantPort = $env:QDRANT_HOST_PORT; if ([string]::IsNullOrWhiteSpace($qdrantPort)) { $qdrantPort = "6333" }
$teiPort    = $env:TEI_HOST_PORT;    if ([string]::IsNullOrWhiteSpace($teiPort))    { $teiPort    = "8081" }

Write-Host "== Checking Qdrant =="
Invoke-WebRequest -Uri "http://127.0.0.1:$qdrantPort/readyz" -UseBasicParsing | Out-Null
Write-Host "OK: Qdrant /readyz"

Write-Host "== Checking TEI =="
Invoke-WebRequest -Uri "http://127.0.0.1:$teiPort/health" -UseBasicParsing | Out-Null
Write-Host "OK: TEI /health"

Write-Host "== Checking Postgres (pg_isready via docker compose) =="
docker compose -f "$RootDir/infra/docker-compose.yml" --env-file "$EnvPath" exec -T postgres `
  pg_isready -U $env:POSTGRES_USER -d $env:POSTGRES_DB | Out-Null
Write-Host "OK: Postgres pg_isready"

Write-Host ""
Write-Host "All infra checks passed."
