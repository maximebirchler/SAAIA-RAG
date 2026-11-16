#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ClientPublishDirectory = 'artifacts/presentation-laptop-20261116/publish-client-v2',
    [string]$Version = '20261116d'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Sortie déjà présente : $output" }
$publish = [IO.Path]::GetFullPath((Join-Path $repo $ClientPublishDirectory))
$repoResolved = [IO.Path]::GetFullPath($repo)
if (-not $publish.StartsWith($repoResolved + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Le client publié doit rester dans le dépôt.'
}
if ($Version -notmatch '^20[0-9]{6}[a-z]$') { throw 'Version de présentation invalide.' }
$launcher = Join-Path $repo 'artifacts/presentation-laptop-20261116/publish-launcher-v2/SAAIA.Presentation.Launcher.exe'
$cpuZip = Join-Path $repo 'artifacts/presentation-laptop-20261116/llama-b10098-bin-win-cpu-x64.zip'
$vulkanZip = Join-Path $repo 'artifacts/presentation-laptop-20261116/llama-b10098-bin-win-vulkan-x64.zip'
$model = Join-Path $env:LOCALAPPDATA 'SAAIA/Models/Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf'
$prerequisites = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.windowsappsdk.runtime/1.8.260209005/tools/MSIX/win10-x64'
foreach ($file in @((Join-Path $publish 'SAAIA.Client.WinUI.exe'), $launcher, $cpuZip, $vulkanZip, $model)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Source absente : $file" }
}
if ((Get-FileHash -LiteralPath $cpuZip -Algorithm SHA256).Hash -ine 'B6DB5D15CEEF04B0835005A45245AA03BE687BF1674888AD11541A370B0387B8') {
    throw 'Le moteur CPU ne correspond pas à la somme SHA-256 publiée par ggml-org.'
}
if ((Get-FileHash -LiteralPath $vulkanZip -Algorithm SHA256).Hash -ine 'CCF80A547C4FE442383B7C6986F564104A12A3C67B079FAF2FDFA14C3F17EE76') {
    throw 'Le moteur Vulkan ne correspond pas à la somme SHA-256 publiée par ggml-org.'
}
$outResolved = [IO.Path]::GetFullPath($output)
if (-not $outResolved.StartsWith($repoResolved + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'La construction doit rester dans le dépôt.'
}
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath $publish -Destination (Join-Path $output 'Client') -Recurse
Get-ChildItem -LiteralPath (Join-Path $output 'Client') -Filter '*.pdb' -Recurse -File | Remove-Item -Force
foreach ($runtime in @(@($cpuZip, 'Moteur'), @($vulkanZip, 'MoteurVulkan'))) {
    $destination = Join-Path $output $runtime[1]
    New-Item -ItemType Directory -Path $destination | Out-Null
    $extract = Join-Path (Split-Path -Parent $output) ('runtime-extraction-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $runtime[0] -DestinationPath $extract
    foreach ($file in Get-ChildItem -LiteralPath $extract -File) {
        if ($file.Extension -eq '.dll' -or $file.Name -eq 'llama-server.exe') {
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $destination 'llama-server.exe'))) { throw "Moteur incomplet : $($runtime[1])" }
}
New-Item -ItemType Directory -Path (Join-Path $output 'Modele') | Out-Null
Copy-Item -LiteralPath $model -Destination (Join-Path $output 'Modele')
New-Item -ItemType Directory -Path (Join-Path $output 'Prerequis') | Out-Null
foreach ($name in @('Microsoft.WindowsAppRuntime.1.8.msix','Microsoft.WindowsAppRuntime.Main.1.8.msix',
        'Microsoft.WindowsAppRuntime.DDLM.1.8.msix','Microsoft.WindowsAppRuntime.Singleton.1.8.msix')) {
    Copy-Item -LiteralPath (Join-Path $prerequisites $name) -Destination (Join-Path $output 'Prerequis')
}
Copy-Item -LiteralPath $launcher -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Installer-SAAIA.ps1') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Installer-SAAIA.cmd') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LISEZ-MOI.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination (Join-Path $output 'Licences') -Recurse
# Windows PowerShell 5.1 needs a UTF-8 BOM to read the French installer text correctly.
$installerPath = Join-Path $output 'Installer-SAAIA.ps1'
[IO.File]::WriteAllText($installerPath, [IO.File]::ReadAllText($installerPath), [Text.UTF8Encoding]::new($true))

$allFiles = @(Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object { $_.Name -ne 'manifest.json' })
$entries = foreach ($file in $allFiles) {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($output, $file.FullName).Replace('\','/')
        bytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifest = [ordered]@{
    schemaVersion = 'saaia-presentation-client.v1'
    version = $Version
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    sourceCommit = (git -C $repo rev-parse HEAD).Trim()
    productStatus = 'VERSION_DE_PRESENTATION_TESTE_NON_APPROUVE'
    backendUrl = 'https://saaia-server.taila2196b.ts.net'
    localModel = 'Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf'
    modelProvider = 'local-vulkan-or-cpu'
    cpuRuntime = 'ggml-org/llama.cpp b10098'
    vulkanRuntime = 'ggml-org/llama.cpp b10098'
    windowsAppSdk = '1.8.260209005'
    files = @($entries)
}
[IO.File]::WriteAllText((Join-Path $output 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Output "Présentation préparée : $output ; $($entries.Count) fichiers vérifiables."
