#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int]$Port = 5147,
    [ValidatePattern('^$|^[A-Za-z0-9_-]{1,64}$')][string]$InstanceName = ''
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$ownerPath = Join-Path $output 'environment.json'
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output) { throw 'Not an owned fixture.' }
if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { throw 'Owned proxy port is occupied.' }
$suffix = if ($InstanceName) { '-' + $InstanceName } else { '' }
$controlPath = Join-Path $output ('runtime/storage-proxy-control' + $suffix + '.json')
if (Test-Path -LiteralPath $controlPath) { throw 'Proxy evidence already exists; refusing overwrite.' }
[IO.File]::WriteAllText($controlPath, '{"failUserMessagesRemaining":0,"delayUserMessagesSeconds":0,"failGetMessagesRemaining":0,"failReadyRemaining":0}')
$info = [Diagnostics.ProcessStartInfo]::new((Get-Command python).Source)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
foreach ($argument in @((Join-Path $PSScriptRoot 'storage_proxy.py'), '--port', "$Port", '--upstream-port', "$($owner.backendPort)", '--control', $controlPath, '--events', (Join-Path $output ('storage-proxy-events' + $suffix + '.jsonl')))) { $info.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($info)
$owner.processes += @{ name = 'storage-proxy'; instanceName = $InstanceName; pid = $process.Id; path = $info.FileName; startedAtUtc = $process.StartTime.ToUniversalTime().ToString('o'); port = $Port }
$owner | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ownerPath -Encoding utf8
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
do {
    try { $ready = Invoke-RestMethod "http://127.0.0.1:$Port/ready" -TimeoutSec 3; if ($ready.ok) { break } } catch { }
    if ($process.HasExited -or [DateTimeOffset]::UtcNow -gt $deadline) { throw 'Storage fault proxy did not become ready.' }
    Start-Sleep -Milliseconds 100
} while ($true)
@{ schemaVersion = 'saaia-winui-storage-proxy.v1'; proxyUrl = "http://127.0.0.1:$Port"; upstreamUrl = $owner.backendUrl;
    controlPath = $controlPath; postFailuresScopedToUserMessages = $true; backendCodeModified = $false; databaseModifiedForInjection = $false;
    instanceName = $InstanceName; sourceSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'storage_proxy.py')).Hash;
    maximumUserMessageDelaySeconds = 120; additionalFaults = @('GET messages500', 'GET ready503');
    actualUserConfigurationModified = $false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output ('storage-proxy' + $suffix + '.json')) -Encoding utf8
Write-Output "Synthetic storage fault proxy ready on127.0.0.1:$Port."
