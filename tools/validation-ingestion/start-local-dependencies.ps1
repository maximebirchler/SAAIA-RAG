#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int]$DoclingPort = 15101,
    [int]$UpstreamPort = 15102,
    [int]$QdrantPort = 16341,
    [switch]$EnableLocalEmbeddings,
    [int]$EmbeddingPort = 15103
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'OwnedDependencies.ps1')
$runtime = Assert-DependencyProjectPath $RuntimeDirectory $repo 'out'
$output = Assert-DependencyProjectPath $OutputDirectory $repo 'artifacts'
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh dependency evidence directory.' }
$ports=@($DoclingPort,$UpstreamPort,$QdrantPort)+@($EmbeddingPort | Where-Object {$EnableLocalEmbeddings})
$null=Get-ValidatedDependencyPorts $ports
foreach ($port in $ports) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "Port already in use: $port" }
}
$python = Join-Path $runtime 'docling-venv/Scripts/python.exe'
$qdrant = Join-Path $runtime 'qdrant-v1.20.0/qdrant.exe'
foreach ($file in @($python,$qdrant)) { if (-not (Test-Path -LiteralPath $file)) { throw 'Provision the pinned local runtime first.' } }
$modelPath=Join-Path $runtime 'models/huggingface/hub/models--intfloat--multilingual-e5-base/snapshots/d128750597153bb5987e10b1c3493a34e5a4502a'
if($EnableLocalEmbeddings -and (-not(Test-Path -LiteralPath (Join-Path $modelPath 'model.safetensors') -PathType Leaf) -or
   -not(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'local_embeddings.py') -PathType Leaf))){throw 'Prefetch the pinned E5 files before starting any dependency.'}
$embeddingWeightsSha=$null
if($EnableLocalEmbeddings){
    $embeddingWeightsSha=Assert-DependencyFileHash -Path (Join-Path $modelPath 'model.safetensors') -ExpectedSha256 'a18a44fad1d0b46ded15928144138cff1135d5cc8233bdd90be5f18822de09a7'
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$state = [ordered]@{schemaVersion='saaia-local-ingestion-dependencies.v1';runId=[Guid]::NewGuid().ToString('N');
    state='starting';outputDirectory=$output;startedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
    doclingUrl="http://127.0.0.1:$DoclingPort";upstreamUrl="http://127.0.0.1:$UpstreamPort";
    qdrantUrl="http://127.0.0.1:$QdrantPort";processes=@();externalLlmCalls=0;productStatus='TESTE_NON_APPROUVE'}
if($embeddingWeightsSha){$state['embeddingModelWeightsSha256']=$embeddingWeightsSha}
function Save-State { [IO.File]::WriteAllText((Join-Path $output 'dependencies.json'),($state | ConvertTo-Json -Depth 8)) }
function Start-Owned([string]$Name,[string]$Executable,[string[]]$Arguments,[hashtable]$Environment) {
    $spec = [ordered]@{executable=$Executable;arguments=$Arguments;stdout=(Join-Path $output "$Name.stdout.log");stderr=(Join-Path $output "$Name.stderr.log")}
    $specPath = Join-Path $output "$Name-process.json"
    [IO.File]::WriteAllText($specPath,($spec | ConvertTo-Json -Depth 5))
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory=$output
    foreach ($arg in @('-NoProfile','-File',(Join-Path $repo 'tools/validation-winui/run-helper.ps1'),'-Specification',$specPath)) { $info.ArgumentList.Add($arg) }
    foreach ($environmentName in @($info.Environment.Keys)) {
        if ($environmentName -match 'TOKEN|SECRET|OPENAI|AZURE|AWS_|GOOGLE|RUNPOD|ConnectionString|ConfigSignature|SAAIA_LLM|SAAIA_ADVANCED|QDRANT') {
            $info.Environment.Remove($environmentName) | Out-Null
        }
    }
    foreach ($entry in $Environment.GetEnumerator()) { $info.Environment[$entry.Key]=[string]$entry.Value }
    $p=[Diagnostics.Process]::Start($info)
    $state.processes += @{name=$Name;pid=$p.Id;path=$info.FileName;startedAtUtc=$p.StartTime.ToUniversalTime().ToString('o');specification=$specPath}
    Save-State
}
Save-State
try {
    # Qdrant's nested Gridstore filenames can exceed the Windows path limit under an evidence root.
    # Keep the owned data path short; reports and process logs stay in the immutable run directory.
    $storage=Join-Path $repo ('out/qdrant-validation/'+$state.runId.Substring(0,12))
    if (Test-Path -LiteralPath $storage) { throw 'Fresh owned Qdrant data directory required.' }
    [IO.Directory]::CreateDirectory($storage) | Out-Null
    $state['qdrantDataDirectory']=$storage
    Save-State
    $configuration = @"
log_level: INFO
telemetry_disabled: true
storage:
  storage_path: '$($storage.Replace('\','/'))/storage'
  snapshots_path: '$($storage.Replace('\','/'))/snapshots'
service:
  host: 127.0.0.1
  http_port: $QdrantPort
  grpc_port: null
  max_workers: 2
"@
    $qdrantConfig=Join-Path $output 'qdrant.yaml'
    [IO.File]::WriteAllText($qdrantConfig,$configuration)
    Start-Owned 'qdrant' $qdrant @('--config-path',$qdrantConfig,'--disable-telemetry') @{}
    $common=@{PYTHONPATH=(Join-Path $repo 'infra/docling-sidecar');PYTHONUTF8='1';PYTHONIOENCODING='utf-8';
        PYTHONDONTWRITEBYTECODE='1';TORCH_HOME=(Join-Path $runtime 'models/torch');XDG_CACHE_HOME=(Join-Path $runtime 'cache');
        HF_HOME=(Join-Path $runtime 'models/huggingface');DOCLING_CACHE_DIR=(Join-Path $runtime 'models/docling');
        DOCLING_SERVE_ARTIFACTS_PATH=(Join-Path $runtime 'models/docling');HF_HUB_OFFLINE='1';TRANSFORMERS_OFFLINE='1';
        HF_HUB_DISABLE_TELEMETRY='1';DO_NOT_TRACK='1';DOCLING_SERVE_ENABLE_REMOTE_SERVICES='false';
        DOCLING_SERVE_ENABLE_MANAGEMENT_ENDPOINTS='false';DOCLING_SERVE_LOAD_MODELS_AT_BOOT='true'}
    Start-Owned 'docling-upstream' $python @('-m','saaia_docling.upstream','run','--host','127.0.0.1','--port',"$UpstreamPort",'--workers','1') $common
    $wrapper=@{} + $common
    $wrapper['SAAIA_DOCLING_UPSTREAM_URL']=$state.upstreamUrl
    Start-Owned 'docling-wrapper' $python @('-m','uvicorn','saaia_docling.app:app','--host','127.0.0.1','--port',"$DoclingPort",'--workers','1','--no-access-log') $wrapper
    if ($EnableLocalEmbeddings) {
        $state['embeddingUrl']="http://127.0.0.1:$EmbeddingPort"
        $state['embeddingRuntime']='saaia-validation-transformers-cpu.v1'
        Start-Owned 'local-embeddings' $python @((Join-Path $repo 'tools/validation-ingestion/local_embeddings.py'),'--model-path',$modelPath,'--port',"$EmbeddingPort") $common
    }
    $state.state='started_readiness_pending'; Save-State
    Write-Output 'Owned dependency processes started; readiness and actual conversion remain to be verified.'
} catch { $state.state='failed'; $state['failureType']=$_.Exception.GetType().Name; Save-State; throw }
