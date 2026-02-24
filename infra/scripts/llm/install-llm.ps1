param(
  [string]$InstallRoot = $(if ($env:SAAIA_INSTALL_ROOT) { $env:SAAIA_INSTALL_ROOT } else { 'C:\SAAIA' }),
  [string]$Repo = 'bartowski/Mistral-7B-Instruct-v0.3-GGUF',
  [string]$File = 'Mistral-7B-Instruct-v0.3-IQ3_M.gguf',
  [string]$Sha256 = '4ea14c5a6c787ac2703505f04a4ee746f746d1ace3ffd907af28f6f179e6b224',
  [string]$BindAddr = '127.0.0.1',
  [int]$HostPort = 1234,
  [int]$ContainerPort = 8080,
  [switch]$NoDockerUp,
  [switch]$NoWait,
  [switch]$ForceRedownload
)

$ErrorActionPreference = 'Stop'

function Info($m) { Write-Host "[INFO] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[ OK ] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[WARN] $m" -ForegroundColor Yellow }

$ModelsDir = Join-Path $InstallRoot 'models'
$DeployDir = Join-Path $InstallRoot 'deploy'
$ModelPath = Join-Path $ModelsDir $File

New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null
New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null

$ModelUrl = "https://huggingface.co/$Repo/resolve/main/$File"
Info "InstallRoot: $InstallRoot"
Info "Model:      $Repo/$File"
Info "Url:        $ModelUrl"
Info "Target:     $ModelPath"

if (Test-Path $ModelPath) {
  if ($ForceRedownload) {
    Warn "Existing model will be removed (-ForceRedownload)."
    Remove-Item -Force $ModelPath
  } else {
    Info "Model already present. Verifying hash..."
  }
}

function Get-FileSha256($path) {
  (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
}

if (!(Test-Path $ModelPath)) {
  Info "Downloading model (curl.exe with resume/retry)..."

  $token = $env:HF_TOKEN
  $headers = @()
  if ($token) {
    Info "Using HF_TOKEN from environment."
    $headers += @('-H', "Authorization: Bearer $token")
  } else {
    Warn "HF_TOKEN not set. Public download may still work, but can be rate-limited or blocked for gated models."
  }

  # curl flags:
  # -L follow redirects
  # --fail fail on HTTP errors
  # --retry retry transient errors
  # --continue-at - resume partial download
  $args = @(
    '-L','--fail','--retry','6','--retry-delay','2','--continue-at','-','-o',$ModelPath
  ) + $headers + @($ModelUrl)

  & curl.exe @args

  Ok "Download completed."
}

Info "Computing SHA256..."
$actual = Get-FileSha256 $ModelPath
Info "SHA256: $actual"
if ($Sha256 -and ($actual -ne $Sha256.ToLowerInvariant())) {
  throw "SHA256 mismatch. Expected: $Sha256 ; Actual: $actual"
}
Ok "Model hash verified."

# Generate a docker-compose.llm.yml pinned to InstallRoot so it works regardless of current directory.
$composePath = Join-Path $DeployDir 'docker-compose.llm.yml'
$winModels = ($ModelsDir -replace '\\','/')
$compose = @"
name: saaia-llm

services:
  llama:
    image: ghcr.io/ggml-org/llama.cpp:server-cuda
    container_name: saaia-llama
    gpus: all
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]

    volumes:
      - ${winModels}:/models:ro

    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility

      LLAMA_ARG_MODEL: /models/$File
      LLAMA_ARG_HOST: 0.0.0.0
      LLAMA_ARG_PORT: $ContainerPort

      # Safe defaults (adjust later if needed)
      LLAMA_ARG_CTX_SIZE: 3072
      LLAMA_ARG_N_PARALLEL: 1
      LLAMA_ARG_THREADS: 6
      LLAMA_ARG_THREADS_BATCH: 6
      LLAMA_ARG_BATCH: 256
      LLAMA_ARG_N_GPU_LAYERS: all

    ports:
      - "${BindAddr}:${HostPort}:${ContainerPort}"

    restart: unless-stopped
"@

Set-Content -Encoding UTF8 -Path $composePath -Value $compose
Ok "Generated: $composePath"

# Write install manifest for support
$manifest = [ordered]@{
  installedAt = (Get-Date).ToString('o')
  installRoot = $InstallRoot
  repo = $Repo
  file = $File
  url = $ModelUrl
  sha256 = $actual
  host = $BindAddr
  port = $HostPort
  containerPort = $ContainerPort
}
$manifestPath = Join-Path $DeployDir 'llm.install.json'
($manifest | ConvertTo-Json -Depth 5) | Set-Content -Encoding UTF8 $manifestPath
Ok "Wrote: $manifestPath"

if ($NoDockerUp) {
  Warn "Skipping docker compose up (-NoDockerUp)."
  exit 0
}

Info "Starting LLM stack (docker compose up -d)..."
& docker compose -f $composePath up -d
Ok "Docker compose up done."

if ($NoWait) {
  Warn "Skipping /v1/models readiness wait (-NoWait)."
  exit 0
}

# Wait until /v1/models is ready (not 503 Loading model)
$modelsUrl = "http://${BindAddr}:$HostPort/v1/models"
Info "Waiting for LLM readiness: $modelsUrl"
for ($i=1; $i -le 60; $i++) {
  try {
    $resp = & curl.exe -s $modelsUrl
    if ($resp -and ($resp -notmatch 'Loading model')) {
      Ok "LLM is ready."
      exit 0
    }
  } catch { }
  Start-Sleep -Seconds 2
}
Warn "LLM did not become ready within timeout. Check container logs: docker logs saaia-llama"
