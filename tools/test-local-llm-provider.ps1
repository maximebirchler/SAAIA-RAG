[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj"

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\local-provider-smoke-$stamp"
}

New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$env:SAAIA_RUN_LOCAL_PROVIDER_TEST = "1"
$env:SAAIA_LOCAL_PROVIDER_ARTIFACT_DIR = $ArtifactDirectory

dotnet test $project `
    -c $Configuration `
    -p:Platform=$Platform `
    --filter "FullyQualifiedName~LiveLocalProviderTests" `
    --logger "console;verbosity=normal"

if ($LASTEXITCODE -ne 0) {
    throw "The explicit local provider probe failed with exit code $LASTEXITCODE."
}

Write-Output "Local provider probe completed. Artifact: $ArtifactDirectory"
