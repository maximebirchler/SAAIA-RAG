#requires -Version 7.4
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'PlanningCampaign.Common.ps1')
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$testParent=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts/planning-harness-tests'))
$testRoot=Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot)|Out-Null
$checks=0
function Check([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message};$script:checks++}
function Copy-Profile($Value){return $Value|ConvertTo-Json -Depth 40|ConvertFrom-Json -AsHashtable -Depth 40}
function Reject($Value,[string]$Reason){
    $thrown=$false
    try{$null=Assert-PlanningProfile -Profile $Value -RepositoryRoot $repo}catch{$thrown=$true}
    Check $thrown $Reason
}
try{
    $manifest=Join-Path $testRoot 'synthetic-manifest.json'
    [IO.File]::WriteAllText($manifest,'{"schemaVersion":"synthetic-only-for-profile-guards"}')
    $profilePath=Join-Path $testRoot 'planning-profile.json'
    # Only this pure profile test supplies a synthetic assembly identity. No
    # backend binary is required or executed; all real file hashes stay real.
    $backendAssemblyPath=[IO.Path]::GetFullPath((Join-Path $repo 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'))
    $syntheticBackendSha='A'*64
    $syntheticHashState=[pscustomobject]@{Reads=0}
    function Get-FileHash {
        [CmdletBinding()]
        param([Parameter(Mandatory)][string]$LiteralPath,[string]$Algorithm='SHA256')
        if([IO.Path]::GetFullPath($LiteralPath) -ceq $backendAssemblyPath){
            if($Algorithm -ine 'SHA256'){throw 'The synthetic assembly identity supports SHA256 only.'}
            $syntheticHashState.Reads++
            return [pscustomobject]@{Hash=$syntheticBackendSha;Path=$LiteralPath;Algorithm='SHA256'}
        }
        return Microsoft.PowerShell.Utility\Get-FileHash @PSBoundParameters
    }
    try{
        & (Join-Path $PSScriptRoot 'new-planning-campaign-profile.ps1') -OutputPath $profilePath -CampaignRoot (Join-Path $testRoot 'envelope') -CorpusManifestPath $manifest |Out-Null
    }finally{Remove-Item Function:\Get-FileHash}
    $p=Get-ValidatedPlanningProfile -ProfilePath $profilePath -RepositoryRoot $repo
    Check ($syntheticHashState.Reads -eq 1 -and $p.execution.backendAssemblySha256 -ceq $syntheticBackendSha) 'Only the single backend assembly identity may be synthetic in this pure profile test.'
    Check ($p.corpus.manifestSha256 -ceq (Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash) 'The synthetic backend hash must never replace the actual manifest hash.'
    $handoff=New-PlanningHandoff -Profile $p
    Check ($handoff.load.answerUnitCount -eq 20 -and $handoff.load.rowCount -eq 5 -and $handoff.load.columnCount -eq 4) 'The real planning load must remain five by four.'
    Check ($p.case.selectionPolicy -ceq 'structured_layout' -and $handoff.load.selectionPolicy -ceq $p.case.selectionPolicy -and $handoff.researchState.evidenceReferences.Count -eq 0 -and $handoff.transferStage -ceq 'before_retrieval') 'The historical grid may reuse supported recipes without replacing exploration or coordinates.'
    $oneDay=Copy-Profile $p;$oneDay.case.kind='one-day-4';$oneDay.case.rowLabels=@('lundi');$oneDay.case.answerUnitCount=4
    $oneDay.case.functionalFiveDayAcceptance=$false;$oneDay.case.question='Quatre propositions concrètes distinctes.';$oneDay.case.selectionPolicy='distinct_structured_layout'
    $null=Assert-PlanningProfile -Profile $oneDay -RepositoryRoot $repo
    Check ((New-PlanningHandoff -Profile $oneDay).load.selectionPolicy -ceq 'distinct_structured_layout') 'The explicit four-distinct diagnostic must retain its diversity requirement.'
    $q=Copy-Profile $oneDay;$q.case.selectionPolicy='structured_layout';Reject $q 'The explicit distinct diagnostic cannot silently allow repetitions.'
    $q=Copy-Profile $p;$q.case.Remove('selectionPolicy');Reject $q 'Old profiles without a declared selection policy require a newly frozen profile.'
    $q=Copy-Profile $p;$q.case.selectionPolicy='distinct_structured_layout';Reject $q 'The historical question cannot silently acquire a twenty-distinct requirement.'
    $options=Get-PlanningAdvancedOptions -Profile $p -OutputDirectory $testRoot -RepositoryRoot $repo
    Check ($options.NativeResearchToolsEnabled -and $options.NativeObservationReadEnabled -and $options.NativeResearchApiProtocol -ceq 'responses') 'Planning must expose the production native/RAW protocol.'
    Check ($options.ExternalBudgetHardLimitUsd -eq [decimal]0.49321314 -and $options.ExternalUsageLedgerPath -eq $p.budget.sharedLedgerPath) 'The durable planning envelope must include the historical baseline exactly once.'
    Check ($options.WriterMaxTokens -eq 8192 -and $options.MaximumEvidencePromptCharacters -eq 65536 -and $options.MaximumToolCalls -eq 128) 'Reviewed explicit planning limits were not preserved.'
    $q=Copy-Profile $p;$q.budget.baselineCostUsd=0;Reject $q 'A fresh profile cannot erase the earlier authorized expenditure.'
    $q=Copy-Profile $p;$q.budget.authorizedUsd=1;Reject $q 'A profile cannot create another or larger budget.'
    $q=Copy-Profile $p;$q.pricing.cacheWriteUsdPerMillion=0.25;Reject $q 'Base cache-write rates must not understate the conservative envelope.'
    $q=Copy-Profile $p;$q.case.answerUnitCount=1;Reject $q 'The factual single-item test must not masquerade as a planning grid.'
    $q=Copy-Profile $p;$q.execution.maximumCallsPerJob=65;Reject $q 'The call envelope must remain bounded.'
    $q=Copy-Profile $p;$q.provider.model='gpt-5.6-terra';Reject $q 'The selected economical model must not escalate silently.'
    $q=Copy-Profile $p;$q.budget.sharedLedgerPath=Join-Path $testRoot 'second-ledger.jsonl';Reject $q 'Every attempt must use the one shared ledger.'
    $blocked=$false
    try{Assert-PlanningExecuteAuthorization -Profile $p -AuthorizePaid -AuthorizeCorpusTransmission -PaidCapUsd 0.50}catch{$blocked=$true}
    Check $blocked 'A proposed profile must stop before provider credential access.'
    $ledger=$p.budget.sharedLedgerPath
    [IO.Directory]::CreateDirectory((Split-Path -Parent $ledger))|Out-Null
    $row=@{requestId='one';costUsd=0.10;httpAttemptCount=1;modelId='gpt-5.6-luna';observedModelId='gpt-5.6-luna';usageSource='provider_usage'}|ConvertTo-Json -Compress
    [IO.File]::WriteAllText($ledger,$row+[Environment]::NewLine)
    $summary=Get-PlanningEnvelopeLedgerSummary -Profile $p
    Check ($summary.totalBoundedCampaignCostUsd -eq [decimal]0.10678686) 'An earlier planning attempt must remain charged on the next read.'
    $summaryAgain=Get-PlanningEnvelopeLedgerSummary -Profile (Copy-Profile $p)
    Check ($summaryAgain.totalBoundedCampaignCostUsd -eq $summary.totalBoundedCampaignCostUsd) 'Reloading a profile must not reset cumulative charges.'
    $tokens=$null;$errors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'invoke-local-planning-campaign.ps1'),[ref]$tokens,[ref]$errors)
    Check ($errors.Count -eq 0) 'The planning runner must parse without starting a runtime.'
    Write-Output "$checks planning guard checks passed; no secret, network, provider, runtime or build."
}finally{
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if(-not $resolved.StartsWith($testParent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test cleanup target.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
