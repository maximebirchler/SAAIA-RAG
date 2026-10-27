#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repo 'tools/validation-ingestion/OwnedIngestionConfig.ps1')
. (Join-Path $PSScriptRoot 'RealIngestion.Common.ps1')
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $root){throw 'Fresh test evidence required.'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[scriptblock]$Body,[bool]$Reject=$false){
    $failed=$false;try{&$Body|Out-Null}catch{$failed=$true}
    $ok=$failed -eq $Reject;$checks.Add(@{name=$Name;passed=$ok})
    if(-not $ok){throw ('Real-ingestion pure guard failed: '+$Name)}
}
function Overlay {
    @{
        Ingestion=@{WorkerEnabled=$true;CanonicalArtifactsEnabled=$true;WorkerConcurrency=1;WatcherEnabled=$false;ScannerEnabled=$false;
            ReindexIfQdrantEmpty=$false;OcrEnabled=$false;OcrImagePageEnabled=$false;MinFileAgeSeconds=0;ChunkMaxWords=200;ChunkMinWords=25;ChunkOverlapWords=0}
        Rag=@{QdrantBaseUrl='http://127.0.0.1:16341';QdrantCollection='synthetic';QdrantRuntimeRevision='synthetic';
            EmbeddingsBaseUrl='http://127.0.0.1:15103';EmbeddingsModel='synthetic';EnableRerank=$false;RequireQdrantAuthInProd=$false}
        DocumentIntelligence=@{Enabled=$true;Provider='docling';DoOcr=$true;BaseUrl='http://127.0.0.1:15101';EngineVersion='synthetic';
            ParseEngineVersion='synthetic';ModelPackageVersion='synthetic';OcrEngineVersion='synthetic';DeploymentRevision='synthetic';
            RegionalTableAlternativeReadingsEnabled=$true;NativeTypographyObservationsEnabled=$true;DoclingInputGeometryCaptureEnabled=$true;
            RegionalTextRepairEnabled=$false;RegionalTextRepairMaxRegions=4}
    }
}
Check 'new profile remains forbidden without opt in' {Assert-OwnedIngestionOverlayValues (Overlay)} $true
Check 'explicit bounded observation profile accepted' {Assert-OwnedIngestionOverlayValues (Overlay) -AllowPlanningIngestionProfile}
Check 'arbitrary chunk tuning remains forbidden after opt in' {$x=Overlay;$x.Ingestion.ChunkMinWords=1;Assert-OwnedIngestionOverlayValues $x -AllowPlanningIngestionProfile} $true
Check 'regional unbounded work forbidden' {$x=Overlay;$x.DocumentIntelligence.RegionalTextRepairMaxRegions=40;Assert-OwnedIngestionOverlayValues $x -AllowPlanningIngestionProfile} $true
Check 'observation flags cannot be strings' {$x=Overlay;$x.DocumentIntelligence.NativeTypographyObservationsEnabled='true';Assert-OwnedIngestionOverlayValues $x -AllowPlanningIngestionProfile} $true
foreach($section in @('Auth','Database','AdvancedAnalysis')){
    Check ('no '+$section+' injection after opt in') {$x=Overlay;$x[$section]=@{Enabled=$true};Assert-OwnedIngestionOverlayValues $x -AllowPlanningIngestionProfile} $true
}
Check 'legacy OCR remains off after opt in' {$x=Overlay;$x.Ingestion.OcrEnabled=$true;Assert-OwnedIngestionOverlayValues $x -AllowPlanningIngestionProfile} $true
foreach($file in @('new-owned-real-ingestion.ps1','RealIngestion.Common.ps1')){
    Check ('AST '+$file) {$tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$tokens,[ref]$errors);if($errors.Count){throw 'AST errors'}}
}
Write-RealPlanningJson -Path (Join-Path $root 'checks.json') -Value @{passed=$true;count=$checks.Count;checks=@($checks.ToArray());serviceCalls=0;modelCalls=0;processesStarted=0;secretReads=0}
Write-Output ('Real ingestion offline guards PASS '+$checks.Count)
