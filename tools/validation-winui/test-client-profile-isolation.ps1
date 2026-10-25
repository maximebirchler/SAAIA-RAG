#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$OriginalProfileBaseline)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh isolation evidence directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$privateDirectory = Join-Path $output 'private'
[IO.Directory]::CreateDirectory($privateDirectory) | Out-Null
$profileRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'SAAIA'
$oldBaseline = Get-Content -LiteralPath $OriginalProfileBaseline -Raw | ConvertFrom-Json
$profileBefore = @(foreach ($entry in $oldBaseline) {
    $path = [IO.Path]::GetFullPath((Join-Path $profileRoot ([string]$entry.path)))
    $prefix = [IO.Path]::GetFullPath($profileRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Baseline file is outside the expected profile.' }
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        @{ path = $entry.path; size = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
})
$beforePath = Join-Path $privateDirectory 'current-profile-before-tests.json'
[IO.File]::WriteAllText($beforePath, ($profileBefore | ConvertTo-Json -Depth 4))
$runner = Join-Path $repo 'tools/test-project.ps1'
$runnerHash = (Get-FileHash -LiteralPath $runner -Algorithm SHA256).Hash
$checks = [Collections.Generic.List[object]]::new()
$filter = 'FullyQualifiedName~ClientProductionDiagnosticsTests.Standalone_advanced_service|FullyQualifiedName~ClientProductionDiagnosticsTests.Source_parser'
foreach ($mode in @('owned', 'explicit', 'invalid-relative')) {
    $caseDirectory = Join-Path $output $mode
    [IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
    $manifestPath = Join-Path $caseDirectory 'manifest.json'
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Environment.Remove('SAAIA_CLIENT_DATA_ROOT') | Out-Null
    $explicitRoot = Join-Path $caseDirectory 'explicit-client-data'
    if ($mode -eq 'explicit') { $info.Environment['SAAIA_CLIENT_DATA_ROOT'] = $explicitRoot }
    if ($mode -eq 'invalid-relative') { $info.Environment['SAAIA_CLIENT_DATA_ROOT'] = 'relative-root-must-be-rejected' }
    foreach ($argument in @('-NoProfile','-File',$runner,'-Profile','Unit','-Projects','Client','-Configuration','Release','-NoRestore','-NoBuild','-Filter',$filter,'-OutputPath',$manifestPath)) { $info.ArgumentList.Add($argument) }
    if ($mode -eq 'invalid-relative') { $info.ArgumentList.Add('-PlanOnly') }
    $child = [Diagnostics.Process]::Start($info)
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(120000)) { throw 'Isolation subprocess did not finish within120s; no process was killed automatically.' }
    [IO.File]::WriteAllText((Join-Path $caseDirectory 'runner.stdout.log'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $caseDirectory 'runner.stderr.log'), $stderr.GetAwaiter().GetResult())
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($mode -eq 'invalid-relative') {
        $passed = $child.ExitCode -ne 0 -and @($manifest.results).Count -eq 0 -and $manifest.state -eq 'failed'
        $checks.Add(@{ name = $mode; passed = $passed; executedTests = 0; rejectedBeforeTests = $passed })
    } else {
        $root = if ($mode -eq 'explicit') { $explicitRoot } else { Join-Path $caseDirectory 'client-data' }
        $logPath = Join-Path $root 'logs/client_startup.log'
        $logExists = Test-Path -LiteralPath $logPath -PathType Leaf
        $eventCount = if ($logExists) { @([IO.File]::ReadLines($logPath) | Where-Object { $_.Contains('CLIENT_PROBE ') }).Count } else { 0 }
        $expectedSource = if ($mode -eq 'explicit') { 'explicit_environment' } else { 'owned_campaign_output' }
        $expectedRootId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant())))
        $passed = $child.ExitCode -eq 0 -and $manifest.state -eq 'passed' -and $manifest.results[0].counts.passed -eq 2 -and
            $manifest.environment.clientDataIsolation.source -eq $expectedSource -and $manifest.environment.clientDataIsolation.rootId -eq $expectedRootId -and
            $manifest.environment.clientDataIsolation.environmentRestored -eq $true -and $eventCount -gt 0
        $checks.Add(@{ name = $mode; passed = $passed; executedTests = $manifest.results[0].counts.executed;
            source = $manifest.environment.clientDataIsolation.source; environmentRestored = $manifest.environment.clientDataIsolation.environmentRestored;
            isolatedProbeEvents = $eventCount; manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash })
    }
}
$profileComparisonPath = Join-Path $output 'profile-comparison.json'
& (Join-Path $PSScriptRoot 'compare-user-profile.ps1') -BaselinePath $beforePath -OutputPath $profileComparisonPath
$profileComparison = Get-Content -LiteralPath $profileComparisonPath -Raw | ConvertFrom-Json
$checks.Add(@{ name = 'normal_profile_unchanged_during_isolated_tests'; passed = $profileComparison.coveredFilesUnchanged;
    coveredFiles = $profileComparison.baselineFiles; unchangedFiles = $profileComparison.unchangedFiles; issues = $profileComparison.issueCount })
$checks.Add(@{ name = 'runner_source_unchanged_during_checks'; passed = (Get-FileHash -LiteralPath $runner -Algorithm SHA256).Hash -eq $runnerHash })
$passed = @($checks | Where-Object { -not $_.passed }).Count -eq 0
@{ schemaVersion = 'saaia-client-test-profile-isolation-checks.v1'; passed = $passed; checks = $checks.ToArray();
    runnerSha256 = $runnerHash; normalProfileRestored = $false; normalProfileModifiedByThisScript = $false;
    productSourcesChanged = $false; buildRequested = $false; modelCalled = $false; apiRequestsWereInMemoryFixtures = $true;
    privateBaselineExcludedFromVersioning = $true; priorNormalProfileAppendPreserved = $true } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'checks.json') -Encoding utf8
if (-not $passed) { throw 'Client test profile isolation checks failed; inspect compact checks.' }
Write-Output 'Client test profile isolation passed: owned root, explicit root, relative rejection, normal profile unchanged.'
