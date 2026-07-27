param(
  [string]$Server            = 'maxime@100.80.213.61',
  [string]$ContextName       = 'saaia-server',
  [string]$ComposeFile       = 'infra/docker-compose.prod.yml',
  [string]$EnvFile           = 'infra/.env.server-linux',
  [string]$DeployWorkDirRel  = 'out/remote-deploy',
  [switch]$WithDependencies,
  [switch]$SkipRemoteProvision,
  [switch]$SkipConfigSync,
  [switch]$SkipReadyCheck,
  [switch]$NoCache,
  [switch]$RemoteSourceBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_common.ps1')

function Parse-Bool([string]$raw, [bool]$default) {
  if ([string]::IsNullOrWhiteSpace($raw)) { return $default }
  $x = $raw.Trim().ToLowerInvariant()
  if ($x -in @('1','true','yes','y','on')) { return $true }
  if ($x -in @('0','false','no','n','off')) { return $false }
  return $default
}

function Require-Command([string]$Name) {
  $cmd = Get-Command $Name -ErrorAction SilentlyContinue
  if ($null -eq $cmd) {
    throw "Missing command '$Name' on this laptop. Install it and retry."
  }
}

function Quote-RemoteShellArg([string]$Value) {
  if ($null -eq $Value) { return "''" }
  if ($Value.Contains("'") -or $Value -match "[`r`n]") {
    throw "Unsafe remote shell argument."
  }

  return "'" + $Value + "'"
}

function Assert-SafeRemoteUserName([string]$Value) {
  if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[a-z_][a-z0-9_-]*[$]?$') {
    throw "Unsafe remote user name '$Value'."
  }
}

function Assert-SafeRemoteInstallPath([string]$Label, [string]$Path) {
  if ([string]::IsNullOrWhiteSpace($Path)) {
    throw "$Label is required."
  }

  $normalized = $Path.Trim().Replace('\', '/')
  if ($normalized -match "[`r`n]") {
    throw "$Label contains a newline, refusing remote provisioning."
  }
  if ($normalized.Contains("'")) {
    throw "$Label contains a single quote, refusing remote provisioning."
  }
  if (-not $normalized.StartsWith('/')) {
    throw "$Label must be an absolute Linux path. Got '$Path'."
  }

  $trimmed = $normalized.TrimEnd('/')
  if ([string]::IsNullOrWhiteSpace($trimmed)) {
    throw "$Label cannot be the filesystem root."
  }

  $blocked = @('/', '/bin', '/boot', '/dev', '/etc', '/home', '/lib', '/lib64', '/opt', '/proc', '/root', '/run', '/sbin', '/srv', '/sys', '/tmp', '/usr', '/var')
  if ($blocked -contains $trimmed) {
    throw "$Label '$trimmed' is too broad for remote provisioning."
  }

  $segments = $trimmed.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
  if ($segments.Count -lt 2) {
    throw "$Label '$trimmed' is too broad for remote provisioning."
  }

  return $trimmed
}

function Get-DoclingSourceRevisionFromInspect([object[]]$InspectOutput) {
  if ($null -eq $InspectOutput -or $InspectOutput.Count -eq 0) {
    return ''
  }

  try {
    $containers = (($InspectOutput | ForEach-Object { [string]$_ }) -join "`n") |
      ConvertFrom-Json
    if ($null -eq $containers -or $containers.Count -eq 0) {
      return ''
    }

    return [string]$containers[0].Config.Labels.'com.saaia.docling.source-revision'
  }
  catch {
    return ''
  }
}

function Get-BackendSourceRevision([string]$RepoRoot) {
  $head = (& git -C $RepoRoot rev-parse HEAD).Trim()
  if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'Unable to resolve the Git HEAD used by the backend build.'
  }

  $files = New-Object System.Collections.Generic.List[System.IO.FileInfo]
  $relativeFiles = @(& git -C $RepoRoot ls-files --cached --others --exclude-standard -- `
    'contracts/SAAIA.Contracts' `
    'backend/SAAIA.Backend' `
    '.dockerignore' `
    'infra/docker-compose.prod.yml' `
    'infra/docling-sidecar' `
    'RAG.sln' `
    'global.json')
  if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate the backend Docker source files.'
  }
  foreach ($relativeFile in $relativeFiles) {
    $path = Join-Path $RepoRoot $relativeFile
    if (Test-Path -LiteralPath $path -PathType Leaf) {
      $files.Add((Get-Item -LiteralPath $path))
    }
  }
  if ($files.Count -eq 0) {
    throw 'The backend Docker source inventory is empty.'
  }

  $lines = foreach ($file in $files | Sort-Object FullName) {
    $relative = $file.FullName.Substring($RepoRoot.TrimEnd('\', '/').Length).TrimStart('\', '/').Replace('\', '/')
    $fileHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$relative|$($file.Length)|$fileHash"
  }
  $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
  $sha256 = [Security.Cryptography.SHA256]::Create()
  try {
    $sourceHash = -join ($sha256.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
  }
  finally {
    $sha256.Dispose()
  }

  return "$($head.ToLowerInvariant())-source-$sourceHash"
}

function Get-DoclingSidecarSourceRevision([string]$RepoRoot) {
  $relativeFiles = @(& git -C $RepoRoot ls-files --cached --others --exclude-standard -- `
    'infra/docling-sidecar/Dockerfile' `
    'infra/docling-sidecar/entrypoint.sh' `
    'infra/docling-sidecar/saaia_docling')
  if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate the Docling sidecar source files.'
  }

  $lines = foreach ($relativeFile in $relativeFiles | Sort-Object) {
    $path = Join-Path $RepoRoot $relativeFile
    if (Test-Path -LiteralPath $path -PathType Leaf) {
      $file = Get-Item -LiteralPath $path
      $fileHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
      "$($relativeFile.Replace('\', '/'))|$($file.Length)|$fileHash"
    }
  }
  if (-not $lines) {
    throw 'The Docling sidecar source inventory is empty.'
  }

  $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
  $sha256 = [Security.Cryptography.SHA256]::Create()
  try {
    return -join ($sha256.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
  }
  finally {
    $sha256.Dispose()
  }
}

function Ensure-DockerContext([string]$Name, [string]$ServerSpec) {
  $exists = $false
  try {
    & docker context inspect $Name *> $null
    if ($LASTEXITCODE -eq 0) { $exists = $true }
  } catch { $exists = $false }

  if (-not $exists) {
    Write-Host "== Create docker context '$Name' ==" -ForegroundColor Cyan
    & docker context create $Name --docker "host=ssh://$ServerSpec"
    if ($LASTEXITCODE -ne 0) { throw "docker context create failed ($LASTEXITCODE)" }
  }
}

function Wait-BackendReady([string]$ServerSpec, [string]$Port, [int]$MaxTries = 40, [int]$DelaySeconds = 2) {
  Write-Host "== Verify backend /ready on remote server ==" -ForegroundColor Cyan
  for ($i = 1; $i -le $MaxTries; $i++) {
    $cmd = "curl -fsS http://127.0.0.1:$Port/ready >/dev/null && echo READY || true"
    $out = & ssh $ServerSpec $cmd
    if ($LASTEXITCODE -eq 0 -and ($out -match 'READY')) {
      Write-Host "/ready => OK (try $i/$MaxTries)" -ForegroundColor Green
      return
    }
    Start-Sleep -Seconds $DelaySeconds
  }
  Write-Warning "Backend /ready did not become healthy in time. Check remote logs:"
  Write-Warning "docker --context $ContextName compose -f $ComposeFile --env-file $EnvFile logs -f backend"
}

$repo = Get-RepoRoot
$envPath = Resolve-PathFromRepo $repo $EnvFile
$composePath = Resolve-PathFromRepo $repo $ComposeFile
$envMap = Read-DotEnv $envPath
$sourceRevision = Get-BackendSourceRevision -RepoRoot $repo
$doclingSourceRevision = Get-DoclingSidecarSourceRevision -RepoRoot $repo
$env:SAAIA_CODE_REVISION = $sourceRevision
$env:SAAIA_DOCLING_SOURCE_REVISION = $doclingSourceRevision

Require-Command 'ssh'
Require-Command 'scp'
Require-Command 'dotnet'
$useRemoteSourceBuild = $RemoteSourceBuild -or $null -eq (Get-Command docker -ErrorAction SilentlyContinue)
if ($useRemoteSourceBuild) {
  Require-Command 'tar'
}
else {
  Require-Command 'docker'
}

$serverUser = $Server
if ($Server -match '^(?<u>[^@]+)@(?<h>.+)$') {
  $serverUser = $Matches['u']
}

if (-not $envMap.ContainsKey('SAAIA_INSTALL_ROOT') -or [string]::IsNullOrWhiteSpace($envMap['SAAIA_INSTALL_ROOT'])) {
  throw "SAAIA_INSTALL_ROOT is required in $EnvFile for remote deploy."
}

$installRoot = ([string]$envMap['SAAIA_INSTALL_ROOT']).Trim()
Assert-SafeRemoteUserName $serverUser
$installRoot = Assert-SafeRemoteInstallPath -Label 'SAAIA_INSTALL_ROOT' -Path $installRoot

$deployDirName = if ($envMap.ContainsKey('SAAIA_DEPLOY_DIR') -and -not [string]::IsNullOrWhiteSpace($envMap['SAAIA_DEPLOY_DIR'])) {
  ([string]$envMap['SAAIA_DEPLOY_DIR']).Trim()
}
else {
  'deploy'
}

if ($deployDirName.StartsWith('/')) {
  $deployDirRemote = $deployDirName
}
else {
  $deployDirRemote = "$($installRoot.TrimEnd('/'))/$deployDirName"
}
$deployDirRemote = Assert-SafeRemoteInstallPath -Label 'SAAIA_DEPLOY_DIR' -Path $deployDirRemote
$backendPort = if ($envMap.ContainsKey('BACKEND_HOST_PORT') -and -not [string]::IsNullOrWhiteSpace($envMap['BACKEND_HOST_PORT'])) { [string]$envMap['BACKEND_HOST_PORT'] } else { '5122' }

Write-Host "== Remote deploy target ==" -ForegroundColor Cyan
Write-Host "Server:       $Server"
Write-Host "ContextName:  $ContextName"
Write-Host "InstallRoot:  $installRoot"
Write-Host "DeployDir:    $deployDirRemote"
Write-Host "ComposeFile:  $composePath"
Write-Host "EnvFile:      $envPath"
Write-Host "CodeRevision: $sourceRevision"
Write-Host "DoclingRev:   $doclingSourceRevision"

# Ensure remote runtime directories exist.
if (-not $SkipRemoteProvision) {
  $qUser = Quote-RemoteShellArg $serverUser
  $dirs = @(
    $installRoot,
    $deployDirRemote,
    "$installRoot/documents",
    "$installRoot/data/backend",
    "$installRoot/data/postgres",
    "$installRoot/data/qdrant",
    "$installRoot/cache/tei"
  )
  $mkdirCmd = ($dirs | ForEach-Object {
    $qPath = Quote-RemoteShellArg $_
    "sudo install -d -o $qUser -g $qUser -m 0755 $qPath"
  }) -join ' && '

  Write-Host "== Ensure remote runtime directories ==" -ForegroundColor Cyan
  & ssh $Server $mkdirCmd
  if ($LASTEXITCODE -ne 0) { throw "Remote directory provisioning failed ($LASTEXITCODE)" }
}
else {
  Write-Host "== Skip remote runtime directory provisioning ==" -ForegroundColor Yellow
}

# Optional: always (re)generate and sync signed config locally, but never copy the private key.
if (-not $SkipConfigSync) {
  Write-Host "== Generate + sign deployment config locally ==" -ForegroundColor Cyan

  Require $envMap @('POSTGRES_PASSWORD','SAAIA_AUTH_PEPPER','SAAIA_CONFIG_PRIVATE_KEY_PATH')
  $bootEnabled = $true
  if ($envMap.ContainsKey('SAAIA_BOOTSTRAP_ENABLED')) {
    $bootEnabled = Parse-Bool ([string]$envMap['SAAIA_BOOTSTRAP_ENABLED']) $true
  }
  if ($bootEnabled) { Require $envMap @('SAAIA_BOOTSTRAP_API_KEY') }

  $deployLocal = Resolve-PathFromRepo $repo $DeployWorkDirRel
  if (!(Test-Path $deployLocal)) { New-Item -ItemType Directory -Force -Path $deployLocal | Out-Null }

  $cfg = New-SignedConfig `
    -RepoRoot $repo `
    -env $envMap `
    -TemplateRel 'infra/config/deployment.config.prod.template.json' `
    -DeployDirRel $DeployWorkDirRel

  Write-Host "== Copy signed config to remote server ==" -ForegroundColor Cyan
  & scp $cfg.ConfigPath "${Server}:$deployDirRemote/deployment.config.json"
  if ($LASTEXITCODE -ne 0) { throw "scp deployment.config.json failed ($LASTEXITCODE)" }

  & scp $cfg.SigPath "${Server}:$deployDirRemote/deployment.config.sig"
  if ($LASTEXITCODE -ne 0) { throw "scp deployment.config.sig failed ($LASTEXITCODE)" }
}
else {
  Write-Host "== Skip config sync ==" -ForegroundColor Yellow
}

if ($useRemoteSourceBuild) {
  $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
  $remoteBuildDir = "$($installRoot.TrimEnd('/'))/deploy/backend-build-$timestamp"
  $remoteBuildDir = Assert-SafeRemoteInstallPath -Label 'RemoteBuildDir' -Path $remoteBuildDir
  $localArchiveDir = Resolve-PathFromRepo $repo $DeployWorkDirRel
  if (-not (Test-Path -LiteralPath $localArchiveDir)) {
    New-Item -ItemType Directory -Force -Path $localArchiveDir | Out-Null
  }
  $localArchive = Join-Path $localArchiveDir "backend-source-$timestamp.tar"

  Write-Host "== Package backend source for remote Docker build ==" -ForegroundColor Cyan
  & tar -cf $localArchive `
    --exclude='*/bin' `
    --exclude='*/bin-codex*' `
    --exclude='*/obj' `
    --exclude='*/obj-codex*' `
    --exclude='*/.vs' `
    --exclude='*.local.json' `
    --exclude='deployment.config.json' `
    --exclude='deployment.config.sig' `
    -C $repo `
    RAG.sln `
    global.json `
    .dockerignore `
    contracts/SAAIA.Contracts `
    backend/SAAIA.Backend `
    infra/docker-compose.prod.yml `
    infra/docling-sidecar
  if ($LASTEXITCODE -ne 0) {
    throw "Backend source archive failed ($LASTEXITCODE)."
  }

  try {
    $qBuildDir = Quote-RemoteShellArg $remoteBuildDir
    & ssh $Server "install -d -m 0755 $qBuildDir"
    if ($LASTEXITCODE -ne 0) { throw "Remote build directory creation failed ($LASTEXITCODE)." }

    & scp $localArchive "${Server}:$remoteBuildDir/backend-source.tar"
    if ($LASTEXITCODE -ne 0) { throw "Backend source upload failed ($LASTEXITCODE)." }
    & scp $envPath "${Server}:$remoteBuildDir/.env.server-linux"
    if ($LASTEXITCODE -ne 0) { throw "Remote environment upload failed ($LASTEXITCODE)." }

    $qRevision = Quote-RemoteShellArg $sourceRevision
    $qDoclingRevision = Quote-RemoteShellArg $doclingSourceRevision
    $extractCommand = "chmod 0600 $qBuildDir/.env.server-linux && tar -xf $qBuildDir/backend-source.tar -C $qBuildDir && rm -f $qBuildDir/backend-source.tar"
    & ssh $Server $extractCommand
    if ($LASTEXITCODE -ne 0) { throw "Remote source extraction failed ($LASTEXITCODE)." }

    $composeCommand = "SAAIA_CODE_REVISION=$qRevision SAAIA_DOCLING_SOURCE_REVISION=$qDoclingRevision docker compose -p infra -f $qBuildDir/infra/docker-compose.prod.yml --env-file $qBuildDir/.env.server-linux"
    if ($NoCache) {
      & ssh $Server "$composeCommand build --no-cache backend"
      if ($LASTEXITCODE -ne 0) { throw "Remote no-cache backend build failed ($LASTEXITCODE)." }
    }
    else {
      & ssh $Server "$composeCommand build backend"
      if ($LASTEXITCODE -ne 0) { throw "Remote backend build failed ($LASTEXITCODE)." }
    }

    $currentDoclingInspect = @(& ssh $Server "docker inspect infra-docling-1 2>/dev/null || true")
    $currentDoclingRevision = (
      Get-DoclingSourceRevisionFromInspect $currentDoclingInspect
    ).Trim()
    if (-not [string]::Equals($currentDoclingRevision, $doclingSourceRevision, [StringComparison]::Ordinal)) {
      Write-Host "== Build changed Docling sidecar ==" -ForegroundColor Cyan
      & ssh $Server "$composeCommand build docling"
      if ($LASTEXITCODE -ne 0) { throw "Remote Docling build failed ($LASTEXITCODE)." }
    }
    else {
      Write-Host "== Reuse unchanged Docling sidecar image ==" -ForegroundColor DarkGray
    }

    if ($WithDependencies) {
      & ssh $Server "$composeCommand up -d --no-build --wait --wait-timeout 300 backend postgres qdrant tei docling"
      if ($LASTEXITCODE -ne 0) { throw "Remote dependency deploy failed ($LASTEXITCODE)." }
    }
    else {
      & ssh $Server "$composeCommand up -d --no-build --no-deps --wait --wait-timeout 300 docling"
      if ($LASTEXITCODE -ne 0) { throw "Remote Docling deploy failed ($LASTEXITCODE)." }
      & ssh $Server "$composeCommand up -d --no-build --no-deps backend"
      if ($LASTEXITCODE -ne 0) { throw "Remote backend deploy failed ($LASTEXITCODE)." }
    }

    & ssh $Server "$composeCommand ps"
    if ($LASTEXITCODE -ne 0) { throw "Remote compose ps failed ($LASTEXITCODE)." }
  }
  finally {
    if (Test-Path -LiteralPath $localArchive -PathType Leaf) {
      $resolvedArchive = (Resolve-Path -LiteralPath $localArchive).Path
      $resolvedArchiveRoot = (Resolve-Path -LiteralPath $localArchiveDir).Path.TrimEnd('\', '/')
      if ($resolvedArchive.StartsWith($resolvedArchiveRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedArchive -Force
      }
    }
  }

  if (-not $SkipReadyCheck) {
    Wait-BackendReady -ServerSpec $Server -Port $backendPort
  }

  Write-Host ""
  Write-Host "Done (remote source build)." -ForegroundColor Green
  exit 0
}

Ensure-DockerContext -Name $ContextName -ServerSpec $Server

if ($NoCache) {
  Write-Host "== Remote rebuild backend (no cache) ==" -ForegroundColor Cyan
  $buildArgs = @('--context', $ContextName, 'compose', '-f', $composePath, '--env-file', $envPath, 'build', '--no-cache', 'backend')
  & docker @buildArgs
  if ($LASTEXITCODE -ne 0) { throw "Remote backend build failed ($LASTEXITCODE)" }
}
else {
  Write-Host "== Remote build backend ==" -ForegroundColor Cyan
  $buildArgs = @('--context', $ContextName, 'compose', '-f', $composePath, '--env-file', $envPath, 'build', 'backend')
  & docker @buildArgs
  if ($LASTEXITCODE -ne 0) { throw "Remote backend build failed ($LASTEXITCODE)" }
}

$currentDoclingInspect = @(& docker --context $ContextName inspect infra-docling-1 2>$null)
$currentDoclingRevision = (
  Get-DoclingSourceRevisionFromInspect $currentDoclingInspect
).Trim()
if (-not [string]::Equals($currentDoclingRevision, $doclingSourceRevision, [StringComparison]::Ordinal)) {
  Write-Host "== Build changed Docling sidecar ==" -ForegroundColor Cyan
  $doclingBuildArgs = @('--context', $ContextName, 'compose', '-f', $composePath, '--env-file', $envPath, 'build', 'docling')
  & docker @doclingBuildArgs
  if ($LASTEXITCODE -ne 0) { throw "Remote Docling build failed ($LASTEXITCODE)" }
}
else {
  Write-Host "== Reuse unchanged Docling sidecar image ==" -ForegroundColor DarkGray
}

Write-Host "== Remote deploy ==" -ForegroundColor Cyan

if ($WithDependencies) {
  $upArgs = @('--context', $ContextName, 'compose', '-f', $composePath, '--env-file', $envPath, 'up', '-d', '--no-build', '--wait', '--wait-timeout', '300', 'backend', 'postgres', 'qdrant', 'tei', 'docling')
  & docker @upArgs
  if ($LASTEXITCODE -ne 0) { throw "Remote dependency deploy failed ($LASTEXITCODE)" }
}
else {
  $doclingArgs = @('--context', $ContextName, 'compose', '-f', $composePath, '--env-file', $envPath, 'up', '-d', '--no-build', '--no-deps', '--wait', '--wait-timeout', '300', 'docling')
  & docker @doclingArgs
  if ($LASTEXITCODE -ne 0) { throw "Remote Docling deploy failed ($LASTEXITCODE)" }
  $upArgs = @('--context', $ContextName, 'compose', '-f', $composePath, '--env-file', $envPath, 'up', '-d', '--no-build', '--no-deps', 'backend')
  & docker @upArgs
  if ($LASTEXITCODE -ne 0) { throw "Remote backend deploy failed ($LASTEXITCODE)" }
}

Write-Host "== Remote compose status ==" -ForegroundColor Cyan
& docker --context $ContextName compose -f $composePath --env-file $envPath ps
if ($LASTEXITCODE -ne 0) { throw "Remote compose ps failed ($LASTEXITCODE)" }

if (-not $SkipReadyCheck) {
  Wait-BackendReady -ServerSpec $Server -Port $backendPort
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "If SAAIA_BIND_ADDR=127.0.0.1 on the server, you can tunnel the backend with:" -ForegroundColor Cyan
Write-Host "ssh -L $backendPort`:127.0.0.1`:$backendPort $Server"
