[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerEnvPath,
    [string]$ReferenceBackendUrl = "http://saaia-server:5122",
    [ValidateRange(1024, 65535)]
    [int]$BackendPort = 5123,
    [string]$Ids = "A755-ADV-01-meal-grid-5x4",
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
    -Repetitions $Repetitions `
    -DelayBetweenCasesSeconds $DelayBetweenCasesSeconds `
    -MaximumJobAttempts $MaximumJobAttempts `
    -MaximumJobRetryDelayMilliseconds $MaximumJobRetryDelayMilliseconds `
    -ModelId $OpenAiModel `
    -ProviderAccountTier $ObservedOrganizationTier `
    -ProviderAccountTierObservedAtUtc $TierObservedAtUtc `
    -AuthorizedBudgetUsd $AuthorizedBudgetUsd `
    -SoftLimitUsd $SoftLimitUsd `
    -HardLimitUsd $HardLimitUsd `
    -MaximumCostPerJobUsd $MaximumCostPerJobUsd `
    -MaximumCallsPerJob $MaximumCallsPerJob `
    -EnableSemanticCritic:$EnableSemanticCritic `
    -CriticMaxTokens $CriticMaxTokens `
    -LocalLlmExePath $LocalLlmExePath `
    -LocalModelPath $LocalModelPath `
    -Configuration $Configuration `
    -Platform $Platform `
    -ArtifactDirectory $ArtifactDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Advanced OpenAI product-path runner failed with exit code $LASTEXITCODE."
}
