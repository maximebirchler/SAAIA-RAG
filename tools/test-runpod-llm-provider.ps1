[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,
    [Parameter(Mandatory = $true)]
    [string]$ModelId,
    [Parameter(Mandatory = $true)]
    [string]$Runtime,
    [string]$RuntimeProfile = "",
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj"

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\runpod-provider-smoke-$stamp"
}

New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$env:SAAIA_LLM_PROVIDER_MODE = "RunPodBench"
$env:SAAIA_LLM_EXTERNAL_POLICY = "BenchmarkExternalAllowed"
$env:SAAIA_RUNPOD_BASE_URL = $BaseUrl.TrimEnd('/')
$env:SAAIA_RUNPOD_MODEL = $ModelId
$env:SAAIA_RUNPOD_RUNTIME = $Runtime
$env:SAAIA_RUNPOD_RUNTIME_PROFILE = $RuntimeProfile
$env:SAAIA_RUN_RUNPOD_PROVIDER_TEST = "1"
$env:SAAIA_RUNPOD_ARTIFACT_DIR = $ArtifactDirectory

dotnet test $project `
    -c $Configuration `
    -p:Platform=$Platform `
    --filter "FullyQualifiedName~LiveRunPodProviderTests" `
    --logger "console;verbosity=normal"

if ($LASTEXITCODE -ne 0) {
    throw "The explicit RunPod provider probe failed with exit code $LASTEXITCODE."
}

Write-Output "RunPod provider probe completed. Artifact: $ArtifactDirectory"
