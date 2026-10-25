#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='Medium')]
param([Parameter(Mandatory)][string]$OutputDirectory,[string]$PlanOutputPath)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$ownerPath=Join-Path $output 'environment.json'
$owner=Get-Content -LiteralPath $ownerPath -Raw|ConvertFrom-Json
if($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output){throw 'Not an owned WinUI fixture.'}
$manifestSha=(Get-FileHash -LiteralPath $ownerPath).Hash
$expectedData=[IO.Path]::GetFullPath((Join-Path $output 'runtime/postgres/data'))
if($expectedData -ine [IO.Path]::GetFullPath($owner.postgresDataDirectory)){throw 'Owned PostgreSQL directory mismatch.'}
if([int]$owner.pgPort -lt 1024 -or [int]$owner.pgPort -eq 5432){throw 'Refusing the user/production PostgreSQL port.'}
$entries=@($owner.processes)
if(@($entries|Group-Object pid|Where-Object Count -ne 1).Count){throw 'Duplicate owned PID entries.'}
$pgEntries=@($entries|Where-Object name -eq 'postgres')
if($pgEntries.Count -ne 1){throw 'Exactly one owned PostgreSQL entry is required.'}
$pgEntry=$pgEntries[0]
$pgExe=Join-Path $owner.postgresBinDirectory 'postgres.exe'
$pgCtl=Join-Path $owner.postgresBinDirectory 'pg_ctl.exe'
if([IO.Path]::GetFullPath($pgEntry.path) -ine [IO.Path]::GetFullPath($pgExe) -or -not (Test-Path -LiteralPath $pgCtl -PathType Leaf)){throw 'Owned PostgreSQL executable path mismatch.'}

function Get-Identity([int]$ProcessId){
    $process=Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if($process){return [pscustomobject]@{pid=$process.Id;startedAtUtc=if($process.StartTime){$process.StartTime.ToUniversalTime().ToString('o')}else{$null};path=$process.Path}}
    return $null
}
function Assert-Identity([object]$Identity){
    $actual=Get-Identity $Identity.pid
    if(-not $actual){return $false}
    if(-not $actual.startedAtUtc -or -not $actual.path){throw 'Owned process identity is unreadable; refusing to stop.'}
    if(([DateTimeOffset]$actual.startedAtUtc).UtcTicks -ne ([DateTimeOffset]$Identity.startedAtUtc).UtcTicks){throw 'PID was reused; refusing to stop an unrelated process.'}
    if([IO.Path]::GetFullPath($actual.path) -ine [IO.Path]::GetFullPath($Identity.path)){throw 'Process executable identity changed; refusing to stop.'}
    return $true
}
function Get-PortListeners([int]$Port){return @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)}
function Assert-LoopbackListeners([object[]]$Listeners,[int[]]$AllowedIds){
    foreach($listener in $Listeners){
        if($listener.LocalAddress -notin @('127.0.0.1','::1')){throw 'Owned test service is not bound exclusively to loopback.'}
        if($listener.OwningProcess -notin $AllowedIds){throw 'A fixture port belongs to an unrelated process.'}
    }
}
function Assert-PostgresOwnership{
    $active=Assert-Identity $pgEntry
    $listeners=@(Get-PortListeners ([int]$owner.pgPort))
    if(-not $active){if($listeners.Count){throw 'PostgreSQL root missing but fixture port remains occupied.'};return $false}
    $pidFile=Join-Path $expectedData 'postmaster.pid'
    if(-not (Test-Path -LiteralPath $pidFile -PathType Leaf)){throw 'Owned PostgreSQL PID file missing.'}
    $pidLines=@([IO.File]::ReadAllLines($pidFile))
    if($pidLines.Count -lt 4 -or [int]$pidLines[0] -ne [int]$pgEntry.pid -or [int]$pidLines[3] -ne [int]$owner.pgPort){throw 'PostgreSQL PID/data-port ownership mismatch.'}
    if([IO.Path]::GetFullPath($pidLines[1]) -ine $expectedData){throw 'PostgreSQL PID file names another data directory.'}
    $epoch=[DateTimeOffset]::FromUnixTimeSeconds([long]$pidLines[2])
    if([Math]::Abs(($epoch-([DateTimeOffset]$pgEntry.startedAtUtc)).TotalSeconds) -gt 3){throw 'PostgreSQL PID file start time mismatch.'}
    Assert-LoopbackListeners $listeners @([int]$pgEntry.pid)
    return $true
}
function Get-Protected5432Snapshot{
    return @(Get-PortListeners 5432|ForEach-Object{
        $identity=Get-Identity $_.OwningProcess
        [pscustomobject]@{pid=$_.OwningProcess;address=$_.LocalAddress;startedAtUtc=$identity.startedAtUtc;identityDetailsReadable=([bool]$identity.startedAtUtc -and [bool]$identity.path);executable=if($identity.path){[IO.Path]::GetFileName($identity.path)}else{$null};
            pathSha256=if($identity.path){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity.path.ToLowerInvariant()))).ToLowerInvariant()}else{$null}}
    }|Sort-Object address,pid)
}
$protected5432=Get-Protected5432Snapshot
$pgActive=Assert-PostgresOwnership
$pgChildren=@()
if($pgActive){
    $pgChildren=@(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($pgEntry.pid)"|ForEach-Object{Get-Identity ([int]$_.ProcessId)})
    foreach($child in $pgChildren){
        if(-not $child.path -or -not $child.startedAtUtc -or ([DateTimeOffset]$child.startedAtUtc) -lt ([DateTimeOffset]$pgEntry.startedAtUtc)){throw 'Owned PostgreSQL child identity is unreadable or predates parent.'}
        if([IO.Path]::GetFullPath($child.path) -inotmatch ('^(?:'+[regex]::Escape([IO.Path]::GetFullPath($pgExe))+'|'+[regex]::Escape([IO.Path]::GetFullPath((Join-Path $env:WINDIR 'System32/conhost.exe')))+')$')){throw 'Unexpected owned PostgreSQL child executable.'}
    }
}
$plan=[Collections.Generic.List[object]]::new()
foreach($entry in @($entries|Where-Object name -ne 'postgres')){
    $port=if($entry.PSObject.Properties['port'] -and $entry.port -gt 0){[int]$entry.port}else{switch($entry.name){
        'fixture-http'{[int]$owner.fixturePort};'backend-runner'{[int]$owner.backendPort};'local-model-runner'{18088};'advanced-fixture-http'{18089};'advanced-backend-runner'{5137}
        default{throw 'Unknown helper role or missing port.'}
    }}
    if($entry.name -notin @('fixture-http','backend-runner','local-model-runner','advanced-fixture-http','advanced-backend-runner','storage-proxy') -or $port -lt 1024 -or $port -in @(5432,[int]$owner.pgPort)){throw 'Invalid owned helper role or port.'}
    $active=Assert-Identity $entry
    $listeners=@(Get-PortListeners $port)
    $children=@();$workChild=$null
    if($active){
        foreach($row in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($entry.pid)")){
            $child=Get-Identity ([int]$row.ProcessId)
            if(-not $child){continue}
            if(([DateTimeOffset]$child.startedAtUtc) -lt ([DateTimeOffset]$entry.startedAtUtc)){throw 'Child start predates owned parent.'}
            $children+=,$child
            if([IO.Path]::GetFullPath($child.path) -ieq [IO.Path]::GetFullPath((Join-Path $env:WINDIR 'System32/conhost.exe'))){continue}
            $expectedChild=switch($entry.name){
                'backend-runner'{(Get-Command dotnet).Source};'advanced-backend-runner'{(Get-Command dotnet).Source}
                'local-model-runner'{Join-Path $env:LOCALAPPDATA 'SAAIA/llm/runtime/win-cuda-x64/b10098/llama-server.exe'}
                default{throw 'Unexpected non-console child under a direct fixture process.'}
            }
            if($workChild -or [IO.Path]::GetFullPath($child.path) -ine [IO.Path]::GetFullPath($expectedChild)){throw 'Unexpected or ambiguous helper child executable.'}
            $workChild=$child
        }
        Assert-LoopbackListeners $listeners (@([int]$entry.pid)+@($children|ForEach-Object{[int]$_.pid}))
        if($workChild -and @($listeners|Where-Object OwningProcess -ne $workChild.pid).Count){throw 'Wrapper listener does not belong to its expected work child.'}
    }elseif($listeners.Count){throw 'Fixture wrapper missing while its port is occupied; manual ownership review required.'}
    $order=switch($entry.name){'backend-runner'{10};'advanced-backend-runner'{10};'local-model-runner'{20};'storage-proxy'{30};default{40}}
    $plan.Add([pscustomobject]@{entry=$entry;port=$port;active=$active;children=$children;workChild=$workChild;order=$order;isWrapper=($entry.name -in @('backend-runner','advanced-backend-runner','local-model-runner'))})
}
if(@($plan|Group-Object port|Where-Object Count -ne 1).Count){throw 'Duplicate owned service port entries.'}
$publicPlan=@($plan|Sort-Object order,port|ForEach-Object{
    [ordered]@{role=$_.entry.name;instanceName=$_.entry.instanceName;pid=$_.entry.pid;startedAtUtc=$_.entry.startedAtUtc;port=$_.port;active=$_.active;
        executable=[IO.Path]::GetFileName($_.entry.path);identityVerified=$_.active;childFirstThenWrapperFlush=$_.isWrapper;
        workChild=if($_.workChild){@{pid=$_.workChild.pid;startedAtUtc=$_.workChild.startedAtUtc;executable=[IO.Path]::GetFileName($_.workChild.path)}}else{$null};
        directConsoleChildren=@($_.children|Where-Object{[IO.Path]::GetFileName($_.path) -ieq 'conhost.exe'}|ForEach-Object{$_.pid});
        plannedStop=if($_.isWrapper){'Exact child stop if alive; await wrapper exit and log flush, then verify listener absent'}else{'Exact verified direct helper stop; verify listener absent'}}
})
$report=[ordered]@{schemaVersion='saaia-winui-owned-stop-plan.v2';capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');prevalidationPassed=$true;whatIf=[bool]$WhatIfPreference;
    processesStopped=$false;databaseStopped=$false;ownedManifestMutated=$false;sourceSha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash;manifestSha256=$manifestSha;services=$publicPlan;
    postgres=@{pid=$pgEntry.pid;port=$owner.pgPort;active=$pgActive;pidStartExecutableDataDirectoryAndPortVerified=$pgActive;verifiedDirectChildren=@($pgChildren|ForEach-Object{@{pid=$_.pid;startedAtUtc=$_.startedAtUtc;executable=[IO.Path]::GetFileName($_.path)}});stopMethod='pg_ctl fast on verified owned data directory, last'};
    protected5432=$protected5432;normalUserProfileTouched=$false;
    limits=@('Prevalidation is a point-in-time read-only observation. Identity and ownership are rechecked immediately before every actual stop.',
        'The operator must first close the isolated WinUI client and finish deliberate job tests. WhatIf does not close clients, change controls or stop anything.',
        'First runtime wrappers buffered output; stop the verified work child first and let the wrapper flush. Never kill the wrapper tree.')}
if($PlanOutputPath){
    $planPath=[IO.Path]::GetFullPath($PlanOutputPath)
    if(Test-Path -LiteralPath $planPath){throw 'Stop plan evidence exists; refusing overwrite.'}
    [IO.File]::WriteAllText($planPath,($report|ConvertTo-Json -Depth 12))
}
if($WhatIfPreference){
    foreach($item in $publicPlan){$null=$PSCmdlet.ShouldProcess(('owned '+$item.role+' port'+$item.port),$item.plannedStop)}
    $null=$PSCmdlet.ShouldProcess(('owned PostgreSQL port'+$owner.pgPort),'pg_ctl fast after all helpers stopped')
    Write-Output ('Owned stop prevalidation PASS; WhatIf only; helpers='+$plan.Count+'; no process, data, controls or ownership manifest changed.')
    return
}
if(Get-Process SAAIA.Client.WinUI -ErrorAction SilentlyContinue){throw 'Close the WinUI client before stopping its owned validation services.'}
if((Get-FileHash -LiteralPath $ownerPath).Hash -ne $manifestSha){throw 'Ownership manifest changed during prevalidation; rerun the plan.'}
foreach($item in @($plan|Sort-Object order,port)){
    if(-not (Assert-Identity $item.entry)){continue}
    Assert-LoopbackListeners @(Get-PortListeners $item.port) (@([int]$item.entry.pid)+@($item.children|ForEach-Object{[int]$_.pid}))
    if(-not $PSCmdlet.ShouldProcess(('owned '+$item.entry.name+' port'+$item.port),'Stop verified helper, preserving wrapper log flush')){throw 'Partial stop declined; runtime not marked stopped.'}
    if($item.isWrapper){
        if($item.workChild -and (Assert-Identity $item.workChild)){
            $directParent=@(Get-CimInstance Win32_Process -Filter "ProcessId=$($item.workChild.pid)")
            if($directParent.Count -ne 1 -or $directParent[0].ParentProcessId -ne $item.entry.pid){throw 'Owned work child parent changed.'}
            $child=Get-Process -Id $item.workChild.pid
            if(-not ($child.CloseMainWindow() -and $child.WaitForExit(5000))){$child.Kill();if(-not $child.WaitForExit(10000)){throw 'Owned work child did not exit.'}}
        }
        $wrapper=Get-Process -Id $item.entry.pid -ErrorAction SilentlyContinue
        if($wrapper -and -not $wrapper.WaitForExit(20000)){throw 'Wrapper did not exit/flush after its child stopped; preserved for diagnosis.'}
    }else{
        $direct=Get-Process -Id $item.entry.pid
        if(-not ($direct.CloseMainWindow() -and $direct.WaitForExit(5000))){$direct.Kill();if(-not $direct.WaitForExit(10000)){throw 'Owned helper did not exit.'}}
    }
    if(@(Get-PortListeners $item.port).Count){throw 'Owned helper port remains occupied after stop.'}
}
if(Assert-PostgresOwnership){
    if(-not $PSCmdlet.ShouldProcess(('owned PostgreSQL port'+$owner.pgPort),'pg_ctl fast on verified dedicated data directory')){throw 'PostgreSQL stop declined; runtime not marked stopped.'}
    & $pgCtl stop -D $expectedData -m fast -w *> (Join-Path $output 'postgres-stop.log')
    if($LASTEXITCODE -ne 0 -or @(Get-PortListeners ([int]$owner.pgPort)).Count){throw 'Owned PostgreSQL did not stop cleanly.'}
}
foreach($entry in $entries){if(Assert-Identity $entry){throw 'An owned root remains alive; runtime not marked stopped.'}}
foreach($item in $plan){foreach($child in $item.children){if(Assert-Identity $child){throw 'An owned helper child remains alive; runtime not marked stopped.'}}}
foreach($child in $pgChildren){if(Assert-Identity $child){throw 'An owned PostgreSQL child remains alive; runtime not marked stopped.'}}
foreach($port in @($owner.pgPort)+@($plan|ForEach-Object{$_.port})){if(@(Get-PortListeners ([int]$port)).Count){throw 'An owned fixture port was rebound; runtime not marked stopped.'}}
$protectedAfter=Get-Protected5432Snapshot
if(($protected5432|ConvertTo-Json -Depth 6 -Compress) -ne ($protectedAfter|ConvertTo-Json -Depth 6 -Compress)){throw 'Existing PostgreSQL5432 observation changed; runtime not marked stopped.'}
$owner.state='stopped';$owner|Add-Member -NotePropertyName stoppedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o')) -Force
$owner|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $ownerPath -Encoding utf8
Write-Output 'Only verified owned helpers stopped; wrapper logs, evidence and dedicated data retained; PostgreSQL5432 unchanged.'
