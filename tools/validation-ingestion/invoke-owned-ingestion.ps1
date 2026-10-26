#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$DoclingProfilePath,
    [Parameter(Mandatory)][string]$DependenciesManifestPath,
    [Parameter(Mandatory)][string]$PythonExecutable,
    [string]$EmbeddingProfilePath='',
    [string]$CorpusManifestPath='',
    [string[]]$CaseId=@('fr-scan-critical-v2','fr-mixed-critical-v2'),
    [int]$BackendPort=5129,[int]$FixturePort=18091,
    [int]$MaximumWaitSeconds=1800,
    [switch]$VerifyCanonicalSearch,
    [switch]$ValidateLifecycle,
    [switch]$Execute
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'OwnedIngestionConfig.ps1')
if(-not $CorpusManifestPath){$CorpusManifestPath=Join-Path $repo 'config/ingestion-acceptance-gold.v2.json'}
$root=[IO.Path]::GetFullPath($OutputDirectory)
$allowed=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(-not $root.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $root)){throw 'A fresh campaign directory under repository artifacts is required.'}
if($MaximumWaitSeconds -lt 30 -or $MaximumWaitSeconds -gt 14400 -or $BackendPort -eq $FixturePort -or
   $BackendPort -lt 1024 -or $FixturePort -lt 1024 -or 5432 -in @($BackendPort,$FixturePort)){throw 'Invalid local ports or bounded timeout.'}
if(-not [IO.Path]::IsPathFullyQualified($PythonExecutable) -or -not [IO.File]::Exists($PythonExecutable)){throw 'An explicit trusted Python executable is required.'}
$manifest=Get-Content -LiteralPath $CorpusManifestPath -Raw|ConvertFrom-Json
if($manifest.schemaVersion -cnotin @('ingestion_acceptance_gold_v2','ingestion_real_acceptance_v1') -or -not @($manifest.cases).Count -or -not $CaseId.Count -or
   @($CaseId|Group-Object|Where-Object Count -ne 1).Count){throw 'Select a nonempty distinct subset of the pinned acceptance corpus.'}
$cases=@($manifest.cases|Where-Object{$_.caseId -cin $CaseId})
if($cases.Count -ne $CaseId.Count){throw 'Unknown corpus case selection.'}
if($ValidateLifecycle -and ($manifest.schemaVersion -cne 'ingestion_acceptance_gold_v2' -or
   'fr-scan-critical-v2' -cnotin $CaseId)){throw 'Lifecycle validation is limited to the selected disposable synthetic French scan.'}
if($ValidateLifecycle){
    . (Join-Path $PSScriptRoot 'OwnedIngestionLifecycle.ps1')
}
$sourceRoot=[IO.Path]::GetFullPath($SourceDirectory)
foreach($case in $cases){
    if($case.caseId -cnotmatch '^[a-z0-9][a-z0-9-]{0,95}$' -or $case.sourceSha256 -cnotmatch '^[a-f0-9]{64}$' -or
       -not @($case.expectations.criticalFacts).Count){throw 'Malformed corpus identity or independent facts.'}
    if($manifest.schemaVersion -ceq 'ingestion_real_acceptance_v1' -and
       ($manifest.PSObject.Properties.Name -ccontains 'rendererPins' -or $case.PSObject.Properties.Name -ccontains 'generator' -or
        $case.sourceAnnotations.kind -cne 'source_pdf_review' -or $case.sourceAnnotations.sourceSha256 -cne $case.sourceSha256 -or
        [string]::IsNullOrWhiteSpace([string]$case.sourceAnnotations.basis))){throw 'Real PDF annotations must bind the reviewed source, without synthetic generator claims.'}
    $pdf=Join-Path $sourceRoot ($case.caseId+'.pdf')
    if(-not [IO.File]::Exists($pdf) -or (Get-FileHash -LiteralPath $pdf -Algorithm SHA256).Hash -ine $case.sourceSha256){throw 'The selected source PDF does not match the frozen manifest.'}
}
$dependency=Get-Content -LiteralPath $DependenciesManifestPath -Raw|ConvertFrom-Json
if($dependency.schemaVersion -cne 'saaia-local-ingestion-dependencies.v1' -or $dependency.state -cne 'ready'){throw 'Explicit ready local dependencies manifest required.'}
Assert-IngestionLoopbackUrl $dependency.doclingUrl
Assert-IngestionLoopbackUrl $dependency.qdrantUrl
$profile=Get-Content -LiteralPath $DoclingProfilePath -Raw|ConvertFrom-Json -AsHashtable
$docling=if($profile.Contains('DocumentIntelligence')){$profile.DocumentIntelligence}else{$profile}
if($docling.BaseUrl -cne $dependency.doclingUrl){throw 'The observed Docling profile must target the declared local dependency.'}
$dll=Join-Path $repo 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'
if(-not [IO.File]::Exists($dll)){throw 'Build the production backend separately before this campaign.'}
$runId=[Guid]::NewGuid().ToString('N')
$collection='ingestion_'+$runId
$overlay=[ordered]@{
    Ingestion=@{WorkerEnabled=$true;WorkerConcurrency=1;CanonicalArtifactsEnabled=$true;WatcherEnabled=$false;ScannerEnabled=$false;
        MinFileAgeSeconds=0;ReindexIfQdrantEmpty=$false;OcrEnabled=$false;OcrImagePageEnabled=$false;HeavyComputeMaxConcurrency=1}
    Rag=@{QdrantBaseUrl=$dependency.qdrantUrl;QdrantCollection=$collection;QdrantRuntimeRevision='qdrant-1.20.0-local';
        EmbeddingsBaseUrl="http://127.0.0.1:$FixturePort";EmbeddingsModel='fixture-3d';EmbeddingsModelRevision='synthetic-unit-vector-v1';
        EmbeddingsRuntimeRevision='saaia-loopback-protocol-fixture-v1';EnableRerank=$false;RequireQdrantAuthInProd=$false}
    DocumentIntelligence=$docling
}
$embeddingProfile=$null
$embeddingAdapter='deterministic_loopback_3d'
if($EmbeddingProfilePath){
    $embeddingProfile=Read-OwnedEmbeddingProfile -Path $EmbeddingProfilePath
    foreach($key in $embeddingProfile.Rag.Keys){$overlay.Rag[$key]=$embeddingProfile.Rag[$key]}
    $embeddingAdapter=$embeddingProfile.adapter
}
$null=Assert-OwnedIngestionOverlayValues -Overlay $overlay
$plan=[ordered]@{schemaVersion='saaia-owned-ingestion-plan.v1';executeRequested=[bool]$Execute;caseIds=@($cases.caseId);
    sourceCount=$cases.Count;corpusManifestSha256=(Get-FileHash -LiteralPath $CorpusManifestPath -Algorithm SHA256).Hash;
    backendAssemblySha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash;
    dependencyManifestSha256=(Get-FileHash -LiteralPath $DependenciesManifestPath -Algorithm SHA256).Hash;
    profileSha256=(Get-FileHash -LiteralPath $DoclingProfilePath -Algorithm SHA256).Hash;
    backendPort=$BackendPort;fixturePort=$FixturePort;qdrantCollection=$collection;maximumWaitSeconds=$MaximumWaitSeconds;
    workerEnabled=$true;canonicalRowsSeeded=$false;paidProviderEnabled=$false;
    embeddingAdapter=$embeddingAdapter;embeddingsReal=($null -ne $embeddingProfile);vectorValidated=$false;productStatus='TESTE_NON_APPROUVE'}
$plan['verifyCanonicalSearch']=[bool]$VerifyCanonicalSearch
$plan['validateLifecycle']=[bool]$ValidateLifecycle
if($embeddingProfile){$plan['embeddingProfileSha256']=(Get-FileHash -LiteralPath $EmbeddingProfilePath -Algorithm SHA256).Hash}
if(-not $Execute){$plan|ConvertTo-Json -Depth 8;return}
# No credentials, HTTP requests or processes are touched before the Execute gate.
. (Join-Path $repo 'tools/validation-paid/PaidRuntime.ps1')
[IO.Directory]::CreateDirectory($root)|Out-Null
$privateRoot=Join-Path $root 'private-observations'
[IO.Directory]::CreateDirectory($privateRoot)|Out-Null
$fixture=Join-Path $root 'local-fixture'
$overlayPath=Join-Path $root 'worker-overlay.json'
$proof=[ordered]@{schemaVersion='saaia-owned-ingestion-execution.v1';startedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
    passed=$false;executed=$true;plan=$plan;canonicalRowsSeeded=$false;paidProviderCalls=0;
    ingestionWorkerExecuted=$false;sqlPublicationValidated=$false;vectorStorePublicationValidated=$false;vectorValidated=$false;
    cleanup=@{ownedEnvironmentStopped=$false;zeroActiveJobs=$false;externalDependenciesStopped=$false};state='starting'}
$owner=$null;$headers=$null
function Save-Private([string]$Name,[object]$Value){[IO.File]::WriteAllText((Join-Path $privateRoot $Name),($Value|ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))}
function Assert-OwnedIngestionBackend {
    $current=Get-OwnedPaidEnvironment $fixture
    $entries=@($current.processes|Where-Object name -ceq 'backend-runner')
    if($entries.Count -ne 1){throw 'Exactly one owned backend wrapper is required.'}
    $entry=$entries[0];$process=Get-Process -Id $entry.pid -ErrorAction Stop
    if($process.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$entry.startedAtUtc).UtcTicks -or
       [IO.Path]::GetFullPath($process.Path) -ine [IO.Path]::GetFullPath($entry.path)){throw 'Owned backend wrapper identity changed.'}
    $listeners=@(Get-NetTCPConnection -State Listen -LocalPort $current.backendPort -ErrorAction Stop)
    if(-not $listeners.Count){throw 'Owned backend listener absent.'}
    foreach($listener in $listeners){
        $child=Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
        if($listener.LocalAddress -notin @('127.0.0.1','::1') -or -not $child -or $child.ParentProcessId -ne $entry.pid -or
           [IO.Path]::GetFullPath($child.ExecutablePath) -ine [IO.Path]::GetFullPath((Get-Command dotnet).Source)){throw 'Backend port does not belong to the exact owned dotnet child.'}
    }
    return $current
}
try{
    [IO.File]::WriteAllText($overlayPath,($overlay|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    $null=Read-OwnedIngestionOverlay -Path $overlayPath -RepositoryRoot $repo
    if($embeddingProfile){
        $info=Invoke-RestMethod -Uri ($embeddingProfile.Rag.EmbeddingsBaseUrl+'/info') -TimeoutSec 30
        Save-Private 'embedding-info.private.json' $info
        if($info.adapter -cne $embeddingProfile.adapter -or $info.model_id -cne $embeddingProfile.Rag.EmbeddingsModel -or
           $info.model_revision -cne $embeddingProfile.Rag.EmbeddingsModelRevision -or $info.model_file_sha256 -cne $embeddingProfile.modelFileSha256 -or
           $info.runtime_revision -cne $embeddingProfile.Rag.EmbeddingsRuntimeRevision){throw 'Local embedding runtime attestation differs from its frozen profile.'}
        $proof['embeddingRuntimeAttested']=$true
    }
    # The collection must not pre-exist, even on the already-running local Qdrant.
    $collectionCheck=Invoke-WebRequest -Uri ($dependency.qdrantUrl+'/collections/'+$collection) -SkipHttpErrorCheck -TimeoutSec 10
    if([int]$collectionCheck.StatusCode -ne 404){throw 'Expected a nonexistent fresh Qdrant collection.'}
    & (Join-Path $repo 'tools/validation-winui/start-environment.ps1') -OutputDirectory $fixture -BackendPort $BackendPort -FixturePort $FixturePort -BackendOverlayPath $overlayPath -NoBuild
    $owner=Assert-OwnedIngestionBackend
    if($owner.backendAssemblySha256 -ine $plan.backendAssemblySha256){throw 'Backend DLL changed between preparation and launch.'}
    $baseline=Invoke-OwnedPaidSql -OutputDirectory $fixture -Query @'
SELECT json_build_object('documents',(SELECT count(*) FROM documents),'jobs',(SELECT count(*) FROM ingestion_jobs),
 'revisions',(SELECT count(*) FROM document_revisions),'units',(SELECT count(*) FROM document_units),
 'chunks',(SELECT count(*) FROM retrieval_chunks),'canonicalBundles',(SELECT count(*) FROM document_revision_binary_artifacts))
'@ | ConvertFrom-Json
    Save-Private 'empty-baseline.json' $baseline
    foreach($property in $baseline.PSObject.Properties){if($property.Value -ne 0){throw 'Owned ingestion tables were not empty before API enqueue.'}}
    $proof['emptyBaselineVerified']=$true
    foreach($case in $cases){
        $target=Join-Path $owner.documentsRoot ($case.caseId+'.pdf')
        Copy-Item -LiteralPath (Join-Path $sourceRoot ($case.caseId+'.pdf')) -Destination $target -ErrorAction Stop
        if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $case.sourceSha256){throw 'Copied PDF source hash mismatch.'}
    }
    $synthetic=Get-Content -LiteralPath (Join-Path $fixture 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
    $headers=@{'X-Api-Key'=$synthetic.apiKey;'X-Admin-Key'=$synthetic.apiKey}
    $null=Assert-OwnedIngestionBackend
    $scan=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/ingestion/scan') -Headers $headers -ContentType 'application/json' -Body (@{max=$cases.Count}|ConvertTo-Json) -TimeoutSec 30
    Save-Private 'enqueue-response.json' $scan
    if($scan.enqueued -ne $cases.Count -or $scan.scanned -ne $cases.Count){throw 'API scan did not enqueue exactly the selected files.'}
    $proof['apiEnqueuedJobCount']=[int]$scan.enqueued
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($MaximumWaitSeconds);$poll=0
    do{
        $null=Assert-OwnedIngestionBackend
        $jobs=Invoke-RestMethod -Uri ($owner.backendUrl+'/ingestion/jobs?limit=200') -Headers $headers -TimeoutSec 20
        Save-Private ('jobs-'+$poll.ToString('D4')+'.json') $jobs
        $proof.ingestionWorkerExecuted=($proof.ingestionWorkerExecuted -or (Test-OwnedIngestionWorkerObserved @($jobs.items)))
        $poll++
        if(@($jobs.items|Where-Object{$_.status -in @('failed','paused','canceled')}).Count){throw 'A selected ingestion job ended unsuccessfully.'}
        if(@($jobs.items).Count -eq $cases.Count -and @($jobs.items|Where-Object status -ne 'done').Count -eq 0){break}
        if([DateTimeOffset]::UtcNow -ge $deadline){throw 'Real worker exceeded the bounded campaign timeout.'}
        Start-Sleep -Milliseconds 750
    }while($true)
    $proof['jobPollCount']=$poll
    $observationText=Invoke-OwnedPaidSql -OutputDirectory $fixture -Query @'
SELECT json_build_object('schemaVersion','saaia-owned-worker-observation.v1',
 'activeJobs',(SELECT count(*) FROM ingestion_jobs WHERE status IN ('queued','running','paused')),
 'jobs',COALESCE((SELECT json_agg(json_build_object('jobId',job_id,'docPath',doc_path,'status',status,'attempts',attempts,'payload',payload) ORDER BY doc_path) FROM ingestion_jobs),'[]'::json),
 'documents',COALESCE((SELECT json_agg(json_build_object('docPath',d.doc_path,'docId',d.doc_id,'revisionId',r.revision_id,
  'status',d.status,'indexedVersion',d.indexed_version,'pageCount',d.page_count,'sourceSha256',encode(r.source_hash,'hex'),
  'activeVectorPlanFingerprint',d.ingestion_vector_plan_fingerprint,
  'sourceAnchorCount',(SELECT count(*) FROM document_source_anchors a WHERE a.revision_id=r.revision_id),
  'chunks',COALESCE((SELECT json_agg(json_build_object('retrievalChunkId',c.retrieval_chunk_id,'chunkIndex',c.chunk_index,'pageStart',c.page_start,'pageEnd',c.page_end,
    'text',c.text_content,'checksumSha256',encode(c.checksum,'hex'),'metadata',c.metadata) ORDER BY c.chunk_index) FROM retrieval_chunks c WHERE c.revision_id=r.revision_id),'[]'::json),
  'binaryArtifacts',COALESCE((SELECT json_agg(json_build_object('artifactType',b.artifact_type,'schemaVersion',b.schema_version,'compression',b.compression,
    'byteSize',b.byte_size,'storedSize',b.stored_size,'contentHash',encode(b.content_hash,'hex'),'payloadBase64',encode(b.payload,'base64')))
    FROM document_revision_binary_artifacts b WHERE b.revision_id=r.revision_id AND b.artifact_type='canonical_ingestion_bundle'),'[]'::json)) ORDER BY d.doc_path)
  FROM documents d LEFT JOIN document_revisions r ON r.tenant_id=d.tenant_id AND r.doc_id=d.doc_id AND r.indexed_version=d.indexed_version),'[]'::json))
'@
    $observationPath=Join-Path $privateRoot 'sql-publication.private.json'
    [IO.File]::WriteAllText($observationPath,$observationText,[Text.UTF8Encoding]::new($false))
    $observation=$observationText|ConvertFrom-Json
    $pointMapPath=Join-Path $privateRoot 'point-identities.private.json'
    & $PythonExecutable -B (Join-Path $PSScriptRoot 'vector_point_identity.py') --observation $observationPath --output $pointMapPath *> (Join-Path $privateRoot 'point-identities.log')
    if($LASTEXITCODE -ne 0){throw 'Canonical chunk to active-plan Qdrant identity preparation failed.'}
    $pointMap=Get-Content -LiteralPath $pointMapPath -Raw|ConvertFrom-Json -AsHashtable
    $api=@{};$vectors=@{};$searches=@{}
    foreach($row in $observation.documents){
        $case=@($cases|Where-Object{($_.caseId+'.pdf') -ceq $row.docPath})[0]
        $reads=@()
        foreach($page in @($case.expectations.criticalFacts.page|Sort-Object -Unique)){
            $null=Assert-OwnedIngestionBackend
            $context=Invoke-RestMethod -Uri ($owner.backendUrl+'/documents/context?docId='+$row.docId+'&pageStart='+$page+'&pageEnd='+$page+'&before=0&after=0&limit=50') -Headers $headers -TimeoutSec 20
            $reads+=@{physicalPage=$page;response=$context}
        }
        $api[$row.docPath]=$reads
        $points=@();$chunks=@($row.chunks)
        for($offset=0;$offset -lt $chunks.Count;$offset+=128){
            $end=[Math]::Min($offset+127,$chunks.Count-1)
            $ids=@($pointMap[$row.docPath][$offset..$end]|ForEach-Object{$_.pointId})
            $retrieved=Invoke-RestMethod -Method Post -Uri ($dependency.qdrantUrl+'/collections/'+$collection+'/points') -ContentType 'application/json' -Body (@{ids=$ids;with_payload=@('chunk_id','ingestion_plan_fingerprint');with_vector=$false}|ConvertTo-Json -Depth 6) -TimeoutSec 30
            $points+=@($retrieved.result)
        }
        $vectors[$row.docPath]=$points
        if($VerifyCanonicalSearch){
            # Queries come from independent source annotations, never from the worker output.
            $fact=@($case.expectations.criticalFacts|Where-Object{$_.retrievalRequired -ne $false})[0]
            if(-not $fact -or [string]::IsNullOrWhiteSpace([string]$fact.text) -or $fact.text.Length -gt 2000){throw 'No bounded source-annotated search fact.'}
            $positiveRequest=@{query=$fact.text;docId=$row.docId;sourceBackedCanonical=$true;pageStart=$fact.page;pageEnd=$fact.page;
                topK=20;maxPerDoc=20;maxPerPage=20}
            $null=Assert-OwnedIngestionBackend
            $positive=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/rag/search') -Headers $headers -ContentType 'application/json' -Body ($positiveRequest|ConvertTo-Json -Depth 6) -TimeoutSec 30
            $negative=@()
            $absentDocument=[Guid]::NewGuid().ToString('D')
            if($absentDocument -cin @($observation.documents.docId)){throw 'Negative document scope unexpectedly exists.'}
            foreach($kind in @('absent_query','absent_document')){
                $request=@{}+$positiveRequest
                if($kind -ceq 'absent_query'){$request.query='saaianomatch'+[Guid]::NewGuid().ToString('N')}
                else{$request.docId=$absentDocument}
                $null=Assert-OwnedIngestionBackend
                $response=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/rag/search') -Headers $headers -ContentType 'application/json' -Body ($request|ConvertTo-Json -Depth 6) -TimeoutSec 30
                $negative+=@{kind=$kind;request=$request;response=$response}
            }
            $factText=[regex]::Replace(([string]$fact.text).Normalize([Text.NormalizationForm]::FormC),'\s+',' ').Trim()
            $pattern='(?<!\w)'+[regex]::Escape($factText)+'(?!\w)'
            $chosen=@($positive.items|Where-Object{
                $_.chunkId -cin @($row.chunks.retrievalChunkId) -and [regex]::IsMatch(
                    [regex]::Replace(([string]$_.text).Normalize([Text.NormalizationForm]::FormC),'\s+',' ').Trim(),$pattern)
            }|Select-Object -First 1)
            $read=@{}
            if($chosen.Count -eq 1){
                $readRequest=@{docId=$row.docId;chunkId=$chosen[0].chunkId}
                $null=Assert-OwnedIngestionBackend
                $readResponse=Invoke-RestMethod -Uri ($owner.backendUrl+'/documents/context?docId='+$row.docId+'&chunkId='+$chosen[0].chunkId+'&before=0&after=0&limit=5') -Headers $headers -TimeoutSec 20
                $read=@{request=$readRequest;response=$readResponse}
            }
            $searches[$row.docPath]=@{positive=@{request=$positiveRequest;response=$positive};negative=$negative;read=$read}
        }
    }
    Save-Private 'api-context.private.json' $api
    Save-Private 'qdrant-points.private.json' $vectors
    if($VerifyCanonicalSearch){Save-Private 'api-search.private.json' $searches}
    $verificationPath=Join-Path $root 'publication-verification.json'
    $verifyArgs=@('-B',(Join-Path $PSScriptRoot 'verify_worker_publication.py'),'--manifest',$CorpusManifestPath,
        '--observation',$observationPath,'--api',(Join-Path $privateRoot 'api-context.private.json'),
        '--vectors',(Join-Path $privateRoot 'qdrant-points.private.json'),'--output',$verificationPath,'--embedding-adapter',$embeddingAdapter)
    if($embeddingProfile){$verifyArgs+='--embeddings-real'}
    if($VerifyCanonicalSearch){$verifyArgs+=@('--require-search','--searches',(Join-Path $privateRoot 'api-search.private.json'))}
    foreach($id in $CaseId){$verifyArgs+=@('--case-id',$id)}
    & $PythonExecutable @verifyArgs *> (Join-Path $privateRoot 'verification.log')
    if($LASTEXITCODE -ne 0){throw 'Captured worker publication did not satisfy independent acceptance checks.'}
    $verification=Get-Content -LiteralPath $verificationPath -Raw|ConvertFrom-Json
    $proof.sqlPublicationValidated=[bool]$verification.sqlPublicationValidated
    $proof.vectorStorePublicationValidated=[bool]$verification.vectorStorePublicationValidated
    $proof['canonicalSearchValidated']=[bool]$verification.canonicalSearchValidated
    if($ValidateLifecycle){
        $lifecycleCase=@($cases|Where-Object caseId -CEQ 'fr-scan-critical-v2')[0]
        $lifecycleDocument=@($observation.documents|Where-Object docPath -CEQ 'fr-scan-critical-v2.pdf')[0]
        $proof['lifecycle']=Invoke-OwnedIngestionLifecycle -RepositoryRoot $repo -FixtureDirectory $fixture `
            -PrivateDirectory (Join-Path $privateRoot 'lifecycle') -Owner $owner -Headers $headers `
            -SourceDirectory $sourceRoot -Case $lifecycleCase -Document $lifecycleDocument `
            -QdrantUrl $dependency.qdrantUrl -QdrantCollection $collection `
            -AssertOwnership {Assert-OwnedIngestionBackend|Out-Null}
        if(-not $proof.lifecycle.passed){throw 'Owned synthetic lifecycle acceptance failed.'}
    }
    $proof.passed=[bool]$verification.passed;$proof.state='verified'
}catch{
    $proof.state='failed';$proof['failureType']=$_.Exception.GetType().Name
    # Details remain private; no API response, corpus text or credential enters the public result.
    [IO.File]::WriteAllText((Join-Path $privateRoot 'failure.private.txt'),($_|Out-String),[Text.UTF8Encoding]::new($false))
}finally{
    if(Test-Path -LiteralPath (Join-Path $fixture 'environment.json')){
        try{
            if($headers -and $owner){
                $null=Assert-OwnedIngestionBackend
                $remaining=Invoke-RestMethod -Uri ($owner.backendUrl+'/ingestion/jobs?limit=200') -Headers $headers -TimeoutSec 10
                foreach($job in @($remaining.items|Where-Object{$_.status -in @('queued','running','paused')})){
                    $null=Assert-OwnedIngestionBackend
                    $null=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/admin/jobs/cancel') -Headers $headers -ContentType 'application/json' -Body (@{jobId=$job.jobId}|ConvertTo-Json) -TimeoutSec 10
                }
            }
            $cleanupDeadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
            do{
                $active=Invoke-OwnedPaidSql -OutputDirectory $fixture -Query "SELECT count(*) FROM ingestion_jobs WHERE status IN ('queued','running','paused')"
                if([int]$active -eq 0 -or [DateTimeOffset]::UtcNow -ge $cleanupDeadline){break}
                Start-Sleep -Milliseconds 500
            }while($true)
            $proof.cleanup['activeJobCountBeforeStop']=[int]$active
            $proof.cleanup.zeroActiveJobs=([int]$active -eq 0)
        }catch{$proof['cleanupObservationFailureType']=$_.Exception.GetType().Name}
        try{
            & (Join-Path $repo 'tools/validation-winui/stop-environment.ps1') -OutputDirectory $fixture -Confirm:$false
            $stopped=Get-Content -LiteralPath (Join-Path $fixture 'environment.json') -Raw|ConvertFrom-Json
            $proof.cleanup.ownedEnvironmentStopped=($stopped.state -ceq 'stopped')
        }catch{$proof['cleanupStopFailureType']=$_.Exception.GetType().Name}
    }
    $proof.passed=($proof.passed -and $proof.cleanup.zeroActiveJobs -and $proof.cleanup.ownedEnvironmentStopped)
    $proof['finishedAtUtc']=[DateTimeOffset]::UtcNow.ToString('o')
    [IO.File]::WriteAllText((Join-Path $root 'execution-result.json'),($proof|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
}
if(-not $proof.passed){throw 'Owned ingestion campaign did not pass; inspect its new public result and private observations.'}
Write-Output ('Real owned ingestion worker and Qdrant point publication verified. Embedding adapter='+$embeddingAdapter+'; semantic vector validation remains false.')
