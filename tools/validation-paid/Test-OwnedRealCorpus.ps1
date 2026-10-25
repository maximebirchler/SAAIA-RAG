#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProfilePath,[Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$PythonExecutable,[Parameter(Mandatory)][string]$SearchQuery,
    [Parameter(Mandatory)][int]$ExpectedPageNumber,[Parameter(Mandatory)][string[]]$ExpectedTextPatterns,
    [switch]$ExecuteLocal)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'PaidCampaign.Common.ps1')
. (Join-Path $PSScriptRoot 'PaidRuntime.ps1')
. (Join-Path $PSScriptRoot 'PaidLedger.ps1')
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$p=Get-ValidatedLunaCampaignProfile $ProfilePath $repo
$root=[IO.Path]::GetFullPath($OutputDirectory)
$prefix=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))+[IO.Path]::DirectorySeparatorChar
if(-not $root.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $root)){throw 'A fresh owned artifacts directory is required.'}
if($ExpectedPageNumber -notin @($p.corpus.pageNumbers) -or -not $ExpectedTextPatterns.Count){throw 'The expected real page and text patterns must be explicitly selected.'}
$dll=Join-Path $repo 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'
if((Get-FileHash -LiteralPath $dll).Hash -ine $p.execution.backendAssemblySha256){throw 'Regenerate the proposed profile after the backend build is stable.'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$proof=[ordered]@{schemaVersion='saaia-owned-real-corpus-preflight.v1';executed=$false;passed=$false;providerCalls=0;paidSecretsRead=0;
    profileSha256=(Get-FileHash -LiteralPath $ProfilePath).Hash;backendAssemblySha256=$p.execution.backendAssemblySha256;
    realPdf=$true;ingestionValidated=$false;vectorIndexIsProtocolSubstitute=$true;expectedPageNumber=$ExpectedPageNumber;
    expectedTextPatternCount=$ExpectedTextPatterns.Count;cleanup=@{zeroActiveJobs=$false;ownedEnvironmentStopped=$false};state='prepared';
    admission=@{tested=$false;workerEnabled=$false;providerKeyIsSynthetic=$true;http202=$false;canceled=$false}}
if(-not $ExecuteLocal){
    [IO.File]::WriteAllText((Join-Path $root 'corpus-preflight.json'),($proof|ConvertTo-Json -Depth 15))
    Write-Output 'Prepared local corpus preflight; ExecuteLocal is required to start owned local services. No key/provider access.'
    return
}
$fixture=Join-Path $root 'local-fixture'
$admissionUrl=$null;$admissionJob=$null;$userId=[Guid]::NewGuid().ToString('D');$headers=$null
try{
    & $PythonExecutable -B -c 'import pypdf'
    if($LASTEXITCODE -ne 0){throw 'Choose a trusted local Python with pypdf; no downloads are performed.'}
    $proof.executed=$true
    & (Join-Path $repo 'tools/validation-winui/start-environment.ps1') -OutputDirectory $fixture -BackendPort $p.execution.backendPort -FixturePort $p.execution.fixturePort -NoBuild
    $owner=Get-OwnedPaidEnvironment $fixture
    & $PythonExecutable -B (Join-Path $PSScriptRoot 'prepare_real_corpus.py') --output $fixture --pdf (Join-Path $repo $p.corpus.path) --sha256 $p.corpus.sha256 --pages ($p.corpus.pageNumbers -join ',')
    if($LASTEXITCODE -ne 0){throw 'Real PDF text extraction failed.'}
    $null=Invoke-OwnedPaidSql -OutputDirectory $fixture -SeedRealCorpus
    $identity=Get-Content -LiteralPath (Join-Path $fixture 'real-corpus-manifest.json') -Raw|ConvertFrom-Json
    $private=Get-Content -LiteralPath (Join-Path $fixture 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
    $headers=@{'X-Api-Key'=$private.apiKey;'X-Admin-Key'=$private.apiKey}
    $null=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/admin/catalog/snapshot/refresh') -Headers $headers -TimeoutSec 20
    $catalog=Invoke-RestMethod -Uri ($owner.backendUrl+'/catalog/documents?pageSize=10') -Headers $headers -TimeoutSec 10
    $search=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/rag/search') -Headers $headers -ContentType 'application/json' `
        -Body (@{query=$SearchQuery;docId=$identity.docId;topK=8;sourceBackedCanonical=$true}|ConvertTo-Json) -TimeoutSec 20
    $context=Invoke-RestMethod -Uri ($owner.backendUrl+'/documents/context?docId='+$identity.docId+'&pageStart='+$ExpectedPageNumber+'&pageEnd='+$ExpectedPageNumber+'&before=0&after=0') -Headers $headers -TimeoutSec 20
    [IO.File]::WriteAllText((Join-Path $fixture 'runtime/canonical-observations.private.json'),(@{catalog=$catalog;search=$search;context=$context}|ConvertTo-Json -Depth 35))
    $text=(@($context.items)|ForEach-Object{[string]$_.text}) -join "`n"
    if([string]::IsNullOrWhiteSpace($text)){$text=(@($context.items)|ForEach-Object{[string]$_.textContent}) -join "`n"}
    $proof.catalogDocumentCount=@($catalog.value).Count
    $proof.canonicalSearchHitCount=@($search.items).Count
    $proof.canonicalReadFound=[bool]$context.found
    $proof.canonicalReadExactIdentity=($context.document.docId -eq $identity.docId -and $context.document.revisionId -eq $identity.revisionId -and
        ([string]$context.document.sourceHash -replace '^sha256:','') -ieq $identity.sourceSha256 -and
        @($context.items|Where-Object{$_.pageStart -ne $ExpectedPageNumber -or $_.pageEnd -ne $ExpectedPageNumber}).Count -eq 0)
    $proof.expectedTextPatternsMatched=@($ExpectedTextPatterns|Where-Object{$text -match $_}).Count
    $proof.readTextSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
    $proof.passed=($proof.catalogDocumentCount -eq 1 -and $proof.canonicalSearchHitCount -gt 0 -and $proof.canonicalReadFound -and
        $proof.canonicalReadExactIdentity -and $proof.expectedTextPatternsMatched -eq $ExpectedTextPatterns.Count)
    if(-not $proof.passed){throw 'Canonical PDF search/read failed before admission validation.'}
    # Same provider/config/entitlement and payload as the real runner, but no
    # worker and no real key: job admission must be proven before any paid test.
    $admissionUrl=Start-OwnedPaidBackend -OutputDirectory $fixture -RepositoryRoot $repo -Profile $p `
        -ApiKey 'synthetic-admission-only-worker-disabled-no-provider-key' -WorkerEnabled:$false
    $session=Invoke-RestMethod -Method Post -Uri ($admissionUrl+'/chat/sessions') -Headers $headers -ContentType 'application/json' `
        -Body (@{userId=$userId;title='Local paid admission without worker';clientUser='synthetic-admission-preflight'}|ConvertTo-Json) -TimeoutSec 10
    $handoff=New-LunaCampaignHandoff -Profile $p
    $body=@{userId=$userId;sessionId=$session.sessionId;handoff=$handoff}|ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText((Join-Path $fixture 'runtime/admission-submission.private.json'),$body)
    $response=Invoke-WebRequest -Method Post -Uri ($admissionUrl+'/advanced-analysis/jobs') -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 15
    $admissionJob=$response.Content|ConvertFrom-Json
    $proof.admission.tested=$true;$proof.admission.http202=([int]$response.StatusCode -eq 202)
    $proof.admission.jobId=$admissionJob.jobId;$proof.admission.statusBeforeCancel=$admissionJob.status
    $null=Invoke-RestMethod -Method Post -Uri ($admissionUrl+'/advanced-analysis/jobs/'+$admissionJob.jobId+'/cancel?userId='+$userId) -Headers $headers -TimeoutSec 10
    $afterCancel=Invoke-RestMethod -Uri ($admissionUrl+'/advanced-analysis/jobs/'+$admissionJob.jobId+'?userId='+$userId) -Headers $headers -TimeoutSec 10
    $proof.admission.canceled=$afterCancel.status -eq 'canceled'
    $proof.admission.payloadSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($body)))
    $ledger=Get-PaidLedgerSummary (Join-Path $fixture 'runtime/usage.jsonl')
    $proof.admission.logicalTerminalLedgerRows=$ledger.logicalTerminalLedgerRows
    $proof.admission.durableReservationStarts=$ledger.durableReservationStarts
    $proof.admission.providerHttpAttemptsInLedger=$ledger.httpAttemptsInTerminalRows
    $proof.admission.noProviderExecution=($ledger.logicalTerminalLedgerRows -eq 0 -and $ledger.durableReservationStarts -eq 0)
    $proof.admission.noProviderExecutionProofLimit='Worker disabled in production config; queued job canceled; zero provider ledger starts/terminals. No packet capture.'
    $proof.passed=($proof.passed -and $proof.admission.http202 -and $proof.admission.canceled -and $proof.admission.noProviderExecution)
    $proof.state=if($proof.passed){'passed_local_canonical_tools'}else{'failed_local_canonical_tools'}
}catch{$proof.state='failed';$proof.failureType=$_.Exception.GetType().Name}
finally{
    if(Test-Path -LiteralPath (Join-Path $fixture 'environment.json')){
        try{
            if($admissionUrl -and $headers){
                $owned=Invoke-OwnedPaidSql -OutputDirectory $fixture -Query "SELECT COALESCE(json_agg(job_id),'[]'::json) FROM advanced_analysis_jobs WHERE user_id='$userId' AND status IN ('queued','running');"
                foreach($id in @($owned|ConvertFrom-Json)){$null=Invoke-RestMethod -Method Post -Uri ($admissionUrl+'/advanced-analysis/jobs/'+([Guid]$id).ToString('D')+'/cancel?userId='+$userId) -Headers $headers -TimeoutSec 10}
            }
            $raw=Invoke-OwnedPaidSql -OutputDirectory $fixture -Query "SELECT json_build_object('active',(SELECT count(*) FROM advanced_analysis_jobs WHERE status IN ('queued','running')),'total',(SELECT count(*) FROM advanced_analysis_jobs));"
            $counts=$raw|ConvertFrom-Json;$proof.cleanup.zeroActiveJobs=([int]$counts.active -eq 0);$proof.cleanup.totalJobsPreserved=$counts.total
        }catch{$proof.cleanup.sqlCheckFailed=$true}
        try{& (Join-Path $repo 'tools/validation-winui/stop-environment.ps1') -OutputDirectory $fixture -Confirm:$false;
            $owner=Get-Content -LiteralPath (Join-Path $fixture 'environment.json') -Raw|ConvertFrom-Json;$proof.cleanup.ownedEnvironmentStopped=$owner.state -eq 'stopped'}catch{$proof.cleanup.stopFailed=$true}
    }
    $headers=$null;$private=$null
    if(-not $proof.cleanup.zeroActiveJobs -or -not $proof.cleanup.ownedEnvironmentStopped){$proof.passed=$false}
    [IO.File]::WriteAllText((Join-Path $root 'corpus-preflight.json'),($proof|ConvertTo-Json -Depth 20))
}
if(-not $proof.passed){throw 'The owned real-corpus preflight failed; review its local private evidence before any API test.'}
Write-Output 'Real local PDF, canonical SQL/search/read and admission passed; zero provider calls/active jobs; canceled synthetic job retained; owned services stopped.'
