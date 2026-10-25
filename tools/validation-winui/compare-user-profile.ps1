#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselinePath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$ProfileRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'SAAIA')
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) { throw 'Select a new output file; evidence is not overwritten.' }
if (-not [IO.Path]::IsPathFullyQualified($ProfileRoot)) { throw 'Profile root must be absolute.' }
$root = [IO.Path]::GetFullPath($ProfileRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$prefix = $root + [IO.Path]::DirectorySeparatorChar
$outputFull = [IO.Path]::GetFullPath($OutputPath)
if ($outputFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must not be written inside the profile being inspected.' }
$baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
$baseline = @($baseline)
function Get-Hash([string]$Value) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value))).ToLowerInvariant()
}
function Get-SafeRelativePath([string]$Value) {
    if ([IO.Path]::IsPathFullyQualified($Value) -or [string]::IsNullOrWhiteSpace($Value)) { throw 'Baseline paths must be nonempty relative paths.' }
    $absolute = [IO.Path]::GetFullPath((Join-Path $root $Value))
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Baseline path escapes the expected profile root.' }
    return [IO.Path]::GetRelativePath($root, $absolute).Replace('\', '/').ToLowerInvariant()
}
$knownScopeLabels = @('client','governance','logs','llm-dev','support','exports','Models','llm','provisioning.json')
$before = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$scopes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $baseline) {
    $relative = Get-SafeRelativePath ([string]$entry.path)
    if ($entry.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $entry.size -lt 0) { throw 'Baseline fingerprint is invalid.' }
    if ($before.ContainsKey($relative)) { throw 'Baseline contains duplicate file paths.' }
    $before[$relative] = @{ size = [long]$entry.size; sha256 = ([string]$entry.sha256).ToLowerInvariant() }
    [void]$scopes.Add(($relative -split '/')[0])
}
$started = [DateTimeOffset]::UtcNow
$current = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$issues = [Collections.Generic.List[object]]::new()
$scopeProofs = [Collections.Generic.List[object]]::new()
foreach ($scope in $scopes) {
    $scopePath = [IO.Path]::GetFullPath((Join-Path $root $scope))
    if (-not $scopePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Scope escapes profile root.' }
    $scopeItem = Get-Item -LiteralPath $scopePath -Force -ErrorAction SilentlyContinue
    $files = @()
    if ($scopeItem) {
        if ($scopeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            $issues.Add(@{ fileId = Get-Hash $scope; reason = 'scope_reparse_point_not_followed' })
        } elseif ($scopeItem.PSIsContainer) {
            $enumerationErrors = @()
            $files = @(Get-ChildItem -LiteralPath $scopePath -Recurse -File -Force -ErrorAction SilentlyContinue -ErrorVariable enumerationErrors)
            if ($enumerationErrors.Count) { $issues.Add(@{ fileId = Get-Hash $scope; reason = 'scope_enumeration_failed'; count = $enumerationErrors.Count }) }
        } else { $files = @($scopeItem) }
    }
    foreach ($file in $files) {
        $relative = Get-SafeRelativePath ([IO.Path]::GetRelativePath($root, $file.FullName))
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            $issues.Add(@{ fileId = Get-Hash $relative; reason = 'file_reparse_point_not_followed' })
            continue
        }
        try {
            $lengthBefore = $file.Length
            $writeBefore = $file.LastWriteTimeUtc.Ticks
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $file.Refresh()
            if ($lengthBefore -ne $file.Length -or $writeBefore -ne $file.LastWriteTimeUtc.Ticks) {
                $issues.Add(@{ fileId = Get-Hash $relative; reason = 'file_changed_while_hashing' })
            }
            $current[$relative] = @{ size = [long]$file.Length; sha256 = $hash }
        } catch {
            $issues.Add(@{ fileId = Get-Hash $relative; reason = 'file_read_failed'; errorType = $_.Exception.GetType().Name })
        }
    }
    $scopePrefix = $scope + '/'
    $beforeCount = @($before.Keys | Where-Object { $_ -eq $scope -or $_.StartsWith($scopePrefix, [StringComparison]::OrdinalIgnoreCase) }).Count
    $currentCount = @($current.Keys | Where-Object { $_ -eq $scope -or $_.StartsWith($scopePrefix, [StringComparison]::OrdinalIgnoreCase) }).Count
    $label = if ($scope -in $knownScopeLabels) { $scope } else { 'unlisted_scope_' + (Get-Hash $scope).Substring(0, 12) }
    $scopeProofs.Add(@{ scope = $label; baselineFiles = $beforeCount; currentFiles = $currentCount })
}
$changes = [Collections.Generic.List[object]]::new()
$unchanged = 0
foreach ($relative in $before.Keys) {
    $previous = $before[$relative]
    if (-not $current.ContainsKey($relative)) {
        $changes.Add(@{ fileId = Get-Hash $relative; kind = 'missing_or_unreadable' })
    } elseif ($current[$relative].size -ne $previous.size -or $current[$relative].sha256 -ne $previous.sha256) {
        $changes.Add(@{ fileId = Get-Hash $relative; kind = 'changed'; baselineBytes = $previous.size; currentBytes = $current[$relative].size;
            baselineSha256 = $previous.sha256; currentSha256 = $current[$relative].sha256 })
    } else { $unchanged++ }
}
foreach ($relative in $current.Keys) {
    if (-not $before.ContainsKey($relative)) {
        $changes.Add(@{ fileId = Get-Hash $relative; kind = 'added'; currentBytes = $current[$relative].size; currentSha256 = $current[$relative].sha256 })
    }
}
$beforeSeal = @($before.Keys | Sort-Object | ForEach-Object { (Get-Hash $_) + '|' + $before[$_].size + '|' + $before[$_].sha256 }) -join "`n"
$currentSeal = @($current.Keys | Sort-Object | ForEach-Object { (Get-Hash $_) + '|' + $current[$_].size + '|' + $current[$_].sha256 }) -join "`n"
$proof = [ordered]@{
    schemaVersion = 'saaia-winui-user-profile-comparison.v1'; startedAtUtc = $started.ToString('o'); finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o');
    baselineInputSha256 = (Get-FileHash -LiteralPath $BaselinePath -Algorithm SHA256).Hash.ToLowerInvariant();
    profileRootId = Get-Hash $root.ToLowerInvariant(); readOnly = $true; contentPublished = $false; privatePathsPublished = $false;
    scopePolicy = 'Exactly the top-level scopes represented in the supplied baseline; no claim about unrecorded profile locations.';
    baselineFiles = $before.Count; currentFiles = $current.Count; unchangedFiles = $unchanged;
    changedOrMissingOrAddedFiles = $changes.Count; issueCount = $issues.Count;
    baselineTreeSha256 = Get-Hash $beforeSeal; currentTreeSha256 = Get-Hash $currentSeal;
    coveredFilesUnchanged = $changes.Count -eq 0 -and $issues.Count -eq 0;
    scopes = $scopeProofs.ToArray(); changes = $changes.ToArray(); issues = $issues.ToArray();
    limits = @('Hash comparison proves the covered files at observation time, not every application setting or Windows credential store.',
        'No process or profile file was stopped, changed, restored or deleted.', 'File timestamps are checked around hashing; the complete profile is not observed atomically.')
}
[IO.Directory]::CreateDirectory((Split-Path -Parent $outputFull)) | Out-Null
[IO.File]::WriteAllText($outputFull, ($proof | ConvertTo-Json -Depth 12))
Write-Output "Profile comparison saved: $([IO.Path]::GetFileName($outputFull)); coveredFiles=$($proof.baselineFiles), unchanged=$($proof.unchangedFiles), issues=$($proof.issueCount), preserved=$($proof.coveredFilesUnchanged)"
