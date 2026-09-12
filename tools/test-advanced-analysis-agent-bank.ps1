[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("openai-dev", "runpod-bench", "customer-server")]
    [string]$ExpectedAdvancedProvider,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedAdvancedModel,
    [string]$LocalLlmBaseUrl = "http://127.0.0.1:1234",
    [string]$LocalModel = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
    [string]$LocalLlmExePath = "",
    [string]$LocalModelPath = "",
    [string]$BankPath = "",
    [string]$Ids = "",
    [ValidateRange(1, 3)]
    [int]$Repetitions = 1,
    [ValidateRange(0, 300)]
    [int]$DelayBetweenCasesSeconds = 0,
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryCommit)) {
    throw "Unable to resolve the repository commit for the campaign seal."
}
$repositoryTrackedDirty = @(
    & git -C $repositoryRoot status --porcelain --untracked-files=no 2>$null).Count -gt 0
$project = Join-Path $repositoryRoot "client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj"
$providerConfig = Join-Path $repositoryRoot "config\llm-providers.dev.json"
if ([string]::IsNullOrWhiteSpace($BankPath)) {
    $BankPath = Join-Path $repositoryRoot "config\advanced-capacity-validation.v1.json"
}
$BankPath = [System.IO.Path]::GetFullPath($BankPath)
if (-not (Test-Path -LiteralPath $BankPath)) {
    throw "Question bank not found: $BankPath"
}
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\advanced-analysis-agent-bank-$stamp"
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null

$bank = Get-Content -LiteralPath $BankPath -Raw | ConvertFrom-Json
[string[]]$selectedIds = @(if ([string]::IsNullOrWhiteSpace($Ids)) {
    $bank.validationCases | ForEach-Object id
} else {
    $Ids -split '[,;]' | ForEach-Object Trim | Where-Object { $_ }
})
if ($selectedIds.Count -eq 0) { throw "No validation case was selected." }

$trackedEnvironment = @(
    "SAAIA_LLM_PROVIDER_MODE",
    "SAAIA_LLM_EXTERNAL_POLICY",
    "SAAIA_LLM_CONFIG_PATH",
    "SAAIA_LIVE_AGENT_BANK",
    "SAAIA_VALIDATION_LLM_BASE_URL",
    "SAAIA_VALIDATION_LLM_MODEL",
    "SAAIA_VALIDATION_LLM_EXE_PATH",
    "SAAIA_VALIDATION_LLM_MODEL_PATH",
    "SAAIA_VALIDATION_MANAGE_LOCAL_LLM_PROCESS",
    "SAAIA_AGENT_VALIDATION_ADVANCED_SERVER",
    "SAAIA_AGENT_VALIDATION_BANK_PATH",
    "SAAIA_AGENT_VALIDATION_IDS",
    "SAAIA_AGENT_VALIDATION_OUTPUT_DIR",
    "SAAIA_AGENT_VALIDATION_TIMEOUT_SECONDS",
    "SAAIA_AGENT_VALIDATION_DELAY_BETWEEN_CASES_SECONDS",
    "SAAIA_AGENT_VALIDATION_MAX_OUTPUT_TOKENS",
    "SAAIA_SOURCE_BACKED_AGENT_V2_MAX_OUTPUT_TOKENS"
)
$previousEnvironment = @{}
foreach ($name in $trackedEnvironment) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

$runs = @()
$failure = $null
$campaignCompleted = $false
try {
    $env:SAAIA_LLM_PROVIDER_MODE = "Local"
    $env:SAAIA_LLM_EXTERNAL_POLICY = "ProductionLocal"
    $env:SAAIA_LLM_CONFIG_PATH = $providerConfig
    $env:SAAIA_LIVE_AGENT_BANK = "1"
    $env:SAAIA_VALIDATION_LLM_BASE_URL = $LocalLlmBaseUrl.TrimEnd('/')
    $env:SAAIA_VALIDATION_LLM_MODEL = $LocalModel
    $env:SAAIA_VALIDATION_LLM_EXE_PATH = $LocalLlmExePath
    $env:SAAIA_VALIDATION_LLM_MODEL_PATH = $LocalModelPath
    $env:SAAIA_VALIDATION_MANAGE_LOCAL_LLM_PROCESS = "1"
    $env:SAAIA_AGENT_VALIDATION_ADVANCED_SERVER = "1"
    $env:SAAIA_AGENT_VALIDATION_BANK_PATH = $BankPath
    $env:SAAIA_AGENT_VALIDATION_IDS = $selectedIds -join ","
    $env:SAAIA_AGENT_VALIDATION_TIMEOUT_SECONDS = "1800"
    $env:SAAIA_AGENT_VALIDATION_DELAY_BETWEEN_CASES_SECONDS = [string]$DelayBetweenCasesSeconds
    $env:SAAIA_AGENT_VALIDATION_MAX_OUTPUT_TOKENS = "900"
    $env:SAAIA_SOURCE_BACKED_AGENT_V2_MAX_OUTPUT_TOKENS = "900"

    $buildOutput = & dotnet build $project -c $Configuration -p:Platform=$Platform --no-restore 2>&1
    $buildExit = $LASTEXITCODE
    $buildOutput | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "build.log") -Encoding utf8
    if ($buildExit -ne 0) { throw "Build failed with exit code $buildExit." }

    [ordered]@{
        schemaVersion = "saaia-advanced-analysis-agent-bank-preflight-v1"
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        clientProvider = "local"
        localModel = $LocalModel
        expectedAdvancedProvider = $ExpectedAdvancedProvider
        expectedAdvancedModel = $ExpectedAdvancedModel
        topology = "local-router-to-durable-server-job-to-configured-large-llm"
        repositoryCommit = $repositoryCommit
        repositoryTrackedDirty = $repositoryTrackedDirty
        bankPath = $BankPath
        bankSha256 = (Get-FileHash -LiteralPath $BankPath -Algorithm SHA256).Hash
        providerConfigPath = $providerConfig
        providerConfigSha256 = (Get-FileHash -LiteralPath $providerConfig -Algorithm SHA256).Hash
        selectedIds = $selectedIds
        repetitions = $Repetitions
        delayBetweenCasesSeconds = $DelayBetweenCasesSeconds
        productStatus = "TESTE_NON_APPROUVE"
        semanticApproval = "PENDING"
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "preflight-seal.json") -Encoding utf8

    foreach ($repetition in 1..$Repetitions) {
        $repetitionDirectory = Join-Path $ArtifactDirectory "r$repetition"
        $outputDirectory = Join-Path $repetitionDirectory "results"
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
        $env:SAAIA_AGENT_VALIDATION_OUTPUT_DIR = $outputDirectory
        $testOutput = & dotnet test $project `
            -c $Configuration `
            -p:Platform=$Platform `
            --no-build `
            --no-restore `
            --filter "FullyQualifiedName=SAAIA.Client.ToolAgent.Tests.LiveQuestionBankAgentValidationTests.Live_question_bank_agent_validation_when_enabled" `
            --results-directory $repetitionDirectory `
            --logger "trx;LogFileName=advanced-analysis-r$repetition.trx" 2>&1
        $testExit = $LASTEXITCODE
        $testOutput | Set-Content -LiteralPath (Join-Path $repetitionDirectory "test-output.log") -Encoding utf8
        if ($testExit -ne 0) {
            throw "Advanced-analysis agent-bank repetition $repetition failed with exit code $testExit."
        }
        $resultFile = Get-ChildItem -LiteralPath $outputDirectory -Filter "*.json" |
            Sort-Object LastWriteTime | Select-Object -Last 1
        if ($null -eq $resultFile) { throw "No result JSON was produced for repetition $repetition." }
        $result = Get-Content -LiteralPath $resultFile.FullName -Raw | ConvertFrom-Json
        $rows = @($result.rows)
        $invalid = @($rows | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_.error) -or
            [string]$_.advancedStatus -ne "succeeded" -or
            [string]$_.advancedProviderKey -ne $ExpectedAdvancedProvider -or
            [string]$_.advancedProviderModel -ne $ExpectedAdvancedModel -or
            [string]$_.advancedJobId -notmatch '^[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$' -or
            [int]$_.advancedProviderCallCount -le 0 -or
            -not [string]::IsNullOrWhiteSpace([string]$_.answerFlags)
        })
        if ($invalid.Count -gt 0) {
            $ids = ($invalid | ForEach-Object id) -join ","
            throw "Advanced server contract mismatch in repetition ${repetition}: $ids"
        }
        $runs += [ordered]@{
            repetition = $repetition
            exitCode = $testExit
            result = $resultFile.FullName
            rowCount = $rows.Count
            totalAdvancedCalls = ($rows | Measure-Object -Property advancedProviderCallCount -Sum).Sum
            totalAdvancedInputTokens = ($rows | Measure-Object -Property advancedInputTokens -Sum).Sum
            totalAdvancedOutputTokens = ($rows | Measure-Object -Property advancedOutputTokens -Sum).Sum
            totalAdvancedEstimatedCostUsd = ($rows | Measure-Object -Property advancedEstimatedCostUsd -Sum).Sum
        }
        if ($repetition -lt $Repetitions -and $DelayBetweenCasesSeconds -gt 0) {
            Start-Sleep -Seconds $DelayBetweenCasesSeconds
        }
    }
    $campaignCompleted = $true
}
catch {
    $failure = $_.Exception.GetType().Name + ": " + $_.Exception.Message
    throw
}
finally {
    if (-not $campaignCompleted -and [string]::IsNullOrWhiteSpace($failure)) {
        $failure = "Campaign interrupted before completion."
    }
    foreach ($name in $trackedEnvironment) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
    }
    $portOwned = @(Get-NetTCPConnection -State Listen -LocalPort 1234 -ErrorAction SilentlyContinue).Count
    [ordered]@{
        endedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        runs = $runs
        completed = $campaignCompleted
        failure = $failure
        environmentRestored = $true
        localPort1234ListenersAfterRun = $portOwned
        advancedProviderInfrastructureStoppedByScript = $false
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "resource-shutdown.json") -Encoding utf8
}

Write-Output "Advanced-analysis agent-bank campaign completed. Artifact: $ArtifactDirectory"
