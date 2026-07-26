[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LlamaServerPath,

    [Parameter(Mandatory = $true)]
    [string]$ModelPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$ProfileName = "category-selection",
    [int]$Port = 18359,
    [int]$GpuLayers = 65,
    [int]$ContextSize = 4096,
    [int]$BatchSize = 1024,
    [int]$UbatchSize = 256,
    [int]$Threads = 4,
    [ValidateSet("on", "off", "auto")]
    [string]$FlashAttention = "on",
    [ValidateRange(0.0, 2.0)]
    [double]$Temperature = 0.15,
    [ValidateRange(0.0, 1.0)]
    [double]$TopP = 1.0,
    [ValidateRange(0, 200)]
    [int]$TopK = 40,
    [ValidateRange(0.0, 1.0)]
    [double]$MinP = 0.0,
    [int]$Seed = 42,
    [string[]]$ScenarioIds,
    [switch]$SwaFull
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Wait-LlamaReady {
    param([string]$BaseUrl, [int]$TimeoutSeconds = 180)

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

function Invoke-ChatCompletion {
    param(
        [string]$BaseUrl,
        [Collections.IDictionary]$Payload,
        [int]$TimeoutSeconds = 300
    )

    $json = $Payload | ConvertTo-Json -Depth 40 -Compress
    $utf8Body = [Text.Encoding]::UTF8.GetBytes($json)
    return Invoke-RestMethod `
        -Method Post `
        -Uri "$BaseUrl/v1/chat/completions" `
        -ContentType "application/json; charset=utf-8" `
        -Body $utf8Body `
        -TimeoutSec $TimeoutSeconds
}

function Get-ToolCalls {
    param([AllowNull()][object]$Message)

    if ($null -eq $Message -or
        $null -eq $Message.PSObject.Properties["tool_calls"] -or
        $null -eq $Message.tool_calls) {
        return @()
    }

    return @($Message.tool_calls)
}

function Convert-ToolArguments {
    param([AllowNull()][object]$Arguments)

    if ($null -eq $Arguments) {
        return $null
    }
    if ($Arguments -is [string]) {
        try {
            return $Arguments | ConvertFrom-Json
        }
        catch {
            return $null
        }
    }
    return $Arguments
}

function Get-RealCatalogPaths {
    $json = @'
[
  "Cuisine",
  "Documents techniques",
  "Achats - devis - fournisseurs",
  "Assurance - CG - Police",
  "Audit",
  "Documents scann\u00e9s  - OCR imparfait",
  "Catalogue commercial",
  "CDC",
  "Certifications",
  "Construction",
  "Documents avec annexe",
  "Documents avec versions multiples",
  "Documents contradictoires",
  "Emails - R\u00e9union",
  "Environnement - Energie",
  "Formations - Education",
  "Finance - Compta",
  "Immobilier",
  "Juridique",
  "Manuels logiciel",
  "MultiLingues",
  "Support client - FAQ",
  "M\u00e9dical",
  "Normes",
  "Normes automation",
  "Programmation",
  "RCA",
  "RH",
  "SOP - GMP - Quality",
  "Tableaux complexes",
  "Voyages"
]
'@
    $parsed = $json | ConvertFrom-Json
    foreach ($path in $parsed) {
        Write-Output ([string]$path)
    }
}

function Get-Scenarios {
    return @(
        [pscustomobject]@{
            id = "meal_plan_full_context"
            promptMode = "full"
            expectedScopeId = 1
            userRequest = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi avec petit-dejeuner, diner, souper et gouter ou collation chaque jour. N'invente rien et utilise uniquement les sources utiles."
            targets = @("Petit-dejeuner", "Collation", "Diner", "Souper")
        },
        [pscustomobject]@{
            id = "meal_plan_current_compact_context"
            promptMode = "compact"
            expectedScopeId = 1
            userRequest = ""
            targets = @("Petit-dejeuner", "Collation", "Diner", "Souper")
        },
        [pscustomobject]@{
            id = "commercial_product_catalog"
            promptMode = "full"
            expectedScopeId = 7
            userRequest = "Trouve les gammes de regulateurs de pression et leurs references disponibles dans les catalogues produits."
            targets = @()
        },
        [pscustomobject]@{
            id = "technical_datasheet"
            promptMode = "full"
            expectedScopeId = 2
            userRequest = "Retrouve la fiche technique d'un raccord PTFE et indique ses limites de pression et de temperature."
            targets = @()
        },
        [pscustomobject]@{
            id = "supplier_quotes"
            promptMode = "full"
            expectedScopeId = 3
            userRequest = "Compare les devis recus de nos fournisseurs pour le dernier achat et cite les montants."
            targets = @()
        },
        [pscustomobject]@{
            id = "insurance_policy"
            promptMode = "full"
            expectedScopeId = 4
            userRequest = "Quelles exclusions et franchises sont prevues par notre police d'assurance ?"
            targets = @()
        },
        [pscustomobject]@{
            id = "finance_accounting"
            promptMode = "full"
            expectedScopeId = 17
            userRequest = "Resume les charges et produits du dernier exercice comptable a partir des documents financiers."
            targets = @()
        },
        [pscustomobject]@{
            id = "software_manual"
            promptMode = "full"
            expectedScopeId = 20
            userRequest = "Comment configurer l'export PDF dans le logiciel d'apres son manuel utilisateur ?"
            targets = @()
        },
        [pscustomobject]@{
            id = "human_resources"
            promptMode = "full"
            expectedScopeId = 28
            userRequest = "Quelle est la procedure interne pour demander des vacances et faire valider une absence ?"
            targets = @()
        },
        [pscustomobject]@{
            id = "travel"
            promptMode = "full"
            expectedScopeId = 31
            userRequest = "Prepare un itineraire de voyage a partir des guides et informations de destination disponibles."
            targets = @()
        }
    )
}

function New-ScopeSelectionTool {
    param([int]$MaximumScopeId)

    return [ordered]@{
        type = "function"
        function = [ordered]@{
            name = "select_retrieval_scope"
            description = "Return the semantic corpus scope selected by the LLM."
            parameters = [ordered]@{
                type = "object"
                properties = [ordered]@{
                    scopeId = [ordered]@{
                        type = "integer"
                        enum = @(0..$MaximumScopeId)
                        description = "One listed scope id. Use 0 only when no category fits."
                    }
                    reason = [ordered]@{
                        type = "string"
                        minLength = 1
                        maxLength = 240
                    }
                }
                required = @("scopeId", "reason")
                additionalProperties = $false
            }
        }
    }
}

function Build-FullMessages {
    param([object]$Scenario, [string[]]$CatalogPaths)

    $scopeLines = for ($index = 0; $index -lt $CatalogPaths.Count; $index++) {
        "- $($index + 1): categoryPath=$($CatalogPaths[$index])"
    }
    $system = @"
You are the semantic retrieval-scope adjudicator for a source-backed RAG system.
Do not answer the end user. Call select_retrieval_scope exactly once.
Choose the corpus that most plausibly contains the answer-bearing source items.
A plan, schedule, comparison, summary or table is an output format, not a corpus.
For a plan assembled from named source items, choose where those source items live.
Use only a listed scope id. Use scopeId 0 only when no category plausibly fits or
the request explicitly spans the full corpus. Ignore superficial token overlap.
The LLM owns this semantic decision; the application only validates the id.
"@.Trim()
    $user = @"
USER_REQUEST:
$($Scenario.userRequest)

AVAILABLE_SCOPE_IDS:
- 0: categoryPath=null
$($scopeLines -join [Environment]::NewLine)
"@.Trim()
    return @(
        [ordered]@{ role = "system"; content = $system },
        [ordered]@{ role = "user"; content = $user }
    )
}

function Build-CompactMessages {
    param([object]$Scenario, [string[]]$CatalogPaths)

    $scopeLines = for ($index = 0; $index -lt $CatalogPaths.Count; $index++) {
        "- $($index + 1): categoryPath=$($CatalogPaths[$index])"
    }
    $system = @"
You choose semantic search terms. Do not answer the end user.
Choose one AVAILABLE_SCOPE or null for the requested TARGETS.
Choose the category that contains the concrete source values needed for the
TARGETS. A plan or table is an output format, not a corpus.
Call select_retrieval_scope exactly once.
"@.Trim()
    $user = @"
OUTPUT_LANGUAGE: fr
TARGETS: $($Scenario.targets -join ' | ')
TARGET_COUNT: $($Scenario.targets.Count)
ROW_COUNT: 5
AVAILABLE_SCOPE_IDS:
- 0: categoryPath=null
$($scopeLines -join [Environment]::NewLine)
"@.Trim()
    return @(
        [ordered]@{ role = "system"; content = $system },
        [ordered]@{ role = "user"; content = $user }
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
    $safeName = "category-selection"
}
$stdoutPath = Join-Path $resolvedOutputDirectory "$safeName-server.stdout.log"
$stderrPath = Join-Path $resolvedOutputDirectory "$safeName-server.stderr.log"
$artifactPath = Join-Path $resolvedOutputDirectory "$safeName-category-selection.json"
$baseUrl = "http://127.0.0.1:$Port"
$serverArguments = @(
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
    $serverArguments += "--swa-full"
}

$process = $null
$startedAt = Get-Date
try {
    $process = Start-Process `
        -FilePath $serverPath `
        -ArgumentList $serverArguments `
        -WorkingDirectory (Split-Path $serverPath) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru
    $models = Wait-LlamaReady -BaseUrl $baseUrl
    $modelId = [string]$models.data[0].id
    $props = Invoke-RestMethod -Method Get -Uri "$baseUrl/props" -TimeoutSec 10
    $catalogPaths = @(Get-RealCatalogPaths)
    $scenarios = @(Get-Scenarios)
    if ($ScenarioIds -and $ScenarioIds.Count -gt 0) {
        $requestedIds = @($ScenarioIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $scenarios = @($scenarios | Where-Object { $requestedIds -contains $_.id })
        $missingIds = @($requestedIds | Where-Object { $_ -notin @($scenarios.id) })
        if ($missingIds.Count -gt 0) {
            throw "Unknown scenario ids: $($missingIds -join ', ')"
        }
    }
    $tool = New-ScopeSelectionTool -MaximumScopeId $catalogPaths.Count
    $results = @()

    foreach ($scenario in $scenarios) {
        Write-Output "CATEGORY_SCENARIO_START id=$($scenario.id) mode=$($scenario.promptMode)"
        $messages = if ($scenario.promptMode -eq "compact") {
            Build-CompactMessages -Scenario $scenario -CatalogPaths $catalogPaths
        }
        else {
            Build-FullMessages -Scenario $scenario -CatalogPaths $catalogPaths
        }
        $payload = [ordered]@{
            model = $modelId
            messages = $messages
            tools = @($tool)
            tool_choice = "auto"
            temperature = $Temperature
            top_p = $TopP
            top_k = $TopK
            min_p = $MinP
            seed = $Seed
            max_tokens = 240
            stream = $false
        }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $response = Invoke-ChatCompletion -BaseUrl $baseUrl -Payload $payload
        $watch.Stop()
        $message = $response.choices[0].message
        $calls = @(Get-ToolCalls $message)
        $selectedCall = @(
            $calls |
                Where-Object { [string]$_.function.name -eq "select_retrieval_scope" }
        ) | Select-Object -First 1
        $arguments = if ($null -ne $selectedCall) {
            Convert-ToolArguments $selectedCall.function.arguments
        }
        else {
            $null
        }
        $selectedScopeId = if (
            $null -ne $arguments -and
            $null -ne $arguments.PSObject.Properties["scopeId"]
        ) {
            [int]$arguments.scopeId
        }
        else {
            $null
        }
        $reason = if (
            $null -ne $arguments -and
            $null -ne $arguments.PSObject.Properties["reason"]
        ) {
            [string]$arguments.reason
        }
        else {
            ""
        }
        $correct = $selectedScopeId -eq [int]$scenario.expectedScopeId
        $expectedIndex = [int]$scenario.expectedScopeId - 1
        if ($expectedIndex -lt 0 -or $expectedIndex -ge $catalogPaths.Count) {
            throw "Expected scope index is out of range for scenario $($scenario.id): index=$expectedIndex catalogCount=$($catalogPaths.Count)."
        }
        $expectedCategoryPath = [string]$catalogPaths[$expectedIndex]
        $selectedCategoryPath = if (
            $null -ne $selectedScopeId -and
            $selectedScopeId -ge 1 -and
            $selectedScopeId -le $catalogPaths.Count
        ) {
            [string]$catalogPaths[([int]$selectedScopeId - 1)]
        }
        elseif ($selectedScopeId -eq 0) {
            $null
        }
        else {
            "invalid"
        }
        $result = [pscustomobject]@{
            id = $scenario.id
            promptMode = $scenario.promptMode
            expectedScopeId = [int]$scenario.expectedScopeId
            expectedCategoryPath = $expectedCategoryPath
            selectedScopeId = $selectedScopeId
            selectedCategoryPath = $selectedCategoryPath
            correct = $correct
            reason = $reason
            toolCallCount = $calls.Count
            elapsedMs = [math]::Round($watch.Elapsed.TotalMilliseconds, 1)
            usage = $response.usage
            rawMessage = $message
        }
        $results += $result
        Write-Output (
            "CATEGORY_SCENARIO_DONE id={0} correct={1} expected={2} selected={3} ms={4}" -f
            $scenario.id,
            $correct,
            $scenario.expectedScopeId,
            $selectedScopeId,
            $result.elapsedMs)
    }

    $correctCount = @($results | Where-Object { $_.correct }).Count
    $fullResults = @($results | Where-Object { $_.promptMode -eq "full" })
    $compactResults = @($results | Where-Object { $_.promptMode -eq "compact" })
    $artifact = [ordered]@{
        schemaVersion = 1
        capturedAt = [DateTimeOffset]::UtcNow.ToString("O")
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
        catalogPaths = $catalogPaths
        summary = [ordered]@{
            correct = $correctCount
            total = $results.Count
            fullContextCorrect = @($fullResults | Where-Object { $_.correct }).Count
            fullContextTotal = $fullResults.Count
            compactContextCorrect = @($compactResults | Where-Object { $_.correct }).Count
            compactContextTotal = $compactResults.Count
            elapsedMs = [math]::Round(((Get-Date) - $startedAt).TotalMilliseconds, 1)
        }
        results = $results
    }
    $artifact | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $artifactPath -Encoding UTF8
    Write-Output (
        "CATEGORY_BENCHMARK_DONE profile={0} score={1}/{2} full={3}/{4} compact={5}/{6} artifact={7}" -f
        $ProfileName,
        $artifact.summary.correct,
        $artifact.summary.total,
        $artifact.summary.fullContextCorrect,
        $artifact.summary.fullContextTotal,
        $artifact.summary.compactContextCorrect,
        $artifact.summary.compactContextTotal,
        $artifactPath)
}
finally {
    Stop-LlamaServer -Process $process
}
