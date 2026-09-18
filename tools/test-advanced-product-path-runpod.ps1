[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerEnvPath,
    [Parameter(Mandatory = $true)]
    [ValidateRange(0.01, 1000)]
    [decimal]$AuthorizedBudgetUsd,
    [string]$ReferenceBackendUrl = "http://saaia-server:5122",
    [ValidateRange(1024, 65535)]
    [int]$BackendPort = 5123,
    [string]$Ids = "A755-ADV-01-meal-grid-5x4,A755-ADV-02-five-student-meals-fr,A755-ADV-03-explicit-document-comparison,A755-ADV-04-nist-seven-points",
    [string]$BankPath = "",
    [ValidateRange(1, 3)]
    [int]$Repetitions = 1,
    [ValidateRange(0, 300)]
    [int]$DelayBetweenCasesSeconds = 0,
    [ValidateRange(1, 20)]
    [int]$MaximumJobAttempts = 3,
    [ValidateRange(1, 3)]
    [int]$MaximumProviderHttpAttempts = 3,
    [ValidateRange(1000, 900000)]
    [int]$MaximumJobRetryDelayMilliseconds = 600000,
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,
    [Parameter(Mandatory = $true)]
    [string]$ModelId,
    [ValidateSet("low", "medium", "high")]
    [string]$ReasoningEffort = "low",
    [ValidateSet("", "low", "medium", "high")]
    [string]$SynthesisReasoningEffort = "",
    [ValidateSet("contract", "agent")]
    [string]$SynthesisPromptStyle = "contract",
    [decimal]$SoftLimitUsd = 0,
    [decimal]$HardLimitUsd = 0,
    [decimal]$MaximumCostPerJobUsd = 0,
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
    [string]$DevelopmentTraceDirectory = "",
    [Parameter(Mandatory = $true)]
    [decimal]$InputUsdPerMillionTokens,
    [Parameter(Mandatory = $true)]
    [decimal]$CachedInputUsdPerMillionTokens,
    [Parameter(Mandatory = $true)]
    [decimal]$OutputUsdPerMillionTokens,
    [Parameter(Mandatory = $true)]
    [string]$ProviderRuntime,
    [string]$RuntimeProfile = "",
    [string]$Gpu = "",
    [string]$Quantization = "",
    [string]$ModelSha256 = "",
    [int]$ContextSize = 0,
    [decimal]$HourlyCostUsd = 0,
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
    -Provider RunPod `
    -ReferenceBackendUrl $ReferenceBackendUrl `
    -BackendPort $BackendPort `
    -Ids $Ids `
    -BankPath $BankPath `
    -Repetitions $Repetitions `
    -DelayBetweenCasesSeconds $DelayBetweenCasesSeconds `
    -MaximumJobAttempts $MaximumJobAttempts `
    -MaximumProviderHttpAttempts $MaximumProviderHttpAttempts `
    -MaximumJobRetryDelayMilliseconds $MaximumJobRetryDelayMilliseconds `
    -ProviderBaseUrl $BaseUrl `
    -ModelId $ModelId `
    -ReasoningEffort $ReasoningEffort `
    -SynthesisReasoningEffort $SynthesisReasoningEffort `
    -SynthesisPromptStyle $SynthesisPromptStyle `
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
    -DevelopmentTraceDirectory $DevelopmentTraceDirectory `
    -InputUsdPerMillionTokens $InputUsdPerMillionTokens `
    -CachedInputUsdPerMillionTokens $CachedInputUsdPerMillionTokens `
    -OutputUsdPerMillionTokens $OutputUsdPerMillionTokens `
    -ProviderRuntime $ProviderRuntime `
    -RuntimeProfile $RuntimeProfile `
    -Gpu $Gpu `
    -Quantization $Quantization `
    -ModelSha256 $ModelSha256 `
    -ContextSize $ContextSize `
    -HourlyCostUsd $HourlyCostUsd `
    -LocalLlmExePath $LocalLlmExePath `
    -LocalModelPath $LocalModelPath `
    -Configuration $Configuration `
    -Platform $Platform `
    -ArtifactDirectory $ArtifactDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Advanced RunPod product-path runner failed with exit code $LASTEXITCODE."
}
