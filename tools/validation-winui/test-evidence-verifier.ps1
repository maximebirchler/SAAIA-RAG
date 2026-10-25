#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a new self-test directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$trace = '11111111111111111111111111111111'
$turn = '22222222222222222222222222222222'
function New-Event([long]$Sequence, [string]$Operation) {
    return [ordered]@{ schemaVersion = 'saaia.client-probe.v1'; traceId = $trace; turnId = $turn
        spanId = '3333333333333333'; sessionId = '44444444-4444-4444-4444-444444444444'
        sequence = $Sequence; elapsedMilliseconds = $Sequence; operation = $Operation
        outcome = 'success'; reason = $null }
}
$begin = New-Event 1 'turn.begin'
$end = New-Event 2 'turn.end'
$cases = @(
    @{ name = 'valid'; events = @($begin, $end); expectedExit = 0 },
    @{ name = 'missing-terminal'; events = @($begin); expectedExit = 1 },
    @{ name = 'duplicate-sequence'; events = @($begin, (New-Event 1 'turn.end')); expectedExit = 1 },
    @{ name = 'terminal-not-last'; events = @($begin, $end, (New-Event 3 'model.call')); expectedExit = 1 },
    @{ name = 'fixture-correlation'; events = @($begin, $end); expectedExit = 0
        fixture = @{ turnId = $turn; traceparent = "00-$trace-3333333333333333-00" } },
    @{ name = 'fixture-missing-correlation'; events = @($begin, $end); expectedExit = 1
        fixture = @{ ordinal = 1; mode = 'success' } },
    @{ name = 'fixture-wrong-trace'; events = @($begin, $end); expectedExit = 1
        fixture = @{ turnId = $turn; traceparent = '00-99999999999999999999999999999999-3333333333333333-00' } }
)
$results = @()
foreach ($case in $cases) {
    $caseRoot = Join-Path $output $case.name
    $logs = Join-Path $caseRoot 'logs'
    [IO.Directory]::CreateDirectory($logs) | Out-Null
    $lines = @($case.events | ForEach-Object { '2026-10-25T00:00:00Z [INFO] CLIENT_PROBE ' + ($_ | ConvertTo-Json -Compress) })
    [IO.File]::WriteAllLines((Join-Path $logs 'client_startup.log'), $lines)
    $resultPath = Join-Path $caseRoot 'result.json'
    $arguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'verify-evidence.ps1'),
        '-ClientDataRoot', $caseRoot, '-OutputPath', $resultPath)
    if ($case.ContainsKey('fixture')) {
        $fixturePath = Join-Path $caseRoot 'fixture-events.jsonl'
        $case.fixture | ConvertTo-Json -Compress | Set-Content -LiteralPath $fixturePath -Encoding utf8
        $arguments += @('-FixtureEvents', $fixturePath, '-MinimumCorrelatedFixtureRequests', '1')
    }
    $text = & (Join-Path $PSHOME 'pwsh.exe') @arguments 2>&1
    $actualExit = $LASTEXITCODE
    $results += [ordered]@{ name = $case.name; expectedExit = $case.expectedExit; actualExit = $actualExit
        passed = $actualExit -eq $case.expectedExit; evidence = $resultPath }
    if ($actualExit -ne $case.expectedExit) { $text | Write-Output }
}
$summary = [ordered]@{ schemaVersion = 'saaia.winui-evidence-verifier-selftests.v1'
    syntheticEvidenceOnly = $true; realUiExecuted = $false; networkExecuted = $false
    passed = @($results | Where-Object passed).Count; total = $results.Count; checks = $results }
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'checks.json') -Encoding utf8
Write-Output ('Verifier self-tests: ' + $summary.passed + '/' + $summary.total)
if ($summary.passed -ne $summary.total) { exit 1 }
