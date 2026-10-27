#requires -Version 7.4
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'PlanningRagRuntime.ps1')
$temp=Join-Path ([IO.Path]::GetTempPath()) ('saaia-rag-guards-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp)|Out-Null
$checks=0
function Check([bool]$Value,[string]$Reason){if(-not $Value){throw $Reason};$script:checks++}
function Reject([scriptblock]$Body){$failed=$false;try{& $Body|Out-Null}catch{$failed=$true};Check $failed 'A changed owned dependency identity was accepted.'}
function Save-Proof($Value,$Path){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30));return (Get-FileHash -LiteralPath $Path).Hash}
try{
    $run='b8d5bc6a-f5d4-44cc-a97d-adf33d0c0b81'
    $sourceRoot='/home/maxime/saaia-ingestion-qualification/'+$run
    $command='ssh.exe -N -L 127.0.0.1:22531:127.0.0.1:22523 -L 127.0.0.1:22532:127.0.0.1:22524 maxime@saaia-server'
    $script:fakeCommand=$command;$script:fakePid=1234;$script:fakePath='C:/Windows/System32/OpenSSH/ssh.exe'
    $script:fakeStart=[DateTime]::Parse('2026-10-27T18:59:00Z').ToUniversalTime();$script:listenerPid=$fakePid
    function Get-Process {param([int]$Id) return [pscustomobject]@{Id=$script:fakePid;Path=$script:fakePath;StartTime=$script:fakeStart}}
    function Get-CimInstance {param($ClassName,$Filter) return [pscustomobject]@{CommandLine=$script:fakeCommand}}
    function Get-NetTCPConnection {param($State,$LocalPort) return [pscustomobject]@{OwningProcess=$script:listenerPid;LocalAddress='127.0.0.1'}}
    $tunnel=@{schemaVersion='saaia-owned-ssh-forward.v1';sourceOwnerRoot=$sourceRoot;hostTarget='maxime@saaia-server';
        process=@{pid=$fakePid;path=$fakePath;startedAtUtc=$fakeStart.ToString('o');commandLineSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($command)))};
        forwardings=@(@{localHost='127.0.0.1';localPort=22531;remoteHost='127.0.0.1';remotePort=22523},@{localHost='127.0.0.1';localPort=22532;remoteHost='127.0.0.1';remotePort=22524})}
    $tunnelPath=Join-Path $temp 'tunnel.json';$tunnelHash=Save-Proof $tunnel $tunnelPath
    $value=@{sourceOwnerRoot=$sourceRoot;tunnelEvidence=@{path=$tunnelPath;sha256=$tunnelHash}}
    Assert-PlanningTunnelIdentity $value;Check $true 'Exact synthetic SSH process must pass.'
    $script:listenerPid=4321;Reject {Assert-PlanningTunnelIdentity $value};$script:listenerPid=$fakePid
    $script:fakeStart=$fakeStart.AddSeconds(1);Reject {Assert-PlanningTunnelIdentity $value};$script:fakeStart=$fakeStart.AddSeconds(-1)
    $script:fakeCommand=$command+' changed';Reject {Assert-PlanningTunnelIdentity $value};$script:fakeCommand=$command
    $script:fakePath='C:/Windows/System32/other.exe';Reject {Assert-PlanningTunnelIdentity $value};$script:fakePath='C:/Windows/System32/OpenSSH/ssh.exe'
    $value.tunnelEvidence.sha256='0'*64;Reject {Assert-PlanningTunnelIdentity $value}
    $manifest=Join-Path $temp 'corpus.json';[IO.File]::WriteAllText($manifest,'{"synthetic":true}');$manifestHash=(Get-FileHash -LiteralPath $manifest).Hash
    $rag=@{schemaVersion='saaia-planning-owned-rag-runtime.v1';origin='real_ingestion_worker';sourceOwnerRunId=$run;
        corpusManifest=@{path=$manifest;sha256=$manifestHash};Rag=@{EmbeddingsBaseUrl='http://127.0.0.1:22531';QdrantBaseUrl='http://127.0.0.1:22532';
        QdrantCollection=('ingestion_'+([Guid]$run).ToString('N'));EnableRerank=$false;RequireQdrantAuthInProd=$false}}
    $ragPath=Join-Path $temp 'rag.json';$ragHash=Save-Proof $rag $ragPath
    $profile=@{realRag=@{path=$ragPath;sha256=$ragHash};corpus=@{manifestPath=$manifest;manifestSha256=$manifestHash}}
    $null=Read-PlanningRealRagProfile $profile;Check $true 'Exact same publication binding must pass.'
    $profile.corpus.manifestSha256='f'*64;Reject {Read-PlanningRealRagProfile $profile};$profile.corpus.manifestSha256=$manifestHash
    $rag.Rag.QdrantBaseUrl='http://127.0.0.1:6333';$profile.realRag.sha256=Save-Proof $rag $ragPath;Reject {Read-PlanningRealRagProfile $profile}
    $blocked=$false
    try{& (Join-Path $PSScriptRoot '../validation-winui/start-environment.ps1') -OutputDirectory (Join-Path $temp 'must-not-exist') -PlanningCloneOnly -BackendOverlayPath 'never-read'}catch{$blocked=$_.Exception.Message -eq 'Planning clone keeps ingestion disabled and cannot use an ingestion overlay.'}
    Check ($blocked -and -not (Test-Path -LiteralPath (Join-Path $temp 'must-not-exist'))) 'Clone must reject ingestion overlay before any environment creation.'
    foreach($name in @('PlanningRagRuntime.ps1','restore-owned-windows-corpus.ps1')){
        $tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name),[ref]$tokens,[ref]$errors)
        Check ($errors.Count -eq 0) 'New opt-in runtime helper must parse.'
    }
    Write-Output "$checks real Rag guards passed with mocked process/listener identities; no real process, SQL, key, network or service access."
}finally{
    $resolved=[IO.Path]::GetFullPath($temp)
    if([IO.Path]::GetDirectoryName($resolved) -ine [IO.Path]::GetTempPath().TrimEnd('\','/')){throw 'Unsafe test cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
