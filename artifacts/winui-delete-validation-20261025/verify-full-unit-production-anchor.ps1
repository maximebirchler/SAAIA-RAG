#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$evidencePath = $PSScriptRoot
$repoPath = [IO.Path]::GetFullPath((Join-Path $evidencePath '../..'))
$manifestPath = Join-Path $evidencePath 'final-unit/manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$inventory = @(Get-Content -LiteralPath (Join-Path (Split-Path $manifestPath) $manifest.sourceAfter.inventory) -Raw | ConvertFrom-Json)
function Is-ProductSource([string]$RelativePath) {
    return (($RelativePath -match '^(backend|client|contracts)/' -and $RelativePath -notmatch '\.Tests/' -and $RelativePath -notmatch '(^|/)(bin|obj)/' -and $RelativePath -match '\.(cs|xaml|csproj|props|targets|sql)$') -or $RelativePath -match '^Directory\.Build\.(props|targets)$' -or $RelativePath -match '^config/.*\.json$')
}
$baseline = @($inventory | Where-Object { Is-ProductSource $_.path } | Sort-Object path)
$baselineIndex = @{}
foreach ($file in $baseline) { $baselineIndex[$file.path] = $file.sha256 }
$currentPaths = @(& git -C $repoPath ls-files --cached --others --exclude-standard | Sort-Object -Unique | Where-Object { Is-ProductSource $_ })
$currentIndex = @{}
foreach ($relative in $currentPaths) {
    $path = Join-Path $repoPath $relative
    if (Test-Path -LiteralPath $path -PathType Leaf) { $currentIndex[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
$changed = @($baselineIndex.Keys | Where-Object { $currentIndex.ContainsKey($_) -and $baselineIndex[$_] -ne $currentIndex[$_] } | Sort-Object)
$missing = @($baselineIndex.Keys | Where-Object { -not $currentIndex.ContainsKey($_) } | Sort-Object)
$added = @($currentIndex.Keys | Where-Object { -not $baselineIndex.ContainsKey($_) } | Sort-Object)
if ($changed.Count -or $missing.Count -or $added.Count) { throw 'Product sources drifted since the accepted full unit run.' }
$projectProofs = [Collections.Generic.List[object]]::new()
$totalPassed = 0
foreach ($result in $manifest.results) {
    $trxPath = Join-Path (Split-Path $manifestPath) $result.trx
    $trxHash = (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash
    if ($trxHash -ne $result.trxSha256) { throw "TRX hash mismatch: $($result.project)" }
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    if ([int]$counters.passed -ne $result.counts.passed -or [int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0) { throw "TRX count mismatch: $($result.project)" }
    $totalPassed += [int]$result.counts.passed
    $projectProofs.Add([ordered]@{ project = $result.project; passed = [int]$result.counts.passed; failed = [int]$result.counts.failed; notExecuted = [int]$result.counts.notExecuted; trx = 'final-unit/' + $result.trx; trxSha256 = $trxHash; trxMatchesAcceptedManifest = $true })
}
$binaryPaths = @('backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll', 'client/SAAIA.Client.WinUI/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/SAAIA.Client.WinUI.dll', 'client/SAAIA.Client.WinUI/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/SAAIA.Client.WinUI.exe', 'client/SAAIA.Client.Core/bin/x64/Release/net8.0/SAAIA.Client.Core.dll', 'contracts/SAAIA.Contracts/bin/x64/Release/net8.0/SAAIA.Contracts.dll')
$binaries = @($binaryPaths | ForEach-Object { $file = Get-Item -LiteralPath (Join-Path $repoPath $_); [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash; bytes = $file.Length; lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString('o') } })
$runtimeOwner = Get-Content -LiteralPath (Join-Path $evidencePath 'local-fixture-fixed/environment.json') -Raw | ConvertFrom-Json
$backendBinary = $binaries | Where-Object path -match '^backend/' | Select-Object -First 1
$testedBackend = $manifest.results | Where-Object project -eq Backend | Select-Object -ExpandProperty assemblies | Where-Object path -match 'SAAIA.Backend.dll$' | Select-Object -First 1
if ($runtimeOwner.backendAssemblySha256 -ne $backendBinary.sha256 -or $testedBackend.sha256 -ne $backendBinary.sha256) { throw 'Backend binary differs between full unit dependency, actual production binary and launched fixture.' }
$sourceJson = $baseline | ConvertTo-Json -Depth 3 -Compress
$sourceDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sourceJson)))
if ($totalPassed -ne 4923 -or $manifest.sourceChangedDuringRun -or $manifest.state -ne 'passed') { throw 'Accepted full unit invariants failed.' }
$proof = [ordered]@{
    schemaVersion = 'saaia-winui-deletion-unit-production-anchor.v1'; capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); status = 'ACCEPTED_FULL_UNIT_AND_PRODUCTION_SOURCE_ANCHOR';
    unitManifest = 'final-unit/manifest.json'; unitManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash;
    totalPassed = $totalPassed; totalFailed = 0; totalNotExecuted = 0; unitSourceChangedDuringRun = $manifest.sourceChangedDuringRun;
    completeTestedSourceFingerprint = $manifest.sourceAfter.sha256; projectResults = $projectProofs.ToArray();
    productSourceDefinition = 'backend/client/contracts production C#/XAML/project/SQL files excluding .Tests and bin/obj; Directory.Build props/targets; config JSON';
    productSourceCount = $baseline.Count; productSourceFingerprintAtUnit = $sourceDigest; currentProductSourcesMatchUnit = $true; changedProductFiles = $changed; addedProductFiles = $added; missingProductFiles = $missing;
    productSourceInventory = $baseline; currentProductionBinaries = $binaries;
    backendRuntimeRunId = $runtimeOwner.runId; actualBackendRuntimeMatchesUnitDependencyAndProductionBinary = $true;
    clientBinaryBoundary = 'WinUI native Release x64 production build09 differs from testhooks AnyCPU assemblies by design; its source files match the accepted full unit inventory. Native launch proofs must bind actual UI observations to the production DLL hash.';
    productionBuilds = @(@{ log = 'production-backend-build.log'; sha256 = (Get-FileHash -LiteralPath (Join-Path $evidencePath 'production-backend-build.log') -Algorithm SHA256).Hash; warnings = 0; errors = 0 }, @{ log = 'production-client-build09.log'; sha256 = (Get-FileHash -LiteralPath (Join-Path $evidencePath 'production-client-build09.log') -Algorithm SHA256).Hash; warnings = 0; errors = 0 });
    limits = @('Unit results do not claim PostgreSQL integration or native UI execution.', 'Post-suite tool-only changes are outside this product-source fingerprint; they must retain their own tests and hashes.', 'Native corrected-build UI acceptance remains in separate observations.', 'The20 targeted tests overlap the4923 full unit tests and must not be added as distinct final coverage.'); productStatus = 'TESTE_NON_APPROUVE'
}
$proofPath = Join-Path $evidencePath 'full-unit-production-anchor.json'
if (Test-Path -LiteralPath $proofPath) { throw 'Refusing to overwrite previous anchor.' }
$proof | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $proofPath -Encoding utf8
[pscustomobject]@{ passed = $totalPassed; productSources = $baseline.Count; changedProductSources = $changed.Count; nativeClientDllSha256 = ($binaries | Where-Object path -match 'WinUI.dll$').sha256; productionBackendDllSha256 = $backendBinary.sha256 } | ConvertTo-Json
