#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Z]$')][string]$Case,
    [Parameter(Mandatory)][string]$EmptyWitnessSessionId
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$owner=Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw|ConvertFrom-Json
$seed=Get-Content -LiteralPath (Join-Path $output 'deletion-seed.json') -Raw|ConvertFrom-Json
$isolation=Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw|ConvertFrom-Json
if($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output -or $owner.runId -ne $seed.environmentRunId -or $seed.userId -ne $isolation.userId){throw 'Owned synthetic fixture identity mismatch.'}
$backend=[Uri]$owner.backendUrl
if($backend.Scheme -ne 'http' -or $backend.Host -ne '127.0.0.1' -or $backend.Port -ne $owner.backendPort){throw 'Only the owned loopback backend may be seeded.'}
if($Case -in @($seed.sessions.case)){throw 'Do not reuse a nominal deletion case.'}
$proofPath=Join-Path $output ('delete-refresh-seed-'+$Case.ToLowerInvariant()+'.json')
if(Test-Path -LiteralPath $proofPath){throw 'Refresh session seed exists; refusing duplicate.'}
$witnessId=([Guid]$EmptyWitnessSessionId).ToString('D')
$userId=([Guid]$isolation.userId).ToString('D')
$private=Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
$headers=@{'X-Api-Key'=$private.apiKey}
$witness=Invoke-RestMethod -Uri ($owner.backendUrl+'/chat/sessions/'+$witnessId+'?userId='+$userId) -Headers $headers -TimeoutSec 20
$witnessMessages=Invoke-RestMethod -Uri ($owner.backendUrl+'/chat/sessions/'+$witnessId+'/messages?userId='+$userId+'&limit=1000') -Headers $headers -TimeoutSec 20
if($witness.sessionId -ne $witnessId -or @($witnessMessages).Count -ne 0){throw 'The preserved replacement witness must exist and remain empty.'}
& (Join-Path $PSScriptRoot 'inspect-deletion-store.ps1') -OutputDirectory $output -Label ('before-refresh-seed-'+$Case.ToLowerInvariant())
function Fingerprint([string]$Text){return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()}
$title='TEST SUPPRESSION '+$Case+' - refresh panne'
$session=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/chat/sessions') -Headers $headers -ContentType 'application/json' -Body (@{userId=$userId;title=$title;clientUser='synthetic-delete-refresh-test-only'}|ConvertTo-Json)
$sessionId=([Guid]$session.sessionId).ToString('D')
$messages=[Collections.Generic.List[object]]::new()
foreach($role in @('user','assistant')){
    $content="TEMOIN REFRESH $Case / $role / $($owner.runId) - Message synthetique de controle. Aucun modele execute."
    $message=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/chat/sessions/'+$sessionId+'/messages') -Headers $headers -ContentType 'application/json' -Body (@{userId=$userId;role=$role;content=$content;sourcesJson=$null;statusNote=$null;progressText=$null;trackingMetaJson=$null}|ConvertTo-Json)
    $messages.Add(@{messageId=([Guid]$message.messageId).ToString('D');role=$role;contentSha256=Fingerprint $content})
}
$actual=Invoke-RestMethod -Uri ($owner.backendUrl+'/chat/sessions/'+$sessionId+'/messages?userId='+$userId+'&limit=1000') -Headers $headers -TimeoutSec 20
$actual=@($actual)
if($actual.Count -ne 2){throw 'Expected exactly two seeded refresh-control messages.'}
foreach($message in $messages){$match=@($actual|Where-Object messageId -eq $message.messageId);if($match.Count -ne 1 -or $match[0].role -ne $message.role -or (Fingerprint $match[0].content) -ne $message.contentSha256){throw 'Refresh-control message changed after acknowledgement.'}}
& (Join-Path $PSScriptRoot 'inspect-deletion-store.ps1') -OutputDirectory $output -Label ('after-refresh-seed-'+$Case.ToLowerInvariant())
$after=Get-Content -LiteralPath (Join-Path $output ('deletion-sql-snapshots/after-refresh-seed-'+$Case.ToLowerInvariant()+'.json')) -Raw|ConvertFrom-Json
$witnessAfter=@($after.sessions|Where-Object sessionId -eq $witnessId)
if($witnessAfter.Count -ne 1 -or $witnessAfter[0].messageCount -ne 0 -or $after.orphanMessageCount -ne 0 -or $after.advancedJobCount -ne 0 -or $after.otherUserSessionCount -ne 0){throw 'Preserved witness or isolated database invariant failed.'}
[ordered]@{schemaVersion='saaia-winui-delete-refresh-seed.v1';createdAtUtc=[DateTimeOffset]::UtcNow.ToString('o');environmentRunId=$owner.runId;case=$Case;
    userId=$userId;sessionId=$sessionId;sessionTitle=$title;messages=$messages.ToArray();emptyWitnessSessionId=$witnessId;emptyWitnessUnchanged=$true;
    seededThrough='production_backend_isolated_loopback';modelExecuted=$false;conversationsDeleted=$false;uiAutomated=$false
}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $proofPath -Encoding utf8
Write-Output "Synthetic refresh case $Case seeded: sessionId=$sessionId, empty witness=$witnessId. No deletion or model call."
