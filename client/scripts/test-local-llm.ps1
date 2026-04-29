param(
    [string]$BaseUrl = "http://127.0.0.1:1234",
    [string]$OutputPath = "",
    [int]$TimeoutSeconds = 120,
    [int]$SequentialRequests = 5,
    [int]$Concurrency = 4,
    [switch]$SkipConcurrency
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

function Resolve-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

function Get-TextPreview {
    param(
        [string]$Text,
        [int]$MaxChars = 420
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return ""
    }

    $flat = ($Text -replace "\s+", " ").Trim()
    if ($flat.Length -le $MaxChars) {
        return $flat
    }

    return $flat.Substring(0, $MaxChars) + "..."
}

function New-StepResult {
    param(
        [string]$Name,
        [string]$Status,
        [object]$Details = $null
    )

    return [ordered]@{
        name = $Name
        status = $Status
        details = $Details
    }
}

function Invoke-HttpJson {
    param(
        [ValidateSet("GET", "POST")]
        [string]$Method,
        [string]$Url,
        [object]$Body = $null,
        [int]$TimeoutSeconds = 120
    )

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    try {
        $started = Get-Date
        if ($Method -eq "GET") {
            $response = $client.GetAsync($Url).GetAwaiter().GetResult()
        }
        else {
            $json = ConvertTo-Json $Body -Depth 40 -Compress
            $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")
            $response = $client.PostAsync($Url, $content).GetAwaiter().GetResult()
        }

        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [ordered]@{
            ok = $response.IsSuccessStatusCode
            statusCode = [int]$response.StatusCode
            elapsedMs = [int]((Get-Date) - $started).TotalMilliseconds
            body = $text
        }
    }
    finally {
        $client.Dispose()
    }
}

function Get-ChatText {
    param([string]$Body)

    try {
        $parsed = $Body | ConvertFrom-Json
        if ($parsed.choices -and $parsed.choices.Count -gt 0) {
            $message = $parsed.choices[0].message
            if ($message -and $message.content) {
                return [string]$message.content
            }
        }
    }
    catch {
        return ""
    }

    return ""
}

function Invoke-ChatProbe {
    param(
        [string]$Name,
        [object[]]$Messages,
        [string[]]$MustContain = @(),
        [string[]]$MustNotContain = @(),
        [int]$MaxTokens = 160,
        [double]$Temperature = 0.0,
        [int]$TimeoutSeconds = 120
    )

    $url = $script:BaseUrl.TrimEnd("/") + "/v1/chat/completions"
    $body = [ordered]@{
        model = $script:ModelName
        stream = $false
        temperature = $Temperature
        max_tokens = $MaxTokens
        messages = $Messages
    }

    $response = Invoke-HttpJson -Method "POST" -Url $url -Body $body -TimeoutSeconds $TimeoutSeconds
    $text = Get-ChatText $response.body
    $checks = New-Object System.Collections.Generic.List[object]

    foreach ($needle in $MustContain) {
        $passed = ($text.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
        $checks.Add([ordered]@{
            type = "must_contain"
            value = $needle
            passed = $passed
        })
    }

    foreach ($needle in $MustNotContain) {
        $passed = ($text.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0)
        $checks.Add([ordered]@{
            type = "must_not_contain"
            value = $needle
            passed = $passed
        })
    }

    $failedChecks = @($checks | Where-Object { -not $_.passed })
    $status = if ($response.ok -and -not [string]::IsNullOrWhiteSpace($text) -and $failedChecks.Count -eq 0) { "passed" } else { "failed" }

    return New-StepResult $Name $status ([ordered]@{
        httpOk = $response.ok
        statusCode = $response.statusCode
        elapsedMs = $response.elapsedMs
        responsePreview = Get-TextPreview $text
        checks = $checks
    })
}

function New-UserMessage {
    param([string]$Content)
    return [ordered]@{
        role = "user"
        content = $Content
    }
}

function New-SystemMessage {
    param([string]$Content)
    return [ordered]@{
        role = "system"
        content = $Content
    }
}

$repoRoot = Resolve-RepoRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $OutputPath = Join-Path $repoRoot "artifacts\local-llm\local-llm-smoke-$stamp.json"
}

$script:BaseUrl = $BaseUrl.TrimEnd("/")
$script:ModelName = "local"
$results = New-Object System.Collections.Generic.List[object]

$health = Invoke-HttpJson -Method "GET" -Url ($script:BaseUrl + "/health") -TimeoutSeconds 10
$results.Add((New-StepResult "health" ($(if ($health.ok) { "passed" } else { "failed" })) ([ordered]@{
    statusCode = $health.statusCode
    elapsedMs = $health.elapsedMs
    bodyPreview = Get-TextPreview $health.body
})))

$models = Invoke-HttpJson -Method "GET" -Url ($script:BaseUrl + "/v1/models") -TimeoutSeconds 10
$modelId = ""
try {
    $modelsJson = $models.body | ConvertFrom-Json
    if ($modelsJson.data -and $modelsJson.data.Count -gt 0 -and $modelsJson.data[0].id) {
        $modelId = [string]$modelsJson.data[0].id
        $script:ModelName = $modelId
    }
}
catch {
    $modelId = ""
}

$results.Add((New-StepResult "models" ($(if ($models.ok -and -not [string]::IsNullOrWhiteSpace($modelId)) { "passed" } else { "failed" })) ([ordered]@{
    statusCode = $models.statusCode
    elapsedMs = $models.elapsedMs
    model = $modelId
    bodyPreview = Get-TextPreview $models.body
})))

$props = Invoke-HttpJson -Method "GET" -Url ($script:BaseUrl + "/props") -TimeoutSeconds 10
$ctxSize = 0
$slotCount = 0
$modelAlias = ""
try {
    $propsJson = $props.body | ConvertFrom-Json
    if ($propsJson.default_generation_settings -and $propsJson.default_generation_settings.n_ctx) {
        $ctxSize = [int]$propsJson.default_generation_settings.n_ctx
    }
    if ($propsJson.total_slots) {
        $slotCount = [int]$propsJson.total_slots
    }
    if ($propsJson.model_alias) {
        $modelAlias = [string]$propsJson.model_alias
    }
}
catch {
    $ctxSize = 0
}

$results.Add((New-StepResult "props" ($(if ($props.ok -and $ctxSize -ge 4096 -and $slotCount -ge 1) { "passed" } else { "failed" })) ([ordered]@{
    statusCode = $props.statusCode
    elapsedMs = $props.elapsedMs
    modelAlias = $modelAlias
    nCtx = $ctxSize
    totalSlots = $slotCount
})))

$system = "Tu es SAAIA. Reponds en francais, de facon concise. Instruction prioritaire: tu n'as pas le droit d'utiliser tes connaissances generales quand un bloc Contexte est fourni. Tu dois utiliser uniquement le bloc Contexte. Si le bloc Contexte est vide, indique Aucun, ou ne contient pas la reponse, reponds exactement CONTEXTE_ABSENT et rien d'autre."

$results.Add((Invoke-ChatProbe -Name "basic_french_identity" -Messages @(
    (New-SystemMessage $system),
    (New-UserMessage "Dis bonjour en une phrase et indique que tu es SAAIA.")
) -MustContain @("SAAIA") -MaxTokens 80 -TimeoutSeconds $TimeoutSeconds))

$inertingContext = @"
Contexte:
Document: CEN/TR 15281:2006 Guidance on inerting for the prevention of explosion.
Excerpt: This Technical Report gives guidance on inerting systems and inert gas blanketing as measures for preventing explosions in industrial equipment.
"@

$results.Add((Invoke-ChatProbe -Name "grounded_inertage_multilingual" -Messages @(
    (New-SystemMessage $system),
    (New-UserMessage "$inertingContext`n`nQuestion: Est-ce que le document parle d'inertage ? Reponds oui/non et cite le document.")
) -MustContain @("oui", "CEN") -MaxTokens 140 -TimeoutSeconds $TimeoutSeconds))

$results.Add((Invoke-ChatProbe -Name "no_context_refusal" -Messages @(
    (New-SystemMessage $system),
    (New-UserMessage "Contexte: Aucun.`n`nQuestion: Quelle sauce irait bien avec une entrecote ?")
) -MustContain @("CONTEXTE_ABSENT") -MaxTokens 80 -TimeoutSeconds $TimeoutSeconds))

$longContextChunk = "Le document de qualification runtime indique que la reponse attendue pour ce test est BUDGET_OK. "
$longContext = ($longContextChunk * 140)
$results.Add((Invoke-ChatProbe -Name "long_context_under_budget" -Messages @(
    (New-SystemMessage $system),
    (New-UserMessage "Contexte: $longContext`n`nQuestion: Quelle est la reponse attendue pour ce test ? Reponds avec le marqueur exact.")
) -MustContain @("BUDGET_OK") -MaxTokens 60 -TimeoutSeconds $TimeoutSeconds))

$sequentialDetails = New-Object System.Collections.Generic.List[object]
for ($i = 1; $i -le $SequentialRequests; $i++) {
    $step = Invoke-ChatProbe -Name "sequential_$i" -Messages @(
        (New-SystemMessage "Reponds de facon tres courte."),
        (New-UserMessage "Reponds avec OK et le nombre $i.")
    ) -MustContain @("OK") -MaxTokens 24 -TimeoutSeconds $TimeoutSeconds
    $sequentialDetails.Add($step.details)
}

$sequentialFailures = @($sequentialDetails | Where-Object { -not $_.httpOk -or [string]::IsNullOrWhiteSpace($_.responsePreview) })
$results.Add((New-StepResult "sequential_stability" ($(if ($sequentialFailures.Count -eq 0) { "passed" } else { "failed" })) ([ordered]@{
    requested = $SequentialRequests
    failures = $sequentialFailures.Count
    results = $sequentialDetails
})))

if (-not $SkipConcurrency) {
    $actualConcurrency = [Math]::Max(1, $Concurrency)
    if ($slotCount -gt 0) {
        $actualConcurrency = [Math]::Min($actualConcurrency, $slotCount)
    }

    $jobs = @()
    for ($i = 1; $i -le $actualConcurrency; $i++) {
        $jobs += Start-Job -ScriptBlock {
            param($baseUrl, $modelName, $timeoutSeconds, $index)
            Add-Type -AssemblyName System.Net.Http
            $client = [System.Net.Http.HttpClient]::new()
            $client.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)
            try {
                $body = [ordered]@{
                    model = $modelName
                    stream = $false
                    temperature = 0
                    max_tokens = 32
                    messages = @(
                        [ordered]@{ role = "system"; content = "Reponds en francais, tres brievement." },
                        [ordered]@{ role = "user"; content = "Reponds simplement: test parallele $index OK." }
                    )
                } | ConvertTo-Json -Depth 20 -Compress
                $content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, "application/json")
                $started = Get-Date
                $response = $client.PostAsync($baseUrl.TrimEnd("/") + "/v1/chat/completions", $content).GetAwaiter().GetResult()
                $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                [ordered]@{
                    index = $index
                    ok = $response.IsSuccessStatusCode
                    statusCode = [int]$response.StatusCode
                    elapsedMs = [int]((Get-Date) - $started).TotalMilliseconds
                    bodyPreview = if ($text.Length -gt 240) { $text.Substring(0, 240) + "..." } else { $text }
                }
            }
            finally {
                $client.Dispose()
            }
        } -ArgumentList $script:BaseUrl, $script:ModelName, $TimeoutSeconds, $i
    }

    $concurrent = @($jobs | Receive-Job -Wait -AutoRemoveJob)
    $concurrentFailures = @($concurrent | Where-Object { -not $_.ok })
    $results.Add((New-StepResult "slot_concurrency" ($(if ($concurrentFailures.Count -eq 0) { "passed" } else { "failed" })) ([ordered]@{
        requested = $Concurrency
        actual = $actualConcurrency
        slots = $slotCount
        failures = $concurrentFailures.Count
        results = $concurrent
    })))
}
else {
    $results.Add((New-StepResult "slot_concurrency" "skipped" ([ordered]@{
        reason = "SkipConcurrency was set"
    })))
}

$failed = @($results | Where-Object { $_.status -eq "failed" })
$overall = if ($failed.Count -eq 0) { "passed" } else { "failed" }

$report = [ordered]@{
    artifact = "local_llm_smoke.json"
    generatedAt = (Get-Date).ToString("o")
    cdcAlignment = "v3.1"
    scope = "client local LLM only; server LLM is intentionally out of scope for user chat"
    baseUrl = $script:BaseUrl
    model = $script:ModelName
    summary = [ordered]@{
        status = $overall
        stepCount = $results.Count
        failedCount = $failed.Count
    }
    steps = $results
}

$outDir = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

$report | ConvertTo-Json -Depth 60 | Set-Content -Path $OutputPath -Encoding UTF8

Write-Host "local-llm-smoke: $overall"
Write-Host "base-url: $script:BaseUrl"
Write-Host "model: $script:ModelName"
Write-Host "report: $OutputPath"
foreach ($step in $results) {
    $color = if ($step.status -eq "passed") { "Green" } elseif ($step.status -eq "skipped") { "Yellow" } else { "Red" }
    Write-Host ("[{0}] {1}" -f $step.status.ToUpperInvariant(), $step.name) -ForegroundColor $color
}

if ($overall -ne "passed") {
    exit 1
}
