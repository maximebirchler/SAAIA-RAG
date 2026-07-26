param(
  [string]$InstallRoot = $(if ($env:SAAIA_INSTALL_ROOT) { $env:SAAIA_INSTALL_ROOT } else { 'C:\SAAIA' }),
  [string]$Repo = 'bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF',
  [string]$Revision = 'ae44f08e1392f39c0e474af10c3ff8355c8b6688',
  [string]$File = 'Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf',
  [string]$Sha256 = '66713ce35a58a82fe87642d4ec13425bf9b9a46800fff5c49a665ef5701439dc',
  [string]$LlamaImage = 'saaia/llama.cpp:server-cuda-b10098',
  [string]$BindAddr = '127.0.0.1',
  [int]$HostPort = 1234,
  [int]$ContainerPort = 8080,
  [int]$LicenseSeats = $(if ($env:SAAIA_LICENSE_SEATS -as [int]) { [int]$env:SAAIA_LICENSE_SEATS } else { 1 }),
  [string]$DockerNetwork = $(if ($env:SAAIA_LLM_DOCKER_NETWORK) { $env:SAAIA_LLM_DOCKER_NETWORK } else { '' }),
  [switch]$AutoPlan,
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

function Get-ServerHardwareSnapshot {
  $cpu = [Environment]::ProcessorCount
  $totalRamMiB = 0
  try {
    $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $totalRamMiB = [int][Math]::Floor([double]$cs.TotalPhysicalMemory / 1MB)
  } catch {
    try {
      $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
      $totalRamMiB = [int][Math]::Floor([double]$os.TotalVisibleMemorySize / 1024)
    } catch { }
  }

  $gpuName = $null
  $vramMiB = 0
  $nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
  if ($nvidiaSmi) {
    try {
      $line = & nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits 2>$null | Select-Object -First 1
      if ($line) {
        $parts = $line -split ','
        $gpuName = $parts[0].Trim()
        if ($parts.Count -ge 2) { $vramMiB = [int]($parts[1].Trim()) }
      }
    } catch { }
  }

  if ($vramMiB -le 0) {
    try {
      $gpu = Get-CimInstance Win32_VideoController -ErrorAction Stop |
        Where-Object { $_.AdapterRAM -gt 0 } |
        Sort-Object AdapterRAM -Descending |
        Select-Object -First 1
      if ($gpu) {
        $gpuName = [string]$gpu.Name
        $vramMiB = [int][Math]::Floor([double]$gpu.AdapterRAM / 1MB)
      }
    } catch { }
  }

  [pscustomobject]@{
    CpuCount = [int]$cpu
    TotalRamMiB = [int]$totalRamMiB
    GpuName = $gpuName
    GpuVramMiB = [int]$vramMiB
  }
}

function New-ServerLlmCapacityPlan {
  param(
    [Parameter(Mandatory=$true)]$Hardware,
    [Parameter(Mandatory=$true)][int]$Seats,
    [Parameter(Mandatory=$true)][int]$BaseHostPort,
    [Parameter(Mandatory=$true)][int]$ContainerPort
  )

  $seatsSafe = [Math]::Max(1, $Seats)
  $vram = [Math]::Max(0, [int]$Hardware.GpuVramMiB)
  $ram = [Math]::Max(0, [int]$Hardware.TotalRamMiB)
  $cpu = [Math]::Max(1, [int]$Hardware.CpuCount)

  $model = [ordered]@{
    repo = 'bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF'
    revision = 'ae44f08e1392f39c0e474af10c3ff8355c8b6688'
    file = 'Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf'
    sha256 = '66713ce35a58a82fe87642d4ec13425bf9b9a46800fff5c49a665ef5701439dc'
    modelId = 'qwen3-4b-instruct-2507-q5-k-m'
    profile = 'server-qwen3-quality'
    estimatedModelMiB = 2900
    ctxSize = 4096
    batch = 512
    ubatch = 128
    reason = 'measured RAG quality winner; concurrency is adapted to available hardware'
  }

  $desiredConcurrent = [int][Math]::Ceiling($seatsSafe * 0.12)
  if ($seatsSafe -le 3) { $desiredConcurrent = 1 }
  if ($seatsSafe -gt 3 -and $desiredConcurrent -lt 2) { $desiredConcurrent = 2 }
  $desiredConcurrent = [Math]::Min(12, [Math]::Max(1, $desiredConcurrent))

  $slotMemoryMiB = if ($model.modelId -like '*27b*') { 650 } elseif ($model.modelId -like '*7b*') { 420 } else { 180 }
  $overheadMiB = 900
  $effectiveVram = if ($vram -gt 0) { [int]($vram * 0.82) } else { 0 }
  $maxSlotsByVram = if ($effectiveVram -gt 0) {
    [int][Math]::Floor(($effectiveVram - $model.estimatedModelMiB - $overheadMiB) / $slotMemoryMiB)
  } else { 1 }
  $maxSlotsByVram = [Math]::Max(1, $maxSlotsByVram)
  $maxSlotsByCpu = [Math]::Max(1, [int][Math]::Floor($cpu / 2))
  $totalSlots = [Math]::Min($desiredConcurrent, [Math]::Min($maxSlotsByVram, $maxSlotsByCpu))
  $totalSlots = [Math]::Max(1, $totalSlots)

  $slotsPerInstance = [Math]::Min(4, $totalSlots)
  $instances = [int][Math]::Ceiling($totalSlots / [double]$slotsPerInstance)

  $perInstanceMiB = $model.estimatedModelMiB + ($slotsPerInstance * $slotMemoryMiB) + $overheadMiB
  if ($effectiveVram -gt 0) {
    $maxInstancesByVram = [Math]::Max(1, [int][Math]::Floor($effectiveVram / $perInstanceMiB))
    $instances = [Math]::Min($instances, $maxInstancesByVram)
  }
  $instances = [Math]::Max(1, $instances)
  $slotsPerInstance = [Math]::Max(1, [int][Math]::Ceiling($totalSlots / [double]$instances))

  $queueLimit = [Math]::Min(100, [Math]::Max(10, $seatsSafe * 2))
  $perUserActive = 1
  $perUserQueued = if ($seatsSafe -le 5) { 2 } else { 1 }

  [pscustomobject]@{
    version = 'v3.1-server-capacity'
    plannedAt = (Get-Date).ToString('o')
    licenseSeats = $seatsSafe
    hardware = $Hardware
    modelId = $model.modelId
    repo = $model.repo
    revision = $model.revision
    file = $model.file
    sha256 = $model.sha256
    profile = $model.profile
    reason = $model.reason
    instances = [int]$instances
    slotsPerInstance = [int]$slotsPerInstance
    totalSlots = [int]($instances * $slotsPerInstance)
    queueLimit = [int]$queueLimit
    perUserActiveLimit = [int]$perUserActive
    perUserQueuedLimit = [int]$perUserQueued
    hostPorts = @(0..($instances - 1) | ForEach-Object { $BaseHostPort + $_ })
    containerPort = $ContainerPort
    llamaArgs = [ordered]@{
      ctxSize = [int]$model.ctxSize
      nParallel = [int]$slotsPerInstance
      threads = [Math]::Max(2, [Math]::Min(8, $cpu - 1))
      threadsBatch = [Math]::Max(2, [Math]::Min(8, $cpu - 1))
      batch = [int]$model.batch
      ubatch = [int]$model.ubatch
      nGpuLayers = 'all'
    }
  }
}

if ($AutoPlan) {
  $hardware = Get-ServerHardwareSnapshot
  $capacityPlan = New-ServerLlmCapacityPlan -Hardware $hardware -Seats $LicenseSeats -BaseHostPort $HostPort -ContainerPort $ContainerPort
  $Repo = $capacityPlan.repo
  $Revision = $capacityPlan.revision
  $File = $capacityPlan.file
  $Sha256 = $capacityPlan.sha256
  $ModelPath = Join-Path $ModelsDir $File
  Info "Auto capacity plan:"
  Info "  seats=$($capacityPlan.licenseSeats) cpu=$($hardware.CpuCount) ramMiB=$($hardware.TotalRamMiB) gpu='$($hardware.GpuName)' vramMiB=$($hardware.GpuVramMiB)"
  Info "  model=$($capacityPlan.modelId) profile=$($capacityPlan.profile)"
  Info "  instances=$($capacityPlan.instances) slots/instance=$($capacityPlan.slotsPerInstance) queue=$($capacityPlan.queueLimit)"
} else {
  $capacityPlan = [pscustomobject]@{
    version = 'manual'
    plannedAt = (Get-Date).ToString('o')
    licenseSeats = [Math]::Max(1, $LicenseSeats)
    modelId = $File
    repo = $Repo
    revision = $Revision
    file = $File
    sha256 = $Sha256
    profile = 'manual'
    reason = 'manual parameters'
    instances = 1
    slotsPerInstance = 1
    totalSlots = 1
    queueLimit = [Math]::Min(100, [Math]::Max(10, [Math]::Max(1, $LicenseSeats) * 2))
    perUserActiveLimit = 1
    perUserQueuedLimit = 1
    hostPorts = @($HostPort)
    containerPort = $ContainerPort
    llamaArgs = [ordered]@{
      ctxSize = 4096
      nParallel = 1
      threads = 4
      threadsBatch = 4
      batch = 512
      ubatch = 128
      nGpuLayers = 'all'
    }
  }
}

$ModelUrl = "https://huggingface.co/$Repo/resolve/$Revision/$File"
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
"@

for ($i = 1; $i -le [int]$capacityPlan.instances; $i++) {
  $serviceName = if ([int]$capacityPlan.instances -eq 1) { 'llama' } else { "llama-$i" }
  $containerName = if ([int]$capacityPlan.instances -eq 1) { 'saaia-llama' } else { "saaia-llama-$i" }
  $port = [int]$capacityPlan.hostPorts[$i - 1]
  $args = $capacityPlan.llamaArgs
  $compose += @"
  ${serviceName}:
    image: $LlamaImage
    container_name: $containerName
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

      # Generated by install-llm.ps1 capacity planner.
      LLAMA_ARG_CTX_SIZE: $($args.ctxSize)
      LLAMA_ARG_N_PARALLEL: $($args.nParallel)
      LLAMA_ARG_THREADS: $($args.threads)
      LLAMA_ARG_THREADS_BATCH: $($args.threadsBatch)
      LLAMA_ARG_BATCH: $($args.batch)
      LLAMA_ARG_UBATCH: $($args.ubatch)
      LLAMA_ARG_N_GPU_LAYERS: $($args.nGpuLayers)
      LLAMA_ARG_JINJA: true
      # Qwen3-4B-Instruct-2507 is the non-thinking variant. Newer llama.cpp
      # builds may otherwise auto-select the DeepSeek reasoning parser.
      LLAMA_ARG_REASONING: off

    healthcheck:
      test: ["CMD", "curl", "-fsS", "--max-time", "5", "http://localhost:$ContainerPort/health"]
      interval: 15s
      timeout: 5s
      retries: 20
      start_period: 15s

    ports:
      - "${BindAddr}:${port}:${ContainerPort}"

    restart: unless-stopped

"@

  if (-not [string]::IsNullOrWhiteSpace($DockerNetwork)) {
    $compose += @"
    networks:
      - backend

"@
  }
}

if (-not [string]::IsNullOrWhiteSpace($DockerNetwork)) {
  $compose += @"
networks:
  backend:
    name: $DockerNetwork
    external: true
"@
}

Set-Content -Encoding UTF8 -Path $composePath -Value $compose
Ok "Generated: $composePath"

$capacityPlanPath = Join-Path $DeployDir 'llm.capacity-plan.json'
($capacityPlan | ConvertTo-Json -Depth 10) | Set-Content -Encoding UTF8 $capacityPlanPath
Ok "Wrote: $capacityPlanPath"

# Write install manifest for support
$manifest = [ordered]@{
  installedAt = (Get-Date).ToString('o')
  installRoot = $InstallRoot
  repo = $Repo
  revision = $Revision
  file = $File
  url = $ModelUrl
  sha256 = $actual
  host = $BindAddr
  port = $HostPort
  containerPort = $ContainerPort
  capacityPlan = $capacityPlan
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
      $probeUrl = "http://${BindAddr}:$HostPort/v1/chat/completions"
      $probeBody = @{
        model = 'local'
        stream = $false
        temperature = 0
        max_tokens = 8
        messages = @(
          @{ role = 'system'; content = 'Reponds uniquement par READY.' },
          @{ role = 'user'; content = 'Test de qualification du runtime.' }
        )
      } | ConvertTo-Json -Depth 5 -Compress
      try {
        $probe = Invoke-RestMethod -Method Post -Uri $probeUrl -ContentType 'application/json' -Body $probeBody -TimeoutSec 60
        $content = [string]$probe.choices[0].message.content
        if ($content -match 'READY') {
          Ok "LLM is ready and completed a live inference probe."
          exit 0
        }
      } catch {
        Warn "Health endpoint is ready, but live inference probe failed: $($_.Exception.Message)"
      }
    }
  } catch { }
  Start-Sleep -Seconds 2
}
Warn "LLM did not become ready within timeout. Check container logs: docker logs saaia-llama"
