[CmdletBinding()]
param(
    [string]$ProfilePath = "",
    [string]$ArtifactDirectory = "",
    [switch]$Execute,
    [switch]$ExternalContentAuthorized,
    [ValidateSet("Probe", "MealGrid", "FullBank")]
    [string]$Stage = "Probe",
    [switch]$FullBankAuthorized,
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

function Get-OptionalProfileValue {
    param(
        [object]$Object,
        [string]$Name,
        [object]$Default
    )

    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
}

function Get-CompatibleRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $normalizedBase = [System.IO.Path]::GetFullPath($BasePath)
    $normalizedTarget = [System.IO.Path]::GetFullPath($TargetPath)
    $directorySeparators = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $baseUri = [Uri]($normalizedBase.TrimEnd($directorySeparators) +
        [System.IO.Path]::DirectorySeparatorChar)
    $targetUri = [Uri]$normalizedTarget

    if (-not [string]::Equals(
            $baseUri.Scheme,
            $targetUri.Scheme,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return $normalizedTarget
    }

    $relativeUri = $baseUri.MakeRelativeUri($targetUri)
    return [Uri]::UnescapeDataString($relativeUri.ToString()).Replace(
        [System.IO.Path]::AltDirectorySeparatorChar,
        [System.IO.Path]::DirectorySeparatorChar)
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

$profileSchemaVersion = [string]$profile.schemaVersion
if ($profileSchemaVersion -notin @(
        "saaia-runpod-benchmark-profile-v1",
        "saaia-runpod-benchmark-profile-v2")) {
    throw "Unsupported RunPod campaign profile schema: $($profile.schemaVersion)"
}
$isStagedProfile = $profileSchemaVersion -eq "saaia-runpod-benchmark-profile-v2"
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
$advanced = Get-OptionalProfileValue $profile "advancedAnalysis" $null
$reasoningEffort = [string](Get-OptionalProfileValue $advanced "reasoningEffort" "low")
$synthesisReasoningEffort = [string](Get-OptionalProfileValue $advanced "synthesisReasoningEffort" "")
$synthesisPromptStyle = [string](Get-OptionalProfileValue $advanced "synthesisPromptStyle" "contract")
$maximumProviderHttpAttempts = [int](Get-OptionalProfileValue $advanced "maximumProviderHttpAttempts" 3)
$maximumJobAttempts = [int](Get-OptionalProfileValue $advanced "maximumJobAttempts" 3)
$maximumJobRetryDelayMilliseconds = [int](Get-OptionalProfileValue $advanced "maximumJobRetryDelayMilliseconds" 600000)
$semanticCriticEnabled = [bool](Get-OptionalProfileValue $advanced "semanticCriticEnabled" $false)
$nativeResearchToolsEnabled = [bool](Get-OptionalProfileValue $advanced "nativeResearchToolsEnabled" $false)
$nativeResearchApiProtocol = [string](Get-OptionalProfileValue $advanced "nativeResearchApiProtocol" "chat-completions")
$nativeResearchTopology = [string](Get-OptionalProfileValue $advanced "nativeResearchTopology" "reviewed")
$nativeResearchMaximumHistoryCharacters = [int](Get-OptionalProfileValue $advanced "nativeResearchMaximumHistoryCharacters" 16384)
$nativeResearchWorkspaceEnabled = [bool](Get-OptionalProfileValue $advanced "nativeResearchWorkspaceEnabled" $false)
$nativeCandidateExplorerEnabled = [bool](Get-OptionalProfileValue $advanced "nativeCandidateExplorerEnabled" $false)
$stagedCandidateExplorerEnabled = [bool](Get-OptionalProfileValue $advanced "stagedCandidateExplorerEnabled" $false)
$candidateExplorerReservePerRole = [int](Get-OptionalProfileValue $advanced "candidateExplorerReservePerRole" 2)
$candidateExplorerMaxTokens = [int](Get-OptionalProfileValue $advanced "candidateExplorerMaxTokens" 4096)
$nativeResearchActiveProposalEnabled = [bool](Get-OptionalProfileValue $advanced "nativeResearchActiveProposalEnabled" $false)
$candidateBindingFeedbackEnabled = [bool](Get-OptionalProfileValue $advanced "candidateBindingFeedbackEnabled" $false)
$plannerMaxTokens = [int](Get-OptionalProfileValue $advanced "plannerMaxTokens" 512)
$writerMaxTokens = [int](Get-OptionalProfileValue $advanced "writerMaxTokens" 4096)
$criticMaxTokens = [int](Get-OptionalProfileValue $advanced "criticMaxTokens" 4096)
$maximumEvidencePromptCharacters = [int](Get-OptionalProfileValue $advanced "maximumEvidencePromptCharacters" 14000)
$maximumToolCalls = [int](Get-OptionalProfileValue $advanced "maximumToolCalls" 32)
$spendAuthorization = Get-OptionalProfileValue $profile "spendAuthorization" $null
$defaultSpendAuthorizationStatus = if ($isStagedProfile) {
    "PROPOSED_PENDING_USER_CONFIRMATION"
} else {
    "LEGACY_PROFILE"
}
$spendAuthorizationStatus = [string](Get-OptionalProfileValue $spendAuthorization "status" `
        $defaultSpendAuthorizationStatus)

if ($reasoningEffort -notin @("low", "medium", "high") -or
    $synthesisReasoningEffort -notin @("", "low", "medium", "high") -or
    $synthesisPromptStyle -notin @("contract", "agent")) {
    throw "The advanced reasoning profile is invalid."
}
if ($maximumProviderHttpAttempts -lt 1 -or $maximumProviderHttpAttempts -gt 3 -or
    $maximumJobAttempts -lt 1 -or $maximumJobAttempts -gt 20 -or
    $maximumJobRetryDelayMilliseconds -lt 1000 -or
    $maximumJobRetryDelayMilliseconds -gt 900000 -or
    $nativeResearchMaximumHistoryCharacters -lt 16384 -or
    $nativeResearchMaximumHistoryCharacters -gt 65536 -or
    $candidateExplorerReservePerRole -lt 0 -or $candidateExplorerReservePerRole -gt 8 -or
    $candidateExplorerMaxTokens -lt 512 -or $candidateExplorerMaxTokens -gt 16384 -or
    $plannerMaxTokens -lt 256 -or $plannerMaxTokens -gt 4096 -or
    $writerMaxTokens -lt 512 -or $writerMaxTokens -gt 16384 -or
    $criticMaxTokens -lt 512 -or $criticMaxTokens -gt 16384 -or
    $maximumEvidencePromptCharacters -lt 8000 -or $maximumEvidencePromptCharacters -gt 1000000 -or
    $maximumToolCalls -lt 1 -or $maximumToolCalls -gt 128) {
    throw "The advanced analysis envelope is invalid."
}
if ($nativeResearchApiProtocol -ne "chat-completions") {
    throw "RunPod profiles must use the OpenAI-compatible chat-completions protocol."
}
if ($stagedCandidateExplorerEnabled -and -not $nativeCandidateExplorerEnabled) {
    throw "Staged Candidate Explorer requires Native Candidate Explorer."
}
if ($isStagedProfile -and (
        -not $semanticCriticEnabled -or
        -not $nativeResearchToolsEnabled -or
        $nativeResearchTopology -ne "agent" -or
        $nativeResearchMaximumHistoryCharacters -lt 32768 -or
        -not $nativeResearchWorkspaceEnabled -or
        -not $nativeCandidateExplorerEnabled -or
        -not $stagedCandidateExplorerEnabled -or
        -not $nativeResearchActiveProposalEnabled -or
        -not $candidateBindingFeedbackEnabled)) {
    throw "A staged RunPod profile must preserve the complete agentic research and verification topology."
}
if ($isStagedProfile -and $spendAuthorizationStatus -notin @(
        "PROPOSED_PENDING_USER_CONFIRMATION",
        "AUTHORIZED")) {
    throw "A v2 RunPod profile must declare a pending or authorized spend status."
}
if ($isStagedProfile -and $spendAuthorizationStatus -eq "AUTHORIZED") {
    $authorizedAtText = [string](Get-OptionalProfileValue $spendAuthorization `
            "authorizedAtUtc" "")
    $authorizationBasis = [string](Get-OptionalProfileValue $spendAuthorization `
            "authorizationBasis" "")
    $authorizedAt = [DateTimeOffset]::MinValue
    if ([string]::IsNullOrWhiteSpace($authorizationBasis) -or
        -not [DateTimeOffset]::TryParse($authorizedAtText, [ref]$authorizedAt) -or
        $authorizedAt.Offset -ne [TimeSpan]::Zero) {
        throw "An authorized v2 profile requires a UTC authorization timestamp and a non-empty authorization basis."
    }
}

$parsedBaseUrl = $null
if (-not [Uri]::TryCreate($baseUrl, [UriKind]::Absolute, [ref]$parsedBaseUrl) -or
    $parsedBaseUrl.Scheme -ne "https" -or
    -not [string]::IsNullOrWhiteSpace($parsedBaseUrl.UserInfo) -or
    -not [string]::IsNullOrWhiteSpace($parsedBaseUrl.Query) -or
    -not [string]::IsNullOrWhiteSpace($parsedBaseUrl.Fragment)) {
    throw "baseUrl must be an absolute HTTPS URI without credentials, query or fragment."
}
$chatCompletionsUrl = $baseUrl.TrimEnd('/') + "/chat/completions"
$parsedChatCompletionsUrl = $null
if (-not [Uri]::TryCreate(
        $chatCompletionsUrl,
        [UriKind]::Absolute,
        [ref]$parsedChatCompletionsUrl) -or
    $parsedChatCompletionsUrl.Scheme -ne "https" -or
    $parsedChatCompletionsUrl.Host -ne $parsedBaseUrl.Host) {
    throw "The profile does not produce a valid HTTPS chat completions URL."
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
$authorizedBudget = Require-Decimal $profile.budget.authorizedUsd "budget.authorizedUsd" 0 25
$softLimit = Require-Decimal $profile.budget.softLimitUsd "budget.softLimitUsd" 0 25
$hardLimit = Require-Decimal $profile.budget.hardLimitUsd "budget.hardLimitUsd" 0 25
$maximumCostPerJob = Require-Decimal $profile.budget.maximumCostPerJobUsd `
    "budget.maximumCostPerJobUsd" 0 25
$maximumCallsPerJob = [int]$profile.budget.maximumCallsPerJob
if ($softLimit -gt $hardLimit -or
    $hardLimit -gt $authorizedBudget -or
    $maximumCostPerJob -gt $hardLimit -or
    $maximumCallsPerJob -lt 1 -or
    $maximumCallsPerJob -gt 128 -or
    ($isStagedProfile -and $maximumCallsPerJob -lt 5)) {
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
$mealGridCaseId = "A755-ADV-01-meal-grid-5x4"
if ($mealGridCaseId -notin $caseIds) {
    throw "The campaign profile must contain the staged meal-grid case."
}
$stageCaseIds = @(switch ($Stage) {
    "Probe" { @() }
    "MealGrid" { @($mealGridCaseId) }
    "FullBank" { @($caseIds) }
})
$stageRepetitions = switch ($Stage) {
    "Probe" { 0 }
    "MealGrid" { 1 }
    "FullBank" { $repetitions }
}
$stageExpectedJobs = switch ($Stage) {
    "Probe" { 1 }
    default { $stageCaseIds.Count * $stageRepetitions }
}
$stageMaximumCallsPerJob = if ($Stage -eq "Probe") {
    2
} else {
    $maximumCallsPerJob
}
$stageMaximumProviderCalls = switch ($Stage) {
    "Probe" { $stageMaximumCallsPerJob }
    default { $stageExpectedJobs * $stageMaximumCallsPerJob }
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
    schemaVersion = if ($isStagedProfile) {
        "saaia-runpod-campaign-preflight-v2"
    } else {
        "saaia-runpod-campaign-preflight-v1"
    }
    validatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    verdict = "VALID_CONFIGURATION_NO_EXTERNAL_CALL"
    campaignId = $campaignId
    provider = "runpod-bench"
    candidateKind = $candidateKind
    endpointScheme = $parsedBaseUrl.Scheme
    endpointHost = $parsedBaseUrl.Host
    endpointPath = $parsedBaseUrl.AbsolutePath
    chatCompletionsUrl = $parsedChatCompletionsUrl.AbsoluteUri
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
    spendAuthorizationStatus = $spendAuthorizationStatus
    reasoningEffort = $reasoningEffort
    synthesisReasoningEffort = $synthesisReasoningEffort
    synthesisPromptStyle = $synthesisPromptStyle
    maximumProviderHttpAttempts = $maximumProviderHttpAttempts
    maximumJobAttempts = $maximumJobAttempts
    maximumJobRetryDelayMilliseconds = $maximumJobRetryDelayMilliseconds
    semanticCriticEnabled = $semanticCriticEnabled
    nativeResearchToolsEnabled = $nativeResearchToolsEnabled
    nativeResearchApiProtocol = $nativeResearchApiProtocol
    nativeResearchTopology = $nativeResearchTopology
    nativeResearchMaximumHistoryCharacters = $nativeResearchMaximumHistoryCharacters
    nativeResearchWorkspaceEnabled = $nativeResearchWorkspaceEnabled
    nativeCandidateExplorerEnabled = $nativeCandidateExplorerEnabled
    stagedCandidateExplorerEnabled = $stagedCandidateExplorerEnabled
    candidateExplorerReservePerRole = $candidateExplorerReservePerRole
    candidateExplorerMaxTokens = $candidateExplorerMaxTokens
    nativeResearchActiveProposalEnabled = $nativeResearchActiveProposalEnabled
    candidateBindingFeedbackEnabled = $candidateBindingFeedbackEnabled
    plannerMaxTokens = $plannerMaxTokens
    writerMaxTokens = $writerMaxTokens
    criticMaxTokens = $criticMaxTokens
    maximumEvidencePromptCharacters = $maximumEvidencePromptCharacters
    maximumToolCalls = $maximumToolCalls
    caseIds = $caseIds
    repetitions = $repetitions
    expectedJobs = $caseIds.Count * $repetitions
    maximumProviderCallsByEnvelope = $caseIds.Count * $repetitions * $maximumCallsPerJob
    delayBetweenCasesSeconds = $delayBetweenCasesSeconds
    requestedStage = $Stage
    stageUsesSyntheticEvidenceOnly = $Stage -eq "Probe"
    stageCaseIds = $stageCaseIds
    stageRepetitions = $stageRepetitions
    stageExpectedJobs = $stageExpectedJobs
    stageMaximumCallsPerJob = $stageMaximumCallsPerJob
    stageMaximumProviderCallsByEnvelope = $stageMaximumProviderCalls
    fullBankAuthorized = [bool]$FullBankAuthorized
    profilePath = Get-CompatibleRelativePath $repositoryRoot $ProfilePath
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
if ($isStagedProfile -and $spendAuthorizationStatus -ne "AUTHORIZED") {
    throw "Execute requires spendAuthorization.status=AUTHORIZED in the reviewed v2 profile."
}
if ($Stage -eq "FullBank" -and -not $FullBankAuthorized) {
    throw "FullBank execution requires -FullBankAuthorized after review of the staged meal-grid result."
}
if ($Stage -ne "Probe" -and [string]::IsNullOrWhiteSpace($ServerEnvPath)) {
    throw "$Stage execution requires ServerEnvPath."
}

$runArtifactDirectory = Join-Path $ArtifactDirectory $Stage.ToLowerInvariant()
$sharedRunnerArguments = @{
    BaseUrl = $baseUrl
    ModelId = $modelId
    ProviderRuntime = $providerRuntime
    RuntimeProfile = $runtimeProfile
    Gpu = [string]$profile.gpu
    Quantization = $quantization
    ModelSha256 = [string]$profile.modelSha256
    ContextSize = $contextSize
    HourlyCostUsd = $hourlyCostUsd
    AuthorizedBudgetUsd = $authorizedBudget
    SoftLimitUsd = $softLimit
    HardLimitUsd = $hardLimit
    MaximumCostPerJobUsd = $maximumCostPerJob
    MaximumCallsPerJob = $stageMaximumCallsPerJob
    InputUsdPerMillionTokens = $inputPrice
    CachedInputUsdPerMillionTokens = $cachedInputPrice
    OutputUsdPerMillionTokens = $outputPrice
    Configuration = $Configuration
    ArtifactDirectory = $runArtifactDirectory
}

if ($Stage -eq "Probe") {
    $runner = Join-Path $PSScriptRoot "test-advanced-server-provider.ps1"
    $runnerArguments = @{
        Provider = "RunPod"
    }
    foreach ($entry in $sharedRunnerArguments.GetEnumerator()) {
        $runnerArguments[$entry.Key] = $entry.Value
    }
}
else {
    $runner = Join-Path $PSScriptRoot "test-advanced-product-path-runpod.ps1"
    $runnerArguments = @{
        ServerEnvPath = $ServerEnvPath
        ReferenceBackendUrl = $ReferenceBackendUrl
        BackendPort = $BackendPort
        Ids = ($stageCaseIds -join ',')
        Repetitions = $stageRepetitions
        DelayBetweenCasesSeconds = $delayBetweenCasesSeconds
        MaximumJobAttempts = $maximumJobAttempts
        MaximumProviderHttpAttempts = $maximumProviderHttpAttempts
        MaximumJobRetryDelayMilliseconds = $maximumJobRetryDelayMilliseconds
        ReasoningEffort = $reasoningEffort
        SynthesisReasoningEffort = $synthesisReasoningEffort
        SynthesisPromptStyle = $synthesisPromptStyle
        EnableSemanticCritic = $semanticCriticEnabled
        EnableNativeResearchTools = $nativeResearchToolsEnabled
        NativeResearchApiProtocol = $nativeResearchApiProtocol
        NativeResearchTopology = $nativeResearchTopology
        NativeResearchMaximumHistoryCharacters = $nativeResearchMaximumHistoryCharacters
        EnableNativeResearchWorkspace = $nativeResearchWorkspaceEnabled
        EnableNativeCandidateExplorer = $nativeCandidateExplorerEnabled
        EnableStagedCandidateExplorer = $stagedCandidateExplorerEnabled
        CandidateExplorerReservePerRole = $candidateExplorerReservePerRole
        CandidateExplorerMaxTokens = $candidateExplorerMaxTokens
        EnableNativeResearchActiveProposal = $nativeResearchActiveProposalEnabled
        EnableCandidateBindingFeedback = $candidateBindingFeedbackEnabled
        PlannerMaxTokens = $plannerMaxTokens
        WriterMaxTokens = $writerMaxTokens
        CriticMaxTokens = $criticMaxTokens
        MaximumEvidencePromptCharacters = $maximumEvidencePromptCharacters
        MaximumToolCalls = $maximumToolCalls
        Platform = $Platform
    }
    foreach ($entry in $sharedRunnerArguments.GetEnumerator()) {
        $runnerArguments[$entry.Key] = $entry.Value
    }
    if (-not [string]::IsNullOrWhiteSpace($LocalLlmExePath)) {
        $runnerArguments.LocalLlmExePath = $LocalLlmExePath
    }
    if (-not [string]::IsNullOrWhiteSpace($LocalModelPath)) {
        $runnerArguments.LocalModelPath = $LocalModelPath
    }
}

& $runner @runnerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Profile-driven RunPod $Stage stage failed with exit code $LASTEXITCODE."
}

Write-Output "Profile-driven RunPod $Stage stage completed. Artifact: $ArtifactDirectory"
