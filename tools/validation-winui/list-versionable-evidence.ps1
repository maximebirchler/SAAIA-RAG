#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvidenceRoot, [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EvidenceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$destination = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $destination) { throw 'Choose a new selection file; evidence is not overwritten.' }
$privateFixture = Join-Path $root 'local-fixture-02/runtime/fixture-secrets.json'
$private = Get-Content -LiteralPath $privateFixture -Raw | ConvertFrom-Json
$knownSecrets = @([string]$private.apiKey,[string]$private.pgPassword)
if (@($knownSecrets | Where-Object { $_.Length -lt 20 }).Count) { throw 'Synthetic credential scanner input is invalid.' }
$candidates = [Collections.Generic.List[object]]::new()
$excludedCounts = @{}
$review = [Collections.Generic.List[object]]::new()
$patterns = [ordered]@{
    private_runtime_or_test_profile = '(^|/)(runtime|private|client-data|explicit-client-data)(/|$)'
    original_profile_inventory = '(^|/)user-profile-before\.json$'
    raw_process_or_model_manifest = '(^|/)(environment|local-model)\.json$'
    raw_model_or_backend_output = '(^|/)(local-model|backend|advanced-backend)\.(stdout|stderr)\.log$'
    excluded_native_capture = '^native-ui/(14[^/]*|16[^/]*|32[^/]*|45[^/]*)\.(png|json)$'
    private_pdf_or_explorer = '(?i)(^|/)(pdf-page[^/]*\.png|[^/]*explorer[^/]*)$'
}
$files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
    if ($file.FullName -eq $destination) { continue }
    $excludedReason = $null
    foreach ($pattern in $patterns.GetEnumerator()) {
        if ($relative -match $pattern.Value) { $excludedReason = $pattern.Key; break }
    }
    if ($excludedReason) {
        if (-not $excludedCounts.ContainsKey($excludedReason)) { $excludedCounts[$excludedReason] = 0 }
        $excludedCounts[$excludedReason]++
        continue
    }
    if ($file.Extension -notin @('.json','.jsonl','.md','.txt','.log','.trx','.png')) {
        $review.Add(@{ path=$relative; reason='unrecognized_format_not_selected' })
        continue
    }
    try {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        if ($file.Extension -ne '.png') {
            $text = [Text.Encoding]::UTF8.GetString($bytes)
            if (@($knownSecrets | Where-Object { $text.Contains($_) }).Count) {
                $review.Add(@{ path=$relative; reason='known_synthetic_credential_found_not_selected' })
                continue
            }
            if ($relative -like 'native-ui/*' -and $text -match '\.ts\.net|192\.168\.') {
                $review.Add(@{ path=$relative; reason='private_infrastructure_placeholder_not_selected' })
                continue
            }
        }
        $kind = if ($file.Extension -eq '.png') { 'native_visual_candidate_operator_review_required' } else { 'synthetic_text_proof_candidate' }
        $historical = $relative -match 'local-fixture-01/|advanced-worker-preflight\.json$|115629531Z-initial-durable\.json$|before-fix|trace-integrity-initial\.json$'
        $candidates.Add(@{ path=$relative; bytes=$bytes.Length; sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant();
            kind=$kind; historicalOrRejectedEvidence=$historical })
    } catch {
        $review.Add(@{ path=$relative; reason='file_unreadable_or_currently_locked_not_selected'; errorType=$_.Exception.GetType().Name })
    }
}
$result=[ordered]@{
    schemaVersion='saaia-winui-versionable-evidence-candidates.v1'; capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); readOnly=$true; stagingPerformed=$false;
    candidateCount=$candidates.Count; visualCandidateCount=@($candidates|Where-Object kind -eq 'native_visual_candidate_operator_review_required').Count;
    excludedCounts=$excludedCounts; excludedPatterns=$patterns; manualReviewOrUnreadable=$review.ToArray(); candidates=@($candidates|Sort-Object path);
    checks=@{knownSyntheticCredentialsChecked=$true;knownCredentialValuesPublished=$false;rawProfileInventoryExcluded=$true;privateRuntimeExcluded=$true};
    limits=@('This is an explicit candidate list, not a declaration that every image has been visually approved. The UI operator must confirm each selected screenshot.',
        'Credential scanning matches only the known synthetic fixture credentials; it is not proof about unknown secrets.',
        'Files were observed during ongoing UI/tests. Regenerate after the final freeze before sealing hashes.',
        'Historical failures and rejected proofs remain evidence of the path taken and must not be added to acceptance pass counts.')
}
[IO.Directory]::CreateDirectory((Split-Path -Parent $destination))|Out-Null
[IO.File]::WriteAllText($destination,($result|ConvertTo-Json -Depth 12))
Write-Output "Versionable candidates=$($result.candidateCount), visualCandidates=$($result.visualCandidateCount), manualReview=$($review.Count); no file staged."
