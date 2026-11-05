#requires -Version 7.4
param ([string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'RuntimeProbes.psm1') -Force
$checks = [Collections.Generic.List[object]]::new()
function Assert-Probe([string]$Name, [bool]$Condition) {
    $checks.Add(@{
            name = $Name
            passed = $Condition
        })
    if (-not $Condition) {
        throw "Probe assertion failed: $Name"
    }
}

Assert-Probe 'zero samples never pass' (-not (Test-Reproducibility -Runs @()).ok)
Assert-Probe 'one sample never passes' (-not (Test-Reproducibility -Runs @(@{
                ok = $true
                ttftMs = 1
                tokPerSec = 1
            }
        )).ok)
Assert-Probe 'missing throughput never passes' (-not (Test-Reproducibility -Runs @(@{
                ok = $true
                ttftMs = 1
                tokPerSec = $null
            }

            , @{
                ok = $true
                ttftMs = 1
                tokPerSec = $null
            }
        )).ok)
Assert-Probe 'absent metric is explicitly unavailable' ((Test-Reproducibility -Runs @([pscustomobject]@{
                ok = $true
                ttftMs = 1
            }

            , [pscustomobject]@{
                ok = $true
                ttftMs = 1
            }
        )).reason -eq 'unavailable_metric')
Assert-Probe 'NaN is not a measured sample' (-not (Test-Reproducibility -Runs @(@{
                ok = $true
                ttftMs = [double]::NaN
                tokPerSec = 1
            }

            , @{
                ok = $true
                ttftMs = 1
                tokPerSec = 1
            }
        )).ok)
Assert-Probe 'zero duration is not stable performance' (-not (Test-Reproducibility -Runs @(@{
                ok = $true
                ttftMs = 0
                tokPerSec = 1
            }

            , @{
                ok = $true
                ttftMs = 0
                tokPerSec = 1
            }
        )).ok)
Assert-Probe 'failed request never passes' (-not (Test-Reproducibility -Runs @(@{
                ok = $false
                ttftMs = 1
                tokPerSec = 1
            }

            , @{
                ok = $true
                ttftMs = 1
                tokPerSec = 1
            }
        )).ok)
Assert-Probe 'stable measured samples pass' ((Test-Reproducibility -Runs @(@{
                ok = $true
                ttftMs = 10
                tokPerSec = 20
            }

            , @{
                ok = $true
                ttftMs = 10
                tokPerSec = 20
            }
        )).ok)
Assert-Probe 'outlier exceeds tolerance' (-not (Test-Reproducibility -Runs @(@{
                ok = $true
                ttftMs = 1
                tokPerSec = 20
            }

            , @{
                ok = $true
                ttftMs = 100
                tokPerSec = 20
            }
        )).ok)
$readyDirectory = Join-Path ([IO.Path]::GetTempPath()) ('saaia-runtime-probe-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($readyDirectory) | Out-Null
$readyFile = Join-Path $readyDirectory 'ready-port.txt'
$server = $null
$serverStdout = $null
$serverStderr = $null
$failure = $null
$startupFailure = ''
$startupTimeoutMilliseconds = 20000
$startupTimer = [Diagnostics.Stopwatch]::StartNew()
$fixtureStartup = [ordered]@{
    timeoutMilliseconds = $startupTimeoutMilliseconds
    elapsedMilliseconds = $null
    ready = $false
    processId = $null
    exitedBeforeReady = $false
    exitCodeBeforeCleanup = $null
    stderr = ''
    stdout = ''
}
$fixtureCleanup = [ordered]@{
    terminatedByTest = $false
    processStopped = $true
    readinessDirectoryRemoved = $false
    errors = [Collections.Generic.List[string]]::new()
}
function Read-FixtureDiagnostic([object]$ReadTask) {
    if ($null -eq $ReadTask) { return '' }
    if (-not $ReadTask.Wait(1000)) { return '[diagnostic pipe did not close within its bounded deadline]' }
    $text = $ReadTask.GetAwaiter().GetResult()
    if ($text.Length -gt 4000) { return $text.Substring($text.Length - 4000) }
    return $text
}
try {
    $start = [Diagnostics.ProcessStartInfo]::new('python')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $start.ArgumentList.Add((Join-Path $PSScriptRoot 'fixtures/runtime-probe-server.py'))
    $start.ArgumentList.Add('--ready-file')
    $start.ArgumentList.Add($readyFile)
    $server = [Diagnostics.Process]::Start($start)
    $fixtureStartup.processId = $server.Id
    $serverStdout = $server.StandardOutput.ReadToEndAsync()
    $serverStderr = $server.StandardError.ReadToEndAsync()
    $port = $null
    do {
        if ([IO.File]::Exists($readyFile)) {
            # The producer publishes this immutable path only after closing its
            # private writer. Do not swallow real read/permission failures.
            $published = [IO.File]::ReadAllText($readyFile)
            $parsedPort = 0
            if (-not [int]::TryParse($published, [Globalization.NumberStyles]::None,
                    [Globalization.CultureInfo]::InvariantCulture, [ref]$parsedPort) -or
                $parsedPort -lt 1 -or $parsedPort -gt 65535) {
                throw 'Loopback fixture published an invalid complete port.'
            }
            $port = $parsedPort
            $fixtureStartup.ready = $true
            break
        }

        Start-Sleep -Milliseconds 20
    }

    while ($startupTimer.ElapsedMilliseconds -lt $startupTimeoutMilliseconds -and -not $server.HasExited)
    $startupTimer.Stop()
    $fixtureStartup.elapsedMilliseconds = $startupTimer.Elapsed.TotalMilliseconds
    if (-not $port) {
        $fixtureStartup.exitedBeforeReady = $server.HasExited
        if ($server.HasExited) { $fixtureStartup.exitCodeBeforeCleanup = $server.ExitCode }
        $startupFailure = if ($server.HasExited) { 'process_exited_before_ready' } else { 'startup_deadline_exceeded' }
        throw 'Loopback fixture did not start.'
    }

    $url = "http://127.0.0.1:$port"
    $stream = Invoke-RuntimeCompletionProbe -BaseUrl "$url/stream" -TimeoutSeconds 2
    Assert-Probe 'stream finished with content' $stream.ok
    Assert-Probe 'first content precedes stream completion' ($stream.ttftState -eq 'available' -and $stream.ttftMs -lt ($stream.elapsedMs-50))
    Assert-Probe 'throughput identifies actual token usage' ($stream.outputTokens -eq 4 -and $stream.tokPerSec -gt 0)
    Assert-Probe 'actual response model is preserved' ($stream.model -eq 'fixture')
    $buffered = Invoke-RuntimeCompletionProbe -BaseUrl "$url/buffered" -TimeoutSeconds 2 -NonStreaming
    Assert-Probe 'buffered total duration is not TTFT' ($buffered.ok -and $null -eq $buffered.ttftMs -and $buffered.ttftState -eq 'unavailable')
    $missingUsage = Invoke-RuntimeCompletionProbe -BaseUrl "$url/no-usage" -TimeoutSeconds 2
    Assert-Probe 'missing usage stays unavailable' ($missingUsage.ok -and $null -eq $missingUsage.tokPerSec -and $missingUsage.throughputState -eq 'unavailable')
    $incomplete = Invoke-RuntimeCompletionProbe -BaseUrl "$url/incomplete" -TimeoutSeconds 2
    Assert-Probe 'truncated stream is not successful' (-not $incomplete.ok -and $incomplete.error -eq 'incomplete_stream')
    $slow = Invoke-RuntimeCompletionProbe -BaseUrl "$url/slow" -TimeoutSeconds 1
    Assert-Probe 'timeout includes reading streamed body' (-not $slow.ok -and $slow.elapsedMs -lt 2200)
} catch {
    $failure = $_
} finally {
    $startupTimer.Stop()
    if ($null -eq $fixtureStartup.elapsedMilliseconds) {
        $fixtureStartup.elapsedMilliseconds = $startupTimer.Elapsed.TotalMilliseconds
    }
    if ($null -ne $server) {
        try {
            if (-not $server.HasExited) {
                $fixtureCleanup.terminatedByTest = $true
                $server.Kill($true)
                if (-not $server.WaitForExit(5000)) { throw 'Owned loopback fixture did not stop within its cleanup deadline.' }
            }
        } catch {
            $fixtureCleanup.errors.Add($_.Exception.Message)
        }
        $fixtureCleanup.processStopped = $server.HasExited
        try {
            $fixtureStartup.stderr = Read-FixtureDiagnostic $serverStderr
            $fixtureStartup.stdout = Read-FixtureDiagnostic $serverStdout
        } catch {
            $fixtureCleanup.errors.Add('Could not collect owned fixture diagnostics: ' + $_.Exception.Message)
        }
        $server.Dispose()
    }

    try {
        foreach ($ownedFile in @($readyFile, ($readyFile + '.publishing'))) {
            if ([IO.File]::Exists($ownedFile)) { Remove-Item -LiteralPath $ownedFile -ErrorAction Stop }
        }
        [IO.Directory]::Delete($readyDirectory)
        $fixtureCleanup.readinessDirectoryRemoved = $true
    } catch {
        $fixtureCleanup.errors.Add('Could not remove owned fixture readiness files: ' + $_.Exception.Message)
    }
    if ($OutputPath) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath)))) | Out-Null
        @{
            schemaVersion = 'saaia-probe-tests-v1'
            checks = $checks
            executedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            providerCalls = 0
            passed = $null -eq $failure -and $fixtureStartup.ready -and $fixtureCleanup.processStopped -and
                $fixtureCleanup.readinessDirectoryRemoved -and $fixtureCleanup.errors.Count -eq 0
            fixtureStartup = $fixtureStartup
            fixtureCleanup = $fixtureCleanup
        }
        | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    }
}

if ($null -ne $failure) {
    if ($startupFailure) {
        throw ("Loopback fixture did not start: reason={0}; elapsed_ms={1:N0}; deadline_ms={2}; exit_code={3}; stderr={4}; cleanup_stopped={5}; cleanup_directory_removed={6}; cleanup_errors={7}" -f
            $startupFailure, $fixtureStartup.elapsedMilliseconds, $startupTimeoutMilliseconds,
            $fixtureStartup.exitCodeBeforeCleanup, $fixtureStartup.stderr,
            $fixtureCleanup.processStopped, $fixtureCleanup.readinessDirectoryRemoved, ($fixtureCleanup.errors -join '; '))
    }
    throw $failure
}
if ($fixtureCleanup.errors.Count -gt 0 -or -not $fixtureCleanup.processStopped -or -not $fixtureCleanup.readinessDirectoryRemoved) {
    throw ('Owned loopback fixture cleanup failed: ' + ($fixtureCleanup.errors -join '; '))
}

Write-Host "$($checks.Count) behavioral probe checks passed with a loopback fixture; provider calls: 0."
