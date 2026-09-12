[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [string]$ExpectedProviderMode = "OpenAiDev",
    [string]$ExpectedModel = "gpt-5.6-terra"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
$jsonlFiles = @(Get-ChildItem -LiteralPath $ArtifactDirectory -Recurse -Filter "*.jsonl" | Sort-Object FullName)
if ($jsonlFiles.Count -eq 0) {
    throw "No agent-bank JSONL result was found in $ArtifactDirectory."
}

function Test-ContainsAll([string]$Text, [string[]]$Values) {
    foreach ($value in $Values) {
        if ($Text -notmatch [regex]::Escape($value)) { return $false }
    }
    return $true
}

function Get-CitationStats([string]$Answer) {
    $matches = @([regex]::Matches($Answer, '\[(?:E|C)\d+\]', 'IgnoreCase'))
    return [ordered]@{
        occurrences = $matches.Count
        distinct = @($matches | ForEach-Object Value | Sort-Object -Unique).Count
    }
}

function Get-MealGridStats([string]$Answer) {
    $rows = @($Answer -split "`r?`n" | Where-Object { $_ -match '^\s*\|' })
    $dataRows = @($rows | Where-Object {
        $_ -match '(?i)\b(lundi|mardi|mercredi|jeudi|vendredi)\b'
    })
    $cells = @()
    foreach ($row in $dataRows) {
        $parts = @($row.Trim().Trim('|') -split '\|' | ForEach-Object Trim)
        if ($parts.Count -ge 5) { $cells += $parts[1..4] }
    }
    $normalizedCells = @($cells | ForEach-Object {
        ([regex]::Replace($_, '\[(?:E|C)\d+\]', '', 'IgnoreCase')).Trim().ToLowerInvariant()
    } | Where-Object { $_ })
    return [ordered]@{
        markdownRows = $dataRows.Count
        mealCells = $cells.Count
        distinctMealCells = @($normalizedCells | Sort-Object -Unique).Count
    }
}

function Test-LooksLikeExactInsufficiency([string]$Answer) {
    $directInsufficiency = $Answer -match '(?i)\b(?:manqu\p{L}*|insuffis\p{L}*|absent\p{L}*|impossible|pas\s+assez|pas\s+suffisamment|non\s+(?:document|[eé]tay)\p{L}*|ne\s+(?:contient|contiennent|dispose|disposent|documentent|fournit|fournissent|permet(?:tent)?|peux|peut|peuvent)\s+(?:donc\s+)?pas|sans\s+fournir|not\s+enough|cannot|missing|insufficient)\b'
    $evidenceSubject = '(?:sources?|extraits?|documents?|documentation|preuves?|[eé]l[eé]ments?|corpus)'
    $sourceBoundPassiveInsufficiency = $Answer -match (
        '(?i)(?:\bne\b|n[''\u2019])[^.!?]{0,100}\b' +
        '(?:pas|aucun(?:e|s|es)?|insuffis\p{L}*|manqu\p{L}*|absent\p{L}*)\b' +
        '[^.!?]{0,160}\b(?:par|dans|selon)\s+' +
        '(?:(?:le|la|les|un|une|des|du|de\s+la)\s+)?' +
        $evidenceSubject + '\b')
    return ($directInsufficiency -or $sourceBoundPassiveInsufficiency) -and
        $Answer -notmatch '(?i)capacit[eé].*avanc[eé]e|advanced analysis'
}

function Get-ExpectedProviderKey([string]$Mode) {
    switch ($Mode.Trim().ToLowerInvariant()) {
        "openaidev" { return "openai-dev" }
        "openai-dev" { return "openai-dev" }
        "runpod" { return "runpod-bench" }
        "runpod-bench" { return "runpod-bench" }
        "customerserver" { return "customer-server" }
        "customer-server" { return "customer-server" }
        default { return $Mode.Trim() }
    }
}

$assessments = @()
$repetition = 0
foreach ($file in $jsonlFiles) {
    $repetition++
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $record = $line | ConvertFrom-Json
        $row = $record.row
        $answer = [string]$record.answer
        $citations = Get-CitationStats $answer
        $usesAdvancedTelemetry = $null -ne $row.PSObject.Properties["advancedProviderKey"] -and
            -not [string]::IsNullOrWhiteSpace([string]$row.advancedProviderKey)
        $expectedProviderKey = Get-ExpectedProviderKey $ExpectedProviderMode
        $observedProvider = if ($usesAdvancedTelemetry) {
            [string]$row.advancedProviderKey
        } else {
            [string]$row.providerMode
        }
        $observedModel = if ($usesAdvancedTelemetry) {
            [string]$row.advancedProviderModel
        } else {
            [string]$row.providerModel
        }
        $observedCallCount = if ($usesAdvancedTelemetry) {
            [int]$row.advancedProviderCallCount
        } else {
            [int]$row.providerCallCount
        }
        $observedCostUsd = if ($usesAdvancedTelemetry) {
            [decimal]$row.advancedEstimatedCostUsd
        } else {
            [decimal]$row.estimatedCostUsd
        }
        $advancedJobId = if ($null -ne $row.PSObject.Properties["advancedJobId"]) {
            [string]$row.advancedJobId
        } else { "" }
        $parsedAdvancedJobId = [Guid]::Empty
        $advancedJobIdIsValid = [Guid]::TryParse(
            $advancedJobId,
            [ref]$parsedAdvancedJobId)
        $looksLikeExactInsufficiency = Test-LooksLikeExactInsufficiency $answer
        $checks = [ordered]@{
            noHarnessError = [string]::IsNullOrWhiteSpace([string]$row.error)
            expectedProviderMode = $observedProvider -eq $expectedProviderKey -or
                $observedProvider -eq $ExpectedProviderMode
            expectedModel = $observedModel -eq $ExpectedModel
            providerWasCalled = $observedCallCount -gt 0
            costWasMeasured = $observedCostUsd -gt 0
            advancedJobIdentityRecorded = -not $usesAdvancedTelemetry -or $advancedJobIdIsValid
            advancedTerminalSucceeded = -not $usesAdvancedTelemetry -or
                [string]$row.advancedStatus -eq "succeeded"
            noLocalAdvancedHandoff = [string]$row.answerSource -notmatch 'capability_boundary:advanced_analysis_required'
            nonEmptyAnswer = -not [string]::IsNullOrWhiteSpace($answer)
            evidenceOrExactInsufficiency = [int]$row.sourceCount -gt 0 -or $looksLikeExactInsufficiency
        }
        $details = [ordered]@{ citations = $citations }

        switch ([string]$row.id) {
            "A755-ADV-01-meal-grid-5x4" {
                $grid = Get-MealGridStats $answer
                $details.mealGrid = $grid
                if (-not $looksLikeExactInsufficiency) {
                    $checks.fiveDaysNamed = Test-ContainsAll $answer @("lundi", "mardi", "mercredi", "jeudi", "vendredi")
                    $checks.fourMealMomentsNamed = Test-ContainsAll $answer @("petit-déjeuner", "déjeuner", "collation", "souper")
                    $checks.fiveMarkdownDataRows = $grid.markdownRows -eq 5
                    $checks.twentyMealCells = $grid.mealCells -eq 20
                    $checks.twentyDistinctMealCells = $grid.distinctMealCells -eq 20
                    $checks.twentyCitations = $citations.occurrences -ge 20
                }
            }
            "A755-ADV-02-five-student-meals-fr" {
                if (-not $looksLikeExactInsufficiency) {
                    $checks.fiveCitedItems = $citations.occurrences -ge 5
                    $checks.fiveDistinctEvidenceReferences = $citations.distinct -ge 5
                }
            }
            "A755-ADV-03-explicit-document-comparison" {
                if (-not $looksLikeExactInsufficiency) {
                    $checks.twoDocumentsNamed = Test-ContainsAll $answer @("15281", "60079-14")
                    $checks.twoSourceLabels = @($record.diagnostics.SourceLabels | Sort-Object -Unique).Count -ge 2
                }
            }
            "A755-ADV-04-nist-seven-points" {
                if (-not $looksLikeExactInsufficiency) {
                    $checks.nistSourceOnly = @($record.diagnostics.SourceLabels | Where-Object { $_ -notmatch 'NIST_CSF_2_0\.pdf' }).Count -eq 0
                    $checks.sevenCitations = $citations.occurrences -ge 7
                }
            }
        }

        $failedChecks = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value } | ForEach-Object Key)
        $assessments += [ordered]@{
            repetition = $repetition
            id = [string]$row.id
            mechanicalVerdict = if ($failedChecks.Count -eq 0) { "PASS_REQUIRES_SEMANTIC_REVIEW" } else { "FAIL_MECHANICAL" }
            failedChecks = $failedChecks
            checks = $checks
            details = $details
            answerSource = [string]$row.answerSource
            advancedJobId = $advancedJobId
            sourceLabels = @($record.diagnostics.SourceLabels)
            telemetrySource = if ($usesAdvancedTelemetry) { "advanced" } else { "direct" }
            observedProvider = $observedProvider
            observedModel = $observedModel
            callCount = $observedCallCount
            estimatedCostUsd = $observedCostUsd
            elapsedMs = [long]$row.elapsedMs
        }
    }
}

$failed = @($assessments | Where-Object mechanicalVerdict -eq "FAIL_MECHANICAL")
$result = [ordered]@{
    schemaVersion = "saaia-advanced-capacity-mechanical-assessment-v1"
    assessedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    artifactDirectory = $ArtifactDirectory
    resultFiles = @($jsonlFiles | ForEach-Object FullName)
    verdict = if ($failed.Count -eq 0) { "PASS_MECHANICAL_REQUIRES_SEMANTIC_REVIEW" } else { "REJECT_MECHANICAL" }
    productStatus = "TESTE_NON_APPROUVE"
    rows = $assessments.Count
    failures = $failed.Count
    warning = "This assessor checks structure and provenance signals only. It cannot approve factual support or semantic quality."
    cases = $assessments
}
$assessmentPath = Join-Path $ArtifactDirectory "mechanical-assessment.v1.json"
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $assessmentPath -Encoding utf8
Write-Output "Assessment: $assessmentPath"
Write-Output "Verdict: $($result.verdict)"
if ($failed.Count -gt 0) {
    $failed | ForEach-Object { Write-Output ("FAIL " + $_.id + ": " + ($_.failedChecks -join ", ")) }
    exit 2
}
