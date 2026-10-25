#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][Guid]$JobId,
    [Parameter(Mandatory)][Guid]$SessionId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$OriginTraceId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$ResumeTraceId,
    [Parameter(Mandatory)][string]$BeforeSnapshot,
    [Parameter(Mandatory)][string]$DuringSnapshot,
    [Parameter(Mandatory)][string]$AfterSnapshot,
    [string]$ProofName='advanced-resume-proof.json'
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
$owner=Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw|ConvertFrom-Json
$isolation=Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw|ConvertFrom-Json
if($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output){throw 'Not an owned fixture.'}
if([IO.Path]::GetFileName($ProofName) -ne $ProofName){throw 'ProofName must be a filename.'}
$proofPath=Join-Path $output $ProofName
if(Test-Path -LiteralPath $proofPath){throw 'Resume proof exists; refusing overwrite.'}
$before=Get-Content -LiteralPath $BeforeSnapshot -Raw|ConvertFrom-Json
$during=Get-Content -LiteralPath $DuringSnapshot -Raw|ConvertFrom-Json
$after=Get-Content -LiteralPath $AfterSnapshot -Raw|ConvertFrom-Json
$userId=([Guid]$isolation.userId).ToString('D')
foreach($snapshot in @($before,$during,$after)){if($snapshot.userId -ne $userId){throw 'Snapshot user mismatch.'}}
function Get-Session([object]$Snapshot,[string]$Id){
    $rows=@($Snapshot.sessions|Where-Object sessionId -eq $Id)
    if($rows.Count -ne 1){throw 'Required session absent from snapshot.'}
    return $rows[0]
}
function Get-SessionDigest([object]$Session){
    $lines=@($Session.messages|ForEach-Object{ @($_.messageId,$_.role,$_.content.sha256,$_.sources.sha256,$_.statusNote.sha256,$_.progressText.sha256,$_.trackingMetadata.sha256)-join '|' })-join "`n"
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lines))).ToLowerInvariant()
}
$originBefore=Get-Session $before $SessionId.ToString('D')
$originDuring=Get-Session $during $SessionId.ToString('D')
$originAfter=Get-Session $after $SessionId.ToString('D')
$beforeMessage=@($originBefore.messages|Where-Object advancedJobId -eq $JobId.ToString('D'))
$duringMessage=@($originDuring.messages|Where-Object advancedJobId -eq $JobId.ToString('D'))
$afterMessage=@($originAfter.messages|Where-Object advancedJobId -eq $JobId.ToString('D'))
if($beforeMessage.Count -ne 1 -or $duringMessage.Count -ne 1 -or $afterMessage.Count -ne 1){throw 'Snapshots do not identify one message for the job.'}
$messageId=([Guid]$beforeMessage[0].messageId).ToString('D')
$otherSessions=[Collections.Generic.List[object]]::new()
foreach($session in $before.sessions|Where-Object sessionId -ne $SessionId.ToString('D')){
    $sameDuring=Get-Session $during $session.sessionId
    $sameAfter=Get-Session $after $session.sessionId
    $first=Get-SessionDigest $session
    $middle=Get-SessionDigest $sameDuring
    $last=Get-SessionDigest $sameAfter
    $otherSessions.Add(@{sessionId=$session.sessionId;beforeSha256=$first;duringSha256=$middle;afterSha256=$last;unchanged=($first -eq $middle -and $middle -eq $last);messageCount=$session.messageCount})
}
$probeEvents=@(Get-ChildItem -LiteralPath (Join-Path $owner.clientDataRoot 'logs') -Filter 'client_startup.log*' -File|ForEach-Object{
    foreach($line in [IO.File]::ReadLines($_.FullName)){
        $offset=$line.IndexOf('CLIENT_PROBE ',[StringComparison]::Ordinal)
        if($offset -ge 0){$entry=ConvertFrom-Json $line.Substring($offset+13);if($entry.traceId -in @($OriginTraceId,$ResumeTraceId)){$entry}}
    }
})
$originProbes=@($probeEvents|Where-Object traceId -eq $OriginTraceId)
$resumeProbes=@($probeEvents|Where-Object traceId -eq $ResumeTraceId)
$resumeEnd=@($resumeProbes|Where-Object operation -eq 'turn.end')
$resumePoll=@($resumeProbes|Where-Object operation -eq 'advanced.poll.response')
$private=Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
$headers=@{'X-Api-Key'=$private.apiKey}
$apiJob=Invoke-RestMethod -Uri ($owner.backendUrl+'/advanced-analysis/jobs/'+$JobId.ToString('D')+'?userId='+[Uri]::EscapeDataString($userId)) -Headers $headers -TimeoutSec 20
$apiMessages=Invoke-RestMethod -Uri ($owner.backendUrl+'/chat/sessions/'+$SessionId.ToString('D')+'/messages?userId='+[Uri]::EscapeDataString($userId)) -Headers $headers -TimeoutSec 20
$apiMessage=@($apiMessages|Where-Object messageId -eq $messageId)
if($apiMessage.Count -ne 1){throw 'Durable resumed message not found by API.'}
$sources=if($apiMessage[0].sources -is [string]){ConvertFrom-Json $apiMessage[0].sources}else{$apiMessage[0].sources}
$beforeTime=([DateTimeOffset]$before.startedAtUtc).ToUniversalTime().ToString('o')
$afterTime=([DateTimeOffset]$after.finishedAtUtc).ToUniversalTime().ToString('o')
$sql=@"
BEGIN READ ONLY;
SELECT json_build_object(
 'dataDirectory',current_setting('data_directory'),
 'jobsAtBefore',(SELECT count(*) FROM advanced_analysis_jobs WHERE user_id='$userId' AND created_at<='$beforeTime'::timestamptz),
 'jobsAtAfter',(SELECT count(*) FROM advanced_analysis_jobs WHERE user_id='$userId' AND created_at<='$afterTime'::timestamptz),
 'jobsCreatedBetween',(SELECT count(*) FROM advanced_analysis_jobs WHERE user_id='$userId' AND created_at>'$beforeTime'::timestamptz AND created_at<='$afterTime'::timestamptz),
 'job',(SELECT json_build_object('jobId',job_id,'sessionId',session_id,'status',status,'revision',revision,'attemptCount',attempt_count,'parentTraceparent',parent_traceparent,'turnId',diagnostic_turn_id,'createdAt',created_at,'finishedAt',finished_at) FROM advanced_analysis_jobs WHERE user_id='$userId' AND job_id='$JobId'),
 'messagesForJob',(SELECT count(*) FROM chat_messages m JOIN chat_sessions s ON s.tenant_id=m.tenant_id AND s.session_id=m.session_id WHERE s.user_id='$userId' AND m.sources_json#>>'{advancedAnalysis,jobId}'='$JobId'),
 'message',(SELECT json_build_object('messageId',m.message_id,'sessionId',m.session_id,'contentSha256',encode(sha256(convert_to(m.content,'UTF8')),'hex'),'originTraceId',m.sources_json#>>'{advancedAnalysis,originTraceId}','traceId',m.sources_json#>>'{advancedAnalysis,traceId}') FROM chat_messages m JOIN chat_sessions s ON s.tenant_id=m.tenant_id AND s.session_id=m.session_id WHERE s.user_id='$userId' AND m.message_id='$messageId'),
 'transitions',(SELECT json_agg(json_build_object('eventName',event_name,'status',status,'attemptCount',attempt_count,'occurredAt',occurred_at,'parentTraceparent',parent_traceparent,'turnId',turn_id,'sessionId',session_id,'clientSessionMismatch',client_session_id_mismatch) ORDER BY event_id) FROM advanced_analysis_job_events WHERE job_id='$JobId')
);
COMMIT;
"@
$sqlPath=Join-Path $output 'runtime/advanced-resume-verification-readonly.sql'
[IO.File]::WriteAllText($sqlPath,$sql)
$previous=[Environment]::GetEnvironmentVariable('PGPASSWORD','Process')
try{
    [Environment]::SetEnvironmentVariable('PGPASSWORD',$private.pgPassword,'Process')
    $lines=@(& (Join-Path $owner.postgresBinDirectory 'psql.exe') -w -h 127.0.0.1 -p $owner.pgPort -U saaia_winui -d postgres -v ON_ERROR_STOP=1 -At -f $sqlPath 2>&1)
    if($LASTEXITCODE -ne 0){throw 'Owned read-only resume SQL check failed.'}
}finally{[Environment]::SetEnvironmentVariable('PGPASSWORD',$previous,'Process')}
$json=@($lines|Where-Object{([string]$_).TrimStart().StartsWith('{')})
if($json.Count -ne 1){throw 'SQL resume check returned an unexpected shape.'}
$durable=ConvertFrom-Json ([string]$json[0])
$checks=@(
    @{name='same_message_id_across_restart';passed=$duringMessage[0].messageId -eq $messageId -and $afterMessage[0].messageId -eq $messageId},
    @{name='original_session_message_count_unchanged';passed=$originBefore.messageCount -eq $originDuring.messageCount -and $originDuring.messageCount -eq $originAfter.messageCount},
    @{name='one_job_reference_in_original_message';passed=$durable.messagesForJob -eq 1},
    @{name='job_count_unchanged_no_new_job_during_resume';passed=$durable.jobsAtBefore -eq $durable.jobsAtAfter -and $durable.jobsCreatedBetween -eq 0},
    @{name='all_common_other_sessions_byte_fingerprints_unchanged';passed=@($otherSessions|Where-Object{-not $_.unchanged}).Count -eq 0},
    @{name='same_job_finishes_in_api_and_sql';passed=$apiJob.jobId -eq $JobId.ToString('D') -and $apiJob.status -eq 'succeeded' -and $durable.job.status -eq 'succeeded'},
    @{name='persisted_trace_chain_retains_origin_and_resume';passed=$sources.advancedAnalysis.originTraceId -eq $OriginTraceId -and $sources.advancedAnalysis.traceId -eq $ResumeTraceId -and $durable.message.originTraceId -eq $OriginTraceId -and $durable.message.traceId -eq $ResumeTraceId},
    @{name='sql_job_parent_matches_original_trace';passed=$durable.job.parentTraceparent -like ('00-'+$OriginTraceId+'-*')},
    @{name='resume_polls_without_create_or_local_model';passed=$resumePoll.Count -gt 0 -and @($resumeProbes|Where-Object operation -eq 'advanced.create.attempt').Count -eq 0 -and @($resumeProbes|Where-Object operation -eq 'model.call').Count -eq 0},
    @{name='resume_has_single_successful_terminal';passed=$resumeEnd.Count -eq 1 -and $resumeEnd[0].outcome -eq 'success'},
    @{name='sql_payload_hash_matches_after_snapshot';passed=$durable.message.contentSha256 -eq $afterMessage[0].content.sha256},
    @{name='owned_database_directory_verified';passed=[IO.Path]::GetFullPath($durable.dataDirectory) -ieq [IO.Path]::GetFullPath($owner.postgresDataDirectory)}
)
$passed=@($checks|Where-Object{-not $_.passed}).Count -eq 0
@{schemaVersion='saaia-winui-advanced-resume-proof.v1';checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');passed=$passed;readOnly=$true;
    jobId=$JobId.ToString('D');sessionId=$SessionId.ToString('D');messageId=$messageId;originTraceId=$OriginTraceId;resumeTraceId=$ResumeTraceId;
    snapshots=@(@{phase='before';sha256=(Get-FileHash -LiteralPath $BeforeSnapshot).Hash;status=$beforeMessage[0].advancedStatus;revision=$beforeMessage[0].advancedRevision},@{phase='during';sha256=(Get-FileHash -LiteralPath $DuringSnapshot).Hash;status=$duringMessage[0].advancedStatus;revision=$duringMessage[0].advancedRevision},@{phase='after';sha256=(Get-FileHash -LiteralPath $AfterSnapshot).Hash;status=$afterMessage[0].advancedStatus;revision=$afterMessage[0].advancedRevision});
    originSessionMessages=@{before=$originBefore.messageCount;during=$originDuring.messageCount;after=$originAfter.messageCount};
    jobs=@{before=$durable.jobsAtBefore;after=$durable.jobsAtAfter;createdBetween=$durable.jobsCreatedBetween;attemptCount=$durable.job.attemptCount;status=$durable.job.status;revision=$durable.job.revision};
    otherSessions=$otherSessions.ToArray();newSessionsOutsideOriginal=@($after.sessions.sessionId|Where-Object{$_ -notin $before.sessions.sessionId});
    probes=@{originEventCount=$originProbes.Count;originTerminalCount=@($originProbes|Where-Object operation -eq 'turn.end').Count;originInterruptedByProcessClose=$true;
        resumeEventCount=$resumeProbes.Count;resumePollResponses=$resumePoll.Count;resumeTerminalCount=$resumeEnd.Count;resumeLocalModelCalls=0;resumeCreateCalls=0;resumeElapsedMilliseconds=$resumeEnd[0].elapsedMilliseconds;
        configurationOriginTraceCheckedInSupport=$false};
    sqlTransitions=$durable.transitions;checks=$checks;payloadContentPublished=$false;productStatus='TESTE_NON_APPROUVE';
    limits=@('The original process closed during polling and its trace has no terminal event; no replacement terminal was fabricated.',
      'The persisted advancedAnalysis metadata and SQL parent trace establish the origin/resume chain. Journal Configuration.originTraceId needs a post-resume UI support export for direct inspection.',
      'Other new sessions during the observation window are listed separately; the original assistant message is patched in place, not duplicated.',
      'The provider is the deterministic internal fixture. This proves durable lifecycle and isolation, not model quality or the real meal-planning task.')
}|ConvertTo-Json -Depth 18|Set-Content -LiteralPath $proofPath -Encoding utf8
if(-not $passed){throw 'Advanced resume proof failed one or more checks.'}
Write-Output 'Advanced resume proof passed: same job/message, no new job, other sessions unchanged, persisted origin/resume trace chain.'
