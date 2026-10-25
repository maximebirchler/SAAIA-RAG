#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputPath, [Parameter(Mandatory)][string]$CorpusPdfPath,
    [Parameter(Mandatory)][int[]]$PageNumbers, [Parameter(Mandatory)][string]$Question,
    [Parameter(Mandatory)][string]$ExpectedAnswerDescription, [decimal]$PriorCampaignCostUsd = 0)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$path=[IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $path) { throw 'Never overwrite a frozen campaign profile.' }
$pdf=[IO.Path]::GetFullPath($CorpusPdfPath)
$backend=Join-Path $repo 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'
$p=[ordered]@{
    schemaVersion='saaia-local-openai-paid-campaign.v1'; campaignId=[Guid]::NewGuid().ToString('N');
    provider=@{mode='OpenAI';model='gpt-5.6-luna';baseUrl='https://api.openai.com/v1';reasoningEffort='none'};
    budget=@{authorizedUsd=0.50;hardStopUsd=0.50;priorCampaignCostUsd=$PriorCampaignCostUsd;maximumCostPerJobUsd=([decimal]0.50-$PriorCampaignCostUsd)};
    pricing=@{inputUsdPerMillion=0.20;cachedInputUsdPerMillion=0.02;cacheWriteUsdPerMillion=0.20;outputUsdPerMillion=1.20;
        sourceUrl='https://developers.openai.com/api/docs/models/gpt-5.6-luna';observedAtUtc='2026-10-25T19:15:28Z';
        observationBasis='Official model/pricing documentation verified by the parent operator for the 2026-10-25T19:15:28Z availability probe; none reasoning support verified. Not fetched by this script.'};
    execution=@{maximumHttpAttempts=1;maximumJobAttempts=1;repetitions=1;maximumJobs=1;maximumCallsPerJob=4;
        plannerMaxTokens=512;writerMaxTokens=1024;timeoutSeconds=120;backendPort=5157;fixturePort=18107;paidBackendPort=5158;
        backendAssemblySha256=(Get-FileHash -LiteralPath $backend -Algorithm SHA256).Hash};
    corpus=@{path=[IO.Path]::GetRelativePath($repo,$pdf).Replace('\','/');sha256=(Get-FileHash -LiteralPath $pdf -Algorithm SHA256).Hash;pageNumbers=@($PageNumbers)};
    case=@{question=$Question;expectedAnswerDescription=$ExpectedAnswerDescription;expectedOutcome='answered'};
    spendAuthorization=@{status='PROPOSED';authorizedAtUtc=$null;basis=$null};
    boundaries=@{ingestionValidated=$false;vectorRetrievalValidated=$false;teiQdrantAreProtocolSubstitutes=$true;clientProviderMustRemain='Local'}
}
. (Join-Path $PSScriptRoot 'PaidCampaign.Common.ps1')
[IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
# Validate in memory through a temporary sibling which is deleted before return.
$temp=$path+'.proposal-'+[Guid]::NewGuid().ToString('N')
try {
    [IO.File]::WriteAllText($temp,($p|ConvertTo-Json -Depth 20))
    $null=Get-ValidatedLunaCampaignProfile -ProfilePath $temp -RepositoryRoot $repo
    [IO.File]::Move($temp,$path,$false)
} finally { if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp} }
Write-Output 'Proposed frozen Luna profile created; no authorization, secret read, network call or process launch.'
