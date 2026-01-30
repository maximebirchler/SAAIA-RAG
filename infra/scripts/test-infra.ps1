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
$llmPort    = $env:LLM_HOST_PORT;    if ([string]::IsNullOrWhiteSpace($llmPort))    { $llmPort    = "1234" }

$modelFile = $env:LLM_MODEL_FILE
if ([string]::IsNullOrWhiteSpace($modelFile)) { $modelFile = $env:LLM_MODEL }
if ([string]::IsNullOrWhiteSpace($modelFile)) { throw "LLM_MODEL_FILE (or LLM_MODEL) not set in infra\.env" }

$modelPath = Join-Path (Join-Path $RootDir "models") $modelFile
if (!(Test-Path $modelPath)) {
  throw "Model file missing: $modelPath`nRun: .\infra\scripts\download-model.ps1"
}

Write-Host "== Checking Qdrant =="
Invoke-WebRequest -Uri "http://127.0.0.1:$qdrantPort/readyz" -UseBasicParsing | Out-Null
Write-Host "OK: Qdrant /readyz"

Write-Host "== Checking TEI =="
Invoke-WebRequest -Uri "http://127.0.0.1:$teiPort/health" -UseBasicParsing | Out-Null
Write-Host "OK: TEI /health"

Write-Host "== Checking llama.cpp server (OpenAI chat) =="
$body = @{
  model = "local"
  messages = @(@{ role="user"; content="Réponds uniquement: OK" })
  temperature = 0
  stream = $false
  max_tokens = 5
} | ConvertTo-Json -Depth 5

$maxTries = 90      # ~90 * 2s = 180s
$delaySec = 2
$ok = $false

for ($i = 1; $i -le $maxTries; $i++) {
  try {
    $r = Invoke-WebRequest -Uri "http://127.0.0.1:$llmPort/v1/chat/completions" `
      -Method Post -ContentType "application/json; charset=utf-8" -Body $body -UseBasicParsing

    if ($r.Content -match '"choices"') {
      $ok = $true
      break
    }
  } catch {
    # llama renvoie 503 "Loading model" pendant le warmup → on retry
    Start-Sleep -Seconds $delaySec
  }
}

if (-not $ok) { throw "llama.cpp server not ready after $($maxTries*$delaySec)s" }
Write-Host "OK: llama /v1/chat/completions"

Write-Host "== Checking Postgres (pg_isready via docker compose) =="
docker compose -f "$RootDir/infra/docker-compose.yml" --env-file "$EnvPath" exec -T postgres `
  pg_isready -U $env:POSTGRES_USER -d $env:POSTGRES_DB | Out-Null
Write-Host "OK: Postgres pg_isready"

Write-Host ""
Write-Host "All infra checks passed."
