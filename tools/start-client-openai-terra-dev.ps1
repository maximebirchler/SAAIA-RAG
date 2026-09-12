[CmdletBinding()]
param(
    [string]$Platform = "x64",
    [ValidateSet("Free", "Tier1", "Tier2", "Tier3", "Tier4", "Tier5")]
    [string]$ObservedOrganizationTier = "Free",
    [string]$TierObservedAtUtc = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "openai-paid-tier-guard.ps1")
Assert-OpenAiPaidTierObservation `
    -ObservedOrganizationTier $ObservedOrganizationTier `
    -TierObservedAtUtc $TierObservedAtUtc | Out-Null
$env:SAAIA_LLM_PROVIDER_MODE = "OpenAiDev"
$env:SAAIA_LLM_EXTERNAL_POLICY = "DevelopmentExternalAllowed"
$env:SAAIA_OPENAI_MODEL = "gpt-5.6-terra"

& (Join-Path $PSScriptRoot "..\client\scripts\run-client-x64.ps1") -Platform $Platform
exit $LASTEXITCODE
