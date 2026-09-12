[CmdletBinding()]
param([string]$ArtifactDirectory = "")

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$finalizer = Join-Path $PSScriptRoot "finalize-advanced-semantic-review.ps1"
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $repositoryRoot "artifacts\reprise-pc-20260908\semantic-finalizer-test-$stamp"
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null
$commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()

function Write-JsonFile {
    param([string]$Path, [object]$Value)
    $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding utf8
}

function New-ReviewFixture {
    param(
        [string]$Name,
        [string[]]$Verdicts,
        [switch]$MissingReason,
        [switch]$UnknownJob,
        [switch]$DiagnosticManifest,
        [int]$UnresolvedEvidence = 0
    )
    $directory = Join-Path $ArtifactDirectory $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $job1 = [Guid]::NewGuid().ToString("D")
    $job2 = [Guid]::NewGuid().ToString("D")
    $bundlePath = Join-Path $directory "evidence-bundle.private.json"
    $reviewPath = Join-Path $directory "semantic-review.private.md"
    Set-Content -LiteralPath $bundlePath -Value '{"fixture":true}' -Encoding utf8
    Set-Content -LiteralPath $reviewPath -Value '# Private review fixture' -Encoding utf8
    $map = @(
        [ordered]@{ jobId = $job1; caseId = "CASE-A"; repetition = 1 },
        [ordered]@{ jobId = $job2; caseId = "CASE-A"; repetition = 2 }
    )
    Write-JsonFile (Join-Path $directory "campaign-map.private.json") $map
    Write-JsonFile (Join-Path $directory "job-ids.private.json") @($job1, $job2)
    $decisionJob2 = if ($UnknownJob) { [Guid]::NewGuid().ToString("D") } else { $job2 }
    $decisions = [ordered]@{
        schemaVersion = "saaia-advanced-semantic-decisions-v1"
        preparedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        repositoryCommit = $commit
        decisions = @(
            [ordered]@{
                jobId = $job1
                caseId = "CASE-A"
                repetition = 1
                verdict = $Verdicts[0]
                reason = "Canonical evidence supports the reviewed answer."
            },
            [ordered]@{
                jobId = $decisionJob2
                caseId = "CASE-A"
                repetition = 2
                verdict = $Verdicts[1]
                reason = if ($MissingReason) { "" } else { "Canonical evidence supports the reviewed answer." }
            }
        )
    }
    $decisionPath = Join-Path $directory "semantic-decisions.private.json"
    Write-JsonFile $decisionPath $decisions
    $manifest = [ordered]@{
        schemaVersion = "saaia-advanced-semantic-review-public-manifest-v1"
        repositoryCommit = $commit
        expectedRows = 2
        distinctDurableJobs = 2
        unresolvedEvidence = $UnresolvedEvidence
        privateBundleSha256 = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash
        privateReviewSha256 = (Get-FileHash -LiteralPath $reviewPath -Algorithm SHA256).Hash
        privateDecisionTemplateSha256 = (Get-FileHash -LiteralPath $decisionPath -Algorithm SHA256).Hash
        privateArtifactsMayLeaveWorkspace = $false
        approvalEligible = -not [bool]$DiagnosticManifest
        reviewMode = if ($DiagnosticManifest) { "DIAGNOSTIC_ONLY" } else { "ACCEPTANCE" }
        campaignRepositoryCommit = if ($DiagnosticManifest) { $commit } else { $null }
        campaignExecutionState = if ($DiagnosticManifest) {
            "FAILED_OR_INTERRUPTED_EXTERNAL_CALLS_POSSIBLE"
        } else { $null }
        semanticVerdict = if ($DiagnosticManifest) {
            "PENDING_DIAGNOSTIC_REVIEW"
        } else {
            "PENDING_HUMAN_REVIEW"
        }
        productStatus = "TESTE_NON_APPROUVE"
    }
    Write-JsonFile (Join-Path $directory "manifest.public.json") $manifest
    return $directory
}

$results = @()
function Add-TestResult {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $script:results += [pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail }
}

$accept = New-ReviewFixture "accept" @("PASS_SEMANTIC", "PASS_SEMANTIC")
& $finalizer -ReviewArtifactDirectory $accept | Out-Null
$acceptAssessment = Get-Content -LiteralPath (Join-Path $accept "semantic-assessment.public.json") -Raw | ConvertFrom-Json
Add-TestResult "all reviewed rows accept" `
    ($acceptAssessment.verdict -eq "ACCEPT_SEMANTIC_ALL_REGISTERED_REPETITIONS" -and
     [int]$acceptAssessment.passedRows -eq 2) ([string]$acceptAssessment.verdict)

$reject = New-ReviewFixture "reject" @("PASS_SEMANTIC", "REJECT_SEMANTIC")
& $finalizer -ReviewArtifactDirectory $reject | Out-Null
$rejectAssessment = Get-Content -LiteralPath (Join-Path $reject "semantic-assessment.public.json") -Raw | ConvertFrom-Json
Add-TestResult "one rejected row rejects semantic campaign" `
    ($rejectAssessment.verdict -eq "REJECT_SEMANTIC" -and [int]$rejectAssessment.rejectedRows -eq 1) `
    ([string]$rejectAssessment.verdict)

$unresolved = New-ReviewFixture "unresolved" @("PASS_SEMANTIC", "PASS_SEMANTIC") -UnresolvedEvidence 1
& $finalizer -ReviewArtifactDirectory $unresolved | Out-Null
$unresolvedAssessment = Get-Content -LiteralPath (Join-Path $unresolved "semantic-assessment.public.json") -Raw | ConvertFrom-Json
Add-TestResult "unresolved canonical evidence rejects semantic campaign" `
    ($unresolvedAssessment.verdict -eq "REJECT_SEMANTIC") ([string]$unresolvedAssessment.verdict)

$missingReason = New-ReviewFixture "missing-reason" @("PASS_SEMANTIC", "PASS_SEMANTIC") -MissingReason
$missingReasonRejected = $false
try { & $finalizer -ReviewArtifactDirectory $missingReason | Out-Null } catch { $missingReasonRejected = $true }
Add-TestResult "missing review reason is refused" `
    ($missingReasonRejected -and -not (Test-Path -LiteralPath (Join-Path $missingReason "semantic-assessment.public.json"))) `
    "rejected=$missingReasonRejected"

$unknownJob = New-ReviewFixture "unknown-job" @("PASS_SEMANTIC", "PASS_SEMANTIC") -UnknownJob
$unknownJobRejected = $false
try { & $finalizer -ReviewArtifactDirectory $unknownJob | Out-Null } catch { $unknownJobRejected = $true }
Add-TestResult "unknown durable job is refused" `
    ($unknownJobRejected -and -not (Test-Path -LiteralPath (Join-Path $unknownJob "semantic-assessment.public.json"))) `
    "rejected=$unknownJobRejected"

$diagnostic = New-ReviewFixture "diagnostic" @("PASS_SEMANTIC", "PASS_SEMANTIC") -DiagnosticManifest
$diagnosticRejected = $false
try { & $finalizer -ReviewArtifactDirectory $diagnostic | Out-Null } catch { $diagnosticRejected = $true }
Add-TestResult "diagnostic campaign cannot be finalized as acceptance" `
    ($diagnosticRejected -and -not (Test-Path -LiteralPath (Join-Path $diagnostic "semantic-assessment.public.json"))) `
    "rejected=$diagnosticRejected"

& $finalizer -ReviewArtifactDirectory $diagnostic -DiagnosticMode | Out-Null
$diagnosticAssessment = Get-Content -LiteralPath `
    (Join-Path $diagnostic "semantic-diagnostic.public.json") -Raw | ConvertFrom-Json
Add-TestResult "diagnostic rows produce a non-approving public verdict" `
    ($diagnosticAssessment.verdict -eq "DIAGNOSTIC_ROWS_ALL_PASS" -and
     -not [bool]$diagnosticAssessment.approvalEligible -and
     $diagnosticAssessment.productStatus -eq "TESTE_NON_APPROUVE") `
    ([string]$diagnosticAssessment.verdict)

$results | Format-Table -AutoSize | Out-String | Write-Output
$failed = @($results | Where-Object { -not $_.passed })
Write-Output "Passed: $($results.Count - $failed.Count)/$($results.Count)"
if ($failed.Count -gt 0) { exit 1 }
