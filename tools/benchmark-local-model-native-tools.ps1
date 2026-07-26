[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LlamaServerPath,

    [Parameter(Mandatory = $true)]
    [string]$ModelPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$ProfileName = "native-tool-calling",
    [int]$Port = 18356,
    [int]$GpuLayers = 65,
    [int]$ContextSize = 4096,
    [int]$BatchSize = 1024,
    [int]$UbatchSize = 256,
    [int]$Threads = 4,
    [ValidateSet("on", "off", "auto")]
    [string]$FlashAttention = "on",
    [ValidateRange(0.0, 2.0)]
    [double]$Temperature = 0.0,
    [ValidateRange(0.0, 1.0)]
    [double]$TopP = 1.0,
    [ValidateRange(0, 200)]
    [int]$TopK = 40,
    [ValidateRange(0.0, 1.0)]
    [double]$MinP = 0.0,
    [int]$Seed = 42,
    [switch]$SwaFull
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Wait-LlamaReady {
    param([string]$BaseUrl, [int]$TimeoutSeconds = 120)

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

function Test-Contains {
    param([AllowNull()][object]$Value, [string]$Needle)
    return ([string]$Value).IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-MessageToolCalls {
    param([object]$Message)

    if ($null -eq $Message -or
        $null -eq $Message.PSObject.Properties["tool_calls"] -or
        $null -eq $Message.tool_calls) {
        return @()
    }

    return @($Message.tool_calls)
}

function New-FunctionTool {
    param(
        [string]$Name,
        [string]$Description,
        [Collections.IDictionary]$Properties,
        [string[]]$Required
    )

    return [ordered]@{
        type = "function"
        function = [ordered]@{
            name = $Name
            description = $Description
            parameters = [ordered]@{
                type = "object"
                properties = $Properties
                required = $Required
                additionalProperties = $false
            }
        }
    }
}

function Get-Scenarios {
    $ragSearch = New-FunctionTool `
        -Name "rag_search" `
        -Description "Search the indexed knowledge base for source-backed evidence." `
        -Properties ([ordered]@{
            query = [ordered]@{ type = "string" }
            docRef = [ordered]@{ type = "string" }
        }) `
        -Required @("query", "docRef")
    $memoryRead = New-FunctionTool `
        -Name "memory_read" `
        -Description "Read current user preferences relevant to the request." `
        -Properties ([ordered]@{
            purpose = [ordered]@{ type = "string" }
        }) `
        -Required @("purpose")

    return @(
        [pscustomobject]@{
            name = "named_document_source_lookup"
            maximumScore = 5
            maxTokens = 320
            tools = @($ragSearch, $memoryRead)
            system = @"
You are the semantic orchestrator. Select the single best next external tool.
For a factual question about a named document, retrieve current evidence and
preserve the exact document reference. Do not answer from model memory.
"@.Trim()
            user = @"
Selon le manuel HydraulicPumpManual.pdf, quel est le couple de serrage des
boulons du couvercle de la pompe HX-42 ?
"@.Trim()
            evaluate = {
                param($message)
                $score = 0
                $reasons = [Collections.Generic.List[string]]::new()
                $calls = @(Get-MessageToolCalls $message)
                if ($calls.Count -eq 1) {
                    $score++
                } else {
                    $reasons.Add("tool_call_count_wrong")
                }
                $call = $calls | Select-Object -First 1
                $name = if ($null -ne $call -and $null -ne $call.function) {
                    [string]$call.function.name
                } else {
                    ""
                }
                if ($name -eq "rag_search") {
                    $score++
                } else {
                    $reasons.Add("tool_name_wrong")
                }
                $arguments = $null
                try {
                    $arguments = if ($null -ne $call -and $null -ne $call.function) {
                        ([string]$call.function.arguments) | ConvertFrom-Json
                    } else {
                        $null
                    }
                }
                catch {
                    $arguments = $null
                }
                if ($null -ne $arguments -and
                    [string]$arguments.docRef -eq "HydraulicPumpManual.pdf") {
                    $score++
                } else {
                    $reasons.Add("document_reference_wrong")
                }
                $query = if ($null -ne $arguments) { [string]$arguments.query } else { "" }
                if ((Test-Contains $query "HX-42") -or (Test-Contains $query "HX42")) {
                    $score++
                } else {
                    $reasons.Add("query_target_missing")
                }
                if ((Test-Contains $query "couple") -or
                    (Test-Contains $query "serrage") -or
                    (Test-Contains $query "torque")) {
                    $score++
                } else {
                    $reasons.Add("query_measure_missing")
                }
                [pscustomobject]@{ score = $score; reasons = @($reasons) }
            }
        },
        [pscustomobject]@{
            name = "preference_lookup_first_step"
            maximumScore = 3
            maxTokens = 240
            tools = @($ragSearch, $memoryRead)
            system = @"
You are the semantic orchestrator. Make exactly one next-step tool call.
The user asks for a plan adapted to current personal preferences, so retrieve
those preferences before searching for source-backed items.
"@.Trim()
            user = @"
Prépare un planning de repas adapté à mes préférences actuelles et fondé sur
la base documentaire.
"@.Trim()
            evaluate = {
                param($message)
                $score = 0
                $reasons = [Collections.Generic.List[string]]::new()
                $calls = @(Get-MessageToolCalls $message)
                if ($calls.Count -eq 1) {
                    $score++
                } else {
                    $reasons.Add("tool_call_count_wrong")
                }
                $call = $calls | Select-Object -First 1
                $name = if ($null -ne $call -and $null -ne $call.function) {
                    [string]$call.function.name
                } else {
                    ""
                }
                if ($name -eq "memory_read") {
                    $score++
                } else {
                    $reasons.Add("tool_name_wrong")
                }
                $purpose = ""
                try {
                    $purpose = if ($null -ne $call -and $null -ne $call.function) {
                        [string]((([string]$call.function.arguments) | ConvertFrom-Json).purpose)
                    } else {
                        ""
                    }
                }
                catch {
                    $purpose = ""
                }
                if ((Test-Contains $purpose "préférence") -or
                    (Test-Contains $purpose "preference") -or
                    (Test-Contains $purpose "aliment") -or
                    (Test-Contains $purpose "diet") -or
                    (Test-Contains $purpose "repas") -or
                    (Test-Contains $purpose "planning")) {
                    $score++
                } else {
                    $reasons.Add("memory_purpose_wrong")
                }
                [pscustomobject]@{ score = $score; reasons = @($reasons) }
            }
        },
        [pscustomobject]@{
            name = "no_unnecessary_tool"
            maximumScore = 2
            maxTokens = 160
            tools = @($ragSearch, $memoryRead)
            system = @"
Use an external tool only when it is needed. For a pure rewriting request,
answer directly without calling a tool.
"@.Trim()
            user = "Reformule en français professionnel : le rapport est pas fini."
            evaluate = {
                param($message)
                $score = 0
                $reasons = [Collections.Generic.List[string]]::new()
                if (@(Get-MessageToolCalls $message).Count -eq 0) {
                    $score++
                } else {
                    $reasons.Add("unnecessary_tool_call")
                }
                if (-not [string]::IsNullOrWhiteSpace([string]$message.content)) {
                    $score++
                } else {
                    $reasons.Add("direct_answer_missing")
                }
                [pscustomobject]@{ score = $score; reasons = @($reasons) }
            }
        }
    )
}

$serverPath = [IO.Path]::GetFullPath($LlamaServerPath)
$resolvedModelPath = [IO.Path]::GetFullPath($ModelPath)
$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) {
    throw "llama-server not found: $serverPath"
}
if (-not (Test-Path -LiteralPath $resolvedModelPath -PathType Leaf)) {
    throw "Model not found: $resolvedModelPath"
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
$safeName = (($ProfileName -replace "[^A-Za-z0-9._-]", "-").Trim("-"))
if ([string]::IsNullOrWhiteSpace($safeName)) {
    $safeName = "native-tool-calling"
}
$stdoutPath = Join-Path $resolvedOutputDirectory "$safeName-server.stdout.log"
$stderrPath = Join-Path $resolvedOutputDirectory "$safeName-server.stderr.log"
$artifactPath = Join-Path $resolvedOutputDirectory "$safeName-tools.json"
$baseUrl = "http://127.0.0.1:$Port"
$arguments = @(
    "--model", $resolvedModelPath,
    "--host", "127.0.0.1",
    "--port", [string]$Port,
    "--ctx-size", [string]$ContextSize,
    "--batch-size", [string]$BatchSize,
    "--ubatch-size", [string]$UbatchSize,
    "--threads", [string]$Threads,
    "--threads-batch", [string]$Threads,
    "--n-gpu-layers", [string]$GpuLayers,
    "--flash-attn", $FlashAttention,
    "--split-mode", "none",
    "--device", "CUDA0",
    "--parallel", "1",
    "--cache-type-k", "f16",
    "--cache-type-v", "f16",
    "--jinja",
    "--no-webui"
)
if ($SwaFull) {
    $arguments += "--swa-full"
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
    $models = Wait-LlamaReady -BaseUrl $baseUrl
    $modelId = [string]$models.data[0].id
    $props = Invoke-RestMethod -Method Get -Uri "$baseUrl/props" -TimeoutSec 10
    $results = [Collections.Generic.List[object]]::new()

    foreach ($scenario in Get-Scenarios) {
        Write-Output "NATIVE_TOOL_SCENARIO_START name=$($scenario.name)"
        $payload = [ordered]@{
            model = $modelId
            messages = @(
                [ordered]@{ role = "system"; content = $scenario.system },
                [ordered]@{ role = "user"; content = $scenario.user }
            )
            tools = $scenario.tools
            tool_choice = "auto"
            temperature = $Temperature
            top_p = $TopP
            top_k = $TopK
            min_p = $MinP
            seed = $Seed
            max_tokens = $scenario.maxTokens
            stream = $false
        }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try {
            $response = Invoke-RestMethod `
                -Method Post `
                -Uri "$baseUrl/v1/chat/completions" `
                -ContentType "application/json" `
                -Body ($payload | ConvertTo-Json -Depth 30 -Compress) `
                -TimeoutSec 300
            $watch.Stop()
            $message = $response.choices[0].message
            $evaluation = & $scenario.evaluate $message
            $results.Add([pscustomobject]@{
                name = $scenario.name
                succeeded = $true
                elapsedMs = [int]$watch.ElapsedMilliseconds
                score = $evaluation.score
                maximumScore = $scenario.maximumScore
                reasons = @($evaluation.reasons)
                finishReason = [string]$response.choices[0].finish_reason
                promptTokens = [int]$response.usage.prompt_tokens
                completionTokens = [int]$response.usage.completion_tokens
                content = [string]$message.content
                toolCalls = @(Get-MessageToolCalls $message)
                error = $null
            })
        }
        catch {
            $watch.Stop()
            $results.Add([pscustomobject]@{
                name = $scenario.name
                succeeded = $false
                elapsedMs = [int]$watch.ElapsedMilliseconds
                score = 0
                maximumScore = $scenario.maximumScore
                reasons = @("request_failed")
                finishReason = "request_failed"
                promptTokens = $null
                completionTokens = $null
                content = $null
                toolCalls = @()
                error = $_.Exception.Message
            })
        }
        $last = $results[$results.Count - 1]
        Write-Output "NATIVE_TOOL_SCENARIO_DONE name=$($scenario.name) score=$($last.score)/$($last.maximumScore)"
    }

    $totalScore = ($results | Measure-Object -Property score -Sum).Sum
    $maximumScore = ($results | Measure-Object -Property maximumScore -Sum).Sum
    $artifact = [ordered]@{
        schemaVersion = 1
        capturedAt = (Get-Date).ToUniversalTime().ToString("o")
        profileName = $ProfileName
        modelId = $modelId
        modelPath = $resolvedModelPath
        generation = [ordered]@{
            temperature = $Temperature
            topP = $TopP
            topK = $TopK
            minP = $MinP
            seed = $Seed
        }
        runtime = [ordered]@{
            contextSize = $ContextSize
            batchSize = $BatchSize
            ubatchSize = $UbatchSize
            threads = $Threads
            gpuLayers = $GpuLayers
            flashAttention = $FlashAttention
            swaFull = [bool]$SwaFull
        }
        chatTemplateCapabilities = $props.chat_template_caps
        elapsedMs = [int]((Get-Date) - $startedAt).TotalMilliseconds
        scenarioCount = $results.Count
        successfulScenarioCount = @($results | Where-Object succeeded).Count
        fullyCorrectScenarioCount = @(
            $results | Where-Object { $_.score -eq $_.maximumScore }
        ).Count
        totalScore = $totalScore
        maximumScore = $maximumScore
        scoreRatio = [math]::Round($totalScore / [math]::Max(1, $maximumScore), 4)
        results = $results
        stdoutLog = $stdoutPath
        stderrLog = $stderrPath
    }
    $artifact | ConvertTo-Json -Depth 30 |
        Set-Content -LiteralPath $artifactPath -Encoding UTF8
    $artifact | ConvertTo-Json -Depth 30
}
finally {
    Stop-LlamaServer -Process $process
}
