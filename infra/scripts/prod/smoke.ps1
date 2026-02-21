param(
  [string]$BaseUrl = "http://localhost:5122",
  [string]$ApiKey = "",
  [string]$AdminApiKey = "",
  [int]$RateLimitAttempts = 600,
  [switch]$SkipRateLimit,
  [switch]$SkipAudit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Info([string]$msg) { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "[ OK ] $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Fail([string]$msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red }

function Get-DotEnvValue([string]$EnvPath, [string]$Key) {
  if (!(Test-Path $EnvPath)) { return $null }
  foreach ($line in Get-Content $EnvPath) {
    $l = $line.Trim()
    if ($l.Length -eq 0 -or $l.StartsWith("#")) { continue }
    $idx = $l.IndexOf("=")
    if ($idx -lt 1) { continue }
    $k = $l.Substring(0, $idx).Trim()
    if ($k -ne $Key) { continue }
    return $l.Substring($idx+1).Trim()
  }
  return $null
}

function Try-GetJson([string]$url, [hashtable]$headers) {
  try {
    return Invoke-RestMethod -Method Get -Uri $url -Headers $headers
  } catch {
    return $null
  }
}

# Resolve ApiKey
if ([string]::IsNullOrWhiteSpace($ApiKey)) { $ApiKey = $env:SAAIA_SMOKE_API_KEY }
if ([string]::IsNullOrWhiteSpace($ApiKey)) { $ApiKey = Get-DotEnvValue (Join-Path $PSScriptRoot "..\..\.env") "SAAIA_BOOTSTRAP_API_KEY" }
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
  throw "Missing ApiKey. Provide -ApiKey or set SAAIA_SMOKE_API_KEY or infra/.env:SAAIA_BOOTSTRAP_API_KEY."
}

# Resolve AdminApiKey (optional)
if ([string]::IsNullOrWhiteSpace($AdminApiKey)) { $AdminApiKey = $env:SAAIA_SMOKE_ADMIN_API_KEY }

Write-Info "BaseUrl: $BaseUrl"
Write-Info ("Using ApiKey: (len={0})" -f $ApiKey.Length)

# 1) /ready
$rdy = Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl/ready" -TimeoutSec 10
if ($rdy.StatusCode -ne 200) { throw "/ready expected 200, got $($rdy.StatusCode)" }
Write-Ok "/ready => 200"

# 2) Chat-store (M1.3)
$userId = [guid]::NewGuid().ToString()
Write-Info "Chat-store test with userId=$userId"

$headers = @{ "X-Api-Key" = $ApiKey; "Content-Type" = "application/json" }

$session = Invoke-RestMethod -Method Post -Uri "$BaseUrl/chat/sessions" -Headers $headers -Body (@{
  userId = $userId
  title = "Smoke $(Get-Date -Format s)"
  clientUser = "smoke.ps1"
} | ConvertTo-Json)

$sessionId = $session.sessionId
if ([string]::IsNullOrWhiteSpace($sessionId)) { throw "Create session did not return sessionId." }
Write-Ok "Create session => $sessionId"

$body1 = (@{ userId=$userId; role="user"; content="Salut"; sourcesJson=$null } | ConvertTo-Json)
$body2 = (@{ userId=$userId; role="assistant"; content="Bonjour"; sourcesJson=$null } | ConvertTo-Json)

Invoke-RestMethod -Method Post -Uri "$BaseUrl/chat/sessions/$sessionId/messages" -Headers $headers -Body $body1 | Out-Null
Invoke-RestMethod -Method Post -Uri "$BaseUrl/chat/sessions/$sessionId/messages" -Headers $headers -Body $body2 | Out-Null
Write-Ok "Add 2 messages => OK"

$msgs = Invoke-RestMethod -Method Get -Uri "$BaseUrl/chat/sessions/$sessionId/messages?userId=$userId&limit=50" -Headers @{ "X-Api-Key" = $ApiKey }
if ($null -eq $msgs) { throw "List messages returned null." }
$cnt = @($msgs).Count
if ($cnt -lt 2) { throw "Expected >=2 messages, got $cnt." }
Write-Ok "List messages => $cnt"

$sessions = Invoke-RestMethod -Method Get -Uri "$BaseUrl/chat/sessions?userId=$userId&limit=10" -Headers @{ "X-Api-Key" = $ApiKey }
if ($null -eq $sessions) { throw "List sessions returned null." }
Write-Ok "List sessions => $(@($sessions).Count)"

Invoke-WebRequest -UseBasicParsing -Method Delete -Uri "$BaseUrl/chat/sessions/$($sessionId)?userId=$userId" -Headers @{ "X-Api-Key" = $ApiKey } | Out-Null
Write-Ok "Delete session => OK"

# 3) Audit (M3.3) (optional)
if (-not $SkipAudit) {
  # If no explicit AdminApiKey was provided, we try with the main ApiKey.
  # Note: the bootstrap key created by the installer is ADMIN by default.
  $auditKey = $AdminApiKey
  $auditKeySource = "AdminApiKey"
  if ([string]::IsNullOrWhiteSpace($auditKey)) {
    $auditKey = $ApiKey
    $auditKeySource = "ApiKey"
  }

  Write-Info "Audit test via /admin/audit (key source=$auditKeySource)."
  try {
    $aud = Invoke-RestMethod -Method Get -Uri "$BaseUrl/admin/audit?limit=20" -Headers @{ "X-Api-Key" = $auditKey }
    $n = 0
    if ($null -ne $aud -and $null -ne $aud.items) { $n = @($aud.items).Count }
    if ($n -le 0) {
      Write-Warn "Audit reachable but returned 0 items. (If this is a fresh DB, run a few chat/rag actions then retry.)"
    } else {
      Write-Ok "Audit list => $n items"
    }
  } catch {
    # Usually 403 if key is not admin
    Write-Warn ("Audit test failed (likely non-admin key). Provide -AdminApiKey or set SAAIA_SMOKE_ADMIN_API_KEY. Error: {0}" -f $_.Exception.Message)
  }
} else {
  Write-Info "Audit test skipped (-SkipAudit)."
}

# 4) Rate limiting (M3.2)
if (-not $SkipRateLimit) {
  Write-Info "Rate limit test on /rag/search (attempts=$RateLimitAttempts). Expect at least one 429."
  Write-Info "Note: this may temporarily throttle your API key for ~60s (Retry-After). Use -SkipRateLimit if you plan manual API calls right after."
  $hit = $false
  $body = '{"query":"ping","topK":1}'
  for ($i=1; $i -le $RateLimitAttempts; $i++) {
    $code = & curl.exe -s -o NUL -w "%{http_code}" -X POST "$BaseUrl/rag/search" -H "Content-Type: application/json" -H "X-Api-Key: $ApiKey" --data-binary $body
    if ($code -eq "429") { $hit = $true; break }
  }

  if ($hit) {
    Write-Ok "Rate limit => 429 observed"
    $raw = & curl.exe -s -i -X POST "$BaseUrl/rag/search" -H "Content-Type: application/json" -H "X-Api-Key: $ApiKey" --data-binary $body
    $retry = ($raw -split "`r?`n" | Where-Object { $_ -match "^Retry-After:" } | Select-Object -First 1)
    if ($retry) { Write-Ok $retry.Trim() } else { Write-Warn "Retry-After header not found (check server config)." }
  } else {
    Write-Warn "No 429 observed. This can happen if PermitLimit is high or the loop is too slow. Try increasing -RateLimitAttempts or reducing RateLimiting:PermitLimit in signed config for validation."
  }
} else {
  Write-Info "Rate limit test skipped (-SkipRateLimit)."
}

Write-Ok "SMOKE OK"
