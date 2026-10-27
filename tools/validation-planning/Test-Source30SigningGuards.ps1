#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'sign-source30-runtime.ps1'
$source = Get-Content -LiteralPath $path -Raw -Encoding UTF8
$tokens = $null
$parseErrors = $null
$null = [System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Signing helper syntax invalid.' }

# Exercise the actual pure configuration-guard section only. No config or key is read.
$lines = $source -split "`n"
$start = 0
$end = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].StartsWith('$value = Get-Content')) { $start = $i + 1 }
    if ($lines[$i].StartsWith('$proofPath =')) { $end = $i }
}
if ($start -eq 0 -or $end -le $start) { throw 'Actual configuration guard section missing.' }
$guards = [scriptblock]::Create(($lines[$start..($end - 1)] -join "`n"))
function New-SyntheticConfig {
    return '{"DocumentIntelligence":{"RegionalTableAlternativeReadingsEnabled":true,"NativeTypographyObservationsEnabled":true,"DoclingInputGeometryCaptureEnabled":true,"RegionalTextRepairEnabled":true,"DeploymentRevision":"8ddd79a4226cfcb9f678414a76302a1dc8ab614a4267b237c1cd3e26d62218c0","TimeoutSeconds":3600,"NumThreads":3,"Device":"cpu"},"AdvancedAnalysis":{"VisualSourceCitationsEnabled":true,"NativeObservationReadEnabled":true,"WorkerEnabled":false,"Provider":"disabled","AllowExternalProviderContent":false,"AllowExternalProviderMetadata":false},"License":{"AdvancedAnalysisEnabled":false},"RuntimeGovernance":{"CapabilityBWorkerEnabled":false},"Ingestion":{"WorkerEnabled":true,"CanonicalArtifactsEnabled":true,"WatcherEnabled":false,"ScannerEnabled":false},"Rag":{"QdrantCollection":"ingestion_dfd06e7de93a4b478b433b7fb58562c6"}}' | ConvertFrom-Json
}
function Invoke-ActualGuards($value) { & $guards }
$checks = 0
Invoke-ActualGuards (New-SyntheticConfig)
$checks++
foreach ($bad in @($null,900,3900,'3600',$true)) {
    $value=New-SyntheticConfig
    $value.DocumentIntelligence.TimeoutSeconds=$bad
    $failed=$false
    try { Invoke-ActualGuards $value } catch { $failed=$true }
    if(-not $failed){throw 'Wrong source30 signed document timeout admitted.'}
    $checks++
}
$enabled = @(
    @{ Group = 'DocumentIntelligence'; Property = 'RegionalTableAlternativeReadingsEnabled' }
    @{ Group = 'DocumentIntelligence'; Property = 'NativeTypographyObservationsEnabled' }
    @{ Group = 'DocumentIntelligence'; Property = 'DoclingInputGeometryCaptureEnabled' }
    @{ Group = 'DocumentIntelligence'; Property = 'RegionalTextRepairEnabled' }
    @{ Group = 'Ingestion'; Property = 'WorkerEnabled' }
    @{ Group = 'Ingestion'; Property = 'CanonicalArtifactsEnabled' }
    @{ Group = 'AdvancedAnalysis'; Property = 'VisualSourceCitationsEnabled' }
    @{ Group = 'AdvancedAnalysis'; Property = 'NativeObservationReadEnabled' }
)
foreach ($property in $enabled) {
    foreach ($bad in @($null, $false, 'true', 1)) {
        $value = New-SyntheticConfig
        $value.($property.Group).($property.Property) = $bad
        $refused = $false
        try { Invoke-ActualGuards $value } catch { $refused = $true }
        if (-not $refused) { throw 'Non-Boolean-true activation accepted.' }
        $checks++
    }
}
$disabled = @(
    @{ Group = 'AdvancedAnalysis'; Property = 'WorkerEnabled' }
    @{ Group = 'AdvancedAnalysis'; Property = 'AllowExternalProviderContent' }
    @{ Group = 'AdvancedAnalysis'; Property = 'AllowExternalProviderMetadata' }
    @{ Group = 'License'; Property = 'AdvancedAnalysisEnabled' }
    @{ Group = 'RuntimeGovernance'; Property = 'CapabilityBWorkerEnabled' }
    @{ Group = 'Ingestion'; Property = 'WatcherEnabled' }
    @{ Group = 'Ingestion'; Property = 'ScannerEnabled' }
)
foreach ($property in $disabled) {
    foreach ($bad in @($null, $true, 'false', 0, 1)) {
        $value = New-SyntheticConfig
        $value.($property.Group).($property.Property) = $bad
        $refused = $false
        try { Invoke-ActualGuards $value } catch { $refused = $true }
        if (-not $refused) { throw 'Non-Boolean-false disabled channel accepted.' }
        $checks++
    }
}
$value = New-SyntheticConfig
$value.AdvancedAnalysis.Provider = 'other'
$refused = $false
try { Invoke-ActualGuards $value } catch { $refused = $true }
if (-not $refused) { throw 'Enabled provider accepted.' }
$checks++
foreach ($bad in @($null, 2, '3', 3.0, $true, $false)) {
    $value = New-SyntheticConfig
    $value.DocumentIntelligence.NumThreads = $bad
    $refused = $false
    try { Invoke-ActualGuards $value } catch { $refused = $true }
    if (-not $refused) { throw 'Wrong CPU28 signed thread profile accepted.' }
    $checks++
}
$value = New-SyntheticConfig
$value.DocumentIntelligence.Device = 'gpu'
$refused = $false
try { Invoke-ActualGuards $value } catch { $refused = $true }
if (-not $refused) { throw 'Wrong CPU28 signed device accepted.' }
$checks++
foreach ($pin in @('dfd06e7d-e93a-4b47-8b43-3b7fb58562c6', '6b5611d20b016b81b660b99abe23a2e2bf5c051c23aa97a5fa06dcef565385d8', 'ab23094c021de13361be19692964e56786bd4bee77534188a8ddca2728c78b30', '8ddd79a4226cfcb9f678414a76302a1dc8ab614a4267b237c1cd3e26d62218c0')) {
    if (-not $source.Contains($pin)) { throw 'Actual source30 identity pin missing.' }
    $checks++
}
$plan = (& $path | ConvertFrom-Json)
if ($plan.executed -ne $false -or $plan.inputFilesRead -ne $false -or $plan.privateKeyRead -ne $false -or $plan.signatureCreated -ne $false) {
    throw 'Default invocation performed an action.'
}
$checks++
[ordered]@{ checksPassed = $checks; actualGuardSectionTested = $true; sourceSyntaxValid = $true; fullDefaultNoExecutePassed = $true; configurationOrKeyRead = $false; signingExecuted = $false; serverExecuted = $false } | ConvertTo-Json -Compress
