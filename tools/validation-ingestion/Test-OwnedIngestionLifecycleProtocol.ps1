#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Acknowledged','Rejected')][string]$CancelAcknowledgement='Acknowledged')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'OwnedIngestionConfig.ps1')
. (Join-Path $PSScriptRoot 'OwnedIngestionLifecycle.ps1')
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root=[IO.Path]::GetFullPath($OutputDirectory)
$prefix=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(-not $root.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $root)){throw 'A fresh owned artifacts protocol directory is required.'}
$fixture=Join-Path $root 'local-fixture';$source=Join-Path $root 'synthetic-source';$documents=Join-Path $fixture 'runtime/documents'
foreach($directory in @($source,$documents)){[IO.Directory]::CreateDirectory($directory)|Out-Null}
$text='Synthetic calibration evidence and its independent operating boundaries.'
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
foreach($directory in @($source,$documents)){[IO.File]::WriteAllText((Join-Path $directory 'synthetic-protocol.pdf'),$text,[Text.UTF8Encoding]::new($false))}
$owner=@{schemaVersion='saaia-winui-local-runtime.v1';outputDirectory=$fixture;documentsRoot=$documents;backendUrl='http://127.0.0.1:5129'}
$case=@{caseId='synthetic-protocol';generator='synthetic_protocol';sourceSha256=$hash;expectations=@{criticalFacts=@(@{text='Synthetic calibration'})}}
$docId='00112233-4455-6677-8899-aabbccddeeff'
$document=@{docPath='synthetic-protocol.pdf';docId=$docId;sourceSha256=$hash;indexedVersion=1;activeVectorPlanFingerprint=('a'*64)}
$script:fakeVersion=1;$script:fakeIndexed=1;$script:fakeStatus='indexed';$script:fakeCalls=[Collections.Generic.List[string]]::new()
$script:fakeJobs=[Collections.Generic.List[object]]::new()
function Fake-Revision {return ('11111111-1111-1111-1111-'+$script:fakeIndexed.ToString('D12'))}
function Fake-Chunk {return ('22222222-2222-2222-2222-'+$script:fakeIndexed.ToString('D12'))}
function Fake-Plan {if($script:fakeIndexed -eq 0){return $null};return (([string][char](96+$script:fakeIndexed))*64)}
# Deliberately replace both transports. No real HTTP, SQL, secret read or process is reachable here.
function Invoke-OwnedPaidSql {
    param([string]$OutputDirectory,[string]$Query)
    $script:fakeCalls.Add('sql')
    if($Query -notmatch '^SELECT json_build_object' -or $Query -match '(?i)\b(UPDATE|DELETE|INSERT|ALTER|CREATE|DROP)\b'){throw 'Protocol helper attempted mutable SQL.'}
    $chunks=if($script:fakeIndexed -gt 0){@(@{chunkId=(Fake-Chunk);text=$text;checksum=$hash})}else{@()}
    return (@{activeJobs=@($script:fakeJobs|Where-Object status -in @('queued','running','paused')).Count;documents=@(@{
        docId=$docId;docPath=$document.docPath;status=$script:fakeStatus;ingestionVersion=$script:fakeVersion;indexedVersion=$script:fakeIndexed;
        plan=(Fake-Plan);sourceSha256=$(if($script:fakeIndexed -gt 0){$hash}else{$null});revisionId=$(if($script:fakeIndexed -gt 0){Fake-Revision}else{$null});chunks=$chunks})}|ConvertTo-Json -Depth 20)
}
function Invoke-RestMethod {
    param([Uri]$Uri,[string]$Method,[object]$Headers,[string]$ContentType,[string]$Body,[int]$TimeoutSec)
    $script:fakeCalls.Add('api'+$Uri.AbsolutePath)
    $request=if($Body){$Body|ConvertFrom-Json}else{$null}
    switch($Uri.AbsolutePath){
        '/ingest/enqueue' {
            if($request.docPath -cne $document.docPath){throw 'Protocol enqueue scope changed.'}
            $script:fakeVersion++
            $job=[pscustomobject]@{jobId=[Guid]::NewGuid().ToString();docId=$docId;docPath=$document.docPath;version=$script:fakeVersion;
                status='done';payload=@{progress=@{phase='document_intelligence'}}}
            if($request.action -ceq 'delete'){$script:fakeIndexed=0;$script:fakeStatus='deleted'}
            elseif($script:fakeVersion -eq 5){$job.status='running'}
            else{$script:fakeIndexed=$script:fakeVersion;$script:fakeStatus='indexed'}
            $script:fakeJobs.Add($job);return $job
        }
        '/ingestion/jobs' {return @{items=@($script:fakeJobs.ToArray())}}
        '/admin/jobs/cancel' {
            $selected=@($script:fakeJobs|Where-Object jobId -eq $request.jobId)
            if($selected.Count -ne 1 -or $selected[0].status -cne 'running'){throw 'Protocol must cancel an actually observed running job.'}
            $selected[0].status='canceled';return @{canceled=($CancelAcknowledgement -ceq 'Acknowledged');canceledInMemory=1;runningCancelRequested=1}
        }
        '/rag/search' {
            if($script:fakeIndexed -eq 0){return @{items=@()}}
            return @{items=@(@{docId=$docId;revisionId=(Fake-Revision);sourceHash=$hash;chunkId=(Fake-Chunk)})}
        }
        default {
            if($Uri.AbsolutePath -notmatch '^/collections/ingestion_[a-f0-9]{32}/points$'){throw 'Unexpected protocol route.'}
            $expected=Get-OwnedLifecycleVectorPointId ([Guid](Fake-Chunk)) (Fake-Plan)
            if(@($request.ids).Count -ne 1 -or $request.ids[0] -cne $expected){throw 'Protocol vector identity changed.'}
            return @{result=@(@{id=$expected;payload=@{chunk_id=(Fake-Chunk);doc_id=$docId;ingestion_plan_fingerprint=(Fake-Plan);ingestion_version=$script:fakeIndexed}})}
        }
    }
}
$result=Invoke-OwnedIngestionLifecycle -RepositoryRoot $repo -FixtureDirectory $fixture -PrivateDirectory (Join-Path $root 'private-observations/lifecycle') `
    -Owner $owner -Headers @{'X-Api-Key'='offline-placeholder'} -SourceDirectory $source -Case $case -Document $document `
    -QdrantUrl 'http://127.0.0.1:16341' -QdrantCollection ('ingestion_'+('b'*32)) -AssertOwnership {$script:fakeCalls.Add('owned')}
if($CancelAcknowledgement -ceq 'Rejected'){
    $safeRejection=(-not $result.passed -and $result.cancel.status -cne 'PASS' -and $result.failedPhase -ceq 'cancel-running')
    $proof=@{schemaVersion='saaia-owned-ingestion-lifecycle-cancel-ack-regression.v1';passed=$safeRejection;
        mockedTransport=$true;realServiceCalls=0;realDatabaseOperations=0;credentialsRead=0;modelCalls=0;
        apiCancellationAcknowledged=$false;observedTerminal='canceled';helperPassed=[bool]$result.passed;
        helperCancelStatus=$result.cancel.status;unsafeCancelPassRejected=$safeRejection}
    [IO.File]::WriteAllText((Join-Path $root 'cancel-ack-regression-result.json'),($proof|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    if(-not $safeRejection){throw 'A canceled terminal without an explicit API acknowledgement was incorrectly reported as a validated cancellation.'}
    Write-Output 'Lifecycle negative-ACK regression PASS; mocked transports only.'
    return
}
if(-not $result.passed){throw 'Lifecycle mock protocol failed; inspect its private failure capture.'}
if($result.cancel.status -cne 'PASS' -or $result.cancel.upstreamEngineCancellationVerified -ne $false -or
   @($result.steps).Count -ne 5 -or $script:fakeVersion -ne 5 -or $script:fakeIndexed -ne 4){throw 'Lifecycle protocol state or proof scope is incorrect.'}
for($index=0;$index -lt $script:fakeCalls.Count;$index++){
    if($script:fakeCalls[$index] -ne 'owned' -and ($index -eq 0 -or $script:fakeCalls[$index-1] -ne 'owned')){throw 'A transport was used without its immediately preceding ownership callback.'}
}
if((Get-FileHash -LiteralPath (Join-Path $documents $document.docPath) -Algorithm SHA256).Hash -ine $hash){throw 'Lifecycle did not restore the exact owned PDF bytes.'}
$proof=@{schemaVersion='saaia-owned-ingestion-lifecycle-offline-protocol.v1';passed=$true;mockedTransport=$true;
    realServiceCalls=0;realDatabaseOperations=0;credentialsRead=0;modelCalls=0;ownershipCallbacks=@($script:fakeCalls|Where-Object{$_ -ceq 'owned'}).Count;
    mockedApiCalls=@($script:fakeCalls|Where-Object{$_ -like 'api*'}).Count;mockedSqlReads=@($script:fakeCalls|Where-Object{$_ -ceq 'sql'}).Count;
    stepCount=@($result.steps).Count;runningCancellationExercised=$true;upstreamEngineCancellationVerified=$false;sourceBytesRestored=$true}
[IO.File]::WriteAllText((Join-Path $root 'offline-protocol-result.json'),($proof|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Write-Output 'Lifecycle offline protocol PASS; mocked transports only, no live ingestion claim.'
