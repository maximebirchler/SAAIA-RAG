#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ClientDataRoot,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$SupportBundle,
    [string]$FixtureEvents,
    [ValidateRange(0, 1000000)][int]$MinimumCorrelatedFixtureRequests = 0,
    [string[]]$RequiredOperations = @(),
    [string[]]$RequiredTerminalOutcomes = @(),
    [switch]$AllowInFlight
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) { throw 'Select a new output path; evidence is not overwritten.' }
if (-not [IO.Path]::IsPathFullyQualified($ClientDataRoot)) { throw 'ClientDataRoot must be absolute.' }
$root = [IO.Path]::GetFullPath($ClientDataRoot)
$checks = [Collections.Generic.List[object]]::new()
$events = [Collections.Generic.List[object]]::new()
$inputs = [Collections.Generic.List[object]]::new()
function Add-Check([string]$Name, [bool]$Passed, [object]$Details = $null) {
    $checks.Add([ordered]@{ name = $Name; passed = $Passed; details = $Details })
}
function Add-Input([string]$Path) {
    $inputs.Add([ordered]@{ path = [IO.Path]::GetFullPath($Path); sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() })
}
$parseFailures = 0
$logs = @(Get-ChildItem -LiteralPath (Join-Path $root 'logs') -File -Filter 'client_startup.log*' -ErrorAction SilentlyContinue)
foreach ($log in $logs) {
    Add-Input $log.FullName
    foreach ($line in [IO.File]::ReadLines($log.FullName)) {
        $index = $line.IndexOf('CLIENT_PROBE ', [StringComparison]::Ordinal)
        if ($index -lt 0) { continue }
        try { $events.Add(($line.Substring($index + 13) | ConvertFrom-Json -Depth 32)) }
        catch { $parseFailures++ }
    }
}
Add-Check 'production_probe_events_exist' ($events.Count -gt 0) $events.Count
Add-Check 'probe_json_is_parseable' ($parseFailures -eq 0) $parseFailures
$invalidSchema = @($events | Where-Object {
    $_.schemaVersion -ne 'saaia.client-probe.v1' -or $_.traceId -cnotmatch '^[0-9a-f]{32}$' -or
    $_.turnId -cnotmatch '^[0-9a-f]{32}$' -or $_.spanId -cnotmatch '^[0-9a-f]{16}$' -or
    $_.sequence -lt 1 -or $_.elapsedMilliseconds -lt 0
})
Add-Check 'probe_schema_correlation_and_nonnegative_clocks' ($invalidSchema.Count -eq 0) $invalidSchema.Count
$turns = @()
foreach ($group in @($events | Group-Object turnId)) {
    # File rotation changes filename order; sequence is the authoritative order inside a turn.
    $ordered = @($group.Group | Sort-Object { [long]$_.sequence })
    $sequence = @($ordered | ForEach-Object { [long]$_.sequence })
    $unique = @($sequence | Select-Object -Unique)
    $terminal = @($ordered | Where-Object operation -eq 'turn.end')
    $begins = @($ordered | Where-Object operation -eq 'turn.begin')
    Add-Check ('turn_sequence_unique_' + $group.Name) ($sequence.Count -eq $unique.Count)
    Add-Check ('turn_trace_identity_stable_' + $group.Name) (@($ordered.traceId | Select-Object -Unique).Count -eq 1)
    Add-Check ('turn_begin_visible_' + $group.Name) ($begins.Count -eq 1)
    Add-Check ('turn_terminal_unique_' + $group.Name) ($terminal.Count -eq 1 -or ($AllowInFlight -and $terminal.Count -eq 0))
    if ($terminal.Count -gt 0) {
        Add-Check ('turn_terminal_is_last_' + $group.Name) ($terminal[-1].sequence -eq $ordered[-1].sequence)
    }
    $turns += [ordered]@{
        turnId = $group.Name; traceId = $ordered[0].traceId; sessionId = $ordered[0].sessionId
        eventCount = $ordered.Count; terminalOutcome = if ($terminal.Count) { $terminal[-1].outcome } else { 'in_flight' }
        terminalReason = if ($terminal.Count) { $terminal[-1].reason } else { $null }
        operations = @($ordered.operation | Select-Object -Unique)
    }
}
foreach ($operation in $RequiredOperations) {
    Add-Check ('required_operation_' + $operation) (@($events | Where-Object operation -eq $operation).Count -gt 0)
}
foreach ($outcome in $RequiredTerminalOutcomes) {
    Add-Check ('required_terminal_' + $outcome) (@($turns | Where-Object terminalOutcome -eq $outcome).Count -gt 0)
}
$supportSummary = $null
if ($SupportBundle) {
    Add-Input $SupportBundle
    $archive = [IO.Compression.ZipFile]::OpenRead($SupportBundle)
    try {
        function Read-EntryJson([string]$Name) {
            $entry = $archive.GetEntry($Name)
            if ($null -eq $entry) { return $null }
            if ($entry.Length -gt 10MB) { throw 'Support entry exceeds evidence verifier limit.' }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { return ($reader.ReadToEnd() | ConvertFrom-Json -Depth 64) }
            finally { $reader.Dispose() }
        }
        $manifest = Read-EntryJson 'collection-manifest.json'
        $history = Read-EntryJson 'diagnostics/client-turns.json'
        Add-Check 'support_collection_manifest_present' ($null -ne $manifest)
        Add-Check 'support_common_history_present' ($null -ne $history)
        Add-Check 'support_secure_store_excluded' (@($archive.Entries | Where-Object Name -eq 'secure.json').Count -eq 0)
        if ($history) {
            Add-Check 'support_collector_health' ($history.collectorFailures -eq 0 -and $history.logWriteFailures -eq 0)
            Add-Check 'support_uses_metadata_only_mode' ($history.contentMode -eq 'metadata_only')
            $supportSummary = [ordered]@{
                contentMode = $history.contentMode; historyTurns = @($history.turns).Count
                collectorFailures = $history.collectorFailures; logWriteFailures = $history.logWriteFailures
                collectionComplete = $manifest.collectionComplete; probeImplementation = $manifest.probeImplementation
                failedPieces = @($manifest.pieces | Where-Object Status -eq 'failed' | ForEach-Object { [ordered]@{ path = $_.Path; reason = $_.Reason } })
                unavailablePieces = @($manifest.pieces | Where-Object Status -eq 'unavailable' | ForEach-Object { $_.Path })
            }
        }
    }
    finally { $archive.Dispose() }
}
$fixtureSummary = $null
if ($FixtureEvents) {
    Add-Input $FixtureEvents
    $fixture = @([IO.File]::ReadLines($FixtureEvents) | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json -Depth 16 })
    $correlated = @($fixture | Where-Object { $_.turnId -and $_.traceparent })
    $unmatched = @($correlated | Where-Object {
        $candidate = $_
        @($events | Where-Object { $_.turnId -eq $candidate.turnId -and $candidate.traceparent -like ('00-' + $_.traceId + '-*') }).Count -eq 0
    })
    Add-Check 'fixture_correlated_requests_match_client_turns' ($unmatched.Count -eq 0) $unmatched.Count
    Add-Check 'fixture_minimum_correlated_requests_observed' ($correlated.Count -ge $MinimumCorrelatedFixtureRequests) $correlated.Count
    $fixtureSummary = [ordered]@{
        requestCount = $fixture.Count; correlatedRequestCount = $correlated.Count
        requestsWithoutClientTurn = $fixture.Count - $correlated.Count
        minimumRequiredCorrelatedRequests = $MinimumCorrelatedFixtureRequests
        note = 'Uncorrelated requests are not assigned to chat turns. Their warmup/probe origin must be established separately.'
    }
}
$failed = @($checks | Where-Object passed -eq $false)
$result = [ordered]@{
    schemaVersion = 'saaia.winui-trace-integrity.v1'; capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    status = if ($failed.Count -eq 0) { 'TRACE_INTEGRITY_VALIDATED' } else { 'TRACE_INTEGRITY_FAILED' }
    verifierScope = 'Local file integrity and production diagnostic correlation; does not execute UI or network.'
    modelQualityValidated = $false; visualUiValidated = $false; pdfPageRenderingValidated = $false
    productStatus = 'TESTE_NON_APPROUVE'; eventCount = $events.Count; turnCount = $turns.Count
    checks = $checks; turns = $turns; support = $supportSummary; fixture = $fixtureSummary; inputs = $inputs
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))) | Out-Null
$result | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output ('Trace integrity: ' + $result.status + '; events=' + $events.Count + '; turns=' + $turns.Count + '; failedChecks=' + $failed.Count)
if ($failed.Count -gt 0) { exit 1 }
