#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $root){throw 'Fresh offline planning test output required.'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$planning=Join-Path $repo 'tools/validation-planning'
$bootstrap=Join-Path $repo 'tools/validation-ingestion/prepare_owned_linux_bootstrap_v6.py'
$expectedBootstrapSha='23F201C39C4D447979D44AE223A1447D382E997AB396B9BF0F816CD0F37046A0'
$results=[Collections.Generic.List[object]]::new()
$report=[ordered]@{
    schemaVersion='saaia-planning-offline-tests.v1';passed=$false;repositoryRoot=$repo;
    historicalArchivesRequired=$false;backendBinaryRequired=$false;serviceExecution=$false;
    providerExecution=$false;secretAccess=$false;bootstrapSha256=$null;
    boundaries=@('Synthetic guards and local file contracts; no ingestion, SQL, model or live planning qualification.',
        'The file/root symlink test retains its explicit OS-privilege skip; reparse rejection has separate deterministic coverage.',
        'Signer owner/configuration-SHA/timeout-policy prefixes are not dynamically exercised by the extracted configuration-guard tests.',
        'CorpusProjection guards require a compiled helper and real inventory/package and are a separate integration test.');
    results=$results
}
try{
    $actual=(Get-FileHash -LiteralPath $bootstrap -Algorithm SHA256).Hash
    if($actual -cne $expectedBootstrapSha){throw 'Public v6 bootstrap byte identity changed.'}
    $report.bootstrapSha256=$actual
    $pythonOutput=@(& python -B -m unittest discover -s $planning -p 'test_*.py' -v 2>&1)
    $code=$LASTEXITCODE
    $log=Join-Path $root 'python.log'
    [IO.File]::WriteAllLines($log,[string[]]$pythonOutput,[Text.UTF8Encoding]::new($false))
    $text=$pythonOutput -join "`n"
    $count=[regex]::Match($text,'(?m)^Ran (\d+) tests? in ')
    $skips=@($pythonOutput|Where-Object{[string]$_ -match ' \.\.\. skipped '})
    $results.Add(@{name='python-planning-contracts';exitCode=$code;testCount=if($count.Success){[int]$count.Groups[1].Value}else{0};
        skippedCount=$skips.Count;skips=[string[]]$skips;logPath=$log;logSha256=(Get-FileHash -LiteralPath $log).Hash})
    if($code -ne 0 -or -not $count.Success){throw 'Planning Python contract suite failed or did not report execution.'}
    foreach($skip in $skips){if([string]$skip -notmatch '^test_transfer_rejects_actual_file_or_root_symlink_before_long_path_resolution .*Host does not permit synthetic symlink creation:'){
        throw 'An undeclared test skip cannot qualify the portable planning suite.'
    }}
    foreach($name in @('Test-PlanningGuards','Test-PlanningRagGuards','Test-RealIngestionGuards','Test-Source29SigningGuards','Test-Source30SigningGuards')){
        $arguments=@('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $planning ($name+'.ps1')))
        if($name -ceq 'Test-RealIngestionGuards'){$arguments+=@('-OutputDirectory',(Join-Path $root 'real-ingestion-guards'))}
        $output=@(& (Join-Path $PSHOME 'pwsh.exe') @arguments 2>&1)
        $code=$LASTEXITCODE;$log=Join-Path $root ($name+'.log')
        [IO.File]::WriteAllLines($log,[string[]]$output,[Text.UTF8Encoding]::new($false))
        $results.Add(@{name=$name;exitCode=$code;logPath=$log;logSha256=(Get-FileHash -LiteralPath $log).Hash})
        if($code -ne 0){throw ('Planning PowerShell guard failed: '+$name)}
    }
    $report.passed=$true
}finally{
    [IO.File]::WriteAllText((Join-Path $root 'manifest.json'),($report|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
}
Write-Output ('Offline planning tools PASS; '+$results.Count+' suites; no archive, binary, secret, provider or service required.')
