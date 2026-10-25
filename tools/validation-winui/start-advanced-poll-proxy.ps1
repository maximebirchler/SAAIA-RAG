#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [int]$Port=5167)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
$ownerPath=Join-Path $output 'environment.json'
$owner=Get-Content -LiteralPath $ownerPath -Raw|ConvertFrom-Json
if($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output){throw 'Not an owned fixture.'}
if($Port -lt 1024 -or $Port -eq 5432 -or (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)){throw 'Invalid or occupied fixture port.'}
$worker=Get-Content -LiteralPath (Join-Path $output 'advanced-worker.json') -Raw|ConvertFrom-Json
if($worker.backendUrl -ne 'http://127.0.0.1:5137'){throw 'Expected dedicated advanced worker endpoint.'}
$isolation=Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw|ConvertFrom-Json
$userId=([Guid]$isolation.userId).ToString('D')
$control=Join-Path $output 'runtime/advanced-poll-control-w24.json'
$events=Join-Path $output 'advanced-poll-events-w24.jsonl'
$profile=Join-Path $output 'runtime/client-profiles/w24'
if((Test-Path -LiteralPath $control) -or (Test-Path -LiteralPath $profile)){throw 'W24 evidence/profile already exists; no overwrite.'}
$source=Join-Path $output 'runtime/client-profiles/manual-reconfiguration/client'
$settings=Get-Content -LiteralPath (Join-Path $source 'settings.json') -Raw|ConvertFrom-Json
$settings.BackendUrl="http://127.0.0.1:$Port"; $settings.BackendUrlAlternates='';$settings.AutoConnect=$true
$settings.LlmMode='external';$settings.UseLocalLlm=$true;$settings.ManageLocalLlmProcess=$false
$settings.AutoStartOnConnect=$false;$settings.EagerLoad=$false;$settings.Host='127.0.0.1';$settings.Port=18088
$settings.LastSessionId=$null
$clientDir=Join-Path $profile 'client';[IO.Directory]::CreateDirectory($clientDir)|Out-Null
[IO.File]::WriteAllText((Join-Path $clientDir 'settings.json'),($settings|ConvertTo-Json -Depth 12))
[IO.File]::Copy((Join-Path $source 'secure.json'),(Join-Path $clientDir 'secure.json'),$false)
[IO.File]::WriteAllText($control,'{"targetJobId":"","mode":"passthrough","remaining":0}')
$info=[Diagnostics.ProcessStartInfo]::new((Get-Command python).Source)
$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
foreach($argument in @((Join-Path $PSScriptRoot 'advanced_poll_proxy.py'),'--port',"$Port",'--upstream-port','5137','--user-id',$userId,'--control',$control,'--events',$events)){$info.ArgumentList.Add($argument)}
$process=[Diagnostics.Process]::Start($info)
$owner.processes+=@{name='storage-proxy';instanceName='advanced-poll-w24';pid=$process.Id;path=$info.FileName;startedAtUtc=$process.StartTime.ToUniversalTime().ToString('o');port=$Port;upstreamPort=5137}
$owner|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $ownerPath -Encoding utf8
$deadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
do{
    try{$ready=Invoke-RestMethod "http://127.0.0.1:$Port/ready" -TimeoutSec 3;if($ready.ok){break}}catch{}
    if($process.HasExited -or [DateTimeOffset]::UtcNow -gt $deadline){throw 'Advanced polling proxy did not become ready.'}
    Start-Sleep -Milliseconds 100
}while($true)
$proof=[ordered]@{schemaVersion='saaia-winui-advanced-poll-proxy.v1';capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');proxyUrl="http://127.0.0.1:$Port";upstreamUrl=$worker.backendUrl;profileName='w24';localModelPort=18088;controlRelative='runtime/advanced-poll-control-w24.json';eventsRelative='advanced-poll-events-w24.jsonl';targetUserId=$userId;controlsInitiallyDisarmed=$true;onlyGetTargetJobMutated=$true;postRequestsPassThroughUnchanged=$true;databaseMutatedForInjection=$false;jobCreated=$false;providerControlChanged=$false;clientLaunched=$false;normalUserProfileTouched=$false;sourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'advanced_poll_proxy.py')).Hash;modes=@('passthrough','http503','regressive_revision','session_mismatch')}
[IO.File]::WriteAllText((Join-Path $output 'advanced-poll-proxy-w24.json'),($proof|ConvertTo-Json -Depth 8))
Write-Output 'Owned advanced polling proxy ready at127.0.0.1:5167; w24 profile prepared; no job/client/model started.'
