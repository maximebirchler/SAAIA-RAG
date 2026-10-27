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

function Assert-PlanningTunnelIdentity {
    param([hashtable]$Value)
    $entry=$Value.tunnelEvidence
    if((Get-FileHash -LiteralPath $entry.path).Hash -ine $entry.sha256){throw 'The owned tunnel proof changed.'}
    $proof=Get-Content -LiteralPath $entry.path -Raw|ConvertFrom-Json
    if($proof.schemaVersion -cne 'saaia-owned-ssh-forward.v1' -or $proof.sourceOwnerRoot -cne $Value.sourceOwnerRoot -or
       $proof.hostTarget -cne 'maxime@saaia-server'){throw 'Owned source tunnel mismatch.'}
    $process=Get-Process -Id ([int]$proof.process.pid) -ErrorAction Stop
    if([IO.Path]::GetFullPath($process.Path) -ine [IO.Path]::GetFullPath($proof.process.path) -or
       [IO.Path]::GetFileName($process.Path) -ine 'ssh.exe' -or
       $process.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$proof.process.startedAtUtc).UtcTicks){throw 'Owned SSH process identity changed.'}
    $actual=Get-CimInstance Win32_Process -Filter ('ProcessId='+$process.Id)
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false,$true).GetBytes($actual.CommandLine)))
    if($hash -ine $proof.process.commandLineSha256 -or -not $actual.CommandLine.Contains('127.0.0.1:22531:127.0.0.1:22523') -or
       -not $actual.CommandLine.Contains('127.0.0.1:22532:127.0.0.1:22524') -or -not $actual.CommandLine.Contains('maxime@saaia-server')){throw 'Owned SSH command binding changed.'}
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
