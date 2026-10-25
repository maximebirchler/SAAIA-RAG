#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'PaidCampaign.Common.ps1')
. (Join-Path $PSScriptRoot 'PaidLedger.ps1')
. (Join-Path $PSScriptRoot 'PaidRuntime.ps1')
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
    $unicodeOutput=Join-Path $root 'unicode-query-fixture'
    [IO.Directory]::CreateDirectory((Join-Path $unicodeOutput 'runtime'))|Out-Null
    $unicodeQuery="SELECT 'Bœuf bourguignon ingrédients…' AS token;"
    $queryFile=New-PaidReadOnlyQueryFile -OutputDirectory $unicodeOutput -Query $unicodeQuery
    Check 'read-only SQL file preserves exact Unicode as UTF8 without BOM' {
        $bytes=[IO.File]::ReadAllBytes($queryFile)
        $decoded=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
        if($decoded -cne ("BEGIN READ ONLY;`n"+$unicodeQuery+";`nCOMMIT;`n") -or
            ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)){
            throw 'Unicode SQL bytes or read-only transaction boundary changed.'
        }
        $again=New-PaidReadOnlyQueryFile -OutputDirectory $unicodeOutput -Query $unicodeQuery
        if($again -ceq $queryFile -or -not (Test-Path -LiteralPath $queryFile)){
            throw 'Each query must retain its own private immutable SQL file.'
        }
    }
    Check 'actual owned Query branch uses a SQL file instead of a Unicode command argument' {
        $body=(Get-Command Invoke-OwnedPaidSql).ScriptBlock.ToString()
        if($body -notmatch "Invoke-PaidSqlProcess[^\r\n]+@\('-f',\`$queryFile\)" -or
            $body -match '-c\s*\([^\r\n]*\$Query'){
            throw 'The actual Query branch must consume the UTF8 file; ASCII ownership probe may retain -c.'
        }
    }
    Check 'SQL process keeps Unicode JSON stdout separate from PostgreSQL-style notices' {
        $command='[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);[Console]::Out.Write(''{"token":"Bœuf ingrédients…"}'');[Console]::Error.Write(''NOTICE: punctuation witness only'')'
        $run=Invoke-PaidSqlProcess -Psql (Get-Process -Id $PID).Path `
            -Arguments @('-NoProfile','-NonInteractive','-Command',$command) -OutputDirectory $unicodeOutput
        if(@($run).Count -ne 1){throw 'The SQL process helper must return exactly one result, without task values.'}
        $parsed=$run.stdout|ConvertFrom-Json
        $stderr=[Text.UTF8Encoding]::new($false,$true).GetString([IO.File]::ReadAllBytes($run.stderrPath))
        if($run.exitCode -ne 0 -or $parsed.token -cne 'Bœuf ingrédients…' -or
            $stderr -cne 'NOTICE: punctuation witness only' -or $run.stdout.Contains('NOTICE:')){
            throw 'SQL stdout, stderr or explicit UTF8 decoding was corrupted.'
        }
    }
    $parserSample=@{schemaVersion='saaia.pg-text-parser-conformance.v1';passed=$true;checks=@(
        @{name='unicode_punctuation';passed=$true},@{name='unicode_case';passed=$true},
        @{name='conjunction';passed=$true},@{name='accent_distinction';passed=$true})}
    Check 'paid worker accepts a complete typed parser conformance proof' {Assert-PaidTextParserConformance $parserSample}
    $parserSample.checks[0].passed=$false
    Check 'paid worker rejects malformed punctuation despite a forged global success' {Assert-PaidTextParserConformance $parserSample} -MustThrow
    $parserSample.checks[0].passed=$true;$parserSample.checks[3].name='conjunction'
    Check 'paid worker rejects a missing parser check replaced by a duplicate' {Assert-PaidTextParserConformance $parserSample} -MustThrow
    $parserSample.checks[3].name='accent_distinction';$parserSample.passed='true'
    Check 'paid worker rejects an untyped string success marker' {Assert-PaidTextParserConformance $parserSample} -MustThrow

    $corpusManifest=@{schemaVersion='saaia-paid-real-corpus.v2';
        docId='a94c6984-60a5-4886-a2c4-9d857e540ab1';revisionId='930fa9f6-cd20-41ea-86ab-b583ddc0641b';
        projectionCounts=@{units=3;retrievalChunks=2;contextualEntries=2;exactEntries=1}}
    $corpusObservation=@{schemaVersion='saaia.paid-corpus-sql-observation.v1';checksumAlgorithm='sha256_utf8';tables=@(
        @{name='document_units';textField='text_content';rowCount=3;scopedRowCount=3;checksumMismatchCount=0;malformedCount=0},
        @{name='retrieval_chunks';textField='text_content';rowCount=2;scopedRowCount=2;checksumMismatchCount=0;malformedCount=0},
        @{name='contextual_text_entries';textField='text_content';rowCount=2;scopedRowCount=2;checksumMismatchCount=0;malformedCount=0},
        @{name='exact_match_entries';textField='normalized_text';rowCount=1;scopedRowCount=1;checksumMismatchCount=0;malformedCount=0})}
    $corpusManifestSha='A'*64
    Check 'stored corpus bytes and exact normalized text can satisfy all four table checks' {
        $result=New-PaidCorpusTextConformanceProof $corpusManifest $corpusObservation $corpusManifestSha
        if(-not $result.passed -or $result.tables.Count -ne 4 -or $result.checksumMismatchRowCount -ne 0){throw 'Complete corpus proof rejected.'}
    }
    foreach($mutation in @(
        @{name='changed stored text checksum';change={param($o)$o.tables[0].checksumMismatchCount=1}},
        @{name='missing seeded row';change={param($o)$o.tables[1].rowCount=1;$o.tables[1].scopedRowCount=1}},
        @{name='foreign revision despite matching global count';change={param($o)$o.tables[2].scopedRowCount=1}},
        @{name='null or malformed checksum';change={param($o)$o.tables[3].malformedCount=1}}
    )){
        $candidate=$corpusObservation|ConvertTo-Json -Depth 10|ConvertFrom-Json -AsHashtable
        & $mutation.change $candidate
        Check ('corpus mismatch fails closed: '+$mutation.name) {
            if((New-PaidCorpusTextConformanceProof $corpusManifest $candidate $corpusManifestSha).passed){throw 'Corpus mismatch was accepted.'}
        }
    }
    foreach($mutation in @(
        @{name='string count';change={param($o)$o.tables[0].rowCount='3'}},
        @{name='missing table';change={param($o)$o.tables=@($o.tables[0..2])}},
        @{name='duplicate table';change={param($o)$o.tables[3].name='document_units'}},
        @{name='exact-entry checksum on display text';change={param($o)$o.tables[3].textField='text_content'}},
        @{name='negative count';change={param($o)$o.tables[0].checksumMismatchCount=-1}},
        @{name='impossible mismatch count';change={param($o)$o.tables[3].checksumMismatchCount=2}}
    )){
        $candidate=$corpusObservation|ConvertTo-Json -Depth 10|ConvertFrom-Json -AsHashtable
        & $mutation.change $candidate
        Check ('malformed corpus observation is rejected: '+$mutation.name) {
            New-PaidCorpusTextConformanceProof $corpusManifest $candidate $corpusManifestSha
        } -MustThrow
    }
    Check 'zero exact candidates do not hide mandatory nonempty corpus tables' {
        $manifest=$corpusManifest|ConvertTo-Json -Depth 10|ConvertFrom-Json -AsHashtable
        $observation=$corpusObservation|ConvertTo-Json -Depth 10|ConvertFrom-Json -AsHashtable
        $manifest.projectionCounts.exactEntries=0;$observation.tables[3].rowCount=0;$observation.tables[3].scopedRowCount=0
        if(-not (New-PaidCorpusTextConformanceProof $manifest $observation $corpusManifestSha).passed){throw 'Optional exact entries were incorrectly required.'}
        $manifest.projectionCounts.units=0
        try{New-PaidCorpusTextConformanceProof $manifest $observation $corpusManifestSha|Out-Null}catch{return}
        throw 'An empty mandatory corpus table was accepted.'
    }
    $originalOwnedSql=(Get-Command Invoke-OwnedPaidSql).ScriptBlock
    $originalOwnedEnvironment=(Get-Command Get-OwnedPaidEnvironment).ScriptBlock
    try{
        function Get-OwnedPaidEnvironment {param([string]$OutputDirectory) return @{outputDirectory=$OutputDirectory}}
        function Invoke-OwnedPaidSql {
            param([string]$OutputDirectory,[string]$Query,[switch]$SeedRealCorpus)
            $script:mockCorpusSql=$Query
            $script:mockCorpusSqlCalls++
            return $script:mockCorpusSqlResponse
        }
        $script:mockCorpusSqlCalls=0;$script:mockCorpusSql=''
        $script:mockCorpusSqlResponse=$corpusObservation|ConvertTo-Json -Depth 10 -Compress
        $mockCorpus=Join-Path $root 'mock-corpus-conformance'
        [IO.Directory]::CreateDirectory($mockCorpus)|Out-Null
        [IO.File]::WriteAllText((Join-Path $mockCorpus 'real-corpus-manifest.json'),($corpusManifest|ConvertTo-Json -Depth 10))
        Check 'owned corpus guard uses read-only SQL and writes a content-free immutable success proof' {
            $result=Get-OwnedPaidCorpusTextConformance -OutputDirectory $mockCorpus
            if(-not $result.passed -or $script:mockCorpusSqlCalls -ne 1 -or
                ([regex]::Matches($script:mockCorpusSql,"sha256\(convert_to\(text_content,'UTF8'\)\)")).Count -ne 3 -or
                ([regex]::Matches($script:mockCorpusSql,"sha256\(convert_to\(normalized_text,'UTF8'\)\)")).Count -ne 1 -or
                $script:mockCorpusSql -match '\b(INSERT|UPDATE|DELETE|ALTER|CREATE|DROP|TRUNCATE)\b'){
                throw 'The corpus guard query or proof changed.'
            }
            if(-not(Test-Path -LiteralPath (Join-Path $mockCorpus 'corpus-text-conformance.json'))){throw 'Missing safe corpus proof.'}
        }
        Check 'an earlier corpus proof cannot be silently replaced' {Get-OwnedPaidCorpusTextConformance -OutputDirectory $mockCorpus} -MustThrow
        $badCorpus=Join-Path $root 'mock-corpus-conformance-mismatch'
        [IO.Directory]::CreateDirectory($badCorpus)|Out-Null
        [IO.File]::WriteAllText((Join-Path $badCorpus 'real-corpus-manifest.json'),($corpusManifest|ConvertTo-Json -Depth 10))
        $badObservation=$corpusObservation|ConvertTo-Json -Depth 10|ConvertFrom-Json -AsHashtable
        $badObservation.tables[0].checksumMismatchCount=1
        $script:mockCorpusSqlResponse=$badObservation|ConvertTo-Json -Depth 10 -Compress
        Check 'an observed text checksum mismatch blocks admission with a durable failed proof' {
            Get-OwnedPaidCorpusTextConformance -OutputDirectory $badCorpus
        } -MustThrow
        Check 'failed checksum proof retains counts without exposing corpus text' {
            $result=Get-Content -LiteralPath (Join-Path $badCorpus 'corpus-text-conformance.json') -Raw|ConvertFrom-Json
            if($result.passed -or $result.checksumMismatchRowCount -ne 1 -or $result.failureCode -cne 'stored_corpus_text_mismatch'){
                throw 'A rejected checksum observation lost its failed proof.'
            }
        }
        $invalidCorpus=Join-Path $root 'mock-corpus-conformance-malformed'
        [IO.Directory]::CreateDirectory($invalidCorpus)|Out-Null
        [IO.File]::WriteAllText((Join-Path $invalidCorpus 'real-corpus-manifest.json'),($corpusManifest|ConvertTo-Json -Depth 10))
        $script:mockCorpusSqlResponse='PRIVATE_MALFORMED_SQL_RESPONSE_SENTINEL'
        Check 'malformed SQL observations block admission' {Get-OwnedPaidCorpusTextConformance -OutputDirectory $invalidCorpus} -MustThrow
        Check 'malformed SQL payload is never copied into the public failure proof' {
            $safe=Get-Content -LiteralPath (Join-Path $invalidCorpus 'corpus-text-conformance.json') -Raw
            if($safe.Contains('PRIVATE_MALFORMED_SQL_RESPONSE_SENTINEL') -or ($safe|ConvertFrom-Json).passed){throw 'Malformed raw SQL content escaped into a success or public proof.'}
        }
    }finally{
        Set-Item -Path Function:\Invoke-OwnedPaidSql -Value $originalOwnedSql
        Set-Item -Path Function:\Get-OwnedPaidEnvironment -Value $originalOwnedEnvironment
    }
    foreach($runnerName in @('Test-OwnedRealCorpus.ps1','invoke-local-luna-campaign.ps1')){
        Check ('corpus text gate precedes worker and job admission in '+$runnerName) {
            $runner=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $runnerName))
            $seed=$runner.IndexOf('-SeedRealCorpus',[StringComparison]::Ordinal)
            $gate=$runner.IndexOf('Get-OwnedPaidCorpusTextConformance',[StringComparison]::Ordinal)
            $worker=$runner.IndexOf('Start-OwnedPaidBackend',[StringComparison]::Ordinal)
            $admission=$runner.IndexOf('/advanced-analysis/jobs',[StringComparison]::Ordinal)
            if($seed -lt 0 -or $gate -le $seed -or $worker -le $gate -or $admission -le $gate){throw 'A paid/admission path bypasses corpus text conformance.'}
        }
    }
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
