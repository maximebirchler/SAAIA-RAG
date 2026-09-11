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
    [ValidateSet("gpt-5.6-terra", "gpt-5.6-luna")]
    [string]$OpenAiModel = "gpt-5.6-terra",
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
    -ModelId $OpenAiModel `
    -LocalLlmExePath $LocalLlmExePath `
    -LocalModelPath $LocalModelPath `
    -Configuration $Configuration `
    -Platform $Platform `
    -ArtifactDirectory $ArtifactDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Advanced OpenAI product-path runner failed with exit code $LASTEXITCODE."
}
