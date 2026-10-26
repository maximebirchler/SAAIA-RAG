#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[switch]$Execute,[string]$PlanOutputPath='')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'OwnedDependencies.ps1')
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$manifest=Join-Path $output 'dependencies.json'
$owner=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json
$validated=Get-ValidatedDependencyOwner -Owner $owner -OutputDirectory $output -RepositoryRoot $repo
$manifestSha=(Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
$conhost=[IO.Path]::GetFullPath((Join-Path $env:WINDIR 'System32/conhost.exe'))
function Get-DependencySnapshot([int]$ProcessId){
    $process=Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if(-not $process){return $null}
    $cim=Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId"
    if(-not $cim){return $null}
    if(-not $cim.ExecutablePath -or -not $cim.CommandLine -or -not $process.Path -or
       [IO.Path]::GetFullPath($cim.ExecutablePath) -ine [IO.Path]::GetFullPath($process.Path)){throw 'The exact owned process identity is unreadable.'}
    return [pscustomobject]@{pid=$process.Id;parentPid=[int]$cim.ParentProcessId;startedAtUtc=$process.StartTime.ToUniversalTime().ToString('o');
        cimStartedAtUtc=$cim.CreationDate.ToUniversalTime().ToString('o');executable=$process.Path;commandLine=$cim.CommandLine}
}
function Get-DependencyListeners([int]$Port){return @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)}
$plans=@();$allSnapshots=@()
foreach($entry in @($validated.entries|Sort-Object {switch($_.name){'docling-wrapper'{0};'docling-upstream'{1};'local-embeddings'{2};'qdrant'{3}}})){
    $role=$entry.name
    $spec=Read-ValidatedDependencySpecification -Entry $entry -ValidatedOwner $validated -RepositoryRoot $repo
    $root=Get-DependencySnapshot ([int]$entry.pid)
    $children=@();$queue=[Collections.Generic.Queue[object]]::new()
    if($root){
        $expectedRoot=[pscustomobject]@{pid=[int]$entry.pid;startedAtUtc=$entry.startedAtUtc;executable=$entry.path;commandLine=$root.commandLine;parentPid=$root.parentPid}
        $null=Assert-DependencyProcessIdentity $expectedRoot $root
        if(-not(Test-DependencyCommandLine $entry.path @('-NoProfile','-File',(Join-Path $repo 'tools/validation-winui/run-helper.ps1'),'-Specification',$entry.specification) $root.commandLine)){throw 'Wrapper command line does not own this exact process specification.'}
        $root|Add-Member -NotePropertyName depth -NotePropertyValue 0
        $root|Add-Member -NotePropertyName kind -NotePropertyValue 'wrapper'
        $queue.Enqueue($root)
    }elseif(@(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($entry.pid)").Count){throw 'A missing wrapper still has descendants; historical ownership cannot be reconstructed safely.'}
    while($queue.Count){
        $parent=$queue.Dequeue()
        if($parent.depth -ge 8 -or $children.Count -gt 64){throw 'Unexpected dependency process topology.'}
        $directWork=0
        foreach($cimChild in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($parent.pid)")){
            $child=Get-DependencySnapshot ([int]$cimChild.ProcessId)
            if(-not $child){continue}
            if($child.parentPid -ne $parent.pid -or ([DateTimeOffset]$child.startedAtUtc) -lt ([DateTimeOffset]$parent.startedAtUtc)){throw 'Dependency child parent/time identity mismatch.'}
            $kind=$null
            if([IO.Path]::GetFullPath($child.executable) -ieq $conhost){
                if($child.commandLine -notmatch '(?i)conhost\.exe\s+0x[0-9a-f]+\s*$'){throw 'Unexpected owned console command line.'}
                $kind='console'
            }elseif($parent.kind -ceq 'wrapper' -and [IO.Path]::GetFullPath($child.executable) -ieq $spec.executable){
                if(-not(Test-DependencyCommandLine $spec.executable $spec.arguments $child.commandLine)){throw 'Direct dependency arguments changed.'}
                $kind='work';$directWork++
            }elseif($parent.kind -ceq 'work' -and $spec.pythonBase -and
                [IO.Path]::GetFullPath($parent.executable) -ieq $spec.executable -and
                [IO.Path]::GetFullPath($child.executable) -ieq [IO.Path]::GetFullPath($spec.pythonBase)){
                if(-not(Test-DependencyCommandLine $spec.pythonBase $spec.arguments $child.commandLine)){throw 'Base Python arguments changed.'}
                $kind='work';$directWork++
            }else{throw 'Unexpected dependency descendant; refusing any process mutation.'}
            if($directWork -gt 1){throw 'Ambiguous dependency work children.'}
            $child|Add-Member -NotePropertyName depth -NotePropertyValue ($parent.depth+1)
            $child|Add-Member -NotePropertyName kind -NotePropertyValue $kind
            $null=Assert-DependencyProcessIdentity $child $child
            $children+=,$child;$queue.Enqueue($child)
        }
    }
    $snapshots=@($children)+@($root|Where-Object{$_})
    $allSnapshots+=$snapshots
    $plans+=,[pscustomobject]@{role=$role;entry=$entry;spec=$spec;root=$root;children=$children;port=$validated.ports[$role]}
}
function Assert-AllDependencyListeners {
    foreach($role in $validated.ports.Keys){
        $allowed=@($plans|Where-Object role -ceq $role|ForEach-Object{$_.children|Where-Object kind -ceq 'work'|ForEach-Object{[int]$_.pid}})
        Assert-DependencyListenerOwnership -Listeners @(Get-DependencyListeners $validated.ports[$role]) -AllowedProcessIds $allowed
    }
}
function Assert-DependencyPlanUnchanged {
    if((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -cne $manifestSha){throw 'Dependency manifest changed since prevalidation.'}
    foreach($item in $plans){if((Get-FileHash -LiteralPath $item.entry.specification -Algorithm SHA256).Hash -cne $item.spec.specificationSha256){throw 'Dependency process specification changed since prevalidation.'}}
}
Assert-AllDependencyListeners
$public=[ordered]@{schemaVersion='saaia-owned-dependency-stop.v2';capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
    executeRequested=[bool]$Execute;prevalidationPassed=$true;partialStartup=$validated.partialStartup;
    manifestSha256=$manifestSha;sourceSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;
    processesStopped=$false;listenersReleased=$false;wrapperLogsFlushed=$false;
    roles=@($plans|ForEach-Object{@{role=$_.role;port=$_.port;wrapperPid=$_.entry.pid;wrapperActive=($null -ne $_.root);
        descendants=@($_.children|ForEach-Object{@{pid=$_.pid;parentPid=$_.parentPid;depth=$_.depth;kind=$_.kind;executable=[IO.Path]::GetFileName($_.executable);startedAtUtc=$_.startedAtUtc}})}})}
if($PlanOutputPath){
    $planPath=[IO.Path]::GetFullPath($PlanOutputPath)
    if(-not $planPath.StartsWith($output+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $planPath)){throw 'Stop plan must be a new file inside its owned evidence directory.'}
    $stream=[IO.FileStream]::new($planPath,[IO.FileMode]::CreateNew)
    try{$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($public|ConvertTo-Json -Depth 12));$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
}
if(-not $Execute){$public|ConvertTo-Json -Depth 12;return}
$proofPath=Join-Path $output ('dependency-stop-'+[Guid]::NewGuid().ToString('N')+'.json')
try{
    Assert-DependencyPlanUnchanged
    Invoke-OwnedDependencyStopSequence -Plans $plans -StopWork {
            param($child)
            # The wrapper remains alive to drain both output pipes.
            Assert-DependencyPlanUnchanged;Assert-AllDependencyListeners
            if(Assert-DependencyProcessIdentity $child (Get-DependencySnapshot $child.pid)){
                $process=Get-Process -Id $child.pid -ErrorAction Stop
                $process.Kill()
                if(-not $process.WaitForExit(10000)){throw 'Owned dependency work process did not exit.'}
            }
    } -WaitWrapper {
            param($rootSnapshot)
            if(-not(Assert-DependencyProcessIdentity $rootSnapshot (Get-DependencySnapshot $rootSnapshot.pid))){return $true}
            $wrapper=Get-Process -Id $rootSnapshot.pid -ErrorAction Stop
            return $wrapper.WaitForExit(20000)
    } -FinishConsole {
            param($console)
            if(Assert-DependencyProcessIdentity $console (Get-DependencySnapshot $console.pid)){
                $process=Get-Process -Id $console.pid -ErrorAction Stop
                if(-not $process.WaitForExit(5000)){
                    $null=Assert-DependencyProcessIdentity $console (Get-DependencySnapshot $console.pid)
                    $process.Kill()
                    if(-not $process.WaitForExit(10000)){throw 'Owned dependency console did not exit.'}
                }
            }
    } -VerifyStopped {
        foreach($snapshot in $allSnapshots){if(Assert-DependencyProcessIdentity $snapshot (Get-DependencySnapshot $snapshot.pid)){throw 'An owned dependency process remains alive.'}}
        foreach($port in $validated.ports.Values){if(@(Get-DependencyListeners $port).Count){throw 'A dependency listener remains or was rebound; stopped state is not proven.'}}
    }
    Assert-DependencyPlanUnchanged
    $public.processesStopped=$true;$public.listenersReleased=$true;$public.wrapperLogsFlushed=$true
    $public['completedAtUtc']=[DateTimeOffset]::UtcNow.ToString('o')
    $owner.state='stopped'
    $owner|Add-Member -NotePropertyName stoppedAtUtc -NotePropertyValue $public.completedAtUtc -Force
    [IO.File]::WriteAllText($manifest,($owner|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($proofPath,($public|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    'Only exact owned dependency descendants stopped; wrappers flushed logs, every observed PID/listener absent, data retained.'
}catch{
    $public['failureType']=$_.Exception.GetType().Name
    [IO.File]::WriteAllText($proofPath,($public|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    throw
}
