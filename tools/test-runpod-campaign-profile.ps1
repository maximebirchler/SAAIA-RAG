[CmdletBinding()]
param(
    [string]$ProfilePath = "",
    [string]$ArtifactDirectory = "",
    [switch]$Execute,
    [switch]$ExternalContentAuthorized,
    [string]$ServerEnvPath = "",
    [string]$ReferenceBackendUrl = "http://saaia-server:5122",
    [ValidateRange(1024, 65535)]
    [int]$BackendPort = 5123,
    [string]$LocalLlmExePath = "",
    [string]$LocalModelPath = "",
    [string]$Configuration = "Debug",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Require-Text {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Name,
        [ValidateRange(1, 2048)][int]$MaximumLength = 256
    )

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text) -or $text.Length -gt $MaximumLength) {
        throw "$Name must contain between 1 and $MaximumLength characters."
    }
    return $text
}

function Require-Decimal {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Name,
        [decimal]$MinimumExclusive = 0,
        [decimal]$MaximumInclusive = 1000
    )

    $number = [decimal]$Value
    if ($number -le $MinimumExclusive -or $number -gt $MaximumInclusive) {
        throw "$Name must be greater than $MinimumExclusive and no greater than $MaximumInclusive."
    }
    return $number
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ProfilePath)) {
    $ProfilePath = Join-Path $repositoryRoot "config\runpod-benchmark.a763.json"
}
$ProfilePath = [System.IO.Path]::GetFullPath($ProfilePath)
if (-not (Test-Path -LiteralPath $ProfilePath -PathType Leaf)) {
    throw "RunPod campaign profile not found: $ProfilePath"
}

$profileBytes = [System.IO.File]::ReadAllBytes($ProfilePath)
$profileSha256 = (Get-FileHash -LiteralPath $ProfilePath -Algorithm SHA256).Hash
$profile = [System.Text.Encoding]::UTF8.GetString($profileBytes) | ConvertFrom-Json

if ([string]$profile.schemaVersion -ne "saaia-runpod-benchmark-profile-v1") {
    throw "Unsupported RunPod campaign profile schema: $($profile.schemaVersion)"
}
if ([string]$profile.provider -ne "RunPod") {
    throw "The campaign profile provider must be RunPod."
}

$campaignId = Require-Text $profile.campaignId "campaignId"
$candidateKind = Require-Text $profile.candidateKind "candidateKind"
$baseUrl = Require-Text $profile.baseUrl "baseUrl" 2048
$modelId = Require-Text $profile.modelId "modelId"
$providerRuntime = Require-Text $profile.providerRuntime "providerRuntime"
$runtimeProfile = Require-Text $profile.runtimeProfile "runtimeProfile"
$quantization = Require-Text $profile.quantization "quantization"

$parsedBaseUrl = $null
if (-not [Uri]::TryCreate($baseUrl, [UriKind]::Absolute, [ref]$parsedBaseUrl) -or
    $parsedBaseUrl.Scheme -ne "https" -or
    -not [string]::IsNullOrWhiteSpace($parsedBaseUrl.UserInfo) -or
    -not [string]::IsNullOrWhiteSpace($parsedBaseUrl.Query) -or
    -not [string]::IsNullOrWhiteSpace($parsedBaseUrl.Fragment)) {
    throw "baseUrl must be an absolute HTTPS URI without credentials, query or fragment."
}

$contextSize = [int]$profile.contextSize
if ($contextSize -lt 4096 -or $contextSize -gt 1048576) {
    throw "contextSize must be between 4096 and 1048576 tokens."
}
$hourlyCostUsd = [decimal]$profile.hourlyCostUsd
if ($candidateKind -eq "public-endpoint" -and $hourlyCostUsd -ne 0) {
    throw "A public token-priced endpoint must not declare an hourly cost."
}

$inputPrice = Require-Decimal $profile.pricingUsdPerMillionTokens.input `
    "pricingUsdPerMillionTokens.input"
$cachedInputPrice = Require-Decimal $profile.pricingUsdPerMillionTokens.cachedInput `
    "pricingUsdPerMillionTokens.cachedInput"
$outputPrice = Require-Decimal $profile.pricingUsdPerMillionTokens.output `
    "pricingUsdPerMillionTokens.output"
$authorizedBudget = Require-Decimal $profile.budget.authorizedUsd "budget.authorizedUsd" 0 5
$softLimit = Require-Decimal $profile.budget.softLimitUsd "budget.softLimitUsd" 0 5
$hardLimit = Require-Decimal $profile.budget.hardLimitUsd "budget.hardLimitUsd" 0 5
$maximumCostPerJob = Require-Decimal $profile.budget.maximumCostPerJobUsd `
    "budget.maximumCostPerJobUsd" 0 5
$maximumCallsPerJob = [int]$profile.budget.maximumCallsPerJob
if ($softLimit -gt $hardLimit -or
    $hardLimit -gt $authorizedBudget -or
    $maximumCostPerJob -gt $hardLimit -or
    $maximumCallsPerJob -lt 1 -or
    $maximumCallsPerJob -gt 4) {
    throw "The RunPod campaign budget envelope is invalid."
}

$caseIds = @($profile.execution.caseIds | ForEach-Object {
    Require-Text $_ "execution.caseIds item"
})
if ($caseIds.Count -lt 1 -or $caseIds.Count -gt 4 -or
    @($caseIds | Sort-Object -Unique).Count -ne $caseIds.Count) {
    throw "execution.caseIds must contain between one and four distinct case identifiers."
}
$repetitions = [int]$profile.execution.repetitions
$delayBetweenCasesSeconds = [int]$profile.execution.delayBetweenCasesSeconds
if ($repetitions -lt 1 -or $repetitions -gt 3 -or
    $delayBetweenCasesSeconds -lt 0 -or $delayBetweenCasesSeconds -gt 300) {
    throw "The campaign execution envelope is invalid."
}
if (-not [bool]$profile.dataPolicy.externalContentTransmission -or
    -not [bool]$profile.dataPolicy.externalMetadataTransmission -or
    -not [bool]$profile.dataPolicy.requiresExplicitAuthorization) {
    throw "The RunPod profile must disclose external content and metadata transmission and require explicit authorization."
}

$repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryCommit)) {
    throw "Unable to resolve the repository commit for the preflight seal."
}
$repositoryTrackedDirty = @(
    & git -C $repositoryRoot status --porcelain --untracked-files=no 2>$null).Count -gt 0

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\runpod-profile-preflight-$stamp"
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null

$preflightPath = Join-Path $ArtifactDirectory "preflight-seal.json"
[ordered]@{
    schemaVersion = "saaia-runpod-campaign-preflight-v1"
    validatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    verdict = "VALID_CONFIGURATION_NO_EXTERNAL_CALL"
    campaignId = $campaignId
    provider = "runpod-bench"
    candidateKind = $candidateKind
    endpointScheme = $parsedBaseUrl.Scheme
    endpointHost = $parsedBaseUrl.Host
    endpointPath = $parsedBaseUrl.AbsolutePath
    modelId = $modelId
    providerRuntime = $providerRuntime
    runtimeProfile = $runtimeProfile
    gpu = [string]$profile.gpu
    quantization = $quantization
    modelSha256 = [string]$profile.modelSha256
    contextSize = $contextSize
    hourlyCostUsd = $hourlyCostUsd
    inputUsdPerMillionTokens = $inputPrice
    cachedInputUsdPerMillionTokens = $cachedInputPrice
    outputUsdPerMillionTokens = $outputPrice
    authorizedBudgetUsd = $authorizedBudget
    softLimitUsd = $softLimit
    hardLimitUsd = $hardLimit
    maximumCostPerJobUsd = $maximumCostPerJob
    maximumCallsPerJob = $maximumCallsPerJob
    caseIds = $caseIds
    repetitions = $repetitions
    expectedJobs = $caseIds.Count * $repetitions
    maximumProviderCallsByEnvelope = $caseIds.Count * $repetitions * $maximumCallsPerJob
    delayBetweenCasesSeconds = $delayBetweenCasesSeconds
    profilePath = [System.IO.Path]::GetRelativePath($repositoryRoot, $ProfilePath)
    profileSha256 = $profileSha256
    repositoryCommit = $repositoryCommit
    repositoryTrackedDirty = $repositoryTrackedDirty
    externalContentTransmission = $true
    externalMetadataTransmission = $true
    explicitAuthorizationRequired = $true
    executeRequested = [bool]$Execute
    externalContentAuthorized = [bool]$ExternalContentAuthorized
    externalCallExecutedByPreflight = $false
    secretReadByPreflight = $false
    productStatus = "TESTE_NON_APPROUVE"
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $preflightPath -Encoding utf8

if (-not $Execute) {
    Write-Output "RunPod campaign profile is valid. No key was read and no external call was executed. Artifact: $ArtifactDirectory"
    exit 0
}

if (-not $ExternalContentAuthorized) {
    throw "Execute requires -ExternalContentAuthorized because prompts and selected evidence leave SAAIA."
}
if ([string]::IsNullOrWhiteSpace($ServerEnvPath)) {
    throw "Execute requires ServerEnvPath."
}

$runArtifactDirectory = Join-Path $ArtifactDirectory "product-path"
$runner = Join-Path $PSScriptRoot "test-advanced-product-path-runpod.ps1"
$runnerArguments = @{
    ServerEnvPath = $ServerEnvPath
    AuthorizedBudgetUsd = $authorizedBudget
    ReferenceBackendUrl = $ReferenceBackendUrl
    BackendPort = $BackendPort
    Ids = ($caseIds -join ',')
    Repetitions = $repetitions
    DelayBetweenCasesSeconds = $delayBetweenCasesSeconds
    BaseUrl = $baseUrl
    ModelId = $modelId
    SoftLimitUsd = $softLimit
    HardLimitUsd = $hardLimit
    MaximumCostPerJobUsd = $maximumCostPerJob
    MaximumCallsPerJob = $maximumCallsPerJob
    InputUsdPerMillionTokens = $inputPrice
    CachedInputUsdPerMillionTokens = $cachedInputPrice
    OutputUsdPerMillionTokens = $outputPrice
    ProviderRuntime = $providerRuntime
    RuntimeProfile = $runtimeProfile
    Gpu = [string]$profile.gpu
    Quantization = $quantization
    ModelSha256 = [string]$profile.modelSha256
    ContextSize = $contextSize
    HourlyCostUsd = $hourlyCostUsd
    Configuration = $Configuration
    Platform = $Platform
    ArtifactDirectory = $runArtifactDirectory
}
if (-not [string]::IsNullOrWhiteSpace($LocalLlmExePath)) {
    $runnerArguments.LocalLlmExePath = $LocalLlmExePath
}
if (-not [string]::IsNullOrWhiteSpace($LocalModelPath)) {
    $runnerArguments.LocalModelPath = $LocalModelPath
}

& $runner @runnerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Profile-driven RunPod campaign failed with exit code $LASTEXITCODE."
}

Write-Output "Profile-driven RunPod campaign completed. Artifact: $ArtifactDirectory"
