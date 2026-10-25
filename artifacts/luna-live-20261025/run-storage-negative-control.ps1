# Owned local negative control only. No paid provider, client or production DB.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PythonExecutable,
    [string]$OutputDirectory=(Join-Path $PSScriptRoot 'storage-negative-control-v2'))
$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repositoryRoot 'tools/validation-paid/PaidCampaign.Common.ps1')
. (Join-Path $repositoryRoot 'tools/validation-paid/PaidRuntime.ps1')
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $root.StartsWith($PSScriptRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
    throw 'Choose a fresh child of this campaign directory.'
}
if(Test-Path -LiteralPath $root){throw 'Never replace earlier negative-control evidence.'}
$fixture=Join-Path $root 'local-fixture'
[IO.Directory]::CreateDirectory($root)|Out-Null
$proof=[ordered]@{schemaVersion='saaia-storage-negative-control.v1';passed=$false;
    providerCalls=0;paidSecretsRead=0;expectedModifiedRows=1;observedModifiedRows=0;guardRejected=$false;
    jobsAfterGuard=$null;ownedEnvironmentStopped=$false}
try{
    & (Join-Path $repositoryRoot 'tools/validation-winui/start-environment.ps1') -OutputDirectory $fixture -BackendPort 5267 -FixturePort 18207 -NoBuild
    $helper=Join-Path $repositoryRoot 'tools/ingestion_gold/canonical_projection/bin/Release/net8.0/SAAIA.IngestionGold.dll'
    $backendSha=(Get-FileHash (Join-Path (Split-Path $helper -Parent) 'SAAIA.Backend.dll')).Hash
    & $PythonExecutable -B (Join-Path $repositoryRoot 'tools/validation-paid/prepare_real_corpus.py') --output $fixture `
        --pdf (Join-Path $repositoryRoot 'out/remote-deploy/30-recettes-preferees-des-francais.pdf') `
        --sha256 AA11E234FB3336EE76644958E888D83565E25BA875264BA75F7CE22B32D25C18 `
        --pages '2,5,6,7' --projection-dll $helper --backend-sha256 $backendSha
    if($LASTEXITCODE -ne 0){throw 'Native preparation failed.'}
    $null=Invoke-OwnedPaidSql -OutputDirectory $fixture -SeedRealCorpus
    # Deliberately break one text/checksum pair inside this exact owned fixture.
    # The statement is a fixture mutation; the conformance guard itself is read-only.
    $owner=Get-OwnedPaidEnvironment -OutputDirectory $fixture
    $fixtureSecret=Get-Content (Join-Path $fixture 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
    $previousPassword=$env:PGPASSWORD
    try{
        $env:PGPASSWORD=$fixtureSecret.pgPassword
        $mutationFile=Join-Path $fixture 'runtime/storage-negative-mutation.private.sql'
        [IO.File]::WriteAllText($mutationFile,"UPDATE retrieval_chunks SET text_content=text_content || E'\nSTORAGE_NEGATIVE_SENTINEL' WHERE retrieval_chunk_id=(SELECT retrieval_chunk_id FROM retrieval_chunks ORDER BY chunk_index LIMIT 1);",[Text.UTF8Encoding]::new($false))
        $mutationResult=Invoke-PaidSqlProcess -Psql (Join-Path $owner.postgresBinDirectory 'psql.exe') `
            -Arguments @('-w','-h','127.0.0.1','-p',[string]$owner.pgPort,'-U','saaia_winui','-d','postgres','-X','-v','ON_ERROR_STOP=1','-f',$mutationFile) -OutputDirectory $fixture
        if($mutationResult.exitCode -ne 0 -or $mutationResult.stdout -notmatch 'UPDATE 1'){throw 'Controlled single-row mutation failed.'}
        $proof.observedModifiedRows=1
    }finally{$env:PGPASSWORD=$previousPassword;$fixtureSecret=$null}
    try{$null=Get-OwnedPaidCorpusTextConformance -OutputDirectory $fixture}catch{$proof.guardRejected=$true}
    $conformance=Get-Content (Join-Path $fixture 'corpus-text-conformance.json') -Raw|ConvertFrom-Json
    $proof.checksumMismatchRowCount=$conformance.checksumMismatchRowCount
    $proof.countMismatchTableCount=$conformance.countMismatchTableCount
    $jobs=Invoke-OwnedPaidSql -OutputDirectory $fixture -Query "SELECT json_build_object('jobs',count(*)) FROM advanced_analysis_jobs;"|ConvertFrom-Json
    $proof.jobsAfterGuard=$jobs.jobs
    $proof.passed=($proof.guardRejected -and -not $conformance.passed -and $conformance.checksumMismatchRowCount -eq 1 -and
        $conformance.countMismatchTableCount -eq 0 -and $jobs.jobs -eq 0)
}finally{
    if(Test-Path (Join-Path $fixture 'environment.json')){
        & (Join-Path $repositoryRoot 'tools/validation-winui/stop-environment.ps1') -OutputDirectory $fixture -Confirm:$false
        $proof.ownedEnvironmentStopped=(Get-Content (Join-Path $fixture 'environment.json') -Raw|ConvertFrom-Json).state -eq 'stopped'
    }
    $proof.passed=$proof.passed -and $proof.ownedEnvironmentStopped
    [IO.File]::WriteAllText((Join-Path $root 'result.json'),($proof|ConvertTo-Json -Depth 12))
}
if(-not $proof.passed){throw 'The owned storage negative control did not satisfy its contract.'}
Write-Output 'One corrupted stored text rejected; zero jobs/provider calls; owned environment stopped.'
