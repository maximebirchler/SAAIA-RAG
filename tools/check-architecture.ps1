#requires -Version 7.4
[CmdletBinding()]
param ([string]$OutputPath = '', [string]$BudgetPath = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $BudgetPath) {
    $BudgetPath = Join-Path $repo 'config/architecture-budgets.json'
}

$budget = Get-Content -LiteralPath $BudgetPath -Raw | ConvertFrom-Json
$tracked = @(& git -C $repo ls-files --cached --others --exclude-standard) | Select-Object -Unique
$source = @($tracked | Where-Object {
        $_ -match '\.(cs|xaml|ps1|psm1|py|js|ts|sql)$' -and $_ -notmatch '(^|/)(bin|obj|node_modules|artifacts)/' -and (Test-Path -LiteralPath (Join-Path $repo $_))
    })
$metrics = [Collections.Generic.List[object]]::new()
$violations = [Collections.Generic.List[object]]::new()
$reviews = [Collections.Generic.List[object]]::new()
$typeTotals = @{}
$typeFiles = @{}

foreach ($type in $budget.partialTypeBudgets) {
    $typeTotals[$type.name] = 0
    $typeFiles[$type.name] = [Collections.Generic.List[string]]::new()
}

foreach ($relative in $source) {
    $text = [IO.File]::ReadAllText((Join-Path $repo $relative))
    $lines = $text.Length -eq 0 ? 0 : ($text -split "`n").Count - [int]$text.EndsWith("`n")

    $entry = @{
        path = $relative
        physicalLines = $lines
    }

    $metrics.Add($entry)
    if ($lines -gt $budget.maximumPhysicalLinesPerFile) {
        $violations.Add(@{
                rule = 'maximum_file_size'
                path = $relative
                observed = $lines
                maximum = $budget.maximumPhysicalLinesPerFile
            })
    } elseif ($lines -gt $budget.reviewPhysicalLinesPerFile) {
        $reviews.Add($entry)
    }

    if ($relative.EndsWith('.cs')) {
        foreach ($type in $budget.partialTypeBudgets) {
            $declaration = '\bpartial\s+(?:class|record)\s+' + [regex]::Escape($type.name) + '\b'
            if ($text -match $declaration) {
                $typeTotals[$type.name] += $lines
                $typeFiles[$type.name].Add($relative)
            }
        }
    }
}

$partial = @(foreach ($type in $budget.partialTypeBudgets) {
        if ($typeTotals[$type.name] -gt $type.maximumPhysicalLines) {
            $violations.Add(@{
                    rule = 'partial_type_budget'
                    type = $type.name
                    observed = $typeTotals[$type.name]
                    maximum = $type.maximumPhysicalLines
                })
        }

        @{
            name = $type.name
            physicalFileLines = $typeTotals[$type.name]
            budget = $type.maximumPhysicalLines
            files = $typeFiles[$type.name]
            reason = $type.reason
        }
    })
$report = @{
    schemaVersion = 'saaia-architecture-check-v1'
    gitCommit = (& git -C $repo rev-parse HEAD)
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    status = $(if ($violations.Count) {
            'failed'
        } else {
            'passed'
        })
    methodology = 'Working-tree source files (tracked and non-ignored new files), physical lines; partial totals include imports and are not semantic complexity.'
    sourceCount = $metrics.Count
    violations = $violations
    reviewRequired = $reviews
    partialTypeBudgets = $partial
    metrics = $metrics
}

if ($OutputPath) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath)))) | Out-Null
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
}

foreach ($violation in $violations) {
    Write-Error ($violation | ConvertTo-Json -Compress) -ErrorAction Continue
}

Write-Host "architecture: $($report.status); sources: $($metrics.Count); files requiring size review: $($reviews.Count)"
if ($violations.Count) {
    exit 1
}

exit 0
