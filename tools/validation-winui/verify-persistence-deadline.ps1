#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$TraceId,
    [Parameter(Mandatory)][string]$SnapshotPath,
    [string]$ProxyEventsName = 'storage-proxy-events-ui-faults-v2.jsonl',
    [string]$ProofName = 'persistence-deadline-proof.json'
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$owner = Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw | ConvertFrom-Json
$isolation = Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output) { throw 'Not an owned fixture.' }
foreach ($name in @($ProofName,$ProxyEventsName)) { if ([IO.Path]::GetFileName($name) -ne $name) { throw 'Evidence names must be filenames.' } }
$proofPath = Join-Path $output $ProofName
if (Test-Path -LiteralPath $proofPath) { throw 'Deadline evidence already exists; refusing overwrite.' }
$proxyPath = Join-Path $output $ProxyEventsName
$proxy = @([IO.File]::ReadLines($proxyPath) | Where-Object { $_.Trim() } | ForEach-Object { ConvertFrom-Json $_ })
$requests = @($proxy | Where-Object { $_.traceparent -like ('00-' + $TraceId + '-*') -and $_.method -eq 'POST' -and $_.userMessageMatched -eq $true -and -not $_.phase })
if ($requests.Count -ne 1) { throw 'Deadline scenario must have exactly one correlated user POST.' }
$request = $requests[0]
$sessionId = [Guid]$request.sessionId
$userId = [Guid]$isolation.userId
$probeEvents = [Collections.Generic.List[object]]::new()
$logFiles = @(Get-ChildItem -LiteralPath (Join-Path $owner.clientDataRoot 'logs') -Filter 'client_startup.log*' -File)
foreach ($file in $logFiles) {
    foreach ($line in [IO.File]::ReadLines($file.FullName)) {
        $start = $line.IndexOf('CLIENT_PROBE ', [StringComparison]::Ordinal)
        if ($start -ge 0) {
            $entry = ConvertFrom-Json $line.Substring($start + 13)
            if ($entry.traceId -eq $TraceId) { $probeEvents.Add($entry) }
        }
    }
}
$events = @($probeEvents | Sort-Object sequence)
$terminals = @($events | Where-Object operation -eq 'turn.end')
$persistence = @($events | Where-Object operation -eq 'chat.user.persistence')
$modelCalls = @($events | Where-Object { $_.operation -match '^(model|llm)\.' })
$snapshot = Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json
if ($snapshot.userId -ne $userId.ToString('D')) { throw 'Snapshot describes another user.' }
$sessionSnapshot = @($snapshot.sessions | Where-Object sessionId -eq $sessionId.ToString('D'))
if ($sessionSnapshot.Count -ne 1) { throw 'Snapshot is missing the traced session.' }
$postAt = [DateTimeOffset]$request.atUtc
$delayedUserMessages = @($sessionSnapshot[0].messages | Where-Object { $_.role -eq 'user' -and ([DateTimeOffset]$_.createdAt) -ge $postAt })
if ($delayedUserMessages.Count -ne 1) { throw 'Snapshot does not isolate exactly one delayed user message.' }
$message = $delayedUserMessages[0]
if ($message.content.sha256 -notmatch '^[0-9a-f]{64}$') { throw 'Snapshot content fingerprint is invalid.' }
$contentHash = [string]$message.content.sha256
$messageId = [Guid]$message.messageId
$private = Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw | ConvertFrom-Json
$sqlPath = Join-Path $output 'runtime/deadline-verification-readonly.sql'
$query = @"
BEGIN READ ONLY;
SELECT json_build_object(
 'dataDirectory',current_setting('data_directory'),
 'matchingContentRows',(SELECT count(*) FROM chat_messages m JOIN chat_sessions s ON s.tenant_id=m.tenant_id AND s.session_id=m.session_id WHERE s.user_id='$userId' AND encode(sha256(convert_to(m.content,'UTF8')),'hex')='$contentHash'),
 'message',(SELECT json_build_object('messageId',m.message_id,'sessionId',m.session_id,'role',m.role,'createdAt',m.created_at,'contentSha256',encode(sha256(convert_to(m.content,'UTF8')),'hex'),'characters',char_length(m.content)) FROM chat_messages m JOIN chat_sessions s ON s.tenant_id=m.tenant_id AND s.session_id=m.session_id WHERE s.user_id='$userId' AND m.session_id='$sessionId' AND m.message_id='$messageId')
);
COMMIT;
"@
[IO.File]::WriteAllText($sqlPath, $query)
$previousPassword = [Environment]::GetEnvironmentVariable('PGPASSWORD','Process')
try {
    [Environment]::SetEnvironmentVariable('PGPASSWORD',$private.pgPassword,'Process')
    $lines = @(& (Join-Path $owner.postgresBinDirectory 'psql.exe') -w -h 127.0.0.1 -p $owner.pgPort -U saaia_winui -d postgres -v ON_ERROR_STOP=1 -At -f $sqlPath 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'Owned read-only PostgreSQL verification failed; no SQL result was published.' }
} finally { [Environment]::SetEnvironmentVariable('PGPASSWORD',$previousPassword,'Process') }
$json = @($lines | Where-Object { ([string]$_).TrimStart().StartsWith('{') })
if ($json.Count -ne 1) { throw 'Read-only PostgreSQL did not return one verification object.' }
$durable = ConvertFrom-Json ([string]$json[0])
$correctDataDirectory = [IO.Path]::GetFullPath($durable.dataDirectory) -ieq [IO.Path]::GetFullPath($owner.postgresDataDirectory)
$duplicatePostCount = @($proxy | Where-Object { $_.method -eq 'POST' -and -not $_.phase -and $_.requestSha256 -eq $request.requestSha256 }).Count
$followingAssistantCount = @($sessionSnapshot[0].messages | Where-Object { $_.role -eq 'assistant' -and $_.ordinal -gt $message.ordinal }).Count
$checks = @(
    @{ name='wire_trace_turn_session_match'; passed=@($events | Where-Object { $_.turnId -ne $request.turnId -or $_.sessionId -ne $sessionId.ToString('D') }).Count -eq 0 -and $events.Count -gt 0 },
    @{ name='confirmation_timeout_observed'; passed=$persistence.Count -eq 1 -and $persistence[0].reason -eq 'confirmation_timeout' -and $persistence[0].measurements.confirmationTimeoutMs -eq 30000 },
    @{ name='single_terminal_before_late_commit'; passed=$terminals.Count -eq 1 -and $terminals[0].reason -eq 'user_message_persistence_unconfirmed' -and $terminals[0].elapsedMilliseconds -ge 30000 -and $terminals[0].elapsedMilliseconds -lt 35000 },
    @{ name='no_model_started_for_unconfirmed_turn'; passed=$modelCalls.Count -eq 0 },
    @{ name='one_correlated_post_and_no_identical_payload_retry'; passed=$requests.Count -eq 1 -and $duplicatePostCount -eq 1 },
    @{ name='owned_cluster_checked_readonly'; passed=$correctDataDirectory },
    @{ name='sql_row_matches_snapshot_hash_and_message_identity'; passed=$durable.message.messageId -eq $messageId.ToString('D') -and $durable.message.contentSha256 -eq $contentHash -and $durable.message.role -eq 'user' },
    @{ name='no_duplicate_content_row_at_sql_observation'; passed=$durable.matchingContentRows -eq 1 },
    @{ name='no_later_assistant_in_after_delay_snapshot'; passed=$followingAssistantCount -eq 0 },
    @{ name='snapshot_taken_after_late_commit'; passed=([DateTimeOffset]$snapshot.startedAtUtc) -gt ([DateTimeOffset]$message.createdAt) }
)
$passed = @($checks | Where-Object { -not $_.passed }).Count -eq 0
$proof = [ordered]@{
    schemaVersion='saaia-winui-persistence-deadline-proof.v1'; checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); passed=$passed; readOnly=$true;
    traceId=$TraceId; turnId=$request.turnId; sessionId=$sessionId.ToString('D'); messageId=$messageId.ToString('D');
    client=@{ confirmationTimeoutMs=30000; persistenceElapsedMilliseconds=$persistence[0].elapsedMilliseconds; terminalElapsedMilliseconds=$terminals[0].elapsedMilliseconds; modelProbeCount=$modelCalls.Count; outcome=$terminals[0].outcome; reason=$terminals[0].reason };
    wire=@{ postAtUtc=$postAt.ToUniversalTime().ToString('o'); injectedDelaySeconds=$request.delaySeconds; requestBodySha256=$request.requestSha256; correlatedPostCount=$requests.Count; identicalBodyPostCount=$duplicatePostCount; traceparent=$request.traceparent };
    durable=@{ createdAtUtc=([DateTimeOffset]$durable.message.createdAt).ToUniversalTime().ToString('o'); contentSha256=$contentHash; characters=$message.content.characters; matchingContentRows=$durable.matchingContentRows; databaseDirectoryVerified=$correctDataDirectory };
    snapshot=@{ sha256=(Get-FileHash -LiteralPath $SnapshotPath -Algorithm SHA256).Hash; totalMessageCount=$snapshot.totalMessageCount; sessionMessageCount=$sessionSnapshot[0].messageCount; followingAssistantCount=$followingAssistantCount };
    extractedProbeEventsSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($events|ConvertTo-Json -Compress -Depth 20))));
    checks=$checks; limits=@('Wire body hash and text hash represent different byte sequences; their association is established through the unique traced session POST, late commit timing, message ID and SQL/snapshot identity, not equality of those hashes.',
      'No automatic retry was observed in this scenario. This is not an idempotency guarantee for a later explicit manual resend.',
      'No model probe appeared in the traced turn; unrelated turns are outside this assertion.', 'Visual warning/reload observations are supplied separately by the UI operator.');
    productStatus='TESTE_NON_APPROUVE'
}
[IO.File]::WriteAllText($proofPath,($proof|ConvertTo-Json -Depth 16))
if (-not $passed) { throw 'Persistence deadline evidence did not satisfy all checks.' }
Write-Output 'Persistence deadline evidence validated: one wire POST, timeout30s, one later durable row, no automatic retry or model probe.'
