[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$BankPath = "",
    [string]$Ids = "",
    [ValidateRange(1, 3)]
    [int]$Repetitions = 1,
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
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
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\openai-terra-agent-bank-$stamp"
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null

$ledgerPath = if ([string]::IsNullOrWhiteSpace($env:SAAIA_OPENAI_USAGE_LEDGER_PATH)) {
    Join-Path $env:LOCALAPPDATA "SAAIA\llm-dev\openai-terra-usage.jsonl"
} else {
    [System.IO.Path]::GetFullPath($env:SAAIA_OPENAI_USAGE_LEDGER_PATH)
}

function Get-RecordedCost([string]$Path) {
    $total = [decimal]0
    if (-not (Test-Path -LiteralPath $Path)) { return $total }
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $entry = $line | ConvertFrom-Json
        if ($null -ne $entry.costUsd) { $total += [decimal]$entry.costUsd }
    }
    return $total
}

$beforeCost = Get-RecordedCost $ledgerPath
if ($beforeCost -ge [decimal]24) {
    throw "The local Terra ledger has reached the 24 USD hard stop."
}

$bank = Get-Content -LiteralPath $BankPath -Raw | ConvertFrom-Json
$selectedIds = if ([string]::IsNullOrWhiteSpace($Ids)) {
    @($bank.validationCases | ForEach-Object id)
} else {
    @($Ids -split '[,;]' | ForEach-Object Trim | Where-Object { $_ })
}
if ($selectedIds.Count -eq 0) {
    throw "No validation case was selected."
}

$trackedEnvironment = @(
    "SAAIA_LLM_PROVIDER_MODE",
    "SAAIA_LLM_EXTERNAL_POLICY",
    "SAAIA_LLM_CONFIG_PATH",
    "SAAIA_OPENAI_MODEL",
    "SAAIA_LIVE_AGENT_BANK",
    "SAAIA_VALIDATION_LLM_BASE_URL",
    "SAAIA_VALIDATION_LLM_MODEL",
    "SAAIA_VALIDATION_MANAGE_LOCAL_LLM_PROCESS",
    "SAAIA_AGENT_VALIDATION_BANK_PATH",
    "SAAIA_AGENT_VALIDATION_IDS",
    "SAAIA_AGENT_VALIDATION_OUTPUT_DIR",
    "SAAIA_AGENT_VALIDATION_TIMEOUT_SECONDS",
    "SAAIA_AGENT_VALIDATION_MAX_OUTPUT_TOKENS",
    "SAAIA_SOURCE_BACKED_AGENT_V2_MAX_OUTPUT_TOKENS"
)
$previousEnvironment = @{}
foreach ($name in $trackedEnvironment) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

$runSummaries = @()
$failure = $null
try {
    $env:SAAIA_LLM_PROVIDER_MODE = "OpenAiDev"
    $env:SAAIA_LLM_EXTERNAL_POLICY = "DevelopmentExternalAllowed"
    $env:SAAIA_LLM_CONFIG_PATH = $providerConfig
    $env:SAAIA_OPENAI_MODEL = "gpt-5.6-terra"
    $env:SAAIA_LIVE_AGENT_BANK = "1"
    $env:SAAIA_VALIDATION_LLM_BASE_URL = "https://api.openai.com/v1"
    $env:SAAIA_VALIDATION_LLM_MODEL = "gpt-5.6-terra"
    $env:SAAIA_VALIDATION_MANAGE_LOCAL_LLM_PROCESS = "0"
    $env:SAAIA_AGENT_VALIDATION_BANK_PATH = $BankPath
    $env:SAAIA_AGENT_VALIDATION_IDS = $selectedIds -join ","
    $env:SAAIA_AGENT_VALIDATION_TIMEOUT_SECONDS = "900"
    $env:SAAIA_AGENT_VALIDATION_MAX_OUTPUT_TOKENS = "2400"
    $env:SAAIA_SOURCE_BACKED_AGENT_V2_MAX_OUTPUT_TOKENS = "2400"

    $buildOutput = & dotnet build $project -c $Configuration -p:Platform=$Platform --no-restore 2>&1
    $buildExit = $LASTEXITCODE
    $buildOutput | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "build.log") -Encoding utf8
    if ($buildExit -ne 0) {
        throw "Build failed with exit code $buildExit."
    }

    [ordered]@{
        schemaVersion = "saaia-terra-agent-bank-preflight-v1"
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        provider = "openai"
        providerMode = "OpenAiDev"
        model = "gpt-5.6-terra"
        policy = "DevelopmentExternalAllowed"
        bankPath = $BankPath
        bankSha256 = (Get-FileHash -LiteralPath $BankPath -Algorithm SHA256).Hash
        providerConfigSha256 = (Get-FileHash -LiteralPath $providerConfig -Algorithm SHA256).Hash
        selectedIds = $selectedIds
        repetitions = $Repetitions
        ledgerPath = $ledgerPath
        recordedCostBeforeUsd = $beforeCost
        authorizedBudgetUsd = 25
        localHardStopUsd = 24
        maximumCostPerTurnUsd = 0.50
        maximumCallsPerTurn = 32
        managesLocalLlmProcess = $false
        externalDataScope = "router prompts, selected tool results and evidence bundles only"
        semanticApproval = "PENDING"
        productStatus = "TESTE_NON_APPROUVE"
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
            --logger "trx;LogFileName=terra-agent-bank-r$repetition.trx" 2>&1
        $testExit = $LASTEXITCODE
        $testOutput | Set-Content -LiteralPath (Join-Path $repetitionDirectory "test-output.log") -Encoding utf8
        $result = Get-ChildItem -LiteralPath $outputDirectory -Filter "*.json" | Sort-Object LastWriteTime | Select-Object -Last 1
        $runSummaries += [ordered]@{
            repetition = $repetition
            exitCode = $testExit
            result = $result.FullName
            recordedCostAfterUsd = Get-RecordedCost $ledgerPath
        }
        if ($testExit -ne 0) {
            throw "Terra agent-bank repetition $repetition failed with exit code $testExit."
        }
    }
}
catch {
    $failure = $_.Exception.GetType().Name + ": " + $_.Exception.Message
    throw
}
finally {
    $afterCost = Get-RecordedCost $ledgerPath
    foreach ($name in $trackedEnvironment) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
    }
    [ordered]@{
        endedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        runs = $runSummaries
        failure = $failure
        recordedCostBeforeUsd = $beforeCost
        recordedCostAfterUsd = $afterCost
        campaignCostUsd = $afterCost - $beforeCost
        localHardStopUsd = 24
        environmentRestored = $true
        localLlmProcessStarted = $false
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "resource-shutdown.json") -Encoding utf8
}

Write-Output "Terra agent-bank campaign completed. Artifact: $ArtifactDirectory"
