[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,
    [ValidateRange(1, 200)]
    [int]$ExpectedJobs = 1,
    [switch]$RequireSemanticCritic,
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Require-File {
    param([string]$Directory, [string]$Name)
    $path = Join-Path $Directory $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Candidate Explorer evidence file is missing: $Name"
    }
    return $path
}

function Read-JsonFile {
    param([string]$Path)
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-FileSha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function ConvertTo-PrivateSingleLine {
    param([object]$Value)
    if ($null -eq $Value) { return "" }
    return ([string]$Value).Replace("`r", " ").Replace("`n", " ").Trim()
}

$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
if (-not (Test-Path -LiteralPath $ArtifactDirectory -PathType Container)) {
    throw "Candidate Explorer artifact directory was not found: $ArtifactDirectory"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path `
        $ArtifactDirectory `
        "candidate-explorer-evidence-assessment.public.json"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

$errors = [Collections.Generic.List[string]]::new()
$auditSha256 = $null
$traceManifestSha256 = $null
$jobCount = 0
$toolEventCount = 0
$checkpointCount = 0
$traceCount = 0
$checkpointMetrics = @()
$traceRoleCounts = [ordered]@{}
$privateReviewPath = Join-Path `
    $ArtifactDirectory `
    "candidate-explorer-evidence-review.private.md"
$privateReviewSha256 = $null
$privateReviewLines = [Collections.Generic.List[string]]::new()
$privateReviewLines.Add("# Private Candidate Explorer evidence review")
$privateReviewLines.Add("")
$privateReviewLines.Add("Status: EVIDENCE_ONLY - human semantic review is still required.")
$privateReviewLines.Add("This file contains private corpus metadata and must not leave the workspace.")

try {
    $guardPath = Require-File $ArtifactDirectory "advanced-job-guard-after.json"
    $auditPath = Require-File $ArtifactDirectory "private-advanced-job-audit.json"
    $traceManifestPath = Require-File `
        $ArtifactDirectory `
        "private-development-traces-manifest.json"
    $shutdownPath = Require-File $ArtifactDirectory "resource-shutdown.json"

    $guard = Read-JsonFile $guardPath
    $audit = Read-JsonFile $auditPath
    $traceManifest = Read-JsonFile $traceManifestPath
    $shutdown = Read-JsonFile $shutdownPath
    $auditSha256 = Get-FileSha256 $auditPath
    $traceManifestSha256 = Get-FileSha256 $traceManifestPath

    if ([string]$guard.schemaVersion -ne "saaia-advanced-validation-job-guard-v2" -or
        -not [bool]$guard.privateAuditCaptured -or
        [string]$guard.privateAuditSha256 -ne $auditSha256) {
        throw "The postflight job guard does not authenticate the private audit."
    }
    if ([string]$audit.schemaVersion -ne
            "saaia-advanced-validation-private-job-audit-v1" -or
        -not [bool]$audit.containsPrivateCorpusMetadata -or
        -not [bool]$audit.mustNotCommit) {
        throw "The private job audit has an unsupported schema or privacy policy."
    }
    if ([string]$traceManifest.schemaVersion -ne
            "saaia-private-development-trace-manifest-v1" -or
        -not [bool]$traceManifest.containsPrivateCorpusMetadata -or
        -not [bool]$traceManifest.mustNotCommit) {
        throw "The development trace manifest has an unsupported schema or privacy policy."
    }
    if (-not [bool]$shutdown.completed -or
        -not [bool]$shutdown.privateAdvancedJobAuditCaptured -or
        -not [bool]$shutdown.privateDevelopmentTraceManifestCaptured -or
        -not [string]::IsNullOrWhiteSpace(
            [string]$shutdown.advancedValidationJobGuardError) -or
        -not [string]::IsNullOrWhiteSpace(
            [string]$shutdown.privateDevelopmentTraceManifestError) -or
        [string]$shutdown.privateAdvancedJobAuditSha256 -ne $auditSha256 -or
        [string]$shutdown.privateDevelopmentTraceManifestSha256 -ne
            $traceManifestSha256) {
        throw "Resource shutdown does not authenticate a completed private evidence capture."
    }

    $jobs = @($audit.jobs)
    $jobCount = $jobs.Count
    $toolEventCount = @($jobs | ForEach-Object { @($_.toolEvents) }).Count
    $checkpointCount = @($jobs | Where-Object {
        $null -ne $_.researchCheckpoint
    }).Count
    if ($jobCount -ne $ExpectedJobs -or
        [int]$guard.auditedJobCount -ne $ExpectedJobs -or
        [int]$guard.auditedToolEventCount -ne $toolEventCount -or
        [int]$guard.auditedResearchCheckpointCount -ne $checkpointCount) {
        throw "Private audit counts do not match the registered Candidate Explorer campaign."
    }

    $jobIds = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $ordinal = 0
    foreach ($job in @($jobs | Sort-Object jobId)) {
        $ordinal++
        $jobId = [string]$job.jobId
        $parsedJobId = [Guid]::Empty
        if (-not [Guid]::TryParse($jobId, [ref]$parsedJobId) -or
            -not $jobIds.Add($parsedJobId.ToString("D"))) {
            throw "The private audit contains an invalid or duplicate job identity."
        }
        if ([string]$job.status -ne "succeeded" -or
            $null -eq $job.result -or
            $null -eq $job.researchCheckpoint -or
            @($job.toolEvents).Count -lt 1 -or
            [int]$job.toolEventCount -ne @($job.toolEvents).Count) {
            throw "A completed Candidate Explorer job lacks its result, checkpoint, or tool events."
        }
        $checkpoint = $job.researchCheckpoint
        if ([string]$checkpoint.schemaVersion -ne
                "saaia.advanced-analysis-research-checkpoint.v1") {
            throw "A Candidate Explorer checkpoint has an unsupported schema."
        }
        $candidates = @($checkpoint.candidates)
        if ($candidates.Count -lt 1 -or $candidates.Count -gt 64) {
            throw "A Candidate Explorer checkpoint has an invalid candidate count."
        }
        $verified = @($candidates | Where-Object {
            [string]$_.status -in @("body_verified", "selected")
        })
        if ($verified.Count -lt 1) {
            throw "A Candidate Explorer checkpoint has no body-verified candidate."
        }
        $checkpointMetrics += [ordered]@{
            jobOrdinal = $ordinal
            candidates = $candidates.Count
            bodyVerifiedOrSelected = $verified.Count
            selected = @($candidates | Where-Object status -eq "selected").Count
            navigationOnly = @($candidates | Where-Object status -eq "navigation_only").Count
            bodyRequested = @($candidates | Where-Object status -eq "body_requested").Count
            rejected = @($candidates | Where-Object status -eq "rejected").Count
            distinctSelectedRoles = @($candidates | ForEach-Object {
                    @($_.selectedRoles)
                } | Where-Object {
                    -not [string]::IsNullOrWhiteSpace([string]$_)
                } |
                Sort-Object -Unique).Count
        }
        $privateReviewLines.Add("")
        $privateReviewLines.Add("## Job $ordinal")
        $privateReviewLines.Add("")
        $privateReviewLines.Add(('Job identity: `{0}`' -f $jobId))
        $privateReviewLines.Add("")
        $privateReviewLines.Add("### Candidate checkpoint")
        foreach ($candidate in $candidates) {
            $privateReviewLines.Add("")
            $privateReviewLines.Add("- Title: " +
                (ConvertTo-PrivateSingleLine $candidate.exactTitle))
            $privateReviewLines.Add("  - Status: " +
                (ConvertTo-PrivateSingleLine $candidate.status))
            $privateReviewLines.Add("  - Source key: " +
                (ConvertTo-PrivateSingleLine $candidate.sourceKey))
            $privateReviewLines.Add("  - Target roles: " +
                (@($candidate.targetRoles) -join ", "))
            $privateReviewLines.Add("  - Selected roles: " +
                (@($candidate.selectedRoles) -join ", "))
            $privateReviewLines.Add("  - Locator evidence: " +
                (@($candidate.locatorEvidenceIds) -join ", "))
            $privateReviewLines.Add("  - Body evidence: " +
                (@($candidate.bodyEvidenceIds) -join ", "))
            $privateReviewLines.Add("  - Model note: " +
                (ConvertTo-PrivateSingleLine $candidate.note))
        }
        $privateReviewLines.Add("")
        $privateReviewLines.Add("### Tool events")
        foreach ($event in @($job.toolEvents | Sort-Object eventSequence)) {
            $privateReviewLines.Add("")
            $privateReviewLines.Add((
                "- Sequence {0}: {1} / {2} / {3} ms" -f
                    [int]$event.eventSequence,
                    (ConvertTo-PrivateSingleLine $event.toolName),
                    (ConvertTo-PrivateSingleLine $event.status),
                    [long]$event.elapsedMilliseconds))
            $privateReviewLines.Add("  - Error: " +
                (ConvertTo-PrivateSingleLine $event.errorCode))
            $privateReviewLines.Add("  - Request JSON: " +
                ($event.request | ConvertTo-Json -Depth 8 -Compress))
            $privateReviewLines.Add("  - Evidence references JSON: " +
                ($event.evidenceReferences | ConvertTo-Json -Depth 8 -Compress))
        }
    }

    $traceDirectory = [IO.Path]::GetFullPath(
        [string]$traceManifest.traceDirectory)
    $traceDirectoryPrefix = $traceDirectory.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $traces = @($traceManifest.traces)
    $traceCount = $traces.Count
    if ($traceCount -lt 1 -or
        [int]$traceManifest.traceCount -ne $traceCount -or
        [int]$shutdown.privateDevelopmentTraceFileCount -ne $traceCount -or
        [int]$shutdown.advancedValidationResearchCheckpointsAudited -ne
            $checkpointCount) {
        throw "The development trace manifest is empty or internally inconsistent."
    }
    $traceJobs = @{}
    $seenTracePaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($trace in $traces) {
        $relativePath = [string]$trace.relativePath
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [IO.Path]::IsPathRooted($relativePath) -or
            -not $seenTracePaths.Add($relativePath)) {
            throw "The development trace manifest contains an invalid relative path."
        }
        $tracePath = [IO.Path]::GetFullPath(
            (Join-Path $traceDirectory $relativePath))
        if (-not $tracePath.StartsWith(
                $traceDirectoryPrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $tracePath -PathType Leaf) -or
            (Get-Item -LiteralPath $tracePath).Length -ne [long]$trace.lengthBytes -or
            (Get-FileSha256 $tracePath) -ne [string]$trace.sha256) {
            throw "A development trace is missing, outside its root, or changed after capture."
        }
        $traceDocument = Read-JsonFile $tracePath
        $traceJobId = [string]$traceDocument.jobId
        $parsedTraceJobId = [Guid]::Empty
        if ([string]$traceDocument.schema -notmatch '^advanced-development-trace\.v[12]$' -or
            -not [Guid]::TryParse($traceJobId, [ref]$parsedTraceJobId) -or
            -not $jobIds.Contains($parsedTraceJobId.ToString("D"))) {
            throw "A development trace has an unsupported schema or an unknown job identity."
        }
        $normalizedTraceJobId = $parsedTraceJobId.ToString("D")
        if (-not $traceJobs.ContainsKey($normalizedTraceJobId)) {
            $traceJobs[$normalizedTraceJobId] = 0
        }
        $traceJobs[$normalizedTraceJobId]++
        $role = [string]$traceDocument.role
        if ([string]::IsNullOrWhiteSpace($role)) {
            throw "A development trace has no provider role."
        }
        if (-not $traceRoleCounts.Contains($role)) {
            $traceRoleCounts[$role] = 0
        }
        $traceRoleCounts[$role]++
    }
    foreach ($jobId in $jobIds) {
        if (-not $traceJobs.ContainsKey($jobId)) {
            throw "A completed Candidate Explorer job has no provider trace."
        }
    }
    if (@($traceRoleCounts.Keys | Where-Object {
            $_ -eq "candidate-explorer" -or
            $_ -like "candidate-explorer-correction-*"
        }).Count -lt 1 -or
        -not $traceRoleCounts.Contains("writer") -or
        ($RequireSemanticCritic -and -not $traceRoleCounts.Contains("critic"))) {
        throw "The provider traces do not contain every required Candidate Explorer role."
    }
}
catch {
    $errors.Add($_.Exception.Message)
}

if ($errors.Count -eq 0) {
    $privateReviewTemporaryPath = $privateReviewPath + ".tmp"
    [IO.File]::WriteAllLines(
        $privateReviewTemporaryPath,
        $privateReviewLines,
        [Text.UTF8Encoding]::new($false))
    Move-Item `
        -LiteralPath $privateReviewTemporaryPath `
        -Destination $privateReviewPath `
        -Force
    $privateReviewSha256 = Get-FileSha256 $privateReviewPath
}

$assessment = [ordered]@{
    schemaVersion = "saaia-candidate-explorer-evidence-assessment-public-v1"
    assessedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    artifactDirectory = $ArtifactDirectory
    expectedJobs = $ExpectedJobs
    requireSemanticCritic = [bool]$RequireSemanticCritic
    privateArtifactsMayLeaveWorkspace = $false
    privateAuditSha256 = $auditSha256
    privateTraceManifestSha256 = $traceManifestSha256
    privateCandidateReviewSha256 = $privateReviewSha256
    auditedJobs = $jobCount
    auditedToolEvents = $toolEventCount
    auditedResearchCheckpoints = $checkpointCount
    providerTraces = $traceCount
    providerTraceRoles = $traceRoleCounts
    checkpointMetrics = $checkpointMetrics
    errors = @($errors)
    verdict = if ($errors.Count -eq 0) {
        "PASS_PRIVATE_EVIDENCE_INTEGRITY_REQUIRES_SEMANTIC_REVIEW"
    } else {
        "REJECT_PRIVATE_EVIDENCE_INTEGRITY"
    }
    productStatus = "TESTE_NON_APPROUVE"
}
$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
$temporaryPath = $OutputPath + ".tmp"
$assessment | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $temporaryPath -Encoding utf8
Move-Item -LiteralPath $temporaryPath -Destination $OutputPath -Force

Write-Output "Candidate Explorer evidence assessment: $OutputPath"
Write-Output "Verdict: $($assessment.verdict)"
if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Output "FAIL: $_" }
    exit 2
}
exit 0
