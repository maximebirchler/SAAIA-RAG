#requires -Version 7.4
# Dot-sourcing declares helpers only. Live actions require the already-owned runner and its callbacks.
function Assert-OwnedLifecyclePath {
    param([Parameter(Mandatory)][string]$Path)
    $cursor=[IO.Path]::GetFullPath($Path)
    while($cursor){
        if(Test-Path -LiteralPath $cursor){
            if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Lifecycle paths cannot contain reparse points.'}
        }
        $parent=[IO.Path]::GetDirectoryName($cursor)
        if($parent -ceq $cursor){break};$cursor=$parent
    }
}

function Assert-OwnedIngestionLifecycleInput {
    param([string]$RepositoryRoot,[string]$FixtureDirectory,[string]$PrivateDirectory,[object]$Owner,
        [string]$SourceDirectory,[object]$Case,[object]$Document,[string]$QdrantUrl,[string]$QdrantCollection)
    $repo=[IO.Path]::GetFullPath($RepositoryRoot)
    $fixture=[IO.Path]::GetFullPath($FixtureDirectory)
    $private=[IO.Path]::GetFullPath($PrivateDirectory)
    $campaign=[IO.Path]::GetDirectoryName($fixture)
    $artifacts=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $privatePrefix=(Join-Path $campaign 'private-observations').TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(-not $fixture.StartsWith($artifacts,[StringComparison]::OrdinalIgnoreCase) -or
       $Owner.schemaVersion -cne 'saaia-winui-local-runtime.v1' -or
       [IO.Path]::GetFullPath($Owner.outputDirectory) -ine $fixture -or
       [IO.Path]::GetFullPath($Owner.documentsRoot) -ine (Join-Path $fixture 'runtime/documents') -or
       -not $private.StartsWith($privatePrefix,[StringComparison]::OrdinalIgnoreCase)) {throw 'Lifecycle requires exact owned campaign, documents and private paths.'}
    Assert-IngestionLoopbackUrl $Owner.backendUrl
    Assert-IngestionLoopbackUrl $QdrantUrl
    if($QdrantCollection -cnotmatch '^ingestion_[a-f0-9]{32}$' -or
       $Case.caseId -cnotmatch '^[a-z0-9][a-z0-9-]{0,95}$' -or
       $Case.generator -cnotmatch '^[a-z0-9_]{1,96}$' -or
       $Case.sourceSha256 -cnotmatch '^[a-f0-9]{64}$' -or
       $Document.docPath -cne ($Case.caseId+'.pdf') -or
       $Document.sourceSha256 -ine $Case.sourceSha256 -or
       [int]$Document.indexedVersion -le 0 -or
       $Document.activeVectorPlanFingerprint -cnotmatch '^[a-f0-9]{64}$') {throw 'Lifecycle requires one published, generated, hash-pinned synthetic document.'}
    $id=[Guid]::Empty
    if(-not [Guid]::TryParse([string]$Document.docId,[ref]$id) -or $id -eq [Guid]::Empty){throw 'Lifecycle document identity is invalid.'}
    $source=[IO.Path]::GetFullPath((Join-Path $SourceDirectory ($Case.caseId+'.pdf')))
    $target=[IO.Path]::GetFullPath((Join-Path $Owner.documentsRoot $Document.docPath))
    if($source -ieq $target){throw 'Lifecycle source and owned working PDF must be distinct.'}
    foreach($path in @($fixture,$private,$source,$target)){Assert-OwnedLifecyclePath $path}
    foreach($path in @($source,$target)){
        if(-not [IO.File]::Exists($path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $Case.sourceSha256){throw 'Lifecycle source bytes differ from the pinned manifest.'}
    }
    return [pscustomobject]@{fixture=$fixture;private=$private;source=$source;target=$target;docId=$id.ToString();docPath=$Document.docPath;campaign=$campaign}
}

function Get-OwnedLifecycleVectorPointId {
    param([Parameter(Mandatory)][Guid]$ChunkId,[Parameter(Mandatory)][string]$PlanFingerprint)
    if($PlanFingerprint -cnotmatch '^[a-f0-9]{64}$'){throw 'Lifecycle needs a complete vector-plan fingerprint.'}
    $bytes=[Text.Encoding]::UTF8.GetBytes('qdrant|'+$ChunkId.ToString('N')+'|'+$PlanFingerprint)
    $hash=[Security.Cryptography.SHA256]::HashData($bytes)
    return [Guid]::new([byte[]]$hash[0..15]).ToString()
}

function Invoke-OwnedIngestionLifecycle {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepositoryRoot,[Parameter(Mandatory)][string]$FixtureDirectory,
        [Parameter(Mandatory)][string]$PrivateDirectory,[Parameter(Mandatory)][object]$Owner,
        [Parameter(Mandatory)][Collections.IDictionary]$Headers,[Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][object]$Case,[Parameter(Mandatory)][object]$Document,
        [Parameter(Mandatory)][string]$QdrantUrl,[Parameter(Mandatory)][string]$QdrantCollection,
        [Parameter(Mandatory)][scriptblock]$AssertOwnership,[ValidateRange(30,1800)][int]$MaximumWaitSeconds=300)
    $scope=Assert-OwnedIngestionLifecycleInput $RepositoryRoot $FixtureDirectory $PrivateDirectory $Owner $SourceDirectory $Case $Document $QdrantUrl $QdrantCollection
    if(Test-Path -LiteralPath $scope.private){throw 'Lifecycle evidence directory must be fresh.'}
    $null=& $AssertOwnership
    [IO.Directory]::CreateDirectory($scope.private)|Out-Null
    $result=[ordered]@{schemaVersion='saaia-owned-ingestion-lifecycle.v1';executed=$true;passed=$false;
        caseId=$Case.caseId;docId=$scope.docId;sourceSha256=$Case.sourceSha256;startedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
        canonicalRowsSeeded=$false;paidProviderCalls=0;steps=@();cancel=[ordered]@{status='NON_EXECUTED';reason='not_attempted';upstreamEngineCancellationVerified=$false};
        limits=@{maximumWaitSecondsPerJob=$MaximumWaitSeconds;cancelObservationSeconds=30;singleOwnedDocument=$true;sqlReadOnly=$true}}
    $targetPresent=$true;$phase='initial'
    $query=[string]@($Case.expectations.criticalFacts)[0].text
    if([string]::IsNullOrWhiteSpace($query) -or $query.Length -gt 300){throw 'Lifecycle requires a bounded corpus query.'}

    function Save-LifecyclePrivate([string]$Name,[object]$Value){
        $path=Join-Path $scope.private $Name
        if([IO.Path]::GetFileName($Name) -cne $Name -or (Test-Path -LiteralPath $path)){throw 'Lifecycle capture names must be new simple files.'}
        [IO.File]::WriteAllText($path,($Value|ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
    }
    function Assert-LifecycleOwned {
        $null=& $AssertOwnership
        foreach($path in @($scope.source,$scope.target,$scope.private)){Assert-OwnedLifecyclePath $path}
        if((Get-FileHash -LiteralPath $scope.source -Algorithm SHA256).Hash -ine $Case.sourceSha256){throw 'Pinned lifecycle source changed.'}
        if($targetPresent -and (-not [IO.File]::Exists($scope.target) -or (Get-FileHash -LiteralPath $scope.target -Algorithm SHA256).Hash -ine $Case.sourceSha256)){throw 'Owned lifecycle PDF changed.'}
        if(-not $targetPresent -and (Test-Path -LiteralPath $scope.target)){throw 'Lifecycle PDF reappeared outside its explicit restore phase.'}
    }
    function Invoke-LifecycleApi([string]$Label,[string]$Route,[object]$Body=$null,[switch]$Qdrant){
        Assert-LifecycleOwned
        $origin=if($Qdrant){$QdrantUrl}else{$Owner.backendUrl}
        $arguments=@{Uri=$origin.TrimEnd('/')+$Route;TimeoutSec=30;Method='Get'}
        if(-not $Qdrant){$arguments.Headers=$Headers}
        if($null -ne $Body){$arguments.Method='Post';$arguments.ContentType='application/json';$arguments.Body=$Body|ConvertTo-Json -Depth 20 -Compress}
        $response=Invoke-RestMethod @arguments
        Save-LifecyclePrivate ($Label+'.json') $response
        return $response
    }
    function Get-LifecycleSnapshot([string]$Label){
        Assert-LifecycleOwned
        $sql=@"
SELECT json_build_object('activeJobs',(SELECT count(*) FROM ingestion_jobs WHERE status IN ('queued','running','paused')),
 'documents',COALESCE((SELECT json_agg(json_build_object('docId',d.doc_id,'docPath',d.doc_path,'status',d.status,
 'ingestionVersion',d.ingestion_version,'indexedVersion',d.indexed_version,'plan',d.ingestion_vector_plan_fingerprint,
 'sourceSha256',encode(r.source_hash,'hex'),'revisionId',r.revision_id,
 'chunks',COALESCE((SELECT json_agg(json_build_object('chunkId',c.retrieval_chunk_id,'text',c.text_content,
 'checksum',encode(c.checksum,'hex')) ORDER BY c.chunk_index) FROM retrieval_chunks c WHERE c.revision_id=r.revision_id),'[]'::json)))
 FROM documents d LEFT JOIN document_revisions r ON r.tenant_id=d.tenant_id AND r.doc_id=d.doc_id AND r.indexed_version=d.indexed_version
 WHERE d.doc_id='$($scope.docId)'::uuid AND d.doc_path='$($scope.docPath)'),'[]'::json))
"@
        $snapshot=Invoke-OwnedPaidSql -OutputDirectory $scope.fixture -Query $sql|ConvertFrom-Json
        Save-LifecyclePrivate ($Label+'-sql.json') $snapshot
        if(@($snapshot.documents).Count -ne 1){throw 'Lifecycle SQL document scope is not unique.'}
        return $snapshot
    }
    function Wait-LifecycleJob([Guid]$JobId,[string]$Label){
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds($MaximumWaitSeconds);$poll=0
        do{
            $jobs=Invoke-LifecycleApi ($Label+'-jobs-'+$poll.ToString('D4')) '/ingestion/jobs?limit=200'
            $rows=@($jobs.items|Where-Object{[string]$_.jobId -ieq $JobId.ToString()})
            if($rows.Count -ne 1 -or $rows[0].docPath -cne $scope.docPath){throw 'Lifecycle job identity does not match its enqueue.'}
            $job=$rows[0]
            if($job.status -in @('done','failed','canceled','paused')){return $job}
            if([DateTimeOffset]::UtcNow -ge $deadline){throw 'Lifecycle job exceeded its bounded timeout.'}
            $poll++;Start-Sleep -Milliseconds 500
        }while($true)
    }
    function Enqueue-Lifecycle([string]$Label,[string]$Action,[int]$PreviousVersion){
        $job=Invoke-LifecycleApi ($Label+'-enqueue') '/ingest/enqueue' @{docPath=$scope.docPath;action=$Action}
        $id=[Guid]::Empty
        if([string]$job.docId -ine $scope.docId -or $job.docPath -cne $scope.docPath -or
           -not [Guid]::TryParse([string]$job.jobId,[ref]$id) -or $id -eq [Guid]::Empty -or [int]$job.version -le $PreviousVersion){throw 'Lifecycle enqueue reused a generation or changed identity.'}
        return $job
    }
    function Assert-LifecyclePublication([object]$Snapshot,[string]$Label){
        $state=@($Snapshot.documents)[0];$chunks=@($state.chunks)
        if([int]$Snapshot.activeJobs -ne 0 -or $state.status -cne 'indexed' -or [int]$state.indexedVersion -le 0 -or
           $state.plan -cnotmatch '^[a-f0-9]{64}$' -or $state.sourceSha256 -ine $Case.sourceSha256 -or -not $chunks.Count){throw 'Lifecycle did not retain a complete current publication.'}
        $identities=@{}
        foreach($chunk in $chunks){
            $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([string]$chunk.text))).ToLowerInvariant()
            if($hash -cne $chunk.checksum){throw 'Lifecycle chunk content/checksum differs.'}
            $id=Get-OwnedLifecycleVectorPointId ([Guid]$chunk.chunkId) $state.plan
            if($identities.ContainsKey($id)){throw 'Lifecycle point identity is duplicated.'};$identities[$id]=[string]$chunk.chunkId
        }
        $pointIds=@($identities.Keys|Sort-Object);$seen=@{}
        for($offset=0;$offset -lt $pointIds.Count;$offset+=128){
            $last=[Math]::Min($offset+127,$pointIds.Count-1)
            $points=Invoke-LifecycleApi ($Label+'-vectors-'+$offset) ('/collections/'+$QdrantCollection+'/points') @{
                ids=@($pointIds[$offset..$last]);with_payload=@('chunk_id','ingestion_plan_fingerprint','ingestion_version','doc_id');with_vector=$false} -Qdrant
            foreach($point in @($points.result)){
                $id=[string]$point.id
                if(-not $identities.ContainsKey($id) -or $seen.ContainsKey($id) -or
                   [string]$point.payload.chunk_id -ine $identities[$id] -or [string]$point.payload.doc_id -ine $scope.docId -or
                   $point.payload.ingestion_plan_fingerprint -cne $state.plan -or [int]$point.payload.ingestion_version -ne [int]$state.indexedVersion){throw 'Lifecycle Qdrant point differs from the current SQL plan.'}
                $seen[$id]=$true
            }
        }
        if($seen.Count -ne $pointIds.Count){throw 'Lifecycle current vector publication is incomplete.'}
        $search=Invoke-LifecycleApi ($Label+'-search') '/rag/search' @{query=$query;docId=$scope.docId;docPath=$scope.docPath;
            sourceBackedCanonical=$true;topK=3;candidates=24;maxPerDoc=3;maxPerPage=3;includeDiagnostics=$true}
        if(-not @($search.items).Count){throw 'Lifecycle search did not return current synthetic evidence.'}
        foreach($item in @($search.items)){
            if([string]$item.docId -ine $scope.docId -or $item.revisionId -ine $state.revisionId -or
               $item.sourceHash -ine $Case.sourceSha256 -or [string]$item.chunkId -notmatch '^[a-f0-9-]{36}$' -or
               [string]$item.chunkId -notin @($chunks.chunkId)){throw 'Lifecycle search exposed an obsolete source identity.'}
        }
        return [pscustomobject]@{phase=$Label;docId=$scope.docId;revisionId=$state.revisionId;indexedVersion=[int]$state.indexedVersion;
            ingestionVersion=[int]$state.ingestionVersion;sourceSha256=$state.sourceSha256;plan=$state.plan;chunkCount=$chunks.Count;
            pointIds=$pointIds;currentRagItems=@($search.items).Count;obsoleteRagIdentities=0;activeJobs=0}
    }
    try{
        $initial=Get-LifecycleSnapshot 'initial'
        $baseline=Assert-LifecyclePublication $initial 'initial'
        if($baseline.indexedVersion -ne [int]$Document.indexedVersion -or $baseline.plan -cne $Document.activeVectorPlanFingerprint){throw 'Initial lifecycle publication changed after runner validation.'}
        $result.steps+=@($baseline)
        $phase='unchanged-upsert'
        $upsert=Enqueue-Lifecycle $phase 'upsert' $baseline.ingestionVersion
        $job=Wait-LifecycleJob ([Guid]$upsert.jobId) $phase
        if($job.status -cne 'done'){throw 'Unchanged-source real worker upsert did not complete.'}
        $afterUpsert=Assert-LifecyclePublication (Get-LifecycleSnapshot $phase) $phase
        if($afterUpsert.indexedVersion -ne [int]$upsert.version -or $afterUpsert.revisionId -ieq $baseline.revisionId -or
           $afterUpsert.plan -ceq $baseline.plan -or @($afterUpsert.pointIds|Where-Object{$_ -in $baseline.pointIds}).Count){throw 'Unchanged-source upsert reused published revision, plan or points.'}
        $result.steps+=@($afterUpsert)
        $phase='delete'
        Assert-LifecycleOwned
        Remove-Item -LiteralPath $scope.target -Force -ErrorAction Stop
        $targetPresent=$false
        $deleted=Enqueue-Lifecycle $phase 'delete' $afterUpsert.ingestionVersion
        $job=Wait-LifecycleJob ([Guid]$deleted.jobId) $phase
        $snapshot=Get-LifecycleSnapshot $phase;$state=@($snapshot.documents)[0]
        if($job.status -cne 'done' -or [int]$snapshot.activeJobs -ne 0 -or $state.status -cne 'deleted' -or
           [int]$state.indexedVersion -ne 0 -or [int]$state.ingestionVersion -ne [int]$deleted.version -or $null -ne $state.plan){throw 'Lifecycle delete did not preserve its generation while deactivating publication.'}
        $empty=Invoke-LifecycleApi ($phase+'-search') '/rag/search' @{query=$query;docId=$scope.docId;sourceBackedCanonical=$true;topK=3;candidates=24}
        if(@($empty.items).Count){throw 'Deleted lifecycle document remained searchable.'}
        $result.steps+=@([pscustomobject]@{phase=$phase;ingestionVersion=[int]$state.ingestionVersion;indexedVersion=0;activeJobs=0;currentRagItems=0})
        $phase='recreate-upsert'
        Assert-LifecycleOwned
        if(Test-Path -LiteralPath $scope.target){throw 'Lifecycle target unexpectedly reappeared.'}
        Copy-Item -LiteralPath $scope.source -Destination $scope.target -ErrorAction Stop
        $targetPresent=$true;Assert-LifecycleOwned
        $recreate=Enqueue-Lifecycle $phase 'upsert' ([int]$state.ingestionVersion)
        $job=Wait-LifecycleJob ([Guid]$recreate.jobId) $phase
        if($job.status -cne 'done'){throw 'Recreated-source real worker upsert did not complete.'}
        $current=Assert-LifecyclePublication (Get-LifecycleSnapshot $phase) $phase
        if($current.indexedVersion -ne [int]$recreate.version -or $current.revisionId -ieq $afterUpsert.revisionId){throw 'Lifecycle recreate reused an abandoned revision.'}
        $result.steps+=@($current)
        $phase='cancel-running'
        $cancelJob=Enqueue-Lifecycle $phase 'upsert' $current.ingestionVersion
        $cancelDeadline=[DateTimeOffset]::UtcNow.AddSeconds(30);$poll=0;$running=$null
        do{
            $jobs=Invoke-LifecycleApi ($phase+'-observe-'+$poll.ToString('D4')) '/ingestion/jobs?limit=200'
            $observed=@($jobs.items|Where-Object{[string]$_.jobId -ieq [string]$cancelJob.jobId})
            if($observed.Count -ne 1 -or $observed[0].docPath -cne $scope.docPath){throw 'Cancel observation left the owned job scope.'}
            if($observed[0].status -ceq 'running'){$running=$observed[0];break}
            if($observed[0].status -in @('done','failed','canceled','paused') -or [DateTimeOffset]::UtcNow -ge $cancelDeadline){break}
            $poll++;Start-Sleep -Milliseconds 100
        }while($true)
        if($running){
            $payload=$running.payload;if($payload -is [string]){$payload=$payload|ConvertFrom-Json}
            $observedPhase=[string]$payload.progress.phase
            if($observedPhase -cnotmatch '^[a-z_]{0,64}$'){$observedPhase='unreported'}
            $ack=Invoke-LifecycleApi ($phase+'-ack') '/admin/jobs/cancel' @{jobId=[string]$cancelJob.jobId}
            $terminal=Wait-LifecycleJob ([Guid]$cancelJob.jobId) ($phase+'-terminal')
            if($terminal.status -ceq 'canceled'){
                if($ack.canceled -isnot [bool] -or $ack.canceled -ne $true){
                    $result.cancel.status='NOT_CONFIRMED';$result.cancel.reason='api_cancellation_not_acknowledged'
                    $result.cancel['apiAcknowledged']=$false
                    throw 'Lifecycle cannot attribute a canceled terminal to an unacknowledged API cancellation.'
                }
                $afterCancel=Assert-LifecyclePublication (Get-LifecycleSnapshot $phase) $phase
                if($afterCancel.indexedVersion -ne $current.indexedVersion -or $afterCancel.plan -cne $current.plan -or
                   $afterCancel.ingestionVersion -lt [int]$cancelJob.version){throw 'Cancel replaced the previous publication or reused its abandoned generation.'}
                $result.cancel=[ordered]@{status='PASS';jobId=$cancelJob.jobId;abandonedVersion=[int]$cancelJob.version;observedRunning=$true;
                    observedPhase=$observedPhase;extractionPhaseObserved=($observedPhase -in @('extracting','document_intelligence'));
                    previousPublicationPreserved=$true;activeJobs=0;apiAcknowledged=[bool]$ack.canceled;
                    upstreamEngineCancellationVerified=$false}
                $result.steps+=@($afterCancel)
            }elseif($terminal.status -ceq 'done'){$result.cancel.reason='job_completed_before_cancel_effect'}
            else{throw 'Running cancel ended in an unexpected state.'}
        }else{$result.cancel.reason='running_state_not_observed_within_bound'}
        if($result.cancel.status -cne 'PASS'){
            $terminal=Wait-LifecycleJob ([Guid]$cancelJob.jobId) 'cancel-unavailable-terminal'
            if($terminal.status -cne 'done'){throw 'Unavailable cancellation left an unsuccessful or active job.'}
            $result.steps+=@(Assert-LifecyclePublication (Get-LifecycleSnapshot 'final') 'final')
        }
        $result.passed=$true
    }catch{
        $result['failureType']=$_.Exception.GetType().Name;$result['failedPhase']=$phase
        Save-LifecyclePrivate 'failure.private.json' @{error=($_|Out-String)}
    }finally{
        if(-not [IO.File]::Exists($scope.target)){
            try{Assert-LifecycleOwned;Copy-Item -LiteralPath $scope.source -Destination $scope.target -ErrorAction Stop;$targetPresent=$true;Assert-LifecycleOwned;$result['sourceRestoredAfterFailure']=$true}
            catch{$result.passed=$false;$result['sourceRestoreFailureType']=$_.Exception.GetType().Name}
        }
        $result['finishedAtUtc']=[DateTimeOffset]::UtcNow.ToString('o')
        $path=Join-Path $scope.campaign 'lifecycle-verification.json'
        if(Test-Path -LiteralPath $path){throw 'Lifecycle public proof must be new.'}
        [IO.File]::WriteAllText($path,($result|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
    }
    return [pscustomobject]$result
}
