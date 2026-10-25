#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[switch]$ResumePartialSeed)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$owner=Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw|ConvertFrom-Json
$isolation=Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw|ConvertFrom-Json
if($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output -or $owner.state -ne 'ready'){throw 'An owned ready fixture is required.'}
if($isolation.schemaVersion -ne 'saaia-winui-client-isolation.v1' -or [IO.Path]::GetFullPath($isolation.clientDataRoot) -ine [IO.Path]::GetFullPath($owner.clientDataRoot)){throw 'Client identity mismatch.'}
$backend=[Uri]$owner.backendUrl
if($backend.Scheme -ne 'http' -or $backend.Host -ne '127.0.0.1' -or $backend.Port -ne [int]$owner.backendPort){throw 'Only the owned loopback backend may be seeded.'}
$proofPath=Join-Path $output 'deletion-seed.json'
if(Test-Path -LiteralPath $proofPath){throw 'Delete-test seed exists; refusing duplicates.'}
$emptySnapshot=Join-Path $output 'deletion-sql-snapshots/00-empty-before-seed.json'
if($ResumePartialSeed){if(-not(Test-Path -LiteralPath $emptySnapshot)){throw 'Partial recovery requires the original empty-database proof.'}}
else{& (Join-Path $PSScriptRoot 'inspect-deletion-store.ps1') -OutputDirectory $output -Label '00-empty-before-seed'}
$before=Get-Content -LiteralPath (Join-Path $output 'deletion-sql-snapshots/00-empty-before-seed.json') -Raw|ConvertFrom-Json
if($before.environmentRunId -ne $owner.runId -or $before.userId -ne $isolation.userId -or $before.databaseSessionCount -ne 0 -or $before.databaseMessageCount -ne 0 -or $before.advancedJobCount -ne 0){throw 'The original observation must prove this dedicated database and user had no existing conversation, message or advanced job.'}
$userId=([Guid]$isolation.userId).ToString('D')
$private=Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
$headers=@{'X-Api-Key'=$private.apiKey}
$sessions=Invoke-RestMethod -Uri ($owner.backendUrl+'/chat/sessions?userId='+[Uri]::EscapeDataString($userId)+'&limit=1000') -Headers $headers -TimeoutSec 20
$sessions=@($sessions)
if($sessions.Count -and -not $ResumePartialSeed){throw 'The new isolated user must have no conversations.'}
function Fingerprint([string]$Text){return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()}
$cases=@(@{code='A';purpose='nonactive';title='TEST SUPPRESSION A - inactive'},@{code='B';purpose='active';title='TEST SUPPRESSION B - active'},@{code='C';purpose='last';title='TEST SUPPRESSION C - derniere'})
if($ResumePartialSeed){
    foreach($existing in $sessions){
        $case=@($cases|Where-Object title -eq $existing.title)
        if($case.Count -ne 1 -or @($sessions|Where-Object title -eq $existing.title).Count -ne 1){throw 'Partial seed contains an unknown or repeated conversation.'}
        $existingMessages=Invoke-RestMethod -Uri ($owner.backendUrl+'/chat/sessions/'+([Guid]$existing.sessionId).ToString('D')+'/messages?userId='+[Uri]::EscapeDataString($userId)+'&limit=1000') -Headers $headers -TimeoutSec 20
        $existingMessages=@($existingMessages)
        if($existingMessages.Count -ne 2){throw 'Partial recovery only accepts complete two-message synthetic sessions.'}
        foreach($role in @('user','assistant')){
            $expected="TEMOIN SUPPRESSION $($case[0].code) / $role / $($owner.runId) - Message synthetique de controle. Aucun modele execute."
            $match=@($existingMessages|Where-Object role -eq $role)
            if($match.Count -ne 1 -or $match[0].content -ne $expected){throw 'Partial seed content does not match this run exactly.'}
        }
    }
    & (Join-Path $PSScriptRoot 'inspect-deletion-store.ps1') -OutputDirectory $output -Label '00b-partial-recovery-preflight'
    $partial=Get-Content -LiteralPath (Join-Path $output 'deletion-sql-snapshots/00b-partial-recovery-preflight.json') -Raw|ConvertFrom-Json
    if($partial.databaseSessionCount -ne $sessions.Count -or $partial.databaseMessageCount -ne 2*$sessions.Count -or $partial.orphanMessageCount -ne 0 -or $partial.advancedJobCount -ne 0 -or $partial.otherUserSessionCount -ne 0){throw 'Partial recovery SQL identity/count preflight failed.'}
}
$created=[Collections.Generic.List[object]]::new()
foreach($case in $cases){
    $existing=@($sessions|Where-Object title -eq $case.title)
    $session=if($existing.Count){$existing[0]}else{Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/chat/sessions') -Headers $headers -ContentType 'application/json' -Body (@{userId=$userId;title=$case.title;clientUser='synthetic-delete-test-only'}|ConvertTo-Json)}
    $sessionId=([Guid]$session.sessionId).ToString('D')
    $messages=[Collections.Generic.List[object]]::new()
    foreach($role in @('user','assistant')){
        $content="TEMOIN SUPPRESSION $($case.code) / $role / $($owner.runId) - Message synthetique de controle. Aucun modele execute."
        $request=@{userId=$userId;role=$role;content=$content;sourcesJson=$null;statusNote=$null;progressText=$null;trackingMetaJson=$null}|ConvertTo-Json
        $message=if($existing.Count){
            $stored=Invoke-RestMethod -Uri ($owner.backendUrl+'/chat/sessions/'+$sessionId+'/messages?userId='+[Uri]::EscapeDataString($userId)+'&limit=1000') -Headers $headers -TimeoutSec 20
            $stored|Where-Object role -eq $role
        }else{Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/chat/sessions/'+$sessionId+'/messages') -Headers $headers -ContentType 'application/json' -Body $request}
        $messages.Add(@{messageId=([Guid]$message.messageId).ToString('D');role=$role;contentSha256=Fingerprint $content;contentCharacters=$content.Length})
    }
    $durable=Invoke-RestMethod -Uri ($owner.backendUrl+'/chat/sessions/'+$sessionId+'/messages?userId='+[Uri]::EscapeDataString($userId)+'&limit=1000') -Headers $headers -TimeoutSec 20
    $durable=@($durable)
    if($durable.Count -ne 2){throw 'Expected exactly two acknowledged synthetic messages per session.'}
    foreach($expected in $messages){$actual=@($durable|Where-Object messageId -eq $expected.messageId);if($actual.Count -ne 1 -or $actual[0].role -ne $expected.role -or (Fingerprint $actual[0].content) -ne $expected.contentSha256){throw 'Stored message changed after acknowledgement.'}}
    $created.Add([ordered]@{case=$case.code;purpose=$case.purpose;sessionTitle=$case.title;titleSha256=Fingerprint $case.title;sessionId=$sessionId;messages=$messages.ToArray()})
}
& (Join-Path $PSScriptRoot 'inspect-chat-store.ps1') -OutputDirectory $output -Label 'delete-initial-three'
& (Join-Path $PSScriptRoot 'inspect-deletion-store.ps1') -OutputDirectory $output -Label '01-three-seeded'
$sql=Get-Content -LiteralPath (Join-Path $output 'deletion-sql-snapshots/01-three-seeded.json') -Raw|ConvertFrom-Json
if($sql.sessionCount -ne 3 -or $sql.databaseMessageCount -ne 6 -or $sql.orphanMessageCount -ne 0 -or $sql.advancedJobCount -ne 0 -or $sql.otherUserSessionCount -ne 0){throw 'Independent SQL seed verification failed.'}
[ordered]@{schemaVersion='saaia-winui-deletion-seed.v1';seededAtUtc=[DateTimeOffset]::UtcNow.ToString('o');environmentRunId=$owner.runId;userId=$userId;
    backendUrl=$owner.backendUrl;backend='production_backend_isolated_loopback';clientDataRoot=$owner.clientDataRoot;sessionCount=3;messageCount=6;
    modelExecuted=$false;advancedJobCreated=$false;uiAutomated=$false;conversationsDeleted=$false;userProfileModified=$false;resumedVerifiedPartialSeed=[bool]$ResumePartialSeed;
    source='Three explicitly synthetic disposable conversations created for native deletion acceptance';sessions=$created.ToArray();
    sqlSnapshot='deletion-sql-snapshots/01-three-seeded.json';sqlSnapshotSha256=(Get-FileHash -LiteralPath (Join-Path $output 'deletion-sql-snapshots/01-three-seeded.json')).Hash;
    productStatus='TESTE_NON_APPROUVE'}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $proofPath -Encoding utf8
Write-Output 'Created exactly three owned disposable conversations, six synthetic control messages, no model execution.'
$created|ForEach-Object{[pscustomobject]$_}|Select-Object case,purpose,sessionTitle,sessionId|Format-Table -AutoSize
