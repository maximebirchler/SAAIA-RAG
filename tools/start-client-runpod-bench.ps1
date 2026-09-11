[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,
    [Parameter(Mandatory = $true)]
    [string]$ModelId,
    [string]$RuntimeProfile = "",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

$env:SAAIA_LLM_PROVIDER_MODE = "RunPodBench"
$env:SAAIA_LLM_EXTERNAL_POLICY = "BenchmarkExternalAllowed"
$env:SAAIA_RUNPOD_BASE_URL = $BaseUrl.TrimEnd('/')
$env:SAAIA_RUNPOD_MODEL = $ModelId
$env:SAAIA_RUNPOD_RUNTIME_PROFILE = $RuntimeProfile

& (Join-Path $PSScriptRoot "..\client\scripts\run-client-x64.ps1") -Platform $Platform
exit $LASTEXITCODE
