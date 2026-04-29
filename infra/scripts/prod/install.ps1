param(
  [switch]$NoBuild,
  [switch]$WithOtel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_common.ps1')

$repo = Get-RepoRoot

# ---------------------------
# Ensure infra/.env exists
# ---------------------------
$envFileRel = 'infra/.env'
$envExampleRel = 'infra/.env.example'
$envPath = Resolve-PathFromRepo $repo $envFileRel
$envExamplePath = Resolve-PathFromRepo $repo $envExampleRel

if (!(Test-Path $envPath)) {
  if (Test-Path $envExamplePath) {
    Copy-Item -Force $envExamplePath $envPath
    Write-Warning "Created $envFileRel from $envExampleRel. Please edit it then re-run."
    exit 1
  }
  throw "Missing $envFileRel and no $envExampleRel found."
}

$envMap = Read-DotEnv $envPath
# ---------------------------
# Optional: install with local OTel collector
# - Use -WithOtel, or set SAAIA_INSTALL_WITH_OTEL=true in infra/.env
# ---------------------------
function Parse-Bool([string]$raw, [bool]$default) {
  if ([string]::IsNullOrWhiteSpace($raw)) { return $default }
  $x = $raw.Trim().ToLowerInvariant()
  if ($x -in @('1','true','yes','y','on')) { return $true }
  if ($x -in @('0','false','no','n','off')) { return $false }
  return $default
}

$installWithOtel = $WithOtel.IsPresent
if (-not $installWithOtel -and $envMap.ContainsKey('SAAIA_INSTALL_WITH_OTEL')) {
  $installWithOtel = Parse-Bool ([string]$envMap['SAAIA_INSTALL_WITH_OTEL']) $false
}


# ---------------------------
# Required keys for signed config
# ---------------------------
Require $envMap @('POSTGRES_PASSWORD','SAAIA_AUTH_PEPPER','SAAIA_CONFIG_PRIVATE_KEY_PATH')

# Bootstrap key only required if bootstrap is enabled (default true).
$bootEnabled = $true
$bootRaw = ''
if ($envMap.ContainsKey('SAAIA_BOOTSTRAP_ENABLED') -and $null -ne $envMap['SAAIA_BOOTSTRAP_ENABLED']) {
  $bootRaw = [string]$envMap['SAAIA_BOOTSTRAP_ENABLED']
}
$bootRaw = $bootRaw.Trim().ToLowerInvariant()
if ($bootRaw -in @('0','false','no','n','off')) { $bootEnabled = $false }

if ($bootEnabled) {
  Require $envMap @('SAAIA_BOOTSTRAP_API_KEY')
}

# Optional strictness: if the operator wants Qdrant auth enforced, require the key.
$requireQdrantAuth = $false
$qraw = ''
if ($envMap.ContainsKey('REQUIRE_QDRANT_AUTH_IN_PROD') -and $null -ne $envMap['REQUIRE_QDRANT_AUTH_IN_PROD']) {
  $qraw = [string]$envMap['REQUIRE_QDRANT_AUTH_IN_PROD']
}
$qraw = $qraw.Trim().ToLowerInvariant()
if ($qraw -in @('1','true','yes','y','on')) { $requireQdrantAuth = $true }

if ($requireQdrantAuth -and (-not $envMap.ContainsKey('QDRANT_API_KEY') -or [string]::IsNullOrWhiteSpace($envMap['QDRANT_API_KEY']))) {
  throw "REQUIRE_QDRANT_AUTH_IN_PROD=true but QDRANT_API_KEY is empty. Set QDRANT_API_KEY in infra/.env."
}

# ---------------------------
# Resolve InstallRoot + DeployDir
# - InstallRoot controls where docker bind-mounts volumes
# - DeployDir is where deployment.config.* is written
# ---------------------------
$installRoot = Get-InstallRoot -RepoRoot $repo -Env $envMap
$deployDir   = Get-DeployDir  -InstallRoot $installRoot -Env $envMap

# Keep a note if user provided an absolute SAAIA_DEPLOY_DIR that is NOT the same deployDir we will mount from.
# (Compose always mounts <InstallRoot>/deploy by default.)
$deployDirExpected = [System.IO.Path]::GetFullPath((Join-Path $installRoot 'deploy'))
$deployDirIsExpected = ($deployDirExpected.TrimEnd('\','/') -ieq $deployDir.TrimEnd('\','/'))

Write-Host "== Install paths ==" -ForegroundColor Cyan
Write-Host "RepoRoot:      $repo"
Write-Host "InstallRoot:   $installRoot"
Write-Host "DeployDir:     $deployDir"
Write-Host "ExpectedDeploy:$deployDirExpected"
if (-not $deployDirIsExpected) {
  Write-Warning "Your SAAIA_DEPLOY_DIR resolves to '$deployDir'. Docker compose will mount config from '$deployDirExpected'. The installer will write config to BOTH locations to avoid mismatch."
}

# ---------------------------
# Ensure install folders exist
# ---------------------------
$pathsToEnsure = @(
  $deployDirExpected,
  (Join-Path $installRoot 'documents'),
  (Join-Path $installRoot 'data'),
  (Join-Path $installRoot 'data/backend'),
  (Join-Path $installRoot 'data/postgres'),
  (Join-Path $installRoot 'data/qdrant'),
  (Join-Path $installRoot 'cache'),
  (Join-Path $installRoot 'cache/tei')
)

foreach ($p in $pathsToEnsure) {
  if (-not (Test-Path $p)) { New-Item -ItemType Directory -Force -Path $p | Out-Null }
}

# ---------------------------
# Deploy optional LLM installer script (client repair)
# Copy infra/scripts/llm/*.ps1 into <InstallRoot>\deploy
# so the WinUI client can run it (UAC) to download the model + start docker LLM.
# ---------------------------
$llmScriptRoot = Resolve-PathFromRepo $repo "infra/scripts/llm"
if (Test-Path $llmScriptRoot) {
  foreach ($llmScriptSrc in Get-ChildItem -Path $llmScriptRoot -Filter '*.ps1' -File) {
    Copy-Item -Force $llmScriptSrc.FullName (Join-Path $deployDirExpected $llmScriptSrc.Name)
    if (-not $deployDirIsExpected) {
      Copy-Item -Force $llmScriptSrc.FullName (Join-Path $deployDir $llmScriptSrc.Name)
    }
  }
}

# ---------------------------
# Generate + sign deployment config
# Always generate into <InstallRoot>\deploy (what compose mounts),
# and optionally duplicate into SAAIA_DEPLOY_DIR if different.
# ---------------------------
Write-Host "== Generate + sign deployment config ==" -ForegroundColor Cyan

$cfg = New-SignedConfig `
  -RepoRoot $repo `
  -env $envMap `
  -TemplateRel 'infra/config/deployment.config.prod.template.json' `
  -DeployDirRel $deployDirExpected

if (-not $deployDirIsExpected) {
  if (-not (Test-Path $deployDir)) { New-Item -ItemType Directory -Force -Path $deployDir | Out-Null }
  Copy-Item -Force $cfg.ConfigPath (Join-Path $deployDir 'deployment.config.json')
  Copy-Item -Force $cfg.SigPath    (Join-Path $deployDir 'deployment.config.sig')
}

# ---------------------------
# docker compose up (prod)
# Export SAAIA_INSTALL_ROOT for docker bind-mounts
# ---------------------------
$env:SAAIA_INSTALL_ROOT = Convert-ToDockerPath $installRoot

$args = @('up','-d')
if (-not $NoBuild) { $args += @('--build') }

Write-Host "== docker compose up (prod) ==" -ForegroundColor Cyan

$composeProd = Resolve-PathFromRepo $repo 'infra/docker-compose.prod.yml'
$envPathAbs  = Resolve-PathFromRepo $repo $envFileRel

$full = @('compose','-f', $composeProd)
if ($installWithOtel) {
  $composeOtelRel = 'infra/docker-compose.otel.yml'
  $composeOtel = Resolve-PathFromRepo $repo $composeOtelRel
  if (!(Test-Path $composeOtel)) {
    throw "InstallWithOtel requested but missing $composeOtelRel. Add it (OTel patch) or run install.ps1 without -WithOtel."
  }
  $full += @('-f', $composeOtel)
  Write-Host "(with OTel collector)" -ForegroundColor DarkCyan
}

$full += @('--env-file', $envPathAbs) + $args
Write-Host ('> docker ' + ($full -join ' '))
& docker @full
if ($LASTEXITCODE -ne 0) { throw "docker compose failed ($LASTEXITCODE)" }

# ---------------------------
# Verify /ready (retry)
# ---------------------------
$port = '5122'
if ($envMap.ContainsKey('BACKEND_HOST_PORT') -and -not [string]::IsNullOrWhiteSpace($envMap['BACKEND_HOST_PORT'])) { $port = $envMap['BACKEND_HOST_PORT'] }

Write-Host "== Verify /ready ==" -ForegroundColor Cyan
$readyUrl = "http://localhost:$port/ready"
$maxTries = 40
$delayMs  = 1500

$ok = $false
for ($i = 1; $i -le $maxTries; $i++) {
  try {
    $r = Invoke-WebRequest -UseBasicParsing -Uri $readyUrl -TimeoutSec 5
    if ($r.StatusCode -eq 200) {
      Write-Host "/ready => 200 (try $i/$maxTries)" -ForegroundColor Green
      $ok = $true
      break
    }
  } catch {
    # ignore; retry
  }
  Start-Sleep -Milliseconds $delayMs
}

if (-not $ok) {
  $logCmd = "docker compose -f .\infra\docker-compose.prod.yml"
  if ($installWithOtel) { $logCmd += " -f .\infra\docker-compose.otel.yml" }
  $logCmd += " --env-file .\infra\.env logs -f backend"
  Write-Warning "Could not reach backend /ready after $maxTries tries. Check logs: $logCmd"
}

if ($ok -and $installWithOtel) {
  Write-Host ""
  Write-Host "OTel collector logs:" -ForegroundColor Cyan
  Write-Host "docker compose -f .\infra\docker-compose.prod.yml -f .\infra\docker-compose.otel.yml --env-file .\infra\.env logs -f otel-collector"
}
