#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Canonical','HashMismatch','MissingPath')][string]$Scenario='Canonical',
    [string]$ProofName=''
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
$owner=Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw|ConvertFrom-Json
$isolation=Get-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Raw|ConvertFrom-Json
if($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output){throw 'Not an owned fixture.'}
if(-not $ProofName){$ProofName=switch($Scenario){'Canonical'{'source-card-session-fixture.json'};'HashMismatch'{'source-card-hash-mismatch-fixture.json'};'MissingPath'{'source-card-missing-path-fixture.json'}}}
if([IO.Path]::GetFileName($ProofName) -ne $ProofName){throw 'ProofName must be a filename.'}
$proofPath=Join-Path $output $ProofName
if(Test-Path -LiteralPath $proofPath){throw 'Source-card fixture already exists; refusing duplicate seed.'}
$backend=[Uri]$owner.backendUrl
if($backend.Scheme -ne 'http' -or $backend.Host -ne '127.0.0.1' -or $backend.Port -ne $owner.backendPort){throw 'Only the owned backend may be seeded.'}
$userId=([Guid]$isolation.userId).ToString('D')
$private=Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw|ConvertFrom-Json
$headers=@{'X-Api-Key'=$private.apiKey}
$identity=Get-Content -LiteralPath (Join-Path $output 'synthetic-documents.json') -Raw|ConvertFrom-Json|Where-Object docPath -eq 'Validation/Guide-Validation.pdf'
if(@($identity).Count -ne 1){throw 'One active synthetic guide must exist.'}
$docId=([Guid]$identity.docId).ToString('D')
$context=Invoke-RestMethod -Uri ($owner.backendUrl+'/documents/context?docId='+$docId+'&pageStart=2&pageEnd=2&before=0&after=0') -Headers $headers -TimeoutSec 20
if(-not $context.found -or @($context.items).Count -ne 1){throw 'Canonical page2 could not be read.'}
$document=$context.document
$item=$context.items[0]
$pdf=[IO.Path]::GetFullPath((Join-Path $owner.documentsRoot 'Validation/Guide-Validation.pdf'))
$pdfPrefix=[IO.Path]::GetFullPath($owner.documentsRoot)+[IO.Path]::DirectorySeparatorChar
if(-not $pdf.StartsWith($pdfPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture source escapes owned documents.'}
$pdfHash=(Get-FileHash -LiteralPath $pdf -Algorithm SHA256).Hash.ToLowerInvariant()
if($document.sourceHash -ne $pdfHash -or $document.docPath -ne 'Validation/Guide-Validation.pdf' -or
    $item.pageStart -ne 2 -or $item.pageEnd -ne 2){throw 'Canonical source identity/page does not match owned PDF.'}
$marker='Fixture UI sources — résultat contrôlé, aucune réponse modèle'
$sourcePath=[string]$document.docPath
$sourceHash=$pdfHash
$sessionTitle='Fixture UI sources — carte page 2'
if($Scenario -eq 'HashMismatch'){
    $sourceHash='0'*64
    if($sourceHash -eq $pdfHash){throw 'Synthetic mismatch unexpectedly matches the source.'}
    $sessionTitle='Fixture UI sources — hash incorrect'
    $marker+=' — CAS NEGATIF HASH'
}
if($Scenario -eq 'MissingPath'){
    $sourcePath='Validation/Absent-Source-'+[Guid]::NewGuid().ToString('N')+'/Guide-Validation.pdf'
    if(Test-Path -LiteralPath (Join-Path $owner.documentsRoot $sourcePath)){throw 'Synthetic missing path unexpectedly exists.'}
    $sessionTitle='Fixture UI sources — chemin absent, identité exacte'
    $marker+=' — CHEMIN ABSENT, RECUPERATION EXACTE OU REFUS ATTENDU'
}
$sources=[ordered]@{
    intent='fixture.ui_sources';fixture=@{schemaVersion='saaia.winui-source-card-fixture.v1';modelExecuted=$false;source='production_backend_canonical_page';scenario=$Scenario};
    sources=@([ordered]@{evidenceId='UI_FIXTURE_E1';docId=$docId;docPath=$sourcePath;docName=$document.docName;
        category='Validation';categoryPath='Validation';docLanguage='fr';pageStart=2;pageEnd=2;sourceHash=$sourceHash;
        revisionId=([Guid]$document.revisionId).ToString('D');chunkId=([Guid]$item.chunkId).ToString('D');
        snippet=[string]$item.text;score=1.0;sectionTitle='Code de validation ORION'})
}
$sourcesJson=$sources|ConvertTo-Json -Depth 12 -Compress
# Parse the exact submitted JSON before any mutation; no model/tool response is fabricated.
$validated=ConvertFrom-Json $sourcesJson
if(@($validated.sources).Count -ne 1 -or $validated.sources[0].sourceHash.Length -ne 64){throw 'Source-card payload is invalid.'}
$session=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/chat/sessions') -Headers $headers -ContentType 'application/json' -Body (@{userId=$userId;title=$sessionTitle;clientUser='synthetic-source-card-fixture'}|ConvertTo-Json)
$sessionId=([Guid]$session.sessionId).ToString('D')
$content=$marker+".`n"+$(switch($Scenario){
    'Canonical'{'Carte de contrôle : ouvrir uniquement le guide de Validation à la page2.'}
    'HashMismatch'{'Carte de contrôle négative : le client doit refuser cette source sans ouvrir de fichier au hash différent.'}
    'MissingPath'{'Chemin absent : une récupération de la version exacte vérifiée par SHA256, ou un refus sûr, est attendu. Ne pas ouvrir un homonyme au hash différent.'}
})
$request=@{userId=$userId;role='assistant';content=$content;sourcesJson=$sourcesJson;statusNote='Fixture UI sources';progressText=$null;trackingMetaJson=$null}|ConvertTo-Json -Depth 14
$message=Invoke-RestMethod -Method Post -Uri ($owner.backendUrl+'/chat/sessions/'+$sessionId+'/messages') -Headers $headers -ContentType 'application/json' -Body $request
$rows=Invoke-RestMethod -Uri ($owner.backendUrl+'/chat/sessions/'+$sessionId+'/messages?userId='+[Uri]::EscapeDataString($userId)+'&limit=1000') -Headers $headers -TimeoutSec 20
$rows=@($rows)
if($rows.Count -ne 1 -or $rows[0].role -ne 'assistant' -or $rows[0].content -ne $content){throw 'Seeded fixture message was not durably acknowledged exactly.'}
$storedSources=if($rows[0].sources -is [string]){ConvertFrom-Json $rows[0].sources}else{$rows[0].sources}
if($storedSources.sources[0].sourceHash -ne $sourceHash -or $storedSources.sources[0].docPath -ne $sourcePath -or $storedSources.sources[0].pageStart -ne 2 -or
    $storedSources.sources[0].revisionId -ne $document.revisionId){throw 'Stored source-card identity changed.'}
$snapshotStart=[DateTimeOffset]::UtcNow
$snapshotLabel=if($Scenario -eq 'Canonical'){'source-card-seeded'}else{'source-card-'+$Scenario.ToLowerInvariant()}
& (Join-Path $PSScriptRoot 'inspect-chat-store.ps1') -OutputDirectory $output -Label $snapshotLabel
$snapshot=Get-ChildItem -LiteralPath (Join-Path $output 'chat-store-snapshots') -Filter ('*-'+$snapshotLabel+'.json') -File|Where-Object LastWriteTimeUtc -ge $snapshotStart.UtcDateTime|Sort-Object Name|Select-Object -Last 1
if(-not $snapshot){throw 'Durable source-card snapshot was not captured.'}
@{schemaVersion='saaia-winui-source-card-session-fixture.v1';seededAtUtc=[DateTimeOffset]::UtcNow.ToString('o');
    backend='production_loopback_backend';userId=$userId;sessionId=$sessionId;messageId=([Guid]$message.messageId).ToString('D');
    sessionTitle=$sessionTitle;marker=$marker;scenario=$Scenario;role='assistant';modelExecuted=$false;advancedTerminalStateInjected=$false;
    canonicalSource=@{docId=$docId;docPath=$sourcePath;revisionId=$document.revisionId;chunkId=$item.chunkId;sourceHash=$sourceHash;pageStart=2;pageEnd=2};
    actualFixturePdfSha256=$pdfHash;pdfFilesModified=$false;expectedOpenOutcome=switch($Scenario){'Canonical'{'page2_of_exact_guide'};'HashMismatch'{'refused_no_matching_hash'};'MissingPath'{'exact_sha256_version_recovery_or_safe_refusal'}};
    contentSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($content))).ToLowerInvariant();
    sourcesJsonSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sourcesJson))).ToLowerInvariant();
    durableMessageCount=1;snapshot=[IO.Path]::GetRelativePath($output,$snapshot.FullName).Replace('\','/');snapshotSha256=(Get-FileHash -LiteralPath $snapshot.FullName).Hash;
    uiCardRenderingValidated=$false;pdfPageRenderingValidated=$false;productStatus='TESTE_NON_APPROUVE'}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $proofPath -Encoding utf8
Write-Output "Controlled source-card fixture seeded; sessionId=$sessionId, messageId=$($message.messageId), sourcePage=2. No model or UI was executed."
