#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'OwnedIngestionConfig.ps1')
. (Join-Path $PSScriptRoot 'OwnedIngestionLifecycle.ps1')
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root=[IO.Path]::GetFullPath($OutputDirectory)
$prefix=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(-not $root.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $root)){throw 'Use a fresh owned artifacts directory for pure lifecycle guards.'}
$fixture=Join-Path $root 'local-fixture';$documents=Join-Path $fixture 'runtime/documents';$source=Join-Path $root 'synthetic-source'
foreach($directory in @($documents,$source)){[IO.Directory]::CreateDirectory($directory)|Out-Null}
$bytes=[Text.Encoding]::UTF8.GetBytes('Synthetic guard bytes; not a PDF conversion or model input.')
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
foreach($directory in @($documents,$source)){[IO.File]::WriteAllBytes((Join-Path $directory 'synthetic-guard.pdf'),$bytes)}
$owner=@{schemaVersion='saaia-winui-local-runtime.v1';outputDirectory=$fixture;documentsRoot=$documents;backendUrl='http://127.0.0.1:5129'}
$case=@{caseId='synthetic-guard';generator='synthetic_guard';sourceSha256=$hash}
$document=@{docPath='synthetic-guard.pdf';docId='00112233-4455-6677-8899-aabbccddeeff';sourceSha256=$hash;indexedVersion=1;activeVectorPlanFingerprint=('a'*64)}
$private=Join-Path $root 'private-observations/lifecycle';$collection='ingestion_'+('b'*32)
$results=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[scriptblock]$Body,[bool]$Reject=$false){
    $rejected=$false;try{& $Body|Out-Null}catch{$rejected=$true}
    $ok=($rejected -eq $Reject);$results.Add(@{name=$Name;passed=$ok;expectedRejection=$Reject})
    if(-not $ok){throw ('Pure lifecycle guard failed: '+$Name)}
}
function Validate([object]$Own=$owner,[object]$Selected=$case,[object]$Doc=$document,[string]$Private=$private,[string]$VectorUrl='http://127.0.0.1:16341',[string]$Collection=$collection){
    Assert-OwnedIngestionLifecycleInput $repo $fixture $Private $Own $source $Selected $Doc $VectorUrl $Collection
}
Check 'one owned generated hash-pinned published document accepted' {Validate}
Check 'target traversal rejected before action' {$x=$case.Clone();$x.caseId='../foreign';Validate -Selected $x} $true
Check 'another selected document rejected' {$x=$document.Clone();$x.docPath='another.pdf';Validate -Doc $x} $true
Check 'invalid UUID rejected' {$x=$document.Clone();$x.docId='not-an-id';Validate -Doc $x} $true
Check 'unknown source hash rejected' {$x=$case.Clone();$x.sourceSha256='b'*64;Validate -Selected $x} $true
Check 'source with no generator rejected' {$x=$case.Clone();$x.generator='';Validate -Selected $x} $true
Check 'unpublished document rejected' {$x=$document.Clone();$x.indexedVersion=0;Validate -Doc $x} $true
Check 'unscoped vector plan rejected' {$x=$document.Clone();$x.activeVectorPlanFingerprint='';Validate -Doc $x} $true
Check 'foreign documents root rejected' {$x=$owner.Clone();$x.documentsRoot=$source;Validate -Own $x} $true
Check 'public captures directory rejected' {Validate -Private (Join-Path $root 'public')} $true
Check 'foreign Qdrant rejected' {Validate -VectorUrl 'http://192.0.2.1:16341'} $true
Check 'normal collection rejected' {Validate -Collection 'knowledge_base'} $true
Check 'plan UUID matches independent Python/.NET little-endian fixture' {
    if((Get-OwnedLifecycleVectorPointId ([Guid]'00112233-4455-6677-8899-aabbccddeeff') ('a'*64)) -cne 'e1eb9298-4800-2f2d-9c59-4f9c81aa0aa5'){throw 'Point identity bytes differ.'}
}
Check 'incomplete plan rejected by point identity' {Get-OwnedLifecycleVectorPointId ([Guid]::NewGuid()) 'short'} $true
foreach($file in @('OwnedIngestionLifecycle.ps1','Test-OwnedIngestionLifecycleGuards.ps1')){
    Check ('AST '+$file) {
        $tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$tokens,[ref]$errors)
        if($errors.Count){throw 'Lifecycle AST is invalid.'}
    }
}
$proof=[ordered]@{schemaVersion='saaia-owned-ingestion-lifecycle-offline-guards.v1';passed=$true;caseCount=$results.Count;
    results=@($results.ToArray());serviceCalls=0;processesStarted=0;credentialsRead=0;databaseOperations=0;modelCalls=0}
[IO.File]::WriteAllText((Join-Path $root 'guards.json'),($proof|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Write-Output ('Pure lifecycle guards PASS '+$results.Count+'; no service, credential, database or model action.')
