Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Escape-JsonString([string]$s) {
  if ($null -eq $s) { return '' }
  # Minimal JSON string escaping (no dependencies, PowerShell 5.1 friendly)
  $x = $s.Replace('\', '\\')
  $x = $x.Replace('"', '\"')
  $x = $x.Replace("`r", '')
  $x = $x.Replace("`n", '\n')
  return $x
}

function Get-RepoRoot {
  # Script root is .../infra/scripts/prod
  # Go up 3 levels: prod -> scripts -> infra -> repo root
  return (Resolve-Path (Join-Path $PSScriptRoot "..\..\.." )).Path
}

# Back-compat: older scripts used Resolve-RepoRoot
function Resolve-RepoRoot {
  return Get-RepoRoot
}

function Normalize-PathValue([string]$p) {
  if ([string]::IsNullOrWhiteSpace($p)) { return $p }
  $x = $p.Trim()
  # Strip surrounding quotes (common in .env when users copy/paste)
  if (($x.StartsWith('"') -and $x.EndsWith('"')) -or ($x.StartsWith("'") -and $x.EndsWith("'"))) {
    $x = $x.Substring(1, $x.Length-2)
  }
  return $x
}

function Resolve-PathFromRepo([string]$RepoRoot, [string]$maybeRelOrAbs) {
  $v = Normalize-PathValue $maybeRelOrAbs
  if ([string]::IsNullOrWhiteSpace($v)) { return $null }

  # If absolute/rooted: keep as-is
  if ([System.IO.Path]::IsPathRooted($v)) {
    return [System.IO.Path]::GetFullPath($v)
  }

  # Relative to repo root
  return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $v))
}

function Read-DotEnv {
  param(
    # Back-compat: some scripts call Read-DotEnv -Path ...
    [Parameter(Mandatory=$true)][Alias('Path')][string]$EnvPath
  )

  if ([string]::IsNullOrWhiteSpace($EnvPath)) {
    throw "Missing .env path (empty string)."
  }

  if (!(Test-Path $EnvPath)) { throw "Missing .env: $EnvPath" }

  $map = @{}
  Get-Content $EnvPath | ForEach-Object {
    $line = $_.Trim()
    if ($line -eq '' -or $line.StartsWith('#')) { return }
    $idx = $line.IndexOf('=')
    if ($idx -lt 1) { return }
    $k = $line.Substring(0, $idx).Trim()
    $v = $line.Substring($idx+1).Trim()
    $map[$k] = $v
  }
  return $map
}

function Require([hashtable]$env, [string[]]$keys) {
  $missing = @()
  foreach ($k in $keys) {
    if (-not $env.ContainsKey($k) -or [string]::IsNullOrWhiteSpace($env[$k])) {
      $missing += $k
    }
  }
  if ($missing.Count -gt 0) {
    throw "Missing required .env keys: $($missing -join ', ')"
  }
}

function Convert-ToDockerPath([string]$hostPath) {
  if ([string]::IsNullOrWhiteSpace($hostPath)) { return $hostPath }

  # Docker Desktop on Windows accepts C:/... (NOT C:\...)
  if ($env:OS -eq 'Windows_NT') {
    return ($hostPath -replace '\\','/')
  }
  return $hostPath
}

function Get-InstallRoot {
  param(
    [Parameter(Mandatory=$true)][string]$RepoRoot,
    [Parameter(Mandatory=$true)][hashtable]$Env
  )

  # Priority:
  # 1) process env var SAAIA_INSTALL_ROOT
  # 2) infra/.env key SAAIA_INSTALL_ROOT
  # 3) legacy: infra/.env key SAAIA_DEPLOY_DIR used as "install root" (when absolute and NOT ending with /deploy)
  # 4) default repo root
  $root = Normalize-PathValue $env:SAAIA_INSTALL_ROOT
  if ([string]::IsNullOrWhiteSpace($root) -and $Env.ContainsKey('SAAIA_INSTALL_ROOT')) {
    $root = Normalize-PathValue $Env['SAAIA_INSTALL_ROOT']
  }

  if ([string]::IsNullOrWhiteSpace($root) -and $Env.ContainsKey('SAAIA_DEPLOY_DIR')) {
    $legacy = Normalize-PathValue $Env['SAAIA_DEPLOY_DIR']
    if (-not [string]::IsNullOrWhiteSpace($legacy) -and [System.IO.Path]::IsPathRooted($legacy)) {
      $leaf = [System.IO.Path]::GetFileName($legacy.TrimEnd('\','/'))
      if ($leaf -and $leaf.ToLowerInvariant() -ne 'deploy') {
        $root = $legacy
      } else {
        # If user set ...\deploy, treat parent as install root
        $root = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($legacy))
      }
    }
  }

  if ([string]::IsNullOrWhiteSpace($root)) { $root = $RepoRoot }

  return [System.IO.Path]::GetFullPath($root)
}

function Get-DeployDir {
  param(
    [Parameter(Mandatory=$true)][string]$InstallRoot,
    [Parameter(Mandatory=$true)][hashtable]$Env
  )

  $v = $null
  if ($Env.ContainsKey('SAAIA_DEPLOY_DIR')) { $v = Normalize-PathValue $Env['SAAIA_DEPLOY_DIR'] }

  if ([string]::IsNullOrWhiteSpace($v)) {
    return [System.IO.Path]::GetFullPath((Join-Path $InstallRoot 'deploy'))
  }

  if ([System.IO.Path]::IsPathRooted($v)) {
    # If it's already a ".../deploy" folder, keep it.
    $leaf = [System.IO.Path]::GetFileName($v.TrimEnd('\','/'))
    if ($leaf -and $leaf.ToLowerInvariant() -eq 'deploy') {
      return [System.IO.Path]::GetFullPath($v)
    }

    # Otherwise interpret as an "install root" legacy value
    return [System.IO.Path]::GetFullPath((Join-Path $v 'deploy'))
  }

  # Relative: resolve under install root
  return [System.IO.Path]::GetFullPath((Join-Path $InstallRoot $v))
}

function Docker-Compose {
  param(
    # NOTE: Do NOT set Alias('RepoRoot') here. PowerShell treats it as a conflict
    # with the parameter name itself when the caller specifies -RepoRoot.
    [Parameter(Mandatory=$true)][Alias('Repo')][string]$RepoRoot,
    [Parameter(Mandatory=$true)][Alias('ComposeFileRel')][string]$ComposeFile,
    [Parameter(Mandatory=$true)][Alias('EnvFileRel')][string]$EnvFile,
    # NOTE: Do NOT set Alias('ComposeArgs') here (same conflict pattern as RepoRoot).
    [Parameter(Mandatory=$true)][Alias('Args')][string[]]$ComposeArgs
  )

  if ($null -eq $ComposeArgs -or $ComposeArgs.Count -eq 0) {
    throw "Internal: ComposeArgs is empty. You are likely running an older install.ps1/_common.ps1 pair. Overwrite both files from the patch."
  }

  $composePath = Resolve-PathFromRepo $RepoRoot $ComposeFile
  $envPath     = Resolve-PathFromRepo $RepoRoot $EnvFile

  $full = @('compose','-f', $composePath, '--env-file', $envPath) + $ComposeArgs
  Write-Host ('> docker ' + ($full -join ' '))
  & docker @full
  if ($LASTEXITCODE -ne 0) { throw "docker compose failed ($LASTEXITCODE)" }
}

function New-SignedConfig {
  param(
    [Parameter(Mandatory=$true)][string]$RepoRoot,
    [Parameter(Mandatory=$true)][hashtable]$env,
    [Parameter(Mandatory=$true)][string]$TemplateRel,
    [Parameter(Mandatory=$true)][string]$DeployDirRel
  )

  $templatePath = Resolve-PathFromRepo $RepoRoot $TemplateRel
  if (!(Test-Path $templatePath)) { throw "Missing template: $templatePath" }

  $deployFull = Resolve-PathFromRepo $RepoRoot $DeployDirRel
  if ([string]::IsNullOrWhiteSpace($deployFull)) {
    $deployFull = Resolve-PathFromRepo $RepoRoot 'deploy'
  }

  # Helpful debug when paths are wrong
  Write-Host "RepoRoot:   $RepoRoot"
  Write-Host "DeployDir:  $DeployDirRel"
  Write-Host "DeployFull: $deployFull"

  if (!(Test-Path $deployFull)) { New-Item -ItemType Directory -Force -Path $deployFull | Out-Null }

  $outCfg = Join-Path $deployFull 'deployment.config.json'
  $outSig = Join-Path $deployFull 'deployment.config.sig'

  function Parse-Bool([string]$raw, [bool]$default) {
    if ([string]::IsNullOrWhiteSpace($raw)) { return $default }
    $x = $raw.Trim().ToLowerInvariant()
    if ($x -in @('1','true','yes','y','on')) { return $true }
    if ($x -in @('0','false','no','n','off')) { return $false }
    return $default
  }

  $qdrantKeyPresent = ($env.ContainsKey('QDRANT_API_KEY') -and -not [string]::IsNullOrWhiteSpace($env['QDRANT_API_KEY']))
  $requireQdrantAuthBool = $null
  if ($env.ContainsKey('REQUIRE_QDRANT_AUTH_IN_PROD')) {
    $requireQdrantAuthBool = Parse-Bool $env['REQUIRE_QDRANT_AUTH_IN_PROD'] $qdrantKeyPresent
  }
  if ($null -eq $requireQdrantAuthBool) { $requireQdrantAuthBool = $qdrantKeyPresent }

  if ($requireQdrantAuthBool -and -not $qdrantKeyPresent) {
    throw "REQUIRE_QDRANT_AUTH_IN_PROD=true but QDRANT_API_KEY is empty. Set QDRANT_API_KEY in infra/.env."
  }

  if ($requireQdrantAuthBool) { $requireQdrantAuth = 'true' } else { $requireQdrantAuth = 'false' }

  $bootstrapEnabledBool = $true
  if ($env.ContainsKey('SAAIA_BOOTSTRAP_ENABLED')) {
    $bootstrapEnabledBool = Parse-Bool $env['SAAIA_BOOTSTRAP_ENABLED'] $true
  }
  if ($bootstrapEnabledBool) { $bootstrapEnabled = 'true' } else { $bootstrapEnabled = 'false' }

  $bootKey = ''
  if ($env.ContainsKey('SAAIA_BOOTSTRAP_API_KEY')) { $bootKey = $env['SAAIA_BOOTSTRAP_API_KEY'] }
  if ($bootstrapEnabledBool -and [string]::IsNullOrWhiteSpace($bootKey)) {
    throw "SAAIA_BOOTSTRAP_ENABLED=true but SAAIA_BOOTSTRAP_API_KEY is empty. Set SAAIA_BOOTSTRAP_API_KEY in infra/.env."
  }

  $pgDb = 'saaia'
  if ($env.ContainsKey('POSTGRES_DB') -and -not [string]::IsNullOrWhiteSpace($env['POSTGRES_DB'])) { $pgDb = $env['POSTGRES_DB'] }

  $pgUser = 'saaia'
  if ($env.ContainsKey('POSTGRES_USER') -and -not [string]::IsNullOrWhiteSpace($env['POSTGRES_USER'])) { $pgUser = $env['POSTGRES_USER'] }

  $teiModel = 'intfloat/multilingual-e5-base'
  if ($env.ContainsKey('TEI_MODEL_ID') -and -not [string]::IsNullOrWhiteSpace($env['TEI_MODEL_ID'])) { $teiModel = $env['TEI_MODEL_ID'] }

  $content = Get-Content $templatePath -Raw
  $content = $content.Replace('__AUTH_PEPPER__', (Escape-JsonString $env['SAAIA_AUTH_PEPPER']))
  $content = $content.Replace('__BOOTSTRAP_API_KEY__', (Escape-JsonString $bootKey))
  $content = $content.Replace('__BOOTSTRAP_ENABLED__', $bootstrapEnabled)
  $content = $content.Replace('__POSTGRES_PASSWORD__', (Escape-JsonString $env['POSTGRES_PASSWORD']))
  $content = $content.Replace('__POSTGRES_DB__', (Escape-JsonString $pgDb))
  $content = $content.Replace('__POSTGRES_USER__', (Escape-JsonString $pgUser))
  $content = $content.Replace('__TEI_MODEL_ID__', (Escape-JsonString $teiModel))
  $content = $content.Replace('__REQUIRE_QDRANT_AUTH__', $requireQdrantAuth)

  $content | Out-File -FilePath $outCfg -Encoding utf8

  # Sign
  $keyPath = Normalize-PathValue $env['SAAIA_CONFIG_PRIVATE_KEY_PATH']
  if (!(Test-Path $keyPath)) { throw "Private key file not found: $keyPath" }

  $proj = Join-Path $RepoRoot 'tools/ConfigSigner/ConfigSigner.csproj'
  Write-Host "> dotnet run --project $proj -- sign $outCfg @$keyPath $outSig"

  # IMPORTANT:
  # If the caller does `$cfg = New-SignedConfig`, any stdout from dotnet would normally
  # be captured into `$cfg` and pollute the return value.
  # Pipe to Out-Host so the output is displayed but NOT captured.
  & dotnet run --project $proj -- sign $outCfg "@$keyPath" $outSig | Out-Host

  if ($LASTEXITCODE -ne 0) { throw "ConfigSigner failed ($LASTEXITCODE)" }

  return [pscustomobject]@{ ConfigPath=$outCfg; SigPath=$outSig; DeployDir=$deployFull }
}
