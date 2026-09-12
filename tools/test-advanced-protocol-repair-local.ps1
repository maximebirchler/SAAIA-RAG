[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int]$FixturePort = 18081,
    [string]$Configuration = "Release",
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Wait-FixtureReady {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [ValidateRange(1, 60)][int]$TimeoutSeconds = 20
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) {
            throw "The local protocol fixture exited before becoming ready."
        }
        try {
            $response = Invoke-WebRequest `
                -Uri "$BaseUrl/health" `
                -UseBasicParsing `
                -TimeoutSec 2
            if ([int]$response.StatusCode -eq 200) { return }
        }
        catch {
            # The loopback listener can refuse connections during Python startup.
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The local protocol fixture did not become ready within $TimeoutSeconds seconds."
}

function Stop-OwnedProcess {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process -or $Process.HasExited) { return }
    Stop-Process -Id $Process.Id -Force -ErrorAction Stop
    $Process.WaitForExit(10000) | Out-Null
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryCommit)) {
    throw "Unable to resolve the repository commit for the protocol repair seal."
}
$repositoryTrackedDirty = @(
    & git -C $repositoryRoot status --porcelain --untracked-files=no 2>$null).Count -gt 0
$python = Get-Command python -ErrorAction Stop
$fixtureScript = Join-Path $PSScriptRoot 'fixtures\fake_openai_repair_server.py'
$testProject = Join-Path $repositoryRoot 'backend\SAAIA.Backend.Tests\SAAIA.Backend.Tests.csproj'
if (-not (Test-Path -LiteralPath $fixtureScript -PathType Leaf)) {
    throw "Local protocol fixture not found: $fixtureScript"
}
if (@(Get-NetTCPConnection -State Listen -LocalPort $FixturePort -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Port $FixturePort is already in use."
}
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ArtifactDirectory = Join-Path $repositoryRoot (
        "artifacts\reprise-pc-20260908\a763-local-protocol-repair-$stamp")
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $ArtifactDirectory.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ArtifactDirectory must stay below the repository artifacts directory."
}
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null
$fixtureArtifactDirectory = Join-Path $ArtifactDirectory 'fixture'
$providerArtifactDirectory = Join-Path $ArtifactDirectory 'provider'
New-Item -ItemType Directory -Path $fixtureArtifactDirectory | Out-Null
New-Item -ItemType Directory -Path $providerArtifactDirectory | Out-Null
$fixtureBaseUrl = "http://127.0.0.1:$FixturePort"
$fixtureProcess = $null
$failure = $null
$completed = $false
$testExitCode = $null

$trackedEnvironment = @(
    'SAAIA_RUN_ADVANCED_PROVIDER_TEST',
    'SAAIA_ADVANCED_ANALYSIS_PROVIDER',
    'SAAIA_ADVANCED_LLM_BASE_URL',
    'SAAIA_ADVANCED_LLM_MODEL',
    'SAAIA_ADVANCED_LLM_API_KEY',
    'SAAIA_ADVANCED_PROVIDER_ARTIFACT_DIR',
    'SAAIA_ADVANCED_PROVIDER_LEDGER_PATH',
    'SAAIA_ADVANCED_EXPECTED_PROVIDER_CALLS',
    'SAAIA_ADVANCED_EXTERNAL_BUDGET_AUTHORIZED_USD',
    'SAAIA_ADVANCED_EXTERNAL_BUDGET_SOFT_LIMIT_USD',
    'SAAIA_ADVANCED_EXTERNAL_BUDGET_HARD_LIMIT_USD',
    'SAAIA_ADVANCED_EXTERNAL_MAXIMUM_COST_PER_JOB_USD',
    'SAAIA_ADVANCED_EXTERNAL_MAXIMUM_CALLS_PER_JOB',
    'SAAIA_ADVANCED_EXTERNAL_INPUT_USD_PER_MILLION_TOKENS',
    'SAAIA_ADVANCED_EXTERNAL_CACHED_INPUT_USD_PER_MILLION_TOKENS',
    'SAAIA_ADVANCED_EXTERNAL_OUTPUT_USD_PER_MILLION_TOKENS'
)
$previousEnvironment = @{}
foreach ($name in $trackedEnvironment) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $python.Source
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    [void]$startInfo.ArgumentList.Add($fixtureScript)
    [void]$startInfo.ArgumentList.Add('--port')
    [void]$startInfo.ArgumentList.Add($FixturePort.ToString([Globalization.CultureInfo]::InvariantCulture))
    [void]$startInfo.ArgumentList.Add('--artifact-directory')
    [void]$startInfo.ArgumentList.Add($fixtureArtifactDirectory)
    $fixtureProcess = [System.Diagnostics.Process]::Start($startInfo)
    Wait-FixtureReady -BaseUrl $fixtureBaseUrl -Process $fixtureProcess

    [ordered]@{
        schemaVersion = 'saaia-advanced-local-protocol-repair-preflight.v1'
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        repositoryCommit = $repositoryCommit
        repositoryTrackedDirty = $repositoryTrackedDirty
        fixtureHost = '127.0.0.1'
        fixturePort = $FixturePort
        provider = 'customer-server'
        providerLocation = 'internal'
        externalProviderCallsPossible = $false
        syntheticEvidenceOnly = $true
        expectedSequence = @('planner-valid', 'writer-malformed', 'writer-repair-valid')
        expectedProviderCallCount = 3
        productStatus = 'TESTE_NON_APPROUVE'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'preflight-seal.json') -Encoding utf8

    $env:SAAIA_RUN_ADVANCED_PROVIDER_TEST = '1'
    $env:SAAIA_ADVANCED_ANALYSIS_PROVIDER = 'customer-server'
    $env:SAAIA_ADVANCED_LLM_BASE_URL = "$fixtureBaseUrl/v1"
    $env:SAAIA_ADVANCED_LLM_MODEL = 'saaia-local-repair-fixture-v1'
    $env:SAAIA_ADVANCED_LLM_API_KEY = 'synthetic-loopback-only'
    $env:SAAIA_ADVANCED_PROVIDER_ARTIFACT_DIR = $providerArtifactDirectory
    $env:SAAIA_ADVANCED_PROVIDER_LEDGER_PATH = Join-Path $ArtifactDirectory 'unused-internal-ledger.jsonl'
    $env:SAAIA_ADVANCED_EXPECTED_PROVIDER_CALLS = '3'
    $env:SAAIA_ADVANCED_EXTERNAL_BUDGET_AUTHORIZED_USD = '1'
    $env:SAAIA_ADVANCED_EXTERNAL_BUDGET_SOFT_LIMIT_USD = '0.80'
    $env:SAAIA_ADVANCED_EXTERNAL_BUDGET_HARD_LIMIT_USD = '0.96'
    $env:SAAIA_ADVANCED_EXTERNAL_MAXIMUM_COST_PER_JOB_USD = '0.50'
    $env:SAAIA_ADVANCED_EXTERNAL_MAXIMUM_CALLS_PER_JOB = '4'
    $env:SAAIA_ADVANCED_EXTERNAL_INPUT_USD_PER_MILLION_TOKENS = '0'
    $env:SAAIA_ADVANCED_EXTERNAL_CACHED_INPUT_USD_PER_MILLION_TOKENS = '0'
    $env:SAAIA_ADVANCED_EXTERNAL_OUTPUT_USD_PER_MILLION_TOKENS = '0'

    $testOutput = & dotnet test $testProject `
        -c $Configuration `
        --filter 'FullyQualifiedName=SAAIA.Backend.Tests.LiveAdvancedAnalysisProviderTests.Advanced_provider_builds_complete_synthetic_meal_grid_when_explicitly_enabled' `
        --logger 'console;verbosity=normal' 2>&1
    $testExitCode = $LASTEXITCODE
    $testOutput | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'test-output.log') -Encoding utf8
    if ($testExitCode -ne 0) {
        throw "Local protocol repair probe failed with exit code $testExitCode."
    }

    $providerResult = Get-Content -LiteralPath (
        Join-Path $providerArtifactDirectory 'result.json') -Raw | ConvertFrom-Json
    $requestTrace = @(Get-Content -LiteralPath (
        Join-Path $fixtureArtifactDirectory 'request-trace.json') -Raw | ConvertFrom-Json)
    $roles = @($requestTrace | ForEach-Object { [string]$_.role })
    if ($providerResult.provider -ne 'customer-server' -or
        $providerResult.providerLocation -ne 'Internal' -or
        [int]$providerResult.result.providerCallCount -ne 3 -or
        [int]$providerResult.result.claims.Count -ne 20 -or
        [int]$providerResult.searches.Count -lt 1 -or
        $requestTrace.Count -ne 3 -or
        ($roles -join ',') -ne 'planner,writer-malformed,writer-repair') {
        throw "The local protocol repair evidence did not match the expected three-call sequence."
    }

    [ordered]@{
        schemaVersion = 'saaia-advanced-local-protocol-repair-assessment.v1'
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        repositoryCommit = $repositoryCommit
        provider = $providerResult.provider
        providerLocation = $providerResult.providerLocation
        model = $providerResult.model
        requestRoles = $roles
        providerCallCount = [int]$providerResult.result.providerCallCount
        outcome = $providerResult.result.outcome
        claimCount = [int]$providerResult.result.claims.Count
        distinctEvidenceCount = @(
            $providerResult.result.claims.evidenceIds | Select-Object -Unique).Count
        externalProviderCalls = 0
        externalContentTransmitted = $false
        verdict = 'PASS_BOUNDED_PROTOCOL_REPAIR_LIVE_LOOPBACK'
        productStatus = 'TESTE_NON_APPROUVE'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'assessment.v1.json') -Encoding utf8
    $completed = $true
}
catch {
    $failure = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
    throw
}
finally {
    foreach ($name in $trackedEnvironment) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    Stop-OwnedProcess -Process $fixtureProcess
    $fixtureStdout = if ($null -ne $fixtureProcess) {
        $fixtureProcess.StandardOutput.ReadToEnd()
    } else { '' }
    $fixtureStderr = if ($null -ne $fixtureProcess) {
        $fixtureProcess.StandardError.ReadToEnd()
    } else { '' }
    Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'fixture.stdout.log') `
        -Value $fixtureStdout -Encoding utf8
    Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'fixture.stderr.log') `
        -Value $fixtureStderr -Encoding utf8
    Start-Sleep -Milliseconds 300
    $listenersAfterRun = @(
        Get-NetTCPConnection -State Listen -LocalPort $FixturePort -ErrorAction SilentlyContinue).Count
    [ordered]@{
        endedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        completed = $completed
        failure = $failure
        testExitCode = $testExitCode
        environmentRestored = $true
        fixtureStopped = $null -eq $fixtureProcess -or $fixtureProcess.HasExited
        fixturePortListenersAfterRun = $listenersAfterRun
        externalProviderCalls = 0
        externalContentTransmitted = $false
        secretPersistedInArtifact = $false
        productStatus = 'TESTE_NON_APPROUVE'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (
        Join-Path $ArtifactDirectory 'resource-shutdown.json') -Encoding utf8
}

Write-Output "Local advanced protocol repair proof completed. Artifact: $ArtifactDirectory"
