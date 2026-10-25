#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$evidenceRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $evidenceRoot '../..'))
$newManifestPath = Join-Path $evidenceRoot 'final-core-client-unit-build10/manifest.json'
$oldManifestPath = Join-Path $repoRoot 'artifacts/winui-delete-validation-20261025/final-unit/manifest.json'
$oldAnchorPath = Join-Path $repoRoot 'artifacts/winui-delete-validation-20261025/full-unit-production-anchor.json'
$outputPath = Join-Path $evidenceRoot 'full-core-client-build10-production-anchor.json'
if (Test-Path -LiteralPath $outputPath) { throw 'Refusing to overwrite the independent anchor.' }
function Get-Sha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Get-CanonicalDigest([object[]]$Inventory) {
    $serialized = $Inventory | ConvertTo-Json -Compress -Depth 3
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($serialized)))
}
function Assert-Inventories([string]$ManifestPath, [object]$Manifest) {
    if ($Manifest.state -ne 'passed' -or -not $Manifest.buildRequested -or $Manifest.sourceChangedDuringRun) { throw "Invalid accepted source/build state: $ManifestPath" }
    $result = @{}
    foreach ($phase in @('Before', 'After')) {
        $reference = $Manifest."source$phase"
        $path = Join-Path (Split-Path $ManifestPath) $reference.inventory
        $inventory = @(Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
        if ($inventory.Count -ne $reference.fileCount -or @(($inventory.path | Sort-Object -Unique)).Count -ne $inventory.Count) { throw "Invalid inventory cardinality: $path" }
        if ((Get-CanonicalDigest $inventory) -ne $reference.sha256) { throw "Inventory digest mismatch: $path" }
        $result[$phase] = $inventory
    }
    if ($Manifest.sourceBefore.sha256 -ne $Manifest.sourceAfter.sha256) { throw 'Inventory changed during the accepted run.' }
    return $result
}
function Is-TestSource([string]$Path) {
    return ($Path -notmatch '(^|/)(artifacts|bin|obj|node_modules|packages)/' -and
        ($Path -match '\.(cs|xaml|csproj|fsproj|sln|props|targets|ps1|psm1|psd1|py|js|ts|sql)$' -or
         $Path -match '^config/.*\.json$' -or $Path -match '^\.github/workflows/.*\.ya?ml$' -or $Path -eq '.editorconfig'))
}
function Compare-Inventories([object[]]$Baseline, [object[]]$Current) {
    $a = @{}; $b = @{}
    foreach ($entry in $Baseline) { $a[$entry.path] = $entry.sha256 }
    foreach ($entry in $Current) { $b[$entry.path] = $entry.sha256 }
    return [ordered]@{
        changed = @($a.Keys | Where-Object { $b.ContainsKey($_) -and $a[$_] -ne $b[$_] } | Sort-Object)
        missing = @($a.Keys | Where-Object { -not $b.ContainsKey($_) } | Sort-Object)
        added = @($b.Keys | Where-Object { -not $a.ContainsKey($_) } | Sort-Object)
    }
}
function Assert-NoDrift([object]$Drift, [string]$Context) {
    if ($Drift.changed.Count -or $Drift.missing.Count -or $Drift.added.Count) { throw "$Context drifted since the accepted test run." }
}
function Get-ProjectProof([string]$ManifestPath, [object]$Result) {
    $trxPath = Join-Path (Split-Path $ManifestPath) $Result.trx
    $trxHash = Get-Sha256 $trxPath
    if ($trxHash -ne $Result.trxSha256 -or $Result.status -ne 'passed' -or $Result.exitCode -ne 0) { throw "TRX or outcome mismatch: $($Result.project)" }
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    $cases = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ([int]$counters.total -ne $Result.counts.total -or [int]$counters.executed -ne $Result.counts.executed -or
        [int]$counters.passed -ne $Result.counts.passed -or [int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0 -or
        $cases.Count -ne $Result.counts.total -or @($cases | Where-Object outcome -ne 'Passed').Count -ne 0) { throw "TRX count mismatch: $($Result.project)" }
    $assemblies = @($Result.assemblies | ForEach-Object {
        $actualHash = Get-Sha256 (Join-Path $repoRoot $_.path)
        if ($actualHash -ne $_.sha256) { throw "Tested assembly changed: $($_.path)" }
        [pscustomobject][ordered]@{ path = $_.path; role = $_.role; sha256 = $actualHash; currentBytesMatchExecutedAssembly = $true }
    })
    return [pscustomobject][ordered]@{ project = $Result.project; passed = [int]$Result.counts.passed; failed = 0; notExecuted = 0;
        trx = [IO.Path]::GetRelativePath($repoRoot, $trxPath).Replace('\','/'); trxSha256 = $trxHash;
        independentlyParsedTrxResultCount = $cases.Count; currentTestedAssemblies = $assemblies }
}
$newManifest = Get-Content -LiteralPath $newManifestPath -Raw | ConvertFrom-Json
$oldManifest = Get-Content -LiteralPath $oldManifestPath -Raw | ConvertFrom-Json
$oldAnchor = Get-Content -LiteralPath $oldAnchorPath -Raw | ConvertFrom-Json
if ((Get-Sha256 $oldManifestPath) -ne $oldAnchor.unitManifestSha256) { throw 'Earlier full-unit manifest differs from its preserved anchor.' }
$newInventories = Assert-Inventories $newManifestPath $newManifest
$oldInventories = Assert-Inventories $oldManifestPath $oldManifest
$currentPaths = @(& git -C $repoRoot ls-files --cached --others --exclude-standard | Sort-Object -Unique | Where-Object { Is-TestSource $_ })
if ($LASTEXITCODE -ne 0) { throw 'Failed to enumerate source files.' }
$currentInventory = @($currentPaths | ForEach-Object {
    $path = Join-Path $repoRoot $_
    if (Test-Path -LiteralPath $path -PathType Leaf) { [ordered]@{ path = $_; sha256 = Get-Sha256 $path } }
})
$completeDrift = Compare-Inventories $newInventories.After $currentInventory
Assert-NoDrift $completeDrift 'Complete current source inventory'
$currentDigest = Get-CanonicalDigest $currentInventory
if ($currentDigest -ne $newManifest.sourceAfter.sha256 -or $currentDigest -ne '9BC818B8741CB814190E3593CC3FB2AF3E44EFD8E73A5963A9A833B3B7AE02D5') { throw 'Unexpected current source fingerprint.' }
$newProofs = @($newManifest.results | ForEach-Object { Get-ProjectProof $newManifestPath $_ })
if (($newProofs | Measure-Object passed -Sum).Sum -ne 2476 -or ($newProofs.project -join ',') -ne 'ClientCore,Client') { throw 'Unexpected latest project selection/count.' }
$reuseProofs = @($oldManifest.results | Where-Object project -in @('Backend', 'Contracts') | ForEach-Object { Get-ProjectProof $oldManifestPath $_ })
if (($reuseProofs | Measure-Object passed -Sum).Sum -ne 2450) { throw 'Unexpected reused project counts.' }
function Is-ReusedSource([string]$Path) {
    return ($Path -match '^(backend|contracts)/' -or $Path -match '^tools/(SAAIA\.AdvancedValidationJobGuard|testing)/' -or
        $Path -match '^Directory\.Build\.(props|targets)$' -or $Path -match '^Directory\.Packages\.(props|targets)$' -or
        $Path -match '^config/.*\.json$' -or $Path -in @('.editorconfig', 'global.json', 'NuGet.Config', 'nuget.config'))
}
$oldReuseInventory = @($oldInventories.After | Where-Object { Is-ReusedSource $_.path })
$currentReuseInventory = @($currentInventory | Where-Object { Is-ReusedSource $_.path })
$reuseDrift = Compare-Inventories $oldReuseInventory $currentReuseInventory
Assert-NoDrift $reuseDrift 'Backend/Contracts source dependency scope'
$changeSinceOld = Compare-Inventories $oldInventories.After $currentInventory
$binaryPaths = @('backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll',
    'client/SAAIA.Client.WinUI/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/SAAIA.Client.WinUI.dll',
    'client/SAAIA.Client.WinUI/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/SAAIA.Client.WinUI.exe',
    'client/SAAIA.Client.Core/bin/x64/Release/net8.0/SAAIA.Client.Core.dll',
    'contracts/SAAIA.Contracts/bin/x64/Release/net8.0/SAAIA.Contracts.dll')
$binaries = @($binaryPaths | ForEach-Object {
    $file = Get-Item -LiteralPath (Join-Path $repoRoot $_)
    [pscustomobject][ordered]@{ path = $_; sha256 = Get-Sha256 $file.FullName; bytes = $file.Length; lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString('o') }
})
$native = $binaries | Where-Object path -match 'WinUI\.dll$'
if ($native.sha256 -ne '579E5F02D869E1780D86248B67EE762405D8F156452068F14BF7F306CDCBEB7F') { throw 'Native production build10 DLL mismatch.' }
$backend = $binaries | Where-Object path -match '^backend/'
$testedBackend = $reuseProofs | Where-Object project -eq Backend | Select-Object -ExpandProperty currentTestedAssemblies | Where-Object path -match 'SAAIA\.Backend\.dll$'
$fixture = Get-Content -LiteralPath (Join-Path $evidenceRoot 'local-fixture/environment.json') -Raw | ConvertFrom-Json
if ($backend.sha256 -ne $testedBackend.sha256 -or $backend.sha256 -ne $fixture.backendAssemblySha256) { throw 'Runtime/production/tested backend mismatch.' }
$buildLogPath = Join-Path $evidenceRoot 'production-client-build10.log'
$buildLog = Get-Content -LiteralPath $buildLogPath -Raw
if ($buildLog -notmatch 'La génération a réussi\.' -or $buildLog -notmatch '(?m)^\s*0 Avertissement\(s\)\s*$' -or
    $buildLog -notmatch '(?m)^\s*0 Erreur\(s\)\s*$' -or $buildLog -notmatch 'Temps écoulé 00:00:21\.90') { throw 'Build10 success summary mismatch.' }
$proof = [ordered]@{
    schemaVersion = 'saaia-core-client-build10-production-anchor.v1'; capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o');
    status = 'INDEPENDENTLY_VERIFIED_SOURCE_TRX_AND_PRODUCTION_ANCHOR';
    verifier = [ordered]@{ path = [IO.Path]::GetRelativePath($repoRoot, $PSCommandPath).Replace('\','/'); sha256 = Get-Sha256 $PSCommandPath };
    currentCompleteSourceFingerprint = $currentDigest; currentCompleteSourceFileCount = $currentInventory.Count;
    latestRun = [ordered]@{ manifest = [IO.Path]::GetRelativePath($repoRoot, $newManifestPath).Replace('\','/'); manifestSha256 = Get-Sha256 $newManifestPath;
        runId = $newManifest.runId; buildRequested = $newManifest.buildRequested; sourceChangedDuringRun = $false;
        sourceBeforeAndAfterDigestsIndependentlyRecomputed = $true; currentCompleteSourcesMatch = $true;
        startedAtUtc = $newManifest.startedAtUtc; finishedAtUtc = $newManifest.finishedAtUtc; passed = 2476; projects = $newProofs; sourceDrift = $completeDrift };
    reusedEarlierResults = [ordered]@{ manifest = [IO.Path]::GetRelativePath($repoRoot, $oldManifestPath).Replace('\','/'); manifestSha256 = Get-Sha256 $oldManifestPath;
        preservedAnchor = [IO.Path]::GetRelativePath($repoRoot, $oldAnchorPath).Replace('\','/'); preservedAnchorSha256 = Get-Sha256 $oldAnchorPath;
        runId = $oldManifest.runId; originalRunPassed = 4923; reusedPassedOnly = 2450; projects = $reuseProofs;
        sourceDependencyScope = 'backend/**, contracts/**, tools/SAAIA.AdvancedValidationJobGuard/**, tools/testing/**, shared Directory.Build/Packages files, config JSON, editorconfig; selected using the same source extensions as the original campaign';
        sourceFileCount = $oldReuseInventory.Count; sourceFingerprint = Get-CanonicalDigest $oldReuseInventory; currentSourceScopeUnchanged = $true; sourceDrift = $reuseDrift;
        limitation = 'These 2430 Backend and20 Contracts tests were executed in the earlier full run, not re-executed after the client-only correction. Their source dependency scope and recorded tested DLL bytes are independently unchanged.' };
    sourceDeltaSinceEarlierFullRun = $changeSinceOld;
    composedCoverage = [ordered]@{ currentClientCore = 80; currentClient = 2396; reusedBackend = 2430; reusedContracts = 20; totalDistinctCasesAcrossProjectRuns = 4926; singleRun = $false;
        explanation = '2476 newly re-executed Client/Core tests plus2450 earlier Backend/Contracts tests retained under identical source scopes and recorded DLL bytes. The163 targeted regressions and3 new tests overlap the full client result and are not added.' };
    productionBuild10 = [ordered]@{ log = 'production-client-build10.log'; logSha256 = Get-Sha256 $buildLogPath; warnings = 0; errors = 0; elapsed = '00:00:21.90'; currentProductionBinaries = $binaries;
        sourceAnchor = 'The production build10 source tree is identical to the complete stable latest unit inventory. AnyCPU testhooks DLLs and Release x64 native DLLs have different build configurations and are not claimed byte-identical.';
        nativeDllMatchesExpectedBuild10 = $true; backendRuntimeRunId = $fixture.runId; testedBackendMatchesCurrentProductionAndFixtureManifest = $true };
    limits = @('This artifact performs file/TRX/source verification only; no tests, native UI or model calls are executed by the verifier.',
        'Unit suites exclude IntegrationPg, LiveLocal and LivePaid; their results cannot substitute for native UI or paid-model acceptance.',
        'Native build10 launch and real journey observations require their own process, screenshot, diagnostic and durable-state evidence.',
        'Inventory verification uses the source selection of tools/test-project.ps1; excluded JSON fixtures, installed packages and untracked ignored files are not retroactively added to the original source fingerprint.');
    productStatus = 'TESTE_NON_APPROUVE'
}
$proof | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $outputPath -Encoding utf8
[pscustomobject]@{ proof = $outputPath; sourceSha256 = $currentDigest; sourceFileCount = $currentInventory.Count; newPassed = 2476; reusedPassed = 2450; composedCoverage = 4926; singleRun = $false; nativeDllSha256 = $native.sha256; backendSha256 = $backend.sha256 } | ConvertTo-Json
