[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("OpenAI", "RunPod")]
    [string]$Provider,
    [string]$BaseUrl = "",
    [string]$ModelId = "",
    [string]$RuntimeProfile = "",
    [string]$Gpu = "",
    [string]$Quantization = "",
    [string]$ModelSha256 = "",
    [decimal]$HourlyCostUsd = 0,
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
$ledgerPath = if ([string]::IsNullOrWhiteSpace($env:SAAIA_OPENAI_USAGE_LEDGER_PATH)) {
    Join-Path $env:LOCALAPPDATA "SAAIA\llm-dev\openai-terra-usage.jsonl"
} else {
    [System.IO.Path]::GetFullPath($env:SAAIA_OPENAI_USAGE_LEDGER_PATH)
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
    "SAAIA_ADVANCED_PROVIDER_LEDGER_PATH"
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
        runtimeProfile = $RuntimeProfile
        gpu = $Gpu
        quantization = $Quantization
        modelSha256 = $ModelSha256
        hourlyCostUsd = $HourlyCostUsd
        syntheticEvidenceOnly = $true
        maximumProviderCalls = 2
        openAiAuthorizedBudgetUsd = $(if ($Provider -eq "OpenAI") { 25 } else { $null })
        openAiHardStopUsd = $(if ($Provider -eq "OpenAI") { 24 } else { $null })
        usageLedgerPath = $(if ($Provider -eq "OpenAI") { $ledgerPath } else { $null })
        productStatus = "TESTE_NON_APPROUVE"
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "preflight-seal.json") -Encoding utf8

    $env:SAAIA_RUN_ADVANCED_PROVIDER_TEST = "1"
    $env:SAAIA_ADVANCED_ANALYSIS_PROVIDER = $providerMode
    $env:SAAIA_ADVANCED_LLM_BASE_URL = $BaseUrl.TrimEnd('/')
    $env:SAAIA_ADVANCED_LLM_MODEL = $ModelId
    $env:SAAIA_ADVANCED_LLM_API_KEY = $secret
    $env:SAAIA_ADVANCED_PROVIDER_ARTIFACT_DIR = $ArtifactDirectory
    $env:SAAIA_ADVANCED_PROVIDER_LEDGER_PATH = $ledgerPath

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
