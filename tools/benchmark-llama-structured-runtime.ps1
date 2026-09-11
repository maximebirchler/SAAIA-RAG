[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LlamaServerPath,

    [Parameter(Mandatory = $true)]
    [string]$ModelPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$ProfileName = "runtime-profile",
    [string[]]$DeviceIds = @(),

    [ValidateSet("none", "layer", "row", "tensor")]
    [string]$SplitMode = "none",

    [double[]]$TensorSplit = @(),
    [int]$MainGpu = 0,
    [int]$GpuLayers = 99,
    [int]$ContextSize = 4096,
    [int]$BatchSize = 512,
    [int]$UbatchSize = 128,
    [int]$Threads = 6,
    [int]$Repetitions = 3,
    [int]$Port = 18245,

    [ValidateSet("on", "off", "auto")]
    [string]$FlashAttention = "on",

    [ValidateSet("f16", "q8_0", "q4_0")]
    [string]$CacheTypeK = "f16",

    [ValidateSet("f16", "q8_0", "q4_0")]
    [string]$CacheTypeV = "f16"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Wait-LlamaReady {
    param(
        [string]$BaseUrl,
        [int]$TimeoutSeconds = 90
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-RestMethod -Method Get -Uri "$BaseUrl/v1/models" -TimeoutSec 3
            if ($response.data -and $response.data.Count -gt 0) {
                return $response
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }

    throw "llama-server did not become ready within ${TimeoutSeconds}s."
}

function Stop-LlamaServer {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $Process.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
    }
    catch {
        Write-Warning "Unable to stop llama-server PID $($Process.Id): $($_.Exception.Message)"
    }
}

function New-StructuredProbePayload {
    param([string]$ModelId)

    $classes = @(
        "named_item_suitable_for_slot",
        "instruction_or_action",
        "isolated_component_or_ingredient",
        "heading_or_broad_category",
        "incomplete_or_other_wrong_type"
    )
    $properties = [ordered]@{}
    foreach ($candidateId in @("V1", "V2", "V3", "V4")) {
        $properties[$candidateId] = [ordered]@{
            type = "string"
            enum = $classes
        }
    }

    $systemPrompt = @"
SAAIA_SOURCE_BACKED_STEP=StructuredValueTypeFitDecisionBatch
Classify the semantic type of each VALUE_AS_WRITTEN relative to its REQUESTED_FIELD. Do not rewrite values, plan retrieval or judge layout.
Classify the grammatical and semantic role of VALUE_AS_WRITTEN itself. CITED_EXCERPTS only prove what that value refers to; instructions or ingredients elsewhere in an excerpt do not turn a self-standing item name into an action or component.
named_item_suitable_for_slot: the value is a self-standing named item of the type requested by the field.
instruction_or_action: the value tells someone to do something or describes a procedure step.
isolated_component_or_ingredient: the value is only one component or ingredient where the field expects the assembled item.
heading_or_broad_category: the value is only a heading, family, topic or broad category rather than one item.
incomplete_or_other_wrong_type: the value is a fragment or another semantic type not requested by the field.
Decision order: first test whether the value is an action; then whether it is only a component or measured ingredient; then whether it is a broad heading or an incomplete reference. Use named_item_suitable_for_slot only when the value itself can independently fill the requested field.
A noun phrase is not automatically a suitable named item. When the field expects an assembled meal, a measured amount of flour, oil, stock or another ingredient is isolated_component_or_ingredient.
Examples:
REQUESTED_FIELD=transport mode, VALUE_AS_WRITTEN=train => named_item_suitable_for_slot.
REQUESTED_FIELD=lunch, VALUE_AS_WRITTEN=mushroom risotto => named_item_suitable_for_slot.
REQUESTED_FIELD=technical document, VALUE_AS_WRITTEN=FIT-PTFE_TF_1620-EN.pdf => named_item_suitable_for_slot.
REQUESTED_FIELD=lunch, VALUE_AS_WRITTEN=mix all ingredients => instruction_or_action.
REQUESTED_FIELD=complete meal, VALUE_AS_WRITTEN=olive oil => isolated_component_or_ingredient.
REQUESTED_FIELD=product, VALUE_AS_WRITTEN=industrial components => heading_or_broad_category.
REQUESTED_FIELD=technical document, VALUE_AS_WRITTEN=the latest one => incomplete_or_other_wrong_type.
REQUESTED_FIELD=meal, VALUE_AS_WRITTEN=roasted vegetable soup, CITED_EXCERPT=roasted vegetable soup followed by ingredients and preparation => named_item_suitable_for_slot.
REQUESTED_FIELD=meal, VALUE_AS_WRITTEN=stir until smooth, CITED_EXCERPT=a complete recipe containing that step => instruction_or_action.
Judge each immutable candidate independently using its cited excerpt. Return exactly one class per schema property and no explanation.
"@

    $userPrompt = @"
USER_QUESTION: Classify four immutable candidate values for a source-backed structured plan.
LANGUAGE: en
VALUE_CANDIDATES:
V1 | REQUESTED_FIELD=meal | VALUE_AS_WRITTEN=roasted vegetable soup | EVIDENCE_IDS=E1
V2 | REQUESTED_FIELD=meal | VALUE_AS_WRITTEN=stir until smooth | EVIDENCE_IDS=E2
V3 | REQUESTED_FIELD=product | VALUE_AS_WRITTEN=industrial components | EVIDENCE_IDS=E3
V4 | REQUESTED_FIELD=complete meal | VALUE_AS_WRITTEN=2 tablespoons flour | EVIDENCE_IDS=E4
CITED_EXCERPTS:
[E1] Roasted vegetable soup. Ingredients: vegetables and stock. Preparation: roast, blend and serve.
[E2] For the complete sauce recipe, add the liquid and stir until smooth before serving.
[E3] Industrial components is the broad catalogue category containing several unrelated product families.
[E4] The complete meal is vegetable stew. Its ingredients include 2 tablespoons flour, vegetable stock and onions.
EXPECTED_CANDIDATE_IDS: V1,V2,V3,V4
"@

    return [ordered]@{
        model = $ModelId
        messages = @(
            [ordered]@{ role = "system"; content = $systemPrompt.Trim() },
            [ordered]@{ role = "user"; content = $userPrompt.Trim() }
        )
        temperature = 0.0
        top_p = 1.0
        frequency_penalty = 0.0
        presence_penalty = 0.0
        max_tokens = 96
        stream = $false
        response_format = [ordered]@{
            type = "json_schema"
            json_schema = [ordered]@{
                name = "structured_value_type_decision_batch"
                strict = $true
                schema = [ordered]@{
                    type = "object"
                    properties = $properties
                    required = @("V1", "V2", "V3", "V4")
                    additionalProperties = $false
                }
            }
        }
    }
}

function Test-StructuredProbeResult {
    param([string]$Content)

    $expected = [ordered]@{
        V1 = "named_item_suitable_for_slot"
        V2 = "instruction_or_action"
        V3 = "heading_or_broad_category"
        V4 = "isolated_component_or_ingredient"
    }

    try {
        $parsed = $Content | ConvertFrom-Json
        $correct = 0
        foreach ($candidateId in $expected.Keys) {
            $property = $parsed.PSObject.Properties[$candidateId]
            if ($property -and [string]$property.Value -eq $expected[$candidateId]) {
                $correct++
            }
        }

        return [pscustomobject]@{
            JsonValid = $true
            CorrectCount = $correct
            ExpectedCount = $expected.Count
        }
    }
    catch {
        return [pscustomobject]@{
            JsonValid = $false
            CorrectCount = 0
            ExpectedCount = $expected.Count
        }
    }
}

$serverPath = [System.IO.Path]::GetFullPath($LlamaServerPath)
$resolvedModelPath = [System.IO.Path]::GetFullPath($ModelPath)
$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) {
    throw "llama-server not found: $serverPath"
}
if (-not (Test-Path -LiteralPath $resolvedModelPath -PathType Leaf)) {
    throw "Model not found: $resolvedModelPath"
}
if ($Repetitions -lt 1 -or $Repetitions -gt 20) {
    throw "Repetitions must be between 1 and 20."
}
if ($TensorSplit.Count -gt 0 -and $DeviceIds.Count -ne $TensorSplit.Count) {
    throw "TensorSplit must contain exactly one weight per DeviceIds entry."
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
$safeName = (($ProfileName -replace "[^A-Za-z0-9._-]", "-").Trim("-"))
if ([string]::IsNullOrWhiteSpace($safeName)) {
    $safeName = "runtime-profile"
}

$stdoutPath = Join-Path $resolvedOutputDirectory "$safeName-server.stdout.log"
$stderrPath = Join-Path $resolvedOutputDirectory "$safeName-server.stderr.log"
$resultPath = Join-Path $resolvedOutputDirectory "$safeName-structured-probe.json"
$baseUrl = "http://127.0.0.1:$Port"
$arguments = [System.Collections.Generic.List[string]]::new()
foreach ($pair in @(
    @("--model", $resolvedModelPath),
    @("--host", "127.0.0.1"),
    @("--port", [string]$Port),
    @("--ctx-size", [string]$ContextSize),
    @("--batch-size", [string]$BatchSize),
    @("--ubatch-size", [string]$UbatchSize),
    @("--threads", [string]$Threads),
    @("--threads-batch", [string]$Threads),
    @("--n-gpu-layers", [string]$GpuLayers),
    @("--flash-attn", $FlashAttention),
    @("--split-mode", $SplitMode),
    @("--main-gpu", [string]$MainGpu),
    @("--parallel", "1"),
    @("--cache-type-k", $CacheTypeK),
    @("--cache-type-v", $CacheTypeV)
)) {
    $arguments.Add($pair[0])
    $arguments.Add($pair[1])
}
if ($DeviceIds.Count -gt 0 -and -not ($DeviceIds.Count -eq 1 -and $DeviceIds[0] -eq "none")) {
    $arguments.Add("--device")
    $arguments.Add(($DeviceIds -join ","))
}
if ($TensorSplit.Count -gt 0) {
    $arguments.Add("--tensor-split")
    $arguments.Add(($TensorSplit -join ","))
}

$process = $null
$startedAt = Get-Date
try {
    $process = Start-Process `
        -FilePath $serverPath `
        -ArgumentList $arguments `
        -WorkingDirectory (Split-Path $serverPath) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru

    $modelsResponse = Wait-LlamaReady -BaseUrl $baseUrl
    $loadMs = [int]((Get-Date) - $startedAt).TotalMilliseconds
    $modelId = [string]$modelsResponse.data[0].id
    $payload = New-StructuredProbePayload -ModelId $modelId
    $body = $payload | ConvertTo-Json -Depth 20 -Compress
    $runs = [System.Collections.Generic.List[object]]::new()

    for ($runNumber = 1; $runNumber -le $Repetitions; $runNumber++) {
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $response = Invoke-RestMethod `
                -Method Post `
                -Uri "$baseUrl/v1/chat/completions" `
                -ContentType "application/json" `
                -Body $body `
                -TimeoutSec 180
            $stopwatch.Stop()
            $content = [string]$response.choices[0].message.content
            $evaluation = Test-StructuredProbeResult -Content $content
            $runs.Add([pscustomobject]@{
                run = $runNumber
                succeeded = $true
                elapsedMs = $stopwatch.ElapsedMilliseconds
                promptTokens = [int]$response.usage.prompt_tokens
                completionTokens = [int]$response.usage.completion_tokens
                jsonValid = $evaluation.JsonValid
                correctCount = $evaluation.CorrectCount
                expectedCount = $evaluation.ExpectedCount
                content = $content
                error = $null
            })
        }
        catch {
            $stopwatch.Stop()
            $runs.Add([pscustomobject]@{
                run = $runNumber
                succeeded = $false
                elapsedMs = $stopwatch.ElapsedMilliseconds
                promptTokens = $null
                completionTokens = $null
                jsonValid = $false
                correctCount = 0
                expectedCount = 4
                content = $null
                error = $_.Exception.Message
            })
        }
    }

    $successfulRuns = @($runs | Where-Object { $_.succeeded })
    $artifact = [ordered]@{
        schemaVersion = 1
        capturedAt = (Get-Date).ToUniversalTime().ToString("o")
        profileName = $ProfileName
        runtime = [ordered]@{
            executablePath = $serverPath
            modelPath = $resolvedModelPath
            modelId = $modelId
            deviceIds = $DeviceIds
            splitMode = $SplitMode
            tensorSplit = $TensorSplit
            mainGpu = $MainGpu
            gpuLayers = $GpuLayers
            contextSize = $ContextSize
            batchSize = $BatchSize
            ubatchSize = $UbatchSize
            threads = $Threads
            flashAttention = $FlashAttention
            cacheTypeK = $CacheTypeK
            cacheTypeV = $CacheTypeV
        }
        loadMs = $loadMs
        repetitions = $Repetitions
        successfulRuns = $successfulRuns.Count
        fullyCorrectRuns = @($runs | Where-Object { $_.correctCount -eq $_.expectedCount }).Count
        averageElapsedMs = if ($successfulRuns.Count -gt 0) {
            [math]::Round(($successfulRuns | Measure-Object -Property elapsedMs -Average).Average, 2)
        } else {
            $null
        }
        runs = $runs
        stdoutLog = $stdoutPath
        stderrLog = $stderrPath
    }
    $artifact | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    $artifact | ConvertTo-Json -Depth 20
}
finally {
    Stop-LlamaServer -Process $process
}
