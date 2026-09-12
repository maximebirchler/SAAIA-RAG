[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$assessor = Join-Path $PSScriptRoot "assess-advanced-capacity-results.ps1"
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$workDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $temporaryRoot ("saaia-assessor-test-" + [Guid]::NewGuid().ToString("N"))))
if (-not $workDirectory.StartsWith(
        $temporaryRoot,
        [StringComparison]::OrdinalIgnoreCase) -or
    $workDirectory -eq $temporaryRoot) {
    throw "Unsafe temporary test directory: $workDirectory"
}

function Write-MealGridFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$Answer
    )

    New-Item -ItemType Directory -Path $Directory | Out-Null
    $record = [ordered]@{
        row = [ordered]@{
            id = "A755-ADV-01-meal-grid-5x4"
            error = ""
            providerMode = "Local"
            providerModel = "local-router"
            providerCallCount = 3
            estimatedCostUsd = 0
            advancedProviderKey = "openai-dev"
            advancedProviderModel = "gpt-5.6-terra"
            advancedProviderCallCount = 2
            advancedEstimatedCostUsd = 0.01
            advancedJobId = [Guid]::NewGuid().ToString()
            advancedStatus = "succeeded"
            sourceCount = 1
            answerSource = "advanced_analysis:server"
            elapsedMs = 100
        }
        answer = $Answer
        diagnostics = [ordered]@{
            SourceLabels = @("menus.pdf - p. 1")
        }
    }
    $record | ConvertTo-Json -Depth 8 -Compress |
        Set-Content -LiteralPath (Join-Path $Directory "result.jsonl") -Encoding utf8
}

function Invoke-AssessorFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Answer,
        [Parameter(Mandatory = $true)][string]$ExpectedVerdict
    )

    $directory = Join-Path $workDirectory $Name
    Write-MealGridFixture -Directory $directory -Answer $Answer
    $null = & pwsh -NoProfile -File $assessor -ArtifactDirectory $directory 2>&1
    $exitCode = $LASTEXITCODE
    $assessment = Get-Content -Raw -LiteralPath (
        Join-Path $directory "mechanical-assessment.v1.json") | ConvertFrom-Json
    $actualVerdict = [string]$assessment.verdict
    $expectedExitCode = if ($ExpectedVerdict -eq "REJECT_MECHANICAL") { 2 } else { 0 }
    return [pscustomobject]@{
        name = $Name
        passed = $actualVerdict -eq $ExpectedVerdict -and $exitCode -eq $expectedExitCode
        detail = "$actualVerdict / exit $exitCode"
    }
}

try {
    New-Item -ItemType Directory -Path $workDirectory | Out-Null
    $results = @(
        Invoke-AssessorFixture `
            -Name "category-specific-insufficiency" `
            -Answer "Documentation insuffisante : les cinq cases de collation ne peuvent pas être remplies avec des options distinctes. [C1]" `
            -ExpectedVerdict "PASS_MECHANICAL_REQUIRES_SEMANTIC_REVIEW"
        Invoke-AssessorFixture `
            -Name "first-person-insufficiency" `
            -Answer "Je ne peux pas produire le planning complet sans invention : les cinq cases de souper ne disposent pas de cinq propositions étayées. [C1]" `
            -ExpectedVerdict "PASS_MECHANICAL_REQUIRES_SEMANTIC_REVIEW"
        Invoke-AssessorFixture `
            -Name "incomplete-answer" `
            -Answer "Voici une proposition partielle pour lundi : soupe. [C1]" `
            -ExpectedVerdict "REJECT_MECHANICAL"
    )
    $results | Format-Table -AutoSize
    $failed = @($results | Where-Object { -not $_.passed })
    Write-Output "Passed: $($results.Count - $failed.Count)/$($results.Count)"
    if ($failed.Count -gt 0) {
        throw "Advanced-capacity assessor regression test failed."
    }
}
finally {
    if (Test-Path -LiteralPath $workDirectory) {
        $resolvedWorkDirectory = [System.IO.Path]::GetFullPath($workDirectory)
        if (-not $resolvedWorkDirectory.StartsWith(
                $temporaryRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
            $resolvedWorkDirectory -eq $temporaryRoot) {
            throw "Refusing to remove unsafe path: $resolvedWorkDirectory"
        }
        Remove-Item -LiteralPath $resolvedWorkDirectory -Recurse -Force
    }
}
