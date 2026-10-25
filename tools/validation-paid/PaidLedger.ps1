#requires -Version 7.4
# Read-only, provider-independent projection of the durable reservation contract.
function Get-PaidLedgerSummary {
    param([Parameter(Mandatory)][string]$LedgerPath, [decimal]$PriorCampaignCostUsd=0,
        [decimal]$AuthorizedCapUsd=0.50)
    $rows=@(if(Test-Path -LiteralPath $LedgerPath){Get-Content -LiteralPath $LedgerPath|Where-Object{-not [string]::IsNullOrWhiteSpace($_)}|ForEach-Object{$_|ConvertFrom-Json -AsHashtable}})
    $startsPath=$LedgerPath+'.reservations.jsonl'
    $starts=@(if(Test-Path -LiteralPath $startsPath){Get-Content -LiteralPath $startsPath|Where-Object{-not [string]::IsNullOrWhiteSpace($_)}|ForEach-Object{$_|ConvertFrom-Json -AsHashtable}})
    $settled=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $started=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $terminalCost=[decimal]0; $attempts=0; $uncertainSettled=0; $attemptViolations=0; $requestedModelMismatches=0
    foreach($row in $rows){
        if(-not $row.ContainsKey('costUsd') -or [decimal]$row.costUsd -lt 0 -or [string]::IsNullOrWhiteSpace($row.requestId) -or
            -not $settled.Add([string]$row.requestId)){throw 'Invalid or duplicate terminal ledger charge.'}
        $terminalCost+=[decimal]$row.costUsd
        $attempts+=[int]$row.httpAttemptCount
        if([int]$row.httpAttemptCount -lt 0 -or [int]$row.httpAttemptCount -gt 1){$attemptViolations++}
        if([string]$row.modelId -cne 'gpt-5.6-luna'){$requestedModelMismatches++}
        if($row.usageSource -eq 'reserved_upper_bound'){$uncertainSettled++}
    }
    $unmatched=[Collections.Generic.List[object]]::new()
    foreach($start in $starts){
        if($start.schema -ne 'advanced-budget-reservation.v1' -or [string]::IsNullOrWhiteSpace($start.RequestId) -or
            [decimal]$start.ReservedCostUsd -lt 0 -or -not $started.Add([string]$start.RequestId)){
            throw 'Invalid or duplicate durable reservation.'
        }
        if(-not $settled.Contains([string]$start.RequestId)){$unmatched.Add($start)}
    }
    $unsettled=[decimal]0
    foreach($reservation in $unmatched){$unsettled+=[decimal]$reservation.ReservedCostUsd}
    $observed=@($rows|Where-Object{-not [string]::IsNullOrWhiteSpace($_.observedModelId)}|ForEach-Object{[string]$_.observedModelId}|Sort-Object -Unique)
    $mismatches=@($observed|Where-Object{$_ -cnotmatch '^gpt-5\.6-luna(?:-\d{4}-\d{2}-\d{2})?$'})
    $bounded=$PriorCampaignCostUsd+$terminalCost+$unsettled
    return [ordered]@{schemaVersion='saaia-paid-ledger-summary.v1';capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
        logicalTerminalLedgerRows=$rows.Count;durableReservationStarts=$starts.Count;unsettledReservationCount=$unmatched.Count;
        terminalEstimatedCostUsd=$terminalCost;unsettledReservedUsd=$unsettled;priorCampaignCostUsd=$PriorCampaignCostUsd;
        totalBoundedCampaignCostUsd=$bounded;authorizedIncrementalCapUsd=$AuthorizedCapUsd;withinAuthorizedCap=($bounded -le $AuthorizedCapUsd);
        httpAttemptsInTerminalRows=$attempts;httpAttemptPolicyViolations=$attemptViolations;requestedModelMismatchCount=$requestedModelMismatches;
        observedModels=$observed;observedModelMismatchCount=$mismatches.Count;
        observedExpectedFamily=($observed.Count -gt 0 -and $mismatches.Count -eq 0);settledUpperBoundChargeCount=$uncertainSettled;
        state=if($unmatched.Count -gt 0 -or $uncertainSettled -gt 0){'UNCERTAIN_RESERVED'}else{'SETTLED_LOCAL_ESTIMATE'};
        providerBalanceKnown=$false;providerInvoiceReconciled=$false;
        ledgerSha256=if(Test-Path -LiteralPath $LedgerPath){(Get-FileHash -LiteralPath $LedgerPath).Hash}else{$null};
        reservationsSha256=if(Test-Path -LiteralPath $startsPath){(Get-FileHash -LiteralPath $startsPath).Hash}else{$null}}
}
