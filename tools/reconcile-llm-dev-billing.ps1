param(
    [Parameter(Mandatory=$true)][decimal]$ObservedOrganizationSpendUsd,
    [Parameter(Mandatory=$true)][decimal]$ObservedCreditBalanceUsd,
    [Parameter(Mandatory=$true)][decimal]$PurchasedCreditsCeilingUsd,
    [Parameter(Mandatory=$true)][datetimeoffset]$ObservedAtUtc,
    [string]$LedgerPath = (Join-Path $env:LOCALAPPDATA 'SAAIA/llm-dev/openai-terra-usage.jsonl'),
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($ObservedOrganizationSpendUsd -lt 0 -or $ObservedCreditBalanceUsd -lt 0 -or
    $PurchasedCreditsCeilingUsd -le 0 -or
    $ObservedOrganizationSpendUsd + $ObservedCreditBalanceUsd -gt $PurchasedCreditsCeilingUsd) {
    throw 'The observation must fit within already purchased credits.'
}
$taskAge = [datetimeoffset]::UtcNow - $ObservedAtUtc.ToUniversalTime()
if ($taskAge.TotalMinutes -gt 15 -or $taskAge.TotalMinutes -lt -1) {
    throw 'A fresh read-only billing observation is required.'
}
if (@(Get-NetTCPConnection -LocalPort 1234,5123 -State Listen -ErrorAction SilentlyContinue).Count) {
    throw 'Stop the owned paid diagnostic campaign before reconciling its ledger.'
}
$taskPath = [IO.Path]::GetFullPath($LedgerPath)
$taskStream = [IO.File]::Open($taskPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $taskReader = [IO.StreamReader]::new($taskStream, [Text.UTF8Encoding]::new($false), $true, 4096, $true)
    try { $taskText = $taskReader.ReadToEnd() } finally { $taskReader.Dispose() }
    [decimal]$taskTotal = 0
    foreach ($taskLine in ($taskText -split '\r?\n')) {
        if ([string]::IsNullOrWhiteSpace($taskLine)) { continue }
        $taskEntry = $taskLine | ConvertFrom-Json
        $taskCostProperty = $taskEntry.PSObject.Properties['costUsd']
        if ($null -eq $taskCostProperty) { continue }
        $taskCost = [decimal]$taskCostProperty.Value
        if ($taskCost -lt 0) { throw 'Negative ledger costs are not supported.' }
        $taskTotal += $taskCost
    }
    # This only raises the accounting floor. It does not reinterpret historic
    # API calls, reimburse conservative estimates, or purchase credits.
    [decimal]$taskDifference = $ObservedOrganizationSpendUsd - $taskTotal
    $taskAdjustment = if ($taskDifference -gt 0) { [decimal]::Round($taskDifference, 8) } else { [decimal]0 }
    $taskSnapshotHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($taskText)))
    $taskRecord = [ordered]@{
        recordType = 'billing_reconciliation'
        timestampUtc = [datetimeoffset]::UtcNow.ToString('o')
        observedAtUtc = $ObservedAtUtc.ToUniversalTime().ToString('o')
        provider = 'billing-observation'
        usageSource = 'observed_organization_spend_conservative_floor'
        observationSource = 'https://platform.openai.com/settings/organization/billing/overview'
        observedOrganizationSpendUsd = $ObservedOrganizationSpendUsd
        observedCreditBalanceUsd = $ObservedCreditBalanceUsd
        purchasedCreditsCeilingUsd = $PurchasedCreditsCeilingUsd
        ledgerTotalBeforeUsd = $taskTotal
        ledgerTextSha256Before = $taskSnapshotHash
        costUsd = $taskAdjustment
        apiCall = $false
        purchasesPerformed = $false
        explanation = 'Organization-wide observed spend is a conservative floor, not a per-call invoice or proof of the cause of the historical gap.'
    }
    if ($Apply -and $taskAdjustment -gt 0) {
        $taskStream.Position = $taskStream.Length
        $taskPrefix = if ($taskText.Length -gt 0 -and !$taskText.EndsWith("`n")) { "`n" } else { '' }
        $taskBytes = [Text.Encoding]::UTF8.GetBytes($taskPrefix + ($taskRecord | ConvertTo-Json -Compress) + "`n")
        $taskStream.Write($taskBytes, 0, $taskBytes.Length)
        $taskStream.Flush($true)
    }
    [ordered]@{
        applied = [bool]($Apply -and $taskAdjustment -gt 0)
        adjustmentUsd = $taskAdjustment
        ledgerTotalBeforeUsd = $taskTotal
        conservativeLedgerTotalAfterUsd = $taskTotal + $(if ($Apply) { $taskAdjustment } else { 0 })
        observedCreditBalanceUsd = $ObservedCreditBalanceUsd
        newPurchasesPerformed = $false
        apiCallsPerformed = 0
    } | ConvertTo-Json
} finally { $taskStream.Dispose() }
