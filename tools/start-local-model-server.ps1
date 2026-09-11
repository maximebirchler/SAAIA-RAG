[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath,

    [Parameter(Mandatory = $true)]
    [string]$ModelPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [int]$Port = 12661,
    [int]$ContextSize = 4096,
    [int]$BatchSize = 512,
    [int]$UbatchSize = 128,
    [int]$Threads = 4,
    [int]$GpuLayers = 65,
    [ValidateSet("on", "off", "auto")]
    [string]$FlashAttention = "on",
    [ValidateSet("f16", "q8_0", "q4_0")]
    [string]$CacheTypeK = "f16",
    [ValidateSet("f16", "q8_0", "q4_0")]
    [string]$CacheTypeV = "f16",
    [ValidateRange(1, 16)]
    [int]$Parallel = 1,
    [string]$Device = "CUDA0",
    [switch]$Jinja,
    [int]$ReadyTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedServer = [IO.Path]::GetFullPath($ServerPath)
$resolvedModel = [IO.Path]::GetFullPath($ModelPath)
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $resolvedServer -PathType Leaf)) {
    throw "llama-server not found: $resolvedServer"
}
if (-not (Test-Path -LiteralPath $resolvedModel -PathType Leaf)) {
    throw "Model not found: $resolvedModel"
}

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$stdoutPath = Join-Path $resolvedOutput "server.stdout.log"
$stderrPath = Join-Path $resolvedOutput "server.stderr.log"
$pidPath = Join-Path $resolvedOutput "server.pid"
$profilePath = Join-Path $resolvedOutput "server-profile.json"

$arguments = [Collections.Generic.List[string]]::new()
foreach ($pair in @(
    @("--model", $resolvedModel),
    @("--host", "127.0.0.1"),
    @("--port", [string]$Port),
    @("--ctx-size", [string]$ContextSize),
    @("--batch-size", [string]$BatchSize),
    @("--ubatch-size", [string]$UbatchSize),
    @("--threads", [string]$Threads),
    @("--threads-batch", [string]$Threads),
    @("--n-gpu-layers", [string]$GpuLayers),
    @("--flash-attn", $FlashAttention),
    @("--split-mode", "none"),
    @("--device", $Device),
    @("--parallel", [string]$Parallel),
    @("--cache-type-k", $CacheTypeK),
    @("--cache-type-v", $CacheTypeV),
    @("--metrics", ""),
    @("--no-webui", "")
)) {
    $arguments.Add($pair[0])
    if (-not [string]::IsNullOrEmpty($pair[1])) {
        $arguments.Add($pair[1])
    }
}
if ($Jinja) {
    $arguments.Add("--jinja")
}

$process = Start-Process `
    -FilePath $resolvedServer `
    -ArgumentList $arguments `
    -WorkingDirectory (Split-Path $resolvedServer) `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath `
    -WindowStyle Hidden `
    -PassThru

Set-Content -LiteralPath $pidPath -Value $process.Id -Encoding ascii
[ordered]@{
    schemaVersion = 1
    capturedAt = [DateTimeOffset]::UtcNow.ToString("O")
    processId = $process.Id
    serverPath = $resolvedServer
    modelPath = $resolvedModel
    baseUrl = "http://127.0.0.1:$Port"
    contextSize = $ContextSize
    batchSize = $BatchSize
    ubatchSize = $UbatchSize
    threads = $Threads
    gpuLayers = $GpuLayers
    flashAttention = $FlashAttention
    cacheTypeK = $CacheTypeK
    cacheTypeV = $CacheTypeV
    parallel = $Parallel
    device = $Device
    jinja = [bool]$Jinja
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $profilePath -Encoding UTF8

$baseUrl = "http://127.0.0.1:$Port"
$ready = $false
$models = $null
for ($attempt = 0; $attempt -lt $ReadyTimeoutSeconds; $attempt++) {
    if ($process.HasExited) {
        break
    }

    try {
        $health = Invoke-RestMethod -Uri "$baseUrl/health" -TimeoutSec 2
        if ($health.status -eq "ok") {
            $models = Invoke-RestMethod -Uri "$baseUrl/v1/models" -TimeoutSec 10
            $ready = $true
            break
        }
    }
    catch {
        Start-Sleep -Seconds 1
    }
}

if (-not $ready) {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $stderrPath) {
        Get-Content -LiteralPath $stderrPath -Tail 80
    }
    throw "llama-server did not become ready at $baseUrl."
}

[pscustomobject]@{
    ProcessId = $process.Id
    BaseUrl = $baseUrl
    ModelId = [string]$models.data[0].id
    OutputDirectory = $resolvedOutput
}
