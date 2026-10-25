#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^[A-Za-z0-9_-]{1,48}$')][string]$ProfileName = 'advanced-terminal',
    [string]$PreflightProofName = 'advanced-answered-preflight.json',
    [string]$ProofName = 'advanced-terminal-pending-fixture.json',
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$owner = Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw | ConvertFrom-Json
$isolation = Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output -or
    $isolation.schemaVersion -ne 'saaia-winui-client-isolation.v1' -or
    [IO.Path]::GetFullPath($isolation.clientDataRoot) -ine [IO.Path]::GetFullPath($owner.clientDataRoot)) {
    throw 'An owned fixture and matching isolated primary profile are required.'
}
foreach ($name in @($PreflightProofName, $ProofName)) {
    if ([IO.Path]::GetFileName($name) -ne $name -or [string]::IsNullOrWhiteSpace($name)) { throw 'Proof names must be filenames.' }
}
$preflightPath = Join-Path $output $PreflightProofName
$preflight = Get-Content -LiteralPath $preflightPath -Raw | ConvertFrom-Json
if ($preflight.schemaVersion -ne 'saaia-winui-worker-preflight.v1' -or -not $preflight.passed -or
    $preflight.expectedOutcome -ne 'answered' -or -not $preflight.answeredCanonicalIdentityValidated -or
    -not $preflight.realWorker -or -not $preflight.providerIsDeterministicFixture -or $preflight.injectedTerminalState -or
    $preflight.userId -eq $isolation.userId) { throw 'A successful distinct-user controlled-worker preflight is required.' }
$userId = ([Guid]$preflight.userId).ToString('D')
$sessionId = ([Guid]$preflight.sessionId).ToString('D')
$jobId = ([Guid]$preflight.jobId).ToString('D')
$handoffId = ([Guid]$preflight.final.handoffId).ToString('D')
foreach ($id in @($userId, $sessionId, $jobId, $handoffId)) {
    if ($id -eq [Guid]::Empty.ToString('D')) { throw 'Nonempty preflight identities are required.' }
}
$initial = @($preflight.snapshots | Where-Object { $_.status -eq 'queued' -and $_.revision -eq 1 })
if ($initial.Count -ne 1) { throw 'One genuinely observed queued revision1 is required for the stale resume token.' }
$advanced = Get-Content -LiteralPath (Join-Path $output 'advanced-worker.json') -Raw | ConvertFrom-Json
$uri = [Uri]$advanced.backendUrl
if ($advanced.schemaVersion -ne 'saaia-winui-advanced-worker.v1' -or -not $advanced.realWorkerEnabled -or
    -not $advanced.providerIsDeterministicFixture -or $advanced.externalProviderUsed -or
    $uri.Scheme -ne 'http' -or $uri.Host -ne '127.0.0.1' -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or
    @($owner.processes | Where-Object { $_.name -eq 'advanced-backend-runner' -and $_.port -eq $uri.Port }).Count -ne 1) {
    throw 'Only the registered owned loopback advanced worker may be queried.'
}
$profileRoot = [IO.Path]::GetFullPath((Join-Path $output ('runtime/client-profiles/' + $ProfileName)))
$profilesPrefix = [IO.Path]::GetFullPath((Join-Path $output 'runtime/client-profiles')) + [IO.Path]::DirectorySeparatorChar
if (-not $profileRoot.StartsWith($profilesPrefix, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $profileRoot)) {
    throw 'A new isolated named profile is required; no existing profile is overwritten.'
}
$proofPath = Join-Path $output $ProofName
if (Test-Path -LiteralPath $proofPath) { throw 'Pending seed proof already exists; refuse duplicate or uncertain mutation.' }
if (-not $Execute) {
    Write-Output 'Prepared only. Add -Execute to create a named isolated profile and one stale pending assistant for the existing real terminal job. No job or model call is created.'
    return
}
$private = Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw | ConvertFrom-Json
$headers = @{ 'X-Api-Key' = $private.apiKey }
$jobUrl = $advanced.backendUrl + '/advanced-analysis/jobs/' + $jobId + '?userId=' + $userId
$job = Invoke-RestMethod -Uri $jobUrl -Headers $headers -TimeoutSec 20
if ($job.jobId -ne $jobId -or $job.sessionId -ne $sessionId -or $job.handoffId -ne $handoffId -or
    $job.status -ne 'succeeded' -or $job.revision -ne $preflight.final.revision -or
    $job.result.outcome -ne 'answered' -or $job.result.providerCallCount -ne $preflight.final.result.providerCallCount) {
    throw 'The live real terminal job differs from its preflight proof.'
}
$messagesUrl = $advanced.backendUrl + '/chat/sessions/' + $sessionId + '/messages?userId=' + $userId + '&limit=1000'
$rows = Invoke-RestMethod -Uri $messagesUrl -Headers $headers -TimeoutSec 20
if (@($rows).Count -ne 0) { throw 'The isolated preflight session must still be empty; do not duplicate an uncertain seed.' }
$pending = [ordered]@{
    intent = 'advanced_analysis.answer'; sources = @()
    fixture = @{ schemaVersion = 'saaia.winui-existing-terminal-resume-seed.v1'; nativeTurnProduced = $false; finalAnswerInjected = $false }
    advancedAnalysis = [ordered]@{
        schemaVersion = 'saaia.advanced-analysis-client-state.v1'; jobId = $jobId; handoffId = $handoffId; sessionId = $sessionId
        status = 'queued'; revision = 1; attemptCount = 0; cancelRequested = $false
        resultOutcome = 'fixture_stale_pending_existing_job'; providerKey = $job.providerKey
    }
}
$sourcesJson = $pending | ConvertTo-Json -Depth 9 -Compress
$content = 'Reprise contrôlée : le client doit récupérer le résultat du job serveur existant. Aucun texte final ni aucune carte source ne sont injectés dans ce message.'
function Text-Hash([string]$Text) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}
$proof = [ordered]@{
    schemaVersion = 'saaia-winui-existing-terminal-pending-fixture.v1'; startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); state = 'writes_pending'
    environmentRunId = $owner.runId; profileName = $ProfileName; profileRootRelative = [IO.Path]::GetRelativePath($output, $profileRoot).Replace('\', '/')
    userId = $userId; sessionId = $sessionId; jobId = $jobId; handoffId = $handoffId; assistantMessageId = $null
    preflightProof = $PreflightProofName; preflightSha256 = (Get-FileHash -LiteralPath $preflightPath -Algorithm SHA256).Hash
    nativePrimaryUserModified = $false; nativePrimaryProfileModified = $false; jobCreated = $false; modelCalled = $false
    terminalJobSqlInjected = $false; finalAnswerInjected = $false; finalSourcesInjected = $false; originTraceIdFabricated = $false
    staleToken = @{ schemaVersion = 'saaia.advanced-analysis-client-state.v1'; status = 'queued'; revision = 1; source = 'actual_preflight_initial_snapshot'; originTraceId = 'omitted_not_a_native_origin' }
    actualWorkerJob = @{ status = $job.status; revision = $job.revision; providerCallCount = $job.result.providerCallCount }
    initialMessageCount = 0; expectedMessageCount = 1; pendingContentSha256 = Text-Hash $content; pendingSourcesSha256 = Text-Hash $sourcesJson
    productionParserSource = 'client/SAAIA.Client.WinUI/Services/AdvancedAnalysisPersistedState.cs'
    productionParserSha256 = (Get-FileHash -LiteralPath 'client/SAAIA.Client.WinUI/Services/AdvancedAnalysisPersistedState.cs' -Algorithm SHA256).Hash
    nativeUiExecuted = $false; productStatus = 'TESTE_NON_APPROUVE'
}
$reservation = [IO.File]::Open($proofPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$reservation.Dispose()
function Save-Proof { $proof | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $proofPath -Encoding utf8 }
Save-Proof
try {
    $client = Join-Path $profileRoot 'client'
    [IO.Directory]::CreateDirectory($client) | Out-Null
    $settings = Get-Content -LiteralPath (Join-Path $owner.clientDataRoot 'client/settings.json') -Raw | ConvertFrom-Json -AsHashtable
    $settings.BackendUrl = $advanced.backendUrl; $settings.BackendUrlAlternates = ''; $settings.AutoConnect = $true
    $settings.ShowAdvancedUi = $true; $settings.UiLanguage = 'fr'; $settings.UiTheme = 'dark'; $settings.LastSessionId = $sessionId
    $settings.LlmMode = 'external'; $settings.UseLocalLlm = $true; $settings.ManageLocalLlmProcess = $false; $settings.AutoStartOnConnect = $false
    $settings.Host = '127.0.0.1'; $settings.Port = $owner.fixturePort; $settings.ModelId = 'saaia-winui-fixture'
    [IO.File]::WriteAllText((Join-Path $client 'settings.json'), ($settings | ConvertTo-Json -Depth 8))
    $entropy = [Text.Encoding]::UTF8.GetBytes('SAAIA.Client.WinUI|CDC-v2.7')
    $protected = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($private.apiKey), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $secure = @{ UserId = $userId; ServerApiKeyProtected = [Convert]::ToBase64String($protected); LegacyApiKeyPlain = $null; OpenAiApiKeyProtected = $null; RunPodApiKeyProtected = $null }
    [IO.File]::WriteAllText((Join-Path $client 'secure.json'), ($secure | ConvertTo-Json))
    $message = Invoke-RestMethod -Method Post -Uri ($advanced.backendUrl + '/chat/sessions/' + $sessionId + '/messages') -Headers $headers -ContentType 'application/json' -TimeoutSec 20 -Body (
        @{ userId = $userId; role = 'assistant'; content = $content; sourcesJson = $sourcesJson; statusNote = 'Reprise contrôlée en attente'; progressText = 'Le vrai tracker doit récupérer le résultat existant.' } | ConvertTo-Json -Depth 11)
    $proof.assistantMessageId = ([Guid]$message.messageId).ToString('D')
    Save-Proof
    $storedRows = Invoke-RestMethod -Uri $messagesUrl -Headers $headers -TimeoutSec 20
    $storedRows = @($storedRows)
    if ($storedRows.Count -ne 1 -or $storedRows[0].messageId -ne $proof.assistantMessageId -or $storedRows[0].content -cne $content) {
        throw 'The exact single pending assistant was not durably acknowledged.'
    }
    $stored = if ($storedRows[0].sources -is [string]) { $storedRows[0].sources | ConvertFrom-Json } else { $storedRows[0].sources }
    if ($stored.advancedAnalysis.schemaVersion -cne 'saaia.advanced-analysis-client-state.v1' -or
        $stored.advancedAnalysis.jobId -ne $jobId -or $stored.advancedAnalysis.handoffId -ne $handoffId -or
        $stored.advancedAnalysis.sessionId -ne $sessionId -or $stored.advancedAnalysis.status -ne 'queued' -or
        $stored.advancedAnalysis.revision -ne 1 -or @($stored.sources).Count -ne 0) { throw 'Stored pending metadata changed.' }
    $unchangedJob = Invoke-RestMethod -Uri $jobUrl -Headers $headers -TimeoutSec 20
    if ($unchangedJob.revision -ne $job.revision -or $unchangedJob.status -ne $job.status -or
        $unchangedJob.result.providerCallCount -ne $job.result.providerCallCount) { throw 'The real worker job changed during seed.' }
    $proof.state = 'profile_prepared_single_pending_assistant_verified'
    $proof.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Save-Proof
    Write-Output ('Prepared profile ' + $ProfileName + ' and pending assistant ' + $proof.assistantMessageId + ' for the existing job. No client launched and no terminal answer injected.')
} catch {
    $proof.state = 'failed_or_uncertain_do_not_retry_automatically'
    $proof.failureType = $_.Exception.GetType().Name
    Save-Proof
    throw
}
