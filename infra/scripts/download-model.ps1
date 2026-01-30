param(
  [string]$ModelUrl = "",
  [string]$ModelFile = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RootDir     = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ModelsDir   = Join-Path $RootDir "models"
$EnvFile     = Join-Path $RootDir "infra\.env"
$EnvExample  = Join-Path $RootDir "infra\.env.example"

New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null

function Get-EnvValueFromFile([string]$Key, [string]$Path) {
  if (-not (Test-Path $Path)) { return $null }
  $line = (Select-String -Path $Path -Pattern "^\s*$([regex]::Escape($Key))\s*=" -SimpleMatch:$false | Select-Object -Last 1)
  if (-not $line) { return $null }
  return ($line.Line -split "=", 2)[1].Trim()
}

# 1) args
$u = $ModelUrl
$f = $ModelFile

# 2) env runtime
if ([string]::IsNullOrWhiteSpace($u)) { $u = $env:LLM_MODEL_URL }
if ([string]::IsNullOrWhiteSpace($f)) { $f = $env:LLM_MODEL_FILE }
if ([string]::IsNullOrWhiteSpace($f)) { $f = $env:LLM_MODEL }

# 3) infra/.env
if ([string]::IsNullOrWhiteSpace($u)) { $u = Get-EnvValueFromFile "LLM_MODEL_URL" $EnvFile }
if ([string]::IsNullOrWhiteSpace($f)) { $f = Get-EnvValueFromFile "LLM_MODEL_FILE" $EnvFile }
if ([string]::IsNullOrWhiteSpace($f)) { $f = Get-EnvValueFromFile "LLM_MODEL" $EnvFile }

# 4) infra/.env.example
if ([string]::IsNullOrWhiteSpace($u)) { $u = Get-EnvValueFromFile "LLM_MODEL_URL" $EnvExample }
if ([string]::IsNullOrWhiteSpace($f)) { $f = Get-EnvValueFromFile "LLM_MODEL_FILE" $EnvExample }
if ([string]::IsNullOrWhiteSpace($f)) { $f = Get-EnvValueFromFile "LLM_MODEL" $EnvExample }

if ([string]::IsNullOrWhiteSpace($u) -or [string]::IsNullOrWhiteSpace($f)) {
  Write-Host "ERROR: Missing MODEL_URL / MODEL_FILE."
  Write-Host "Fix by:"
  Write-Host "  - Copy infra/.env.example -> infra/.env"
  Write-Host "  - Set LLM_MODEL_URL + LLM_MODEL_FILE"
  Write-Host "Or run:"
  Write-Host "  .\infra\scripts\download-model.ps1 <MODEL_URL> <MODEL_FILE>"
  exit 1
}

# Expected checks (optional)
$expectedSize = $env:LLM_MODEL_SIZE_BYTES
$expectedSha  = $env:LLM_MODEL_SHA256
if (-not $expectedSize) { $expectedSize = Get-EnvValueFromFile "LLM_MODEL_SIZE_BYTES" $EnvFile }
if (-not $expectedSha)  { $expectedSha  = Get-EnvValueFromFile "LLM_MODEL_SHA256"     $EnvFile }
if (-not $expectedSize) { $expectedSize = Get-EnvValueFromFile "LLM_MODEL_SIZE_BYTES" $EnvExample }
if (-not $expectedSha)  { $expectedSha  = Get-EnvValueFromFile "LLM_MODEL_SHA256"     $EnvExample }

$outPath  = Join-Path $ModelsDir $f
$partPath = "$outPath.part"

Write-Host "Downloading model..."
Write-Host "  URL : $u"
Write-Host "  OUT : $outPath"

if (Test-Path $outPath) {
  Write-Host "Already exists: $outPath"
  (Get-Item $outPath) | Format-List FullName,Length,LastWriteTime
  exit 0
}

# Download with curl.exe (retries like your manual command)
& curl.exe -L --fail --retry 5 --retry-delay 2 `
  -o $partPath `
  $u

Move-Item -Force $partPath $outPath

# Verify size
if ($expectedSize) {
  $actualSize = (Get-Item $outPath).Length
  if ([int64]$expectedSize -ne [int64]$actualSize) {
    throw "Size mismatch. expected=$expectedSize actual=$actualSize"
  }
  Write-Host "OK size: $actualSize bytes"
}

# Verify sha256
if ($expectedSha) {
  $actualSha = (Get-FileHash $outPath -Algorithm SHA256).Hash.ToUpperInvariant()
  $expectedSha = $expectedSha.Trim().ToUpperInvariant()
  if ($actualSha -ne $expectedSha) {
    throw "SHA256 mismatch.`nexpected=$expectedSha`nactual  =$actualSha"
  }
  Write-Host "OK sha256: $actualSha"
}

Write-Host "Done."
(Get-Item $outPath) | Format-List FullName,Length,LastWriteTime
