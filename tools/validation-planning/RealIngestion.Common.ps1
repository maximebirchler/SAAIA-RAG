#requires -Version 7.4
# Preparation functions are pure; runtime helpers are called only after -Execute.
function Get-RealPlanningSources {
    param([string]$ManifestPath)
    $manifest=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json -AsHashtable
    if($manifest.schemaVersion -cne 'ingestion_real_acceptance_v1' -or @($manifest.cases).Count -ne 4){throw 'Four independently annotated original PDFs are required.'}
    $paths=@{};$hashes=@{}
    foreach($case in $manifest.cases){
        if($case.caseId -cnotmatch '^[a-z0-9][a-z0-9-]{0,95}$' -or $case.sourceSha256 -cnotmatch '^[a-f0-9]{64}$' -or
           [int]$case.expectations.pageCount -le 0 -or -not @($case.expectations.criticalFacts).Count -or
           $manifest.newWorkerObservationSeenDuringRegistration -ne $false -or $manifest.originalFilesModified -ne $false -or
           -not [IO.Path]::IsPathFullyQualified($case.physicalPdfPath)){
            throw 'Original PDF identity and independent physical annotations are required.'
        }
        if($paths.ContainsKey($case.caseId) -or $hashes.ContainsKey($case.sourceSha256)){throw 'Duplicate source identity.'}
        if((Get-FileHash -LiteralPath $case.physicalPdfPath -Algorithm SHA256).Hash -ine $case.sourceSha256){throw 'Original PDF hash differs from annotations.'}
        foreach($fact in $case.expectations.criticalFacts){
            if([int]$fact.physicalPage -lt 1 -or [int]$fact.physicalPage -gt [int]$case.expectations.pageCount -or
               [string]::IsNullOrWhiteSpace($fact.inspection) -or $fact.render.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
               (Get-FileHash -LiteralPath $fact.render.path -Algorithm SHA256).Hash -ine $fact.render.sha256){throw 'Invalid independently rendered physical annotation.'}
        }
        $paths[$case.caseId]=$true;$hashes[$case.sourceSha256]=$true
    }
    return $manifest
}

function New-RealPlanningOverlay {
    param([hashtable]$Docling,[hashtable]$Embedding,[object]$Dependency,[string]$Collection)
    $overlay=@{
        Ingestion=@{WorkerEnabled=$true;WorkerConcurrency=1;CanonicalArtifactsEnabled=$true;WatcherEnabled=$false;ScannerEnabled=$false;
            MinFileAgeSeconds=0;ReindexIfQdrantEmpty=$false;OcrEnabled=$false;OcrImagePageEnabled=$false;HeavyComputeMaxConcurrency=1;
            TeiTimeoutSeconds=180;QdrantTimeoutSeconds=180;ChunkMaxWords=200;ChunkMinWords=25;ChunkOverlapWords=0}
        Rag=@{QdrantBaseUrl=$Dependency.qdrantUrl;QdrantCollection=$Collection;QdrantRuntimeRevision='qdrant-1.20.0-local';
            EmbeddingsBaseUrl=$Embedding.Rag.EmbeddingsBaseUrl;EmbeddingsModel=$Embedding.Rag.EmbeddingsModel;
            EmbeddingsModelRevision=$Embedding.Rag.EmbeddingsModelRevision;EmbeddingsRuntimeRevision=$Embedding.Rag.EmbeddingsRuntimeRevision;
            EnableRerank=$false;RequireQdrantAuthInProd=$false}
        DocumentIntelligence=$Docling
    }
    $null=Assert-OwnedIngestionOverlayValues -Overlay $overlay -AllowPlanningIngestionProfile
    if($Docling.BaseUrl -cne $Dependency.doclingUrl -or $Embedding.Rag.EmbeddingsBaseUrl -cne $Dependency.embeddingUrl){throw 'Profiles must target the exact declared local dependencies.'}
    foreach($name in @('RegionalTableAlternativeReadingsEnabled','NativeTypographyObservationsEnabled','DoclingInputGeometryCaptureEnabled')){
        if($Docling[$name] -ne $true){throw 'Current native observation and geometry capabilities must be explicitly enabled.'}
    }
    if($Docling.Device -cne 'cpu' -or [int]$Docling.NumThreads -notin 1..8 -or [int]$Docling.ServiceWorkerConcurrency -ne 1){throw 'A bounded CPU profile is required.'}
    return $overlay
}

function Assert-RealPlanningBackend {
    param([string]$OutputDirectory)
    $owner=Get-OwnedPaidEnvironment $OutputDirectory
    $entry=@($owner.processes|Where-Object name -CEQ 'backend-runner')
    if($entry.Count -ne 1){throw 'Exactly one owned backend wrapper is required.'}
    $wrapper=Get-Process -Id $entry[0].pid -ErrorAction Stop
    if($wrapper.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$entry[0].startedAtUtc).UtcTicks -or
       [IO.Path]::GetFullPath($wrapper.Path) -ine [IO.Path]::GetFullPath($entry[0].path)){throw 'Owned backend wrapper identity changed.'}
    $listeners=@(Get-NetTCPConnection -State Listen -LocalPort $owner.backendPort -ErrorAction Stop)
    if(-not $listeners.Count){throw 'Owned backend listener absent.'}
    foreach($listener in $listeners){
        $child=Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
        if($listener.LocalAddress -notin @('127.0.0.1','::1') -or -not $child -or $child.ParentProcessId -ne $entry[0].pid -or
           [IO.Path]::GetFullPath($child.ExecutablePath) -ine [IO.Path]::GetFullPath((Get-Command dotnet).Source)){
            throw 'Listener does not belong to the owned backend child.'
        }
    }
    return $owner
}

function Export-RealPlanningPublication {
    param([string]$OutputDirectory,[hashtable]$Sources,[string]$SourceManifestPath,[string]$SourceManifestSha256,
        [string]$ProfileSha256,[string]$DependencyManifestSha256,[hashtable]$ExecutionEvidence)
    $owner=Assert-RealPlanningBackend $OutputDirectory
    $query=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'worker-publication.sql'),[Text.Encoding]::UTF8)
    $capture=Invoke-OwnedPaidSql -OutputDirectory $OutputDirectory -Query $query|ConvertFrom-Json -AsHashtable -Depth 100
    foreach($doc in $capture.documents){
        $source=@($Sources.cases|Where-Object {($_.caseId+'.pdf') -ceq $doc.docPath})
        if($source.Count -ne 1){throw 'Unregistered document in worker publication.'}
        $doc['physicalPdfPath']=$source[0].physicalPdfPath
    }
    $capture['ownerRunId']=$owner.runId;$capture['capturedAtUtc']=[DateTimeOffset]::UtcNow.ToString('o')
    $capture['sourceManifestSha256']=$SourceManifestSha256;$capture['profileSha256']=$ProfileSha256
    $capture['sourceManifestPath']=[IO.Path]::GetFullPath($SourceManifestPath)
    $capture['workerExecutionEvidence']=$ExecutionEvidence
    $capture['dependencyManifestSha256']=$DependencyManifestSha256;$capture['backendAssemblySha256']=$owner.backendAssemblySha256.ToLowerInvariant()
    $capture['configurationSha256']=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'runtime/deployment.config.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $capture['canonicalRowsSeeded']=$false
    $path=Join-Path $OutputDirectory 'runtime/worker-corpus.private.json'
    Write-RealPlanningJson -Path $path -Value $capture
    return @{path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
}

function Write-RealPlanningJson {
    param([string]$Path,[object]$Value)
    $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes(($Value|ConvertTo-Json -Depth 100))
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
}
