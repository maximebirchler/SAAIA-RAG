[CmdletBinding()]
param([string]$ArtifactDirectory = "")

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$verifier = Join-Path $PSScriptRoot "verify-candidate-explorer-evidence.ps1"
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path `
        $repositoryRoot `
        ("artifacts\reprise-pc-20260908\candidate-explorer-evidence-verifier-test-" +
            (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null

function Write-JsonFile {
    param([string]$Path, [object]$Value)
    $Value | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $Path -Encoding utf8
}

function New-EvidenceFixture {
    param(
        [string]$Name,
        [switch]$TamperTrace,
        [switch]$ForeignTraceJob,
        [switch]$MissingCheckpoint,
        [switch]$MissingCritic
    )

    $directory = Join-Path $ArtifactDirectory $Name
    $traceDirectory = Join-Path $directory "private-traces"
    New-Item -ItemType Directory -Path $traceDirectory -Force | Out-Null
    $jobId = [Guid]::NewGuid().ToString("D")
    $traceJobId = if ($ForeignTraceJob) {
        [Guid]::NewGuid().ToString("D")
    } else {
        $jobId
    }
    $traceEntries = @()
    $roles = @("candidate-explorer", "writer")
    if (-not $MissingCritic) { $roles += "critic" }
    foreach ($role in $roles) {
        $tracePath = Join-Path $traceDirectory "$role.json"
        Write-JsonFile $tracePath ([ordered]@{
            schema = "advanced-development-trace.v2"
            jobId = $traceJobId
            role = $role
            requestJson = "{}"
            responseEnvelopeJson = "{}"
            completionJson = "{}"
        })
        $traceEntries += [ordered]@{
            relativePath = [IO.Path]::GetFileName($tracePath)
            lengthBytes = (Get-Item -LiteralPath $tracePath).Length
            sha256 = (Get-FileHash -LiteralPath $tracePath -Algorithm SHA256).Hash
        }
    }
    $traceManifestPath = Join-Path $directory `
        "private-development-traces-manifest.json"
    Write-JsonFile $traceManifestPath ([ordered]@{
        schemaVersion = "saaia-private-development-trace-manifest-v1"
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        containsPrivateCorpusMetadata = $true
        mustNotCommit = $true
        traceDirectory = $traceDirectory
        traceCount = $traceEntries.Count
        traces = $traceEntries
    })
    if ($TamperTrace) {
        Add-Content -LiteralPath `
            (Join-Path $traceDirectory "writer.json") `
            -Value "tampered"
    }

    $checkpoint = if ($MissingCheckpoint) {
        $null
    } else {
        [ordered]@{
            schemaVersion = "saaia.advanced-analysis-research-checkpoint.v1"
            candidates = @([ordered]@{
                key = "candidate-1"
                exactTitle = "Private fixture title"
                sourceKey = "internal-source-1"
                targetRoles = @("breakfast")
                selectedRoles = @()
                status = "body_verified"
                note = "fixture"
                locatorEvidenceIds = @("E1")
                bodyEvidenceIds = @("E2")
            })
            promptSourceKeys = [ordered]@{
                "private-source" = "internal-source-1"
            }
        }
    }
    $auditPath = Join-Path $directory "private-advanced-job-audit.json"
    Write-JsonFile $auditPath ([ordered]@{
        schemaVersion = "saaia-advanced-validation-private-job-audit-v1"
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        containsPrivateCorpusMetadata = $true
        mustNotCommit = $true
        userIds = @([Guid]::NewGuid().ToString("D"))
        jobs = @([ordered]@{
            jobId = $jobId
            status = "succeeded"
            result = [ordered]@{ outcome = "answered" }
            researchCheckpoint = $checkpoint
            toolEventCount = 1
            toolEvents = @([ordered]@{
                eventSequence = 1
                toolName = "source_backed_canonical_search"
                status = "succeeded"
            })
        })
    })
    $auditSha = (Get-FileHash -LiteralPath $auditPath -Algorithm SHA256).Hash
    $traceManifestSha = (
        Get-FileHash -LiteralPath $traceManifestPath -Algorithm SHA256).Hash
    Write-JsonFile (Join-Path $directory "advanced-job-guard-after.json") ([ordered]@{
        schemaVersion = "saaia-advanced-validation-job-guard-v2"
        privateAuditCaptured = $true
        privateAuditSha256 = $auditSha
        auditedJobCount = 1
        auditedToolEventCount = 1
        auditedResearchCheckpointCount = if ($MissingCheckpoint) { 0 } else { 1 }
    })
    Write-JsonFile (Join-Path $directory "resource-shutdown.json") ([ordered]@{
        completed = $true
        privateAdvancedJobAuditCaptured = $true
        privateAdvancedJobAuditSha256 = $auditSha
        privateDevelopmentTraceManifestCaptured = $true
        privateDevelopmentTraceManifestSha256 = $traceManifestSha
        privateDevelopmentTraceFileCount = $traceEntries.Count
        advancedValidationResearchCheckpointsAudited = if ($MissingCheckpoint) {
            0
        } else {
            1
        }
        advancedValidationJobGuardError = $null
        privateDevelopmentTraceManifestError = $null
        productStatus = "TESTE_NON_APPROUVE"
    })
    return $directory
}

$results = @()
function Add-Result {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $script:results += [pscustomobject]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    }
}

$valid = New-EvidenceFixture "valid"
& pwsh -NoProfile -File $verifier `
    -ArtifactDirectory $valid `
    -ExpectedJobs 1 `
    -RequireSemanticCritic | Out-Null
$validExit = $LASTEXITCODE
$validAssessmentPath = Join-Path $valid `
    "candidate-explorer-evidence-assessment.public.json"
$validAssessmentRaw = Get-Content -LiteralPath $validAssessmentPath -Raw
$validAssessment = $validAssessmentRaw | ConvertFrom-Json
Add-Result "valid private evidence passes integrity verification" `
    ($validExit -eq 0 -and
     [string]$validAssessment.verdict -eq
        "PASS_PRIVATE_EVIDENCE_INTEGRITY_REQUIRES_SEMANTIC_REVIEW" -and
     [int]$validAssessment.auditedJobs -eq 1 -and
     [int]$validAssessment.providerTraces -eq 3 -and
     $validAssessmentRaw -notmatch
        'Private fixture title|private-source|candidate-1|"E[12]"') `
    "exit=$validExit verdict=$($validAssessment.verdict)"

foreach ($case in @(
    [pscustomobject]@{ Name = "tampered-trace"; Arguments = @{ TamperTrace = $true } },
    [pscustomobject]@{ Name = "foreign-trace-job"; Arguments = @{ ForeignTraceJob = $true } },
    [pscustomobject]@{ Name = "missing-checkpoint"; Arguments = @{ MissingCheckpoint = $true } },
    [pscustomobject]@{ Name = "missing-critic"; Arguments = @{ MissingCritic = $true } }
)) {
    $fixtureArguments = $case.Arguments
    $fixture = New-EvidenceFixture -Name $case.Name @fixtureArguments
    & pwsh -NoProfile -File $verifier `
        -ArtifactDirectory $fixture `
        -ExpectedJobs 1 `
        -RequireSemanticCritic | Out-Null
    $exitCode = $LASTEXITCODE
    $assessment = Get-Content -LiteralPath (Join-Path $fixture `
        "candidate-explorer-evidence-assessment.public.json") -Raw | ConvertFrom-Json
    Add-Result "$($case.Name) is rejected" `
        ($exitCode -eq 2 -and
         [string]$assessment.verdict -eq "REJECT_PRIVATE_EVIDENCE_INTEGRITY" -and
         @($assessment.errors).Count -eq 1) `
        "exit=$exitCode verdict=$($assessment.verdict)"
}

$results | Format-Table -AutoSize | Out-String | Write-Output
$failed = @($results | Where-Object { -not $_.passed })
Write-Output "Passed: $($results.Count - $failed.Count)/$($results.Count)"
if ($failed.Count -gt 0) { exit 1 }
exit 0
