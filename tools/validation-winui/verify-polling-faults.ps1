#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][Guid]$JobId,
    [Parameter(Mandatory)][Guid]$SessionId,
    [Parameter(Mandatory)][Guid]$AssistantMessageId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$TraceId,
    [Parameter(Mandatory)][string]$BeforeSnapshot,
    [Parameter(Mandatory)][string]$AfterSnapshot,
    [string]$ProofName='advanced-polling-faults-proof.json'
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
$owner=Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw|ConvertFrom-Json
$isolation=Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw|ConvertFrom-Json
if($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output -or [int]$owner.pgPort -eq 5432){throw 'Not an owned isolated fixture.'}
if([IO.Path]::GetFileName($ProofName) -ne $ProofName){throw 'ProofName must be a filename.'}
$proofPath=Join-Path $output $ProofName
if(Test-Path -LiteralPath $proofPath){throw 'Proof exists; refusing overwrite.'}
$before=Get-Content -LiteralPath $BeforeSnapshot -Raw|ConvertFrom-Json
$after=Get-Content -LiteralPath $AfterSnapshot -Raw|ConvertFrom-Json
$userId=([Guid]$isolation.userId).ToString('D')
if($before.userId -ne $userId -or $after.userId -ne $userId -or -not $before.completeWithinApiLimits -or -not $after.completeWithinApiLimits){throw 'Snapshots must completely describe the owned user.'}
function Session-Digest([object]$Session){
    $text=@($Session.messages|ForEach-Object{@($_.messageId,$_.role,$_.content.sha256,$_.sources.sha256,$_.statusNote.sha256,$_.progressText.sha256,$_.trackingMetadata.sha256)-join '|'})-join "`n"
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
}
$unchanged=@(foreach($session in $before.sessions){
    $current=@($after.sessions|Where-Object sessionId -eq $session.sessionId)
    $first=Session-Digest $session
    $last=if($current.Count -eq 1){Session-Digest $current[0]}else{$null}
    @{sessionId=$session.sessionId;beforeSha256=$first;afterSha256=$last;unchanged=($first -eq $last)}
})
$newSessions=@($after.sessions|Where-Object sessionId -notin @($before.sessions.sessionId))
$expected=@($after.sessions|Where-Object sessionId -eq $SessionId.ToString('D'))
$events=@([IO.File]::ReadLines((Join-Path $output 'advanced-poll-events-w24.jsonl'))|ForEach-Object{ConvertFrom-Json $_})
$targetEvents=@($events|Where-Object{ $_.jobId -eq $JobId.ToString('D') -and $_.phase -eq 'completed' })
$injections=@($targetEvents|Where-Object mode -ne 'passthrough')
$postCreates=@($events|Where-Object{ $_.method -eq 'POST' -and $_.path -eq '/advanced-analysis/jobs' -and $_.phase -eq 'completed' -and $_.traceparent -like ('00-'+$TraceId+'-*') })
$probes=@(foreach($file in Get-ChildItem -LiteralPath (Join-Path $output 'runtime/client-profiles/w24/logs') -Filter 'client_startup.log*' -File){
    foreach($line in [IO.File]::ReadLines($file.FullName)){
        $index=$line.IndexOf('CLIENT_PROBE ',[StringComparison]::Ordinal)
        if($index -ge 0){$probe=ConvertFrom-Json $line.Substring($index+13);if($probe.traceId -eq $TraceId){$probe}}
    }
})
$turns=@($probes|Group-Object turnId|ForEach-Object{@{turnId=$_.Name;begins=@($_.Group|Where-Object operation -eq 'turn.begin').Count;ends=@($_.Group|Where-Object operation -eq 'turn.end').Count;createAttempts=@($_.Group|Where-Object operation -eq 'advanced.create.attempt').Count}})
$private=Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
$headers=@{'X-Api-Key'=$private.apiKey}
$apiJob=Invoke-RestMethod -Uri ($owner.backendUrl+'/advanced-analysis/jobs/'+$JobId.ToString('D')+'?userId='+$userId) -Headers $headers -TimeoutSec 20
$baselineUtc=([DateTimeOffset]$before.finishedAtUtc).ToUniversalTime().ToString('o')
$sql=@"
BEGIN READ ONLY;
SELECT json_build_object(
 'dataDirectory',current_setting('data_directory'),
 'jobCountAtBaseline',(SELECT count(*) FROM advanced_analysis_jobs WHERE user_id='$userId' AND created_at<='$baselineUtc'::timestamptz),
 'jobCountNow',(SELECT count(*) FROM advanced_analysis_jobs WHERE user_id='$userId'),
 'jobsCreatedSinceBaseline',(SELECT json_agg(job_id) FROM advanced_analysis_jobs WHERE user_id='$userId' AND created_at>'$baselineUtc'::timestamptz),
 'jobCountForSession',(SELECT count(*) FROM advanced_analysis_jobs WHERE user_id='$userId' AND session_id='$SessionId'),
 'job',(SELECT json_build_object('jobId',job_id,'sessionId',session_id,'status',status,'revision',revision,'attemptCount',attempt_count,'parentTraceparent',parent_traceparent,'createdAt',created_at,'finishedAt',finished_at) FROM advanced_analysis_jobs WHERE user_id='$userId' AND job_id='$JobId'),
 'messagesForJob',(SELECT count(*) FROM chat_messages m JOIN chat_sessions s ON s.tenant_id=m.tenant_id AND s.session_id=m.session_id WHERE s.user_id='$userId' AND m.sources_json#>>'{advancedAnalysis,jobId}'='$JobId'),
 'wrongIdentityInAnyUserSource',(SELECT count(*) FROM chat_messages m JOIN chat_sessions s ON s.tenant_id=m.tenant_id AND s.session_id=m.session_id WHERE s.user_id='$userId' AND position('00000000-0000-4000-8000-000000000024' IN m.sources_json::text)>0),
 'message',(SELECT json_build_object('messageId',m.message_id,'sessionId',m.session_id,'contentSha256',encode(sha256(convert_to(m.content,'UTF8')),'hex'),'sourceSessionId',m.sources_json#>>'{advancedAnalysis,sessionId}','sourceJobId',m.sources_json#>>'{advancedAnalysis,jobId}','sourceRevision',m.sources_json#>>'{advancedAnalysis,revision}','sourceStatus',m.sources_json#>>'{advancedAnalysis,status}') FROM chat_messages m JOIN chat_sessions s ON s.tenant_id=m.tenant_id AND s.session_id=m.session_id WHERE s.user_id='$userId' AND m.message_id='$AssistantMessageId'),
 'transitions',(SELECT json_agg(json_build_object('eventName',event_name,'status',status,'attemptCount',attempt_count,'sessionId',session_id,'clientSessionMismatch',client_session_id_mismatch,'atUtc',occurred_at) ORDER BY event_id) FROM advanced_analysis_job_events WHERE job_id='$JobId')
);
COMMIT;
"@
$sqlPath=Join-Path $output 'runtime/polling-fault-verification-readonly.sql'
[IO.File]::WriteAllText($sqlPath,$sql)
$previous=[Environment]::GetEnvironmentVariable('PGPASSWORD','Process')
try{
    [Environment]::SetEnvironmentVariable('PGPASSWORD',$private.pgPassword,'Process')
    $lines=@(& (Join-Path $owner.postgresBinDirectory 'psql.exe') -w -h 127.0.0.1 -p $owner.pgPort -U saaia_winui -d postgres -v ON_ERROR_STOP=1 -At -f $sqlPath 2>&1)
    if($LASTEXITCODE -ne 0){throw 'Owned read-only polling SQL check failed.'}
}finally{[Environment]::SetEnvironmentVariable('PGPASSWORD',$previous,'Process')}
$json=@($lines|Where-Object{([string]$_).TrimStart().StartsWith('{')})
if($json.Count -ne 1){throw 'Unexpected read-only SQL result shape.'}
$durable=ConvertFrom-Json ([string]$json[0])
$pollerPath=Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'client/SAAIA.Client.Core/AdvancedAnalysis/AdvancedAnalysisJobPoller.cs'
$poller=[IO.File]::ReadAllText($pollerPath)
$validateIndex=$poller.IndexOf('var invalid = ValidateSnapshot(')
$assignmentIndex=$poller.IndexOf('job = next;')
$callbackIndex=$poller.IndexOf('await onSnapshot(job, cancellationToken)')
$checks=@(
    @{name='owned_data_directory_verified';passed=[IO.Path]::GetFullPath($durable.dataDirectory) -ieq [IO.Path]::GetFullPath($owner.postgresDataDirectory)},
    @{name='one_real_job_created_since_baseline';passed=$postCreates.Count -eq 1 -and $durable.jobCountNow -eq $durable.jobCountAtBaseline+1 -and @($durable.jobsCreatedSinceBaseline).Count -eq 1 -and $durable.jobsCreatedSinceBaseline[0] -eq $JobId.ToString('D') -and $durable.jobCountForSession -eq 1},
    @{name='one_assistant_reference_same_session';passed=$durable.messagesForJob -eq 1 -and $durable.message.messageId -eq $AssistantMessageId.ToString('D') -and $durable.message.sessionId -eq $SessionId.ToString('D') -and $durable.message.sourceSessionId -eq $SessionId.ToString('D')},
    @{name='other_sessions_hashes_unchanged';passed=@($unchanged|Where-Object unchanged -ne $true).Count -eq 0},
    @{name='only_expected_session_added_with_two_messages';passed=$newSessions.Count -eq 1 -and $newSessions[0].sessionId -eq $SessionId.ToString('D') -and $expected.Count -eq 1 -and $expected[0].messageCount -eq 2},
    @{name='two_503_responses_and_two_client_retries';passed=@($injections|Where-Object{ $_.mode -eq 'http503' -and $_.status -eq 503 }).Count -eq 2 -and @($probes|Where-Object operation -eq 'advanced.poll.retry').Count -eq 2},
    @{name='regression_injected_and_rejected_at_previous_revision';passed=@($injections|Where-Object{ $_.mode -eq 'regressive_revision' -and $_.injectedRevision -lt $_.upstreamRevision }).Count -eq 1 -and @($probes|Where-Object{ $_.operation -eq 'advanced.poll.validation' -and $_.reason -eq 'job_revision_regressed' -and $_.outcome -eq 'failed' }).Count -eq 1},
    @{name='wrong_session_injected_and_rejected';passed=@($injections|Where-Object mode -eq 'session_mismatch').Count -eq 1 -and @($probes|Where-Object{ $_.operation -eq 'advanced.poll.validation' -and $_.reason -eq 'session_identity_changed' -and $_.outcome -eq 'failed' }).Count -eq 1 -and $durable.wrongIdentityInAnyUserSource -eq 0},
    @{name='invalid_snapshot_checked_before_state_and_persistence_callback';passed=$validateIndex -ge 0 -and $assignmentIndex -gt $validateIndex -and $callbackIndex -gt $assignmentIndex},
    @{name='real_terminal_revision_and_identity_persisted';passed=$apiJob.status -eq 'succeeded' -and $apiJob.revision -eq $durable.job.revision -and $durable.message.sourceRevision -eq [string]$durable.job.revision -and $durable.message.sourceStatus -eq 'succeeded' -and $durable.job.sessionId -eq $SessionId.ToString('D')},
    @{name='all_target_wire_responses_correlated';passed=$targetEvents.Count -gt 0 -and @($targetEvents|Where-Object{ $_.traceparent -notlike ('00-'+$TraceId+'-*') -or -not $_.turnId }).Count -eq 0},
    @{name='both_turns_have_one_begin_and_end';passed=$turns.Count -eq 2 -and @($turns|Where-Object{ $_.begins -ne 1 -or $_.ends -ne 1 }).Count -eq 0}
)
$passed=@($checks|Where-Object passed -ne $true).Count -eq 0
$proof=[ordered]@{schemaVersion='saaia-winui-polling-fault-proof.v1';capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');status=if($passed){'MECHANICAL_POLLING_FAULTS_VALIDATED'}else{'REJECTED_CHECK_FAILURE'};readOnly=$true;
    jobId=$JobId.ToString('D');sessionId=$SessionId.ToString('D');assistantMessageId=$AssistantMessageId.ToString('D');traceId=$TraceId;
    baselineSha256=(Get-FileHash -LiteralPath $BeforeSnapshot).Hash;afterSnapshotSha256=(Get-FileHash -LiteralPath $AfterSnapshot).Hash;
    sourcePollerSha256=(Get-FileHash -LiteralPath $pollerPath).Hash;proxySourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'advanced_poll_proxy.py')).Hash;
    jobs=@{before=$durable.jobCountAtBaseline;after=$durable.jobCountNow;forTargetSession=$durable.jobCountForSession;targetReferences=$durable.messagesForJob;attemptCount=$durable.job.attemptCount;terminalRevision=$durable.job.revision;terminalStatus=$durable.job.status};
    controlledResponses=$injections;turns=$turns;otherSessions=$unchanged;durableTransitions=$durable.transitions;checks=$checks;
    limits=@('Injected GET snapshots test transport/identity handling; the real worker job and final status come from the owned production backend and deterministic provider fixture.',
        'Current SQL and message hashes establish the final durable state. Invalid snapshots never reach onSnapshot under the inspected poller control flow; no independent statement-by-statement historical SQL audit is claimed.',
        'A turn.end success records completion of that client workflow, while invalid advanced.poll.validation/result.validation events remain explicit rejections.',
        'No visual UI claim or semantic meal-planning acceptance is made by this read-only verifier.')}
[IO.File]::WriteAllText($proofPath,($proof|ConvertTo-Json -Depth 15))
Write-Output ('Polling fault proof '+$proof.status+'; checks='+$checks.Count+'; jobs='+$durable.jobCountAtBaseline+'->'+$durable.jobCountNow+'; otherSessions='+$unchanged.Count)
if(-not $passed){throw 'Polling fault proof checks failed; rejected evidence preserved.'}
