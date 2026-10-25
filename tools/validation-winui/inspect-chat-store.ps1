#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string]$Label = 'snapshot',
    [string]$BaselinePath = ''
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$owner = Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw | ConvertFrom-Json
$isolation = Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output) { throw 'Not an owned fixture.' }
if ($isolation.schemaVersion -ne 'saaia-winui-client-isolation.v1' -or
    [IO.Path]::GetFullPath($isolation.clientDataRoot) -ine [IO.Path]::GetFullPath($owner.clientDataRoot)) { throw 'Client isolation identity mismatch.' }
$backend = [Uri]$owner.backendUrl
if ($backend.Scheme -ne 'http' -or $backend.Host -ne '127.0.0.1' -or $backend.Port -ne $owner.backendPort) { throw 'Only the owned loopback backend may be read.' }
$clientUserId = [Guid]::Empty
if (-not [Guid]::TryParse([string]$isolation.userId, [ref]$clientUserId)) { throw 'Isolated client user ID is invalid.' }
$encodedUser = [Uri]::EscapeDataString($clientUserId.ToString('D'))
$started = [DateTimeOffset]::UtcNow
$private = Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw | ConvertFrom-Json
$headers = @{ 'X-Api-Key' = $private.apiKey }
function Get-TextFingerprint([AllowNull()][object]$Value) {
    $text = if ($null -eq $Value) { '' } elseif ($Value -is [string]) { $Value } else { ConvertTo-Json -InputObject $Value -Compress -Depth 50 }
    return [ordered]@{ sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant();
        characters = $text.Length; newlineCount = [regex]::Matches($text, '\r\n|\r|\n').Count; present = $null -ne $Value }
}
function Get-SafeMetadataCode([AllowNull()][object]$Value) {
    $text = [string]$Value
    if ($text -match '^[A-Za-z0-9_.:-]{1,128}$') { return $text }
    return $null
}
function Read-JsonMetadata([AllowNull()][object]$Value) {
    if ($Value -is [string]) {
        try { return ConvertFrom-Json -InputObject $Value -ErrorAction Stop } catch { return $null }
    }
    return $Value
}
$sessionRows = [Collections.Generic.List[object]]::new()
$sessionLimitReached = $false
for ($page = 0; $page -lt 10; $page++) {
    $sessionPage = Invoke-RestMethod -Uri ($owner.backendUrl + '/chat/sessions?userId=' + $encodedUser + '&limit=1000&offset=' + ($page * 1000)) -Headers $headers -TimeoutSec 20
    $sessionPage = @($sessionPage)
    foreach ($session in $sessionPage) { $sessionRows.Add($session) }
    if ($sessionPage.Count -lt 1000) { break }
    if ($page -eq 9) { $sessionLimitReached = $true }
}
$sessions = [Collections.Generic.List[object]]::new()
$messageLimitReached = $false
foreach ($session in $sessionRows) {
    $sessionGuid = [Guid]::Empty
    if (-not [Guid]::TryParse([string]$session.sessionId, [ref]$sessionGuid)) { throw 'Backend returned invalid session ID.' }
    $messages = Invoke-RestMethod -Uri ($owner.backendUrl + '/chat/sessions/' + $sessionGuid.ToString('D') + '/messages?userId=' + $encodedUser + '&limit=1000') -Headers $headers -TimeoutSec 20
    $messages = @($messages)
    $bounded = $messages.Count -eq 1000
    $messageLimitReached = $messageLimitReached -or $bounded
    $messageProofs = [Collections.Generic.List[object]]::new()
    $ordinal = 0
    foreach ($message in $messages) {
        $ordinal++
        $source = Read-JsonMetadata $message.sources
        $tracking = Read-JsonMetadata $message.trackingMetaJson
        $advanced = if ($source.advancedAnalysis) { $source.advancedAnalysis } elseif ($tracking.advancedAnalysis) { $tracking.advancedAnalysis } else { $null }
        $messageProofs.Add([ordered]@{
            ordinal = $ordinal; messageId = [string]$message.messageId; role = Get-SafeMetadataCode $message.role;
            createdAt = $message.createdAt; content = Get-TextFingerprint $message.content;
            statusNote = Get-TextFingerprint $message.statusNote; progressText = Get-TextFingerprint $message.progressText;
            sources = Get-TextFingerprint $message.sources; trackingMetadata = Get-TextFingerprint $message.trackingMetaJson;
            intent = Get-SafeMetadataCode $source.intent;
            advancedJobId = Get-SafeMetadataCode $advanced.jobId; advancedStatus = Get-SafeMetadataCode $advanced.status;
            advancedRevision = $advanced.revision; advancedResultOutcome = Get-SafeMetadataCode $advanced.resultOutcome
        })
    }
    $repeated = @($messageProofs | Group-Object { $_.role + ':' + $_.content.sha256 } | Where-Object Count -gt 1 | ForEach-Object {
        @{ role = $_.Group[0].role; contentSha256 = $_.Group[0].content.sha256; count = $_.Count; messageIds = @($_.Group.messageId) }
    })
    $sessions.Add([ordered]@{
        sessionId = $sessionGuid.ToString('D'); title = Get-TextFingerprint $session.title;
        createdAt = $session.createdAt; updatedAt = $session.updatedAt; lastMessageAt = $session.lastMessageAt;
        messageCount = $messages.Count; userMessageCount = @($messages | Where-Object role -eq 'user').Count;
        assistantMessageCount = @($messages | Where-Object role -eq 'assistant').Count;
        messageLimitReached = $bounded; repeatedContentGroups = $repeated; messages = $messageProofs.ToArray()
    })
}
$comparison = $null
if ($BaselinePath) {
    $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
    if ($baseline.schemaVersion -ne 'saaia-winui-chat-store-snapshot.v1' -or $baseline.userId -ne $clientUserId.ToString('D')) { throw 'Baseline does not describe the same isolated client user.' }
    # Enumerating a missing property on an empty collection can yield a single
    # null in PowerShell. Enumerate actual records so an empty store stays [].
    $beforeSessions = @($baseline.sessions | ForEach-Object { $_.sessionId })
    $currentSessions = @($sessions | ForEach-Object { $_.sessionId })
    $beforeMessages = @($baseline.sessions | ForEach-Object { $_.messages } | ForEach-Object { $_ })
    $currentMessages = @($sessions | ForEach-Object { $_.messages } | ForEach-Object { $_ })
    $beforeIds = @($beforeMessages | ForEach-Object { $_.messageId })
    $currentIds = @($currentMessages | ForEach-Object { $_.messageId })
    $changed = @($currentMessages | Where-Object {
        $candidate = $_
        $previous = $beforeMessages | Where-Object messageId -eq $candidate.messageId | Select-Object -First 1
        $previous -and ($previous.content.sha256 -ne $candidate.content.sha256 -or
            $previous.sources.sha256 -ne $candidate.sources.sha256 -or $previous.statusNote.sha256 -ne $candidate.statusNote.sha256 -or
            $previous.progressText.sha256 -ne $candidate.progressText.sha256 -or $previous.trackingMetadata.sha256 -ne $candidate.trackingMetadata.sha256)
    } | Select-Object -ExpandProperty messageId)
    $comparison = @{ baselinePath = [IO.Path]::GetFullPath($BaselinePath); baselineSha256 = (Get-FileHash -LiteralPath $BaselinePath -Algorithm SHA256).Hash;
        addedSessionIds = @($currentSessions | Where-Object { $_ -notin $beforeSessions }); removedSessionIds = @($beforeSessions | Where-Object { $_ -notin $currentSessions });
        addedMessageIds = @($currentIds | Where-Object { $_ -notin $beforeIds }); removedMessageIds = @($beforeIds | Where-Object { $_ -notin $currentIds }); changedMessageIds = $changed }
}
$totalMessages = 0
$totalUsers = 0
$totalAssistants = 0
foreach ($sessionProof in $sessions) {
    $totalMessages += [int]$sessionProof.messageCount
    $totalUsers += [int]$sessionProof.userMessageCount
    $totalAssistants += [int]$sessionProof.assistantMessageCount
}
$proof = [ordered]@{
    schemaVersion = 'saaia-winui-chat-store-snapshot.v1'; startedAtUtc = $started.ToString('o'); finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o');
    environmentRunId = $owner.runId; backendUrl = $owner.backendUrl; userId = $clientUserId.ToString('D'); readOnly = $true;
    contentMode = 'sha256_utf8_exact_text_counts_only'; sessionCount = $sessions.Count;
    totalMessageCount = $totalMessages; totalUserMessages = $totalUsers; totalAssistantMessages = $totalAssistants;
    sessionLimitReached = $sessionLimitReached; messageLimitReached = $messageLimitReached;
    completeWithinApiLimits = -not ($sessionLimitReached -or $messageLimitReached);
    consistency = 'Read-only sequential HTTP observations; not an atomic database snapshot. Capture while the client is idle for comparisons.';
    repeatedContentInterpretation = 'Repeated hashes can be legitimate repeated user requests; they are candidates for review, not an automatic duplicate verdict.';
    sessions = $sessions.ToArray(); comparison = $comparison
}
$directory = Join-Path $output 'chat-store-snapshots'
[IO.Directory]::CreateDirectory($directory) | Out-Null
$proofPath = Join-Path $directory ($started.ToString('yyyyMMddTHHmmssfffZ') + '-' + $Label + '.json')
if (Test-Path -LiteralPath $proofPath) { throw 'Snapshot path already exists; refusing overwrite.' }
[IO.File]::WriteAllText($proofPath, ($proof | ConvertTo-Json -Depth 40))
Write-Output "Chat-store snapshot saved: $proofPath"
Write-Output "Sessions=$($proof.sessionCount), userMessages=$($proof.totalUserMessages), assistantMessages=$($proof.totalAssistantMessages), completeWithinApiLimits=$($proof.completeWithinApiLimits)"
