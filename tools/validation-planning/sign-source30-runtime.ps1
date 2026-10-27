#requires -Version 7.4
param([switch]$Execute,[string]$ExpectedConfigurationSha256='')
if (-not $Execute) {
    [ordered]@{ executed = $false; inputFilesRead = $false; privateKeyRead = $false; signatureCreated = $false } | ConvertTo-Json -Compress
    return
}
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$capture = Join-Path $repo 'artifacts/meal-live-20261027/private/pc-signing-source30-v1'
$config = Join-Path $capture 'runtime/deployment.config.json'
$signature = Join-Path $capture 'runtime/deployment.config.sig'
$receipt = Join-Path $repo 'artifacts/meal-live-20261027/pc-signing-admission-source30-v1.json'
if ((Test-Path -LiteralPath $signature) -or (Test-Path -LiteralPath $receipt)) { throw 'Fresh signature and receipt required.' }
$prep = Get-Content -Encoding UTF8 -LiteralPath (Join-Path $capture 'runtime/owner.json') -Raw | ConvertFrom-Json
if ($prep.schemaVersion -cne 'saaia-linux-ingestion-runtime.v1' -or $prep.state -cne 'config_prepared_waiting_signature' -or
    $prep.qualificationMode -cne 'production' -or @($prep.containers).Count -ne 0) { throw 'Exact newly prepared unsigned namespace required.' }
$configSha = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ExpectedConfigurationSha256 -cnotmatch '^[a-f0-9]{64}$' -or $configSha -cne $ExpectedConfigurationSha256 -or
    $configSha -ne $prep.bootstrapPreparation.files.'deploy/deployment.config.json' -or $prep.runId -ne 'dfd06e7d-e93a-4b47-8b43-3b7fb58562c6') { throw 'Prepared config identity mismatch.' }
$owner = $prep
if ($owner.root -cne '/home/maxime/saaia-ingestion-qualification/dfd06e7d-e93a-4b47-8b43-3b7fb58562c6' -or
    $owner.runId -cne $prep.runId -or @($owner.containers).Count -ne 0 -or
    $owner.bootstrapPreparation.files.'deploy/deployment.config.json' -cne $configSha -or
    $owner.bootstrapPreparation.backendSourceRevision -cne 'ab23094c021de13361be19692964e56786bd4bee77534188a8ddca2728c78b30' -or
    $owner.bootstrapPreparation.doclingSourceRevision -cne '8ddd79a4226cfcb9f678414a76302a1dc8ab614a4267b237c1cd3e26d62218c0') { throw 'New owner or split backend/Docling identity mismatch.' }
if ($owner.fullDocumentTimeoutPolicy.backendFullDocumentTimeoutSeconds -ne 3600 -or
    $owner.fullDocumentTimeoutPolicy.doclingUpstreamTimeoutSeconds -ne 3900 -or
    $owner.fullDocumentTimeoutPolicy.doclingMaximumSyncWaitSeconds -ne 3900 -or
    $owner.fullDocumentTimeoutPolicy.profile -cne 'cpu28_3cores_7GiB_document3600_proxy3900') { throw 'Reviewed source30 full-document timeout policy required.' }
$value = Get-Content -Encoding UTF8 -LiteralPath $config -Raw | ConvertFrom-Json
if ($value.DocumentIntelligence.RegionalTableAlternativeReadingsEnabled -isnot [bool] -or $value.DocumentIntelligence.RegionalTableAlternativeReadingsEnabled -ne $true) { throw 'Signed secondary activation required.' }
if ($value.DocumentIntelligence.DoclingInputGeometryCaptureEnabled -isnot [bool] -or $value.DocumentIntelligence.DoclingInputGeometryCaptureEnabled -ne $true) { throw 'Signed input geometry capture activation required.' }
if ($value.DocumentIntelligence.NativeTypographyObservationsEnabled -isnot [bool] -or $value.DocumentIntelligence.NativeTypographyObservationsEnabled -ne $true) { throw 'Signed native typography activation required.' }
if ($value.AdvancedAnalysis.VisualSourceCitationsEnabled -isnot [bool] -or $value.AdvancedAnalysis.VisualSourceCitationsEnabled -ne $true) { throw 'Signed visual citation activation required.' }
if ($value.AdvancedAnalysis.NativeObservationReadEnabled -isnot [bool] -or $value.AdvancedAnalysis.NativeObservationReadEnabled -ne $true) { throw 'Signed native observation read activation required.' }
$disabledFlags = @(
    $value.AdvancedAnalysis.WorkerEnabled
    $value.AdvancedAnalysis.AllowExternalProviderContent
    $value.AdvancedAnalysis.AllowExternalProviderMetadata
    $value.License.AdvancedAnalysisEnabled
)
if (@($disabledFlags | Where-Object { $_ -isnot [bool] -or $_ -ne $false }).Count -ne 0 -or $value.AdvancedAnalysis.Provider -cne 'disabled') {
    throw 'LLM and external transmission must remain strictly disabled.'
}
if (($value.DocumentIntelligence.NumThreads -isnot [long] -and $value.DocumentIntelligence.NumThreads -isnot [int]) -or $value.DocumentIntelligence.NumThreads -ne 3 -or $value.DocumentIntelligence.Device -cne 'cpu') {
    throw 'CPU28 signed NumThreads=3 and Device=cpu required.'
}
if ($value.DocumentIntelligence.RegionalTextRepairEnabled -isnot [bool] -or $value.DocumentIntelligence.RegionalTextRepairEnabled -ne $true -or
    $value.DocumentIntelligence.DeploymentRevision -cne '8ddd79a4226cfcb9f678414a76302a1dc8ab614a4267b237c1cd3e26d62218c0' -or
    $value.RuntimeGovernance.CapabilityBWorkerEnabled -isnot [bool] -or $value.RuntimeGovernance.CapabilityBWorkerEnabled -ne $false -or
    $value.Ingestion.WorkerEnabled -isnot [bool] -or $value.Ingestion.WorkerEnabled -ne $true -or
    $value.Ingestion.CanonicalArtifactsEnabled -isnot [bool] -or $value.Ingestion.CanonicalArtifactsEnabled -ne $true -or
    $value.Ingestion.WatcherEnabled -isnot [bool] -or $value.Ingestion.WatcherEnabled -ne $false -or
    $value.Ingestion.ScannerEnabled -isnot [bool] -or $value.Ingestion.ScannerEnabled -ne $false -or
    $value.Rag.QdrantCollection -cne 'ingestion_dfd06e7de93a4b478b433b7fb58562c6') { throw 'Real canonical/manual ingestion and regional CPU settings required.' }
if (($value.DocumentIntelligence.TimeoutSeconds -isnot [long] -and $value.DocumentIntelligence.TimeoutSeconds -isnot [int]) -or
    $value.DocumentIntelligence.TimeoutSeconds -ne 3600) { throw 'Signed source30 document timeout=3600 required.' }
$proofPath = Join-Path $capture 'build/backend-build-proof-v9.json'
$proofSha = (Get-FileHash -LiteralPath $proofPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($proofSha -ne '6b5611d20b016b81b660b99abe23a2e2bf5c051c23aa97a5fa06dcef565385d8') { throw 'Prepared build proof mismatch.' }
$proof = Get-Content -Encoding UTF8 -LiteralPath $proofPath -Raw | ConvertFrom-Json
if ($proof.backend.sourceIdentity -cne $owner.bootstrapPreparation.backendSourceRevision -or
    $proof.backend.imageId -cne 'sha256:237f5221bb76a0793270169b68b8bbb318b67eebf2b14ffee81efae2810f236b' -or
    $proof.backendDllSha256 -cne '217c7d34053992118b931142c992f5eb376aa2fbd34b6d33fed9b1cbfa890dcf' -or
    $proof.docling.sourceIdentity -cne $owner.bootstrapPreparation.doclingSourceRevision -or
    $proof.docling.imageId -cne 'sha256:0d9c8608a72f3dfb2f19a58583c7a118767d5f203bdd2e225bb6a87b4d6f6c5f') { throw 'Reviewed backend and reused Docling identities mismatch.' }
$signer = Join-Path $repo 'tools/ConfigSigner/bin/Release/net8.0/ConfigSigner.dll'
$keyFile = 'C:/Users/maxim/Desktop/ecom/AAA/Keys/saaia_config_private.b64'
if (-not (Test-Path -LiteralPath $keyFile -PathType Leaf)) { throw 'Authorized PC signing key missing.' }
$log = Join-Path $capture 'signing.private.log'
& dotnet $signer sign $config ('@' + $keyFile) $signature *> $log
$signExit = $LASTEXITCODE
if ($signExit -ne 0) { throw 'PC signing failed; inspect private log.' }
& dotnet $signer verify $config ('@' + $signature) '8LNTQpr23+P3YfN5RHz21zD1d75GOqc/f17vAipLTZ0=' *>> $log
$verifyExit = $LASTEXITCODE
if ($verifyExit -ne 0) { throw 'PC signature verification failed.' }
if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash.ToLowerInvariant() -ne $configSha) { throw 'Config changed while signing.' }
$result = [ordered]@{
    recordedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    runId = $prep.runId
    configurationSha256 = $configSha
    backendBuildProofSha256 = $proofSha
    sourceIdentity = $proof.backend.sourceIdentity
    doclingSourceIdentity = $proof.docling.sourceIdentity
    doclingImageReused = $true
    fullDocumentTimeoutSeconds = 3600
    doclingUpstreamTimeoutSeconds = 3900
    doclingMaximumSyncWaitSeconds = 3900
    secondaryEnabled = $true
    nativeTypographyEnabled = $true
    visualSourceCitationsEnabled = $true
    nativeObservationReadEnabled = $true
    doclingInputGeometryCaptureEnabled = $true
    imageToolAvailable = $false
    modelPixelsProvided = $false
    humanViewerAvailable = $null
    signerAssemblySha256 = (Get-FileHash -LiteralPath $signer -Algorithm SHA256).Hash.ToLowerInvariant()
    signatureSha256 = (Get-FileHash -LiteralPath $signature -Algorithm SHA256).Hash.ToLowerInvariant()
    signExitCode = $signExit
    verifyExitCode = $verifyExit
    privateKeyTransferredToServer = $false
    runtimeExecutedByThisScript = $false
    sourceFidelityApproved = $false
}
$result | ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding utf8
$result | ConvertTo-Json -Compress
