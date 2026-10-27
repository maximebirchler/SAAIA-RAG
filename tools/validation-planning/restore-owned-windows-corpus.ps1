#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreparedEnvironmentDirectory,
    [Parameter(Mandatory)][string]$PacketDirectory,[Parameter(Mandatory)][string]$MetadataSha256,
    [Parameter(Mandatory)][string]$CorpusManifestPath,[Parameter(Mandatory)][string]$CorpusManifestSha256,
    [Parameter(Mandatory)][string]$PythonExecutable,[switch]$Execute)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../validation-paid/PaidRuntime.ps1')
if(-not $Execute){Write-Output 'NOEXEC: no SQL, key, provider, process or fixture mutation.';return}
$owner=Get-OwnedPaidEnvironment $PreparedEnvironmentDirectory
if($owner.planningCloneOnly -ne $true -or $owner.state -cne 'ready'){throw 'Fresh explicit PlanningCloneOnly environment required.'}
$null=Invoke-OwnedPaidSql -OutputDirectory $PreparedEnvironmentDirectory -Query 'SELECT 1'
& $PythonExecutable -X utf8 -B (Join-Path $PSScriptRoot 'restore_owned_windows_corpus.py') `
    --owner (Join-Path $PreparedEnvironmentDirectory 'environment.json') --packet $PacketDirectory `
    --corpus-manifest $CorpusManifestPath --corpus-manifest-sha256 $CorpusManifestSha256 `
    --metadata-sha256 $MetadataSha256 --output (Join-Path $PreparedEnvironmentDirectory 'runtime/strict-worker-clone') --execute
if($LASTEXITCODE -ne 0){throw 'Strict worker clone refused; preserve evidence and stop the owned environment before another fresh attempt.'}
