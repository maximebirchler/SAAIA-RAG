#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProfilePath,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $root){throw 'Use a new dry-run test directory.'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$runner=Join-Path $PSScriptRoot 'invoke-local-luna-campaign.ps1'
$sentinel=Join-Path $root 'not-a-secret-store.txt'
[IO.File]::WriteAllText($sentinel,'THIS IS NOT JSON: READING THIS AS A SECRET STORE MUST FAIL')
$checks=[Collections.Generic.List[object]]::new()
$dry=Join-Path $root 'dry'
& $runner -ProfilePath $ProfilePath -OutputDirectory $dry -SecretStorePath $sentinel
$result=Get-Content -LiteralPath (Join-Path $dry 'campaign-result.json') -Raw|ConvertFrom-Json
$checks.Add(@{name='dry-run never reads the malformed secret sentinel';passed=(-not $result.secretRead -and -not $result.executed -and $result.state -eq 'prepared')})
$blocked=Join-Path $root 'must-not-exist'
$failure=$null
try{& $runner -ProfilePath $ProfilePath -OutputDirectory $blocked -Execute -SecretStorePath $sentinel}catch{$failure=$_.Exception.Message}
$checks.Add(@{name='Execute without explicit authorization is blocked before key access and output mutation';
    passed=($failure -like '*Execution requires explicit paid/transmission authorization*' -and -not(Test-Path -LiteralPath $blocked))})
[IO.File]::WriteAllText((Join-Path $root 'checks.json'),(@{schemaVersion='saaia-luna-dry-run-tests.v1';checks=@($checks);providerCalls=0;servicesStarted=0;secretsRead=0}|ConvertTo-Json -Depth 10))
if(@($checks|Where-Object{-not $_.passed}).Count){throw 'Luna dry-run/auth isolation failed.'}
Write-Output '2 dry-run/key-boundary checks passed; no provider or service.'
