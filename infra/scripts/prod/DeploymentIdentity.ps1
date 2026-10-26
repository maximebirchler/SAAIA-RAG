# Pure source/runtime identity helpers. No Git, process launch or network access.
function Get-EffectiveDeploymentValue([hashtable]$Values, [string]$Name, [string]$Default='') {
  # Docker Compose gives process variables priority over its --env-file.
  $processValue = [Environment]::GetEnvironmentVariable($Name, 'Process')
  if (-not [string]::IsNullOrWhiteSpace($processValue)) { return $processValue.Trim() }
  if ($Values.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace([string]$Values[$Name])) {
    return (Normalize-PathValue ([string]$Values[$Name])).Trim()
  }
  return $Default
}

function Get-ComposeDefaultImage([string]$RepoRoot, [string]$ImageVariable) {
  $compose = Get-Content -LiteralPath (Join-Path $RepoRoot 'infra/docker-compose.prod.yml') -Raw
  $match = [regex]::Match($compose, '\$\{' + [regex]::Escape($ImageVariable) + ':-([^}]+)\}')
  if (-not $match.Success) { throw "Missing Compose default for $ImageVariable." }
  return $match.Groups[1].Value
}

function Resolve-DeploymentRuntimeRevision([hashtable]$Values, [string]$ImageVariable, [string]$RevisionVariable, [string]$DefaultImage) {
  $image = Get-EffectiveDeploymentValue $Values $ImageVariable $DefaultImage
  $revision = Get-EffectiveDeploymentValue $Values $RevisionVariable
  $digest = [regex]::Match($image, '@(sha256:[0-9a-fA-F]{64})$')
  if ($digest.Success) {
    $imageRevision = $digest.Groups[1].Value.ToLowerInvariant()
    if ($revision -and $revision.ToLowerInvariant() -cne $imageRevision) {
      throw "$RevisionVariable does not match the digest of $ImageVariable."
    }
    return $imageRevision
  }
  # A mutable image cannot inherit the unrelated historical default identity.
  # This explicit value is a declaration; actual image attestation is a separate gate.
  if ($revision -notmatch '^sha256:[0-9a-fA-F]{64}$') {
    throw "$RevisionVariable must explicitly identify the observed image digest when $ImageVariable is not digest-pinned."
  }
  return $revision.ToLowerInvariant()
}

function Get-DeploymentSourceFiles([string]$RepoRoot, [ValidateSet('backend','docling')][string]$Kind='backend') {
  $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\','/')
  $paths = [Collections.Generic.List[string]]::new()
  $required = if ($Kind -eq 'backend') { @('RAG.sln','global.json','Directory.Build.props','.dockerignore','backend/SAAIA.Backend/Dockerfile') } else { @('infra/docling-sidecar/Dockerfile','infra/docling-sidecar/entrypoint.sh') }
  foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) { throw "Missing build input: $relative" }
    $paths.Add($relative)
  }
  $trees = if ($Kind -eq 'backend') { @('backend/SAAIA.Backend','contracts/SAAIA.Contracts') } else { @('infra/docling-sidecar/saaia_docling') }
  foreach ($tree in $trees) {
    Get-ChildItem -LiteralPath (Join-Path $root $tree) -File -Recurse | ForEach-Object {
      $relative = $_.FullName.Substring($root.Length+1).Replace('\','/')
      # Match the private/generated exclusions of the root Docker build context.
      if ($relative -match '(^|/)(bin|obj|bin-codex[^/]*|obj-codex[^/]*|\.vs|\.idea|keys|__pycache__)(/|$)' -or
          $_.Name -match '^(\.env($|\.)|appsettings\.Local\.json$|appsettings\..*\.local\.json$|deployment\.config\.(json|sig)$|private.*\.b64$)' -or
          $_.Name -match '\.(user|suo|log|tmp|bak|zip|7z|tgz|pdf|gguf|bin|safetensors|pem|key|crt|cer|pfx|p8|p12|pyc)$' -or
          $_.Name -match '\.tar\.gz$') { return }
      if (-not $paths.Contains($relative)) { $paths.Add($relative) }
    }
  }
  $ordered = $paths.ToArray()
  [Array]::Sort($ordered, [StringComparer]::Ordinal)
  return $ordered
}

function Get-DeploymentSourceRevision([string]$RepoRoot, [ValidateSet('backend','docling')][string]$Kind='backend') {
  $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\','/')
  $lines = foreach ($relative in (Get-DeploymentSourceFiles -RepoRoot $root -Kind $Kind)) {
    $file = Get-Item -LiteralPath (Join-Path $root $relative)
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $length = $file.Length.ToString([Globalization.CultureInfo]::InvariantCulture)
    "$relative|$length|$hash"
  }
  $sha = [Security.Cryptography.SHA256]::Create()
  try { return 'source-sha256:' + (-join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))) | ForEach-Object { $_.ToString('x2') })) }
  finally { $sha.Dispose() }
}

function Get-BackendArchiveSourceFiles([string]$RepoRoot) {
  $selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
  foreach ($kind in @('backend','docling')) {
    foreach ($relative in (Get-DeploymentSourceFiles -RepoRoot $RepoRoot -Kind $kind)) { [void]$selected.Add($relative) }
  }
  $compose = 'infra/docker-compose.prod.yml'
  if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $compose) -PathType Leaf)) { throw 'Missing build Compose file.' }
  [void]$selected.Add($compose)
  [string[]]$ordered = @($selected)
  [Array]::Sort($ordered, [StringComparer]::Ordinal)
  return $ordered
}

function New-BackendSourceArchive([string]$RepoRoot, [string]$ArchivePath) {
  $files = @(Get-BackendArchiveSourceFiles -RepoRoot $RepoRoot)
  # NUL-separated explicit file inputs avoid recursive inclusion and native
  # Windows command-line limits. No excluded file's contents are read or archived.
  $listPath = [IO.Path]::GetFullPath($ArchivePath) + '.inputs-' + [guid]::NewGuid().ToString('N')
  $stream = [IO.File]::Open($listPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
  try {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($files -join [char]0) + [char]0)
    $stream.Write($bytes, 0, $bytes.Length)
  } finally { $stream.Dispose() }
  try {
    & tar -cf $ArchivePath -C $RepoRoot --null -T $listPath
    if ($LASTEXITCODE -ne 0) { throw "Backend source archive failed ($LASTEXITCODE)." }
  } finally {
    # This single fresh file was created by this function; never recursive cleanup.
    Remove-Item -LiteralPath $listPath -Force
  }
}
