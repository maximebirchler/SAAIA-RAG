[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LlmBaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$LlmModel,

    [Parameter(Mandatory = $true)]
    [string]$TestProject,

    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory,

    [string]$TestFilter = "FullyQualifiedName=SAAIA.Client.ToolAgent.Tests.LiveCuisineAgentValidationTests.Live_final_weekly_meal_plan_question_runs_through_real_client_agent_when_enabled",
    [double]$TimeoutMinutes = 28,
    [Nullable[double]]$Temperature,
    [Nullable[double]]$TopP,
    [Nullable[int]]$TopK,
    [Nullable[double]]$MinP,
    [Nullable[double]]$FrequencyPenalty,
    [Nullable[double]]$PresencePenalty,
    [ValidateSet("deterministic", "native")]
    [string]$StructuredSampling = "deterministic",
    [switch]$DisableSemanticCandidateAudit,
    [switch]$DisableEvidenceSelectionHandoff,
    [ValidateRange(8, 80)]
    [int]$MaximumWorkingEvidenceItems = 80,
    [ValidateRange(8, 80)]
    [int]$MaximumSemanticCandidatesPerAuditTurn = 40,
    [ValidateRange(1, 4)]
    [int]$MaximumSemanticCandidateAuditConcurrency = 2,
    [switch]$SkipBuild,
    [string]$Platform = "x64",
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$project = [IO.Path]::GetFullPath($TestProject)
$results = [IO.Path]::GetFullPath($ResultsDirectory)
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Test project not found: $project"
}
New-Item -ItemType Directory -Path $results -Force | Out-Null

function Get-ProjectBuildEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    $projectDirectory = Split-Path -Parent $ProjectPath
    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($ProjectPath) + ".dll"
    $assemblyRoot = Join-Path $projectDirectory (
        "bin\{0}\{1}" -f $Platform, $Configuration)
    if (-not (Test-Path -LiteralPath $assemblyRoot -PathType Container)) {
        throw "Build output directory not found: $assemblyRoot"
    }

    $assembly = Get-ChildItem -LiteralPath $assemblyRoot -Recurse -File `
            -Filter $assemblyName |
        Where-Object {
            $_.FullName -notmatch '[\\/]ref(int)?[\\/]'
        } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $assembly) {
        throw "Build assembly not found below ${assemblyRoot}: $assemblyName"
    }

    $newestSource = Get-ChildItem -LiteralPath $projectDirectory -Recurse -File `
            -Filter "*.cs" |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -ne $newestSource `
        -and $newestSource.LastWriteTimeUtc -gt $assembly.LastWriteTimeUtc.AddSeconds(1)) {
        throw (
            "SkipBuild refused: source '{0}' ({1:O}) is newer than assembly '{2}' ({3:O})." `
                -f $newestSource.FullName,
                $newestSource.LastWriteTimeUtc,
                $assembly.FullName,
                $assembly.LastWriteTimeUtc)
    }

    return [pscustomobject]@{
        projectPath = $ProjectPath
        assemblyPath = $assembly.FullName
        assemblyDirectory = $assembly.DirectoryName
        assemblyLastWriteUtc = $assembly.LastWriteTimeUtc.ToString("O")
        assemblySha256 = (Get-FileHash -LiteralPath $assembly.FullName `
            -Algorithm SHA256).Hash
        newestSourcePath = if ($null -eq $newestSource) {
            $null
        } else {
            $newestSource.FullName
        }
        newestSourceLastWriteUtc = if ($null -eq $newestSource) {
            $null
        } else {
            $newestSource.LastWriteTimeUtc.ToString("O")
        }
    }
}

$buildEvidence = $null
if ($SkipBuild) {
    $testBuildEvidence = Get-ProjectBuildEvidence -ProjectPath $project
    [xml]$testProjectXml = Get-Content -LiteralPath $project -Raw
    $clientProjectReference = @(
        $testProjectXml.SelectNodes("//*[local-name()='ProjectReference']")) |
        Where-Object {
            [string]$_.Include -match 'SAAIA\.Client\.WinUI\.csproj$'
        } |
        Select-Object -First 1
    if ($null -eq $clientProjectReference) {
        throw "SkipBuild validation could not find the WinUI project reference."
    }

    $clientProject = [IO.Path]::GetFullPath((Join-Path `
        (Split-Path -Parent $project) `
        ([string]$clientProjectReference.Include)))
    $clientBuildEvidence = Get-ProjectBuildEvidence -ProjectPath $clientProject
    $copiedClientAssembly = Join-Path `
        $testBuildEvidence.assemblyDirectory `
        ([IO.Path]::GetFileName($clientBuildEvidence.assemblyPath))
    if (-not (Test-Path -LiteralPath $copiedClientAssembly -PathType Leaf)) {
        throw "SkipBuild validation found no copied WinUI assembly: $copiedClientAssembly"
    }

    $copiedClientSha256 = (Get-FileHash -LiteralPath $copiedClientAssembly `
        -Algorithm SHA256).Hash
    if ($copiedClientSha256 -ne $clientBuildEvidence.assemblySha256) {
        throw (
            "SkipBuild refused: copied WinUI assembly hash {0} differs from built assembly hash {1}." `
                -f $copiedClientSha256,
                $clientBuildEvidence.assemblySha256)
    }

    $buildEvidence = [ordered]@{
        verified = $true
        testAssembly = $testBuildEvidence
        clientAssembly = $clientBuildEvidence
        copiedClientAssemblyPath = $copiedClientAssembly
        copiedClientAssemblySha256 = $copiedClientSha256
    }
}

$env:SAAIA_LIVE_VALIDATION = "1"
$env:SAAIA_VALIDATION_MANAGE_LOCAL_LLM_PROCESS = "0"
$env:SAAIA_VALIDATION_LLM_BASE_URL = $LlmBaseUrl.TrimEnd("/")
$env:SAAIA_VALIDATION_LLM_MODEL = $LlmModel
$env:SAAIA_LIVE_FINAL_TIMEOUT_MINUTES = $TimeoutMinutes.ToString(
    [Globalization.CultureInfo]::InvariantCulture)
$env:SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_AUDIT = if ($DisableSemanticCandidateAudit) {
    "0"
} else {
    "1"
}
$env:SAAIA_SOURCE_BACKED_AGENT_V2_MAX_WORKING_EVIDENCE =
    $MaximumWorkingEvidenceItems.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATES_PER_AUDIT_TURN =
    $MaximumSemanticCandidatesPerAuditTurn.ToString(
        [Globalization.CultureInfo]::InvariantCulture)
$env:SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_AUDIT_CONCURRENCY =
    $MaximumSemanticCandidateAuditConcurrency.ToString(
        [Globalization.CultureInfo]::InvariantCulture)
$env:SAAIA_SOURCE_BACKED_AGENT_V2_REQUIRE_EVIDENCE_SELECTION =
    if ($DisableEvidenceSelectionHandoff) { "0" } else { "1" }
$samplingEnvironment = [ordered]@{
    SAAIA_LLM_TEMPERATURE = $Temperature
    SAAIA_LLM_TOP_P = $TopP
    SAAIA_LLM_TOP_K = $TopK
    SAAIA_LLM_MIN_P = $MinP
    SAAIA_LLM_FREQUENCY_PENALTY = $FrequencyPenalty
    SAAIA_LLM_PRESENCE_PENALTY = $PresencePenalty
}
foreach ($entry in $samplingEnvironment.GetEnumerator()) {
    if ($null -eq $entry.Value) {
        Remove-Item -LiteralPath ("Env:" + $entry.Key) -ErrorAction SilentlyContinue
        continue
    }

    Set-Item -LiteralPath ("Env:" + $entry.Key) -Value (
        [Convert]::ToString($entry.Value, [Globalization.CultureInfo]::InvariantCulture))
}
$env:SAAIA_LLM_STRUCTURED_SAMPLING = $StructuredSampling

$samplingArtifact = Join-Path $results "sampling-profile.json"
[ordered]@{
    schemaVersion = 1
    capturedAt = [DateTimeOffset]::UtcNow.ToString("O")
    model = $LlmModel
    temperature = $Temperature
    topP = $TopP
    topK = $TopK
    minP = $MinP
    frequencyPenalty = $FrequencyPenalty
    presencePenalty = $PresencePenalty
    structuredSampling = $StructuredSampling
    sourceBackedAgentV2 = $true
    sourceBackedAgentV2Activation = "canonical_mandatory"
    semanticCandidateAudit = [bool](-not $DisableSemanticCandidateAudit)
    semanticCandidateAuditMode = "adaptive_batched_semantic_decision"
    maximumWorkingEvidenceItems = $MaximumWorkingEvidenceItems
    maximumSemanticCandidatesPerAuditTurn = $MaximumSemanticCandidatesPerAuditTurn
    maximumSemanticCandidateAuditConcurrency = $MaximumSemanticCandidateAuditConcurrency
    evidenceSelectionHandoff = [bool](-not $DisableEvidenceSelectionHandoff)
    skipBuild = [bool]$SkipBuild
    platform = $Platform
    configuration = $Configuration
    buildEvidence = $buildEvidence
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $samplingArtifact -Encoding UTF8

$safeModelName = (($LlmModel -replace "[^A-Za-z0-9._-]", "-").Trim("-"))
if ([string]::IsNullOrWhiteSpace($safeModelName)) {
    $safeModelName = "external-model"
}

$testArguments = @(
    "test"
    $project
    "-p:Platform=$Platform"
    "-p:Configuration=$Configuration"
    "-p:UseSharedCompilation=false"
)
if ($SkipBuild) {
    $testArguments += "--no-build"
}
$testArguments += @(
    "--no-restore"
    "--filter"
    $TestFilter
    "--results-directory"
    $results
    "--logger"
    "trx;LogFileName=$safeModelName-live.trx"
    "--logger"
    "console;verbosity=minimal"
)

& dotnet @testArguments

$testExitCode = $LASTEXITCODE
if ($testExitCode -ne 0) {
    throw "Live model end-to-end test failed with exit code $testExitCode."
}

$trxPath = Join-Path $results "$safeModelName-live.trx"
if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    throw "Live model end-to-end test produced no TRX artifact: $trxPath"
}

[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$counters = $trx.TestRun.ResultSummary.Counters
$executedTests = 0
if ($null -ne $counters -and $null -ne $counters.executed) {
    [void][int]::TryParse(
        [string]$counters.executed,
        [ref]$executedTests)
}
if ($executedTests -le 0) {
    throw "Live model end-to-end filter executed zero tests: $TestFilter"
}
