#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProfilePath,[Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PreparedEnvironmentDirectory='',[string]$JobInspectorDll='',[string]$PythonExecutable='',
    [switch]$Preflight,[switch]$Execute,[switch]$AuthorizePaid,[switch]$AuthorizeCorpusTransmission,
    [decimal]$PaidCapUsd=0,[string]$SecretStorePath='',[string]$PreflightReceiptPath='')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'PlanningCampaign.Common.ps1')
. (Join-Path $PSScriptRoot '../validation-paid/PaidRuntime.ps1')
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$profile=Get-ValidatedPlanningProfile -ProfilePath $ProfilePath -RepositoryRoot $repo
$manifest=Get-ValidatedPlanningCorpusManifest -Profile $profile
$output=[IO.Path]::GetFullPath($OutputDirectory)
$campaign=[IO.Path]::GetFullPath($profile.budget.campaignRoot)
if(-not $output.StartsWith($campaign+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
    throw 'Every attempt must remain below the one registered campaign root.'
}
if($Execute -and $Preflight){throw 'Preflight and paid Execute are separate actions.'}
$proof=[ordered]@{schemaVersion='saaia-local-planning-result.v1';startedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
    profileSha256=(Get-FileHash -LiteralPath $ProfilePath).Hash;corpusManifestSha256=$profile.corpus.manifestSha256;
    preflight=[bool]$Preflight;executed=$false;secretRead=$false;externalProviderUsed=$false;state='prepared';
    model=$profile.provider.model;pricingMode=$profile.pricing.pricingMode;pricing=$profile.pricing;
    authorizedCumulativeUsd=$profile.budget.authorizedUsd;baselineCostUsd=$profile.budget.baselineCostUsd;
    backendAssemblySha256=$profile.execution.backendAssemblySha256;functionalFiveDayAccepted=$false;
    productStatus='TESTE_NON_APPROUVE';semanticReview='PENDING';cleanup=@{zeroActiveJobs=$null;workerQuiescent=$null;ownedEnvironmentStopped=$null;errors=@()}}
if(-not $Execute -and -not $Preflight){
    Write-Output ($proof|ConvertTo-Json -Depth 20)
    Write-Output 'Dry planning preflight: no secret, SQL, process, build, API or provider call.'
    return
}
if($Execute){
    Assert-PlanningExecuteAuthorization -Profile $profile -AuthorizePaid:$AuthorizePaid -AuthorizeCorpusTransmission:$AuthorizeCorpusTransmission -PaidCapUsd $PaidCapUsd
    $pre=Get-Content -LiteralPath $PreflightReceiptPath -Raw|ConvertFrom-Json
    if($pre.schemaVersion -cne 'saaia-planning-envelope-preflight.v1' -or $pre.passed -ne $true -or
       $pre.externalProviderUsed -ne $false -or $pre.profileSha256 -ine $proof.profileSha256 -or
       $pre.corpusManifestSha256 -ine $profile.corpus.manifestSha256 -or $pre.writerToolsObserved -ne $true){
        throw 'Paid execution requires a successful exact-profile loopback envelope preflight.'
    }
    if(-not [IO.Path]::IsPathFullyQualified($SecretStorePath) -or -not (Test-Path -LiteralPath $SecretStorePath -PathType Leaf)){
        throw 'An explicit existing DPAPI store is required after all authorization gates; no secret was read.'
    }
}
$fixture=[IO.Path]::GetFullPath($PreparedEnvironmentDirectory)
if(-not $fixture.StartsWith($output+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
    throw 'The explicit prepared environment must be owned by this fresh attempt directory.'
}
if(-not [IO.Path]::IsPathFullyQualified($PythonExecutable) -or -not (Test-Path -LiteralPath $PythonExecutable -PathType Leaf)){
    throw 'Provide the explicit trusted local Python executable.'
}
$backend=Join-Path $repo 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'
if((Get-FileHash -LiteralPath $backend).Hash -ine $profile.execution.backendAssemblySha256 -or
   (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $JobInspectorDll) 'SAAIA.Backend.dll')).Hash -ine $profile.execution.backendAssemblySha256){
    throw 'Backend and inspector must match the frozen planning assembly.'
}
$owner=Get-OwnedPaidEnvironment $fixture
$observed=Assert-OwnedPlanningCorpus -OutputDirectory $fixture -Manifest $manifest
[IO.Directory]::CreateDirectory($campaign)|Out-Null
$lock=$null;$key=$null;$plain=$null;$headers=$null;$paidUrl=$null;$userId=[Guid]::NewGuid().ToString('D');$job=$null
$marker=Join-Path $output 'planning-run.owner.json'
$markerStream=[IO.FileStream]::new($marker,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$markerStream.Dispose()
try{
    # This lock and the production durable ledger cover every attempt in the same envelope.
    $lock=[IO.FileStream]::new((Join-Path $campaign 'planning-attempt.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $proof.financialBefore=Get-PlanningEnvelopeLedgerSummary -Profile $profile
    if(-not $proof.financialBefore.withinAuthorizedCap -or $proof.financialBefore.totalBoundedCampaignCostUsd -ge [decimal]$profile.budget.authorizedUsd -or
       $proof.financialBefore.unsettledReservationCount -ne 0){throw 'The cumulative envelope is exhausted or has an unsettled prior reservation.'}
    $proof.corpusSqlReadback=Export-OwnedPlanningCorpusReadback -OutputDirectory $fixture
    if($profile.ContainsKey('realRag')){$proof.realRagAttestation=Assert-OwnedPlanningRealRag -Profile $profile -OutputDirectory $fixture -PythonExecutable $PythonExecutable}
    $proof.executed=$true;$proof.state='running'
    if($Preflight){
        $control=Join-Path $fixture 'runtime/planning-preflight-control.json'
        [IO.File]::WriteAllText($control,(@{caseId='planning-envelope';scenario=@{};preflightQuery='recette'}|ConvertTo-Json))
        $info=[Diagnostics.ProcessStartInfo]::new($PythonExecutable);$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
        foreach($arg in @('-X','utf8','-B',(Join-Path $PSScriptRoot 'planning_preflight.py'),'--port',[string]$profile.execution.preflightProviderPort,
            '--control',$control,'--capture',(Join-Path $fixture 'runtime/planning-wire-captures'))){$info.ArgumentList.Add($arg)}
        $sink=[Diagnostics.Process]::Start($info)
        $owner.processes+=@{name='advanced-fixture-http';pid=$sink.Id;path=$info.FileName;port=[int]$profile.execution.preflightProviderPort;startedAtUtc=$sink.StartTime.ToUniversalTime().ToString('o')}
        [IO.File]::WriteAllText((Join-Path $fixture 'environment.json'),($owner|ConvertTo-Json -Depth 20))
        $sinkDeadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
        do{
            try{$status=Invoke-RestMethod -Uri "http://127.0.0.1:$($profile.execution.preflightProviderPort)/fixture/status" -TimeoutSec 2;if($status.fixture){break}}catch{}
            if($sink.HasExited -or [DateTimeOffset]::UtcNow -gt $sinkDeadline){throw 'The owned loopback preflight sink did not become ready.'}
            Start-Sleep -Milliseconds 100
        }while($true)
        $paidUrl=Start-OwnedPaidBackend -OutputDirectory $fixture -RepositoryRoot $repo -Profile $profile -ApiKey '' `
            -PlanningPreflightBaseUrl "http://127.0.0.1:$($profile.execution.preflightProviderPort)/v1"
    }else{
        Add-Type -AssemblyName System.Security
        $secret=Get-Content -LiteralPath $SecretStorePath -Raw|ConvertFrom-Json
        $plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($secret.OpenAiApiKeyProtected),
            [Text.Encoding]::UTF8.GetBytes('SAAIA.Client.WinUI|CDC-v2.7'),[Security.Cryptography.DataProtectionScope]::CurrentUser)
        $key=[Text.Encoding]::UTF8.GetString($plain);$secret=$null
        if([string]::IsNullOrWhiteSpace($key)){throw 'The authorized protected key is empty.'}
        $proof.secretRead=$true
        $paidUrl=Start-OwnedPaidBackend -OutputDirectory $fixture -RepositoryRoot $repo -Profile $profile -ApiKey $key
        $proof.externalProviderUsed=$true
    }
    $private=Get-Content -LiteralPath (Join-Path $fixture 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
    $headers=@{'X-Api-Key'=$private.apiKey;'X-Admin-Key'=$private.apiKey;'X-SAAIA-Turn-Id'=('planning-'+[Guid]::NewGuid().ToString('N'))}
    $null=Invoke-RestMethod -Method Post -Uri ($paidUrl+'/admin/catalog/snapshot/refresh') -Headers $headers -TimeoutSec 20
    $session=Invoke-RestMethod -Method Post -Uri ($paidUrl+'/chat/sessions') -Headers $headers -ContentType 'application/json' `
        -Body (@{userId=$userId;title='Qualification planning isolée';clientUser='planning-campaign'}|ConvertTo-Json) -TimeoutSec 10
    $headers['X-SAAIA-Session-Id']=[string]$session.sessionId
    $submission=@{userId=$userId;sessionId=$session.sessionId;handoff=(New-PlanningHandoff -Profile $profile)}
    [IO.File]::WriteAllText((Join-Path $fixture 'runtime/planning-submission.private.json'),($submission|ConvertTo-Json -Depth 30))
    $job=Invoke-RestMethod -Method Post -Uri ($paidUrl+'/advanced-analysis/jobs') -Headers $headers -ContentType 'application/json' -Body ($submission|ConvertTo-Json -Depth 30) -TimeoutSec 15
    $proof.jobId=$job.jobId;$proof.userId=$userId
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds([int]$profile.execution.timeoutSeconds)
    do{
        Start-Sleep -Milliseconds 300
        $job=Invoke-RestMethod -Uri ($paidUrl+'/advanced-analysis/jobs/'+$job.jobId+'?userId='+$userId) -Headers $headers -TimeoutSec 10
        if($job.status -notin @('queued','running')){break}
        if([DateTimeOffset]::UtcNow -gt $deadline){throw 'Planning timeout; owned cancellation follows.'}
    }while($true)
    [IO.File]::WriteAllText((Join-Path $fixture 'runtime/planning-terminal.private.json'),($job|ConvertTo-Json -Depth 60))
    & (Get-Command dotnet).Source $JobInspectorDll inspect-job --owner (Join-Path $fixture 'environment.json') --job-id $job.jobId --output (Join-Path $fixture 'runtime/planning-sql-job.private.json') *> (Join-Path $output 'sql-inspection.log')
    if($LASTEXITCODE -ne 0){throw 'Read-only SQL job inspection failed.'}
    $proof.terminalStatus=$job.status;$proof.lastErrorCode=$job.lastErrorCode
    $proof.outcome=if($job.result){$job.result.outcome}else{$null}
    $proof.state=if($Preflight){'local_envelope_captured_requires_verification'}else{'terminal_requires_input_and_semantic_review'}
}catch{$proof.state='failed';$proof.failureType=$_.Exception.GetType().Name}
finally{
    if($paidUrl -and $headers){
        try{
            $active=Invoke-OwnedPaidSql -OutputDirectory $fixture -Query "SELECT coalesce(json_agg(job_id),'[]'::json) FROM advanced_analysis_jobs WHERE status IN ('queued','running');"|ConvertFrom-Json
            foreach($id in @($active)){$parsed=[Guid]::Parse([string]$id);$null=Invoke-RestMethod -Method Post -Uri ($paidUrl+'/advanced-analysis/jobs/'+$parsed+'/cancel?userId='+$userId) -Headers $headers -TimeoutSec 10}
        }catch{$proof.cleanup.errors+=@('owned_cancel_failed')}
    }
    try{
        $active=Invoke-OwnedPaidSql -OutputDirectory $fixture -Query "SELECT count(*) FROM advanced_analysis_jobs WHERE status IN ('queued','running');"
        $proof.cleanup.zeroActiveJobs=[int]$active -eq 0
        $quietDeadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
        do{
            $financial=Get-PlanningEnvelopeLedgerSummary -Profile $profile
            if($financial.unsettledReservationCount -eq 0){Start-Sleep -Milliseconds 1200;$after=Get-PlanningEnvelopeLedgerSummary -Profile $profile
                if($after.unsettledReservationCount -eq 0 -and $after.ledgerSha256 -eq $financial.ledgerSha256){$proof.cleanup.workerQuiescent=$true;break}}
            if([DateTimeOffset]::UtcNow -gt $quietDeadline){break};Start-Sleep -Milliseconds 250
        }while($true)
        $proof.financialAfter=Get-PlanningEnvelopeLedgerSummary -Profile $profile
        if($proof.financialBefore){
            $proof.currentAttemptLedgerRows=$proof.financialAfter.logicalTerminalLedgerRows-$proof.financialBefore.logicalTerminalLedgerRows
            $proof.currentAttemptBoundedCostUsd=$proof.financialAfter.totalBoundedCampaignCostUsd-$proof.financialBefore.totalBoundedCampaignCostUsd
        }
        if(-not $proof.financialAfter.withinAuthorizedCap -or $proof.financialAfter.observedModelMismatchCount -gt 0 -or $proof.financialAfter.httpAttemptPolicyViolations -gt 0){$proof.cleanup.errors+=@('financial_or_model_policy_failed')}
    }catch{$proof.cleanup.errors+=@('owned_postflight_failed')}
    try{Set-StrictMode -Off;& (Join-Path $repo 'tools/validation-winui/stop-environment.ps1') -OutputDirectory $fixture -Confirm:$false
        $proof.cleanup.ownedEnvironmentStopped=(Get-Content -LiteralPath (Join-Path $fixture 'environment.json') -Raw|ConvertFrom-Json).state -eq 'stopped'
    }catch{$proof.cleanup.errors+=@('owned_stop_failed')}
    if($plain){[Array]::Clear($plain,0,$plain.Length)};$plain=$null;$key=$null;$headers=$null;$private=$null
    if($lock){$lock.Dispose()}
    $proof.endedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    $proof.lifecycleComplete=($proof.cleanup.zeroActiveJobs -and $proof.cleanup.workerQuiescent -and $proof.cleanup.ownedEnvironmentStopped -and $proof.cleanup.errors.Count -eq 0)
    [IO.File]::WriteAllText((Join-Path $output 'planning-result.json'),($proof|ConvertTo-Json -Depth 30))
}
if(-not $proof.lifecycleComplete -or $proof.state -eq 'failed'){throw 'Planning attempt failed or lacks complete cleanup proof; inspect private evidence before any next attempt.'}
Write-Output 'One isolated planning attempt terminated; inspect exact private inputs and sources before interpreting the answer. No automatic retry.'
