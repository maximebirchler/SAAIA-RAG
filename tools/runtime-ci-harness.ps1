param(
    [string]$RuntimeBaseUrl = $env:SAAIA_RUNTIME_CI_BASE_URL,
    [string]$OutputPath = "",
    [int]$Concurrency = 4,
    [int]$Iterations = 3,
    [int]$TimeoutSeconds = 30,
    [double]$TtftTolerancePercent = 25,
    [double]$TokPerSecTolerancePercent = 25,
    [switch]$SkipDotnet,
    [switch]$StrictRuntime
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    $dir = Split-Path -Parent $PSScriptRoot
    return (Resolve-Path $dir).Path
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

function Invoke-CommandStep {
    param(
        [string]$Name,
        [string]$FileName,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FileName
    $psi.Arguments = ($Arguments -join " ")
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $started = Get-Date
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    $elapsedMs = [int]((Get-Date) - $started).TotalMilliseconds

    return New-StepResult $Name ($(if ($proc.ExitCode -eq 0) { "passed" } else { "failed" })) ([ordered]@{
        command = "$FileName $($psi.Arguments)"
        exitCode = $proc.ExitCode
        elapsedMs = $elapsedMs
        stdoutTail = Get-TextTail $stdout 120
        stderrTail = Get-TextTail $stderr 120
    })
}

function Get-TextTail {
    param(
        [string]$Text,
        [int]$MaxLines
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return ""
    }

    $lines = $Text -split "`r?`n"
    if ($lines.Length -le $MaxLines) {
        return ($lines -join "`n")
    }

    return (($lines | Select-Object -Last $MaxLines) -join "`n")
}

function Invoke-HttpJson {
    param(
        [string]$Method,
        [string]$Url,
        [object]$Body = $null,
        [int]$TimeoutSeconds = 30
    )

    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    try {
        if ($Body -eq $null) {
            $response = $client.GetAsync($Url).GetAwaiter().GetResult()
        }
        else {
            $json = ConvertTo-Json $Body -Depth 20
            $content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, "application/json")
            $response = $client.PostAsync($Url, $content).GetAwaiter().GetResult()
        }

        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [ordered]@{
            ok = $response.IsSuccessStatusCode
            statusCode = [int]$response.StatusCode
            body = $text
        }
    }
    finally {
        $client.Dispose()
    }
}

function Invoke-RuntimeCompletionProbe {
    param(
        [string]$BaseUrl,
        [int]$TimeoutSeconds
    )

    $url = $BaseUrl.TrimEnd("/") + "/v1/chat/completions"
    $body = [ordered]@{
        model = "local"
        stream = $false
        max_tokens = 24
        temperature = 0
        messages = @(
            [ordered]@{
                role = "user"
                content = "Reply with exactly: SAAIA_RUNTIME_CI_OK"
            }
        )
    }

    $start = Get-Date
    $response = Invoke-HttpJson "POST" $url $body $TimeoutSeconds
    $elapsedMs = [int]((Get-Date) - $start).TotalMilliseconds
    $tokPerSec = $null

    try {
        $parsed = $response.body | ConvertFrom-Json
        if ($parsed.timings -and $parsed.timings.predicted_per_second) {
            $tokPerSec = [double]$parsed.timings.predicted_per_second
        }
    }
    catch {
        $tokPerSec = $null
    }

    return [ordered]@{
        ok = $response.ok
        statusCode = $response.statusCode
        elapsedMs = $elapsedMs
        ttftMs = $elapsedMs
        tokPerSec = $tokPerSec
        bodyTail = Get-TextTail $response.body 20
    }
}

function Test-Reproducibility {
    param(
        [object[]]$Runs,
        [double]$TtftTolerancePercent,
        [double]$TokPerSecTolerancePercent
    )

    if ($Runs.Count -lt 2) {
        return [ordered]@{
            ok = $true
            reason = "not_enough_samples"
        }
    }

    $ttft = @($Runs | ForEach-Object { [double]$_.ttftMs })
    $avgTtft = ($ttft | Measure-Object -Average).Average
    $maxTtft = ($ttft | Measure-Object -Maximum).Maximum
    $ttftDrift = if ($avgTtft -gt 0) { (($maxTtft - $avgTtft) * 100.0 / $avgTtft) } else { 0 }

    $tok = @($Runs | Where-Object { $_.tokPerSec -ne $null } | ForEach-Object { [double]$_.tokPerSec })
    $tokDrift = 0
    if ($tok.Count -ge 2) {
        $avgTok = ($tok | Measure-Object -Average).Average
        $minTok = ($tok | Measure-Object -Minimum).Minimum
        $tokDrift = if ($avgTok -gt 0) { (($avgTok - $minTok) * 100.0 / $avgTok) } else { 0 }
    }

    return [ordered]@{
        ok = ($ttftDrift -le $TtftTolerancePercent -and $tokDrift -le $TokPerSecTolerancePercent)
        ttftDriftPercent = [Math]::Round($ttftDrift, 2)
        tokPerSecDriftPercent = [Math]::Round($tokDrift, 2)
        ttftTolerancePercent = $TtftTolerancePercent
        tokPerSecTolerancePercent = $TokPerSecTolerancePercent
    }
}

$repoRoot = Resolve-RepoRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $OutputPath = Join-Path $repoRoot "artifacts\runtime-ci\runtime-ci-$stamp.json"
}

$results = New-Object System.Collections.Generic.List[object]

if (-not $SkipDotnet) {
    $results.Add((Invoke-CommandStep "backend_tests" "dotnet" @("test", "backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj", "--no-restore") $repoRoot))
    $results.Add((Invoke-CommandStep "client_tests" "dotnet" @("test", "client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj", "--no-restore") $repoRoot))
}

$runtimeRuns = @()
if ([string]::IsNullOrWhiteSpace($RuntimeBaseUrl)) {
    $results.Add((New-StepResult "runtime_probes" "skipped" ([ordered]@{
        reason = "SAAIA_RUNTIME_CI_BASE_URL not set"
        strictRuntime = [bool]$StrictRuntime
    })))
}
else {
    $warmup = Invoke-RuntimeCompletionProbe $RuntimeBaseUrl $TimeoutSeconds
    $runtimeRuns += $warmup
    $results.Add((New-StepResult "runtime_warmup_probe" ($(if ($warmup.ok) { "passed" } else { "failed" })) $warmup))

    $jobs = @()
    for ($i = 0; $i -lt $Concurrency; $i++) {
        $jobs += Start-Job -ScriptBlock {
            param($baseUrl, $timeoutSeconds)
            $url = $baseUrl.TrimEnd("/") + "/v1/chat/completions"
            $body = @{
                model = "local"
                stream = $false
                max_tokens = 12
                temperature = 0
                messages = @(@{ role = "user"; content = "Return CI_OK" })
            } | ConvertTo-Json -Depth 10
            $client = New-Object System.Net.Http.HttpClient
            $client.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)
            try {
                $start = Get-Date
                $content = New-Object System.Net.Http.StringContent($body, [System.Text.Encoding]::UTF8, "application/json")
                $response = $client.PostAsync($url, $content).GetAwaiter().GetResult()
                [ordered]@{
                    ok = $response.IsSuccessStatusCode
                    statusCode = [int]$response.StatusCode
                    elapsedMs = [int]((Get-Date) - $start).TotalMilliseconds
                }
            }
            finally {
                $client.Dispose()
            }
        } -ArgumentList $RuntimeBaseUrl, $TimeoutSeconds
    }

    $concurrent = @($jobs | Receive-Job -Wait -AutoRemoveJob)
    $results.Add((New-StepResult "runtime_concurrency" ($(if (($concurrent | Where-Object { -not $_.ok }).Count -eq 0) { "passed" } else { "failed" })) ([ordered]@{
        concurrency = $Concurrency
        results = $concurrent
    })))

    for ($i = 0; $i -lt $Iterations; $i++) {
        $runtimeRuns += (Invoke-RuntimeCompletionProbe $RuntimeBaseUrl $TimeoutSeconds)
    }

    $repro = Test-Reproducibility $runtimeRuns $TtftTolerancePercent $TokPerSecTolerancePercent
    $results.Add((New-StepResult "runtime_reproducibility" ($(if ($repro.ok) { "passed" } else { "failed" })) $repro))

    $models = Invoke-HttpJson "GET" ($RuntimeBaseUrl.TrimEnd("/") + "/v1/models") $null $TimeoutSeconds
    $results.Add((New-StepResult "runtime_recovery_probe" ($(if ($models.ok) { "passed" } else { "failed" })) $models))
}

$failed = @($results | Where-Object { $_.status -eq "failed" })
$skippedRuntime = @($results | Where-Object { $_.name -eq "runtime_probes" -and $_.status -eq "skipped" })
$overall = "passed"
if ($failed.Count -gt 0 -or ($StrictRuntime -and $skippedRuntime.Count -gt 0)) {
    $overall = "failed"
}

$payload = [ordered]@{
    artifact = "runtime_ci_harness.json"
    cdcAlignment = "v3.1"
    generatedAt = (Get-Date).ToString("o")
    purpose = "CI regression harness distinct from runtime qualification warmup gate"
    runtimeBaseUrl = $RuntimeBaseUrl
    strictRuntime = [bool]$StrictRuntime
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

$payload | ConvertTo-Json -Depth 40 | Set-Content -Path $OutputPath -Encoding UTF8
Write-Host "runtime-ci-harness: $overall"
Write-Host "report: $OutputPath"

if ($overall -ne "passed") {
    exit 1
}
