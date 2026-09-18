[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerEnvPath,
    [string]$ReferenceBackendUrl = "http://saaia-server:5122",
    [ValidateRange(1024, 65535)]
    [int]$BackendPort = 5123,
    [string]$Ids = "A755-ADV-01-meal-grid-5x4",
    [string]$BankPath = "",
    [ValidateRange(1, 3)]
    [int]$Repetitions = 1,
    [ValidateRange(0, 300)]
    [int]$DelayBetweenCasesSeconds = 0,
    [ValidateRange(1, 20)]
    [int]$MaximumJobAttempts = 3,
    [ValidateRange(1000, 900000)]
    [int]$MaximumJobRetryDelayMilliseconds = 600000,
    [ValidateSet("gpt-5.6-terra", "gpt-5.6-luna")]
    [string]$OpenAiModel = "gpt-5.6-terra",
    [ValidateSet("low", "medium", "high")]
    [string]$ReasoningEffort = "low",
    [ValidateSet("", "low", "medium", "high")]
    [string]$SynthesisReasoningEffort = "",
    [ValidateSet("contract", "agent")]
    [string]$SynthesisPromptStyle = "contract",
    [ValidateRange(1, 3)]
    [int]$MaximumProviderHttpAttempts = 3,
    [ValidateSet("Free", "Tier1", "Tier2", "Tier3", "Tier4", "Tier5")]
    [string]$ObservedOrganizationTier = "Free",
    [string]$TierObservedAtUtc = "",
    [ValidateRange(0.01, 1000)]
    [decimal]$AuthorizedBudgetUsd = 25,
    [decimal]$SoftLimitUsd = 20,
    [decimal]$HardLimitUsd = 24,
    [decimal]$MaximumCostPerJobUsd = 0.50,
    [ValidateRange(1, 32)]
    [int]$MaximumCallsPerJob = 4,
    [switch]$EnableSemanticCritic,
    [switch]$EnableNativeResearchTools,
    [ValidateSet("chat-completions", "responses")]
    [string]$NativeResearchApiProtocol = "chat-completions",
    [ValidateSet("reviewed", "agent")]
    [string]$NativeResearchTopology = "reviewed",
    [ValidateRange(16384, 65536)]
    [int]$NativeResearchMaximumHistoryCharacters = 16384,
    [switch]$EnableNativeResearchWorkspace,
    [switch]$EnableNativeCandidateExplorer,
    [ValidateRange(0, 8)]
    [int]$CandidateExplorerReservePerRole = 2,
    [ValidateRange(512, 16384)]
    [int]$CandidateExplorerMaxTokens = 4096,
    [switch]$EnableNativeResearchActiveProposal,
    [switch]$EnableCandidateBindingFeedback,
    [ValidateRange(256, 4096)]
    [int]$PlannerMaxTokens = 512,
    [ValidateRange(512, 16384)]
    [int]$WriterMaxTokens = 4096,
    [ValidateRange(512, 16384)]
    [int]$CriticMaxTokens = 4096,
    [string]$LocalLlmExePath = "",
    [string]$LocalModelPath = "",
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$runner = Join-Path $PSScriptRoot "test-advanced-product-path-provider.ps1"
& $runner `
    -ServerEnvPath $ServerEnvPath `
    -Provider OpenAI `
    -ReferenceBackendUrl $ReferenceBackendUrl `
    -BackendPort $BackendPort `
    -Ids $Ids `
    -BankPath $BankPath `
    -Repetitions $Repetitions `
    -DelayBetweenCasesSeconds $DelayBetweenCasesSeconds `
    -MaximumJobAttempts $MaximumJobAttempts `
    -MaximumJobRetryDelayMilliseconds $MaximumJobRetryDelayMilliseconds `
    -ModelId $OpenAiModel `
    -ReasoningEffort $ReasoningEffort `
    -SynthesisReasoningEffort $SynthesisReasoningEffort `
    -SynthesisPromptStyle $SynthesisPromptStyle `
    -MaximumProviderHttpAttempts $MaximumProviderHttpAttempts `
    -ProviderAccountTier $ObservedOrganizationTier `
    -ProviderAccountTierObservedAtUtc $TierObservedAtUtc `
    -AuthorizedBudgetUsd $AuthorizedBudgetUsd `
    -SoftLimitUsd $SoftLimitUsd `
    -HardLimitUsd $HardLimitUsd `
    -MaximumCostPerJobUsd $MaximumCostPerJobUsd `
    -MaximumCallsPerJob $MaximumCallsPerJob `
    -EnableSemanticCritic:$EnableSemanticCritic `
    -EnableNativeResearchTools:$EnableNativeResearchTools `
    -NativeResearchApiProtocol $NativeResearchApiProtocol `
    -NativeResearchTopology $NativeResearchTopology `
    -NativeResearchMaximumHistoryCharacters $NativeResearchMaximumHistoryCharacters `
    -EnableNativeResearchWorkspace:$EnableNativeResearchWorkspace `
    -EnableNativeCandidateExplorer:$EnableNativeCandidateExplorer `
    -CandidateExplorerReservePerRole $CandidateExplorerReservePerRole `
    -CandidateExplorerMaxTokens $CandidateExplorerMaxTokens `
    -EnableNativeResearchActiveProposal:$EnableNativeResearchActiveProposal `
    -EnableCandidateBindingFeedback:$EnableCandidateBindingFeedback `
    -PlannerMaxTokens $PlannerMaxTokens `
    -WriterMaxTokens $WriterMaxTokens `
    -CriticMaxTokens $CriticMaxTokens `
    -LocalLlmExePath $LocalLlmExePath `
    -LocalModelPath $LocalModelPath `
    -Configuration $Configuration `
    -Platform $Platform `
    -ArtifactDirectory $ArtifactDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Advanced OpenAI product-path runner failed with exit code $LASTEXITCODE."
}
