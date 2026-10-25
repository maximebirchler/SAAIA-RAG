#requires -Version 7.4
# Pure guards: no secret, process or network access in this file.
function ConvertTo-PaidUtcStamp {
    param([object]$Value)
    if ($Value -is [DateTime] -and $Value.Kind -eq [DateTimeKind]::Utc) { return [DateTimeOffset]$Value }
    if ($Value -is [DateTimeOffset] -and $Value.Offset -eq [TimeSpan]::Zero) { return $Value }
    $stamp=[DateTimeOffset]::MinValue
    if ([string]$Value -notmatch 'Z$' -or -not [DateTimeOffset]::TryParse([string]$Value,[ref]$stamp)) {
        throw 'A valid explicit UTC timestamp is required.'
    }
    return $stamp
}
function Assert-LegacyPaidAuthorization {
    param([Parameter(Mandatory)][string]$Model, [Parameter(Mandatory)][decimal]$BudgetUsd)
    if ([string]::IsNullOrWhiteSpace($Model) -or $BudgetUsd -le 0) {
        throw 'An explicit model and positive authorized budget are required; no paid defaults are assumed.'
    }
    $cap = [decimal]0
    if ($env:SAAIA_TEST_PROFILE -ne 'LivePaid' -or $env:SAAIA_TEST_PAID_AUTHORIZED -ne '1' -or
        $env:SAAIA_TEST_TRANSMISSION_AUTHORIZED -ne '1' -or
        -not [decimal]::TryParse($env:SAAIA_TEST_PAID_CAP_USD, [Globalization.NumberStyles]::Number,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$cap) -or $cap -lt $BudgetUsd) {
        throw 'Use an explicitly authorized LivePaid campaign with a cap covering the requested budget. No secret was read.'
    }
}

function Assert-LiveTestEvidence {
    param([Parameter(Mandatory)][string]$TrxPath, [Parameter(Mandatory)][string]$ResultPath,
        [int]$ExpectedTests = 1, [string[]]$ExpectedCaseIds = @())
    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
        throw 'Live execution requires both a new TRX and a result artifact; exit code zero alone is insufficient.'
    }
    [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
    $cases = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($cases.Count -ne $ExpectedTests -or @($cases | Where-Object outcome -ne 'Passed').Count -ne 0) {
        throw 'Every explicitly selected live test must actually pass; missing, skipped and inconclusive tests are rejected.'
    }
    $result = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json -Depth 100
    if ($null -eq $result) { throw 'A nonempty live result is required.' }
    if ($ExpectedCaseIds.Count -gt 0) {
        $rows = @($result.rows)
        $actualIds = @($rows | ForEach-Object { [string]$_.id })
        if ($rows.Count -ne $ExpectedCaseIds.Count -or
            @($ExpectedCaseIds | Where-Object { $_ -notin $actualIds }).Count -gt 0 -or
            @($actualIds | Group-Object | Where-Object Count -ne 1).Count -gt 0) {
            throw 'The live result must contain every selected case exactly once.'
        }
    }
    return [ordered]@{ executed = $cases.Count; passed = $cases.Count; notExecuted = 0;
        trxSha256 = (Get-FileHash -LiteralPath $TrxPath -Algorithm SHA256).Hash;
        resultSha256 = (Get-FileHash -LiteralPath $ResultPath -Algorithm SHA256).Hash }
}

function Get-ValidatedLunaCampaignProfile {
    param([Parameter(Mandatory)][string]$ProfilePath, [Parameter(Mandatory)][string]$RepositoryRoot)
    $p = Get-Content -LiteralPath $ProfilePath -Raw | ConvertFrom-Json -AsHashtable -Depth 40
    if ($p.schemaVersion -ne 'saaia-local-openai-paid-campaign.v1' -or
        $p.provider.mode -ne 'OpenAI' -or $p.provider.model -cne 'gpt-5.6-luna' -or
        $p.provider.baseUrl -cne 'https://api.openai.com/v1' -or $p.provider.reasoningEffort -cne 'none') {
        throw 'This local economical campaign accepts only gpt-5.6-luna at the exact official OpenAI endpoint.'
    }
    $cap = [decimal]$p.budget.authorizedUsd
    if ($cap -le 0 -or $cap -gt [decimal]0.50 -or [decimal]$p.budget.hardStopUsd -ne $cap -or
        [decimal]$p.budget.priorCampaignCostUsd -lt 0 -or [decimal]$p.budget.priorCampaignCostUsd -ge $cap -or
        [decimal]$p.budget.maximumCostPerJobUsd -le 0 -or
        [decimal]$p.budget.maximumCostPerJobUsd -gt ($cap - [decimal]$p.budget.priorCampaignCostUsd)) {
        throw 'The incremental campaign, including its availability probe, must fit within at most 0.50 USD.'
    }
    if ([int]$p.execution.maximumHttpAttempts -ne 1 -or [int]$p.execution.maximumJobAttempts -ne 1 -or
        [int]$p.execution.repetitions -ne 1 -or [int]$p.execution.maximumJobs -ne 1 -or
        [int]$p.execution.maximumCallsPerJob -lt 2 -or [int]$p.execution.maximumCallsPerJob -gt 4 -or
        [int]$p.execution.plannerMaxTokens -lt 256 -or [int]$p.execution.plannerMaxTokens -gt 512 -or
        [int]$p.execution.writerMaxTokens -lt 512 -or [int]$p.execution.writerMaxTokens -gt 1024 -or
        [int]$p.execution.timeoutSeconds -lt 30 -or [int]$p.execution.timeoutSeconds -gt 300) {
        throw 'One job/repetition and one HTTP/job attempt are mandatory; small explicit call/token/time bounds are required.'
    }
    $ports = @($p.execution.backendPort, $p.execution.fixturePort, $p.execution.paidBackendPort)
    if (@($ports | Where-Object { [int]$_ -lt 1024 -or [int]$_ -gt 65535 -or [int]$_ -eq 5432 }).Count -gt 0 -or
        @($ports | Sort-Object -Unique).Count -ne 3) { throw 'Three distinct nonproduction local ports are required.' }
    $prices = $p.pricing
    if ([decimal]$prices.inputUsdPerMillion -ne [decimal]0.20 -or [decimal]$prices.cachedInputUsdPerMillion -ne [decimal]0.02 -or
        [decimal]$prices.outputUsdPerMillion -ne [decimal]1.20 -or [decimal]$prices.cacheWriteUsdPerMillion -ne [decimal]0.20 -or
        [string]$prices.sourceUrl -notmatch '^https://(platform\.openai\.com|developers\.openai\.com|openai\.com)/') { throw 'The dated, reviewed Luna campaign pricing is required.' }
    $priceStamp=ConvertTo-PaidUtcStamp $prices.observedAtUtc
    if ($priceStamp -gt [DateTimeOffset]::UtcNow.AddMinutes(2)) { throw 'Pricing observation cannot be in the future.' }
    if ([string]::IsNullOrWhiteSpace($p.case.question) -or $p.case.question.Length -gt 2000 -or
        [string]::IsNullOrWhiteSpace($p.case.expectedAnswerDescription)) { throw 'An explicit bounded question and acceptance description are required.' }
    $pages = @($p.corpus.pageNumbers)
    if ($pages.Count -lt 1 -or $pages.Count -gt 8 -or
        @($pages | Where-Object { [int]$_ -lt 1 -or [int]$_ -gt 10000 }).Count -gt 0 -or
        @($pages | Sort-Object -Unique).Count -ne $pages.Count) { throw 'Select one to eight distinct PDF pages.' }
    $pdf = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $p.corpus.path))
    $allowed = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'out/remote-deploy')) + [IO.Path]::DirectorySeparatorChar
    if (-not $pdf.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetExtension($pdf) -ine '.pdf' -or -not (Test-Path -LiteralPath $pdf -PathType Leaf) -or
        (Get-FileHash -LiteralPath $pdf -Algorithm SHA256).Hash -ine $p.corpus.sha256) {
        throw 'The PDF must be an unchanged local out/remote-deploy corpus document.'
    }
    if ($p.provider.ContainsKey('apiKey') -or $p.provider.ContainsKey('apiKeyValue') -or $p.provider.ContainsKey('key') -or
        $p.ContainsKey('credentials')) { throw 'Campaign profiles must not contain credentials.' }
    return $p
}

function Assert-LunaExecuteAuthorization {
    param([Parameter(Mandatory)][hashtable]$Profile, [switch]$AuthorizePaid,
        [switch]$AuthorizeCorpusTransmission, [decimal]$PaidCapUsd)
    if (-not $AuthorizePaid -or -not $AuthorizeCorpusTransmission -or $PaidCapUsd -le 0 -or
        $PaidCapUsd -gt [decimal]0.50 -or $PaidCapUsd -lt [decimal]$Profile.budget.authorizedUsd -or
        $Profile.spendAuthorization.status -cne 'AUTHORIZED' -or
        [string]::IsNullOrWhiteSpace($Profile.spendAuthorization.basis)) {
        throw 'Execution requires explicit paid/transmission authorization and a reviewed AUTHORIZED profile within the 0.50 USD cap. No secret was read.'
    }
    $stamp = ConvertTo-PaidUtcStamp $Profile.spendAuthorization.authorizedAtUtc
    if ($stamp -gt [DateTimeOffset]::UtcNow.AddMinutes(2)) { throw 'A valid UTC authorization timestamp is required.' }
}

function New-LunaCampaignHandoff {
    param([Parameter(Mandatory)][hashtable]$Profile)
    return @{
        schemaVersion='saaia.advanced-analysis-handoff.v1';handoffId=[Guid]::NewGuid().ToString('D');createdAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
        requestText=$Profile.case.question;language='fr';originIntent='rag.answer';reasonCode='paid_local_real_corpus_validation';transferStage='before_retrieval';
        load=@{planKind='bounded_named_document_extraction';deliverable='short_source_backed_answer';answerUnitCount=1;atomicEvidenceCount=1;rowCount=0;columnCount=0;
            structuredLayout=$false;atomicEvidenceType='content_claim';atomicEvidenceMode='single_item';selectionPolicy='single_item';questionFocus='content';
            requestedDocumentName=[IO.Path]::GetFileName($Profile.corpus.path);boundedNamedDocumentExtraction=$true;candidateScopePaths=@('Corpus');rowLabels=@();columns=@()};
        researchState=@{memoryIsEvidence=$false;evidenceRevalidationRequired=$true;executedTools=@();executedQueries=@();attempts=@();evidenceReferences=@()};
        # The server derives external transmission rights from its reviewed config.
        # Client-supplied true flags are rejected by the production endpoint.
        dataPolicy=@{externalProviderContentAuthorized=$false;externalProviderMetadataAuthorized=$false;authorizationSource='server_controlled'}
    }
}
