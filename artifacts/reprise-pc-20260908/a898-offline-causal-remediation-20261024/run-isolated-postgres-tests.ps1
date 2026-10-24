param(
 [string]$RunName='',
 [string]$TestFilter='FullyQualifiedName!~SAAIA.Backend.Tests.Live'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$proofDir=$PSScriptRoot
$repo=[IO.Path]::GetFullPath((Join-Path $proofDir '..\..\..'))
if($RunName){
 if($RunName -notmatch '^[a-zA-Z0-9-]+$'){throw 'Invalid run name'}
 $proofDir=Join-Path $PSScriptRoot $RunName
 if(Test-Path -LiteralPath $proofDir){throw 'Run artifacts already exist; choose a fresh run name'}
 $null=[IO.Directory]::CreateDirectory($proofDir)
}
$pgBin='C:\Program Files\Odoo 19.0.20260622\PostgreSQL\bin'
$postgres=Join-Path $pgBin 'postgres.exe'
$pgVersion=(& $postgres --version | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $pgVersion -notmatch 'PostgreSQL\) 16\.'){throw 'Expected installed PostgreSQL 16 binaries'}
$port=55439
if(@(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count){throw 'Isolated test port busy'}
$clusterParent=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'SAAIA\reprise-integration-postgres'))
$clusterRoot=[IO.Path]::GetFullPath((Join-Path $clusterParent ('run-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N'))))
if(-not $clusterRoot.StartsWith($clusterParent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Cluster outside owned parent'}
if(Test-Path -LiteralPath $clusterRoot){throw 'Cluster already exists'}
$null=[IO.Directory]::CreateDirectory($clusterRoot)
$dataDir=Join-Path $clusterRoot 'data'
$passwordFile=Join-Path $clusterRoot 'bootstrap-password.txt'
$testDbSecret=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
[IO.File]::WriteAllText($passwordFile,$testDbSecret,[Text.UTF8Encoding]::new($false))
$savedTestPg=[Environment]::GetEnvironmentVariable('SAAIA_TEST_PG_CONN','Process')
$savedPgPassword=[Environment]::GetEnvironmentVariable('PGPASSWORD','Process')
$savedContractExport=[Environment]::GetEnvironmentVariable('SAAIA_TEST_CONTRACT_EXPORT_DIR','Process')
$managed=$null
$testsExit=1
$failure=$null
$runBinaryState=$null
$started=[DateTime]::UtcNow
try{
 $initArgs='-D "'+$dataDir+'" --username=saaia_test --encoding=UTF8 --locale=C --auth=scram-sha-256 --pwfile="'+$passwordFile+'"'
 $init=Start-Process -FilePath (Join-Path $pgBin 'initdb.exe') -ArgumentList $initArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $proofDir 'initdb.stdout.log') -RedirectStandardError (Join-Path $proofDir 'initdb.stderr.log')
 if(-not $init.WaitForExit(60000)){Stop-Process -Id $init.Id -Force;throw 'initdb deadline'}
 if($init.ExitCode -ne 0){throw 'Isolated initdb failed; inspect stderr'}
 Remove-Item -LiteralPath $passwordFile
 $managed=Start-Process -FilePath $postgres -ArgumentList ('-D "'+$dataDir+'" -h 127.0.0.1 -p '+$port) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $proofDir 'postgres.stdout.log') -RedirectStandardError (Join-Path $proofDir 'postgres.stderr.log')
 $ready=$false;$deadline=[DateTime]::UtcNow.AddSeconds(45)
 while([DateTime]::UtcNow -lt $deadline){
  $managed.Refresh();if($managed.HasExited){throw 'Owned PostgreSQL exited'}
  & (Join-Path $pgBin 'pg_isready.exe') -h 127.0.0.1 -p $port -U saaia_test -d postgres *> $null
  if($LASTEXITCODE -eq 0){$ready=$true;break}
  Start-Sleep -Milliseconds 500
 }
 if(-not $ready){throw 'Isolated PostgreSQL readiness deadline'}
 [Environment]::SetEnvironmentVariable('PGPASSWORD',$testDbSecret,'Process')
 $actualData=(& (Join-Path $pgBin 'psql.exe') -w -h 127.0.0.1 -p $port -U saaia_test -d postgres -Atc "SELECT current_setting('data_directory');" | Out-String).Trim()
 if($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($actualData) -ine [IO.Path]::GetFullPath($dataDir)){throw 'Wrong database cluster; refusing tests'}
 [Environment]::SetEnvironmentVariable('PGPASSWORD',$savedPgPassword,'Process')
 [Environment]::SetEnvironmentVariable('SAAIA_TEST_CONTRACT_EXPORT_DIR',$proofDir,'Process')
 [Environment]::SetEnvironmentVariable('SAAIA_TEST_PG_CONN',"Host=127.0.0.1;Port=$port;Database=postgres;Username=saaia_test;Password=$testDbSecret;Pooling=false;Timeout=10;Command Timeout=120;Include Error Detail=false",'Process')
 $runBinaryState=@('SAAIA.Backend.Tests.dll','SAAIA.Backend.dll','SAAIA.Contracts.dll') | ForEach-Object {
  $binaryPath=Join-Path $repo ('backend\SAAIA.Backend.Tests\bin\Release\net8.0\'+$_)
  [ordered]@{path=$binaryPath;sha256=(Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash}
 }
 $runBinaryState | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $proofDir 'test-binaries-before.json') -Encoding utf8
 [ordered]@{startedAtUtc=$started.ToString('o');engine=$pgVersion;postgresBinarySha256=(Get-FileHash -LiteralPath $postgres -Algorithm SHA256).Hash;ownedPid=$managed.Id;cluster=$dataDir;port=$port;verifiedDataDirectory=$true;testFilter=$TestFilter;externalCorpusAccess=$false;existingClusterModified=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $proofDir 'preflight.json') -Encoding utf8
 Write-Output 'Isolated PostgreSQL 16 ready; backend tests execute with a real disposable database connection.'
 & dotnet test (Join-Path $repo 'backend\SAAIA.Backend.Tests\SAAIA.Backend.Tests.csproj') -c Release --no-build --no-restore --filter $TestFilter --logger 'trx;LogFileName=backend-real-postgres.trx' --results-directory $proofDir --nologo --verbosity minimal *> (Join-Path $proofDir 'backend-tests.log')
 $testsExit=$LASTEXITCODE
 Get-Content -LiteralPath (Join-Path $proofDir 'backend-tests.log') -Tail 4
}catch{$failure=$_.Exception.Message;Write-Output ('Isolated database test incident: '+$failure)}
finally{
 [Environment]::SetEnvironmentVariable('SAAIA_TEST_PG_CONN',$savedTestPg,'Process')
 [Environment]::SetEnvironmentVariable('PGPASSWORD',$savedPgPassword,'Process')
 [Environment]::SetEnvironmentVariable('SAAIA_TEST_CONTRACT_EXPORT_DIR',$savedContractExport,'Process')
 if(Test-Path -LiteralPath $passwordFile){Remove-Item -LiteralPath $passwordFile}
 $ownedPid=$null
 if($null -ne $managed){
  $ownedPid=$managed.Id;$managed.Refresh()
  if(-not $managed.HasExited){
   & (Join-Path $pgBin 'pg_ctl.exe') -D $dataDir -m fast -w -t 20 stop *> (Join-Path $proofDir 'pg-stop.log')
   $null=$managed.WaitForExit(10000);$managed.Refresh()
   if(-not $managed.HasExited){Stop-Process -Id $managed.Id -Force;$null=$managed.WaitForExit(10000)}
  }
 }
 $remaining=@(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count
 if($remaining){$failure='Isolated port remains occupied after shutdown';$testsExit=1}
 if($null -ne $runBinaryState){
  $binaryAudit=@($runBinaryState | ForEach-Object {
   $currentHash=(Get-FileHash -LiteralPath $_.path -Algorithm SHA256).Hash
   [ordered]@{path=$_.path;beforeSha256=$_.sha256;afterSha256=$currentHash;unchanged=($_.sha256 -eq $currentHash)}
  })
  $binaryAudit | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $proofDir 'test-binaries-after.json') -Encoding utf8
  if(@($binaryAudit | Where-Object { -not $_.unchanged }).Count){$failure='Test binaries changed during the run';$testsExit=1}
 }
 [ordered]@{startedAtUtc=$started.ToString('o');endedAtUtc=[DateTime]::UtcNow.ToString('o');ownedPid=$ownedPid;cluster=$dataDir;port=$port;remainingListeners=$remaining;testsExit=$testsExit;failure=$failure;bootstrapPasswordFileRemoved=(-not (Test-Path -LiteralPath $passwordFile));processEnvironmentRestored=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $proofDir 'resource-shutdown.json') -Encoding utf8
}
exit $testsExit
