[CmdletBinding()]
param(
    [string]$LocalAppDataRoot = $env:LOCALAPPDATA,
    [string]$OutputPath = "",
    [switch]$AsJson
)

$ErrorActionPreference = "Stop"

$governanceRoot = Join-Path $LocalAppDataRoot "SAAIA\governance"
$llmRoot = Join-Path $LocalAppDataRoot "SAAIA\llm"
$runtimeRoot = Join-Path $llmRoot "runtime"

$expectedGovernanceFiles = @(
    "model_catalog.json",
    "model_collections.json",
    "model_policy.json",
    "model_sources.json",
    "warmup_profiles.json",
    "warmup_results.json",
    "hardware_probe.json",
    "last_known_good_profile.json",
    "blacklist.json",
    "rollback_log.json",
    "blacklist_applied.json",
    "capability_state.json",
    "acquisition_log.json",
    "battery_policies.json",
    "runtime_compatibility_policy.json",
    "runtime_event_log.json"
)

$expectedLlmFiles = @(
    "runtime\\active-runtime.json"
)

function Get-Sha256Hex {
    param([string]$Path)
    $text = [System.IO.File]::ReadAllText($Path)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hashBytes)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Test-GovernanceArtifact {
    param(
        [string]$Root,
        [string]$RelativePath
    )

    $fullPath = Join-Path $Root $RelativePath
    $sidecarPath = $fullPath + ".sha256"

    $exists = Test-Path -LiteralPath $fullPath -PathType Leaf
    $sidecarExists = Test-Path -LiteralPath $sidecarPath -PathType Leaf
    $checksumStatus = "not_checked"
    $detail = $null

    if ($exists -and $sidecarExists) {
        try {
            $expected = ([System.IO.File]::ReadAllText($sidecarPath)).Trim().ToLowerInvariant()
            $actual = Get-Sha256Hex -Path $fullPath
            if ($expected -eq $actual) {
                $checksumStatus = "ok"
            }
            else {
                $checksumStatus = "mismatch"
                $detail = "expected=$expected actual=$actual"
            }
        }
        catch {
            $checksumStatus = "error"
            $detail = $_.Exception.Message
        }
    }
    elseif ($exists) {
        $checksumStatus = "missing_sidecar"
    }
    else {
        $checksumStatus = "missing_file"
    }

    [pscustomobject]@{
        relativePath = $RelativePath
        fullPath = $fullPath
        exists = $exists
        sidecarExists = $sidecarExists
        checksumStatus = $checksumStatus
        detail = $detail
    }
}

$governanceResults = @(
foreach ($file in $expectedGovernanceFiles) {
    Test-GovernanceArtifact -Root $governanceRoot -RelativePath $file
})

$llmResults = @(
foreach ($file in $expectedLlmFiles) {
    Test-GovernanceArtifact -Root $llmRoot -RelativePath $file
})

$summary = [pscustomobject]@{
    timestamp = [DateTimeOffset]::Now.ToString("o")
    governanceRoot = $governanceRoot
    llmRoot = $llmRoot
    governance = [pscustomobject]@{
        expectedCount = $governanceResults.Count
        presentCount = @($governanceResults | Where-Object { $_.exists }).Count
        fullyVerifiedCount = @($governanceResults | Where-Object { $_.checksumStatus -eq "ok" }).Count
        missingCount = @($governanceResults | Where-Object { -not $_.exists }).Count
        missingSidecarCount = @($governanceResults | Where-Object { $_.checksumStatus -eq "missing_sidecar" }).Count
        mismatchCount = @($governanceResults | Where-Object { $_.checksumStatus -eq "mismatch" }).Count
        items = $governanceResults
    }
    llm = [pscustomobject]@{
        expectedCount = $llmResults.Count
        presentCount = @($llmResults | Where-Object { $_.exists }).Count
        fullyVerifiedCount = @($llmResults | Where-Object { $_.checksumStatus -eq "ok" }).Count
        missingCount = @($llmResults | Where-Object { -not $_.exists }).Count
        missingSidecarCount = @($llmResults | Where-Object { $_.checksumStatus -eq "missing_sidecar" }).Count
        mismatchCount = @($llmResults | Where-Object { $_.checksumStatus -eq "mismatch" }).Count
        items = $llmResults
    }
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputDir = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
        New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    }

    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $OutputPath -Encoding UTF8
}

if ($AsJson) {
    $summary | ConvertTo-Json -Depth 8
    exit 0
}

Write-Host "SAAIA governance artifact audit"
Write-Host "Governance root: $governanceRoot"
Write-Host "LLM root:        $llmRoot"
Write-Host ""
Write-Host ("Governance: {0}/{1} present, {2} verified, {3} missing, {4} missing sidecar, {5} mismatch" -f `
    $summary.governance.presentCount,
    $summary.governance.expectedCount,
    $summary.governance.fullyVerifiedCount,
    $summary.governance.missingCount,
    $summary.governance.missingSidecarCount,
    $summary.governance.mismatchCount)
Write-Host ("LLM:        {0}/{1} present, {2} verified, {3} missing, {4} missing sidecar, {5} mismatch" -f `
    $summary.llm.presentCount,
    $summary.llm.expectedCount,
    $summary.llm.fullyVerifiedCount,
    $summary.llm.missingCount,
    $summary.llm.missingSidecarCount,
    $summary.llm.mismatchCount)
Write-Host ""

$problemItems = @($governanceResults + $llmResults | Where-Object { $_.checksumStatus -ne "ok" })
if ($problemItems.Count -eq 0) {
    Write-Host "All expected artifacts are present and verified."
    exit 0
}
else {
    Write-Host "Artifacts requiring attention:"
    $problemItems |
        Select-Object relativePath, exists, sidecarExists, checksumStatus, detail |
        Format-Table -AutoSize
    exit 1
}
