#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SourceManifestPath,
    [Parameter(Mandatory)][string]$DoclingProfilePath,
    [Parameter(Mandatory)][string]$EmbeddingProfilePath,
    [Parameter(Mandatory)][string]$DependenciesManifestPath,
    [Parameter(Mandatory)][string]$PythonExecutable,
    [Parameter(Mandatory)][string]$ExpectedBackendSha256,
    [int]$BackendPort=22511,[int]$FixturePort=22512,
    [ValidateRange(60,14400)][int]$MaximumWaitSeconds=7200,
    [switch]$Execute
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repo 'tools/validation-ingestion/OwnedIngestionConfig.ps1')
. (Join-Path $repo 'tools/validation-ingestion/OwnedDependencies.ps1')
. (Join-Path $PSScriptRoot 'PlanningCampaign.Common.ps1')
. (Join-Path $PSScriptRoot 'RealIngestion.Common.ps1')
. (Join-Path $repo 'tools/validation-paid/PaidRuntime.ps1')
$root=[IO.Path]::GetFullPath($OutputDirectory)
$prefix=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(-not $root.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $root)){
    throw 'A fresh owned environment directory under artifacts is required.'
}
if($BackendPort -eq $FixturePort -or @(@($BackendPort,$FixturePort)|Where-Object {$_ -lt 1024 -or $_ -gt 65535 -or $_ -eq 5432}).Count){throw 'Invalid distinct local ports.'}
if(-not [IO.Path]::IsPathFullyQualified($PythonExecutable) -or -not [IO.File]::Exists($PythonExecutable)){throw 'Explicit trusted Python is required.'}
$sources=Get-RealPlanningSources $SourceManifestPath
$dependency=Get-Content -LiteralPath $DependenciesManifestPath -Raw|ConvertFrom-Json
$dependencyRoot=Split-Path -Parent ([IO.Path]::GetFullPath($DependenciesManifestPath))
$null=Get-ValidatedDependencyOwner -Owner $dependency -OutputDirectory $dependencyRoot -RepositoryRoot $repo
if($dependency.state -cnotin @('started_readiness_pending','ready') -or -not $dependency.embeddingUrl){throw 'Already-started owned real dependencies are required; startup is a separate reviewed action.'}
$profile=Get-Content -LiteralPath $DoclingProfilePath -Raw|ConvertFrom-Json -AsHashtable
$docling=if($profile.Contains('DocumentIntelligence')){$profile.DocumentIntelligence}else{$profile}
$embedding=Read-OwnedEmbeddingProfile $EmbeddingProfilePath
$collection='ingestion_'+[Guid]::NewGuid().ToString('N')
$overlay=New-RealPlanningOverlay -Docling $docling -Embedding $embedding -Dependency $dependency -Collection $collection
$dll=Join-Path $repo 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'
$backendSha=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
if($ExpectedBackendSha256 -cnotmatch '^[a-fA-F0-9]{64}$' -or $backendSha -ine $ExpectedBackendSha256){throw 'Reviewed backend bytes changed before real ingestion.'}
$plan=[ordered]@{schemaVersion='saaia-planning-real-ingestion-plan.v1';execute=[bool]$Execute;
    sourceManifestPath=[IO.Path]::GetFullPath($SourceManifestPath);sourceManifestSha256=(Get-FileHash -LiteralPath $SourceManifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
    profilePath=[IO.Path]::GetFullPath($DoclingProfilePath);profileSha256=(Get-FileHash -LiteralPath $DoclingProfilePath -Algorithm SHA256).Hash.ToLowerInvariant();
    embeddingProfileSha256=(Get-FileHash -LiteralPath $EmbeddingProfilePath -Algorithm SHA256).Hash.ToLowerInvariant();
    dependencyManifestSha256=(Get-FileHash -LiteralPath $DependenciesManifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
    backendAssemblySha256=$backendSha;sourceCount=4;physicalPageCount=($sources.cases.expectations.pageCount|Measure-Object -Sum).Sum;
    backendPort=$BackendPort;fixturePort=$FixturePort;qdrantCollection=$collection;maximumWaitSeconds=$MaximumWaitSeconds;
    realWorker=$true;realDocling=$true;realEmbeddings=$true;canonicalRowsSeeded=$false;paidProviderEnabled=$false;
    keepOwnerReadyOnSuccess=$true;productStatus='TESTE_NON_APPROUVE';qualificationMode='windows_development_diagnostic'}
if(-not $Execute){$plan|ConvertTo-Json -Depth 10;return}
# No process, credential, SQL or network access occurs above the Execute gate.
$parent=Split-Path -Parent $root
[IO.Directory]::CreateDirectory($parent)|Out-Null
$control=Join-Path $parent ('ingestion-control-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($control)|Out-Null
Write-RealPlanningJson -Path (Join-Path $control 'plan.json') -Value $plan
$overlayPath=Join-Path $control 'ingestion-overlay.json'
Write-RealPlanningJson -Path $overlayPath -Value $overlay
$receipt=[ordered]@{schemaVersion='saaia-planning-real-ingestion-result.v1';plan=$plan;startedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
    passed=$false;state='starting';ownerDirectory=$root;canonicalRowsSeeded=$false;externalProviderCalls=0;cleanup=@{ownerStoppedOnFailure=$false}}
$owner=$null;$headers=$null
try{
    # Reuse the independent full PID/start/argv/port prevalidation without stopping dependencies.
    & (Join-Path $repo 'tools/validation-ingestion/stop-local-dependencies.ps1') -OutputDirectory $dependencyRoot *> (Join-Path $control 'dependency-ownership-readonly.log')
    $health=Invoke-RestMethod -Uri ($dependency.doclingUrl+'/health') -TimeoutSec 10
    Write-RealPlanningJson -Path (Join-Path $control 'docling-health.private.json') -Value $health
    if($health.status -cne 'ok' -or $health.upstream -ne $true -or $health.regionalTableRepair -ne $docling.RegionalTableRepairEnabled -or
       $health.regionalTextRepair.enabled -ne $docling.RegionalTextRepairEnabled){throw 'Actual Docling readiness or regional settings differ from the declared CPU profile.'}
    $info=Invoke-RestMethod -Uri ($embedding.Rag.EmbeddingsBaseUrl+'/info') -TimeoutSec 20
    Write-RealPlanningJson -Path (Join-Path $control 'embedding-info.private.json') -Value $info
    if($info.adapter -cne $embedding.adapter -or $info.model_id -cne $embedding.Rag.EmbeddingsModel -or
       $info.model_revision -cne $embedding.Rag.EmbeddingsModelRevision -or $info.model_file_sha256 -cne $embedding.modelFileSha256 -or
       $info.runtime_revision -cne $embedding.Rag.EmbeddingsRuntimeRevision -or $info.offline -ne $true -or [int]$info.dimensions -ne 768){throw 'Real local embedding runtime identity differs.'}
    $collectionCheck=Invoke-WebRequest -Uri ($dependency.qdrantUrl+'/collections/'+$collection) -SkipHttpErrorCheck -TimeoutSec 10
    if([int]$collectionCheck.StatusCode -ne 404){throw 'Real ingestion requires a nonexistent vector collection.'}
    & (Join-Path $repo 'tools/validation-winui/start-environment.ps1') -OutputDirectory $root -BackendPort $BackendPort -FixturePort $FixturePort `
        -BackendOverlayPath $overlayPath -AllowPlanningIngestionProfile -NoBuild *> (Join-Path $control 'start-environment.log')
    $owner=Assert-RealPlanningBackend $root
    if($owner.backendAssemblySha256 -ine $backendSha){throw 'Backend changed between preparation and start.'}
    $baseline=Invoke-OwnedPaidSql -OutputDirectory $root -Query @'
SELECT jsonb_build_object('documents',(SELECT count(*) FROM documents),'ingestionJobs',(SELECT count(*) FROM ingestion_jobs),
 'advancedJobs',(SELECT count(*) FROM advanced_analysis_jobs),'revisions',(SELECT count(*) FROM document_revisions),
 'chunks',(SELECT count(*) FROM retrieval_chunks),'bundles',(SELECT count(*) FROM document_revision_binary_artifacts));
'@|ConvertFrom-Json
    foreach($property in $baseline.PSObject.Properties){if([long]$property.Value -ne 0){throw 'Owned publication store was not empty.'}}
    Write-RealPlanningJson -Path (Join-Path $control 'empty-baseline.json') -Value $baseline
    foreach($case in $sources.cases){
        $target=Join-Path $owner.documentsRoot ($case.caseId+'.pdf')
        Copy-Item -LiteralPath $case.physicalPdfPath -Destination $target
        if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $case.sourceSha256){throw 'Original PDF copy is not exact.'}
    }
    $credentials=Get-Content -LiteralPath (Join-Path $root 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
    $headers=@{'X-Api-Key'=$credentials.apiKey;'X-Admin-Key'=$credentials.apiKey}
    $null=Assert-RealPlanningBackend $root
    $scan=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/ingestion/scan') -Headers $headers -ContentType 'application/json' -Body '{"max":4}' -TimeoutSec 30
    Write-RealPlanningJson -Path (Join-Path $control 'scan-response.json') -Value $scan
    if([int]$scan.scanned -ne 4 -or [int]$scan.enqueued -ne 4){throw 'Scan did not enqueue exactly the four originals.'}
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($MaximumWaitSeconds);$poll=0
    do{
        $null=Assert-RealPlanningBackend $root
        $jobs=Invoke-RestMethod -Uri ($owner.backendUrl+'/ingestion/jobs?limit=20') -Headers $headers -TimeoutSec 20
        Write-RealPlanningJson -Path (Join-Path $control ('jobs-'+$poll.ToString('D5')+'.json')) -Value $jobs
        $poll++
        if(@($jobs.items|Where-Object {$_.status -in @('failed','paused','canceled')}).Count){throw 'A real ingestion job failed; preserve its cause before retry.'}
        if(@($jobs.items).Count -eq 4 -and @($jobs.items|Where-Object {$_.status -ne 'done' -or [int]$_.attempts -lt 1}).Count -eq 0){break}
        if([DateTimeOffset]::UtcNow -ge $deadline){throw 'Real complete-document ingestion exceeded the bounded timeout.'}
        Start-Sleep -Seconds 2
    }while($true)
    $receipt['workerObserved']=$true;$receipt['pollCount']=$poll
    $logSnapshot=Join-Path $root 'runtime/ingestion-backend.stdout.snapshot.log'
    Copy-Item -LiteralPath (Join-Path $root 'backend.stdout.log') -Destination $logSnapshot
    $receipt['backendLogSnapshot']=@{path=$logSnapshot;sha256=(Get-FileHash -LiteralPath $logSnapshot -Algorithm SHA256).Hash.ToLowerInvariant()}
    $executionEvidence=@{}
    foreach($entry in @{backendLog=$logSnapshot;emptyBaseline=(Join-Path $control 'empty-baseline.json');
        scanResponse=(Join-Path $control 'scan-response.json');terminalJobs=(Join-Path $control ('jobs-'+($poll-1).ToString('D5')+'.json'));
        doclingHealth=(Join-Path $control 'docling-health.private.json');embeddingInfo=(Join-Path $control 'embedding-info.private.json');
        dependencyOwnership=(Join-Path $control 'dependency-ownership-readonly.log')}.GetEnumerator()){
        $executionEvidence[$entry.Key]=@{path=$entry.Value;sha256=(Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    $receipt['publication']=Export-RealPlanningPublication -OutputDirectory $root -Sources $sources -SourceManifestPath $SourceManifestPath `
        -SourceManifestSha256 $plan.sourceManifestSha256 -ProfileSha256 $plan.profileSha256 -DependencyManifestSha256 $plan.dependencyManifestSha256 -ExecutionEvidence $executionEvidence
    $pointMapPath=Join-Path $root 'runtime/worker-point-identities.private.json'
    & $PythonExecutable -X utf8 -B (Join-Path $PSScriptRoot 'verify_worker_corpus.py') --observation $receipt.publication.path --source-manifest $SourceManifestPath `
        --point-output $pointMapPath *> (Join-Path $control 'point-identities.log')
    if($LASTEXITCODE -ne 0){throw 'Actual SQL publication identities or canonical integrity checks failed.'}
    $pointMap=Get-Content -LiteralPath $pointMapPath -Raw|ConvertFrom-Json -AsHashtable
    $vectors=@{}
    foreach($docPath in $pointMap.Keys){
        $points=@($pointMap[$docPath]);$found=@()
        for($offset=0;$offset -lt $points.Count;$offset+=64){
            $last=[Math]::Min($points.Count-1,$offset+63)
            $ids=@($points[$offset..$last]|ForEach-Object {$_.pointId})
            $result=Invoke-RestMethod -Method Post -Uri ($dependency.qdrantUrl+'/collections/'+$collection+'/points') -ContentType 'application/json' `
                -Body (@{ids=$ids;with_payload=@('chunk_id','ingestion_plan_fingerprint');with_vector=$true}|ConvertTo-Json -Depth 6) -TimeoutSec 30
            $found+=@($result.result)
        }
        $vectors[$docPath]=$found
    }
    $vectorPath=Join-Path $root 'runtime/worker-vector-points.private.json'
    Write-RealPlanningJson -Path $vectorPath -Value $vectors
    $manifest=Join-Path $root 'complete-corpus.json'
    & $PythonExecutable -X utf8 -B (Join-Path $PSScriptRoot 'verify_worker_corpus.py') --observation $receipt.publication.path --source-manifest $SourceManifestPath --manifest-output $manifest `
        --vectors $vectorPath --output (Join-Path $root 'worker-publication-verification.json') *> (Join-Path $control 'worker-verification.log')
    if($LASTEXITCODE -ne 0){throw 'Actual worker publication did not pass corpus integrity checks.'}
    $receipt['corpusManifest']=@{path=$manifest;sha256=(Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant()}
    $receipt.passed=$true;$receipt.state='ingested_owner_kept_ready'
}catch{
    $receipt.state='failed';$receipt['failureType']=$_.Exception.GetType().Name
    [IO.File]::WriteAllText((Join-Path $control 'failure.private.txt'),($_|Out-String),[Text.UTF8Encoding]::new($false))
}finally{
    if(-not $receipt.passed -and (Test-Path -LiteralPath (Join-Path $root 'environment.json'))){
        try{
            if($headers -and $owner){
                $null=Assert-RealPlanningBackend $root
                $jobs=Invoke-RestMethod -Uri ($owner.backendUrl+'/ingestion/jobs?limit=20') -Headers $headers -TimeoutSec 10
                foreach($job in @($jobs.items|Where-Object status -In @('queued','running','paused'))){
                    $null=Assert-RealPlanningBackend $root
                    $null=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/admin/jobs/cancel') -Headers $headers -ContentType 'application/json' -Body (@{jobId=$job.jobId}|ConvertTo-Json) -TimeoutSec 10
                }
            }
            & (Join-Path $repo 'tools/validation-winui/stop-environment.ps1') -OutputDirectory $root -Confirm:$false *> (Join-Path $control 'failure-stop.log')
            $receipt.cleanup.ownerStoppedOnFailure=((Get-Content -LiteralPath (Join-Path $root 'environment.json') -Raw|ConvertFrom-Json).state -ceq 'stopped')
        }catch{$receipt.cleanup['failureType']=$_.Exception.GetType().Name}
    }
    $receipt['finishedAtUtc']=[DateTimeOffset]::UtcNow.ToString('o')
    Write-RealPlanningJson -Path (Join-Path $control 'result.json') -Value $receipt
    if(Test-Path -LiteralPath $root){Write-RealPlanningJson -Path (Join-Path $root 'worker-ingestion-result.json') -Value $receipt}
}
if(-not $receipt.passed){throw ('Real ingestion did not pass; inspect '+$control)}
Write-Output ('Four originals ingested by the real worker; owned services remain ready. Corpus manifest: '+$receipt.corpusManifest.path)
