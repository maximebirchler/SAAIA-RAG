#requires -Version 7.4
[CmdletBinding()]
param([string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$owned = Join-Path ([IO.Path]::GetTempPath()) ('saaia-probe-readiness-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($owned) | Out-Null
$hook = @'
import io, json, os, sys, time
from pathlib import Path

if "--ready-file" in sys.argv:
    target = Path(sys.argv[sys.argv.index("--ready-file") + 1]).resolve()
    receipt = os.environ["SAAIA_PROBE_READINESS_RECEIPT"]
    original_open = io.open

    class HeldPublication:
        def __init__(self, stream, path):
            self.stream, self.path = stream, path
        def __getattr__(self, name):
            return getattr(self.stream, name)
        def __enter__(self):
            return self
        def __exit__(self, *args):
            return self.stream.__exit__(*args)
        def write(self, text):
            count = self.stream.write(text)
            self.stream.flush()
            with original_open(receipt, "w", encoding="utf-8") as log:
                json.dump({"heldWriteObserved": True, "heldMilliseconds": 350,
                    "writerOpenedPublishedPath": self.path == target,
                    "publishedPathExistsDuringPrivateWrite": target.exists()}, log)
            time.sleep(0.35)
            return count

    def held_open(file, mode="r", *args, **kwargs):
        stream = original_open(file, mode, *args, **kwargs)
        path = Path(file).resolve()
        if any(flag in mode for flag in "wax") and path in (target, Path(str(target) + ".publishing")):
            return HeldPublication(stream, path)
        return stream

    io.open = held_open
'@
$receiptPath = Join-Path $owned 'held-write.json'
$childProofPath = Join-Path $owned 'behavioral-checks.json'
$process = $null
$proof = [ordered]@{
    schemaVersion = 'saaia-runtime-probe-readiness-test-v1'
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    providerCalls = 0
    passed = $false
}
try {
    [IO.File]::WriteAllText((Join-Path $owned 'sitecustomize.py'), $hook, [Text.UTF8Encoding]::new($false))
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $info.Environment['PYTHONPATH'] = $owned
    $info.Environment['SAAIA_PROBE_READINESS_RECEIPT'] = $receiptPath
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Test-RuntimeProbes.ps1'), '-OutputPath', $childProofPath)) {
        $info.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(20000)) { throw 'Readiness regression exceeded its bounded 20-second deadline.' }
    $proof.childExitCode = $process.ExitCode
    $proof.childStdout = $stdout.GetAwaiter().GetResult()
    $proof.childStderr = $stderr.GetAwaiter().GetResult()
    if (Test-Path -LiteralPath $receiptPath) {
        $proof.heldWrite = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    }
    if (Test-Path -LiteralPath $childProofPath) {
        $childProof = Get-Content -LiteralPath $childProofPath -Raw | ConvertFrom-Json
        $proof.behavioralChecks = @($childProof.checks)
    }
    $proof.passed = $process.ExitCode -eq 0 -and $proof.Contains('heldWrite') -and
        $proof.heldWrite.heldWriteObserved -eq $true -and
        $proof.heldWrite.writerOpenedPublishedPath -eq $false -and
        $proof.heldWrite.publishedPathExistsDuringPrivateWrite -eq $false -and
        $proof.Contains('behavioralChecks') -and $proof.behavioralChecks.Count -gt 0 -and
        @($proof.behavioralChecks | Where-Object passed -ne $true).Count -eq 0
} finally {
    if ($process) {
        if (-not $process.HasExited) {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) { throw 'Owned readiness regression did not stop within its cleanup deadline.' }
        }
        $process.Dispose()
    }
    # This generated directory contains only the three fixed proof/hook names
    # and Python's cache; validate its exact ownership before recursive cleanup.
    $resolvedOwned = [IO.Path]::GetFullPath($owned)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if ([IO.Path]::GetDirectoryName($resolvedOwned) -ine $expectedParent -or
        [IO.Path]::GetFileName($resolvedOwned) -notmatch '^saaia-probe-readiness-[a-f0-9]{32}$') {
        throw 'Refusing cleanup outside the exact generated readiness test directory.'
    }
    Remove-Item -LiteralPath $resolvedOwned -Recurse -Force
    if ($OutputPath) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath)))) | Out-Null
        $proof | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    }
}
if (-not $proof.passed) { throw 'Runtime readiness publication is not isolated from its concurrent writer. Inspect the causal regression proof.' }
Write-Output 'Atomic readiness remained unpublished during the held write; all original behavioral probe checks passed.'
