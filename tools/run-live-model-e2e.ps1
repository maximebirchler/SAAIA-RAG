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
    [ValidateSet("legacy-compensated", "canonical-direct")]
    [string]$OrchestrationProfile = "legacy-compensated",
    [switch]$SourceBackedAgentV2,
    [switch]$DisableSemanticCandidateAudit,
    [int]$SemanticCandidateAuditBatchSize = 24,
    [int]$SemanticCandidateAuditTokens = 320,
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

$env:SAAIA_LIVE_VALIDATION = "1"
$env:SAAIA_VALIDATION_MANAGE_LOCAL_LLM_PROCESS = "0"
$env:SAAIA_VALIDATION_LLM_BASE_URL = $LlmBaseUrl.TrimEnd("/")
$env:SAAIA_VALIDATION_LLM_MODEL = $LlmModel
$env:SAAIA_LIVE_FINAL_TIMEOUT_MINUTES = $TimeoutMinutes.ToString(
    [Globalization.CultureInfo]::InvariantCulture)
$env:SAAIA_SOURCE_BACKED_ORCHESTRATION_PROFILE = $OrchestrationProfile
if ($SourceBackedAgentV2) {
    $env:SAAIA_SOURCE_BACKED_AGENT_V2 = "1"
    $env:SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_AUDIT = if ($DisableSemanticCandidateAudit) {
        "0"
    } else {
        "1"
    }
    $env:SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_AUDIT_BATCH_SIZE =
        $SemanticCandidateAuditBatchSize.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_AUDIT_TOKENS =
        $SemanticCandidateAuditTokens.ToString([Globalization.CultureInfo]::InvariantCulture)
} else {
    Remove-Item Env:SAAIA_SOURCE_BACKED_AGENT_V2 -ErrorAction SilentlyContinue
    Remove-Item Env:SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_AUDIT -ErrorAction SilentlyContinue
    Remove-Item Env:SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_AUDIT_BATCH_SIZE -ErrorAction SilentlyContinue
    Remove-Item Env:SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_AUDIT_TOKENS -ErrorAction SilentlyContinue
}
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
    orchestrationProfile = $OrchestrationProfile
    sourceBackedAgentV2 = [bool]$SourceBackedAgentV2
    semanticCandidateAudit = [bool](
        $SourceBackedAgentV2 -and -not $DisableSemanticCandidateAudit)
    semanticCandidateAuditBatchSize = $SemanticCandidateAuditBatchSize
    semanticCandidateAuditTokens = $SemanticCandidateAuditTokens
    skipBuild = [bool]$SkipBuild
    platform = $Platform
    configuration = $Configuration
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

if ($LASTEXITCODE -ne 0) {
    throw "Live model end-to-end test failed with exit code $LASTEXITCODE."
}
