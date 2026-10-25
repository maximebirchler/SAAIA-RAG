#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ProofName = 'source-ambiguity-session-fixture.json',
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$owner = Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw | ConvertFrom-Json
$isolation = Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output -or
    $isolation.schemaVersion -ne 'saaia-winui-client-isolation.v1' -or
    [IO.Path]::GetFullPath($isolation.clientDataRoot) -ine [IO.Path]::GetFullPath($owner.clientDataRoot)) {
    throw 'Owned fixture and isolated client identity are required.'
}
if ([IO.Path]::GetFileName($ProofName) -ne $ProofName -or [string]::IsNullOrWhiteSpace($ProofName)) {
    throw 'ProofName must be a filename.'
}
$proofPath = Join-Path $output $ProofName
if (Test-Path -LiteralPath $proofPath) { throw 'Proof already exists; refusing duplicate or uncertain mutation.' }
$backend = [Uri]$owner.backendUrl
if ($backend.Scheme -ne 'http' -or $backend.Host -ne '127.0.0.1' -or $backend.Port -ne $owner.backendPort -or
    $backend.AbsolutePath -ne '/' -or $backend.Query -or $backend.Fragment) {
    throw 'Only the owned loopback backend may be seeded.'
}
$userId = ([Guid]$isolation.userId).ToString('D')
if ($userId -eq [Guid]::Empty.ToString('D')) { throw 'An explicit nonempty isolated user is required.' }
$expectedDocuments = [IO.Path]::GetFullPath((Join-Path $output 'runtime/documents'))
if ([IO.Path]::GetFullPath($owner.documentsRoot) -ine $expectedDocuments) { throw 'Unexpected owned documents root.' }
if (-not $Execute) {
    Write-Output 'Prepared only. Add -Execute to create one isolated assistant with a canonical and a deliberately conflicting revision for the same inline link. No model is used.'
    return
}
$private = Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw | ConvertFrom-Json
$headers = @{ 'X-Api-Key' = $private.apiKey }
$identities = @(Get-Content -LiteralPath (Join-Path $output 'synthetic-documents.json') -Raw | ConvertFrom-Json)
$canonical = @($identities | Where-Object docPath -eq 'Validation/Guide-Validation.pdf')
if ($canonical.Count -ne 1) { throw 'One synthetic canonical guide is required.' }
$docId = ([Guid]$canonical[0].docId).ToString('D')
$context = Invoke-RestMethod -Uri ($owner.backendUrl + '/documents/context?docId=' + $docId + '&pageStart=2&pageEnd=2&before=0&after=0') -Headers $headers -TimeoutSec 20
if (-not $context.found -or @($context.items).Count -ne 1) { throw 'The canonical second page was not found.' }
$document = $context.document
$item = $context.items[0]
$pdf = Join-Path $expectedDocuments 'Validation/Guide-Validation.pdf'
$pdfHash = (Get-FileHash -LiteralPath $pdf -Algorithm SHA256).Hash.ToLowerInvariant()
if ($document.docId -ne $docId -or $document.docPath -cne 'Validation/Guide-Validation.pdf' -or
    $document.sourceHash -ne $pdfHash -or $canonical[0].sha256 -ne $pdfHash -or
    $canonical[0].revisionId -ne $document.revisionId -or $item.pageStart -ne 2 -or $item.pageEnd -ne 2) {
    throw 'Canonical PDF, version and physical page identity disagree.'
}
$competingHash = '0' * 64
$competingRevision = [Guid]::NewGuid().ToString('D')
if ($pdfHash -eq $competingHash -or $document.revisionId -eq $competingRevision) { throw 'Competing control must differ from canonical identity.' }
$sourceA = [ordered]@{
    evidenceId = 'UI_AMBIGUITY_CANONICAL'; docId = $docId; docPath = $document.docPath; docName = $document.docName
    category = 'Validation'; categoryPath = 'Validation'; docLanguage = 'fr'; pageStart = 2; pageEnd = 2
    sourceHash = $pdfHash; revisionId = ([Guid]$document.revisionId).ToString('D'); chunkId = ([Guid]$item.chunkId).ToString('D')
    snippet = 'Version canonique réelle, carte de contrôle exacte.'; score = 1.0
}
$sourceB = [ordered]@{
    evidenceId = 'UI_AMBIGUITY_CONFLICT'; docId = $docId; docPath = $document.docPath; docName = $document.docName
    category = 'Validation'; categoryPath = 'Validation'; docLanguage = 'fr'; pageStart = 2; pageEnd = 2
    sourceHash = $competingHash; revisionId = $competingRevision
    snippet = 'Révision concurrente fictive et absente : contrôle négatif, aucune provenance canonique revendiquée.'; score = 1.0
}
$payload = [ordered]@{
    intent = 'fixture.ui_source_ambiguity'
    fixture = @{ schemaVersion = 'saaia.winui-source-ambiguity-fixture.v1'; modelExecuted = $false; competingRevisionIsSynthetic = $true }
    sources = @($sourceA, $sourceB)
}
$sourcesJson = $payload | ConvertTo-Json -Depth 10 -Compress
$content = 'Contrôle UI sans modèle : le lien ci-dessous vise deux révisions distinctes et doit être refusé sans choisir arbitrairement.' + [Environment]::NewLine +
    '[[open|Validation/Guide-Validation.pdf|2|Référence ambiguë : Guide-Validation.pdf page 2]]' + [Environment]::NewLine +
    'Après fermeture du refus, la carte Version canonique réelle doit ouvrir la bonne page ; la révision fictive doit rester refusée.'
function Text-Hash([string]$Text) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}
$proof = [ordered]@{
    schemaVersion = 'saaia-winui-source-ambiguity-session-fixture.v1'; startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    state = 'writes_pending'; userId = $userId; sessionId = $null; messageId = $null; modelExecuted = $false
    competingRevisionIsSynthetic = $true; pdfFilesModified = $false; existingSessionsModified = $false
    canonical = @{ docId = $docId; docPath = $document.docPath; sourceHash = $pdfHash; revisionId = $document.revisionId; page = 2 }
    competing = @{ sourceHash = $competingHash; revisionId = $competingRevision; canonicalProvenanceClaimed = $false }
    contentSha256 = Text-Hash $content; sourcesJsonSha256 = Text-Hash $sourcesJson
    expected = @{ inlineLink = 'refused_ambiguous_indexed_revision'; canonicalCard = 'exact_page2'; competingCard = 'safe_refusal_no_exact_hash' }
    nativeUiValidated = $false; productStatus = 'TESTE_NON_APPROUVE'
}
$reservation = [IO.File]::Open($proofPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$reservation.Dispose()
function Save-Proof { $proof | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $proofPath -Encoding utf8 }
Save-Proof
try {
    $session = Invoke-RestMethod -Method Post -Uri ($owner.backendUrl + '/chat/sessions') -Headers $headers -ContentType 'application/json' -TimeoutSec 20 -Body (
        @{ userId = $userId; title = 'Fixture UI — lien ambigu entre deux révisions'; clientUser = 'synthetic-source-ambiguity' } | ConvertTo-Json)
    $proof.sessionId = ([Guid]$session.sessionId).ToString('D')
    Save-Proof
    $message = Invoke-RestMethod -Method Post -Uri ($owner.backendUrl + '/chat/sessions/' + $proof.sessionId + '/messages') -Headers $headers -ContentType 'application/json' -TimeoutSec 20 -Body (
        @{ userId = $userId; role = 'assistant'; content = $content; sourcesJson = $sourcesJson; statusNote = 'Fixture UI — ambiguïté contrôlée' } | ConvertTo-Json -Depth 12)
    $proof.messageId = ([Guid]$message.messageId).ToString('D')
    Save-Proof
    $rows = Invoke-RestMethod -Uri ($owner.backendUrl + '/chat/sessions/' + $proof.sessionId + '/messages?userId=' + [Uri]::EscapeDataString($userId) + '&limit=1000') -Headers $headers -TimeoutSec 20
    $rows = @($rows)
    if ($rows.Count -ne 1 -or $rows[0].messageId -ne $proof.messageId -or $rows[0].role -ne 'assistant' -or $rows[0].content -cne $content) {
        throw 'The exact single assistant was not durably acknowledged.'
    }
    $stored = if ($rows[0].sources -is [string]) { $rows[0].sources | ConvertFrom-Json } else { $rows[0].sources }
    if (@($stored.sources).Count -ne 2 -or $stored.sources[0].sourceHash -ne $pdfHash -or
        $stored.sources[1].sourceHash -ne $competingHash -or $stored.sources[0].revisionId -ne $document.revisionId -or
        $stored.sources[1].revisionId -ne $competingRevision -or
        @($stored.sources | Where-Object { $_.docPath -cne $document.docPath -or $_.pageStart -ne 2 -or $_.pageEnd -ne 2 }).Count) {
        throw 'Stored conflicting source identities changed.'
    }
    $proof.state = 'durable_single_assistant_verified'
    $proof.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Save-Proof
    Write-Output ('Controlled source ambiguity seeded; sessionId=' + $proof.sessionId + ', messageId=' + $proof.messageId + '. Native UI remains untested.')
} catch {
    $proof.state = 'failed_or_uncertain_do_not_retry_automatically'
    $proof.failureType = $_.Exception.GetType().Name
    Save-Proof
    throw
}
