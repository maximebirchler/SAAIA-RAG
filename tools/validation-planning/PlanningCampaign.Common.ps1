#requires -Version 7.4
# Planning is a separate campaign contract. Historical factual profiles stay unchanged.
. (Join-Path $PSScriptRoot '../validation-paid/PaidCampaign.Common.ps1')
. (Join-Path $PSScriptRoot '../validation-paid/PaidLedger.ps1')
. (Join-Path $PSScriptRoot 'PlanningRagRuntime.ps1')

function Assert-PlanningProfile {
    param([Parameter(Mandatory)][hashtable]$Profile,[Parameter(Mandatory)][string]$RepositoryRoot)
    $p=$Profile
    if($p.schemaVersion -cne 'saaia-local-openai-planning-campaign.v1' -or
       $p.provider.model -cne 'gpt-5.6-luna' -or $p.provider.baseUrl -cne 'https://api.openai.com/v1' -or
       $p.provider.reasoningEffort -cne 'low' -or $p.provider.apiProtocol -cne 'responses') {
        throw 'Only the reviewed economical Luna Responses/low planning profile is admitted.'
    }
    if([decimal]$p.budget.authorizedUsd -ne [decimal]0.50 -or [decimal]$p.budget.hardStopUsd -ne [decimal]0.50 -or
       [decimal]$p.budget.baselineCostUsd -lt [decimal]0.00678686 -or [decimal]$p.budget.baselineCostUsd -ge [decimal]0.50 -or
       [decimal]$p.budget.maximumCostPerJobUsd -le 0 -or
       [decimal]$p.budget.maximumCostPerJobUsd -gt ([decimal]0.50-[decimal]$p.budget.baselineCostUsd)) {
        throw 'The single 0.50 USD envelope includes the established baseline and every planning attempt.'
    }
    $root=[IO.Path]::GetFullPath($p.budget.campaignRoot)
    $artifacts=[IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts'))+[IO.Path]::DirectorySeparatorChar
    if(-not $root.StartsWith($artifacts,[StringComparison]::OrdinalIgnoreCase) -or
       [IO.Path]::GetFullPath($p.budget.sharedLedgerPath) -ine (Join-Path $root 'usage.jsonl')) {
        throw 'A single explicit persistent campaign ledger below artifacts is required.'
    }
    $e=$p.execution
    if($e.nativeResearchTopology -cne 'agent' -or $e.candidateExplorerEnabled -ne $false -or
       $e.researchWorkspaceEnabled -ne $false -or $e.semanticCriticEnabled -ne $false){
        throw 'This first planning campaign explicitly uses the monolithic native agent, without staged explorer/workspace/critic.'
    }
    if([int]$e.maximumHttpAttempts -ne 1 -or [int]$e.maximumJobAttempts -ne 1 -or
       [int]$e.maximumJobsPerAttempt -ne 1 -or [int]$e.maximumCallsPerJob -lt 4 -or [int]$e.maximumCallsPerJob -gt 64 -or
       [int]$e.maximumToolCalls -lt 4 -or [int]$e.maximumToolCalls -gt 128 -or
       [int]$e.plannerMaxTokens -lt 512 -or [int]$e.plannerMaxTokens -gt 2048 -or
       [int]$e.writerMaxTokens -lt 1024 -or [int]$e.writerMaxTokens -gt 8192 -or
       [int]$e.timeoutSeconds -lt 60 -or [int]$e.timeoutSeconds -gt 1200) {
        throw 'Planning requires explicit bounded calls, tools, tokens, time and one job/HTTP attempt.'
    }
    foreach($pair in @(@('maximumEvidencePromptCharacters',14000,65536),@('maximumEvidenceCharactersTotal',24000,96000),
        @('maximumEvidenceCharactersPerItem',1000,14000),@('maximumHistoryCharacters',16384,65536),@('maximumAccumulatedEvidenceItems',20,512))) {
        if([int]$e[$pair[0]] -lt $pair[1] -or [int]$e[$pair[0]] -gt $pair[2]) { throw 'Planning context bounds are outside the reviewed envelope.' }
    }
    $ports=@($e.backendPort,$e.fixturePort,$e.paidBackendPort,$e.preflightProviderPort)
    if(@($ports|Where-Object{[int]$_ -lt 1024 -or [int]$_ -gt 65535 -or [int]$_ -eq 5432}).Count -gt 0 -or
       @($ports|Sort-Object -Unique).Count -ne 4) { throw 'Four distinct unprivileged local ports are required.' }
    $r=$p.pricing
    if($r.pricingMode -cne 'conservative_long_context_upper_bound' -or
       [decimal]$r.inputUsdPerMillion -ne [decimal]0.40 -or [decimal]$r.cachedInputUsdPerMillion -ne [decimal]0.04 -or
       [decimal]$r.cacheWriteUsdPerMillion -ne [decimal]0.50 -or [decimal]$r.outputUsdPerMillion -ne [decimal]1.80 -or
       [decimal]$r.baseRates.inputUsdPerMillion -ne [decimal]0.20 -or [decimal]$r.baseRates.cachedInputUsdPerMillion -ne [decimal]0.02 -or
       [decimal]$r.baseRates.cacheWriteUsdPerMillion -ne [decimal]0.25 -or [decimal]$r.baseRates.outputUsdPerMillion -ne [decimal]1.20 -or
       $r.sourceUrl -cne 'https://developers.openai.com/api/docs/models/gpt-5.6-luna') {
        throw 'The reviewed conservative long-context upper bound and separately recorded base rates are required.'
    }
    $null=ConvertTo-PaidUtcStamp $r.observedAtUtc
    if($p.case.kind -cnotin @('five-day-20','one-day-4') -or [string]::IsNullOrWhiteSpace($p.case.question)) {
        throw 'An explicit planning case is required.'
    }
    $days=if($p.case.kind -ceq 'five-day-20'){5}else{1}
    $selectionPolicy=if($p.case.kind -ceq 'one-day-4'){'distinct_structured_layout'}else{'structured_layout'}
    if($p.case.selectionPolicy -cne $selectionPolicy){throw 'An explicit selection policy matching the registered planning case is required.'}
    if(@($p.case.rowLabels).Count -ne $days -or @($p.case.columns).Count -ne 4 -or
       [int]$p.case.answerUnitCount -ne ($days*4) -or
       ($days -eq 1 -and $p.case.functionalFiveDayAcceptance -ne $false) -or
       ($days -eq 5 -and $p.case.functionalFiveDayAcceptance -ne $true)) { throw 'The declared grid and functional scope must agree.' }
    if($p.ContainsKey('credentials') -or $p.provider.ContainsKey('apiKey') -or $p.provider.ContainsKey('key')) {
        throw 'A planning profile must never contain provider credentials.'
    }
    if($p.corpus.origin -ceq 'real_ingestion_worker' -or $p.ContainsKey('realRag')){$null=Read-PlanningRealRagProfile $p}
    return $p
}

function Get-ValidatedPlanningProfile {
    param([Parameter(Mandatory)][string]$ProfilePath,[Parameter(Mandatory)][string]$RepositoryRoot)
    return Assert-PlanningProfile -Profile (Get-Content -LiteralPath $ProfilePath -Raw|ConvertFrom-Json -AsHashtable -Depth 40) -RepositoryRoot $RepositoryRoot
}

function Assert-PlanningExecuteAuthorization {
    param([hashtable]$Profile,[switch]$AuthorizePaid,[switch]$AuthorizeCorpusTransmission,[decimal]$PaidCapUsd)
    if(-not $AuthorizePaid -or -not $AuthorizeCorpusTransmission -or $PaidCapUsd -ne [decimal]0.50 -or
       $Profile.spendAuthorization.status -cne 'AUTHORIZED' -or [string]::IsNullOrWhiteSpace($Profile.spendAuthorization.basis)) {
        throw 'Root-reviewed explicit paid/transmission authorization within the existing 0.50 USD envelope is required. No secret was read.'
    }
    $stamp=ConvertTo-PaidUtcStamp $Profile.spendAuthorization.authorizedAtUtc
    if($stamp -gt [DateTimeOffset]::UtcNow.AddMinutes(2)){throw 'Authorization timestamp cannot be in the future.'}
}

function New-PlanningHandoff {
    param([Parameter(Mandatory)][hashtable]$Profile)
    $case=$Profile.case
    return @{
        schemaVersion='saaia.advanced-analysis-handoff.v1';handoffId=[Guid]::NewGuid().ToString('D');createdAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
        requestText=$case.question;language='fr';originIntent='rag.answer';reasonCode='paid_local_planning_validation';transferStage='before_retrieval';
        load=@{planKind='structured_layout';deliverable='source_backed_meal_planning';answerUnitCount=[int]$case.answerUnitCount;
            atomicEvidenceCount=[int]$case.answerUnitCount;rowCount=@($case.rowLabels).Count;columnCount=4;structuredLayout=$true;
            atomicEvidenceType='named_item';atomicEvidenceMode='named_item';selectionPolicy=$case.selectionPolicy;questionFocus='content';
            requestedDocumentName='';boundedNamedDocumentExtraction=$false;candidateScopePaths=@();rowLabels=@($case.rowLabels);columns=@($case.columns)};
        researchState=@{memoryIsEvidence=$false;evidenceRevalidationRequired=$true;executedTools=@();executedQueries=@();attempts=@();evidenceReferences=@()};
        dataPolicy=@{externalProviderContentAuthorized=$false;externalProviderMetadataAuthorized=$false;authorizationSource='server_controlled'}
    }
}

function Get-PlanningAdvancedOptions {
    param([hashtable]$Profile,[string]$OutputDirectory,[string]$RepositoryRoot)
    $null=Assert-PlanningProfile -Profile $Profile -RepositoryRoot $RepositoryRoot
    $e=$Profile.execution;$remaining=[decimal]$Profile.budget.authorizedUsd-[decimal]$Profile.budget.baselineCostUsd
    return @{
        ReasoningEffort='low';NativeResearchToolsEnabled=$true;NativeObservationReadEnabled=$true;NativeResearchApiProtocol='responses';NativeResearchTopology='agent';
        NativeCandidateExplorerEnabled=$false;StagedCandidateExplorerEnabled=$false;NativeResearchWorkspaceEnabled=$false;SemanticCriticEnabled=$false;
        NativeResearchMaximumHistoryCharacters=[int]$e.maximumHistoryCharacters;PlannerMaxTokens=[int]$e.plannerMaxTokens;WriterMaxTokens=[int]$e.writerMaxTokens;
        ExternalMaximumCallsPerJob=[int]$e.maximumCallsPerJob;MaximumToolCalls=[int]$e.maximumToolCalls;MaximumPlanQueries=8;
        MaximumEvidencePromptCharacters=[int]$e.maximumEvidencePromptCharacters;MaximumEvidenceCharactersTotal=[int]$e.maximumEvidenceCharactersTotal;
        MaximumEvidenceCharactersPerItem=[int]$e.maximumEvidenceCharactersPerItem;MaximumAccumulatedEvidenceItems=[int]$e.maximumAccumulatedEvidenceItems;
        MaximumToolElapsedMilliseconds=([int]$e.timeoutSeconds*1000);ExternalBudgetAuthorizedUsd=$remaining;ExternalBudgetSoftLimitUsd=$remaining;ExternalBudgetHardLimitUsd=$remaining;
        ExternalMaximumCostPerJobUsd=[decimal]$Profile.budget.maximumCostPerJobUsd;ExternalUsageLedgerPath=[IO.Path]::GetFullPath($Profile.budget.sharedLedgerPath);
        DevelopmentTraceMaximumFiles=1000;DevelopmentTraceMaximumCharacters=3000000;DevelopmentTraceMaximumDirectoryBytes=67108864
    }
}

function Get-PlanningEnvelopeLedgerSummary {
    param([Parameter(Mandatory)][hashtable]$Profile)
    return Get-PaidLedgerSummary -LedgerPath $Profile.budget.sharedLedgerPath -PriorCampaignCostUsd $Profile.budget.baselineCostUsd -AuthorizedCapUsd $Profile.budget.authorizedUsd
}

function Get-ValidatedPlanningCorpusManifest {
    param([hashtable]$Profile)
    $path=[IO.Path]::GetFullPath($Profile.corpus.manifestPath)
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $Profile.corpus.manifestSha256){throw 'The complete-corpus manifest changed after registration.'}
    $m=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -AsHashtable -Depth 40
    if($m.origin -ceq 'real_ingestion_worker' -and $Profile.corpus.origin -cne 'real_ingestion_worker'){throw 'A new worker publication must use the explicit real-Rag planning route.'}
    if($m.schemaVersion -cne 'saaia-planning-complete-corpus.v1' -or $m.completeDocuments -ne $true -or
       @($m.documents).Count -lt 1 -or @($m.documents|Group-Object docPath|Where-Object Count -ne 1).Count -gt 0){
        throw 'A full-document corpus manifest is required; selected pages or remembered recipe windows are insufficient.'
    }
    foreach($d in $m.documents){
        $doc=[Guid]::Empty;$rev=[Guid]::Empty
        if(-not [Guid]::TryParse($d.docId,[ref]$doc) -or $doc -eq [Guid]::Empty -or
           -not [Guid]::TryParse($d.revisionId,[ref]$rev) -or $rev -eq [Guid]::Empty -or
           $d.sourceSha256 -cnotmatch '^[a-fA-F0-9]{64}$' -or $d.canonicalHash -cnotmatch '^[a-fA-F0-9]{64}$' -or
           [int]$d.pageCount -lt 1 -or [int]$d.retrievalChunkCount -lt 1 -or [int]$d.sourceAnchorCount -lt 1 -or
           $d.completeCanonicalBundle -ne $true -or
           (Get-FileHash -LiteralPath $d.physicalPdfPath -Algorithm SHA256).Hash -ine $d.sourceSha256){
            throw 'Every complete PDF and canonical publication must have exact identity, source hash and positive counts.'
        }
    }
    return $m
}

function Assert-OwnedPlanningCorpus {
    param([string]$OutputDirectory,[hashtable]$Manifest)
    $query=@'
SELECT jsonb_build_object('jobs',(SELECT count(*) FROM advanced_analysis_jobs),
 'documents',(SELECT coalesce(jsonb_agg(jsonb_build_object('docPath',d.doc_path,'docId',d.doc_id,'revisionId',r.revision_id,
 'sourceSha256',encode(r.source_hash,'hex'),'pageCount',d.page_count,
 'retrievalChunkCount',(SELECT count(*) FROM retrieval_chunks c WHERE c.revision_id=r.revision_id),
 'sourceAnchorCount',(SELECT count(*) FROM document_source_anchors a WHERE a.revision_id=r.revision_id),
 'canonicalHash',(SELECT encode(b.content_hash,'hex') FROM document_revision_binary_artifacts b WHERE b.revision_id=r.revision_id AND b.artifact_type='canonical_ingestion_bundle')) ORDER BY d.doc_path),'[]'::jsonb)
 FROM documents d JOIN document_revisions r ON r.doc_id=d.doc_id));
'@
    $observed=Invoke-OwnedPaidSql -OutputDirectory $OutputDirectory -Query $query|ConvertFrom-Json -Depth 30
    if([int]$observed.jobs -ne 0 -or @($observed.documents).Count -ne @($Manifest.documents).Count){throw 'Planning requires a fresh owned job store and exactly the registered complete corpus.'}
    foreach($expected in $Manifest.documents){
        $actual=@($observed.documents|Where-Object docPath -CEQ $expected.docPath)
        if($actual.Count -ne 1){throw 'A registered full document is missing from the prepared owned store.'}
        foreach($key in @('docId','revisionId','sourceSha256','pageCount','retrievalChunkCount','sourceAnchorCount','canonicalHash')){
            if([string]$actual[0].$key -ine [string]$expected[$key]){throw "Prepared corpus differs in $key; no provider call may start."}
        }
    }
    return $observed
}

function Export-OwnedPlanningCorpusReadback {
    param([string]$OutputDirectory,[string]$FileName='planning-corpus-sql.private.json')
    if($FileName -cnotmatch '^[a-z0-9][a-z0-9.-]{0,100}\.json$'){throw 'A plain bounded SQL evidence filename is required.'}
    $query=@'
SELECT jsonb_build_object('schemaVersion','saaia-planning-corpus-sql-observation.v1',
 'retrievalChunks',(SELECT coalesce(jsonb_agg(jsonb_build_object('retrievalChunkId',retrieval_chunk_id,'revisionId',revision_id,
   'sectionId',section_id,'unitId',unit_id,'chunkIndex',chunk_index,'pageStart',page_start,'pageEnd',page_end,
   'text',text_content,'checksumSha256',encode(checksum,'hex'),'metadata',metadata) ORDER BY revision_id,chunk_index),'[]'::jsonb) FROM retrieval_chunks),
 'units',(SELECT coalesce(jsonb_agg(jsonb_build_object('unitId',unit_id,'revisionId',revision_id,'sectionId',section_id,
   'ordinal',ordinal,'pageStart',page_start,'pageEnd',page_end,'text',text_content,'checksumSha256',encode(checksum,'hex'),
   'metadata',metadata) ORDER BY revision_id,ordinal),'[]'::jsonb) FROM document_units),
 'sections',(SELECT coalesce(jsonb_agg(jsonb_build_object('sectionId',section_id,'revisionId',revision_id,'ordinal',ordinal,
   'title',title,'pageStart',page_start,'pageEnd',page_end) ORDER BY revision_id,ordinal),'[]'::jsonb) FROM document_sections),
 'contentCards',(SELECT coalesce(jsonb_agg(jsonb_build_object('contentCardId',content_card_id,'docId',doc_id,'revisionId',revision_id,
   'documentProfileId',document_profile_id,'cardIndex',card_index,'title',title,'normalizedTitle',normalized_title,
   'pageStart',page_start,'pageEnd',page_end,'searchText',search_text,'checksumSha256',encode(checksum,'hex'),
   'metadata',metadata) ORDER BY revision_id,card_index),'[]'::jsonb) FROM document_profile_content_cards));
'@
    $raw=Invoke-OwnedPaidSql -OutputDirectory $OutputDirectory -Query $query
    $parsed=$raw|ConvertFrom-Json -Depth 60
    if($parsed.schemaVersion -cne 'saaia-planning-corpus-sql-observation.v1'){throw 'Malformed owned canonical corpus readback.'}
    $path=Join-Path $OutputDirectory ('runtime/'+$FileName)
    $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes($raw)
    $stream=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    return @{path=$path;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));
        retrievalChunks=@($parsed.retrievalChunks).Count;units=@($parsed.units).Count;sections=@($parsed.sections).Count;contentCards=@($parsed.contentCards).Count}
}
