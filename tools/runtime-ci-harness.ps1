param (
    [string]$RuntimeBaseUrl = $env:SAAIA_RUNTIME_CI_BASE_URL,
    [string]$OutputPath = "",
    [string]$Model = "local",
    [string]$CorpusManifestPath = "",
    [ValidateRange(1, 32)][int]$Concurrency = 4,
    [ValidateRange(0, 100)][int]$Iterations = 3,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 30,
    [double]$TtftTolerancePercent = 25,
    [double]$TokPerSecTolerancePercent = 25,
    [switch]$SkipDotnet,
    [switch]$StrictRuntime
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot 'testing/RuntimeProbes.psm1') -Force
$repoRoot = Split-Path -Parent $PSScriptRoot
$runId = [Guid]::NewGuid().ToString('N')
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "artifacts/runtime-ci/$runId/manifest.json"
}

$results = [Collections.Generic.List[object]]::new()
if (-not $SkipDotnet) {
    $testManifest = Join-Path (Split-Path -Parent $OutputPath) 'tests/manifest.json'
    & (Join-Path $PSScriptRoot 'test-project.ps1') -Profile Unit -OutputPath $testManifest
    $results.Add([ordered]@{
            name = 'project_tests'
            status = $(if ($LASTEXITCODE -eq 0) {
                    'passed'
                } else {
                    'failed'
                })
            manifest = $testManifest
        })
}

$runtimeRuns = @()
if ([string]::IsNullOrWhiteSpace($RuntimeBaseUrl)) {
    $results.Add([ordered]@{
            name = 'runtime_probes'
            status = 'skipped'
            reason = 'SAAIA_RUNTIME_CI_BASE_URL not set'
        })
} else {
    $uri = [Uri]$RuntimeBaseUrl
    if (-not $uri.IsAbsoluteUri -or -not $uri.IsLoopback -or $uri.Scheme -notin @('http', 'https')) {
        throw 'Runtime CI accepts loopback only. External providers require a paid campaign profile.'
    }

    $warmup = Invoke-RuntimeCompletionProbe -BaseUrl $RuntimeBaseUrl -Model $Model -TimeoutSeconds $TimeoutSeconds
    $results.Add([ordered]@{
            name = 'runtime_warmup_probe'
            status = $(if ($warmup.ok) {
                    'passed'
                } else {
                    'failed'
                })
            details = $warmup
        })
    # Warmup is separate and excluded from reproducibility samples.
    $modulePath = Join-Path $PSScriptRoot 'testing/RuntimeProbes.psm1'
    $jobs = @(for ($i = 0
            $i -lt $Concurrency
            $i++) {
            Start-Job -ScriptBlock {
                param ($module, $url, $model, $timeout)
                Import-Module $module
                Invoke-RuntimeCompletionProbe -BaseUrl $url -Model $model -TimeoutSeconds $timeout
            }

            -ArgumentList $modulePath, $RuntimeBaseUrl, $Model, $TimeoutSeconds
        })
    try {
        $concurrent = @($jobs | Receive-Job -Wait -ErrorAction Stop)
    } finally {
        $jobs | Remove-Job -Force -ErrorAction SilentlyContinue
    }

    $results.Add([ordered]@{
            name = 'runtime_concurrency'
            status = $(if ($concurrent.Count -eq $Concurrency -and @($concurrent | Where-Object {
                            -not $_.ok
                        }
                    ).Count -eq 0) {
                    'passed'
                } else {
                    'failed'
                })
            details = $concurrent
        })
    for ($i = 0; $i -lt $Iterations; $i++) {
        $runtimeRuns += Invoke-RuntimeCompletionProbe -BaseUrl $RuntimeBaseUrl -Model $Model -TimeoutSeconds $TimeoutSeconds
    }

    $repro = Test-Reproducibility -Runs $runtimeRuns -TtftTolerancePercent $TtftTolerancePercent -TokPerSecTolerancePercent $TokPerSecTolerancePercent
    $results.Add([ordered]@{
            name = 'runtime_reproducibility'
            status = $(if ($repro.ok) {
                    'passed'
                } else {
                    'failed'
                })
            details = $repro
        })
    $models = Invoke-ProbeHttpJson -Url ($RuntimeBaseUrl.TrimEnd('/')+'/v1/models') -TimeoutSeconds $TimeoutSeconds
    $results.Add([ordered]@{
            name = 'runtime_availability_probe'
            status = $(if ($models.ok) {
                    'passed'
                } else {
                    'failed'
                })
            details = $models
            guarantees = 'HTTP model catalogue availability only; fault recovery not exercised.'
        })
}

$failed = @($results | Where-Object status -eq 'failed')
$skipped = @($results | Where-Object status -eq 'skipped')
$overall = if ($failed.Count -gt 0 -or ($StrictRuntime -and $skipped.Count -gt 0)) {
    'failed'
} elseif (@($results | Where-Object status -eq 'passed').Count -eq 0) {
    'not_executed'
} else {
    'passed'
}

$payload = [ordered]@{
    schemaVersion = 'saaia-runtime-ci-v2'
    runId = $runId
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    gitCommit = (& git -C $repoRoot rev-parse HEAD)
    gitDirty = [bool](& git -C $repoRoot status --porcelain)
    purpose = 'Local runtime smoke and streaming reproducibility; no fault recovery claim.'
    runtimeBaseUrl = $RuntimeBaseUrl
    requestedModel = $Model
    observedModels = @($runtimeRuns | ForEach-Object model | Select-Object -Unique)
    corpus = if ($CorpusManifestPath) {
        @{
            sha256 = (Get-FileHash -LiteralPath $CorpusManifestPath -Algorithm SHA256).Hash
            path = $CorpusManifestPath
        }
    } else {
        @{
            state = 'not_applicable'
            reason = 'Synthetic completion smoke has no retrieval corpus.'
        }
    }

    strictRuntime = [bool]$StrictRuntime
    streaming = $true
    profile = 'Unit'
    runtimeSamples = $runtimeRuns
    correlation = @{
        state = 'probe_only'
        reason = 'Synthetic completion probes are not end-to-end RAG turns.'
    }

    summary = @{
        status = $overall
        stepCount = $results.Count
        failedCount = $failed.Count
        skippedCount = $skipped.Count
    }

    steps = $results
}

[IO.Directory]::CreateDirectory((Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath)))) | Out-Null
$payload | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "runtime-ci-harness: $overall; report: $OutputPath"
if ($overall -eq 'failed') {
    exit 1
}

exit 0
