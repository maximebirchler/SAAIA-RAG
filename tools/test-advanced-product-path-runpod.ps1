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
    [ValidateRange(1, 3)]
    [int]$Repetitions = 1,
    [ValidateRange(0, 300)]
    [int]$DelayBetweenCasesSeconds = 0,
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,
    [Parameter(Mandatory = $true)]
    [string]$ModelId,
    [decimal]$SoftLimitUsd = 0,
    [decimal]$HardLimitUsd = 0,
    [decimal]$MaximumCostPerJobUsd = 0,
    [int]$MaximumCallsPerJob = 4,
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
    -Repetitions $Repetitions `
    -DelayBetweenCasesSeconds $DelayBetweenCasesSeconds `
    -ProviderBaseUrl $BaseUrl `
    -ModelId $ModelId `
    -AuthorizedBudgetUsd $AuthorizedBudgetUsd `
    -SoftLimitUsd $SoftLimitUsd `
    -HardLimitUsd $HardLimitUsd `
    -MaximumCostPerJobUsd $MaximumCostPerJobUsd `
    -MaximumCallsPerJob $MaximumCallsPerJob `
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
