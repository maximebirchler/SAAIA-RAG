#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string]$Label
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$owner=Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw|ConvertFrom-Json
$isolation=Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw|ConvertFrom-Json
if($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output){throw 'Not an owned local fixture.'}
if($isolation.schemaVersion -ne 'saaia-winui-client-isolation.v1' -or [IO.Path]::GetFullPath($isolation.clientDataRoot) -ine [IO.Path]::GetFullPath($owner.clientDataRoot)){throw 'Client isolation identity mismatch.'}
$expectedData=[IO.Path]::GetFullPath((Join-Path $output 'runtime/postgres/data'))
if([IO.Path]::GetFullPath($owner.postgresDataDirectory) -ine $expectedData -or [int]$owner.pgPort -in @(5432,0)){throw 'Unexpected database ownership or protected port.'}
$userId=([Guid]$isolation.userId).ToString('D')
$pg=@($owner.processes|Where-Object name -eq 'postgres')
if($pg.Count -ne 1){throw 'One owned PostgreSQL root is required.'}
$process=Get-Process -Id $pg[0].pid -ErrorAction Stop
if([IO.Path]::GetFullPath($process.Path) -ine [IO.Path]::GetFullPath($pg[0].path) -or $process.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$pg[0].startedAtUtc).UtcTicks){throw 'Owned PostgreSQL PID identity changed.'}
$pidLines=[IO.File]::ReadAllLines((Join-Path $expectedData 'postmaster.pid'))
if([int]$pidLines[0] -ne $process.Id -or [int]$pidLines[3] -ne [int]$owner.pgPort -or [IO.Path]::GetFullPath($pidLines[1]) -ine $expectedData){throw 'PostgreSQL data, PID or port mismatch.'}
$listeners=@(Get-NetTCPConnection -State Listen -LocalPort $owner.pgPort -ErrorAction Stop)
if(-not $listeners.Count -or @($listeners|Where-Object{$_.LocalAddress -notin @('127.0.0.1','::1') -or $_.OwningProcess -ne $process.Id}).Count){throw 'Database is not the owned loopback listener.'}
$private=Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
$psql=Join-Path $owner.postgresBinDirectory 'psql.exe'
$priorPassword=$env:PGPASSWORD
$priorAppName=$env:PGAPPNAME
try{
    $env:PGPASSWORD=$private.pgPassword
    $env:PGAPPNAME='saaia-owned-winui-delete-readonly'
    $actual=(& $psql -w -X -qAt -h 127.0.0.1 -p $owner.pgPort -U saaia_winui -d postgres -v ON_ERROR_STOP=1 -c "SELECT current_setting('data_directory');"|Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($actual) -ine $expectedData){throw 'Connected database is outside the owned fixture.'}
    $query=@"
BEGIN READ ONLY;
SELECT json_build_object(
 'deleteAuditEvents',COALESCE((SELECT json_agg(json_build_object('auditId',audit_id,'atUtc',ts,'targetSessionId',target) ORDER BY ts,audit_id) FROM audit_events WHERE action='chat.session.delete'),'[]'::json),
 'databaseSessionCount',(SELECT count(*) FROM chat_sessions),
 'databaseMessageCount',(SELECT count(*) FROM chat_messages),
 'orphanMessageCount',(SELECT count(*) FROM chat_messages m LEFT JOIN chat_sessions s ON s.tenant_id=m.tenant_id AND s.session_id=m.session_id WHERE s.session_id IS NULL),
 'advancedJobCount',(SELECT count(*) FROM advanced_analysis_jobs),
 'otherUserSessionCount',(SELECT count(*) FROM chat_sessions WHERE user_id <> '$userId'),
 'sessions',COALESCE((SELECT json_agg(row_to_json(q) ORDER BY q.created_at,q.session_id) FROM (
   SELECT s.session_id,s.title,s.created_at,s.updated_at,
     COALESCE((SELECT json_agg(row_to_json(mq) ORDER BY mq.created_at,mq.message_id) FROM (
       SELECT m.message_id,m.role,m.content,m.created_at FROM chat_messages m
       WHERE m.tenant_id=s.tenant_id AND m.session_id=s.session_id) mq),'[]'::json) AS messages
   FROM chat_sessions s WHERE s.user_id='$userId') q),'[]'::json));
COMMIT;
"@
    $raw=(& $psql -w -X -qAt -h 127.0.0.1 -p $owner.pgPort -U saaia_winui -d postgres -v ON_ERROR_STOP=1 -c $query|Out-String).Trim()
    if($LASTEXITCODE -ne 0){throw 'Read-only SQL observation failed.'}
    $sql=$raw|ConvertFrom-Json
}finally{$env:PGPASSWORD=$priorPassword;$env:PGAPPNAME=$priorAppName}
function Fingerprint([string]$Text){return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()}
$sessions=@($sql.sessions|ForEach-Object{
    [ordered]@{sessionId=$_.session_id;titleSha256=Fingerprint $_.title;titleCharacters=$_.title.Length;createdAt=$_.created_at;updatedAt=$_.updated_at;
        messageCount=@($_.messages).Count;messages=@($_.messages|ForEach-Object{[ordered]@{messageId=$_.message_id;role=$_.role;contentSha256=Fingerprint $_.content;contentCharacters=$_.content.Length;createdAt=$_.created_at}})}
})
$directory=Join-Path $output 'deletion-sql-snapshots'
[IO.Directory]::CreateDirectory($directory)|Out-Null
$path=Join-Path $directory ($Label+'.json')
if(Test-Path -LiteralPath $path){throw 'SQL evidence exists; refusing overwrite.'}
[ordered]@{schemaVersion='saaia-winui-deletion-sql-snapshot.v1';capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');environmentRunId=$owner.runId;
    userId=$userId;readOnly=$true;transaction='BEGIN READ ONLY';identityDataDirectoryAndLoopbackPortVerified=$true;postgresPort=$owner.pgPort;
    databaseSessionCount=$sql.databaseSessionCount;databaseMessageCount=$sql.databaseMessageCount;orphanMessageCount=$sql.orphanMessageCount;
    otherUserSessionCount=$sql.otherUserSessionCount;advancedJobCount=$sql.advancedJobCount;deleteAuditEvents=@($sql.deleteAuditEvents);sessionCount=$sessions.Count;sessions=$sessions
}|ConvertTo-Json -Depth 16|Set-Content -LiteralPath $path -Encoding utf8
Write-Output "Read-only SQL snapshot: $path"
Write-Output "Sessions=$($sql.databaseSessionCount), messages=$($sql.databaseMessageCount), orphans=$($sql.orphanMessageCount), jobs=$($sql.advancedJobCount), otherUserSessions=$($sql.otherUserSessionCount)"
