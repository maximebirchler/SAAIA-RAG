#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1024,65535)][int]$Port = 5167,
    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string]$InstanceName = 'delete-refresh'
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$ownerPath = Join-Path $output 'environment.json'
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output) { throw 'Not an owned fixture.' }
$backend = [Uri]$owner.backendUrl
if ($backend.Scheme -ne 'http' -or $backend.Host -ne '127.0.0.1' -or $backend.Port -ne $owner.backendPort -or $Port -eq $backend.Port) { throw 'Distinct owned loopback backend and proxy ports are required.' }
if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { throw 'Owned proxy port is occupied.' }
$controlPath = Join-Path $output ('runtime/delete-refresh-control-' + $InstanceName + '.json')
$eventsPath = Join-Path $output ('delete-refresh-events-' + $InstanceName + '.jsonl')
$proofPath = Join-Path $output ('delete-refresh-proxy-' + $InstanceName + '.json')
foreach ($path in @($controlPath, $eventsPath, $proofPath)) {
    if (Test-Path -LiteralPath $path) { throw 'Proxy evidence already exists; refusing overwrite.' }
}
[IO.File]::WriteAllText($controlPath, '{"schemaVersion":"saaia-winui-delete-refresh-control.v1","enabled":false,"state":"waiting_for_delete_ack","targetSessionId":null,"targetUserId":null,"inventoryFailuresInjected":0}')
$info = [Diagnostics.ProcessStartInfo]::new((Get-Command python).Source)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$sourcePath = Join-Path $PSScriptRoot 'delete_refresh_proxy.py'
foreach ($argument in @($sourcePath, '--port', "$Port", '--upstream-port', "$($owner.backendPort)", '--control', $controlPath, '--events', $eventsPath)) { $info.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($info)
$owner.processes += @{ name = 'delete-refresh-proxy'; instanceName = $InstanceName; pid = $process.Id; path = $info.FileName; startedAtUtc = $process.StartTime.ToUniversalTime().ToString('o'); port = $Port }
$owner | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ownerPath -Encoding utf8
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
do {
    try { $ready = Invoke-RestMethod "http://127.0.0.1:$Port/ready" -TimeoutSec 3; if ($ready.ok) { break } } catch { }
    if ($process.HasExited -or [DateTimeOffset]::UtcNow -gt $deadline) { throw 'Delete-refresh fault proxy did not become ready.' }
    Start-Sleep -Milliseconds 100
} while ($true)
@{ schemaVersion = 'saaia-winui-delete-refresh-proxy.v1'; proxyUrl = "http://127.0.0.1:$Port"; upstreamUrl = $owner.backendUrl;
    controlPath = $controlPath; initiallyEnabled = $false; exactSessionAndUserRequired = $true;
    injection = 'One GET /chat/sessions500 only after the target DELETE receives a real2xx';
    allOtherRequestsForwarded = $true; databaseModifiedForInjection = $false; backendCodeModified = $false;
    sourceSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash;
    instanceName = $InstanceName; actualUserConfigurationModified = $false } | ConvertTo-Json | Set-Content -LiteralPath $proofPath -Encoding utf8
Write-Output "Owned delete-refresh proxy ready on127.0.0.1:$Port; injection disabled until explicitly armed for a test session/user."
