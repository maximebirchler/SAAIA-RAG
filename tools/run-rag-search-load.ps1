param(
    [string]$BackendBaseUrl = $env:SAAIA_VALIDATION_BACKEND_URL,
    [string]$ApiKey = $env:SAAIA_API_KEY,
    [string]$QuestionBankPath = "",
    [string[]]$Queries = @(),
    [string[]]$Concurrency = @("1", "4", "8", "16", "32"),
    [int]$RequestsPerLevel = 32,
    [int]$TopK = 8,
    [string]$Category = $env:SAAIA_VALIDATION_CATEGORY,
    [ValidateSet("diagnostic", "runtime", "lean", "focused")]
    [string]$Mode = "diagnostic",
    [int]$TimeoutSeconds = 90,
    [string]$OutputDir = "",
    [switch]$FailOnTimeout
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    $dir = Split-Path -Parent $PSScriptRoot
    return (Resolve-Path $dir).Path
}

function Resolve-QuestionBankPath {
    param([string]$Path)

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return (Resolve-Path $Path).Path
    }

    $repoRoot = Resolve-RepoRoot
    $defaultPath = Join-Path $repoRoot "backend\SAAIA.Backend.Tests\Fixtures\retrieval_cuisine_validation.v1.json"
    if (Test-Path $defaultPath) {
        return (Resolve-Path $defaultPath).Path
    }

    return ""
}

function Get-LoadQueries {
    param(
        [string[]]$ExplicitQueries,
        [string]$QuestionBank
    )

    $items = New-Object System.Collections.Generic.List[string]
    foreach ($query in $ExplicitQueries) {
        if (-not [string]::IsNullOrWhiteSpace($query)) {
            $items.Add($query.Trim())
        }
    }

    if ($items.Count -gt 0) {
        return $items.ToArray()
    }

    $resolvedBank = Resolve-QuestionBankPath $QuestionBank
    if ([string]::IsNullOrWhiteSpace($resolvedBank)) {
        throw "Provide -Queries or -QuestionBankPath."
    }

    $json = Get-Content -Raw -Path $resolvedBank | ConvertFrom-Json
    foreach ($case in $json.validationCases) {
        if ($null -ne $case.question -and -not [string]::IsNullOrWhiteSpace([string]$case.question)) {
            $items.Add(([string]$case.question).Trim())
        }
    }

    if ($items.Count -eq 0) {
        throw "No usable questions found in '$resolvedBank'."
    }

    return $items.ToArray()
}

function Normalize-IntList {
    param(
        [string[]]$Values,
        [string]$Name
    )

    $items = New-Object System.Collections.Generic.List[int]
    foreach ($value in $Values) {
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        foreach ($part in ([string]$value -split "[,;]")) {
            if ([string]::IsNullOrWhiteSpace($part)) {
                continue
            }

            $parsed = 0
            if (-not [int]::TryParse($part.Trim(), [ref]$parsed)) {
                throw "Invalid $Name value '$part'."
            }

            if ($parsed -gt 0) {
                $items.Add($parsed)
            }
        }
    }

    if ($items.Count -eq 0) {
        throw "$Name must contain at least one positive integer."
    }

    return $items.ToArray()
}

function Get-Percentile {
    param(
        [long[]]$Values,
        [double]$Percentile
    )

    if ($Values.Count -eq 0) {
        return 0
    }

    $sorted = @($Values | Sort-Object)
    $index = [Math]::Ceiling($Percentile * $sorted.Count) - 1
    $index = [Math]::Max(0, [Math]::Min($sorted.Count - 1, $index))
    return [long]$sorted[$index]
}

function Invoke-LoadLevel {
    param(
        [string]$Url,
        [string]$ApiKeyValue,
        [string[]]$QuerySet,
        [int]$Level,
        [int]$RequestCount,
        [int]$TopKValue,
        [string]$CategoryValue,
        [string]$ModeValue,
        [int]$TimeoutSecondsValue
    )

    $worker = {
        param(
            [string]$Url,
            [string]$ApiKeyValue,
            [string]$Query,
            [int]$Index,
            [int]$TopKValue,
            [string]$CategoryValue,
            [string]$ModeValue,
            [int]$TimeoutSecondsValue
        )

        Add-Type -AssemblyName System.Net.Http
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $result = [ordered]@{
            index = $Index
            query = $Query
            ok = $false
            statusCode = 0
            durationMs = 0
            sourceCount = 0
            error = $null
            errorKind = $null
            retryAfter = $null
            active = $null
            queued = $null
            maxConcurrency = $null
            queueWaitMs = $null
            waitedQueued = $null
        }

        function Get-HeaderValue {
            param(
                [System.Net.Http.HttpResponseMessage]$Response,
                [string]$Name
            )

            $values = $null
            if ($Response.Headers.TryGetValues($Name, [ref]$values)) {
                return ($values | Select-Object -First 1)
            }

            if ($Response.Content.Headers.TryGetValues($Name, [ref]$values)) {
                return ($values | Select-Object -First 1)
            }

            return $null
        }

        $client = [System.Net.Http.HttpClient]::new()
        try {
            $client.Timeout = [TimeSpan]::FromSeconds([Math]::Max(1, $TimeoutSecondsValue))
            if (-not [string]::IsNullOrWhiteSpace($ApiKeyValue)) {
                $client.DefaultRequestHeaders.Add("X-Api-Key", $ApiKeyValue)
            }

            $body = [ordered]@{
                query = $Query
                topK = $TopKValue
                includeContextualSnippet = $true
                mode = $ModeValue
            }

            if (-not [string]::IsNullOrWhiteSpace($CategoryValue)) {
                $body.category = $CategoryValue
            }

            $jsonBody = $body | ConvertTo-Json -Depth 8 -Compress
            $content = [System.Net.Http.StringContent]::new(
                $jsonBody,
                [System.Text.Encoding]::UTF8,
                "application/json")

            $response = $client.PostAsync($Url, $content).GetAwaiter().GetResult()
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $sw.Stop()

            $result.statusCode = [int]$response.StatusCode
            $result.ok = $response.IsSuccessStatusCode
            $result.durationMs = [long]$sw.ElapsedMilliseconds
            $result.retryAfter = Get-HeaderValue $response "Retry-After"
            $result.active = Get-HeaderValue $response "X-SAAIA-RAG-Active"
            $result.queued = Get-HeaderValue $response "X-SAAIA-RAG-Queued"
            $result.maxConcurrency = Get-HeaderValue $response "X-SAAIA-RAG-Max-Concurrency"
            $result.queueWaitMs = Get-HeaderValue $response "X-SAAIA-RAG-Queue-Wait-Ms"
            $result.waitedQueued = Get-HeaderValue $response "X-SAAIA-RAG-Waited-Queued"

            if ($response.IsSuccessStatusCode -and -not [string]::IsNullOrWhiteSpace($text)) {
                try {
                    $parsed = $text | ConvertFrom-Json
                    if ($null -ne $parsed.sources) {
                        $result.sourceCount = @($parsed.sources).Count
                    }
                    elseif ($null -ne $parsed.items) {
                        $result.sourceCount = @($parsed.items).Count
                    }
                }
                catch {
                    $result.error = "invalid_json_response"
                }
            }
            elseif (-not $response.IsSuccessStatusCode -and -not [string]::IsNullOrWhiteSpace($text)) {
                $result.error = $text.Substring(0, [Math]::Min(500, $text.Length))
                try {
                    $parsedError = $text | ConvertFrom-Json
                    if ($null -ne $parsedError.error -and -not [string]::IsNullOrWhiteSpace([string]$parsedError.error)) {
                        $result.errorKind = [string]$parsedError.error
                    }
                }
                catch {
                    $result.errorKind = "non_json_error"
                }
            }
        }
        catch [System.Threading.Tasks.TaskCanceledException] {
            $sw.Stop()
            $result.durationMs = [long]$sw.ElapsedMilliseconds
            $result.error = "timeout"
            $result.errorKind = "timeout"
        }
        catch {
            $sw.Stop()
            $result.durationMs = [long]$sw.ElapsedMilliseconds
            $result.error = $_.Exception.Message
            $result.errorKind = "exception"
        }
        finally {
            $client.Dispose()
        }

        [pscustomobject]$result
    }

    $pool = [runspacefactory]::CreateRunspacePool(1, [Math]::Max(1, $Level))
    $pool.Open()
    $jobs = New-Object System.Collections.Generic.List[object]
    $startedAt = [DateTimeOffset]::UtcNow
    $levelSw = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        for ($i = 0; $i -lt $RequestCount; $i++) {
            $query = $QuerySet[$i % $QuerySet.Count]
            $ps = [powershell]::Create()
            $ps.RunspacePool = $pool
            $null = $ps.AddScript($worker).
                AddArgument($Url).
                AddArgument($ApiKeyValue).
                AddArgument($query).
                AddArgument($i).
                AddArgument($TopKValue).
                AddArgument($CategoryValue).
                AddArgument($ModeValue).
                AddArgument($TimeoutSecondsValue)

            $jobs.Add([pscustomobject]@{
                PowerShell = $ps
                Handle = $ps.BeginInvoke()
            })
        }

        $results = New-Object System.Collections.Generic.List[object]
        foreach ($job in $jobs) {
            $output = $job.PowerShell.EndInvoke($job.Handle)
            foreach ($item in $output) {
                $results.Add($item)
            }
        }
    }
    finally {
        foreach ($job in $jobs) {
            $job.PowerShell.Dispose()
        }
        $pool.Close()
        $pool.Dispose()
    }

    $levelSw.Stop()
    $allDurations = @($results | ForEach-Object { [long]$_.durationMs })
    $successDurations = @($results | Where-Object { $_.statusCode -eq 200 } | ForEach-Object { [long]$_.durationMs })
    $queueWaitDurations = New-Object System.Collections.Generic.List[long]
    foreach ($result in $results) {
        $queueWait = 0L
        if ([long]::TryParse([string]$result.queueWaitMs, [ref]$queueWait)) {
            $queueWaitDurations.Add($queueWait)
        }
    }
    $statusCounts = [ordered]@{}
    foreach ($group in ($results | Group-Object statusCode | Sort-Object Name)) {
        $statusCounts[[string]$group.Name] = $group.Count
    }

    $timeoutCount = @($results | Where-Object { $_.error -eq "timeout" }).Count
    $busyCount = @($results | Where-Object { $_.statusCode -eq 429 }).Count
    $ragSearchBusyCount = @($results | Where-Object { $_.statusCode -eq 429 -and $_.errorKind -eq "rag_search_busy" }).Count
    $rateLimitedCount = @($results | Where-Object { $_.statusCode -eq 429 -and $_.errorKind -eq "rate_limited" }).Count
    $other429Count = $busyCount - $ragSearchBusyCount - $rateLimitedCount
    $waitedQueuedCount = @($results | Where-Object { [string]$_.waitedQueued -eq "true" }).Count
    $unexpectedCount = @($results | Where-Object { $_.statusCode -ne 200 -and $_.statusCode -ne 429 }).Count

    [pscustomobject][ordered]@{
        concurrency = $Level
        requested = $RequestCount
        startedAtUtc = $startedAt.ToString("O")
        elapsedMs = [long]$levelSw.ElapsedMilliseconds
        ok = @($results | Where-Object { $_.statusCode -eq 200 }).Count
        busy = $busyCount
        ragSearchBusy = $ragSearchBusyCount
        rateLimited = $rateLimitedCount
        other429 = $other429Count
        waitedQueued = $waitedQueuedCount
        timeouts = $timeoutCount
        unexpected = $unexpectedCount
        statusCounts = $statusCounts
        latencyMs = [ordered]@{
            avgAll = if ($allDurations.Count -gt 0) { [Math]::Round(($allDurations | Measure-Object -Average).Average, 1) } else { 0 }
            p50All = Get-Percentile $allDurations 0.50
            p95All = Get-Percentile $allDurations 0.95
            p99All = Get-Percentile $allDurations 0.99
            avgOk = if ($successDurations.Count -gt 0) { [Math]::Round(($successDurations | Measure-Object -Average).Average, 1) } else { 0 }
            p50Ok = Get-Percentile $successDurations 0.50
            p95Ok = Get-Percentile $successDurations 0.95
            p99Ok = Get-Percentile $successDurations 0.99
        }
        queueWaitMs = [ordered]@{
            avg = if ($queueWaitDurations.Count -gt 0) { [Math]::Round(($queueWaitDurations | Measure-Object -Average).Average, 1) } else { 0 }
            p50 = Get-Percentile $queueWaitDurations.ToArray() 0.50
            p95 = Get-Percentile $queueWaitDurations.ToArray() 0.95
            p99 = Get-Percentile $queueWaitDurations.ToArray() 0.99
        }
        results = @($results | Sort-Object index)
    }
}

if ([string]::IsNullOrWhiteSpace($BackendBaseUrl)) {
    throw "BackendBaseUrl is required. Set -BackendBaseUrl or SAAIA_VALIDATION_BACKEND_URL."
}

$repoRoot = Resolve-RepoRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\load-tests"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$querySet = Get-LoadQueries -ExplicitQueries $Queries -QuestionBank $QuestionBankPath
$concurrencyLevels = Normalize-IntList -Values $Concurrency -Name "Concurrency"
$url = $BackendBaseUrl.TrimEnd("/") + "/rag/search"
$levels = New-Object System.Collections.Generic.List[object]
$generatedAt = [DateTimeOffset]::UtcNow

Write-Host "RAG search load test"
Write-Host "Backend: $BackendBaseUrl"
Write-Host "Queries: $($querySet.Count)"
Write-Host "Concurrency: $($concurrencyLevels -join ', ')"

foreach ($level in $concurrencyLevels) {
    if ($level -le 0) {
        continue
    }

    Write-Host ""
    Write-Host "Running concurrency=$level requests=$RequestsPerLevel ..."
    $levelResult = Invoke-LoadLevel `
        -Url $url `
        -ApiKeyValue $ApiKey `
        -QuerySet $querySet `
        -Level $level `
        -RequestCount $RequestsPerLevel `
        -TopKValue $TopK `
        -CategoryValue $Category `
        -ModeValue $Mode `
        -TimeoutSecondsValue $TimeoutSeconds

    $levels.Add($levelResult)
    Write-Host ("  200={0} 429={1} rag_busy={2} rate_limited={3} waited={4} timeout={5} unexpected={6} p95_ok={7}ms p99_ok={8}ms" -f `
        $levelResult.ok,
        $levelResult.busy,
        $levelResult.ragSearchBusy,
        $levelResult.rateLimited,
        $levelResult.waitedQueued,
        $levelResult.timeouts,
        $levelResult.unexpected,
        $levelResult.latencyMs.p95Ok,
        $levelResult.latencyMs.p99Ok)
}

$report = [ordered]@{
    generatedAtUtc = $generatedAt.ToString("O")
    backendBaseUrl = $BackendBaseUrl
    category = $Category
    topK = $TopK
    mode = $Mode
    timeoutSeconds = $TimeoutSeconds
    requestsPerLevel = $RequestsPerLevel
    queryCount = $querySet.Count
    levels = $levels
}

$stamp = $generatedAt.ToString("yyyyMMdd_HHmmss")
$outputPath = Join-Path $OutputDir "rag-search-load-$stamp.json"
$report | ConvertTo-Json -Depth 32 | Set-Content -Path $outputPath -Encoding UTF8

Write-Host ""
Write-Host "Wrote $outputPath"

if ($FailOnTimeout) {
    $totalTimeouts = 0
    $totalUnexpected = 0
    foreach ($level in $levels) {
        $totalTimeouts += [int]$level.timeouts
        $totalUnexpected += [int]$level.unexpected
    }

    if ($totalTimeouts -gt 0 -or $totalUnexpected -gt 0) {
        exit 2
    }
}
