#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'OwnedDependencies.ps1')
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output=Assert-DependencyProjectPath $OutputDirectory $repo 'artifacts'
if(Test-Path -LiteralPath $output){throw 'Use a fresh offline test evidence directory.'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$results=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[scriptblock]$Body,[bool]$Reject=$false){
    $failed=$false
    try{& $Body|Out-Null}catch{$failed=$true}
    $ok=($failed -eq $Reject)
    $results.Add(@{name=$Name;passed=$ok;expectedRejection=$Reject})
    if(-not $ok){throw ('Offline dependency guard failed: '+$Name)}
}
Check 'distinct dependency ports accepted' {Get-ValidatedDependencyPorts @(15101,15102,16341,15103)}
foreach($ports in @(@(15101,15101),@(5432),@(80),@(65536))){Check ('invalid ports '+$results.Count) {Get-ValidatedDependencyPorts $ports} $true}
$fakeRepo=Join-Path $output 'synthetic-repo'
$campaign=Join-Path $fakeRepo 'artifacts/campaign'
$runtime=Join-Path $fakeRepo 'out/runtime'
[IO.Directory]::CreateDirectory($campaign)|Out-Null
[IO.Directory]::CreateDirectory((Join-Path $runtime 'qdrant-v1.20.0'))|Out-Null
$qdrant=Join-Path $runtime 'qdrant-v1.20.0/qdrant.exe'
[IO.File]::WriteAllText($qdrant,'not executable; filesystem test fixture only')
$fixtureHash=(Get-FileHash -LiteralPath $qdrant -Algorithm SHA256).Hash.ToLowerInvariant()
Check 'pinned bytes accepted before startup' {Assert-DependencyFileHash $qdrant $fixtureHash}
Check 'changed bytes rejected before startup' {Assert-DependencyFileHash $qdrant ('0'*64)} $true
Check 'missing pinned file rejected before startup' {Assert-DependencyFileHash (Join-Path $output 'absent') $fixtureHash} $true
$entry=[pscustomobject]@{name='qdrant';pid=101;path=(Join-Path $output 'host/pwsh.exe');startedAtUtc='2026-10-26T00:00:00Z';specification=(Join-Path $campaign 'qdrant-process.json')}
$owner=[pscustomobject]@{schemaVersion='saaia-local-ingestion-dependencies.v1';state='failed';outputDirectory=$campaign;
    doclingUrl='http://127.0.0.1:15101';upstreamUrl='http://127.0.0.1:15102';qdrantUrl='http://127.0.0.1:16341';processes=@($entry)}
function Copy-Owner {return ($owner|ConvertTo-Json -Depth 8|ConvertFrom-Json)}
Check 'partial failed startup can be cleaned with its registered role only' {
    $value=Get-ValidatedDependencyOwner $owner $campaign $fakeRepo
    if(-not $value.partialStartup -or $value.entries.Count -ne 1){throw 'Partial startup identity lost.'}
}
Check 'partial starting startup accepted' {$x=Copy-Owner;$x.state='starting';Get-ValidatedDependencyOwner $x $campaign $fakeRepo}
Check 'ready cannot omit roles' {$x=Copy-Owner;$x.state='ready';Get-ValidatedDependencyOwner $x $campaign $fakeRepo} $true
Check 'duplicate process identity rejected' {$x=Copy-Owner;$x.processes=@($entry,$entry);Get-ValidatedDependencyOwner $x $campaign $fakeRepo} $true
Check 'unknown role rejected' {$x=Copy-Owner;$x.processes[0].name='unrelated';Get-ValidatedDependencyOwner $x $campaign $fakeRepo} $true
Check 'foreign specification rejected' {$x=Copy-Owner;$x.processes[0].specification=Join-Path $output 'foreign.json';Get-ValidatedDependencyOwner $x $campaign $fakeRepo} $true
Check 'manifest listener declarations validated before mutation' {$x=Copy-Owner;$x.qdrantUrl='http://0.0.0.0:16341';Get-ValidatedDependencyOwner $x $campaign $fakeRepo} $true
$spec=@{executable=$qdrant;arguments=@('--config-path',(Join-Path $campaign 'qdrant.yaml'),'--disable-telemetry');stdout=(Join-Path $campaign 'qdrant.stdout.log');stderr=(Join-Path $campaign 'qdrant.stderr.log')}
[IO.File]::WriteAllText($entry.specification,($spec|ConvertTo-Json -Depth 6))
$validated=Get-ValidatedDependencyOwner $owner $campaign $fakeRepo
Check 'legacy four-field process specification accepted' {Read-ValidatedDependencySpecification $entry $validated $fakeRepo}
$spec.stdout=Join-Path $output 'foreign.log'
[IO.File]::WriteAllText($entry.specification,($spec|ConvertTo-Json -Depth 6))
Check 'foreign log destination rejected' {Read-ValidatedDependencySpecification $entry $validated $fakeRepo} $true
$spec.stdout=Join-Path $campaign 'qdrant.stdout.log';$spec.arguments=@('--config-path',(Join-Path $output 'foreign.yaml'),'--disable-telemetry')
[IO.File]::WriteAllText($entry.specification,($spec|ConvertTo-Json -Depth 6))
Check 'foreign process argument rejected' {Read-ValidatedDependencySpecification $entry $validated $fakeRepo} $true
$arguments=@('--config-path',(Join-Path $campaign 'qdrant.yaml'),'--disable-telemetry')
$command='"'+$qdrant+'" --config-path "'+(Join-Path $campaign 'qdrant.yaml')+'" --disable-telemetry'
Check 'quoted exact command line accepted' {if(-not(Test-DependencyCommandLine $qdrant $arguments $command)){throw 'Expected exact command.'}}
Check 'specification suffix cannot impersonate owned command' {if(Test-DependencyCommandLine $qdrant $arguments ($command -replace 'qdrant.yaml','qdrant.yaml.extra')){throw 'Prefix-only command accepted.'}}
Check 'extra command argument rejected' {if(Test-DependencyCommandLine $qdrant $arguments ($command+' --extra')){throw 'Extra argument accepted.'}}
$identity=[pscustomobject]@{pid=201;parentPid=101;startedAtUtc='2026-10-26T00:00:00.1234567Z';cimStartedAtUtc='2026-10-26T00:00:00.123456Z';executable=$qdrant;commandLine=$command}
Check 'exact process and CIM identity accepted' {Assert-DependencyProcessIdentity $identity $identity}
foreach($mutation in @('time','parent','exe','command','cim')){
    Check ('changed process '+$mutation) {
        $x=$identity|ConvertTo-Json|ConvertFrom-Json
        switch($mutation){'time'{$x.startedAtUtc='2026-10-26T00:00:01Z'};'parent'{$x.parentPid=999};'exe'{$x.executable=Join-Path $output 'other.exe'};'command'{$x.commandLine+=' --extra'};'cim'{$x.cimStartedAtUtc='2026-10-26T00:00:01Z'}}
        Assert-DependencyProcessIdentity $identity $x
    } $true
}
Check 'owned loopback listener accepted' {Assert-DependencyListenerOwnership @(@{LocalAddress='127.0.0.1';OwningProcess=201}) @(201)}
Check 'unrelated listener blocks mutation' {Assert-DependencyListenerOwnership @(@{LocalAddress='127.0.0.1';OwningProcess=999}) @(201)} $true
Check 'nonloopback listener blocks mutation' {Assert-DependencyListenerOwnership @(@{LocalAddress='0.0.0.0';OwningProcess=201}) @(201)} $true
$fakePlan=[pscustomobject]@{root=[pscustomobject]@{pid=1};children=@([pscustomobject]@{pid=2;kind='work';depth=1},[pscustomobject]@{pid=3;kind='work';depth=2},[pscustomobject]@{pid=4;kind='console';depth=2})}
Check 'deepest work first then wrapper drain then console then final verification' {
    $events=[Collections.Generic.List[string]]::new()
    Invoke-OwnedDependencyStopSequence @($fakePlan) -StopWork {param($x)$events.Add('work'+$x.pid)} -WaitWrapper {param($x)$events.Add('wait'+$x.pid);return $true} -FinishConsole {param($x)$events.Add('console'+$x.pid)} -VerifyStopped {$events.Add('verify')}
    if(($events -join ',') -cne 'work3,work2,wait1,console4,verify'){throw 'Unsafe process/log ordering.'}
}
Check 'wrapper wait timeout cannot report stopped' {
    $events=[Collections.Generic.List[string]]::new();$rejected=$false
    try{Invoke-OwnedDependencyStopSequence @($fakePlan) -StopWork {} -WaitWrapper {return $false} -FinishConsole {} -VerifyStopped {$events.Add('unsafe_final_success')}}catch{$rejected=$true}
    if(-not $rejected -or $events.Contains('unsafe_final_success')){throw 'Wrapper timeout did not block final success.'}
}
Check 'remaining process or port cannot report stopped' {Invoke-OwnedDependencyStopSequence @() -StopWork {} -WaitWrapper {return $true} -FinishConsole {} -VerifyStopped {throw 'synthetic remaining owned process'}} $true
foreach($file in @('OwnedDependencies.ps1','start-local-dependencies.ps1','stop-local-dependencies.ps1')){
    Check ('AST '+$file) {$tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$tokens,[ref]$errors);if($errors.Count){throw 'AST parse error.'}}
}
$proof=[ordered]@{schemaVersion='saaia-owned-dependency-offline-guards.v1';passed=$true;caseCount=$results.Count;results=@($results.ToArray());
    servicesStarted=0;processesStopped=0;httpCalls=0;modelCalls=0;credentialsRead=0;testsUseSyntheticProcessIdentities=$true}
[IO.File]::WriteAllText((Join-Path $output 'guards.json'),($proof|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Write-Output ('Offline dependency guards PASS '+$results.Count+'; no live process, service, model or HTTP action.')
