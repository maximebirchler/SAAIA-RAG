[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [ValidateSet("Free", "Tier1", "Tier2", "Tier3", "Tier4", "Tier5")]
    [string]$ObservedOrganizationTier = "Free",
    [string]$TierObservedAtUtc = "",
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "openai-paid-tier-guard.ps1")
Assert-OpenAiPaidTierObservation `
    -ObservedOrganizationTier $ObservedOrganizationTier `
    -TierObservedAtUtc $TierObservedAtUtc | Out-Null
$project = Join-Path $repositoryRoot "client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj"

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\openai-terra-provider-smoke-$stamp"
}

New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$env:SAAIA_LLM_PROVIDER_MODE = "OpenAiDev"
$env:SAAIA_LLM_EXTERNAL_POLICY = "DevelopmentExternalAllowed"
$env:SAAIA_RUN_OPENAI_TERRA_PROVIDER_TEST = "1"
$env:SAAIA_OPENAI_TERRA_ARTIFACT_DIR = $ArtifactDirectory

dotnet test $project `
    -c $Configuration `
    -p:Platform=$Platform `
    --filter "FullyQualifiedName~LiveOpenAiTerraProviderTests" `
    --logger "console;verbosity=normal"

if ($LASTEXITCODE -ne 0) {
    throw "The explicit Terra provider probe failed with exit code $LASTEXITCODE."
}

Write-Output "Terra provider probe completed. Artifact: $ArtifactDirectory"
