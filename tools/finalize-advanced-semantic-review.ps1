[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReviewArtifactDirectory,
    [switch]$DiagnosticMode
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Require-ReviewFile {
    param([string]$Directory, [string]$Name)
    $path = Join-Path $Directory $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required semantic review file is missing: $Name"
    }
    return $path
}

function Require-ReviewText {
    param([object]$Value, [string]$Name)
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "$Name is required."
    }
    return $text.Trim()
}

function Get-FileSha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$ReviewArtifactDirectory = [System.IO.Path]::GetFullPath($ReviewArtifactDirectory)
if (-not (Test-Path -LiteralPath $ReviewArtifactDirectory -PathType Container)) {
    throw "Semantic review artifact directory was not found: $ReviewArtifactDirectory"
}

$manifestPath = Require-ReviewFile $ReviewArtifactDirectory "manifest.public.json"
$bundlePath = Require-ReviewFile $ReviewArtifactDirectory "evidence-bundle.private.json"
$reviewPath = Require-ReviewFile $ReviewArtifactDirectory "semantic-review.private.md"
$mapPath = Require-ReviewFile $ReviewArtifactDirectory "campaign-map.private.json"
$jobIdsPath = Require-ReviewFile $ReviewArtifactDirectory "job-ids.private.json"
$decisionPath = Require-ReviewFile $ReviewArtifactDirectory "semantic-decisions.private.json"
$assessmentName = if ($DiagnosticMode) {
    "semantic-diagnostic.public.json"
} else {
    "semantic-assessment.public.json"
}
$assessmentPath = Join-Path $ReviewArtifactDirectory $assessmentName
if (Test-Path -LiteralPath $assessmentPath) {
    throw "A semantic assessment already exists. Preserve it and use a new review artifact directory."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.schemaVersion -ne "saaia-advanced-semantic-review-public-manifest-v1") {
    throw "Unsupported semantic review manifest schema."
}
if ([bool]$manifest.privateArtifactsMayLeaveWorkspace) {
    throw "Private semantic review artifacts may not leave the workspace."
}
if ($DiagnosticMode) {
    if ([bool]$manifest.approvalEligible -or
        [string]$manifest.reviewMode -ne "DIAGNOSTIC_ONLY" -or
        [string]$manifest.semanticVerdict -ne "PENDING_DIAGNOSTIC_REVIEW" -or
        [string]::IsNullOrWhiteSpace(
            [string]$manifest.campaignRepositoryCommit) -or
        [string]::IsNullOrWhiteSpace(
            [string]$manifest.campaignExecutionState)) {
        throw "The review manifest is not eligible for a diagnostic decision."
    }
} elseif (($null -ne $manifest.PSObject.Properties["approvalEligible"] -and
        -not [bool]$manifest.approvalEligible) -or
    [string]$manifest.semanticVerdict -ne "PENDING_HUMAN_REVIEW") {
    throw "The review manifest is not eligible for an acceptance decision."
}
if ((Get-FileSha256 $bundlePath) -ne [string]$manifest.privateBundleSha256 -or
    (Get-FileSha256 $reviewPath) -ne [string]$manifest.privateReviewSha256) {
    throw "The private evidence or review text changed after packet preparation."
}

$repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
$trackedDirty = @(& git -C $repositoryRoot status --porcelain --untracked-files=no 2>$null).Count -gt 0
$reviewPreparationCommit = [string]$manifest.repositoryCommit
if ($trackedDirty) {
    throw "Semantic decisions must be finalized from a clean repository state."
}
if ($DiagnosticMode) {
    & git -C $repositoryRoot merge-base --is-ancestor `
        $reviewPreparationCommit $repositoryCommit 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Diagnostic decisions must be finalized from the review preparation commit or a clean descendant."
    }
} elseif ($repositoryCommit -ne $reviewPreparationCommit) {
    throw "Semantic decisions must be finalized from the exact clean campaign commit."
}

$campaignMapDocument = Get-Content -LiteralPath $mapPath -Raw | ConvertFrom-Json
$jobIdsDocument = Get-Content -LiteralPath $jobIdsPath -Raw | ConvertFrom-Json
$campaignMap = @($campaignMapDocument)
$expectedJobIds = @($jobIdsDocument)
$decisionDocument = Get-Content -LiteralPath $decisionPath -Raw | ConvertFrom-Json
if ([string]$decisionDocument.schemaVersion -ne "saaia-advanced-semantic-decisions-v1") {
    throw "Unsupported semantic decision schema."
}
if ([string]$decisionDocument.repositoryCommit -ne $reviewPreparationCommit) {
    throw "Semantic decisions do not target the campaign commit."
}
$decisions = @($decisionDocument.decisions)
$expectedRows = [int]$manifest.expectedRows
if ($campaignMap.Count -ne $expectedRows -or
    $expectedJobIds.Count -ne $expectedRows -or
    $decisions.Count -ne $expectedRows) {
    throw "Semantic decision count does not match the prepared campaign."
}

$mapByJob = @{}
foreach ($record in $campaignMap) {
    $jobId = [string]$record.jobId
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse($jobId, [ref]$parsed) -or $mapByJob.ContainsKey($jobId)) {
        throw "Campaign map contains an invalid or duplicate job UUID."
    }
    $mapByJob[$jobId] = $record
}
foreach ($jobIdValue in $expectedJobIds) {
    $jobId = [string]$jobIdValue
    if (-not $mapByJob.ContainsKey($jobId)) {
        throw "Prepared job identity is absent from the campaign map."
    }
}

$seenJobs = @{}
$normalized = @()
foreach ($decision in $decisions) {
    $jobId = Require-ReviewText $decision.jobId "decision.jobId"
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse($jobId, [ref]$parsed) -or
        -not $mapByJob.ContainsKey($jobId) -or
        $seenJobs.ContainsKey($jobId)) {
        throw "Semantic decision contains an unknown, invalid or duplicate job UUID."
    }
    $seenJobs[$jobId] = $true
    $record = $mapByJob[$jobId]
    $caseId = Require-ReviewText $decision.caseId "decision.caseId"
    $repetition = [int]$decision.repetition
    if ($caseId -ne [string]$record.caseId -or $repetition -ne [int]$record.repetition) {
        throw "Semantic decision does not match its prepared case and repetition."
    }
    $verdict = Require-ReviewText $decision.verdict "decision.verdict"
    if ($verdict -notin @("PASS_SEMANTIC", "REJECT_SEMANTIC")) {
        throw "Every semantic decision must be PASS_SEMANTIC or REJECT_SEMANTIC."
    }
    $reason = Require-ReviewText $decision.reason "decision.reason"
    $normalized += [pscustomobject]@{
        jobId = $jobId
        caseId = $caseId
        repetition = $repetition
        verdict = $verdict
        reason = $reason
    }
}
if ($seenJobs.Count -ne $mapByJob.Count) {
    throw "At least one prepared job has no semantic decision."
}

$rejected = @($normalized | Where-Object verdict -eq "REJECT_SEMANTIC")
$unresolvedEvidence = [int]$manifest.unresolvedEvidence
$accepted = $rejected.Count -eq 0 -and $unresolvedEvidence -eq 0
$caseSummary = @($normalized | Group-Object caseId | Sort-Object Name | ForEach-Object {
    $caseRows = @($_.Group)
    [ordered]@{
        caseId = $_.Name
        repetitions = $caseRows.Count
        passed = @($caseRows | Where-Object verdict -eq "PASS_SEMANTIC").Count
        rejected = @($caseRows | Where-Object verdict -eq "REJECT_SEMANTIC").Count
    }
})
$assessment = [ordered]@{
    schemaVersion = if ($DiagnosticMode) {
        "saaia-advanced-semantic-diagnostic-public-v1"
    } else {
        "saaia-advanced-semantic-assessment-public-v1"
    }
    assessedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    repositoryCommit = $repositoryCommit
    reviewPreparationCommit = $reviewPreparationCommit
    campaignRepositoryCommit = $(if ($DiagnosticMode) {
        [string]$manifest.campaignRepositoryCommit
    } else { $null })
    campaignExecutionState = $(if ($DiagnosticMode) {
        [string]$manifest.campaignExecutionState
    } else { $null })
    reviewManifestSha256 = (Get-FileSha256 $manifestPath)
    privateEvidenceBundleSha256 = (Get-FileSha256 $bundlePath)
    privateReviewSha256 = (Get-FileSha256 $reviewPath)
    privateDecisionsSha256 = (Get-FileSha256 $decisionPath)
    reviewedRows = $normalized.Count
    passedRows = @($normalized | Where-Object verdict -eq "PASS_SEMANTIC").Count
    rejectedRows = $rejected.Count
    unresolvedEvidence = $unresolvedEvidence
    verdict = if ($DiagnosticMode) {
        if ($accepted) {
            "DIAGNOSTIC_ROWS_ALL_PASS"
        } else {
            "DIAGNOSTIC_ROWS_REJECTED"
        }
    } elseif ($accepted) {
        "ACCEPT_SEMANTIC_ALL_REGISTERED_REPETITIONS"
    } else {
        "REJECT_SEMANTIC"
    }
    approvalEligible = -not [bool]$DiagnosticMode
    privateArtifactsMayLeaveWorkspace = $false
    productStatus = "TESTE_NON_APPROUVE"
    cases = $caseSummary
}
$assessment | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $assessmentPath -Encoding utf8

Write-Output "Advanced semantic review finalized: $assessmentPath"
Write-Output "Verdict: $($assessment.verdict); pass/reject: $($assessment.passedRows)/$($assessment.rejectedRows)"
