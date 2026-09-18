[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CampaignArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$ServerEnvPath,
    [ValidateRange(1, 200)][int]$ExpectedRows = 12,
    [switch]$DiagnosticMode,
    [string]$ArtifactDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Read-ReviewEnvFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*(#|$)' -or $line -notmatch '=') { continue }
        $separator = $line.IndexOf('=')
        $name = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        if ($value.Length -ge 2 -and
            (($value.StartsWith('"') -and $value.EndsWith('"')) -or
             ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$name] = $value
    }
    return $values
}

function Require-ReviewEnvValue {
    param([hashtable]$Values, [string]$Name)
    if (-not $Values.ContainsKey($Name) -or
        [string]::IsNullOrWhiteSpace([string]$Values[$Name])) {
        throw "Required server setting is missing: $Name"
    }
    return [string]$Values[$Name]
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Add-ReviewMarkdownText {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [string]$Text
    )
    $value = if ($null -eq $Text) { "" } else { $Text }
    $Lines.Add($value.Trim())
}

function Test-AgentBankResultFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    $firstRecord = Get-Content -LiteralPath $Path |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace([string]$firstRecord)) { return $false }
    try {
        $value = $firstRecord | ConvertFrom-Json
        return $value -is [pscustomobject] -and
            $null -ne $value.PSObject.Properties["row"] -and
            $null -ne $value.PSObject.Properties["answer"]
    }
    catch {
        return $false
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$CampaignArtifactDirectory = [System.IO.Path]::GetFullPath($CampaignArtifactDirectory)
$ServerEnvPath = [System.IO.Path]::GetFullPath($ServerEnvPath)
if (-not (Test-Path -LiteralPath $CampaignArtifactDirectory -PathType Container)) {
    throw "Campaign artifact directory not found: $CampaignArtifactDirectory"
}
if (-not (Test-Path -LiteralPath $ServerEnvPath -PathType Leaf)) {
    throw "Server environment file not found: $ServerEnvPath"
}

$profilePreflightPath = Join-Path $CampaignArtifactDirectory "preflight-seal.json"
if (-not (Test-Path -LiteralPath $profilePreflightPath -PathType Leaf)) {
    throw "Campaign profile preflight seal is missing."
}
$profilePreflight = Get-Content -LiteralPath $profilePreflightPath -Raw | ConvertFrom-Json
$campaignExecutionState = [string]$profilePreflight.executionState
$campaignKind = if ($null -eq $profilePreflight.PSObject.Properties["campaignKind"]) {
    "final-acceptance"
} else {
    [string]$profilePreflight.campaignKind
}
$candidateExplorerEnabled = $null -ne $profilePreflight.PSObject.Properties[
    "nativeCandidateExplorerEnabled"] -and
    [bool]$profilePreflight.nativeCandidateExplorerEnabled
$approvalEligible = -not $DiagnosticMode -and
    $campaignExecutionState -eq "COMPLETED" -and
    $campaignKind -eq "final-acceptance"
$diagnosticEligible = $DiagnosticMode -and (
    $campaignExecutionState -eq "FAILED_OR_INTERRUPTED_EXTERNAL_CALLS_POSSIBLE" -or
    ($campaignExecutionState -eq "COMPLETED" -and
        $campaignKind -eq "targeted-causal"))
if (-not $approvalEligible -and -not $diagnosticEligible) {
    throw "Campaign profile is not eligible for this review mode. Completed targeted campaigns and successful rows from failed campaigns require -DiagnosticMode."
}
$repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
$trackedDirty = @(& git -C $repositoryRoot status --porcelain --untracked-files=no 2>$null).Count -gt 0
$campaignRepositoryCommit = [string]$profilePreflight.repositoryCommit
if ($trackedDirty) {
    throw "Semantic review must be prepared from a clean tracked worktree."
}
if ($approvalEligible -and $repositoryCommit -ne $campaignRepositoryCommit) {
    throw "Acceptance review must be prepared from the exact clean campaign commit."
}
if ($diagnosticEligible) {
    & git -C $repositoryRoot merge-base --is-ancestor $campaignRepositoryCommit HEAD 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Diagnostic review must run from the campaign commit or a clean descendant."
    }
}

$resultFiles = @(Get-ChildItem `
    -LiteralPath $CampaignArtifactDirectory `
    -Recurse `
    -Filter "*.jsonl" `
    -File | Where-Object {
        Test-AgentBankResultFile $_.FullName
    } | Sort-Object FullName)
if ($resultFiles.Count -eq 0) { throw "No campaign JSONL result was found." }
$records = @()
$repetition = 0
foreach ($file in $resultFiles) {
    $repetition++
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $record = $line | ConvertFrom-Json
        $jobId = if ($null -ne $record.row.PSObject.Properties["advancedJobId"]) {
            [string]$record.row.advancedJobId
        } else { "" }
        $parsedJobId = [Guid]::Empty
        if (-not [Guid]::TryParse($jobId, [ref]$parsedJobId)) {
            throw "Campaign result has no valid advanced job UUID."
        }
        if ([string]$record.row.advancedStatus -ne "succeeded" -or
            -not [string]::IsNullOrWhiteSpace([string]$record.row.error)) {
            throw "Campaign result is not a successful advanced answer."
        }
        $records += [pscustomobject]@{
            repetition = $repetition
            jobId = $parsedJobId.ToString("D")
            caseId = [string]$record.row.id
            question = [string]$record.row.question
            validationPoints = [string]$record.row.validationPoints
            answer = [string]$record.answer
            provider = [string]$record.row.advancedProviderKey
            model = [string]$record.row.advancedProviderModel
        }
    }
}
if ($records.Count -ne $ExpectedRows) {
    throw "Expected $ExpectedRows successful rows but found $($records.Count)."
}
$jobIds = @($records | ForEach-Object jobId | Sort-Object -Unique)
if ($jobIds.Count -ne $ExpectedRows) {
    throw "Each successful row must map to one distinct durable job UUID."
}

$candidateExplorerEvidenceAssessment = $null
$candidateExplorerEvidenceAssessmentPath = $null
$candidateExplorerPrivateReviewPath = $null
if ($candidateExplorerEnabled) {
    $integrityAssessments = @(Get-ChildItem `
        -LiteralPath $CampaignArtifactDirectory `
        -Recurse `
        -Filter "candidate-explorer-evidence-assessment.public.json" `
        -File)
    if ($integrityAssessments.Count -ne 1) {
        throw "Exactly one Candidate Explorer evidence assessment is required."
    }
    $candidateExplorerEvidenceAssessmentPath = $integrityAssessments[0].FullName
    $candidateExplorerEvidenceAssessment = Get-Content `
        -LiteralPath $candidateExplorerEvidenceAssessmentPath `
        -Raw | ConvertFrom-Json
    if ([string]$candidateExplorerEvidenceAssessment.schemaVersion -ne
            "saaia-candidate-explorer-evidence-assessment-public-v1" -or
        [string]$candidateExplorerEvidenceAssessment.verdict -ne
            "PASS_PRIVATE_EVIDENCE_INTEGRITY_REQUIRES_SEMANTIC_REVIEW" -or
        [bool]$candidateExplorerEvidenceAssessment.privateArtifactsMayLeaveWorkspace -or
        [int]$candidateExplorerEvidenceAssessment.expectedJobs -ne $ExpectedRows -or
        [int]$candidateExplorerEvidenceAssessment.auditedJobs -ne $ExpectedRows -or
        [int]$candidateExplorerEvidenceAssessment.auditedResearchCheckpoints -ne
            $ExpectedRows) {
        throw "Candidate Explorer evidence is not eligible for semantic review."
    }
    $candidateExplorerPrivateReviewPath = Join-Path `
        (Split-Path -Parent $candidateExplorerEvidenceAssessmentPath) `
        "candidate-explorer-evidence-review.private.md"
    if (-not (Test-Path `
            -LiteralPath $candidateExplorerPrivateReviewPath `
            -PathType Leaf) -or
        (Get-FileSha256 $candidateExplorerPrivateReviewPath) -ne
            [string]$candidateExplorerEvidenceAssessment.privateCandidateReviewSha256) {
        throw "Candidate Explorer private review changed after integrity verification."
    }
}

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDirectory = Join-Path $CampaignArtifactDirectory "semantic-review-$stamp"
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    throw "Semantic review artifact directory already exists: $ArtifactDirectory"
}
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null

$jobIdsPath = Join-Path $ArtifactDirectory "job-ids.private.json"
ConvertTo-Json -InputObject @($jobIds) |
    Set-Content -LiteralPath $jobIdsPath -Encoding utf8
$recordMapPath = Join-Path $ArtifactDirectory "campaign-map.private.json"
$records | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recordMapPath -Encoding utf8
$bundlePath = Join-Path $ArtifactDirectory "evidence-bundle.private.json"
$projectPath = Join-Path $PSScriptRoot "SAAIA.AdvancedSemanticReviewExporter\SAAIA.AdvancedSemanticReviewExporter.csproj"
$serverEnvironment = Read-ReviewEnvFile -Path $ServerEnvPath
$database = Require-ReviewEnvValue $serverEnvironment "POSTGRES_DB"
$user = Require-ReviewEnvValue $serverEnvironment "POSTGRES_USER"
$password = Require-ReviewEnvValue $serverEnvironment "POSTGRES_PASSWORD"
$connectionString = "Host=saaia-server;Port=5432;Database=$database;Username=$user;Password=$password;Pooling=false"
$previousConnection = [Environment]::GetEnvironmentVariable(
    "SAAIA_SEMANTIC_REVIEW_CONNECTION", "Process")
try {
    $env:SAAIA_SEMANTIC_REVIEW_CONNECTION = $connectionString
    $buildOutput = & dotnet build $projectPath -c Release 2>&1
    $buildOutput | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "exporter-build.log") -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Semantic review exporter build failed." }
    $exportOutput = & dotnet run --project $projectPath -c Release --no-build --no-restore -- `
        --job-ids $jobIdsPath --output $bundlePath 2>&1
    $exportOutput | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "exporter-run.log") -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Semantic review export failed." }
}
finally {
    [Environment]::SetEnvironmentVariable(
        "SAAIA_SEMANTIC_REVIEW_CONNECTION", $previousConnection, "Process")
    $connectionString = $null
    $password = $null
    $serverEnvironment.Clear()
}

$bundle = Get-Content -LiteralPath $bundlePath -Raw | ConvertFrom-Json
if ([int]$bundle.requestedJobCount -ne $ExpectedRows -or
    @($bundle.jobs).Count -ne $ExpectedRows -or
    [string]$bundle.transaction -ne "REPEATABLE READ ONLY") {
    throw "Semantic review bundle validation failed."
}

$chunksById = @{}
foreach ($chunk in @($bundle.chunks)) { $chunksById[[string]$chunk.chunkId] = $chunk }
$cardsById = @{}
foreach ($card in @($bundle.contentCards)) { $cardsById[[string]$card.contentCardId] = $card }
$recordsByJob = @{}
foreach ($record in $records) { $recordsByJob[$record.jobId] = $record }
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# Private semantic review of the advanced campaign")
$lines.Add("")
$lines.Add($(if ($approvalEligible) {
    "Initial status: PENDING_REVIEW - this file is not a verdict."
} else {
    "Initial status: DIAGNOSTIC_ONLY - this incomplete campaign cannot be approved."
}))
$lines.Add("Each claim must be compared with the canonical text below.")
foreach ($job in @($bundle.jobs | Sort-Object jobId)) {
    $jobId = [string]$job.jobId
    $record = $recordsByJob[$jobId]
    $lines.Add("")
    $lines.Add("## $($record.caseId) - repetition $($record.repetition)")
    $lines.Add("")
    $lines.Add(('Job : `{0}`' -f $jobId))
    $lines.Add("")
    $lines.Add("Question:")
    $lines.Add("")
    Add-ReviewMarkdownText -Lines $lines -Text $record.question
    $lines.Add("")
    $lines.Add("Preregistered criteria:")
    $lines.Add("")
    Add-ReviewMarkdownText -Lines $lines -Text $record.validationPoints
    $lines.Add("")
    $lines.Add("Answer:")
    $lines.Add("")
    Add-ReviewMarkdownText -Lines $lines -Text ([string]$job.result.answerText)
    $lines.Add("")
    $lines.Add("Declared claims and relations:")
    foreach ($claim in @($job.result.claims)) {
        $lines.Add("")
        $lines.Add("- $([string]$claim.claimId) - $([string]$claim.text)")
        $lines.Add("  - Evidence IDs : $(@($claim.evidenceIds) -join ', ')")
    }
    $lines.Add("")
    $lines.Add("Canonical evidence:")
    foreach ($evidence in @($job.result.evidence)) {
        $lines.Add("")
        $lines.Add("### $([string]$evidence.evidenceId)")
        $lines.Add("")
        $lines.Add("Source: $([string]$evidence.fileName), pages $($evidence.pageStart)-$($evidence.pageEnd)")
        $chunkId = [string]$evidence.chunkId
        $cardId = [string]$evidence.contentCardId
        if (-not [string]::IsNullOrWhiteSpace($chunkId) -and $chunksById.ContainsKey($chunkId)) {
            Add-ReviewMarkdownText -Lines $lines -Text ([string]$chunksById[$chunkId].text)
        }
        elseif (-not [string]::IsNullOrWhiteSpace($cardId) -and $cardsById.ContainsKey($cardId)) {
            Add-ReviewMarkdownText -Lines $lines -Text ([string]$cardsById[$cardId].searchText)
        }
        else {
            $lines.Add("PREUVE_TEXTUELLE_NON_RESOLUE")
        }
    }
    $lines.Add("")
    $lines.Add("Decision to fill: PASS_SEMANTIC or REJECT_SEMANTIC")
    $lines.Add("Reason:")
}
if ($candidateExplorerEnabled) {
    $lines.Add("")
    $lines.Add("# Candidate Explorer causal evidence appendix")
    $lines.Add("")
    $lines.Add("This appendix was integrity-checked before semantic review.")
    foreach ($line in Get-Content -LiteralPath $candidateExplorerPrivateReviewPath) {
        $lines.Add([string]$line)
    }
}
$reviewPath = Join-Path $ArtifactDirectory "semantic-review.private.md"
[IO.File]::WriteAllLines($reviewPath, $lines, [Text.UTF8Encoding]::new($false))

$decisionTemplate = [ordered]@{
    schemaVersion = "saaia-advanced-semantic-decisions-v1"
    preparedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    repositoryCommit = $repositoryCommit
    decisions = @($records | Sort-Object caseId, repetition | ForEach-Object {
        [ordered]@{
            jobId = $_.jobId
            caseId = $_.caseId
            repetition = $_.repetition
            verdict = "PENDING_REVIEW"
            reason = ""
        }
    })
}
$decisionPath = Join-Path $ArtifactDirectory "semantic-decisions.private.json"
$decisionTemplate | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $decisionPath -Encoding utf8

$unresolvedEvidence = @([regex]::Matches(
    [IO.File]::ReadAllText($reviewPath), "PREUVE_TEXTUELLE_NON_RESOLUE")).Count
$manifest = [ordered]@{
    schemaVersion = "saaia-advanced-semantic-review-public-manifest-v1"
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    repositoryCommit = $repositoryCommit
    campaignRepositoryCommit = $campaignRepositoryCommit
    campaignExecutionState = $campaignExecutionState
    campaignKind = $campaignKind
    reviewMode = $(if ($approvalEligible) { "ACCEPTANCE" } else { "DIAGNOSTIC_ONLY" })
    approvalEligible = $approvalEligible
    campaignPreflightSha256 = (Get-FileHash -LiteralPath $profilePreflightPath -Algorithm SHA256).Hash
    resultFileSha256 = @($resultFiles | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })
    expectedRows = $ExpectedRows
    distinctDurableJobs = $jobIds.Count
    canonicalChunks = @($bundle.chunks).Count
    canonicalContentCards = @($bundle.contentCards).Count
    toolEvents = @($bundle.toolEvents).Count
    candidateExplorerEnabled = $candidateExplorerEnabled
    candidateExplorerEvidenceIntegrityVerdict = $(
        if ($candidateExplorerEnabled) {
            [string]$candidateExplorerEvidenceAssessment.verdict
        } else { $null })
    candidateExplorerEvidenceAssessmentSha256 = $(
        if ($candidateExplorerEnabled) {
            Get-FileSha256 $candidateExplorerEvidenceAssessmentPath
        } else { $null })
    privateCandidateExplorerReviewSha256 = $(
        if ($candidateExplorerEnabled) {
            Get-FileSha256 $candidateExplorerPrivateReviewPath
        } else { $null })
    candidateExplorerAuditedJobs = $(
        if ($candidateExplorerEnabled) {
            [int]$candidateExplorerEvidenceAssessment.auditedJobs
        } else { 0 })
    candidateExplorerAuditedResearchCheckpoints = $(
        if ($candidateExplorerEnabled) {
            [int]$candidateExplorerEvidenceAssessment.auditedResearchCheckpoints
        } else { 0 })
    candidateExplorerAuditedToolEvents = $(
        if ($candidateExplorerEnabled) {
            [int]$candidateExplorerEvidenceAssessment.auditedToolEvents
        } else { 0 })
    candidateExplorerProviderTraces = $(
        if ($candidateExplorerEnabled) {
            [int]$candidateExplorerEvidenceAssessment.providerTraces
        } else { 0 })
    candidateExplorerRequiresSemanticCritic = $(
        if ($candidateExplorerEnabled) {
            [bool]$candidateExplorerEvidenceAssessment.requireSemanticCritic
        } else { $false })
    unresolvedEvidence = $unresolvedEvidence
    privateBundleSha256 = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash
    privateReviewSha256 = (Get-FileHash -LiteralPath $reviewPath -Algorithm SHA256).Hash
    privateDecisionTemplateSha256 = (Get-FileHash -LiteralPath $decisionPath -Algorithm SHA256).Hash
    privateArtifactsMayLeaveWorkspace = $false
    semanticVerdict = $(if ($approvalEligible) {
        "PENDING_HUMAN_REVIEW"
    } else {
        "PENDING_DIAGNOSTIC_REVIEW"
    })
    productStatus = "TESTE_NON_APPROUVE"
}
$manifestPath = Join-Path $ArtifactDirectory "manifest.public.json"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output "Advanced semantic review packet prepared: $manifestPath"
Write-Output "Rows/jobs: $($records.Count)/$($jobIds.Count); unresolved evidence: $unresolvedEvidence"
