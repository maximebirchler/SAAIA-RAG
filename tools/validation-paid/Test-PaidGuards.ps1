#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'PaidCampaign.Common.ps1')
. (Join-Path $PSScriptRoot 'PaidLedger.ps1')
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $root){throw 'Use a new offline evidence root.'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[scriptblock]$Body,[switch]$MustThrow){
    $threw=$false
    try{& $Body|Out-Null}catch{$threw=$true}
    $ok=if($MustThrow){$threw}else{-not $threw}
    $checks.Add(@{name=$Name;passed=$ok})
    if(-not $ok){throw "Offline paid guard check failed: $Name"}
}
$fakeRepo=Join-Path $root 'fake-repository'
$pdf=Join-Path $fakeRepo 'out/remote-deploy/test.pdf'
[IO.Directory]::CreateDirectory((Split-Path -Parent $pdf))|Out-Null
[IO.File]::WriteAllText($pdf,'offline byte fixture; PDF parsing is not performed by pure profile validation')
$profilePath=Join-Path $root 'profile.json'
$base=@{schemaVersion='saaia-local-openai-paid-campaign.v1';provider=@{mode='OpenAI';model='gpt-5.6-luna';baseUrl='https://api.openai.com/v1';reasoningEffort='none'};
    budget=@{authorizedUsd=0.50;hardStopUsd=0.50;priorCampaignCostUsd=0;maximumCostPerJobUsd=0.50};
    pricing=@{inputUsdPerMillion=0.20;cachedInputUsdPerMillion=0.02;cacheWriteUsdPerMillion=0.20;outputUsdPerMillion=1.20;
        sourceUrl='https://developers.openai.com/api/docs/pricing';observedAtUtc='2026-10-25T00:00:00Z'};
    execution=@{maximumHttpAttempts=1;maximumJobAttempts=1;repetitions=1;maximumJobs=1;maximumCallsPerJob=4;
        plannerMaxTokens=512;writerMaxTokens=1024;timeoutSeconds=120;backendPort=5157;fixturePort=18107;paidBackendPort=5158};
    corpus=@{path='out/remote-deploy/test.pdf';sha256=(Get-FileHash -LiteralPath $pdf).Hash;pageNumbers=@(1)};
    case=@{question='Question explicite';expectedAnswerDescription='Réponse attendue définie avant essai';expectedOutcome='answered'};
    spendAuthorization=@{status='PROPOSED';authorizedAtUtc=$null;basis=$null}}
function Write-Profile([scriptblock]$Change={}){
    $candidate=$base|ConvertTo-Json -Depth 30|ConvertFrom-Json -AsHashtable
    & $Change $candidate
    [IO.File]::WriteAllText($profilePath,($candidate|ConvertTo-Json -Depth 30))
}
try{
    Write-Profile
    Check 'proposed profile validates without key or authorization' {Get-ValidatedLunaCampaignProfile $profilePath $fakeRepo}
    foreach($mutation in @(
        @{name='Terra forbidden';change={param($p)$p.provider.model='gpt-5.6-terra'}},
        @{name='reasoning budget must be explicitly none for the first factual case';change={param($p)$p.provider.reasoningEffort='low'}},
        @{name='nonofficial endpoint forbidden';change={param($p)$p.provider.baseUrl='https://api.openai.com.attacker.invalid/v1'}},
        @{name='incremental cap cannot exceed authorization';change={param($p)$p.budget.authorizedUsd=0.51;$p.budget.hardStopUsd=0.51}},
        @{name='previous probe cost counts against job cap';change={param($p)$p.budget.priorCampaignCostUsd=0.01}},
        @{name='provider retries forbidden';change={param($p)$p.execution.maximumHttpAttempts=2}},
        @{name='job retries forbidden';change={param($p)$p.execution.maximumJobAttempts=2}},
        @{name='second job forbidden';change={param($p)$p.execution.maximumJobs=2}},
        @{name='production PG port forbidden';change={param($p)$p.execution.backendPort=5432}},
        @{name='port collisions forbidden';change={param($p)$p.execution.paidBackendPort=$p.execution.backendPort}},
        @{name='frozen source hash enforced';change={param($p)$p.corpus.sha256='0'*64}},
        @{name='secret-bearing profile forbidden';change={param($p)$p.provider.apiKey='synthetic-forbidden-value'}}
    )){
        Write-Profile $mutation.change
        Check $mutation.name {Get-ValidatedLunaCampaignProfile $profilePath $fakeRepo} -MustThrow
    }
    Write-Profile
    $p=Get-ValidatedLunaCampaignProfile $profilePath $fakeRepo
    $handoff=New-LunaCampaignHandoff -Profile $p
    Check 'actual submission payload leaves external authorization to the server' {
        if($handoff.dataPolicy.externalProviderContentAuthorized -or $handoff.dataPolicy.externalProviderMetadataAuthorized -or
            $handoff.requestText -cne $p.case.question -or $handoff.load.requestedDocumentName -cne 'test.pdf'){throw 'Client handoff violates the server-controlled data-policy contract'}
    }
    Check 'Execute rejects proposed authorization' {Assert-LunaExecuteAuthorization $p -AuthorizePaid -AuthorizeCorpusTransmission -PaidCapUsd 0.50} -MustThrow
    $p.spendAuthorization=@{status='AUTHORIZED';authorizedAtUtc=[DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ');basis='Offline synthetic authorization, no provider execution'}
    Check 'Execute rejects missing transmission authorization' {Assert-LunaExecuteAuthorization $p -AuthorizePaid -PaidCapUsd 0.50} -MustThrow
    Check 'Execute rejects a cap below profile' {Assert-LunaExecuteAuthorization $p -AuthorizePaid -AuthorizeCorpusTransmission -PaidCapUsd 0.49} -MustThrow
    Check 'all explicit authorization gates can validate purely offline' {Assert-LunaExecuteAuthorization $p -AuthorizePaid -AuthorizeCorpusTransmission -PaidCapUsd 0.50}
    $trx=Join-Path $root 'sample.trx';$result=Join-Path $root 'sample-result.json'
    [IO.File]::WriteAllText($result,'{"rows":[{"id":"A"}]}')
    [IO.File]::WriteAllText($trx,'<TestRun><Results><UnitTestResult testName="paid" outcome="NotExecuted"/></Results><ResultSummary><Counters executed="1" passed="0" notExecuted="0"/></ResultSummary></TestRun>')
    Check 'false adapter counters cannot hide a true skip' {Assert-LiveTestEvidence $trx $result -ExpectedCaseIds A} -MustThrow
    [IO.File]::WriteAllText($trx,'<TestRun><Results/></TestRun>')
    Check 'empty TRX cannot pass' {Assert-LiveTestEvidence $trx $result} -MustThrow
    [IO.File]::WriteAllText($trx,'<TestRun><Results><UnitTestResult testName="paid" outcome="Passed"/></Results></TestRun>')
    Check 'TRX plus exact result case passes' {Assert-LiveTestEvidence $trx $result -ExpectedCaseIds A}
    Check 'missing selected case fails' {Assert-LiveTestEvidence $trx $result -ExpectedCaseIds @('A','B')} -MustThrow
    [IO.File]::WriteAllText($result,'{"rows":[{"id":"A"},{"id":"A"}]}')
    Check 'duplicate result cases fail' {Assert-LiveTestEvidence $trx $result -ExpectedCaseIds @('A','B')} -MustThrow
    Remove-Item -LiteralPath $result
    Check 'a result artifact is mandatory despite passed TRX' {Assert-LiveTestEvidence $trx $result} -MustThrow
    $ledger=Join-Path $root 'synthetic-usage.jsonl'
    $start=@{schema='advanced-budget-reservation.v1';RequestId='r1';ReservedCostUsd=0.10;JobId='synthetic';Role='planner'}
    [IO.File]::WriteAllText(($ledger+'.reservations.jsonl'),($start|ConvertTo-Json -Compress))
    $summary=Get-PaidLedgerSummary $ledger -PriorCampaignCostUsd 0.01
    Check 'unsettled durable reservation remains financially reserved' {
        if($summary.state -ne 'UNCERTAIN_RESERVED' -or $summary.totalBoundedCampaignCostUsd -ne [decimal]0.11 -or $summary.unsettledReservationCount -ne 1){throw 'Unsettled cost lost'}
    }
    $charge=@{requestId='r1';costUsd=0.02;httpAttemptCount=1;observedModelId='gpt-5.6-luna';usageSource='provider_usage';modelId='gpt-5.6-luna'}
    [IO.File]::WriteAllText($ledger,($charge|ConvertTo-Json -Compress))
    $summary=Get-PaidLedgerSummary $ledger -PriorCampaignCostUsd 0.01
    Check 'terminal settles matching reservation without charging it twice' {
        if($summary.unsettledReservationCount -ne 0 -or $summary.totalBoundedCampaignCostUsd -ne [decimal]0.03 -or
            $summary.httpAttemptsInTerminalRows -ne 1 -or -not $summary.observedExpectedFamily){throw 'Incorrect settled projection'}
    }
    $charge.usageSource='reserved_upper_bound'
    [IO.File]::WriteAllText($ledger,($charge|ConvertTo-Json -Compress))
    Check 'ambiguous timeout charge is labeled uncertain even when locally settled' {
        if((Get-PaidLedgerSummary $ledger).state -ne 'UNCERTAIN_RESERVED'){throw 'Ambiguity hidden'}
    }
    $charge.observedModelId='gpt-5.6-terra'
    [IO.File]::WriteAllText($ledger,($charge|ConvertTo-Json -Compress))
    Check 'observed Terra does not match the allowed Luna identity' {
        if((Get-PaidLedgerSummary $ledger).observedModelMismatchCount -ne 1){throw 'Wrong model hidden'}
    }
    [IO.File]::AppendAllText($ledger,"`n"+($charge|ConvertTo-Json -Compress))
    Check 'duplicate request settlement cannot yield a public success' {Get-PaidLedgerSummary $ledger} -MustThrow
    [IO.File]::WriteAllText($ledger,'{"requestId":"r1","httpAttemptCount":1}')
    Check 'schema-valid JSON without a valid cost is rejected' {Get-PaidLedgerSummary $ledger} -MustThrow
}finally{
    [IO.File]::WriteAllText((Join-Path $root 'checks.json'),(@{schemaVersion='saaia-paid-offline-checks.v1';checks=@($checks);providerCalls=0;servicesStarted=0;secretsRead=0}|ConvertTo-Json -Depth 10))
}
Write-Output "$($checks.Count) offline paid guards passed; no secret, service, dotnet or network."
