#requires -Version 5.1
[CmdletBinding()]
param([switch]$PreflightOnly)
$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 'saaia-presentation-client.v1') { throw 'Archive de présentation non reconnue.' }
if (-not [Environment]::Is64BitOperatingSystem) { throw 'Windows x64 est nécessaire.' }

foreach ($entry in $manifest.files) {
    $path = Join-Path $package $entry.path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Fichier manquant : $($entry.path)" }
    $file = Get-Item -LiteralPath $path
    if ($file.Length -ne [long]$entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $entry.sha256) {
        throw "Intégrité incorrecte : $($entry.path)"
    }
}

$runtimeVersion = [version]'8000.946.1701.0'
$installedRuntime = @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8' -ErrorAction SilentlyContinue |
    Where-Object { $_.Architecture -eq 'X64' -and [version]$_.Version -ge $runtimeVersion -and $_.Status -eq 'Ok' })
if ($installedRuntime.Count -eq 0) {
    foreach ($name in @('Microsoft.WindowsAppRuntime.1.8.msix','Microsoft.WindowsAppRuntime.Main.1.8.msix',
            'Microsoft.WindowsAppRuntime.DDLM.1.8.msix','Microsoft.WindowsAppRuntime.Singleton.1.8.msix')) {
        Add-AppxPackage -Path (Join-Path $package ('Prerequis\' + $name)) -ErrorAction Stop
    }
    $installedRuntime = @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8' -ErrorAction SilentlyContinue |
        Where-Object { $_.Architecture -eq 'X64' -and [version]$_.Version -ge $runtimeVersion -and $_.Status -eq 'Ok' })
    if ($installedRuntime.Count -eq 0) { throw 'Windows App Runtime 1.8 x64 ne s''est pas enregistré.' }
}
if ($PreflightOnly) {
    Write-Host 'Archive et prérequis vérifiés.'
    return
}

$root = Join-Path $env:LOCALAPPDATA 'SAAIA\presentation'
$install = Join-Path $root ('app-' + $manifest.version)
$profile = Join-Path $root 'profile'
$settings = Join-Path $profile 'client\settings.json'
if (Test-Path -LiteralPath $install) { throw "Cette version est déjà installée : $install. Les fichiers existants sont préservés." }
$shell = New-Object -ComObject WScript.Shell
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'SAAIA - Présentation.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'SAAIA - Présentation.lnk'))
foreach ($shortcutPath in $shortcuts) {
    if (Test-Path -LiteralPath $shortcutPath) {
        throw "Raccourci existant préservé : $shortcutPath"
    }
}
New-Item -ItemType Directory -Path $root -Force | Out-Null
$temporary = Join-Path $root ('installation-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    foreach ($directory in @('Client','Modele','Moteur','MoteurVulkan')) {
        Copy-Item -LiteralPath (Join-Path $package $directory) -Destination (Join-Path $temporary $directory) -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $package 'SAAIA.Presentation.Launcher.exe') -Destination $temporary
    Move-Item -LiteralPath $temporary -Destination $install
} catch { throw }

New-Item -ItemType Directory -Path (Split-Path -Parent $settings) -Force | Out-Null
if (-not (Test-Path -LiteralPath $settings)) {
    $vulkanExe = Join-Path $install 'MoteurVulkan\llama-server.exe'
    $useVulkan = $false
    try {
        $vulkanDevices = & $vulkanExe --list-devices 2>&1
        $useVulkan = $LASTEXITCODE -eq 0 -and ($vulkanDevices -match 'Vulkan\d+:')
    } catch {
        Write-Host 'Détection graphique indisponible ; utilisation du moteur CPU.'
    }
    $enginePath = if ($useVulkan) { $vulkanExe } else { Join-Path $install 'Moteur\llama-server.exe' }
    $engineArgs = if ($useVulkan) {
        '--ctx-size 8192 --n-gpu-layers auto --fit on --fit-target 512 --alias Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf'
    } else {
        '--ctx-size 8192 --n-gpu-layers 0 --alias Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf'
    }
    $clientSettings = [ordered]@{
        BackendUrl='https://saaia-server.taila2196b.ts.net'; BackendUrlAlternates=''
        ShowAdvancedUi=$false; AutoConnect=$true; UiLanguage='fr'; UiTheme='dark'
        LlmMode='embedded'; UseLocalLlm=$true; ManageLocalLlmProcess=$true
        AutoStartOnConnect=$true; EagerLoad=$false
        LlamaExePath=$enginePath
        ModelPath=(Join-Path $install 'Modele\Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf')
        Host='127.0.0.1'; Port=1234
        ModelId='Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf'
        ExtraArgs=$engineArgs; UbatchSize=128; ThreadsBatch=4
        StartupTimeoutSeconds=180; LlmTemperature=0.2; LlmMaxOutputTokens=1600
        RagQualityPreset='balanced'
    }
    [IO.File]::WriteAllText($settings, ($clientSettings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Host $(if ($useVulkan) { 'Moteur graphique Vulkan sélectionné.' } else { 'Moteur CPU sélectionné.' })
}

foreach ($shortcutPath in $shortcuts) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $install 'SAAIA.Presentation.Launcher.exe'
    $shortcut.WorkingDirectory = $install
    $shortcut.IconLocation = (Join-Path $install 'Client\Assets\SAAIA_Main.ico') + ',0'
    $shortcut.Description = 'Client SAAIA de présentation, profil séparé'
    $shortcut.Save()
}

Write-Host "SAAIA installé : $install"
Write-Host 'Raccourcis créés sur le Bureau et dans le menu Démarrer.'
Write-Host 'Au premier lancement, entrez la clé API du backend dans l''assistant SAAIA.'
