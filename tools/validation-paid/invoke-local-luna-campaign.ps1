#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProfilePath, [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Execute, [switch]$AuthorizePaid, [switch]$AuthorizeCorpusTransmission,
    [decimal]$PaidCapUsd=0, [string]$SecretStorePath='', [string]$PythonExecutable='')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'PaidCampaign.Common.ps1')
. (Join-Path $PSScriptRoot 'PaidLedger.ps1')
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$profile=Get-ValidatedLunaCampaignProfile -ProfilePath $ProfilePath -RepositoryRoot $repo
$output=[IO.Path]::GetFullPath($OutputDirectory)
$allowed=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))+[IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) {
    throw 'Select a new owned campaign directory below repository artifacts.'
}
$backend=Join-Path $repo 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'
if ((Get-FileHash -LiteralPath $backend -Algorithm SHA256).Hash -ine $profile.execution.backendAssemblySha256) {
    throw 'The backend binary differs from the frozen campaign profile.'
}
if ($Execute) {
    Assert-LunaExecuteAuthorization -Profile $profile -AuthorizePaid:$AuthorizePaid `
        -AuthorizeCorpusTransmission:$AuthorizeCorpusTransmission -PaidCapUsd $PaidCapUsd
    if (-not [IO.Path]::IsPathFullyQualified($SecretStorePath) -or -not (Test-Path -LiteralPath $SecretStorePath -PathType Leaf)) {
        throw 'An explicit existing DPAPI store path is required after authorization. No secret was read.'
    }
    if (-not [IO.Path]::IsPathFullyQualified($PythonExecutable) -or -not (Test-Path -LiteralPath $PythonExecutable -PathType Leaf)) {
        throw 'Provide an explicit trusted local Python with pypdf; no package download or runtime fallback is allowed.'
    }
    & $PythonExecutable -B -c 'import pypdf'
    if($LASTEXITCODE -ne 0){throw 'The selected local Python lacks pypdf. No secret was read or service started.'}
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$proof=[ordered]@{schemaVersion='saaia-local-paid-result.v1';startedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
    profileSha256=(Get-FileHash -LiteralPath $ProfilePath -Algorithm SHA256).Hash;executed=$false;completed=$false;
    model='gpt-5.6-luna';incrementalCapUsd=$profile.budget.authorizedUsd;priorCampaignCostUsd=$profile.budget.priorCampaignCostUsd;
    backendAssemblySha256=$profile.execution.backendAssemblySha256;secretRead=$false;logicalTerminalLedgerRows=0;
    pricing=$profile.pricing;expectedModel='gpt-5.6-luna';
    providerUsageLedgerSha256=$null;state='prepared';productStatus='TESTE_NON_APPROUVE';semanticApproval='PENDING';
    limits=@('TEI/Qdrant are deterministic loopback protocol substitutes; canonical SQL retrieval and original PDF text are real.',
        'PDF extraction is seeded for this focused test; this does not validate ingestion, OCR or vector recall.',
        'The single backend job does not validate local-client routing or native UI. No client or local model is launched.');
    cleanup=@{zeroActiveJobs=$false;workerQuiescentBeforeStop=$false;ownedEnvironmentStopped=$false;errors=@()}}
if (-not $Execute) {
    $proof.cleanup=@{zeroActiveJobs=$null;ownedEnvironmentStopped=$null;errors=@()}
    [IO.File]::WriteAllText((Join-Path $output 'campaign-result.json'),($proof|ConvertTo-Json -Depth 20))
    Write-Output 'Dry preflight prepared; no secret read, service/build/network/provider call.'
    return
}
. (Join-Path $PSScriptRoot 'PaidRuntime.ps1')
$fixture=Join-Path $output 'local-fixture'
$job=$null; $userId=[Guid]::NewGuid().ToString('D'); $headers=$null; $paidUrl=$null; $key=$null; $plain=$null; $failure=$null
try {
    $proof.executed=$true; $proof.state='running'
    # The only credential acquisition point is below every explicit Execute/auth gate.
    Add-Type -AssemblyName System.Security
    $secret=Get-Content -LiteralPath $SecretStorePath -Raw|ConvertFrom-Json
    $entropy=[Text.Encoding]::UTF8.GetBytes('SAAIA.Client.WinUI|CDC-v2.7')
    $plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($secret.OpenAiApiKeyProtected),$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $key=[Text.Encoding]::UTF8.GetString($plain)
    if ([string]::IsNullOrWhiteSpace($key)) { throw 'The explicitly supplied DPAPI key is empty.' }
    $proof.secretRead=$true; $secret=$null
    & (Join-Path $repo 'tools/validation-winui/start-environment.ps1') -OutputDirectory $fixture `
        -BackendPort $profile.execution.backendPort -FixturePort $profile.execution.fixturePort -NoBuild
    $null=Get-OwnedPaidEnvironment $fixture
    & $PythonExecutable -B (Join-Path $PSScriptRoot 'prepare_real_corpus.py') --output $fixture `
        --pdf (Join-Path $repo $profile.corpus.path) --sha256 $profile.corpus.sha256 --pages ($profile.corpus.pageNumbers -join ',')
    if ($LASTEXITCODE -ne 0) { throw 'Real corpus extraction failed; no paid job submitted.' }
    $null=Invoke-OwnedPaidSql -OutputDirectory $fixture -SeedRealCorpus
    $owner=Get-OwnedPaidEnvironment $fixture
    $private=Get-Content -LiteralPath (Join-Path $fixture 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
    $headers=@{'X-Api-Key'=$private.apiKey;'X-Admin-Key'=$private.apiKey;'X-SAAIA-Turn-Id'=('paid-'+[Guid]::NewGuid().ToString('N'))}
    $null=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/admin/catalog/snapshot/refresh') -Headers $headers -TimeoutSec 20
    $catalog=Invoke-RestMethod -Uri ($owner.backendUrl+'/catalog/documents?pageSize=10') -Headers $headers -TimeoutSec 10
    if (@($catalog.value).Count -ne 1) { throw 'The owned local catalogue must expose exactly one real document.' }
    $paidUrl=Start-OwnedPaidBackend -OutputDirectory $fixture -RepositoryRoot $repo -Profile $profile -ApiKey $key
    $session=Invoke-RestMethod -Method Post -Uri ($paidUrl+'/chat/sessions') -Headers $headers -ContentType 'application/json' `
        -Body (@{userId=$userId;title='Isolated paid Luna validation';clientUser='paid-campaign'}|ConvertTo-Json) -TimeoutSec 10
    $headers['X-SAAIA-Session-Id']=[string]$session.sessionId
    $handoff=New-LunaCampaignHandoff -Profile $profile
    [IO.File]::WriteAllText((Join-Path $fixture 'runtime/submission.private.json'),(@{userId=$userId;sessionId=$session.sessionId;handoff=$handoff}|ConvertTo-Json -Depth 20))
    $job=Invoke-RestMethod -Method Post -Uri ($paidUrl+'/advanced-analysis/jobs') -Headers $headers -ContentType 'application/json' `
        -Body (@{userId=$userId;sessionId=$session.sessionId;handoff=$handoff}|ConvertTo-Json -Depth 20) -TimeoutSec 15
    $proof.jobId=$job.jobId; $proof.userId=$userId; $proof.sessionId=$session.sessionId
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds([int]$profile.execution.timeoutSeconds)
    do {
        Start-Sleep -Milliseconds 300
        $job=Invoke-RestMethod -Uri ($paidUrl+'/advanced-analysis/jobs/'+$job.jobId+'?userId='+$userId) -Headers $headers -TimeoutSec 10
        if ($job.status -notin @('queued','running')) { break }
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'The owned paid job timed out; cancellation follows in finally.' }
    } while ($true)
    [IO.File]::WriteAllText((Join-Path $fixture 'runtime/terminal-result.private.json'),($job|ConvertTo-Json -Depth 50))
    $proof.terminalStatus=$job.status; $proof.outcome=$job.result.outcome
    if ($job.status -ne 'succeeded' -or $job.result.outcome -ne $profile.case.expectedOutcome) {
        throw 'The real paid job did not achieve the frozen expected outcome; inspect private job/payload evidence before another call.'
    }
    $corpus=Get-Content -LiteralPath (Join-Path $fixture 'real-corpus-manifest.json') -Raw|ConvertFrom-Json
    $evidence=@($job.result.evidence)
    $proof.canonicalSourceVerified=($evidence.Count -gt 0 -and
        @($evidence|Where-Object{$_.docId -ne $corpus.docId -or $_.revisionId -ne $corpus.revisionId -or
            ([string]$_.sourceHash -replace '^sha256:','') -ine $corpus.sourceSha256}).Count -eq 0)
    if(-not $proof.canonicalSourceVerified){throw 'The terminal answer must cite the frozen real local PDF revision and hash.'}
    $proof.completed=$true; $proof.state='completed_requires_semantic_review'
} catch {
    $failure=$_.Exception.GetType().Name
    $proof.state='failed'; $proof.failureType=$failure
} finally {
    if ($paidUrl -and $headers -and (Test-Path -LiteralPath (Join-Path $fixture 'environment.json'))) {
        try {
            # A submission can reach the database even if its HTTP response is lost.
            # Discover this exact new owner's jobs rather than relying on $job alone.
            $rawJobs=Invoke-OwnedPaidSql -OutputDirectory $fixture -Query "SELECT COALESCE(json_agg(job_id),'[]'::json) FROM advanced_analysis_jobs WHERE user_id='$userId' AND status IN ('queued','running');"
            $activeIds=@($rawJobs|ConvertFrom-Json)
            foreach($activeId in $activeIds){
                $parsed=[Guid]::Empty
                if(-not [Guid]::TryParse([string]$activeId,[ref]$parsed)){throw 'Invalid owned job ID in cleanup.'}
                $null=Invoke-RestMethod -Method Post -Uri ($paidUrl+'/advanced-analysis/jobs/'+$parsed.ToString('D')+'/cancel?userId='+$userId) -Headers $headers -TimeoutSec 10
            }
        } catch { $proof.cleanup.errors+=@('owned_job_cancel_failed') }
    }
    if (Test-Path -LiteralPath (Join-Path $fixture 'environment.json')) {
        try {
            # Fresh owned DB has exactly one campaign owner. No unrelated jobs can be hidden by filtering.
            $raw=Invoke-OwnedPaidSql -OutputDirectory $fixture -Query "SELECT json_build_object('active',(SELECT count(*) FROM advanced_analysis_jobs WHERE status IN ('queued','running')),'jobs',(SELECT count(*) FROM advanced_analysis_jobs),'toolEvents',(SELECT count(*) FROM advanced_analysis_tool_events),'toolNames',(SELECT COALESCE(json_agg(DISTINCT tool_name),'[]'::json) FROM advanced_analysis_tool_events));"
            $counts=$raw|ConvertFrom-Json
            $proof.cleanup.zeroActiveJobs=([int]$counts.active -eq 0)
            $proof.cleanup.observedJobCount=$counts.jobs
            $proof.toolEventCount=$counts.toolEvents; $proof.toolNames=@($counts.toolNames)
            if (-not $proof.cleanup.zeroActiveJobs) { $proof.cleanup.errors+=@('active_owned_jobs_remain') }
            $ledger=Join-Path $fixture 'runtime/usage.jsonl'
            # A database cancellation can precede HTTP termination. Allow heartbeat
            # cancellation to settle its durable reservation before stopping the worker.
            $quietDeadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
            do{
                $summary=Get-PaidLedgerSummary $ledger -PriorCampaignCostUsd $profile.budget.priorCampaignCostUsd -AuthorizedCapUsd $profile.budget.authorizedUsd
                if($summary.unsettledReservationCount -eq 0){Start-Sleep -Milliseconds 1200;
                    $afterQuiet=Get-PaidLedgerSummary $ledger -PriorCampaignCostUsd $profile.budget.priorCampaignCostUsd -AuthorizedCapUsd $profile.budget.authorizedUsd
                    if($afterQuiet.unsettledReservationCount -eq 0 -and $afterQuiet.ledgerSha256 -eq $summary.ledgerSha256){
                        $proof.cleanup.workerQuiescentBeforeStop=$true;break
                    }
                }
                if([DateTimeOffset]::UtcNow -gt $quietDeadline){break}
                Start-Sleep -Milliseconds 250
            }while($true)
            $proof.financial=Get-PaidLedgerSummary $ledger -PriorCampaignCostUsd $profile.budget.priorCampaignCostUsd -AuthorizedCapUsd $profile.budget.authorizedUsd
            $proof.logicalTerminalLedgerRows=$proof.financial.logicalTerminalLedgerRows
            $proof.providerUsageLedgerSha256=$proof.financial.ledgerSha256
            if(-not $proof.financial.withinAuthorizedCap -or $proof.financial.observedModelMismatchCount -gt 0 -or
                $proof.financial.requestedModelMismatchCount -gt 0 -or $proof.financial.httpAttemptPolicyViolations -gt 0){$proof.cleanup.errors+=@('financial_or_model_identity_failed')}
            if($proof.completed -and ($proof.logicalTerminalLedgerRows -eq 0 -or -not $proof.financial.observedExpectedFamily -or $proof.toolEventCount -eq 0)){
                $proof.cleanup.errors+=@('missing_real_provider_or_tool_execution')
            }
        } catch { $proof.cleanup.errors+=@('owned_sql_postflight_failed') }
        try {
            & (Join-Path $repo 'tools/validation-winui/stop-environment.ps1') -OutputDirectory $fixture -Confirm:$false
            $stopped=Get-Content -LiteralPath (Join-Path $fixture 'environment.json') -Raw|ConvertFrom-Json
            $proof.cleanup.ownedEnvironmentStopped=$stopped.state -eq 'stopped'
        } catch { $proof.cleanup.errors+=@('owned_environment_stop_failed') }
    }
    if($plain){[Array]::Clear($plain,0,$plain.Length)}; $key=$null; $headers=$null; $private=$null
    $proof.endedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    if (-not $proof.cleanup.zeroActiveJobs -or -not $proof.cleanup.workerQuiescentBeforeStop -or
        -not $proof.cleanup.ownedEnvironmentStopped -or $proof.cleanup.errors.Count -gt 0) {
        $proof.completed=$false; $proof.state='failed_or_cleanup_incomplete'
    }
    if($proof.financial -and $proof.financial.state -eq 'UNCERTAIN_RESERVED'){$proof.completed=$false;$proof.state='UNCERTAIN_RESERVED'}
    [IO.File]::WriteAllText((Join-Path $output 'campaign-result.json'),($proof|ConvertTo-Json -Depth 20))
}
if (-not $proof.completed) { throw 'The isolated paid campaign failed or lacks complete cleanup proof. No automatic retry is performed.' }
Write-Output 'One owned Luna job completed; services stopped and evidence retained. Semantic approval remains pending.'
