#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [string]$ProofName = 'advanced-worker-preflight.json')
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$owner = Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw | ConvertFrom-Json
$advanced = Get-Content -LiteralPath (Join-Path $output 'advanced-worker.json') -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output) { throw 'Not an owned fixture.' }
if ([IO.Path]::GetFileName($ProofName) -ne $ProofName) { throw 'ProofName must be a filename.' }
$proofPath = Join-Path $output $ProofName
if (Test-Path -LiteralPath $proofPath) { throw 'Preflight evidence already exists; refusing overwrite.' }
$private = Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw | ConvertFrom-Json
$userId = [Guid]::NewGuid().ToString('D')
$headers = @{ 'X-Api-Key' = $private.apiKey; 'X-SAAIA-Turn-Id' = 'fixture-preflight-' + [Guid]::NewGuid().ToString('N') }
$sessionBody = @{ userId = $userId; title = 'Isolated worker protocol preflight'; clientUser = 'synthetic-worker-preflight' } | ConvertTo-Json
$session = Invoke-RestMethod -Method Post -Uri ($advanced.backendUrl + '/chat/sessions') -Headers $headers -ContentType 'application/json' -Body $sessionBody
$headers['X-SAAIA-Session-Id'] = [string]$session.sessionId
$handoff = @{ schemaVersion = 'saaia.advanced-analysis-handoff.v1'; handoffId = [Guid]::NewGuid().ToString('D'); createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o');
    requestText = 'Quel est le code ORION du guide de validation ?'; language = 'fr'; originIntent = 'rag.answer'; reasonCode = 'winui_worker_protocol_preflight'; transferStage = 'before_retrieval';
    load = @{ planKind = 'lifecycle_validation'; deliverable = 'worker_protocol'; answerUnitCount = 1; atomicEvidenceCount = 1; rowCount = 0; columnCount = 0; structuredLayout = $false;
        atomicEvidenceType = 'content_claim'; atomicEvidenceMode = 'single_item'; selectionPolicy = 'single_item'; questionFocus = 'content'; requestedDocumentName = ''; boundedNamedDocumentExtraction = $false;
        candidateScopePaths = @(); rowLabels = @(); columns = @() };
    researchState = @{ memoryIsEvidence = $false; evidenceRevalidationRequired = $true; executedTools = @(); executedQueries = @(); attempts = @(); evidenceReferences = @() };
    dataPolicy = @{ externalProviderContentAuthorized = $false; externalProviderMetadataAuthorized = $false; authorizationSource = 'local_synthetic_fixture' } }
$body = @{ userId = $userId; sessionId = $session.sessionId; handoff = $handoff } | ConvertTo-Json -Depth 12
$job = Invoke-RestMethod -Method Post -Uri ($advanced.backendUrl + '/advanced-analysis/jobs') -Headers $headers -ContentType 'application/json' -Body $body
$snapshots = @(@{ status = $job.status; revision = $job.revision; atUtc = [DateTimeOffset]::UtcNow.ToString('o') })
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
do {
    Start-Sleep -Milliseconds 300
    $job = Invoke-RestMethod -Uri ($advanced.backendUrl + '/advanced-analysis/jobs/' + $job.jobId + '?userId=' + $userId) -Headers $headers
    if ($snapshots[-1].revision -ne $job.revision) { $snapshots += @{ status = $job.status; revision = $job.revision; atUtc = [DateTimeOffset]::UtcNow.ToString('o') } }
    if ($job.status -notin @('queued','running')) { break }
    if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Owned worker preflight timed out.' }
} while ($true)
$proof = @{ schemaVersion = 'saaia-winui-worker-preflight.v1'; performedThroughUi = $false; injectedTerminalState = $false; realWorker = $true;
    providerIsDeterministicFixture = $true; externalCalls = 0; semanticApproval = $false; jobId = $job.jobId; sessionId = $session.sessionId;
    userId = $userId; snapshots = $snapshots; final = $job; passed = ($job.status -eq 'succeeded' -and $job.result.outcome -eq 'insufficient_documentation') }
$proof | ConvertTo-Json -Depth 18 | Set-Content -LiteralPath $proofPath -Encoding utf8
if (-not $proof.passed) { throw "Worker did not produce the expected controlled terminal outcome; see preflight evidence." }
Write-Output 'Real worker API preflight passed with controlled insufficient_documentation; UI remains untested by this script.'
