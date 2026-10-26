#requires -Version 7.4
# Pure guards shared by start/stop and their offline tests. No process/HTTP calls.
function Assert-DependencyFileHash {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$ExpectedSha256)
    if($ExpectedSha256 -cnotmatch '^[a-f0-9]{64}$' -or -not(Test-Path -LiteralPath $Path -PathType Leaf)){throw 'A pinned dependency file must exist before any service starts.'}
    $actual=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if($actual -cne $ExpectedSha256){throw 'Pinned dependency bytes differ; no service may start.'}
    return $actual
}

function Get-ValidatedDependencyPorts {
    param([Parameter(Mandatory)][int[]]$Ports)
    if(-not $Ports.Count -or @($Ports|Group-Object|Where-Object Count -ne 1).Count -or
       @($Ports|Where-Object{$_ -lt 1024 -or $_ -gt 65535 -or $_ -eq 5432}).Count){throw 'Dependency ports must be distinct unprivileged ports, excluding PostgreSQL5432.'}
    return $Ports
}

function Assert-DependencyProjectPath {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$RepositoryRoot,[Parameter(Mandatory)][string]$Subdirectory)
    $resolved=[IO.Path]::GetFullPath($Path)
    $prefix=[IO.Path]::GetFullPath((Join-Path $RepositoryRoot $Subdirectory)).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'The dependency path is outside its owned project subtree.'}
    return $resolved
}

function Get-ValidatedDependencyOwner {
    param([Parameter(Mandatory)][object]$Owner,[Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][string]$RepositoryRoot)
    $output=Assert-DependencyProjectPath $OutputDirectory $RepositoryRoot 'artifacts'
    if($Owner.schemaVersion -cne 'saaia-local-ingestion-dependencies.v1' -or
       [IO.Path]::GetFullPath($Owner.outputDirectory) -ine $output -or $Owner.state -cnotin @('starting','started_readiness_pending','ready','failed','stopped')){throw 'Not an owned dependency manifest.'}
    $urls=[ordered]@{'docling-wrapper'=$Owner.doclingUrl;'docling-upstream'=$Owner.upstreamUrl;'qdrant'=$Owner.qdrantUrl}
    if($Owner.PSObject.Properties['embeddingUrl']){$urls['local-embeddings']=$Owner.embeddingUrl}
    $ports=@{}
    foreach($role in $urls.Keys){
        $uri=$null
        if(-not[Uri]::TryCreate($urls[$role],[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -cne 'http' -or
           $uri.Host -cne '127.0.0.1' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -cne '/'){
            throw 'Dependency listener declarations must be uncredentialed literal loopback origins.'
        }
        $ports[$role]=$uri.Port
    }
    $null=Get-ValidatedDependencyPorts @($ports.Values)
    $entries=@($Owner.processes)
    if(@($entries|Group-Object pid|Where-Object Count -ne 1).Count -or @($entries|Group-Object name|Where-Object Count -ne 1).Count -or
       @($entries|Where-Object{$_.name -cnotin @($urls.Keys) -or [int]$_.pid -le 0}).Count){throw 'Unknown or duplicate owned dependency process identities.'}
    # A failed/starting bootstrap may have registered only its first processes.
    if($Owner.state -cin @('ready','started_readiness_pending') -and $entries.Count -ne $urls.Count){throw 'A completed startup must declare every dependency role.'}
    foreach($entry in $entries){
        if([IO.Path]::GetFullPath($entry.specification) -ine (Join-Path $output ($entry.name+'-process.json')) -or
           [string]::IsNullOrWhiteSpace($entry.path) -or -not $entry.startedAtUtc){throw 'Invalid owned wrapper specification or identity.'}
    }
    return [pscustomobject]@{output=$output;ports=$ports;entries=$entries;partialStartup=($entries.Count -lt $urls.Count)}
}

function Read-ValidatedDependencySpecification {
    param([Parameter(Mandatory)][object]$Entry,[Parameter(Mandatory)][object]$ValidatedOwner,[Parameter(Mandatory)][string]$RepositoryRoot)
    $spec=Get-Content -LiteralPath $Entry.specification -Raw|ConvertFrom-Json
    if(@($spec.PSObject.Properties.Name|Where-Object{$_ -cnotin @('executable','arguments','stdout','stderr')}).Count -or
       $spec.stdout -ine (Join-Path $ValidatedOwner.output ($Entry.name+'.stdout.log')) -or
       $spec.stderr -ine (Join-Path $ValidatedOwner.output ($Entry.name+'.stderr.log'))){throw 'Dependency log targets must stay inside their owned evidence directory.'}
    $exe=Assert-DependencyProjectPath $spec.executable $RepositoryRoot 'out'
    $pythonBase=$null;$expected=@()
    if($Entry.name -ceq 'qdrant'){
        if([IO.Path]::GetFileName($exe) -ine 'qdrant.exe'){throw 'Unexpected Qdrant executable.'}
        $expected=@('--config-path',(Join-Path $ValidatedOwner.output 'qdrant.yaml'),'--disable-telemetry')
    }else{
        if([IO.Path]::GetFileName($exe) -ine 'python.exe' -or [IO.Path]::GetFileName((Split-Path -Parent $exe)) -ine 'Scripts'){throw 'Expected the owned Python virtual environment launcher.'}
        $venv=Split-Path -Parent (Split-Path -Parent $exe)
        $config=Get-Content -LiteralPath (Join-Path $venv 'pyvenv.cfg')
        $baseLines=@($config|Where-Object{$_ -match '^executable\s*='})
        if($baseLines.Count -ne 1){throw 'A unique base-Python identity is required from the owned virtual environment.'}
        $pythonBase=($baseLines[0] -replace '^executable\s*=\s*','').Trim()
        if(-not[IO.Path]::IsPathFullyQualified($pythonBase) -or [IO.Path]::GetFileName($pythonBase) -ine 'python.exe'){throw 'Invalid base-Python identity.'}
        $runtime=Split-Path -Parent $venv
        switch -CaseSensitive ($Entry.name){
            'docling-upstream' {$expected=@('-m','saaia_docling.upstream','run','--host','127.0.0.1','--port',[string]$ValidatedOwner.ports[$Entry.name],'--workers','1')}
            'docling-wrapper' {$expected=@('-m','uvicorn','saaia_docling.app:app','--host','127.0.0.1','--port',[string]$ValidatedOwner.ports[$Entry.name],'--workers','1','--no-access-log')}
            'local-embeddings' {$expected=@((Join-Path $RepositoryRoot 'tools/validation-ingestion/local_embeddings.py'),'--model-path',
                (Join-Path $runtime 'models/huggingface/hub/models--intfloat--multilingual-e5-base/snapshots/d128750597153bb5987e10b1c3493a34e5a4502a'),'--port',[string]$ValidatedOwner.ports[$Entry.name])}
            default {throw 'Unknown Python dependency role.'}
        }
    }
    $actual=@($spec.arguments)
    if($actual.Count -ne $expected.Count){throw 'Dependency arguments differ from the owned role contract.'}
    for($index=0;$index -lt $expected.Count;$index++){if([string]$actual[$index] -ine [string]$expected[$index]){throw 'Dependency arguments differ from the owned role contract.'}}
    return [pscustomobject]@{specification=$spec;executable=$exe;arguments=$expected;pythonBase=$pythonBase;
        specificationSha256=(Get-FileHash -LiteralPath $Entry.specification -Algorithm SHA256).Hash}
}

function Test-DependencyCommandLine {
    param([Parameter(Mandatory)][string]$Executable,[string[]]$Arguments=@(),[Parameter(Mandatory)][string]$CommandLine)
    $tokens=@($Executable)+$Arguments
    $patterns=foreach($token in $tokens){
        if($token -match '["\r\n\x00]'){throw 'Unexpected quoted/control characters in an owned argument.'}
        $escaped=[regex]::Escape($token)
        if($token -match '\s'){'"'+$escaped+'"'}else{'(?:"'+$escaped+'"|'+$escaped+')'}
    }
    return [regex]::IsMatch($CommandLine,('^\s*'+($patterns -join '\s+')+'\s*$'),[Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant)
}

function Assert-DependencyProcessIdentity {
    param([Parameter(Mandatory)][object]$Expected,[AllowNull()][object]$Actual)
    if($null -eq $Actual){return $false}
    if([int]$Expected.pid -ne [int]$Actual.pid -or
       ([DateTimeOffset]$Expected.startedAtUtc).UtcTicks -ne ([DateTimeOffset]$Actual.startedAtUtc).UtcTicks -or
       [IO.Path]::GetFullPath($Expected.executable) -ine [IO.Path]::GetFullPath($Actual.executable) -or
       ($Expected.PSObject.Properties['parentPid'] -and [int]$Expected.parentPid -ne [int]$Actual.parentPid) -or
       $Expected.commandLine -cne $Actual.commandLine){throw 'Owned process PID/start/executable/parent/command identity changed.'}
    if([Math]::Abs(([DateTimeOffset]$Actual.cimStartedAtUtc - [DateTimeOffset]$Actual.startedAtUtc).TotalMilliseconds) -gt 1){throw 'CIM and process timestamps disagree.'}
    return $true
}

function Assert-DependencyListenerOwnership {
    param([object[]]$Listeners=@(),[int[]]$AllowedProcessIds=@())
    foreach($listener in $Listeners){
        if($listener.LocalAddress -notin @('127.0.0.1','::1') -or [int]$listener.OwningProcess -notin $AllowedProcessIds){throw 'Dependency listener belongs to an unrelated process or a non-loopback address.'}
    }
}

function Invoke-OwnedDependencyStopSequence {
    param([object[]]$Plans=@(),[Parameter(Mandatory)][scriptblock]$StopWork,
        [Parameter(Mandatory)][scriptblock]$WaitWrapper,[Parameter(Mandatory)][scriptblock]$FinishConsole,
        [Parameter(Mandatory)][scriptblock]$VerifyStopped)
    foreach($item in $Plans){
        foreach($child in @($item.children|Where-Object kind -ceq 'work'|Sort-Object depth -Descending)){& $StopWork $child}
        if($item.root -and -not(& $WaitWrapper $item.root)){throw 'Owned wrapper did not finish its log drain; preserved for diagnosis.'}
        foreach($console in @($item.children|Where-Object kind -ceq 'console'|Sort-Object depth -Descending)){& $FinishConsole $console}
    }
    & $VerifyStopped
}
