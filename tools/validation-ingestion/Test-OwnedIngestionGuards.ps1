#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'OwnedIngestionConfig.ps1')
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root=[IO.Path]::GetFullPath($OutputDirectory)
$prefix=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(-not $root.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $root)){throw 'Use a fresh artifacts test directory.'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$results=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[scriptblock]$Body,[bool]$Reject=$false){
    $failed=$false
    try{& $Body|Out-Null}catch{$failed=$true}
    $ok=($failed -eq $Reject)
    $results.Add(@{name=$Name;passed=$ok;expectedRejection=$Reject})
    if(-not $ok){throw ('Offline ingestion guard failed: '+$Name)}
}
function Valid-Overlay {
    return @{
        Ingestion=@{WorkerEnabled=$true;CanonicalArtifactsEnabled=$true;WorkerConcurrency=1;WatcherEnabled=$false;
            ScannerEnabled=$false;ReindexIfQdrantEmpty=$false;OcrEnabled=$false;OcrImagePageEnabled=$false;MinFileAgeSeconds=0}
        Rag=@{QdrantBaseUrl='http://127.0.0.1:16341';QdrantCollection='test_collection';QdrantRuntimeRevision='synthetic-test';
            EmbeddingsBaseUrl='http://127.0.0.1:18091';EmbeddingsModel='fixture-3d';EnableRerank=$false;RequireQdrantAuthInProd=$false}
        DocumentIntelligence=@{Enabled=$true;Provider='docling';BaseUrl='http://127.0.0.1:15101';DoOcr=$true;
            EngineVersion='synthetic-test';ParseEngineVersion='synthetic-test';ModelPackageVersion='synthetic-test';
            OcrEngineVersion='synthetic-test';DeploymentRevision='synthetic-test'}
    }
}
Check 'explicit local canonical worker accepted' {Assert-OwnedIngestionOverlayValues (Valid-Overlay)}
Check 'provenance binds the loaded DLL bytes' {
    if((Get-OwnedIngestionCodeRevision ('A'*64)) -cne ('sha256:'+('a'*64))){throw 'Code provenance did not preserve the immutable DLL identity.'}
}
Check 'Git HEAD alone is not loaded-DLL evidence' {Get-OwnedIngestionCodeRevision ('a'*40)} $true
Check 'queued job alone is not worker execution' {
    if(Test-OwnedIngestionWorkerObserved @(@{status='queued';attempts=0})){throw 'An enqueue is not an execution.'}
}
Check 'actual running claim proves worker observed' {
    if(-not(Test-OwnedIngestionWorkerObserved @(@{status='running';attempts=1}))){throw 'The actual worker claim was not observed.'}
}
Check 'terminal done proves worker observed' {
    if(-not(Test-OwnedIngestionWorkerObserved @(@{status='done';attempts=1}))){throw 'The actual terminal worker job was not observed.'}
}
Check 'IPv6 literal loopback accepted' {Assert-IngestionLoopbackUrl 'http://[::1]:15101'}
foreach($url in @('https://127.0.0.1:15101','http://example.com:15101','http://127.0.0.1:5432',
    'http://127.0.0.1:15101/path','http://name:value@127.0.0.1:15101','http://127.0.0.1:15101/?q=x')){
    Check ('reject origin '+$results.Count) {Assert-IngestionLoopbackUrl $url} $true
}
Check 'cannot override authentication' {$x=Valid-Overlay;$x.Auth=@{Pepper='synthetic'};Assert-OwnedIngestionOverlayValues $x} $true
Check 'cannot override PostgreSQL' {$x=Valid-Overlay;$x.Ingestion.ConnectionString='synthetic';Assert-OwnedIngestionOverlayValues $x} $true
Check 'cannot inject a Qdrant key reference' {$x=Valid-Overlay;$x.Rag.QdrantApiKeyRef='FILE:synthetic';Assert-OwnedIngestionOverlayValues $x} $true
Check 'cannot configure an external LLM' {$x=Valid-Overlay;$x.AdvancedAnalysis=@{Provider='openai'};Assert-OwnedIngestionOverlayValues $x} $true
Check 'worker must execute' {$x=Valid-Overlay;$x.Ingestion.WorkerEnabled=$false;Assert-OwnedIngestionOverlayValues $x} $true
Check 'canonical artifacts required' {$x=Valid-Overlay;$x.Ingestion.CanonicalArtifactsEnabled=$false;Assert-OwnedIngestionOverlayValues $x} $true
Check 'background scanner forbidden' {$x=Valid-Overlay;$x.Ingestion.ScannerEnabled=$true;Assert-OwnedIngestionOverlayValues $x} $true
Check 'legacy OCR command forbidden' {$x=Valid-Overlay;$x.Ingestion.OcrCommand='synthetic';Assert-OwnedIngestionOverlayValues $x} $true
Check 'chunk minimum override forbidden' {$x=Valid-Overlay;$x.Ingestion.ChunkMinWords=1;Assert-OwnedIngestionOverlayValues $x} $true
Check 'typed concurrency required' {$x=Valid-Overlay;$x.Ingestion.WorkerConcurrency='1';Assert-OwnedIngestionOverlayValues $x} $true
Check 'actual OCR required' {$x=Valid-Overlay;$x.DocumentIntelligence.DoOcr=$false;Assert-OwnedIngestionOverlayValues $x} $true
Check 'observed engine identity required' {$x=Valid-Overlay;$x.DocumentIntelligence.EngineVersion='';Assert-OwnedIngestionOverlayValues $x} $true
Check 'remote Docling forbidden' {$x=Valid-Overlay;$x.DocumentIntelligence.BaseUrl='http://192.0.2.1:15101';Assert-OwnedIngestionOverlayValues $x} $true
Check 'multiline values forbidden' {$x=Valid-Overlay;$x.DocumentIntelligence.EngineVersion="one`ntwo";Assert-OwnedIngestionOverlayValues $x} $true
$path=Join-Path $root 'synthetic-overlay.json'
[IO.File]::WriteAllText($path,((Valid-Overlay)|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Check 'bounded campaign file accepted' {Read-OwnedIngestionOverlay -Path $path -RepositoryRoot $repo}
Check 'outside campaign root rejected before read' {Read-OwnedIngestionOverlay -Path (Join-Path $repo 'config/not-read.json') -RepositoryRoot $repo} $true
$embedding=@{schemaVersion='saaia-local-embedding-profile.v1';adapter='transformers_cpu';modelFileSha256=('b'*64);
    Rag=@{EmbeddingsBaseUrl='http://127.0.0.1:15103';EmbeddingsModel='synthetic/model';EmbeddingsModelRevision=('a'*40);EmbeddingsRuntimeRevision='synthetic-test'}}
$embeddingPath=Join-Path $root 'synthetic-embedding-profile.json'
[IO.File]::WriteAllText($embeddingPath,($embedding|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Check 'explicit pinned local Transformers profile accepted' {Read-OwnedEmbeddingProfile $embeddingPath}
$embedding.Rag.EmbeddingsModelRevision='main'
[IO.File]::WriteAllText($embeddingPath,($embedding|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Check 'mutable model revision rejected' {Read-OwnedEmbeddingProfile $embeddingPath} $true
$embedding.Rag.EmbeddingsModelRevision='a'*40;$embedding.Rag.EmbeddingsBaseUrl='http://192.0.2.1:15103'
[IO.File]::WriteAllText($embeddingPath,($embedding|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Check 'remote embedding endpoint rejected' {Read-OwnedEmbeddingProfile $embeddingPath} $true
$embedding.Rag.EmbeddingsBaseUrl='http://127.0.0.1:15103';$embedding.ApiKey='synthetic-forbidden-field'
[IO.File]::WriteAllText($embeddingPath,($embedding|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Check 'embedding credential fields rejected' {Read-OwnedEmbeddingProfile $embeddingPath} $true
foreach($file in @('OwnedIngestionConfig.ps1','invoke-owned-ingestion.ps1')){
    Check ('AST '+$file) {
        $tokens=$null;$errors=$null
        $null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$tokens,[ref]$errors)
        if($errors.Count){throw 'AST parse errors.'}
    }
}
$proof=[ordered]@{schemaVersion='saaia-owned-ingestion-offline-guards.v1';passed=$true;caseCount=$results.Count;results=@($results.ToArray());
    serviceCalls=0;processesStarted=0;credentialsRead=0;databaseOperations=0;modelCalls=0}
[IO.File]::WriteAllText((Join-Path $root 'guards.json'),($proof|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Write-Output ('Offline ingestion guards PASS '+$results.Count+'; no service/process/credential/database access.')
