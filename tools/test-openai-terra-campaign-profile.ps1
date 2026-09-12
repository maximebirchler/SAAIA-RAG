[CmdletBinding()]
param(
    [string]$ProfilePath = "",
    [ValidateSet("Free", "Tier1", "Tier2", "Tier3", "Tier4", "Tier5")]
    [string]$ObservedOrganizationTier = "Free",
    [string]$TierObservedAtUtc = "",
    [switch]$Execute,
    [string]$ServerEnvPath = "",
    [string]$ReferenceBackendUrl = "",
    [ValidateRange(1024, 65535)]
    [int]$BackendPort = 5123,
    [string]$LocalLlmExePath = "",
    [string]$LocalModelPath = "",
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Require-Text {
    param([object]$Value, [string]$Name)
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { throw "$Name is required." }
    return $text
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ProfilePath)) {
    $ProfilePath = Join-Path $repositoryRoot "config\openai-terra-final-campaign.a763.json"
}
$ProfilePath = [System.IO.Path]::GetFullPath($ProfilePath)
if (-not (Test-Path -LiteralPath $ProfilePath -PathType Leaf)) {
    throw "Campaign profile not found: $ProfilePath"
}

$profile = Get-Content -LiteralPath $ProfilePath -Raw | ConvertFrom-Json
if ([string]$profile.schemaVersion -ne "saaia-openai-terra-final-campaign-v1") {
    throw "Unsupported campaign profile schema."
}
if ([string]$profile.provider.mode -ne "OpenAI" -or
    [string]$profile.provider.model -ne "gpt-5.6-terra") {
    throw "This runner accepts only the registered OpenAI Terra profile."
}

$bankRelativePath = Require-Text $profile.bank.path "bank.path"
$bankPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $bankRelativePath))
if (-not (Test-Path -LiteralPath $bankPath -PathType Leaf)) {
    throw "Registered question bank not found: $bankPath"
}
$observedBankHash = (Get-FileHash -LiteralPath $bankPath -Algorithm SHA256).Hash
$expectedBankHash = (Require-Text $profile.bank.sha256 "bank.sha256").ToUpperInvariant()
$providerConfigRelativePath = Require-Text `
    $profile.localRouter.providerConfigPath `
    "localRouter.providerConfigPath"
$providerConfigPath = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot $providerConfigRelativePath))
$providerConfigHash = if (Test-Path -LiteralPath $providerConfigPath -PathType Leaf) {
    (Get-FileHash -LiteralPath $providerConfigPath -Algorithm SHA256).Hash
} else { $null }

if ([string]::IsNullOrWhiteSpace($LocalLlmExePath)) {
    $runtimeRoot = Join-Path $env:LOCALAPPDATA "SAAIA\llm\runtime"
    $runtime = Get-ChildItem `
        -LiteralPath $runtimeRoot `
        -Filter "llama-server.exe" `
        -File `
        -Recurse `
        -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -ne $runtime) { $LocalLlmExePath = $runtime.FullName }
}
if ([string]::IsNullOrWhiteSpace($LocalModelPath)) {
    $LocalModelPath = Join-Path $env:LOCALAPPDATA `
        "SAAIA\Models\Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf"
}
$localRuntimeHash = if (-not [string]::IsNullOrWhiteSpace($LocalLlmExePath) -and
    (Test-Path -LiteralPath $LocalLlmExePath -PathType Leaf)) {
    $LocalLlmExePath = [System.IO.Path]::GetFullPath($LocalLlmExePath)
    (Get-FileHash -LiteralPath $LocalLlmExePath -Algorithm SHA256).Hash
} else { $null }
$localModelHash = if (-not [string]::IsNullOrWhiteSpace($LocalModelPath) -and
    (Test-Path -LiteralPath $LocalModelPath -PathType Leaf)) {
    $LocalModelPath = [System.IO.Path]::GetFullPath($LocalModelPath)
    (Get-FileHash -LiteralPath $LocalModelPath -Algorithm SHA256).Hash
} else { $null }

$head = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
$branch = (& git -C $repositoryRoot rev-parse --abbrev-ref HEAD 2>$null).Trim()
$trackedDirty = @(& git -C $repositoryRoot status --porcelain --untracked-files=no 2>$null).Count -gt 0
$minimumCommit = Require-Text $profile.repository.minimumSemanticFixCommit "repository.minimumSemanticFixCommit"
& git -C $repositoryRoot merge-base --is-ancestor $minimumCommit HEAD 2>$null
$containsMinimumCommit = $LASTEXITCODE -eq 0

$tierObservation = $null
$tierObservationAgeMinutes = $null
$tierTimestampHasExplicitOffset = $TierObservedAtUtc -match '(?:[zZ]|[+-]\d{2}:\d{2})$'
if (-not [string]::IsNullOrWhiteSpace($TierObservedAtUtc) -and
    $tierTimestampHasExplicitOffset) {
    try {
        $tierObservation = [DateTimeOffset]::Parse(
            $TierObservedAtUtc,
            [Globalization.CultureInfo]::InvariantCulture)
        $tierObservationAgeMinutes = ([DateTimeOffset]::UtcNow - $tierObservation).TotalMinutes
    }
    catch {
        $tierObservation = $null
    }
}
$minimumTierSatisfied = $ObservedOrganizationTier -match '^Tier[1-5]$'
$maximumTierAgeMinutes = [double]$profile.provider.freshTierObservationMaximumAgeMinutes
$freshTierObservation = $null -ne $tierObservation -and
    $tierTimestampHasExplicitOffset -and
    $tierObservationAgeMinutes -ge -2 -and
    $tierObservationAgeMinutes -le $maximumTierAgeMinutes

$caseIds = @($profile.bank.caseIds | ForEach-Object { Require-Text $_ "bank.caseIds item" })
$campaignKind = if ($null -eq $profile.PSObject.Properties["campaignKind"]) {
    "final-acceptance"
} else {
    Require-Text $profile.campaignKind "campaignKind"
}
$blockingReasons = @()
if ($branch -ne [string]$profile.repository.expectedBranch) { $blockingReasons += "unexpected_branch" }
if ($trackedDirty) { $blockingReasons += "tracked_worktree_dirty" }
if (-not $containsMinimumCommit) { $blockingReasons += "semantic_fix_commit_missing" }
if ($observedBankHash -ne $expectedBankHash) { $blockingReasons += "question_bank_hash_mismatch" }
if ([string]::IsNullOrWhiteSpace($providerConfigHash)) {
    $blockingReasons += "provider_configuration_missing"
} elseif ($providerConfigHash -ne [string]$profile.localRouter.providerConfigSha256) {
    $blockingReasons += "provider_configuration_hash_mismatch"
}
if ([string]::IsNullOrWhiteSpace($localRuntimeHash)) {
    $blockingReasons += "local_llm_runtime_missing"
} elseif ($localRuntimeHash -ne [string]$profile.localRouter.runtimeSha256) {
    $blockingReasons += "local_llm_runtime_hash_mismatch"
}
if ([string]::IsNullOrWhiteSpace($localModelHash)) {
    $blockingReasons += "local_model_missing"
} elseif ($localModelHash -ne [string]$profile.localRouter.modelSha256) {
    $blockingReasons += "local_model_hash_mismatch"
}
if (-not [string]::IsNullOrWhiteSpace($ReferenceBackendUrl) -and
    $ReferenceBackendUrl.TrimEnd('/') -ne ([string]$profile.execution.referenceBackendUrl).TrimEnd('/')) {
    $blockingReasons += "reference_backend_profile_mismatch"
}
if ($BackendPort -ne [int]$profile.execution.backendPort) {
    $blockingReasons += "backend_port_profile_mismatch"
}
if (-not $minimumTierSatisfied) { $blockingReasons += "paid_tier_not_observed" }
if (-not $freshTierObservation) { $blockingReasons += "paid_tier_observation_missing_or_stale" }
if (($campaignKind -eq "final-acceptance" -and $caseIds.Count -ne 4) -or
    ($campaignKind -eq "targeted-causal" -and
        ($caseIds.Count -lt 1 -or $caseIds.Count -gt 4)) -or
    $campaignKind -notin @("final-acceptance", "targeted-causal") -or
    @($caseIds | Sort-Object -Unique).Count -ne $caseIds.Count) {
    $blockingReasons += "registered_case_set_invalid"
}

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\reprise-pc-20260908\a763-provider-comparison\terra-final-profile-preflight-$stamp"
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null

$preflight = [ordered]@{
    schemaVersion = "saaia-openai-terra-campaign-profile-preflight-v1"
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    executeRequested = [bool]$Execute
    profilePath = $ProfilePath
    profileSha256 = (Get-FileHash -LiteralPath $ProfilePath -Algorithm SHA256).Hash
    repositoryCommit = $head
    repositoryBranch = $branch
    repositoryTrackedDirty = $trackedDirty
    minimumSemanticFixCommit = $minimumCommit
    containsMinimumSemanticFixCommit = $containsMinimumCommit
    bankPath = $bankPath
    bankSha256 = $observedBankHash
    bankHashMatches = $observedBankHash -eq $expectedBankHash
    providerConfigPath = $providerConfigPath
    providerConfigSha256 = $providerConfigHash
    providerConfigHashMatches = $providerConfigHash -eq [string]$profile.localRouter.providerConfigSha256
    campaignKind = $campaignKind
    localLlmRuntimePath = $LocalLlmExePath
    localLlmRuntimeSha256 = $localRuntimeHash
    localLlmRuntimeHashMatches = $localRuntimeHash -eq [string]$profile.localRouter.runtimeSha256
    localModelPath = $LocalModelPath
    localModelSha256 = $localModelHash
    localModelHashMatches = $localModelHash -eq [string]$profile.localRouter.modelSha256
    observedOrganizationTier = $ObservedOrganizationTier
    tierObservedAtUtc = if ($null -eq $tierObservation) { $null } else { $tierObservation.ToString("o") }
    tierObservationAgeMinutes = $tierObservationAgeMinutes
    tierTimestampHasExplicitOffset = $tierTimestampHasExplicitOffset
    maximumTierObservationAgeMinutes = $maximumTierAgeMinutes
    paidTierGateSatisfied = $minimumTierSatisfied -and $freshTierObservation
    selectedIds = $caseIds
    repetitions = [int]$profile.bank.repetitions
    referenceBackendUrl = [string]$profile.execution.referenceBackendUrl
    backendPort = [int]$profile.execution.backendPort
    delayBetweenCasesSeconds = [int]$profile.execution.delayBetweenCasesSeconds
    maximumJobAttempts = [int]$profile.execution.maximumJobAttempts
    maximumJobRetryDelayMilliseconds = [int]$profile.execution.maximumJobRetryDelayMilliseconds
    maximumCostPerJobUsd = [decimal]$profile.budget.maximumCostPerJobUsd
    maximumCampaignCostImpliedByPerJobCapsUsd = [decimal]$profile.budget.maximumCampaignCostImpliedByPerJobCapsUsd
    blockingReasons = $blockingReasons
    executionState = "NOT_STARTED"
    executionStartedAtUtc = $null
    executionEndedAtUtc = $null
    externalCallMayHaveOccurred = $false
    externalCallExecuted = $false
    productStatus = "TESTE_NON_APPROUVE"
}
$preflightPath = Join-Path $ArtifactDirectory "preflight-seal.json"
$preflight | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $preflightPath -Encoding utf8

if (-not $Execute) {
    Write-Output "OpenAI Terra campaign profile checked without model call. Artifact: $ArtifactDirectory"
    Write-Output ("Execution gate: " + $(if ($blockingReasons.Count -eq 0) { "READY" } else { "BLOCKED: " + ($blockingReasons -join ",") }))
    exit 0
}
if ($blockingReasons.Count -gt 0) {
    throw "OpenAI Terra campaign execution is blocked: $($blockingReasons -join ', ')"
}
if ([string]::IsNullOrWhiteSpace($ServerEnvPath)) {
    $ServerEnvPath = [string]$profile.execution.serverEnvironmentPath
}
if ([string]::IsNullOrWhiteSpace($ReferenceBackendUrl)) {
    $ReferenceBackendUrl = [string]$profile.execution.referenceBackendUrl
}

$runArtifactDirectory = Join-Path $ArtifactDirectory "run"
$runner = Join-Path $PSScriptRoot "test-advanced-product-path-openai.ps1"
$preflight.executionState = "STARTED_EXTERNAL_CALLS_POSSIBLE"
$preflight.executionStartedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
$preflight.externalCallMayHaveOccurred = $true
$preflight | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $preflightPath -Encoding utf8
try {
    & $runner `
        -ServerEnvPath $ServerEnvPath `
        -ReferenceBackendUrl $ReferenceBackendUrl `
        -BackendPort $BackendPort `
        -Ids ($caseIds -join ',') `
        -Repetitions ([int]$profile.bank.repetitions) `
        -DelayBetweenCasesSeconds ([int]$profile.execution.delayBetweenCasesSeconds) `
        -MaximumJobAttempts ([int]$profile.execution.maximumJobAttempts) `
        -MaximumJobRetryDelayMilliseconds ([int]$profile.execution.maximumJobRetryDelayMilliseconds) `
        -OpenAiModel ([string]$profile.provider.model) `
        -ObservedOrganizationTier $ObservedOrganizationTier `
        -TierObservedAtUtc $TierObservedAtUtc `
        -AuthorizedBudgetUsd ([decimal]$profile.budget.authorizedLifetimeUsd) `
        -SoftLimitUsd ([decimal]$profile.budget.softLimitUsd) `
        -HardLimitUsd ([decimal]$profile.budget.hardStopUsd) `
        -MaximumCostPerJobUsd ([decimal]$profile.budget.maximumCostPerJobUsd) `
        -MaximumCallsPerJob ([int]$profile.budget.maximumCallsPerJob) `
        -LocalLlmExePath $LocalLlmExePath `
        -LocalModelPath $LocalModelPath `
        -Configuration ([string]$profile.execution.configuration) `
        -ArtifactDirectory $runArtifactDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "OpenAI Terra profile campaign failed with exit code $LASTEXITCODE."
    }
}
catch {
    $preflight.executionState = "FAILED_OR_INTERRUPTED_EXTERNAL_CALLS_POSSIBLE"
    $preflight.executionEndedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    $preflight | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $preflightPath -Encoding utf8
    throw
}
$preflight.executionState = "COMPLETED"
$preflight.executionEndedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
$preflight.externalCallExecuted = $true
$preflight | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $preflightPath -Encoding utf8

Write-Output "OpenAI Terra profile campaign completed. Artifact: $ArtifactDirectory"
