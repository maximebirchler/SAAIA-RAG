# Campaign-specific, read-only source-fidelity witness. No services, keys or API.
# Predicates were selected from the original PDF before a further paid trial.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SnapshotPath,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputPath){throw 'Use a new proof path.'}
$snapshot=Get-Content -LiteralPath $SnapshotPath -Raw|ConvertFrom-Json -Depth 100
$sourceSha='AA11E234FB3336EE76644958E888D83565E25BA875264BA75F7CE22B32D25C18'
if($snapshot.sourceSha256 -ine $sourceSha -or $snapshot.sourcePageCount -ne 36){throw 'Unexpected original source.'}
$checks=@(
    @{id='p5-servings';page=5;pattern='(?is)Pour\s+4\s+personnes'},
    @{id='p5-wine-quantity';page=5;pattern='(?is)1[,.]5\s+l\s+de\s+vin\s+rouge'},
    @{id='p5-preparation-duration';page=5;pattern='(?is)Pr[ée]paration\s*:\s*25\s+minutes'},
    @{id='p5-cooking-duration';page=5;pattern='(?is)Cuisson\s*:\s*2\s+h\s+45\s+minutes'},
    @{id='p5-cost-qualification';page=5;pattern='(?is)Pas\s+cher'},
    # Raw PDF operators can concatenate adjacent text boxes ("cherFacile").
    # A leading word boundary would test that transport detail, not the fact.
    @{id='p5-difficulty';page=5;pattern='(?is)Facile(?=\s|$)'},
    @{id='p5-tilted-note-sentence';page=5;pattern='(?is)Le\s+secret\s+du\s+bœuf\s+bourguignon\s+tient\s+surtout\s+au\s+choix\s+des\s+ingrédients'},
    @{id='p5-tilted-note-cut-order';page=5;pattern='(?is)le\s+paleron,\s+la\s+macreuse\s+ou\s+le\s+gîte'},
    @{id='p5-tilted-note-wine-advice';page=5;pattern='(?is)vin,\s+préférez-le\s+sec\s+et\s+de\s+qualité,\s+type\s+Bourgogne\s+ou\s+Bordeaux'},
    @{id='p5-warning';page=5;pattern='(?is)doit\s+impérativement\s+rester\s+bleue\s+à\s+l.intérieur'},
    @{id='p6-preparation-duration';page=6;pattern='(?is)Pr[ée]paration\s*:\s*20\s+minutes'},
    @{id='p6-cooking-duration';page=6;pattern='(?is)Cuisson\s*:\s*20\s+minutes'},
    @{id='p7-cooking-duration';page=7;pattern='(?is)Cuisson\s*:\s*1\s+h\s+30'},
    @{id='p7-qualification-note';page=7;pattern='(?is)mais\s+changez\s+le\s+nom\s+du\s+gratin'}
)
$observed=@(foreach($check in $checks){
    $page=@($snapshot.pages|Where-Object pageNumber -eq $check.page)
    if($page.Count -ne 1){throw 'Missing physical page.'}
    $units=@($snapshot.units|Where-Object{$_.pageStart -eq $check.page -and $_.pageEnd -eq $check.page})
    $chunks=@($snapshot.retrievalChunks|Where-Object{$_.pageStart -eq $check.page -and $_.pageEnd -eq $check.page})
    $indexes=@($chunks|ForEach-Object chunkIndex)
    $contexts=@($snapshot.contextualEntries|Where-Object{$_.chunkIndex -in $indexes})
    [ordered]@{id=$check.id;physicalPage=$check.page;
        rawSourceText=([string]$page[0].rawText -match $check.pattern);
        selectedPageText=([string]$page[0].text -match $check.pattern);
        extractedUnit=(@($units|Where-Object{$_.text -match $check.pattern}).Count -gt 0);
        publishedChunk=(@($chunks|Where-Object{$_.text -match $check.pattern}).Count -gt 0);
        publishedContext=(@($contexts|Where-Object{$_.text -match $check.pattern}).Count -gt 0)}
})
$failed=@($observed|Where-Object{-not($_.rawSourceText -and $_.selectedPageText -and $_.extractedUnit -and $_.publishedChunk -and $_.publishedContext)})
$proof=[ordered]@{schemaVersion='saaia-real-native-fidelity-witness.v1';observedAtUtc=[DateTime]::UtcNow.ToString('o');
    sourceSha256=$sourceSha;backendAssemblySha256=$snapshot.backendAssemblySha256;
    projectionSnapshotSha256=(Get-FileHash -LiteralPath $SnapshotPath -Algorithm SHA256).Hash;
    witnessScriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;
    providerCalls=0;predicateCount=$observed.Count;failedPredicateCount=$failed.Count;passed=($failed.Count -eq 0);
    limits=@('Selected physical pages and specified facts only; not exhaustive semantic equality or full worker ingestion.',
        'Exact physical-page bounds are required for units and chunks; neighboring pages cannot satisfy a predicate.',
        'No source text, credentials or document bytes are copied to this public proof.');checks=$observed}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($proof|ConvertTo-Json -Depth 20))
Write-Output ('Native fidelity: {0}/{1} predicates retained at every checked stage.' -f ($observed.Count-$failed.Count),$observed.Count)
if($failed.Count){throw 'Native fidelity is incomplete; no paid trial is justified by this witness.'}
