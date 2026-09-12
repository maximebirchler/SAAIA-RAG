[CmdletBinding()]
param(
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot "openai-paid-tier-guard.ps1")
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\openai-paid-tier-guards-$stamp"
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null

$checks = @()
function Add-ExpectedFailure {
    param([string]$Name, [scriptblock]$Action, [string]$ExpectedMessage)
    $message = $null
    try { & $Action } catch { $message = $_.Exception.Message }
    $script:checks += [ordered]@{
        name = $Name
        pass = -not [string]::IsNullOrWhiteSpace($message) -and
            $message -match [regex]::Escape($ExpectedMessage)
        observedMessage = $message
    }
}

Add-ExpectedFailure "helper_blocks_free" {
    Assert-OpenAiPaidTierObservation -ObservedOrganizationTier Free -TierObservedAtUtc ""
} "freshly verified paid tier"
Add-ExpectedFailure "helper_requires_timestamp" {
    Assert-OpenAiPaidTierObservation -ObservedOrganizationTier Tier1 -TierObservedAtUtc ""
} "TierObservedAtUtc is required"
Add-ExpectedFailure "helper_requires_explicit_offset" {
    Assert-OpenAiPaidTierObservation -ObservedOrganizationTier Tier1 -TierObservedAtUtc "2026-09-12T08:00:00"
} "explicit UTC or numeric offset"
Add-ExpectedFailure "helper_rejects_stale_observation" {
    $stale = [DateTimeOffset]::UtcNow.AddHours(-1).ToString("o")
    Assert-OpenAiPaidTierObservation -ObservedOrganizationTier Tier1 -TierObservedAtUtc $stale
} "missing, stale or implausibly"

$fresh = [DateTimeOffset]::UtcNow.ToString("o")
$verified = Assert-OpenAiPaidTierObservation `
    -ObservedOrganizationTier Tier1 `
    -TierObservedAtUtc $fresh
$checks += [ordered]@{
    name = "helper_accepts_fresh_paid_tier"
    pass = $verified -is [DateTimeOffset]
    observedMessage = $null
}

$mustNotExistRoot = Join-Path $ArtifactDirectory "must-not-exist"
$entrypoints = @(
    [ordered]@{
        name = "product_path"
        artifact = Join-Path $mustNotExistRoot "product-path"
        action = {
            & (Join-Path $PSScriptRoot "test-advanced-product-path-openai.ps1") `
                -ServerEnvPath "C:\definitely-missing.env" `
                -ObservedOrganizationTier Free `
                -ArtifactDirectory (Join-Path $mustNotExistRoot "product-path")
        }
    },
    [ordered]@{
        name = "direct_agent_bank"
        artifact = Join-Path $mustNotExistRoot "direct-agent-bank"
        action = {
            & (Join-Path $PSScriptRoot "test-openai-terra-agent-bank.ps1") `
                -ObservedOrganizationTier Free `
                -ArtifactDirectory (Join-Path $mustNotExistRoot "direct-agent-bank")
        }
    },
    [ordered]@{
        name = "direct_provider_probe"
        artifact = Join-Path $mustNotExistRoot "direct-provider"
        action = {
            & (Join-Path $PSScriptRoot "test-openai-terra-provider.ps1") `
                -ObservedOrganizationTier Free `
                -ArtifactDirectory (Join-Path $mustNotExistRoot "direct-provider")
        }
    },
    [ordered]@{
        name = "advanced_server_probe"
        artifact = Join-Path $mustNotExistRoot "advanced-server"
        action = {
            & (Join-Path $PSScriptRoot "test-advanced-server-provider.ps1") `
                -Provider OpenAI `
                -ObservedOrganizationTier Free `
                -ArtifactDirectory (Join-Path $mustNotExistRoot "advanced-server")
        }
    }
)
foreach ($entrypoint in $entrypoints) {
    $message = $null
    try { & $entrypoint.action } catch { $message = $_.Exception.Message }
    $checks += [ordered]@{
        name = "entrypoint_$($entrypoint.name)_blocks_free"
        pass = $message -match "freshly verified paid tier" -and
            -not (Test-Path -LiteralPath $entrypoint.artifact)
        observedMessage = $message
    }
}

$previousProviderMode = [Environment]::GetEnvironmentVariable(
    "SAAIA_LLM_PROVIDER_MODE",
    "Process")
$startMessage = $null
try {
    & (Join-Path $PSScriptRoot "start-client-openai-terra-dev.ps1") `
        -ObservedOrganizationTier Free
}
catch { $startMessage = $_.Exception.Message }
$providerModeAfter = [Environment]::GetEnvironmentVariable(
    "SAAIA_LLM_PROVIDER_MODE",
    "Process")
$checks += [ordered]@{
    name = "entrypoint_start_client_blocks_before_environment_change"
    pass = $startMessage -match "freshly verified paid tier" -and
        $providerModeAfter -eq $previousProviderMode
    observedMessage = $startMessage
}

$failures = @($checks | Where-Object { -not $_.pass })
$result = [ordered]@{
    schemaVersion = "saaia-openai-paid-tier-guard-assessment-v1"
    assessedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    checks = $checks
    total = $checks.Count
    passed = $checks.Count - $failures.Count
    failed = $failures.Count
    externalProviderCalls = 0
    productStatus = "TESTE_NON_APPROUVE"
}
$resultPath = Join-Path $ArtifactDirectory "assessment.v1.json"
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding utf8
Write-Output "OpenAI paid-tier guard assessment: $resultPath"
Write-Output "Passed: $($result.passed)/$($result.total)"
if ($failures.Count -gt 0) { exit 2 }
