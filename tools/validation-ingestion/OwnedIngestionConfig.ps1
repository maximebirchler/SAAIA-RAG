#requires -Version 7.4
# Pure configuration validation: no process, secret store, database or HTTP access.
function Get-OwnedIngestionCodeRevision {
    param([Parameter(Mandatory)][string]$BackendAssemblySha256)
    if($BackendAssemblySha256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'The controlled provenance identity requires the actual SHA256 of the backend DLL.'}
    return 'sha256:'+$BackendAssemblySha256.ToLowerInvariant()
}

function Test-OwnedIngestionWorkerObserved {
    param([object[]]$Jobs=@())
    # Enqueue alone proves no execution. Claim/attempt state comes from the
    # actual owned job API, never from the submitted request count.
    return @($Jobs|Where-Object{$_.status -in @('running','done') -or [int]$_.attempts -gt 0}).Count -gt 0
}

function Assert-IngestionLoopbackUrl {
    param([Parameter(Mandatory)][string]$Value)
    $uri=$null;$address=$null
    if(-not [Uri]::TryCreate($Value,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -cne 'http' -or
       -not [Net.IPAddress]::TryParse($uri.Host.Trim('[',']'),[ref]$address) -or
       -not [Net.IPAddress]::IsLoopback($address) -or $uri.Port -lt 1024 -or $uri.Port -eq 5432 -or
       $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -cne '/') {
        throw 'Ingestion validation endpoints must be literal loopback HTTP origins without credentials or paths.'
    }
}

function Read-OwnedIngestionOverlay {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$RepositoryRoot,
        [switch]$AllowPlanningIngestionProfile)
    $resolved=[IO.Path]::GetFullPath($Path)
    $allowedRoot=[IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(-not $resolved.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase) -or
       [IO.Path]::GetExtension($resolved) -cne '.json' -or -not [IO.File]::Exists($resolved) -or
       ([IO.FileInfo]::new($resolved)).Length -gt 65536) { throw 'The ingestion overlay must be a small campaign JSON under repository artifacts.' }
    $overlay=[IO.File]::ReadAllText($resolved,[Text.Encoding]::UTF8)|ConvertFrom-Json -AsHashtable
    return (Assert-OwnedIngestionOverlayValues -Overlay $overlay -AllowPlanningIngestionProfile:$AllowPlanningIngestionProfile)
}

function Assert-OwnedIngestionOverlayValues {
    param([Parameter(Mandatory)][object]$Overlay,[switch]$AllowPlanningIngestionProfile)
    if($overlay -isnot [Collections.IDictionary] -or $overlay.Count -ne 3 -or
       @($overlay.Keys|Where-Object{$_ -cnotin @('Ingestion','Rag','DocumentIntelligence')}).Count) {
        throw 'Only Ingestion, Rag and DocumentIntelligence may be overlaid; authentication and database settings are immutable.'
    }
    $allowed=@{
        Ingestion=@('WorkerEnabled','WorkerConcurrency','CanonicalArtifactsEnabled','WatcherEnabled','ScannerEnabled',
            'MinFileAgeSeconds','ReindexIfQdrantEmpty','OcrEnabled','OcrImagePageEnabled','TeiTimeoutSeconds','QdrantTimeoutSeconds',
            'HeavyComputeMaxConcurrency','WorkerEmptyDelayMs')
        Rag=@('QdrantBaseUrl','QdrantCollection','EmbeddingsBaseUrl','EmbeddingsModel','EmbeddingsModelRevision',
            'EmbeddingsRuntimeRevision','QdrantRuntimeRevision','EnableRerank','RequireQdrantAuthInProd')
        DocumentIntelligence=@('Enabled','Provider','BaseUrl','TimeoutSeconds','MaxResponseBytes','DoOcr','ForceOcr','OcrPreset',
            'PdfBackend','TableMode','TableCellMatching','DoTableStructure','IncludeImages','IncludePageImages',
            'HeadingHierarchyEnabled','HeadingHierarchyUseBookmarks','HeadingHierarchyUseNumbering','HeadingHierarchyUseStyle',
            'HeadingHierarchyMaxLevel','HeadingHierarchyBookmarkMatchThreshold','RegionalTableRepairEnabled',
            'RegionalTableRepairScale','RegionalTableRepairPaddingPoints','RegionalTableRepairMinimumScore',
            'NativeTextCoverageReconciliationEnabled','NativeTextCoverageMinimumLineCoverage','NativePdfImageInventoryEnabled',
            'Device','NumThreads','ServiceWorkerConcurrency','EngineVersion','ParseEngineVersion','ModelPackageVersion',
            'OcrEngineVersion','DeploymentRevision')
    }
    if($AllowPlanningIngestionProfile){
        $allowed.Ingestion+=@('ChunkMaxWords','ChunkMinWords','ChunkOverlapWords')
        $allowed.DocumentIntelligence+=@('RegionalTableAlternativeReadingsEnabled','NativeTypographyObservationsEnabled',
            'DoclingInputGeometryCaptureEnabled','RegionalTextRepairEnabled','RegionalTextRepairMaxRegions',
            'RegionalTextRepairTotalTimeoutSeconds','RegionalTextRepairRegionTimeoutSeconds','RegionalTextRepairRenderScale',
            'RegionalTextRepairPaddingPoints','RegionalTextRepairMinimumRapidScore','RegionalTextRepairMinimumSecondaryScore',
            'RegionalTextRepairMaxRenderedPixels')
        foreach($name in @('RegionalTableAlternativeReadingsEnabled','NativeTypographyObservationsEnabled','DoclingInputGeometryCaptureEnabled','RegionalTextRepairEnabled')){
            if($overlay.DocumentIntelligence.Contains($name) -and $overlay.DocumentIntelligence[$name] -isnot [bool]){throw 'Planning observation flags must be actual booleans.'}
        }
        # These are the bounded ingestion defaults qualified by the recipe campaign,
        # not arbitrary chunk tuning supplied through an executable configuration.
        foreach($entry in @{ChunkMaxWords=200;ChunkMinWords=25;ChunkOverlapWords=0}.GetEnumerator()){
            if($overlay.Ingestion.Contains($entry.Key) -and ($overlay.Ingestion[$entry.Key] -isnot [int] -or $overlay.Ingestion[$entry.Key] -ne $entry.Value)){throw 'Planning chunk dimensions must remain 200/25/0.'}
        }
        foreach($entry in @{RegionalTextRepairMaxRegions=4;RegionalTextRepairTotalTimeoutSeconds=60;
            RegionalTextRepairRegionTimeoutSeconds=15;RegionalTextRepairRenderScale=4;RegionalTextRepairPaddingPoints=2;
            RegionalTextRepairMinimumRapidScore=0.98;RegionalTextRepairMinimumSecondaryScore=0.95;
            RegionalTextRepairMaxRenderedPixels=20000000}.GetEnumerator()){
            if($overlay.DocumentIntelligence.Contains($entry.Key) -and $overlay.DocumentIntelligence[$entry.Key] -ne $entry.Value){throw 'Planning regional text repair must use the explicit bounded CPU profile.'}
        }
    }
    foreach($section in $overlay.Keys) {
        if($overlay[$section] -isnot [Collections.IDictionary]){throw 'Overlay sections must be objects.'}
        foreach($key in $overlay[$section].Keys) {
            if($key -cnotin $allowed[$section]){throw ('Forbidden ingestion overlay key: '+$section+'.'+$key)}
            $value=$overlay[$section][$key]
            if($value -is [Collections.IDictionary] -or $value -is [array] -or $null -eq $value){throw 'Overlay values must be explicit scalar configuration values.'}
            if($value -is [string] -and ($value.Length -gt 1024 -or $value -match '[\r\n\x00]')){throw 'Overlay strings must be bounded single-line values.'}
        }
    }
    foreach($name in @('WorkerEnabled','CanonicalArtifactsEnabled')) {
        if($overlay.Ingestion[$name] -isnot [bool] -or $overlay.Ingestion[$name] -ne $true){throw 'The ingestion campaign must execute its canonical worker.'}
    }
    foreach($name in @('WatcherEnabled','ScannerEnabled','ReindexIfQdrantEmpty','OcrEnabled','OcrImagePageEnabled')) {
        if($overlay.Ingestion[$name] -isnot [bool] -or $overlay.Ingestion[$name] -ne $false){throw 'Background scanners and legacy OCR must remain explicitly disabled in the isolated Docling campaign.'}
    }
    if(($overlay.Ingestion.WorkerConcurrency -isnot [int] -and $overlay.Ingestion.WorkerConcurrency -isnot [long]) -or $overlay.Ingestion.WorkerConcurrency -ne 1 -or
       ($overlay.Ingestion.MinFileAgeSeconds -isnot [int] -and $overlay.Ingestion.MinFileAgeSeconds -isnot [long]) -or $overlay.Ingestion.MinFileAgeSeconds -ne 0){throw 'Use one worker and explicit zero minimum file age for the controlled enqueue.'}
    if($overlay.DocumentIntelligence.Enabled -isnot [bool] -or -not $overlay.DocumentIntelligence.Enabled -or
       $overlay.DocumentIntelligence.Provider -cne 'docling' -or $overlay.DocumentIntelligence.DoOcr -isnot [bool] -or
       -not $overlay.DocumentIntelligence.DoOcr){throw 'A real enabled Docling OCR profile is required.'}
    foreach($name in @('EngineVersion','ParseEngineVersion','ModelPackageVersion','OcrEngineVersion','DeploymentRevision')) {
        if([string]::IsNullOrWhiteSpace($overlay.DocumentIntelligence[$name])){throw 'Explicit observed Docling engine and deployment identities are required.'}
    }
    Assert-IngestionLoopbackUrl $overlay.DocumentIntelligence.BaseUrl
    Assert-IngestionLoopbackUrl $overlay.Rag.QdrantBaseUrl
    Assert-IngestionLoopbackUrl $overlay.Rag.EmbeddingsBaseUrl
    if($overlay.Rag.QdrantCollection -cnotmatch '^[a-z0-9][a-z0-9_-]{0,63}$' -or
       [string]::IsNullOrWhiteSpace($overlay.Rag.EmbeddingsModel) -or
       [string]::IsNullOrWhiteSpace($overlay.Rag.QdrantRuntimeRevision)) { throw 'A fresh named collection and explicit embedding/Qdrant identity are required.' }
    if($overlay.Rag.EnableRerank -isnot [bool] -or $overlay.Rag.EnableRerank -ne $false -or
       $overlay.Rag.RequireQdrantAuthInProd -isnot [bool] -or $overlay.Rag.RequireQdrantAuthInProd -ne $false){throw 'The local diagnostic requires explicit local no-auth Qdrant and no reranker.'}
    return $overlay
}

function Read-OwnedEmbeddingProfile {
    param([Parameter(Mandatory)][string]$Path)
    $resolved=[IO.Path]::GetFullPath($Path)
    if(-not [IO.File]::Exists($resolved) -or ([IO.FileInfo]::new($resolved)).Length -gt 65536){throw 'A bounded explicit local embedding profile is required.'}
    $profile=[IO.File]::ReadAllText($resolved,[Text.Encoding]::UTF8)|ConvertFrom-Json -AsHashtable
    if($profile -isnot [Collections.IDictionary] -or $profile.Count -ne 4 -or
       @($profile.Keys|Where-Object{$_ -cnotin @('schemaVersion','adapter','modelFileSha256','Rag')}).Count -or
       $profile.schemaVersion -cne 'saaia-local-embedding-profile.v1' -or
       $profile.adapter -cne 'transformers_cpu' -or $profile.modelFileSha256 -cnotmatch '^[a-f0-9]{64}$' -or
       $profile.Rag -isnot [Collections.IDictionary] -or $profile.Rag.Count -ne 4 -or
       @($profile.Rag.Keys|Where-Object{$_ -cnotin @('EmbeddingsBaseUrl','EmbeddingsModel','EmbeddingsModelRevision','EmbeddingsRuntimeRevision')}).Count){throw 'Malformed local Transformers embedding profile.'}
    Assert-IngestionLoopbackUrl $profile.Rag.EmbeddingsBaseUrl
    if($profile.Rag.EmbeddingsModelRevision -cnotmatch '^[a-f0-9]{40}(?:[a-f0-9]{24})?$'){throw 'Pin an explicit immutable embedding model revision.'}
    foreach($name in @('EmbeddingsModel','EmbeddingsRuntimeRevision')){
        $value=$profile.Rag[$name]
        if($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or $value.Length -gt 512 -or $value -match '[\r\n\x00]'){throw 'Explicit bounded embedding model/runtime identities are required.'}
    }
    return $profile
}
