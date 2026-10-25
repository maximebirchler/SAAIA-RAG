#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$BundlePath,
    [string]$ProofName='support-bundle-review.json'
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
$owner=Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw|ConvertFrom-Json
$isolation=Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw|ConvertFrom-Json
if($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output){throw 'Not an owned fixture.'}
if([IO.Path]::GetFileName($ProofName) -ne $ProofName){throw 'ProofName must be a filename.'}
$proofPath=Join-Path $output $ProofName
if(Test-Path -LiteralPath $proofPath){throw 'Support proof already exists; refusing overwrite.'}
$bundle=[IO.Path]::GetFullPath($BundlePath)
$supportPrefix=[IO.Path]::GetFullPath((Join-Path $owner.clientDataRoot 'support'))+[IO.Path]::DirectorySeparatorChar
if(-not $bundle.StartsWith($supportPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Bundle is outside the owned isolated support root.'}
$private=Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
$secure=Get-Content -LiteralPath (Join-Path $owner.clientDataRoot 'client/secure.json') -Raw|ConvertFrom-Json
$needles=@([string]$private.apiKey,[string]$private.pgPassword)+@($secure.PSObject.Properties|Where-Object{$_.Name -match 'Protected' -and $_.Value}|ForEach-Object{[string]$_.Value})
$needleHits=[Collections.Generic.List[string]]::new()
$entryText=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
$entryBytes=[Collections.Generic.Dictionary[string,byte[]]]::new([StringComparer]::Ordinal)
$archive=[IO.Compression.ZipFile]::OpenRead($bundle)
try{
    foreach($entry in $archive.Entries){
        if($entry.Length -gt 20MB){throw 'Bundle entry exceeds inspection bound.'}
        if($entry.FullName.StartsWith('/') -or $entry.FullName -match '(^|/)\.\.(/|$)'){throw 'Unexpected archive entry path.'}
        $stream=$entry.Open();$memory=[IO.MemoryStream]::new()
        try{$stream.CopyTo($memory);$bytes=$memory.ToArray()}finally{$stream.Dispose();$memory.Dispose()}
        $text=[Text.Encoding]::UTF8.GetString($bytes)
        $entryBytes[$entry.FullName]=$bytes;$entryText[$entry.FullName]=$text
        if(@($needles|Where-Object{$_.Length -gt 0 -and $text.Contains($_)}).Count){$needleHits.Add($entry.FullName)}
    }
}finally{$archive.Dispose()}
function Read-Entry([string]$Name){
    if(-not $entryText.ContainsKey($Name)){throw 'Required support piece is absent.'}
    return ConvertFrom-Json -InputObject $entryText[$Name] -Depth 64
}
$manifest=Read-Entry 'collection-manifest.json'
$history=Read-Entry 'diagnostics/client-turns.json'
$settings=Read-Entry 'settings.json'
$runtime=Read-Entry 'runtime.json'
$models=Read-Entry 'llm_models.json'
$ready=Read-Entry 'backend_ready.json'
$manifestNames=@($manifest.files|Sort-Object)
$archiveNames=@($entryText.Keys|Where-Object{$_ -ne 'collection-manifest.json'}|Sort-Object)
$manifestNamesMatch=(@(Compare-Object $manifestNames $archiveNames).Count -eq 0)
$sidecarFailures=[Collections.Generic.List[string]]::new()
$sidecarContracts=[Collections.Generic.List[object]]::new()
foreach($name in $entryText.Keys|Where-Object{$_.EndsWith('.sha256',[StringComparison]::OrdinalIgnoreCase)}){
    $source=$name.Substring(0,$name.Length-7)
    if(-not $entryBytes.ContainsKey($source)){$sidecarFailures.Add($name);continue}
    $isRedactedExport=$entryText.ContainsKey($name+'.source')
    # GovernanceArtifactStore hashes UTF8 text; File.WriteAllText(Encoding.UTF8)
    # can add a BOM. Redacted export sidecars explicitly switch to file bytes.
    $checksumBytes=$entryBytes[$source]
    if(-not $isRedactedExport){
        $governanceText=$entryText[$source]
        if($governanceText.Length -gt 0 -and $governanceText[0] -eq [char]0xFEFF){$governanceText=$governanceText.Substring(1)}
        $checksumBytes=[Text.Encoding]::UTF8.GetBytes($governanceText)
    }
    $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($checksumBytes)).ToLowerInvariant()
    if($entryText[$name].Trim().ToLowerInvariant() -ne $digest){$sidecarFailures.Add($name)}
    $sidecarContracts.Add(@{path=$name;contract=if($isRedactedExport){'redacted_export_file_bytes'}else{'governance_utf8_text_without_bom'}})
}
$probeRows=[Collections.Generic.List[object]]::new()
$invalidProbeLines=0;$legacyLogLines=0
if($entryText.ContainsKey('logs/client_startup.log')){
    foreach($line in $entryText['logs/client_startup.log'] -split '\r?\n'){
        if(-not $line.Trim() -or $line.StartsWith('[REDACTED]')){continue}
        $offset=$line.IndexOf('CLIENT_PROBE ',[StringComparison]::Ordinal)
        if($offset -lt 0){$legacyLogLines++;continue}
        try{$probeRows.Add((ConvertFrom-Json $line.Substring($offset+13) -Depth 32))}catch{$invalidProbeLines++}
    }
}
# Inspect synthetic corpus text and chat content only in memory; never copy it to the proof.
$textNeedles=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$context=Get-Content -LiteralPath (Join-Path $output 'context-page2-preflight.json') -Raw|ConvertFrom-Json
foreach($item in $context.items){if(([string]$item.text).Length -ge 20){[void]$textNeedles.Add([string]$item.text)}}
[void]$textNeedles.Add('Code de validation ORION : 4826')
$headers=@{'X-Api-Key'=$private.apiKey}
$userId=[Uri]::EscapeDataString([string]$isolation.userId)
$sessions=Invoke-RestMethod -Uri ($owner.backendUrl+'/chat/sessions?userId='+$userId+'&limit=1000') -Headers $headers -TimeoutSec 20
foreach($session in $sessions){
    $messages=Invoke-RestMethod -Uri ($owner.backendUrl+'/chat/sessions/'+([Guid]$session.sessionId).ToString('D')+'/messages?userId='+$userId+'&limit=1000') -Headers $headers -TimeoutSec 20
    foreach($message in $messages){if(([string]$message.content).Length -ge 20){[void]$textNeedles.Add([string]$message.content)}}
}
$contentHitEntries=[Collections.Generic.List[string]]::new()
foreach($name in $entryText.Keys){if(@($textNeedles|Where-Object{$entryText[$name].Contains($_)}).Count){$contentHitEntries.Add($name)}}
$absolutePaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Collect-AbsolutePaths([AllowNull()][object]$Value){
    if($Value -is [string]){if($Value -match '^[A-Za-z]:[\\/]'){[void]$absolutePaths.Add($Value)};return}
    if($Value -is [System.Management.Automation.PSCustomObject]){foreach($property in $Value.PSObject.Properties){Collect-AbsolutePaths $property.Value};return}
    if($Value -is [System.Collections.IEnumerable]){foreach($item in $Value){Collect-AbsolutePaths $item}}
}
foreach($name in $entryText.Keys|Where-Object{$_ -like '*.json'}){try{Collect-AbsolutePaths (ConvertFrom-Json $entryText[$name] -Depth 64)}catch{}}
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$binaryPrefix=[IO.Path]::GetFullPath((Join-Path $repo 'client/SAAIA.Client.WinUI/bin'))+[IO.Path]::DirectorySeparatorChar
$readOnlyBinaryPaths=@($absolutePaths|Where-Object{$_ -eq $runtime.appBaseDir -and [IO.Path]::GetFullPath($_).StartsWith($binaryPrefix,[StringComparison]::OrdinalIgnoreCase)})
$outsideRootPaths=@($absolutePaths|Where-Object{$_ -notin $readOnlyBinaryPaths -and -not [IO.Path]::GetFullPath($_).StartsWith([IO.Path]::GetFullPath($owner.clientDataRoot)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFullPath($_) -ine [IO.Path]::GetFullPath($owner.clientDataRoot)})
$modelUri=[Uri]$models.url
$expectedModelsUrl='http://'+$settings.Host+':'+$settings.Port+'/v1/models'
$failedPieces=@($manifest.pieces|Where-Object Status -eq 'failed')
$unavailable=@($manifest.pieces|Where-Object Status -eq 'unavailable'|ForEach-Object{@{path=$_.Path;reason=$_.Reason}})
$checks=@(
    @{name='bundle_under_owned_isolated_root';passed=$true},
    @{name='collection_manifest_matches_zip_entries';passed=$manifestNamesMatch},
    @{name='checksum_sidecars_follow_governance_or_redacted_export_contract';passed=$sidecarFailures.Count -eq 0},
    @{name='secure_store_not_exported';passed=@($entryText.Keys|Where-Object{[IO.Path]::GetFileName($_) -eq 'secure.json'}).Count -eq 0},
    @{name='known_plaintext_and_dpapi_values_absent';passed=$needleHits.Count -eq 0},
    @{name='metadata_only_history';passed=$history.contentMode -eq 'metadata_only' -and $manifest.contentMode -eq 'metadata_only'},
    @{name='no_collector_or_log_failures';passed=$history.collectorFailures -eq 0 -and $history.logWriteFailures -eq 0 -and $manifest.collectorFailures -eq 0},
    @{name='collection_complete_no_failed_pieces';passed=$manifest.collectionComplete -eq $true -and $failedPieces.Count -eq 0},
    @{name='raw_chat_and_source_text_absent';passed=$contentHitEntries.Count -eq 0},
    @{name='legacy_log_content_excluded';passed=$legacyLogLines -eq 0},
    @{name='production_probes_present_and_parseable';passed=$probeRows.Count -gt 0 -and $invalidProbeLines -eq 0 -and @($history.turns).Count -gt 0},
    @{name='llm_catalog_url_includes_v1_models';passed=$models.url -eq $expectedModelsUrl -and $modelUri.IsLoopback -and $modelUri.AbsolutePath -eq '/v1/models' -and $models.ok -eq $true -and $models.status -eq 200},
    @{name='backend_readiness_is_loopback_success';passed=([Uri]$ready.url).IsLoopback -and $ready.ok -eq $true -and $ready.status -eq 200},
    @{name='absolute_data_paths_remain_isolated';passed=$outsideRootPaths.Count -eq 0}
)
$passed=@($checks|Where-Object{-not $_.passed}).Count -eq 0
$proof=[ordered]@{
    schemaVersion='saaia-winui-support-bundle-review.v1';checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');passed=$passed;readOnly=$true;
    bundleSha256=(Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash;zipBytes=(Get-Item -LiteralPath $bundle).Length;
    entries=$entryText.Count;contentMode=$history.contentMode;probeImplementation=$manifest.probeImplementation;
    collectionComplete=$manifest.collectionComplete;failedPieceCount=$failedPieces.Count;optionalUnavailablePieces=$unavailable;
    turnCount=@($history.turns).Count;productionProbeEvents=$probeRows.Count;legacyLogLines=$legacyLogLines;invalidProbeLines=$invalidProbeLines;
    knownCredentialNeedlesChecked=$needles.Count;credentialHitCount=$needleHits.Count;credentialValuesPublished=$false;
    syntheticTextNeedlesChecked=$textNeedles.Count;contentHitCount=$contentHitEntries.Count;contentHitEntries=$contentHitEntries.ToArray();
    checksumSidecarFailures=$sidecarFailures.ToArray();checksumSidecarContracts=$sidecarContracts.ToArray();absolutePathCount=$absolutePaths.Count;readOnlyApplicationBinaryPathCount=$readOnlyBinaryPaths.Count;unexpectedOutsideIsolatedRootPathCount=$outsideRootPaths.Count;
    llmModelsUrl=$models.url;backendReadinessUrl=$ready.url;checks=$checks;
    rawBundleSelectedForVersioning=$false;limits=@('Exact known synthetic credential/DPAPI values and available synthetic text were checked in memory; no private profile or paid provider was accessed.',
      'Probe/history presence and redaction do not by themselves prove every UI scenario or source page rendering.',
      'Optional unavailable pieces remain declared; they do not mean the collect operation failed.');productStatus='TESTE_NON_APPROUVE'
}
[IO.File]::WriteAllText($proofPath,($proof|ConvertTo-Json -Depth 16))
if(-not $passed){throw 'Support bundle inspection has failed checks; inspect compact evidence.'}
Write-Output 'Support bundle review passed: metadata-only, credentials/text excluded, probes/checksums intact, isolated data paths and /v1/models correct.'
