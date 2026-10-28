#requires -Version 7.4
function Read-PlanningRealRagProfile {
    param([hashtable]$Profile)
    if(-not $Profile.ContainsKey('realRag')){throw 'An explicit real Rag profile is required for a new worker publication.'}
    $entry=$Profile.realRag
    if((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash -ine $entry.sha256){throw 'The real Rag profile changed.'}
    $value=Get-Content -LiteralPath $entry.path -Raw|ConvertFrom-Json -AsHashtable -Depth 40
    if($value.schemaVersion -cne 'saaia-planning-owned-rag-runtime.v1' -or $value.origin -cne 'real_ingestion_worker' -or
       $value.corpusManifest.sha256 -ine $Profile.corpus.manifestSha256 -or
       [IO.Path]::GetFullPath($value.corpusManifest.path) -ine [IO.Path]::GetFullPath($Profile.corpus.manifestPath) -or
       $value.Rag.EmbeddingsBaseUrl -cne 'http://127.0.0.1:22531' -or $value.Rag.QdrantBaseUrl -cne 'http://127.0.0.1:22532' -or
       $value.Rag.EnableRerank -ne $false -or $value.Rag.RequireQdrantAuthInProd -ne $false -or
       $value.Rag.QdrantCollection -cne ('ingestion_'+([Guid]::Parse($value.sourceOwnerRunId)).ToString('N'))){throw 'Real Rag must bind the same new publication and exact owned dependency forwards.'}
    return $value
}

function Get-PlanningSourceForwardings {
    param([hashtable]$Value)
    $entry=$Value.sourceOwnerEvidence
    if($null -eq $entry -or (Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash -ine $entry.sha256){throw 'The owned Linux source proof changed.'}
    $source=Get-Content -LiteralPath $entry.path -Raw|ConvertFrom-Json -AsHashtable -Depth 40
    if($source.schemaVersion -cne 'saaia-linux-ingestion-runtime.v1' -or $source.root -cne $Value.sourceOwnerRoot -or
       $source.runId -cne $Value.sourceOwnerRunId -or $source.endpoints -isnot [hashtable] -or
       $source.endpoints.Count -ne 4 -or @($source.endpoints.Keys|Where-Object{$_ -cnotin @('backend','docling','tei','qdrant')}).Count){throw 'Owned Linux endpoint identity changed.'}
    $ports=@{}
    foreach($role in @('backend','docling','tei','qdrant')){
        $url=$source.endpoints[$role]
        if($url -isnot [string] -or $url -cnotmatch '\Ahttp://127\.0\.0\.1:([0-9]{1,5})\z'){throw 'Owned Linux endpoint must be an exact loopback origin.'}
        $port=[int]$Matches[1]
        if($port -lt 1024 -or $port -gt 65535 -or $port -eq 5432){throw 'Owned Linux qualification port invalid.'}
        $ports[$role]=$port
    }
    if(@($ports.Values|Select-Object -Unique).Count -ne 4){throw 'Owned Linux role ports must be distinct.'}
    return @(@{localHost='127.0.0.1';localPort=22531;remoteHost='127.0.0.1';remotePort=$ports.tei},
             @{localHost='127.0.0.1';localPort=22532;remoteHost='127.0.0.1';remotePort=$ports.qdrant})
}

function Assert-PlanningTunnelIdentity {
    param([hashtable]$Value)
    $entry=$Value.tunnelEvidence
    if((Get-FileHash -LiteralPath $entry.path).Hash -ine $entry.sha256){throw 'The owned tunnel proof changed.'}
    $proof=Get-Content -LiteralPath $entry.path -Raw|ConvertFrom-Json -AsHashtable -Depth 40
    if($proof.schemaVersion -cne 'saaia-owned-ssh-forward.v1' -or $proof.sourceOwnerRoot -cne $Value.sourceOwnerRoot -or
       $proof.hostTarget -cne 'maxime@saaia-server'){throw 'Owned source tunnel mismatch.'}
    $expected=@(Get-PlanningSourceForwardings $Value)
    if(@($proof.forwardings).Count -ne $expected.Count){throw 'Owned SSH forwarding list changed.'}
    for($index=0;$index -lt $expected.Count;$index++){
        $row=$proof.forwardings[$index];$wanted=$expected[$index]
        if($row -isnot [hashtable] -or $row.Count -ne 4 -or @($row.Keys|Where-Object{$_ -cnotin $wanted.Keys}).Count){throw 'Owned SSH forwarding fields changed.'}
        foreach($name in @('localHost','remoteHost')){if($row[$name] -isnot [string] -or $row[$name] -cne $wanted[$name]){throw 'Owned SSH forwarding host changed.'}}
        foreach($name in @('localPort','remotePort')){if(($row[$name] -isnot [int] -and $row[$name] -isnot [long]) -or $row[$name] -ne $wanted[$name]){throw 'Owned SSH forwarding port changed.'}}
    }
    $process=Get-Process -Id ([int]$proof.process.pid) -ErrorAction Stop
    if([IO.Path]::GetFullPath($process.Path) -ine [IO.Path]::GetFullPath($proof.process.path) -or
       [IO.Path]::GetFileName($process.Path) -ine 'ssh.exe' -or
       $process.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$proof.process.startedAtUtc).UtcTicks){throw 'Owned SSH process identity changed.'}
    $actual=Get-CimInstance Win32_Process -Filter ('ProcessId='+$process.Id)
    if($actual.CommandLine -match "[\r\n\x00]"){throw 'Owned SSH command contains an invalid line delimiter.'}
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false,$true).GetBytes($actual.CommandLine)))
    if($hash -ine $proof.process.commandLineSha256){throw 'Owned SSH command binding changed.'}
    # Admit simple Windows argv quoting only; unknown SSH options fail closed.
    $tokens=[regex]::Matches($actual.CommandLine,'\G[ \t]*(?:"(?<argument>[^"]*)"|(?<argument>[^ \t"]+))(?=[ \t]|\z)')
    if($tokens.Count -lt 2 -or $tokens[-1].Index+$tokens[-1].Length -ne $actual.CommandLine.TrimEnd([char[]]" `t").Length){throw 'Owned SSH command has unsupported argument quoting.'}
    $arguments=@($tokens|ForEach-Object{$_.Groups['argument'].Value})
    if([IO.Path]::GetFileName($arguments[0]) -ine 'ssh.exe' -or $arguments[-1] -cne 'maxime@saaia-server'){throw 'Owned SSH command destination changed.'}
    $forwardIndex=0
    for($index=1;$index -lt $arguments.Count-1;$index++){
        $argument=$arguments[$index]
        if($argument -ceq '-L'){
            if($forwardIndex -ge $expected.Count -or $index+1 -ge $arguments.Count-1){throw 'Owned SSH command has an extra or incomplete forward.'}
            $row=$expected[$forwardIndex]
            $binding='{0}:{1}:{2}:{3}' -f $row.localHost,$row.localPort,$row.remoteHost,$row.remotePort
            $index++
            if($arguments[$index] -cne $binding){throw 'Owned SSH command forwarding differs from the Linux owner.'}
            $forwardIndex++
        }elseif($argument -cin @('-N','-T','-n','-q','-v','-vv','-vvv')){
            continue
        }elseif($argument -ceq '-o' -or $argument.StartsWith('-o',[StringComparison]::Ordinal)){
            if($argument -ceq '-o'){
                $index++
                if($index -ge $arguments.Count-1){throw 'Owned SSH command has an incomplete option.'}
                $option=$arguments[$index]
            }else{$option=$argument.Substring(2)}
            if($option -cnotmatch '\A(?:BatchMode=(?:yes|no)|ExitOnForwardFailure=yes|(?:ConnectTimeout|ServerAliveInterval|ServerAliveCountMax)=[0-9]+)\z'){throw 'Owned SSH command option is outside the qualification tunnel grammar.'}
        }else{throw 'Owned SSH command argument is outside the qualification tunnel grammar.'}
    }
    if($forwardIndex -ne $expected.Count){throw 'Owned SSH command must contain exactly the declared forward arguments.'}
    foreach($port in @(22531,22532)){
        $listeners=@(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction Stop)
        if($listeners.Count -eq 0 -or @($listeners|Where-Object{$_.OwningProcess -ne $process.Id -or $_.LocalAddress -cne '127.0.0.1'}).Count -gt 0){throw 'Dependency forward listener is not owned by the root SSH process.'}
    }
}

function Assert-OwnedPlanningRealRag {
    param([hashtable]$Profile,[string]$OutputDirectory,[string]$PythonExecutable)
    $value=Read-PlanningRealRagProfile $Profile
    Assert-PlanningTunnelIdentity $value
    $null=Get-OwnedPaidEnvironment $OutputDirectory
    $null=Invoke-OwnedPaidSql -OutputDirectory $OutputDirectory -Query 'SELECT 1'
    $output=Join-Path $OutputDirectory 'runtime/real-rag-attestation'
    & $PythonExecutable -X utf8 -B (Join-Path $PSScriptRoot 'planning_rag_runtime.py') attest --profile $Profile.realRag.path `
        --owner (Join-Path $OutputDirectory 'environment.json') --output $output --execute *> (Join-Path $OutputDirectory 'real-rag-attestation.private.log')
    if($LASTEXITCODE -ne 0){throw 'Real Rag attestation failed before provider secret access.'}
    $path=Join-Path $output 'result.json'
    $result=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    if($result.passed -ne $true -or $result.providerRequests -ne 0 -or $result.realRagProfileSha256 -ine $Profile.realRag.sha256){throw 'Incomplete real Rag dependency attestation.'}
    return @{path=$path;sha256=(Get-FileHash -LiteralPath $path).Hash;realPointChunkPlanBindings=$result.realPointChunkPlanBindings;tunnelProcessVerifiedByPowerShell=$true}
}

function Get-OwnedPlanningRealRagOverrides {
    param([hashtable]$Profile,[string]$OutputDirectory)
    $value=Read-PlanningRealRagProfile $Profile
    Assert-PlanningTunnelIdentity $value
    $owner=Get-OwnedPaidEnvironment $OutputDirectory
    $result=Get-Content -LiteralPath (Join-Path $OutputDirectory 'runtime/real-rag-attestation/result.json') -Raw|ConvertFrom-Json
    if($result.passed -ne $true -or $result.providerRequests -ne 0 -or $result.realRagProfileSha256 -ine $Profile.realRag.sha256 -or
       $result.targetOwnerRunId -cne $owner.runId -or $result.currentSqlRowsByteExact -ne $true -or $result.nativeTeiInfoExact -ne $true){throw 'A current exact-clone/live Rag attestation is required before starting the worker.'}
    return $value.Rag
}
