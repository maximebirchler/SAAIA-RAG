#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputPath,[Parameter(Mandatory)][string]$CampaignRoot,
    [Parameter(Mandatory)][string]$CorpusManifestPath,[ValidateSet('five-day-20','one-day-4')][string]$CaseKind='five-day-20',
    [string]$RealRagProfilePath='')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'PlanningCampaign.Common.ps1')
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output=[IO.Path]::GetFullPath($OutputPath)
if(Test-Path -LiteralPath $output){throw 'Never overwrite a frozen planning profile.'}
$manifest=[IO.Path]::GetFullPath($CorpusManifestPath)
if(-not (Test-Path -LiteralPath $manifest -PathType Leaf)){throw 'A reviewed complete-corpus manifest must exist before profile registration.'}
$bank=Get-Content -LiteralPath (Join-Path $repo 'config/advanced-capacity-validation.v1.json') -Raw|ConvertFrom-Json
$question=($bank.validationCases|Where-Object id -CEQ 'A755-ADV-01-meal-grid-5x4').question
$days=@('lundi','mardi','mercredi','jeudi','vendredi')
$manifestValue=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json -AsHashtable -Depth 40
if($CaseKind -ceq 'one-day-4'){$days=@('lundi');$question="Prépare un planning de repas pour lundi incluant petit-déjeuner, déjeuner, collation et souper. Quatre propositions concrètes distinctes, un format clair, avec des preuves utiles. N'invente rien."}
$profile=@{
    schemaVersion='saaia-local-openai-planning-campaign.v1';campaignId=[Guid]::NewGuid().ToString('N');
    provider=@{model='gpt-5.6-luna';baseUrl='https://api.openai.com/v1';reasoningEffort='low';apiProtocol='responses'};
    pricing=@{pricingMode='conservative_long_context_upper_bound';inputUsdPerMillion=0.40;cachedInputUsdPerMillion=0.04;cacheWriteUsdPerMillion=0.50;outputUsdPerMillion=1.80;
        baseRates=@{inputUsdPerMillion=0.20;cachedInputUsdPerMillion=0.02;cacheWriteUsdPerMillion=0.25;outputUsdPerMillion=1.20};
        sourceUrl='https://developers.openai.com/api/docs/models/gpt-5.6-luna';observedAtUtc='2026-10-27T17:37:00Z';
        observationBasis='Official documentation and model availability checked by root; this generator makes no network call.'};
    budget=@{authorizedUsd=0.50;hardStopUsd=0.50;baselineCostUsd=0.00678686;maximumCostPerJobUsd=0.49321314;
        campaignRoot=[IO.Path]::GetFullPath($CampaignRoot);sharedLedgerPath=(Join-Path ([IO.Path]::GetFullPath($CampaignRoot)) 'usage.jsonl')};
    execution=@{maximumHttpAttempts=1;maximumJobAttempts=1;maximumJobsPerAttempt=1;maximumCallsPerJob=64;maximumToolCalls=128;
        nativeResearchTopology='agent';candidateExplorerEnabled=$false;researchWorkspaceEnabled=$false;semanticCriticEnabled=$false;
        plannerMaxTokens=1024;writerMaxTokens=8192;timeoutSeconds=900;backendPort=22501;fixturePort=22502;paidBackendPort=22503;preflightProviderPort=22504;
        maximumEvidencePromptCharacters=65536;maximumEvidenceCharactersTotal=96000;maximumEvidenceCharactersPerItem=14000;
        maximumHistoryCharacters=65536;maximumAccumulatedEvidenceItems=512;
        backendAssemblySha256=(Get-FileHash -LiteralPath (Join-Path $repo 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll')).Hash};
    corpus=@{manifestPath=$manifest;manifestSha256=(Get-FileHash -LiteralPath $manifest).Hash;completeDocumentsRequired=$true};
    case=@{kind=$CaseKind;question=$question;rowLabels=$days;columns=@('petit-déjeuner','déjeuner','collation','souper');
        answerUnitCount=($days.Count*4);functionalFiveDayAcceptance=($CaseKind -ceq 'five-day-20');
        selectionPolicy=$(if($CaseKind -ceq 'one-day-4'){'distinct_structured_layout'}else{'structured_layout'})};
    spendAuthorization=@{status='PROPOSED';authorizedAtUtc=$null;basis=$null};
    boundaries=@{newIngestionValidated=$false;denseRetrievalValidated=$false;clientUiValidated=$false;semanticApproval='PENDING'}
}
if($manifestValue.origin -ceq 'real_ingestion_worker'){
    if(-not $RealRagProfilePath){throw 'A new worker publication requires its pinned real-Rag dependency profile.'}
    $profile.corpus.origin='real_ingestion_worker'
    $profile.realRag=@{path=[IO.Path]::GetFullPath($RealRagProfilePath);sha256=(Get-FileHash -LiteralPath $RealRagProfilePath).Hash}
}
elseif($RealRagProfilePath){throw 'Real Rag overrides are reserved for the explicitly new worker corpus.'}
$null=Assert-PlanningProfile -Profile $profile -RepositoryRoot $repo
[IO.Directory]::CreateDirectory((Split-Path -Parent $output))|Out-Null
$stream=[IO.FileStream]::new($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
try{$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($profile|ConvertTo-Json -Depth 30));$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
Write-Output 'Planning profile proposed; no secret, API, process, build or paid authorization.'
