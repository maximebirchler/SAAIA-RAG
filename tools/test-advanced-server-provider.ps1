[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("OpenAI", "RunPod")]
    [string]$Provider,
    [string]$BaseUrl = "",
    [string]$ModelId = "",
    [string]$ProviderRuntime = "",
    [string]$RuntimeProfile = "",
    [string]$Gpu = "",
    [string]$Quantization = "",
    [string]$ModelSha256 = "",
    [decimal]$HourlyCostUsd = 0,
    [decimal]$AuthorizedBudgetUsd = 0,
    [decimal]$SoftLimitUsd = 0,
    [decimal]$HardLimitUsd = 0,
    [decimal]$MaximumCostPerJobUsd = 0,
    [int]$MaximumCallsPerJob = 4,
    [decimal]$InputUsdPerMillionTokens = 0,
    [decimal]$CachedInputUsdPerMillionTokens = 0,
    [decimal]$OutputUsdPerMillionTokens = 0,
    [string]$Configuration = "Debug",
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not ("System.Security.Cryptography.ProtectedData" -as [type])) {
    Add-Type -AssemblyName System.Security
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Join-Path $repositoryRoot "backend\SAAIA.Backend.Tests\SAAIA.Backend.Tests.csproj"
$storePath = Join-Path $env:LOCALAPPDATA "SAAIA\client\secure.json"
$providerProperty = if ($Provider -eq "OpenAI") {
    "OpenAiApiKeyProtected"
} else {
    "RunPodApiKeyProtected"
}
$providerMode = if ($Provider -eq "OpenAI") { "openai-dev" } else { "runpod-bench" }
if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    if ($Provider -eq "OpenAI") { $BaseUrl = "https://api.openai.com/v1" }
    else { throw "BaseUrl is required for RunPod." }
}
if ([string]::IsNullOrWhiteSpace($ModelId)) {
    if ($Provider -eq "OpenAI") { $ModelId = "gpt-5.6-terra" }
    else { throw "ModelId is required for RunPod." }
}
if ($Provider -eq "OpenAI") {
    if ($AuthorizedBudgetUsd -le 0) { $AuthorizedBudgetUsd = 25 }
    if ($InputUsdPerMillionTokens -le 0 -or
        $CachedInputUsdPerMillionTokens -le 0 -or
        $OutputUsdPerMillionTokens -le 0) {
        switch ($ModelId) {
            "gpt-5.6-terra" {
                $InputUsdPerMillionTokens = 2
                $CachedInputUsdPerMillionTokens = 0.20
                $OutputUsdPerMillionTokens = 12
            }
            "gpt-5.6-luna" {
                $InputUsdPerMillionTokens = 0.20
                $CachedInputUsdPerMillionTokens = 0.02
                $OutputUsdPerMillionTokens = 1.20
            }
            default {
                throw "Explicit token pricing is required for OpenAI model '$ModelId'."
            }
        }
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($ProviderRuntime)) {
        throw "ProviderRuntime is required for RunPod; no runtime identity is assumed."
    }
    if ($AuthorizedBudgetUsd -le 0) {
        throw "AuthorizedBudgetUsd is required for RunPod; no RunPod spend is assumed."
    }
    if ($InputUsdPerMillionTokens -le 0 -or
        $CachedInputUsdPerMillionTokens -le 0 -or
        $OutputUsdPerMillionTokens -le 0) {
        throw "Explicit input, cached-input and output token pricing is required for RunPod."
    }
}
if ($SoftLimitUsd -le 0) {
    $SoftLimitUsd = [decimal]::Round($AuthorizedBudgetUsd * 0.80, 2)
}
if ($HardLimitUsd -le 0) {
    $HardLimitUsd = [decimal]::Round($AuthorizedBudgetUsd * 0.96, 2)
}
if ($MaximumCostPerJobUsd -le 0) {
    $MaximumCostPerJobUsd = [decimal]::Min(0.50, $HardLimitUsd)
}
if ($MaximumCallsPerJob -le 0 -or
    $SoftLimitUsd -le 0 -or
    $HardLimitUsd -le 0 -or
    $SoftLimitUsd -gt $HardLimitUsd -or
    $HardLimitUsd -gt $AuthorizedBudgetUsd -or
    $MaximumCostPerJobUsd -gt $HardLimitUsd) {
    throw "The external provider budget envelope is invalid."
}
if (-not [Uri]::IsWellFormedUriString($BaseUrl, [UriKind]::Absolute)) {
    throw "BaseUrl must be an absolute URI."
}
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $slug = $Provider.ToLowerInvariant()
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\advanced-server-$slug-$stamp"
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null
$ledgerPath = if (-not [string]::IsNullOrWhiteSpace(
        $env:SAAIA_ADVANCED_PROVIDER_LEDGER_PATH)) {
    [System.IO.Path]::GetFullPath($env:SAAIA_ADVANCED_PROVIDER_LEDGER_PATH)
} elseif ($Provider -eq "OpenAI" -and
          -not [string]::IsNullOrWhiteSpace($env:SAAIA_OPENAI_USAGE_LEDGER_PATH)) {
    [System.IO.Path]::GetFullPath($env:SAAIA_OPENAI_USAGE_LEDGER_PATH)
} elseif ($Provider -eq "OpenAI") {
    Join-Path $env:LOCALAPPDATA "SAAIA\llm-dev\openai-terra-usage.jsonl"
} else {
    $ledgerSlug = ($providerMode + "-" + $ModelId) -replace '[^a-zA-Z0-9._-]', '-'
    Join-Path $env:LOCALAPPDATA "SAAIA\llm-dev\$ledgerSlug-usage.jsonl"
}

if (-not (Test-Path -LiteralPath $storePath)) {
    throw "Protected SAAIA secret store not found. Import the $Provider key first."
}
$store = Get-Content -LiteralPath $storePath -Raw | ConvertFrom-Json
$protectedBase64 = $store.$providerProperty
if ([string]::IsNullOrWhiteSpace($protectedBase64)) {
    throw "$Provider key is absent from the protected SAAIA secret store."
}
$entropy = [System.Text.Encoding]::UTF8.GetBytes("SAAIA.Client.WinUI|CDC-v2.7")
$protectedBytes = [Convert]::FromBase64String($protectedBase64)
$plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
    $protectedBytes,
    $entropy,
    [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$secret = [System.Text.Encoding]::UTF8.GetString($plainBytes)

$tracked = @(
    "SAAIA_RUN_ADVANCED_PROVIDER_TEST",
    "SAAIA_ADVANCED_ANALYSIS_PROVIDER",
    "SAAIA_ADVANCED_LLM_BASE_URL",
    "SAAIA_ADVANCED_LLM_MODEL",
    "SAAIA_ADVANCED_LLM_API_KEY",
    "SAAIA_ADVANCED_PROVIDER_ARTIFACT_DIR",
    "SAAIA_ADVANCED_PROVIDER_LEDGER_PATH",
    "SAAIA_ADVANCED_EXTERNAL_BUDGET_AUTHORIZED_USD",
    "SAAIA_ADVANCED_EXTERNAL_BUDGET_SOFT_LIMIT_USD",
    "SAAIA_ADVANCED_EXTERNAL_BUDGET_HARD_LIMIT_USD",
    "SAAIA_ADVANCED_EXTERNAL_MAXIMUM_COST_PER_JOB_USD",
    "SAAIA_ADVANCED_EXTERNAL_MAXIMUM_CALLS_PER_JOB",
    "SAAIA_ADVANCED_EXTERNAL_INPUT_USD_PER_MILLION_TOKENS",
    "SAAIA_ADVANCED_EXTERNAL_CACHED_INPUT_USD_PER_MILLION_TOKENS",
    "SAAIA_ADVANCED_EXTERNAL_OUTPUT_USD_PER_MILLION_TOKENS"
)
$previous = @{}
foreach ($name in $tracked) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

try {
    [ordered]@{
        schemaVersion = "saaia-advanced-server-provider-preflight-v1"
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        provider = $providerMode
        model = $ModelId
        endpointScheme = ([Uri]$BaseUrl).Scheme
        endpointHost = ([Uri]$BaseUrl).Host
        providerRuntime = $ProviderRuntime
        runtimeProfile = $RuntimeProfile
        gpu = $Gpu
        quantization = $Quantization
        modelSha256 = $ModelSha256
        hourlyCostUsd = $HourlyCostUsd
        syntheticEvidenceOnly = $true
        maximumProviderCalls = 2
        authorizedBudgetUsd = $AuthorizedBudgetUsd
        softLimitUsd = $SoftLimitUsd
        hardStopUsd = $HardLimitUsd
        maximumCostPerJobUsd = $MaximumCostPerJobUsd
        maximumCallsPerJob = $MaximumCallsPerJob
        inputUsdPerMillionTokens = $InputUsdPerMillionTokens
        cachedInputUsdPerMillionTokens = $CachedInputUsdPerMillionTokens
        outputUsdPerMillionTokens = $OutputUsdPerMillionTokens
        usageLedgerPath = $ledgerPath
        productStatus = "TESTE_NON_APPROUVE"
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "preflight-seal.json") -Encoding utf8

    $env:SAAIA_RUN_ADVANCED_PROVIDER_TEST = "1"
    $env:SAAIA_ADVANCED_ANALYSIS_PROVIDER = $providerMode
    $env:SAAIA_ADVANCED_LLM_BASE_URL = $BaseUrl.TrimEnd('/')
    $env:SAAIA_ADVANCED_LLM_MODEL = $ModelId
    $env:SAAIA_ADVANCED_LLM_API_KEY = $secret
    $env:SAAIA_ADVANCED_PROVIDER_ARTIFACT_DIR = $ArtifactDirectory
    $env:SAAIA_ADVANCED_PROVIDER_LEDGER_PATH = $ledgerPath
    $env:SAAIA_ADVANCED_EXTERNAL_BUDGET_AUTHORIZED_USD =
        $AuthorizedBudgetUsd.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:SAAIA_ADVANCED_EXTERNAL_BUDGET_SOFT_LIMIT_USD =
        $SoftLimitUsd.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:SAAIA_ADVANCED_EXTERNAL_BUDGET_HARD_LIMIT_USD =
        $HardLimitUsd.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:SAAIA_ADVANCED_EXTERNAL_MAXIMUM_COST_PER_JOB_USD =
        $MaximumCostPerJobUsd.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:SAAIA_ADVANCED_EXTERNAL_MAXIMUM_CALLS_PER_JOB =
        $MaximumCallsPerJob.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:SAAIA_ADVANCED_EXTERNAL_INPUT_USD_PER_MILLION_TOKENS =
        $InputUsdPerMillionTokens.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:SAAIA_ADVANCED_EXTERNAL_CACHED_INPUT_USD_PER_MILLION_TOKENS =
        $CachedInputUsdPerMillionTokens.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:SAAIA_ADVANCED_EXTERNAL_OUTPUT_USD_PER_MILLION_TOKENS =
        $OutputUsdPerMillionTokens.ToString([Globalization.CultureInfo]::InvariantCulture)

    $testOutput = & dotnet test $project `
        -c $Configuration `
        --filter "FullyQualifiedName=SAAIA.Backend.Tests.LiveAdvancedAnalysisProviderTests.Advanced_provider_builds_complete_synthetic_meal_grid_when_explicitly_enabled" `
        --logger "console;verbosity=normal" 2>&1
    $testExit = $LASTEXITCODE
    $testOutput | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "test-output.log") -Encoding utf8
    if ($testExit -ne 0) {
        throw "Advanced server provider probe failed with exit code $testExit."
    }
}
finally {
    foreach ($name in $tracked) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], "Process")
    }
    if ($plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
    $secret = $null
    [ordered]@{
        endedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        environmentRestored = $true
        secretPersistedInArtifact = $false
        localLlmProcessStarted = $false
        providerInfrastructureStoppedByScript = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "resource-shutdown.json") -Encoding utf8
}

Write-Output "Advanced server provider probe completed. Artifact: $ArtifactDirectory"
