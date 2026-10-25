[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(0.01,1000)][decimal]$AuthorizedBudgetUsd,
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
. (Join-Path $PSScriptRoot 'validation-paid/PaidCampaign.Common.ps1')
Assert-LegacyPaidAuthorization -Model 'gpt-5.6-terra' -BudgetUsd $AuthorizedBudgetUsd
$baseline = Get-Content -LiteralPath (Join-Path $repositoryRoot 'config/llm-providers.dev.json') -Raw | ConvertFrom-Json
if ([decimal]$baseline.openAi.budget.hardLimitUsd -gt $AuthorizedBudgetUsd) {
    throw 'The immutable Terra baseline exceeds the explicit authorized cap; use a reviewed campaign instead.'
}
Assert-OpenAiPaidTierObservation `
    -ObservedOrganizationTier $ObservedOrganizationTier `
    -TierObservedAtUtc $TierObservedAtUtc | Out-Null
$project = Join-Path $repositoryRoot "client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj"

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\openai-terra-provider-smoke-$stamp"
}

if (Test-Path -LiteralPath $ArtifactDirectory) { throw 'Use a new artifact directory; prior live evidence is immutable.' }
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null
$values = @{ SAAIA_LLM_PROVIDER_MODE = 'OpenAiDev'; SAAIA_LLM_EXTERNAL_POLICY = 'DevelopmentExternalAllowed';
    SAAIA_RUN_OPENAI_TERRA_PROVIDER_TEST = '1'; SAAIA_OPENAI_TERRA_ARTIFACT_DIR = $ArtifactDirectory;
    SAAIA_OPENAI_MODEL = 'gpt-5.6-terra' }
$previous = @{}
try {
    foreach ($entry in $values.GetEnumerator()) {
        $previous[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    & dotnet test $project -c $Configuration -p:Platform=$Platform `
        --filter 'FullyQualifiedName~LiveOpenAiTerraProviderTests' `
        --results-directory $ArtifactDirectory --logger 'trx;LogFileName=live-terra.trx'
    if ($LASTEXITCODE -ne 0) { throw "The explicit Terra provider probe failed with exit code $LASTEXITCODE." }
    $proof = Assert-LiveTestEvidence -TrxPath (Join-Path $ArtifactDirectory 'live-terra.trx') `
        -ResultPath (Join-Path $ArtifactDirectory 'result.json')
    $proof | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'executed-test-evidence.json')
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}

Write-Output "Terra provider probe completed. Artifact: $ArtifactDirectory"
