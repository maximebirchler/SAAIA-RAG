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
try {
    $start = [Diagnostics.ProcessStartInfo]::new('python')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.ArgumentList.Add((Join-Path $PSScriptRoot 'fixtures/runtime-probe-server.py'))
    $start.ArgumentList.Add('--ready-file')
    $start.ArgumentList.Add($readyFile)
    $server = [Diagnostics.Process]::Start($start)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
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
            break
        }

        Start-Sleep -Milliseconds 20
    }

    while ([DateTimeOffset]::UtcNow -lt $deadline -and -not $server.HasExited)
    if (-not $port) {
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
} finally {
    if ($null -ne $server) {
        if (-not $server.HasExited) {
            $server.Kill($true)
            if (-not $server.WaitForExit(5000)) { throw 'Owned loopback fixture did not stop within its cleanup deadline.' }
        }

        $server.Dispose()
    }

    Remove-Item -LiteralPath $readyFile, ($readyFile + '.publishing') -ErrorAction SilentlyContinue
    [IO.Directory]::Delete($readyDirectory)
    if ($OutputPath) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath)))) | Out-Null
        @{
            schemaVersion = 'saaia-probe-tests-v1'
            checks = $checks
            executedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            providerCalls = 0
        }
        | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    }
}

Write-Host "$($checks.Count) behavioral probe checks passed with a loopback fixture; provider calls: 0."
