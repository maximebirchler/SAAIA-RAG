#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ProofName = 'source-selection-two-message-fixture.json',
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$owner = Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw | ConvertFrom-Json
$isolation = Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output) {
    throw 'Not an owned fixture.'
}
if ($isolation.schemaVersion -ne 'saaia-winui-client-isolation.v1' -or
    [IO.Path]::GetFullPath($isolation.clientDataRoot) -ine [IO.Path]::GetFullPath($owner.clientDataRoot)) {
    throw 'Client isolation identity mismatch.'
}
if ([IO.Path]::GetFileName($ProofName) -ne $ProofName -or [string]::IsNullOrWhiteSpace($ProofName)) {
    throw 'ProofName must be a filename.'
}
$proofPath = Join-Path $output $ProofName
if (Test-Path -LiteralPath $proofPath) { throw 'Proof already exists; refusing a duplicate or uncertain seed.' }
$backend = [Uri]$owner.backendUrl
if ($backend.Scheme -ne 'http' -or $backend.Host -ne '127.0.0.1' -or
    $backend.Port -ne $owner.backendPort -or $backend.AbsolutePath -ne '/' -or $backend.Query -or $backend.Fragment) {
    throw 'Only the owned loopback backend may be seeded.'
}
$userId = ([Guid]$isolation.userId).ToString('D')
if ($userId -eq [Guid]::Empty.ToString('D')) { throw 'Isolated user ID cannot be empty.' }
$documentsRoot = [IO.Path]::GetFullPath($owner.documentsRoot)
$ownedPrefix = $output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $documentsRoot.StartsWith($ownedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Synthetic documents must remain under the owned fixture.'
}
if (-not $Execute) {
    Write-Output 'Prepared only. Add -Execute to create one new isolated session with exactly two controlled assistant messages. No model is used.'
    return
}
$private = Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw | ConvertFrom-Json
$headers = @{ 'X-Api-Key' = $private.apiKey }
$identities = @(Get-Content -LiteralPath (Join-Path $output 'synthetic-documents.json') -Raw | ConvertFrom-Json)
$specifications = @(
    @{ marker = 'UI_FIXTURE_SOURCE_A'; path = 'Validation/Guide-Validation.pdf'; category = 'Validation'; page = 2 },
    @{ marker = 'UI_FIXTURE_SOURCE_B'; path = 'Archive/Guide-Validation.pdf'; category = 'Archive'; page = 1 }
)
function Get-TextSha256([string]$Text) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}
$prepared = [Collections.Generic.List[object]]::new()
# Both real canonical identities and PDF hashes are checked before the first write.
foreach ($specification in $specifications) {
    $matches = @($identities | Where-Object docPath -eq $specification.path)
    if ($matches.Count -ne 1) { throw 'Each synthetic document must have one canonical identity.' }
    $identity = $matches[0]
    $docId = ([Guid]$identity.docId).ToString('D')
    $contextUrl = $owner.backendUrl + '/documents/context?docId=' + $docId +
        '&pageStart=' + $specification.page + '&pageEnd=' + $specification.page + '&before=0&after=0'
    $context = Invoke-RestMethod -Uri $contextUrl -Headers $headers -TimeoutSec 20
    if (-not $context.found -or @($context.items).Count -ne 1) { throw 'Canonical page could not be read exactly.' }
    $document = $context.document
    $item = $context.items[0]
    $pdf = [IO.Path]::GetFullPath((Join-Path $documentsRoot $specification.path))
    $documentsPrefix = $documentsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $pdf.StartsWith($documentsPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source escapes owned documents.' }
    $pdfHash = (Get-FileHash -LiteralPath $pdf -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($document.docPath -cne $specification.path -or $document.docId -ne $docId -or
        $document.sourceHash -ne $pdfHash -or $identity.sha256 -ne $pdfHash -or
        $document.revisionId -ne $identity.revisionId -or
        $item.pageStart -ne $specification.page -or $item.pageEnd -ne $specification.page) {
        throw 'Canonical identity, version, page and owned PDF disagree.'
    }
    $source = [ordered]@{
        evidenceId = $specification.marker; docId = $docId; docPath = [string]$document.docPath
        docName = [string]$document.docName; category = $specification.category; categoryPath = $specification.category
        docLanguage = 'fr'; pageStart = $specification.page; pageEnd = $specification.page
        sourceHash = $pdfHash; revisionId = ([Guid]$document.revisionId).ToString('D')
        chunkId = ([Guid]$item.chunkId).ToString('D'); snippet = [string]$item.text; score = 1.0
    }
    $payload = [ordered]@{
        intent = 'fixture.ui_source_selection'
        fixture = @{ schemaVersion = 'saaia.winui-source-selection-fixture.v1'; modelExecuted = $false; marker = $specification.marker }
        sources = @($source)
    }
    $sourcesJson = $payload | ConvertTo-Json -Depth 12 -Compress
    $validated = $sourcesJson | ConvertFrom-Json
    if (@($validated.sources).Count -ne 1 -or $validated.sources[0].sourceHash.Length -ne 64) { throw 'Invalid source payload.' }
    $content = $specification.marker + ' — résultat contrôlé sans modèle. Source ' +
        $specification.path + ', page ' + $specification.page + '.'
    $prepared.Add(@{ marker = $specification.marker; content = $content; sourcesJson = $sourcesJson; source = $source })
}
if ($prepared[0].source.docId -eq $prepared[1].source.docId -or
    $prepared[0].source.sourceHash -eq $prepared[1].source.sourceHash) {
    throw 'This scenario requires two different documents and exact file versions.'
}
$proof = [ordered]@{
    schemaVersion = 'saaia-winui-source-selection-session-fixture.v1'
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); state = 'writes_pending'
    backend = 'production_owned_loopback_backend'; userId = $userId; sessionId = $null
    modelExecuted = $false; pdfFilesModified = $false; existingSessionsModified = $false
    expectedMessageCount = 2; messages = @(); uiSelectionValidated = $false
    productStatus = 'TESTE_NON_APPROUVE'
}
# Reserve a durable proof before mutation. A partial/uncertain failure must not be silently retried.
$reservation = [IO.File]::Open($proofPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$reservation.Dispose()
function Save-Proof { $proof | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $proofPath -Encoding utf8 }
Save-Proof
try {
    $sessionRequest = @{ userId = $userId; title = 'Fixture UI sources — deux messages distincts'; clientUser = 'synthetic-source-selection-fixture' } | ConvertTo-Json
    $session = Invoke-RestMethod -Method Post -Uri ($owner.backendUrl + '/chat/sessions') -Headers $headers -ContentType 'application/json' -Body $sessionRequest -TimeoutSec 20
    $sessionId = ([Guid]$session.sessionId).ToString('D')
    if ($sessionId -eq [Guid]::Empty.ToString('D')) { throw 'Invalid created session identity.' }
    $proof.sessionId = $sessionId
    Save-Proof
    foreach ($entry in $prepared) {
        $request = @{ userId = $userId; role = 'assistant'; content = $entry.content; sourcesJson = $entry.sourcesJson
            statusNote = 'Fixture UI sources distinctes'; progressText = $null; trackingMetaJson = $null } | ConvertTo-Json -Depth 14
        $message = Invoke-RestMethod -Method Post -Uri ($owner.backendUrl + '/chat/sessions/' + $sessionId + '/messages') -Headers $headers -ContentType 'application/json' -Body $request -TimeoutSec 20
        $messageId = ([Guid]$message.messageId).ToString('D')
        if ($messageId -eq [Guid]::Empty.ToString('D')) { throw 'Invalid created message identity.' }
        $proof.messages += @{ marker = $entry.marker; messageId = $messageId
            canonicalSource = @{} + $entry.source; contentSha256 = Get-TextSha256 $entry.content; sourcesJsonSha256 = Get-TextSha256 $entry.sourcesJson }
        # Raw indexed snippets stay in the owned backend, never in the public proof.
        $proof.messages[-1].canonicalSource.Remove('snippet')
        Save-Proof
    }
    $rows = Invoke-RestMethod -Uri ($owner.backendUrl + '/chat/sessions/' + $sessionId + '/messages?userId=' + [Uri]::EscapeDataString($userId) + '&limit=1000') -Headers $headers -TimeoutSec 20
    # Invoke-RestMethod does not enumerate a JSON array in a pipeline expression.
    # Normalize the assigned response, rather than wrapping the command itself.
    $rows = @($rows)
    if ($rows.Count -ne 2 -or @($rows | Where-Object role -ne 'assistant').Count -ne 0) { throw 'Expected exactly two durable assistant messages.' }
    foreach ($entry in $prepared) {
        $saved = @($rows | Where-Object content -CEQ $entry.content)
        if ($saved.Count -ne 1) { throw 'Controlled message was not durably stored exactly once.' }
        $stored = if ($saved[0].sources -is [string]) { $saved[0].sources | ConvertFrom-Json } else { $saved[0].sources }
        if (@($stored.sources).Count -ne 1) { throw 'Stored message has an unexpected source count.' }
        foreach ($field in @('evidenceId','docId','docPath','sourceHash','revisionId','chunkId','pageStart','pageEnd')) {
            if ([string]$stored.sources[0].$field -cne [string]$entry.source.$field) { throw 'Stored canonical source identity changed.' }
        }
        $expectedProof = @($proof.messages | Where-Object marker -eq $entry.marker)[0]
        if ($saved[0].messageId -ne $expectedProof.messageId) { throw 'Read-back message identity does not match acknowledgement.' }
    }
    $snapshotStart = [DateTimeOffset]::UtcNow
    & (Join-Path $PSScriptRoot 'inspect-chat-store.ps1') -OutputDirectory $output -Label 'source-selection-two-messages'
    $snapshot = Get-ChildItem -LiteralPath (Join-Path $output 'chat-store-snapshots') -Filter '*-source-selection-two-messages.json' -File |
        Where-Object LastWriteTimeUtc -ge $snapshotStart.UtcDateTime | Sort-Object Name | Select-Object -Last 1
    if (-not $snapshot) { throw 'Durable snapshot was not captured.' }
    $proof.state = 'seeded_and_read_back'
    $proof.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $proof.durableMessageCount = 2
    $proof.snapshot = [IO.Path]::GetRelativePath($output, $snapshot.FullName).Replace('\','/')
    $proof.snapshotSha256 = (Get-FileHash -LiteralPath $snapshot.FullName).Hash
    Save-Proof
} catch {
    $proof.state = 'failed_or_unconfirmed_do_not_retry'
    $proof.failureType = $_.Exception.GetType().Name
    Save-Proof
    throw 'Controlled two-message seed failed or is unconfirmed. Inspect its proof and owned session; no automatic retry or cleanup was attempted.'
}
Write-Output ("Two controlled source messages seeded in one new session: " + $proof.sessionId + ". No model or UI was executed.")
