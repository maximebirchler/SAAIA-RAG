[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$QualityScriptPath,

    [Parameter(Mandatory = $true)]
    [string]$LlamaServerPath,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string[]]$ScenarioNames = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$qualityScript = [IO.Path]::GetFullPath($QualityScriptPath)
$serverPath = [IO.Path]::GetFullPath($LlamaServerPath)
$manifest = [IO.Path]::GetFullPath($ManifestPath)
$output = [IO.Path]::GetFullPath($OutputDirectory)

foreach ($requiredFile in @($qualityScript, $serverPath, $manifest)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required file not found: $requiredFile"
    }
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$definitions = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
if ($definitions.Count -eq 0) {
    throw "The quality matrix manifest contains no model definitions."
}

$matrixStartedAt = Get-Date
$matrixResults = [Collections.Generic.List[object]]::new()
foreach ($definition in $definitions) {
    $profileName = [string]$definition.profileName
    Write-Output "QUALITY_MATRIX_MODEL_START profile=$profileName"
    $startedAt = Get-Date
    try {
        $parameters = @{
            LlamaServerPath = $serverPath
            ModelPath = [string]$definition.modelPath
            OutputDirectory = $output
            ProfileName = $profileName
            Port = [int]$definition.port
            GpuLayers = [int]$definition.gpuLayers
            ContextSize = [int]$definition.contextSize
            BatchSize = [int]$definition.batchSize
            UbatchSize = [int]$definition.ubatchSize
            Threads = [int]$definition.threads
            FlashAttention = [string]$definition.flashAttention
            CacheTypeK = [string]$definition.cacheTypeK
            CacheTypeV = [string]$definition.cacheTypeV
            ScenarioNames = $ScenarioNames
        }
        if ($null -ne $definition.PSObject.Properties["temperature"]) {
            $parameters.Temperature = [double]$definition.temperature
        }
        if ($null -ne $definition.PSObject.Properties["topP"]) {
            $parameters.TopP = [double]$definition.topP
        }
        if ($null -ne $definition.PSObject.Properties["topK"]) {
            $parameters.TopK = [int]$definition.topK
        }
        if ($null -ne $definition.PSObject.Properties["minP"]) {
            $parameters.MinP = [double]$definition.minP
        }
        if ($null -ne $definition.PSObject.Properties["seed"]) {
            $parameters.Seed = [int]$definition.seed
        }
        if ($null -ne $definition.PSObject.Properties["maxTokenScale"]) {
            $parameters.MaxTokenScale = [double]$definition.maxTokenScale
        }
        if ($null -ne $definition.PSObject.Properties["messageMode"]) {
            $parameters.MessageMode = [string]$definition.messageMode
        }
        if ($null -ne $definition.PSObject.Properties["outputMode"]) {
            $parameters.OutputMode = [string]$definition.outputMode
        }
        if ($definition.swaFull) {
            $parameters.SwaFull = $true
        }

        & $qualityScript @parameters
        $artifactPath = Join-Path $output "$profileName-quality.json"
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            throw "Expected quality artifact not produced: $artifactPath"
        }
        $artifact = Get-Content -LiteralPath $artifactPath -Raw | ConvertFrom-Json
        $matrixResults.Add([pscustomobject]@{
            profileName = $profileName
            succeeded = $true
            elapsedMs = [int]((Get-Date) - $startedAt).TotalMilliseconds
            score = $artifact.totalScore
            maximumScore = $artifact.maximumScore
            scoreRatio = $artifact.scoreRatio
            error = $null
        })
        Write-Output (
            "QUALITY_MATRIX_MODEL_DONE profile={0} score={1}/{2}" -f
            $profileName,
            $artifact.totalScore,
            $artifact.maximumScore)
    }
    catch {
        $matrixResults.Add([pscustomobject]@{
            profileName = $profileName
            succeeded = $false
            elapsedMs = [int]((Get-Date) - $startedAt).TotalMilliseconds
            score = 0
            maximumScore = 0
            scoreRatio = 0
            error = $_.Exception.Message
        })
        Write-Warning "Quality matrix failed for $profileName`: $($_.Exception.Message)"
    }
}

$matrixArtifact = [ordered]@{
    schemaVersion = 1
    capturedAt = (Get-Date).ToUniversalTime().ToString("o")
    elapsedMs = [int]((Get-Date) - $matrixStartedAt).TotalMilliseconds
    scenarioNames = @($ScenarioNames)
    modelCount = $definitions.Count
    successfulModelCount = @($matrixResults | Where-Object succeeded).Count
    results = @($matrixResults)
}
$matrixArtifactPath = Join-Path $output "quality-matrix-summary.json"
$matrixArtifact |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $matrixArtifactPath -Encoding UTF8
Write-Output "QUALITY_MATRIX_ARTIFACT=$matrixArtifactPath"
