#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidateSet('A','B','C')][string]$DeletedThroughCase,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string]$SqlSnapshotLabel,
    [Parameter(Mandatory)][string]$ApiSnapshotPath,
    [string]$EmptyReplacementSessionId=''
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
$seed=Get-Content -LiteralPath (Join-Path $output 'deletion-seed.json') -Raw|ConvertFrom-Json
$sqlPath=Join-Path $output ('deletion-sql-snapshots/'+$SqlSnapshotLabel+'.json')
$sql=Get-Content -LiteralPath $sqlPath -Raw|ConvertFrom-Json
$apiPath=[IO.Path]::GetFullPath($ApiSnapshotPath)
if(-not $apiPath.StartsWith(($output.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)){throw 'API evidence must be under this owned fixture.'}
$api=Get-Content -LiteralPath $apiPath -Raw|ConvertFrom-Json
if($seed.schemaVersion -ne 'saaia-winui-deletion-seed.v1' -or $sql.schemaVersion -ne 'saaia-winui-deletion-sql-snapshot.v1' -or $api.schemaVersion -ne 'saaia-winui-chat-store-snapshot.v1'){throw 'Unexpected proof schema.'}
if($seed.environmentRunId -ne $sql.environmentRunId -or $seed.environmentRunId -ne $api.environmentRunId -or $seed.userId -ne $sql.userId -or $seed.userId -ne $api.userId){throw 'Proofs do not describe the same fixture and synthetic user.'}
$stage=@('A','B','C').IndexOf($DeletedThroughCase)+1
$removed=@($seed.sessions|Select-Object -First $stage)
$remaining=@($seed.sessions|Select-Object -Skip $stage)
$removedIds=@($removed|ForEach-Object{$_.sessionId})
$remainingIds=@($remaining|ForEach-Object{$_.sessionId})
$replacementId=$null
if($EmptyReplacementSessionId){
    if($DeletedThroughCase -ne 'C'){throw 'An empty replacement is only allowed after deleting the final seeded conversation.'}
    $replacementId=([Guid]$EmptyReplacementSessionId).ToString('D')
    if($replacementId -in @($seed.sessions.sessionId)){throw 'The replacement must have a new ID distinct from all deleted sessions.'}
    $remainingIds=@($replacementId)
}
function Same-Set([object[]]$Actual,[object[]]$Expected){return (($Actual|Sort-Object|ConvertTo-Json -Compress) -eq ($Expected|Sort-Object|ConvertTo-Json -Compress))}
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[bool]$Passed){$checks.Add([ordered]@{name=$Name;passed=$Passed})}
Check 'sql_session_ids_exact_remaining_set' (Same-Set @($sql.sessions|ForEach-Object{$_.sessionId}) $remainingIds)
Check 'api_session_ids_exact_remaining_set' (Same-Set @($api.sessions|ForEach-Object{$_.sessionId}) $remainingIds)
Check 'sql_database_session_count' ($sql.databaseSessionCount -eq $remainingIds.Count)
Check 'sql_database_message_count' ($sql.databaseMessageCount -eq 2*$remaining.Count)
Check 'sql_no_orphan_messages' ($sql.orphanMessageCount -eq 0)
Check 'sql_no_other_user_sessions' ($sql.otherUserSessionCount -eq 0)
Check 'sql_no_advanced_jobs' ($sql.advancedJobCount -eq 0)
Check 'api_complete_within_limits' ([bool]$api.completeWithinApiLimits)
Check 'api_message_count' ($api.totalMessageCount -eq 2*$remaining.Count)
Check 'api_removed_sessions_exact' (Same-Set @($api.comparison.removedSessionIds) $removedIds)
Check 'api_removed_messages_exact' (Same-Set @($api.comparison.removedMessageIds) @($removed.messages.messageId))
if($replacementId){
    Check 'api_added_empty_replacement_session_exact' (Same-Set @($api.comparison.addedSessionIds) @($replacementId))
    Check 'sql_replacement_has_zero_messages' (@($sql.sessions).Count -eq 1 -and $sql.sessions[0].messageCount -eq 0)
    Check 'api_replacement_has_zero_messages' (@($api.sessions).Count -eq 1 -and $api.sessions[0].messageCount -eq 0)
}else{Check 'api_no_added_sessions' (-not @($api.comparison.addedSessionIds).Count)}
Check 'api_no_added_or_changed_messages' (-not @($api.comparison.addedMessageIds).Count -and -not @($api.comparison.changedMessageIds).Count)
Check 'sql_delete_audit_targets_exact' (Same-Set @($sql.deleteAuditEvents.targetSessionId) @($removed.sessionId))
Check 'sql_delete_audit_count_exact' (@($sql.deleteAuditEvents).Count -eq $removed.Count)
foreach($session in $remaining){
    $sqlSession=@($sql.sessions|Where-Object sessionId -eq $session.sessionId)
    $apiSession=@($api.sessions|Where-Object sessionId -eq $session.sessionId)
    Check ('remaining_'+$session.case+'_sql_title_hash_unchanged') ($sqlSession.Count -eq 1 -and $sqlSession[0].titleSha256 -eq $session.titleSha256)
    Check ('remaining_'+$session.case+'_api_title_hash_unchanged') ($apiSession.Count -eq 1 -and $apiSession[0].title.sha256 -eq $session.titleSha256)
    Check ('remaining_'+$session.case+'_sql_message_ids_exact') (Same-Set @($sqlSession.messages.messageId) @($session.messages.messageId))
    Check ('remaining_'+$session.case+'_api_message_ids_exact') (Same-Set @($apiSession.messages.messageId) @($session.messages.messageId))
    foreach($message in $session.messages){
        $sqlMessage=@($sqlSession.messages|Where-Object messageId -eq $message.messageId)
        $apiMessage=@($apiSession.messages|Where-Object messageId -eq $message.messageId)
        Check ('remaining_'+$session.case+'_'+$message.role+'_sql_content_and_role_unchanged') ($sqlMessage.Count -eq 1 -and $sqlMessage[0].role -eq $message.role -and $sqlMessage[0].contentSha256 -eq $message.contentSha256)
        Check ('remaining_'+$session.case+'_'+$message.role+'_api_content_and_role_unchanged') ($apiMessage.Count -eq 1 -and $apiMessage[0].role -eq $message.role -and $apiMessage[0].content.sha256 -eq $message.contentSha256)
    }
}
$failed=@($checks|Where-Object passed -eq $false)
$proofPath=Join-Path $output ('deletion-step-'+$DeletedThroughCase.ToLowerInvariant()+'-verification.json')
if(Test-Path -LiteralPath $proofPath){throw 'Step verification already exists; refusing overwrite.'}
[ordered]@{schemaVersion='saaia-winui-deletion-step-verification.v1';capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');environmentRunId=$seed.environmentRunId;
    deletedThroughCase=$DeletedThroughCase;readOnly=$true;status=if($failed.Count){'FAILED'}else{'PASSED'};checkCount=$checks.Count;failedCheckCount=$failed.Count;
    removedSessionIds=$removedIds;remainingSessionIds=$remainingIds;emptyReplacementSessionId=$replacementId;
    apiSnapshot=[IO.Path]::GetRelativePath($output,$apiPath).Replace('\','/');apiSnapshotSha256=(Get-FileHash -LiteralPath $apiPath).Hash;
    sqlSnapshot=[IO.Path]::GetRelativePath($output,$sqlPath).Replace('\','/');sqlSnapshotSha256=(Get-FileHash -LiteralPath $sqlPath).Hash;
    checks=$checks.ToArray();limits=@('This verifies durable backend state and audit records after native UI actions. UI observations are recorded separately. It does not execute deletion, a model call or a client action.')
}|ConvertTo-Json -Depth 14|Set-Content -LiteralPath $proofPath -Encoding utf8
Write-Output "Deletion step $DeletedThroughCase : $($checks.Count-$failed.Count)/$($checks.Count) checks passed. Proof=$proofPath"
if($failed.Count){$failed|ForEach-Object{Write-Output ('FAILED: '+$_.name)};throw 'Deletion durable-state verification failed.'}
