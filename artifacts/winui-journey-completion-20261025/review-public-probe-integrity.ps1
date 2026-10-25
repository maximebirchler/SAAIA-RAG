#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$reviewRoot = $PSScriptRoot
$fixtureRoot = Join-Path $reviewRoot 'local-fixture'
$outputPath = Join-Path $reviewRoot 'public-probe-integrity-review.json'
if (Test-Path -LiteralPath $outputPath) { throw 'Refusing to replace an existing review.' }
function Read-Probes([string]$RelativePath) {
    $path = Join-Path $fixtureRoot $RelativePath
    return @(Get-Content -LiteralPath $path | ForEach-Object {
        if ($_ -match 'CLIENT_PROBE (\{.*\})$') { $Matches[1] | ConvertFrom-Json }
        elseif ($_ -match 'CLIENT_PROBE') { throw "Malformed probe line: $RelativePath" }
    })
}
function Summarize-Trace([object[]]$Events) {
    if (-not $Events.Count) { throw 'Missing selected trace.' }
    $sequenceValid = $true
    for ($index = 0; $index -lt $Events.Count; $index++) {
        if ($Events[$index].sequence -ne $index + 1) { $sequenceValid = $false }
    }
    $begins = @($Events | Where-Object operation -eq 'turn.begin')
    $ends = @($Events | Where-Object operation -eq 'turn.end')
    if (-not $sequenceValid -or $begins.Count -ne 1 -or $ends.Count -ne 1 -or $Events[-1].operation -ne 'turn.end') { throw 'Nonterminal or discontinuous selected trace.' }
    return [pscustomobject][ordered]@{
        traceId = $Events[0].traceId; eventCount = $Events.Count; firstSequence = $Events[0].sequence; lastSequence = $Events[-1].sequence;
        sequenceContinuous = $sequenceValid; begins = $begins.Count; terminalEvents = $ends.Count;
        terminalOutcome = $Events[-1].outcome; terminalReason = $Events[-1].reason;
        operations = @($Events | Select-Object sequence, operation, outcome, reason)
    }
}
$originalId = 'a5e082ac06ea53f7d34508e29be75e1e'
$resumedId = 'dd866295069fa24df98c78c47b2bb02d'
$primary = Read-Probes 'runtime/client-data/logs/client_startup.log'
$selectedTurns = @($originalId, $resumedId | ForEach-Object {
    $id = $_; $events = @($primary | Where-Object traceId -eq $id)
    $summary = Summarize-Trace $events
    $guards = @($events | Where-Object operation -eq 'agent.source_backed_fallback.skipped')
    $unexpected = @($events | Where-Object { $_.operation -match 'agent\.source_backed_pipeline\.active\.start|advanced\.(create|poll)|^rag\.' })
    if ($guards.Count -ne 2 -or $unexpected.Count -ne 0 -or @($guards | Where-Object reason -ne 'router_general_no_tools').Count) { throw 'Unexpected general-route fallback behavior.' }
    foreach ($guard in $guards) {
        if (@($guard.measurements.PSObject.Properties).Count -ne 0 -or @($guard.identifiers.PSObject.Properties).Count -ne 0) { throw 'Unexpected public correction-probe metadata.' }
        $serialized = $guard | ConvertTo-Json -Compress -Depth 5
        if ($serialized -match '(?i)"(?:password|apiKey|authorization|prompt|question|answer|content|documentPath)"\s*:') { throw 'Unexpected content/secret field in correction probe.' }
    }
    [pscustomobject][ordered]@{ trace = $summary; guardSkipCount = $guards.Count; unexpectedDocumentaryOrAdvancedEvents = $unexpected.Count;
        modelCalls = @($events | Where-Object operation -eq 'model.call' | Select-Object outcome, reason, identifiers);
        correctionProbes = $guards }
})
if ($selectedTurns[0].trace.terminalOutcome -ne 'cancelled' -or $selectedTurns[0].trace.terminalReason -ne 'user_cancelled' -or
    $selectedTurns[1].trace.terminalOutcome -ne 'success') { throw 'Unexpected general-route terminal outcomes.' }
$profileProofs = @('source-ambiguity', 'advanced-terminal' | ForEach-Object {
    $name = $_
    $probes = Read-Probes "runtime/client-profiles/$name/logs/client_startup.log"
    [pscustomobject][ordered]@{ profile = $name; totalProbeEvents = $probes.Count; traces = @($probes | Group-Object traceId | ForEach-Object { Summarize-Trace @($_.Group) }) }
})
$ambiguity = $profileProofs | Where-Object profile -eq 'source-ambiguity'
$advanced = $profileProofs | Where-Object profile -eq 'advanced-terminal'
if (@($ambiguity.traces | Where-Object { $_.terminalReason -eq 'ambiguous_indexed_revision' }).Count -ne 1 -or
    @($ambiguity.traces | Where-Object { $_.terminalReason -eq 'launch_accepted_page_unverified' }).Count -ne 1 -or
    @($ambiguity.traces | Where-Object { $_.terminalReason -eq 'resolution_or_launch_failed' }).Count -ne 1) { throw 'Missing expected source-resolution outcome.' }
$advancedValidation = @((Read-Probes 'runtime/client-profiles/advanced-terminal/logs/client_startup.log') | Where-Object operation -eq 'advanced.result.validation')
if ($advancedValidation.Count -ne 1 -or $advancedValidation[0].outcome -ne 'success' -or $advancedValidation[0].measurements.evidence -ne 1 -or
    $advancedValidation[0].measurements.claims -ne 1 -or $advancedValidation[0].measurements.visibleSources -ne 1) { throw 'Missing canonical advanced-result validation.' }
$backendLines = @(Get-Content -LiteralPath (Join-Path $fixtureRoot 'advanced-backend.stdout.log'))
$wire = @($backendLines | Where-Object { $_ -match 'Request completed: (GET /advanced-analysis/jobs/08655291-6f0f-4d34-8f58-fa8a9dffd49b|PATCH /chat/messages/f0e3aba3-2ef0-4f11-9257-0f92b6858116) 200 request_id=00-8e415a6b07c45ec763cb63fabae9ae8e-' } | ForEach-Object { $_.Trim() })
if ($wire.Count -ne 2 -or $wire[0] -notmatch 'GET /advanced-analysis/jobs/' -or $wire[1] -notmatch 'PATCH /chat/messages/') { throw 'Missing actual GET/PATCH correlation.' }
$providerEvents = @(Get-Content -LiteralPath (Join-Path $fixtureRoot 'advanced-fixture-events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
$firstNativeAt = [DateTimeOffset]'2026-10-25T16:23:09Z'
$laterProvider = @($providerEvents | Where-Object { [DateTimeOffset]$_.atUtc -ge $firstNativeAt })
if ($providerEvents.Count -ne 4 -or $laterProvider.Count -ne 0) { throw 'Unexpected provider activity during the advanced native window.' }
$references = @('build10-original-phrase-stream-cancel-proof.json', 'build10-cancel-late-stability-proof.json',
    'source-ambiguity-native-proof.json', 'advanced-terminal-final-state-proof.json' | ForEach-Object {
    $path = Join-Path $fixtureRoot $_
    [pscustomobject][ordered]@{ path = "local-fixture/$_"; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
})
$review = [ordered]@{
    schemaVersion = 'saaia-winui-public-probe-integrity-review.v1'; capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o');
    status = 'READ_ONLY_TRACE_INTEGRITY_VERIFIED_WITH_DECLARED_DIAGNOSTIC_LIMIT';
    executedActions = @('Read existing local logs and evidence files', 'Parse existing probe JSON and compare identities, sequences, terminal outcomes and actual HTTP completion lines', 'Write this new evidence projection only');
    requestsIssued = 0; testsExecuted = 0; nativeActionsPerformed = 0; sourceFilesModified = 0;
    generalBuild10 = $selectedTurns; sourceAndAdvancedProfiles = $profileProofs;
    publicCorrectionProbeContent = [ordered]@{ probesChecked = 4; noQuestionAnswerDocumentContentOrSecretField = $true;
        metadataBoundary = 'Synthetic trace/turn/session/span identifiers, sequence and timing, fixed operation/outcome/reason; measurements and identifiers are empty for the four correction probes.';
        ragTraceReasonAndIntentVisible = $true; exactFallbackEntryNotRetained = $true;
        limitation = 'In build10 the helper supplies entry, but RagTrace metadata allowlists omit it. Those public events show two skips and their policy reason, but cannot distinguish exact-item from documentary-probe by event payload alone. This corrects any broader statement that entry was observable in the earlier source review. A subsequently authorized source_step correction must be validated separately and is not retroactively attributed to build10.' };
    realAdvancedTransport = [ordered]@{ validationTraceId = $advancedValidation[0].traceId; actualGetThenPatch200 = $wire;
        backendLinesAreCompletionLogsNotPacketCapture = $true; canonicalValidation = $advancedValidation[0];
        providerEventCountWholeCampaign = $providerEvents.Count; originalPreflightProviderEvents = @($providerEvents | Where-Object traceparent -match '^00-ed0e5be7c9d84dc7bc62972cba8993cb-').Count;
        earlierFailedJ04ProviderEvents = @($providerEvents | Where-Object traceparent -match '^00-2ac4fbbbb36484c97f28330c14250b39-').Count;
        providerEventsSinceAdvancedNativeLaunch = $laterProvider.Count; advancedNativeWindowStartUtc = $firstNativeAt.ToString('o');
        durableStateBoundary = 'Final SQL/API identity, revision, answer and sources are checked by the separate runtime projection referenced below; this review issues no database/API query.' };
    evidenceReferences = $references;
    scope = @('Source launch probes intentionally say launch_accepted_page_unverified; native PDF page observations remain separate private visual evidence.',
        'The preflight job, the controlled pending assistant seed, actual native GET/PATCH recovery, and restart persistence are distinct stages; no new advanced job creation is claimed for J06.',
        'No new support export was performed. Earlier accepted W26 support-export coverage is retained with its original scope; this is a local-log review, not a new export acceptance.',
        'Only the selected build10 turns and the isolated source-ambiguity/advanced-terminal profile logs were checked here; unrelated earlier attempts are not reclassified as successful.');
    visualMetadataFinding = [ordered]@{ displayedText = 'type contenu contenu'; cause = 'Single label/value pair: source_card.content_role translates to type contenu and content_role.content to contenu. Production advanced mapping sets ContentRole=content.';
        classification = 'Minor product wording issue, reproducible outside this fixture; no duplicate source and no identity/hash corruption.'; sourceModified = $false };
    productStatus = 'TESTE_NON_APPROUVE'
}
$review | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $outputPath -Encoding utf8
[pscustomobject]@{ path = $outputPath; selectedGeneralTurns = $selectedTurns.Count; sourceAmbiguityTraces = $ambiguity.traces.Count;
    advancedProfileTraces = $advanced.traces.Count; correctionProbes = 4; actualAdvancedTransportCompletions = $wire.Count; laterProviderEvents = $laterProvider.Count } | ConvertTo-Json
