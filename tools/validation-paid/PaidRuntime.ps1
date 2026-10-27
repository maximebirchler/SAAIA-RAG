#requires -Version 7.4
# These helpers are called only after the runner's explicit Execute gate.
function New-PaidReadOnlyQueryFile {
    param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$Query)
    if([string]::IsNullOrWhiteSpace($Query)){throw 'A nonempty read-only SQL query is required.'}
    # The caller has already verified this fixture's ownership. Keep the exact
    # query as private evidence; Windows native -c arguments can corrupt Unicode.
    $runtime=[IO.Path]::GetFullPath((Join-Path $OutputDirectory 'runtime'))
    if(-not [IO.Directory]::Exists($runtime)){throw 'The owned runtime directory must already exist.'}
    $path=Join-Path $runtime ('readonly-query-'+[Guid]::NewGuid().ToString('N')+'.sql')
    $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes("BEGIN READ ONLY;`n"+$Query+";`nCOMMIT;`n")
    $stream=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    return $path
}

function Invoke-PaidSqlProcess {
    param([Parameter(Mandatory)][string]$Psql, [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$OutputDirectory)
    $runtime=[IO.Path]::GetFullPath((Join-Path $OutputDirectory 'runtime'))
    if(-not [IO.Directory]::Exists($runtime)){throw 'The owned runtime directory must already exist.'}
    $prefix=Join-Path $runtime ('psql-'+[Guid]::NewGuid().ToString('N'))
    $stdoutPath=$prefix+'.stdout.private.txt';$stderrPath=$prefix+'.stderr.private.txt'
    $utf8=[Text.UTF8Encoding]::new($false,$true)
    $info=[Diagnostics.ProcessStartInfo]::new($Psql)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.StandardOutputEncoding=$utf8;$info.StandardErrorEncoding=$utf8
    $info.Environment['PGCLIENTENCODING']='UTF8'
    $info.Environment['PGOPTIONS']='-c statement_timeout=30000'
    foreach($argument in $Arguments){$info.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$info
    $started=$false;$stdoutFile=$null;$stderrFile=$null;$stdoutTask=$null;$stderrTask=$null
    try{
        $stdoutFile=[IO.FileStream]::new($stdoutPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        $stderrFile=[IO.FileStream]::new($stderrPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        $started=$process.Start()
        if(-not $started){throw 'The owned SQL client did not start.'}
        # Preserve bytes before decoding and drain both pipes concurrently. A
        # PostgreSQL NOTICE on stderr must never become part of a JSON result.
        $stdoutTask=$process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
        $stderrTask=$process.StandardError.BaseStream.CopyToAsync($stderrFile)
        if(-not $process.WaitForExit(60000)){throw [TimeoutException]::new('The owned SQL client exceeded its 60-second local timeout.')}
        $null=$stdoutTask.GetAwaiter().GetResult();$null=$stderrTask.GetAwaiter().GetResult()
        $exitCode=$process.ExitCode
        $stdoutFile.Flush($true);$stderrFile.Flush($true)
    }finally{
        if($started -and -not $process.HasExited){$process.Kill();$process.WaitForExit()}
        if($stdoutTask){try{$null=$stdoutTask.GetAwaiter().GetResult()}catch{}}
        if($stderrTask){try{$null=$stderrTask.GetAwaiter().GetResult()}catch{}}
        if($stdoutFile){$stdoutFile.Dispose()};if($stderrFile){$stderrFile.Dispose()}
        $process.Dispose()
    }
    return [pscustomobject]@{exitCode=$exitCode;stdout=$utf8.GetString([IO.File]::ReadAllBytes($stdoutPath));
        stderrPath=$stderrPath;stdoutPath=$stdoutPath}
}

function Get-OwnedPaidEnvironment {
    param([Parameter(Mandatory)][string]$OutputDirectory)
    $root = [IO.Path]::GetFullPath($OutputDirectory)
    $owner = Get-Content -LiteralPath (Join-Path $root 'environment.json') -Raw | ConvertFrom-Json
    $data = [IO.Path]::GetFullPath((Join-Path $root 'runtime/postgres/data'))
    if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or
        [IO.Path]::GetFullPath($owner.outputDirectory) -ine $root -or
        [IO.Path]::GetFullPath($owner.postgresDataDirectory) -ine $data -or [int]$owner.pgPort -eq 5432) {
        throw 'The paid runner requires its own fresh loopback PostgreSQL cluster.'
    }
    $pg = @($owner.processes | Where-Object name -eq 'postgres')
    if ($pg.Count -ne 1) { throw 'Exactly one owned PostgreSQL process is required.' }
    $actual = Get-Process -Id $pg[0].pid -ErrorAction Stop
    if ($actual.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$pg[0].startedAtUtc).UtcTicks -or
        [IO.Path]::GetFullPath($actual.Path) -ine [IO.Path]::GetFullPath($pg[0].path)) { throw 'PostgreSQL process identity changed.' }
    $pidLines = [IO.File]::ReadAllLines((Join-Path $data 'postmaster.pid'))
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $owner.pgPort -ErrorAction Stop)
    if ($pidLines.Count -lt 4 -or [int]$pidLines[0] -ne $actual.Id -or [int]$pidLines[3] -ne $owner.pgPort -or
        [IO.Path]::GetFullPath($pidLines[1]) -ine $data -or $listeners.Count -eq 0 -or
        @($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1','::1') -or $_.OwningProcess -ne $actual.Id }).Count -gt 0) {
        throw 'PostgreSQL path, PID, port or loopback ownership mismatch.'
    }
    return $owner
}

function Invoke-OwnedPaidSql {
    param([Parameter(Mandatory)][string]$OutputDirectory, [string]$Query = '', [switch]$SeedRealCorpus)
    $owner = Get-OwnedPaidEnvironment $OutputDirectory
    $private = Get-Content -LiteralPath (Join-Path $OutputDirectory 'runtime/fixture-secrets.json') -Raw | ConvertFrom-Json
    $previous = $env:PGPASSWORD
    try {
        $env:PGPASSWORD = $private.pgPassword
        $psql = Join-Path $owner.postgresBinDirectory 'psql.exe'
        $sqlArgs = @('-w','-X','-qAt','-h','127.0.0.1','-p',"$($owner.pgPort)",'-U','saaia_winui','-d','postgres','-v','ON_ERROR_STOP=1')
        $ownership=Invoke-PaidSqlProcess -Psql $psql -Arguments (@($sqlArgs)+@('-c',"SELECT current_setting('data_directory');")) -OutputDirectory $OutputDirectory
        $actual=$ownership.stdout.Trim()
        if ($ownership.exitCode -ne 0 -or [IO.Path]::GetFullPath($actual) -ine [IO.Path]::GetFullPath($owner.postgresDataDirectory)) {
            throw 'Connected PostgreSQL data_directory is not owned by this campaign.'
        }
        $result = if ($SeedRealCorpus) {
            Invoke-PaidSqlProcess -Psql $psql -Arguments (@($sqlArgs)+@('-f',(Join-Path $OutputDirectory 'runtime/seed-real-corpus.sql'))) -OutputDirectory $OutputDirectory
        } else {
            $queryFile=New-PaidReadOnlyQueryFile -OutputDirectory $OutputDirectory -Query $Query
            Invoke-PaidSqlProcess -Psql $psql -Arguments (@($sqlArgs)+@('-f',$queryFile)) -OutputDirectory $OutputDirectory
        }
        if ($result.exitCode -ne 0) { throw ('Owned campaign SQL failed; inspect private diagnostics: '+$result.stderrPath) }
        return $result.stdout.Trim()
    } finally { $env:PGPASSWORD = $previous }
}

function New-PaidCorpusTextConformanceProof {
    param([Parameter(Mandatory)][object]$Manifest, [Parameter(Mandatory)][object]$Observation,
        [Parameter(Mandatory)][string]$ManifestSha256)
    $mapping=[ordered]@{document_units='units';retrieval_chunks='retrievalChunks';
        contextual_text_entries='contextualEntries';exact_match_entries='exactEntries'}
    if($Manifest.schemaVersion -cne 'saaia-paid-real-corpus.v2' -or $ManifestSha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or
        $Observation.schemaVersion -cne 'saaia.paid-corpus-sql-observation.v1' -or
        $Observation.checksumAlgorithm -cne 'sha256_utf8'){
        throw 'Malformed corpus manifest or SQL conformance observation.'
    }
    $rows=@($Observation.tables)
    if($rows.Count -ne $mapping.Count -or
        @($rows|Where-Object{$_.name -cnotin @($mapping.Keys)}).Count -ne 0 -or
        @($rows|Group-Object name|Where-Object Count -ne 1).Count -ne 0){
        throw 'All four distinct canonical corpus tables must be observed.'
    }
    $checks=@();[long]$countMismatches=0;[long]$checksumMismatches=0;[long]$malformedRows=0;[long]$identityMismatches=0
    foreach($table in $mapping.Keys){
        $expected=$Manifest.projectionCounts.($mapping[$table])
        $row=@($rows|Where-Object name -CEQ $table)[0]
        $field=if($table -ceq 'exact_match_entries'){'normalized_text'}else{'text_content'}
        foreach($count in @($expected,$row.rowCount,$row.scopedRowCount,$row.checksumMismatchCount,$row.malformedCount)){
            if(($count -isnot [int] -and $count -isnot [long]) -or $count -lt 0){
                throw 'Corpus conformance counts must be typed nonnegative integers.'
            }
        }
        if(($table -cne 'exact_match_entries' -and $expected -eq 0) -or $row.textField -cne $field -or
            $row.scopedRowCount -gt $row.rowCount -or $row.checksumMismatchCount -gt $row.rowCount -or
            $row.malformedCount -gt $row.rowCount){
            throw 'Malformed corpus table counts or text checksum field.'
        }
        $countMatches=($row.rowCount -eq $expected -and $row.scopedRowCount -eq $expected)
        if(-not $countMatches){$countMismatches++}
        $checksumMismatches += $row.checksumMismatchCount
        $malformedRows += $row.malformedCount
        $identityMismatches += $row.rowCount-$row.scopedRowCount
        $checks += [ordered]@{name=$table;textField=$field;expectedCount=[long]$expected;
            actualCount=[long]$row.rowCount;scopedCount=[long]$row.scopedRowCount;
            checksumMismatchCount=[long]$row.checksumMismatchCount;malformedCount=[long]$row.malformedCount;
            countMatches=$countMatches;checksumMatches=($row.checksumMismatchCount -eq 0 -and $row.malformedCount -eq 0)}
    }
    return [ordered]@{schemaVersion='saaia.paid-corpus-text-conformance.v1';manifestSha256=$ManifestSha256;
        checksumAlgorithm='sha256_utf8';passed=($countMismatches -eq 0 -and $checksumMismatches -eq 0 -and $malformedRows -eq 0);
        countMismatchTableCount=$countMismatches;checksumMismatchRowCount=$checksumMismatches;
        malformedRowCount=$malformedRows;scopedIdentityMismatchRowCount=$identityMismatches;tables=$checks;
        limits=@('Checks stored text bytes against stored production checksums and counts against the frozen corpus manifest.',
            'Exact-entry checksum contract covers normalized_text; no checksum claim is made for its display text.',
            'Read-only SQL conformance does not validate ingestion, retrieval ranking, vectors or answer quality.')}
}

function Get-OwnedPaidCorpusTextConformance {
    param([Parameter(Mandatory)][string]$OutputDirectory)
    $null=Get-OwnedPaidEnvironment -OutputDirectory $OutputDirectory
    $proofPath=Join-Path $OutputDirectory 'corpus-text-conformance.json'
    if(Test-Path -LiteralPath $proofPath){throw 'Never overwrite an earlier corpus text conformance proof.'}
    $proof=[ordered]@{schemaVersion='saaia.paid-corpus-text-conformance.v1';passed=$false;
        manifestSha256=$null;failureCode='malformed_or_unavailable';tables=@()}
    $failure=$null
    try{
        $manifestPath=Join-Path $OutputDirectory 'real-corpus-manifest.json'
        $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json -Depth 25
        $manifestSha=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        $proof.manifestSha256=$manifestSha
        $revision=[Guid]::Empty;$doc=[Guid]::Empty
        if(-not [Guid]::TryParse([string]$manifest.revisionId,[ref]$revision) -or $revision -eq [Guid]::Empty -or
            -not [Guid]::TryParse([string]$manifest.docId,[ref]$doc) -or $doc -eq [Guid]::Empty){
            throw 'The corpus manifest must identify a nonempty document and revision.'
        }
        $revisionLiteral=$revision.ToString('D')
        # Fixed table/field names; only a parsed UUID is interpolated. Invoke-OwnedPaidSql
        # enforces ownership, BEGIN READ ONLY, UTF8 and private stdout/stderr capture.
        $sql=@"
WITH table_checks AS (
 SELECT 'document_units'::text AS name,'text_content'::text AS text_field,count(*) AS row_count,
  count(*) FILTER (WHERE tenant_id='77777777-7777-4777-8777-777777777777'::uuid AND revision_id='$revisionLiteral'::uuid) AS scoped_count,
  count(*) FILTER (WHERE checksum IS DISTINCT FROM sha256(convert_to(text_content,'UTF8'))) AS checksum_mismatch_count,
  count(*) FILTER (WHERE text_content IS NULL OR checksum IS NULL OR octet_length(checksum)<>32) AS malformed_count
 FROM document_units
 UNION ALL
 SELECT 'retrieval_chunks','text_content',count(*),
  count(*) FILTER (WHERE tenant_id='77777777-7777-4777-8777-777777777777'::uuid AND revision_id='$revisionLiteral'::uuid),
  count(*) FILTER (WHERE checksum IS DISTINCT FROM sha256(convert_to(text_content,'UTF8'))),
  count(*) FILTER (WHERE text_content IS NULL OR checksum IS NULL OR octet_length(checksum)<>32)
 FROM retrieval_chunks
 UNION ALL
 SELECT 'contextual_text_entries','text_content',count(*),
  count(*) FILTER (WHERE tenant_id='77777777-7777-4777-8777-777777777777'::uuid AND revision_id='$revisionLiteral'::uuid),
  count(*) FILTER (WHERE checksum IS DISTINCT FROM sha256(convert_to(text_content,'UTF8'))),
  count(*) FILTER (WHERE text_content IS NULL OR checksum IS NULL OR octet_length(checksum)<>32)
 FROM contextual_text_entries
 UNION ALL
 SELECT 'exact_match_entries','normalized_text',count(*),
  count(*) FILTER (WHERE tenant_id='77777777-7777-4777-8777-777777777777'::uuid AND revision_id='$revisionLiteral'::uuid),
  count(*) FILTER (WHERE checksum IS DISTINCT FROM sha256(convert_to(normalized_text,'UTF8'))),
  count(*) FILTER (WHERE normalized_text IS NULL OR checksum IS NULL OR octet_length(checksum)<>32)
 FROM exact_match_entries
)
SELECT jsonb_build_object('schemaVersion','saaia.paid-corpus-sql-observation.v1','checksumAlgorithm','sha256_utf8',
 'tables',(SELECT jsonb_agg(jsonb_build_object('name',name,'textField',text_field,'rowCount',row_count,
   'scopedRowCount',scoped_count,'checksumMismatchCount',checksum_mismatch_count,'malformedCount',malformed_count)
   ORDER BY name) FROM table_checks));
"@
        $raw=Invoke-OwnedPaidSql -OutputDirectory $OutputDirectory -Query $sql
        $observation=$raw|ConvertFrom-Json -Depth 20
        $proof=New-PaidCorpusTextConformanceProof -Manifest $manifest -Observation $observation -ManifestSha256 $manifestSha
        if(-not $proof.passed){$proof.failureCode='stored_corpus_text_mismatch';throw 'Stored corpus text checksum or count mismatch.'}
    }catch{$failure=$_.Exception.GetType().Name}
    finally{
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($proof|ConvertTo-Json -Depth 20))
        $stream=[IO.FileStream]::new($proofPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    }
    if($failure){throw 'Owned corpus text conformance failed; no job may be admitted. Inspect the safe proof and private SQL diagnostics.'}
    return $proof
}

function Assert-PaidTextParserConformance {
    param([Parameter(Mandatory)][object]$Conformance)
    $expected=@('unicode_punctuation','unicode_case','conjunction','accent_distinction')
    $checks=@($Conformance.checks)
    if($Conformance.schemaVersion -cne 'saaia.pg-text-parser-conformance.v1' -or
       $Conformance.passed -isnot [bool] -or -not $Conformance.passed -or $checks.Count -ne $expected.Count -or
       @($checks|Where-Object{$_.name -notin $expected -or $_.passed -isnot [bool] -or -not $_.passed}).Count -gt 0 -or
       @($checks|Group-Object name|Where-Object Count -ne 1).Count -gt 0){
        throw 'The owned PostgreSQL text parser does not satisfy Unicode punctuation/case and strict lexical conjunction checks; no paid worker may start.'
    }
}

function Get-OwnedPaidTextParserConformance {
    param([Parameter(Mandatory)][string]$OutputDirectory)
    # These public, corpus-independent sentinels match the production startup
    # diagnostic's behavior contract. They do not alter retrieval or its query.
    $sql=@'
WITH checks(name,passed) AS (VALUES
 ('unicode_punctuation',
   to_tsvector('simple',U&'alpha\2026beta') @@ websearch_to_tsquery('simple','alpha beta')
   AND to_tsvector('simple',U&'alpha\2014beta') @@ websearch_to_tsquery('simple','alpha beta')),
 ('unicode_case',to_tsvector('simple',U&'\00C9COLE') @@ websearch_to_tsquery('simple',U&'\00E9cole')),
 ('conjunction',to_tsvector('simple','alpha beta') @@ websearch_to_tsquery('simple','alpha beta')
   AND NOT to_tsvector('simple','alpha') @@ websearch_to_tsquery('simple','alpha beta')
   AND NOT to_tsvector('simple','beta') @@ websearch_to_tsquery('simple','alpha beta')),
 ('accent_distinction',to_tsvector('simple',U&'caf\00E9') @@ websearch_to_tsquery('simple',U&'caf\00E9')
   AND NOT to_tsvector('simple',U&'caf\00E9') @@ websearch_to_tsquery('simple','cafe'))
)
SELECT jsonb_build_object('schemaVersion','saaia.pg-text-parser-conformance.v1',
 'passed',(SELECT bool_and(passed) FROM checks),
 'checks',(SELECT jsonb_agg(jsonb_build_object('name',name,'passed',passed) ORDER BY name) FROM checks),
 'databaseLocale',(SELECT jsonb_build_object('lcCtype',to_jsonb(db)->>'datctype',
    'lcCollate',to_jsonb(db)->>'datcollate','provider',to_jsonb(db)->>'datlocprovider',
    'icuLocale',coalesce(to_jsonb(db)->>'datlocale',to_jsonb(db)->>'daticulocale'))
    FROM pg_database db WHERE db.datname=current_database()),
 'serverVersion',version());
'@
    $raw=Invoke-OwnedPaidSql -OutputDirectory $OutputDirectory -Query $sql
    $result=$raw|ConvertFrom-Json -Depth 20
    $path=Join-Path $OutputDirectory 'text-parser-conformance.json'
    if(Test-Path -LiteralPath $path){throw 'Never overwrite an earlier text-parser conformance proof.'}
    [IO.File]::WriteAllText($path,($result|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
    Assert-PaidTextParserConformance -Conformance $result
    return $result
}

function Start-OwnedPaidBackend {
    param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][hashtable]$Profile, [Parameter(Mandatory)][AllowEmptyString()][string]$ApiKey, [bool]$WorkerEnabled=$true,
        [string]$PlanningPreflightBaseUrl='')
    $planning=$Profile.schemaVersion -ceq 'saaia-local-openai-planning-campaign.v1'
    if($PlanningPreflightBaseUrl -and -not $planning){throw 'Only the separate planning profile supports the local preflight sink.'}
    if(-not $PlanningPreflightBaseUrl -and [string]::IsNullOrWhiteSpace($ApiKey)){throw 'An authorized nonempty provider key is required.'}
    $owner = Get-OwnedPaidEnvironment $OutputDirectory
    $parserConformance=Get-OwnedPaidTextParserConformance -OutputDirectory $OutputDirectory
    $port = [int]$Profile.execution.paidBackendPort
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw 'Paid backend port is occupied.' }
    $runtime = Join-Path $OutputDirectory 'runtime/paid-worker'
    if (Test-Path -LiteralPath $runtime) { throw 'A paid worker may only be started once in a fresh campaign.' }
    [IO.Directory]::CreateDirectory($runtime) | Out-Null
    $config = Get-Content -LiteralPath (Join-Path $OutputDirectory 'runtime/deployment.config.json') -Raw | ConvertFrom-Json -AsHashtable
    $prior=if($planning){[decimal]$Profile.budget.baselineCostUsd}else{[decimal]$Profile.budget.priorCampaignCostUsd}
    $remaining = [decimal]$Profile.budget.authorizedUsd - $prior
    $config.AdvancedAnalysis = @{
        Provider='openai-dev'; LlmLocation='external-service'; LlmBaseUrl=$Profile.provider.baseUrl; LlmModel=$Profile.provider.model;
        LlmApiKeyRef='ENV:SAAIA_ADVANCED_LLM_API_KEY'; ReasoningEffort=$Profile.provider.reasoningEffort; WorkerEnabled=$WorkerEnabled;
        AdaptiveResearchEnabled=$true; NativeResearchToolsEnabled=$false; SemanticCriticEnabled=$false;
        PlannerMaxTokens=[int]$Profile.execution.plannerMaxTokens; WriterMaxTokens=[int]$Profile.execution.writerMaxTokens;
        LlmMaximumHttpAttempts=1; MaximumAttempts=1; LlmTimeoutSeconds=[int]$Profile.execution.timeoutSeconds;
        MaximumQueuedJobsPerUser=1; PollDelayMilliseconds=250; LeaseSeconds=30; HeartbeatMilliseconds=1000;
        AllowExternalProviderContent=$true; AllowExternalProviderMetadata=$true;
        ExternalBudgetAuthorizedUsd=$remaining; ExternalBudgetSoftLimitUsd=$remaining; ExternalBudgetHardLimitUsd=$remaining;
        ExternalMaximumCostPerJobUsd=[decimal]$Profile.budget.maximumCostPerJobUsd;
        ExternalMaximumCallsPerJob=[int]$Profile.execution.maximumCallsPerJob;
        ExternalInputUsdPerMillionTokens=[decimal]$Profile.pricing.inputUsdPerMillion;
        ExternalCachedInputUsdPerMillionTokens=[decimal]$Profile.pricing.cachedInputUsdPerMillion;
        ExternalCacheWriteInputUsdPerMillionTokens=[decimal]$Profile.pricing.cacheWriteUsdPerMillion;
        ExternalOutputUsdPerMillionTokens=[decimal]$Profile.pricing.outputUsdPerMillion;
        ExternalUsageLedgerPath=(Join-Path $OutputDirectory 'runtime/usage.jsonl');
        MaximumPlanQueries=4; MaximumEvidencePromptCharacters=24000; MaximumEvidenceCharactersTotal=120000;
        MaximumToolCalls=12; MaximumToolElapsedMilliseconds=60000; RetentionDays=1;
        DevelopmentTraceDirectory=(Join-Path $OutputDirectory 'runtime/private-traces'); DevelopmentTraceStrict=$true
    }
    if($planning){
        if(-not (Get-Command Get-PlanningAdvancedOptions -ErrorAction SilentlyContinue)){throw 'Load the reviewed planning profile guards before starting this opt-in runtime.'}
        $overrides=Get-PlanningAdvancedOptions -Profile $Profile -OutputDirectory $OutputDirectory -RepositoryRoot $RepositoryRoot
        foreach($name in $overrides.Keys){$config.AdvancedAnalysis[$name]=$overrides[$name]}
        if($Profile.ContainsKey('realRag')){
            $config.Rag=Get-OwnedPlanningRealRagOverrides -Profile $Profile -OutputDirectory $OutputDirectory
            $config.VectorGc=@{Enabled=$false}
            $config.Ingestion.WorkerEnabled=$false;$config.Ingestion.WatcherEnabled=$false;$config.Ingestion.ScannerEnabled=$false
        }
        if($PlanningPreflightBaseUrl){
            $expected="http://127.0.0.1:$($Profile.execution.preflightProviderPort)/v1"
            if($PlanningPreflightBaseUrl -cne $expected -or -not [string]::IsNullOrEmpty($ApiKey)){throw 'The preflight must use its exact loopback sink and no provider key.'}
            $config.AdvancedAnalysis.Provider='customer-server';$config.AdvancedAnalysis.ProviderKey='planning-loopback-preflight';$config.AdvancedAnalysis.LlmLocation='internal'
            $config.AdvancedAnalysis.LlmBaseUrl=$expected;$config.AdvancedAnalysis.LlmApiKeyRef=$null
            $config.AdvancedAnalysis.ExternalUsageLedgerPath=(Join-Path $OutputDirectory 'runtime/preflight-usage.jsonl')
        }
    }
    $configPath = Join-Path $runtime 'deployment.config.json'
    [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 20))
    Copy-Item -LiteralPath (Join-Path $OutputDirectory 'runtime/Db') -Destination (Join-Path $runtime 'Db') -Recurse
    $backendDll = Join-Path $RepositoryRoot 'backend/SAAIA.Backend/bin/Release/net8.0/SAAIA.Backend.dll'
    if ((Get-FileHash -LiteralPath $backendDll -Algorithm SHA256).Hash -ine $Profile.execution.backendAssemblySha256) {
        throw 'The production backend changed after the paid profile was frozen.'
    }
    $spec = @{executable=(Get-Command dotnet).Source; arguments=@($backendDll,'--contentRoot',$runtime,'--urls',"http://127.0.0.1:$port");
        stdout=(Join-Path $OutputDirectory 'runtime/paid-backend.stdout.log'); stderr=(Join-Path $OutputDirectory 'runtime/paid-backend.stderr.log')}
    $specPath = Join-Path $runtime 'backend-process.json'
    [IO.File]::WriteAllText($specPath, ($spec | ConvertTo-Json))
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory=$runtime
    foreach ($name in @($info.Environment.Keys)) {
        if ($name -match 'OPENAI|RUNPOD|AZURE|SAAIA_|ConfigSignature|ConnectionString|QDRANT') { [void]$info.Environment.Remove($name) }
    }
    foreach ($arg in @('-NoProfile','-File',(Join-Path $RepositoryRoot 'tools/validation-winui/run-helper.ps1'),'-Specification',$specPath)) { $info.ArgumentList.Add($arg) }
    $info.Environment['ASPNETCORE_ENVIRONMENT']='Development'; $info.Environment['DOTNET_ENVIRONMENT']='Development'
    $info.Environment['ConfigSignature__ConfigPath']=$configPath; $info.Environment['ConfigSignature__SignaturePath']=(Join-Path $runtime 'missing.sig')
    $info.Environment['ConfigSignature__AllowUnsignedInDevelopment']='true'; $info.Environment['SAAIA_ADVANCED_LLM_API_KEY']=$ApiKey
    $process=[Diagnostics.Process]::Start($info)
    $owner.processes += @{name='advanced-backend-runner';pid=$process.Id;path=$info.FileName;port=$port;startedAtUtc=$process.StartTime.ToUniversalTime().ToString('o')}
    $owner.externalCallsPossible=$WorkerEnabled
    $owner|Add-Member -NotePropertyName textParserConformancePassed -NotePropertyValue $parserConformance.passed -Force
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'environment.json'), ($owner | ConvertTo-Json -Depth 12))
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        try { $ready=Invoke-RestMethod -Uri "http://127.0.0.1:$port/ready" -TimeoutSec 3; if ($ready.ok) { break } } catch { }
        if ($process.HasExited -or [DateTimeOffset]::UtcNow -gt $deadline) { throw 'The owned paid worker failed readiness; no job may be submitted.' }
        Start-Sleep -Milliseconds 250
    } while ($true)
    return "http://127.0.0.1:$port"
}
