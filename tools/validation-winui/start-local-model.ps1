#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [int]$Port = 18088)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$ownerPath = Join-Path $output 'environment.json'
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output) { throw 'Not an owned fixture.' }
if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { throw 'Dedicated model port is occupied.' }
$model = Join-Path $env:LOCALAPPDATA 'SAAIA/Models/Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf'
$executable = Join-Path $env:LOCALAPPDATA 'SAAIA/llm/runtime/win-cuda-x64/b10098/llama-server.exe'
foreach ($path in @($model, $executable)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Existing local model/runtime is absent; no download is performed.' } }
$specPath = Join-Path $output 'runtime/local-model-process.json'
if (Test-Path -LiteralPath $specPath) { throw 'Local model evidence already exists; refusing to overwrite.' }
$spec = @{ executable = $executable; arguments = @('--model', $model, '--alias', 'Qwen3-4B-Instruct-2507', '--host', '127.0.0.1', '--port', "$Port", '--ctx-size', '8192', '--n-gpu-layers', '99', '--parallel', '1', '--threads', '8');
    stdout = (Join-Path $output 'local-model.stdout.log'); stderr = (Join-Path $output 'local-model.stderr.log') }
[IO.File]::WriteAllText($specPath, ($spec | ConvertTo-Json))
$info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
foreach ($argument in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-helper.ps1'), '-Specification', $specPath)) { $info.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($info)
$owner.processes += @{ name = 'local-model-runner'; pid = $process.Id; path = $info.FileName; port = $Port; startedAtUtc = $process.StartTime.ToUniversalTime().ToString('o') }
$owner | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ownerPath -Encoding utf8
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
do {
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 3
        if ($health.status -eq 'ok') { break }
    } catch { }
    if ($process.HasExited -or [DateTimeOffset]::UtcNow -gt $deadline) { throw 'Dedicated local model did not become ready.' }
    Start-Sleep -Milliseconds 500
} while ($true)
@{ schemaVersion = 'saaia-winui-local-model.v1'; endpoint = "http://127.0.0.1:$Port/v1"; modelAlias = 'Qwen3-4B-Instruct-2507';
    modelPath = $model; bytes = (Get-Item -LiteralPath $model).Length; executableSha256 = (Get-FileHash -LiteralPath $executable).Hash;
    contextTokens = 8192; gpuLayers = 99; existingWeightModified = $false; downloadPerformed = $false; paidProviderUsed = $false; ready = $true } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'local-model.json') -Encoding utf8
Write-Output "Dedicated local Qwen model ready on127.0.0.1:$Port."
