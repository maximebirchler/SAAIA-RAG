#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'PaidCampaign.Common.ps1')
$temp=Join-Path ([IO.Path]::GetTempPath()) ('saaia-projection-guards-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp)|Out-Null
$count=0
function Reject([scriptblock]$Action){
    $rejected=$false
    try{&$Action|Out-Null}catch{$rejected=$true}
    if(-not$rejected){throw 'Expected the projection compatibility guard to reject this input.'}
    $script:count++
}
try{
    $dll=Join-Path $temp 'SAAIA.IngestionGold.dll'
    $backend=Join-Path $temp 'SAAIA.Backend.dll'
    $config=Join-Path $temp 'SAAIA.IngestionGold.runtimeconfig.json'
    # Synthetic bytes exercise file/hash gates only; never execute these files.
    [IO.File]::WriteAllText($dll,'synthetic non-executable helper bytes')
    [IO.File]::WriteAllText($backend,'synthetic non-executable backend bytes')
    [IO.File]::WriteAllText($config,'{}')
    $hash=(Get-FileHash -LiteralPath $backend).Hash
    if((Assert-ProductionCorpusProjectionAssembly -Path $dll -ExpectedBackendSha256 $hash) -cne $dll){throw 'Compatible metadata was not preserved.'};$count++
    Reject {Assert-ProductionCorpusProjectionAssembly -Path 'relative.dll' -ExpectedBackendSha256 $hash}
    Reject {Assert-ProductionCorpusProjectionAssembly -Path $backend -ExpectedBackendSha256 $hash}
    Reject {Assert-ProductionCorpusProjectionAssembly -Path $dll -ExpectedBackendSha256 ('0'*64)}
    Remove-Item -LiteralPath $config
    Reject {Assert-ProductionCorpusProjectionAssembly -Path $dll -ExpectedBackendSha256 $hash}
    foreach($script in @('invoke-local-luna-campaign.ps1','Test-OwnedRealCorpus.ps1')){
        $source=Get-Content -LiteralPath (Join-Path $PSScriptRoot $script) -Raw
        if($source -notmatch '--projection-dll \$ProjectionAssemblyPath --backend-sha256' -or
            $source -notmatch 'Assert-ProductionCorpusProjectionAssembly') {throw 'A real entry point omitted the production projection gates.'}
        $count++
    }
    $sourceHash='a'*64
    $identity=@{docId='11111111-1111-4111-8111-111111111111';revisionId='22222222-2222-4222-8222-222222222222';
        sourceSha256=$sourceHash;sourcePageCount=36}
    $context=@{found=$true;document=@{docId=$identity.docId;revisionId=$identity.revisionId;sourceHash=('sha256:'+$sourceHash);pageCount=36};
        items=@(@{pageStart=5;pageEnd=6;text='whole canonical chunk across two physical pages'})}
    $read=Get-CanonicalWholeChunkReadCheck -Context $context -Identity $identity -ExpectedPhysicalPage 5
    if(-not $read.wholeChunkOverlapVerified -or $read.returnedWholeChunkRanges[0].pageEnd -ne 6 -or
       $read.windowSemantics -cne 'whole_canonical_chunks_overlapping_requested_physical_page_not_sliced_page_text'){
        throw 'A legitimate whole chunk overlapping page 5 must retain its exact 5-6 provenance, without claiming a sliced page.'
    };$count++
    foreach($mutation in @(
        {param($c)$c.document.sourceHash='b'*64},
        {param($c)$c.document.revisionId='33333333-3333-4333-8333-333333333333'},
        {param($c)$c.document.docId='44444444-4444-4444-8444-444444444444'},
        {param($c)$c.items[0].pageStart=6;$c.items[0].pageEnd=6},
        {param($c)$c.items[0].pageEnd=37},
        {param($c)$c.items[0].pageStart=6;$c.items[0].pageEnd=5},
        {param($c)$c.document.pageCount=35},
        {param($c)$c.items=@()}
    )){
        $candidate=$context|ConvertTo-Json -Depth 15|ConvertFrom-Json -AsHashtable
        &$mutation $candidate
        $check=Get-CanonicalWholeChunkReadCheck -Context $candidate -Identity $identity -ExpectedPhysicalPage 5
        if($check.wholeChunkOverlapVerified){throw 'Invalid whole-chunk identity/range was accepted.'};$count++
    }
    $pageText='Pour 4 personnes ; quantité 1,5 l.'
    $pageHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($pageText)))
    $identity.pages=@(@{pageNumber=5;textSha256=$pageHash})
    $projection=@{sourceSha256=$sourceHash;sourcePageCount=36;pages=@(@{pageNumber=5;text=$pageText},@{pageNumber=6;text='Neighboring page.'})}
    $physical=Get-PhysicalPagePatternCheck -Projection $projection -Identity $identity -ExpectedPhysicalPage 5 -ExpectedTextPatterns @('4 personnes','1,5 l')
    if(-not $physical.passed -or $physical.physicalPagePatternsMatched -ne 2){throw 'Exact original physical-page facts were not preserved.'};$count++
    $neighbor=$projection|ConvertTo-Json -Depth 15|ConvertFrom-Json -AsHashtable
    $neighbor.pages[0].text='Page five contains neither expected fact.';$neighbor.pages[1].text=$pageText
    $neighborIdentity=$identity|ConvertTo-Json -Depth 15|ConvertFrom-Json -AsHashtable
    $neighborIdentity.pages[0].textSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($neighbor.pages[0].text)))
    $check=Get-PhysicalPagePatternCheck -Projection $neighbor -Identity $neighborIdentity -ExpectedPhysicalPage 5 -ExpectedTextPatterns @('4 personnes','1,5 l')
    if($check.passed -or $check.physicalPagePatternsMatched -ne 0 -or -not $check.physicalPageTextHashExact){throw 'Neighboring page incorrectly satisfied expected facts on page five.'};$count++
    $wrongPageHash=$identity|ConvertTo-Json -Depth 15|ConvertFrom-Json -AsHashtable
    $wrongPageHash.pages[0].textSha256='b'*64
    $check=Get-PhysicalPagePatternCheck -Projection $projection -Identity $wrongPageHash -ExpectedPhysicalPage 5 -ExpectedTextPatterns @('4 personnes','1,5 l')
    if($check.passed -or $check.physicalPageTextHashExact){throw 'Incorrect physical page text hash was accepted.'};$count++
    $wrongSource=$projection|ConvertTo-Json -Depth 15|ConvertFrom-Json -AsHashtable
    $wrongSource.sourceSha256='b'*64
    $check=Get-PhysicalPagePatternCheck -Projection $wrongSource -Identity $identity -ExpectedPhysicalPage 5 -ExpectedTextPatterns @('4 personnes','1,5 l')
    if($check.passed -or $check.sourceIdentityExact){throw 'Native page from a different source hash was accepted.'};$count++
    $missingPage=$projection|ConvertTo-Json -Depth 15|ConvertFrom-Json -AsHashtable
    $missingPage.pages=@(@{pageNumber=6;text=$pageText})
    $check=Get-PhysicalPagePatternCheck -Projection $missingPage -Identity $identity -ExpectedPhysicalPage 5 -ExpectedTextPatterns @('4 personnes','1,5 l')
    if($check.passed -or $check.matchingPhysicalPageCount -ne 0){throw 'Missing physical page five was replaced with page six.'};$count++
    Write-Output "$count production projection transport guards passed; no helper, service, build, credential or network executed."
}finally{
    $resolved=[IO.Path]::GetFullPath($temp)
    $allowed=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if($resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('saaia-projection-guards-')){
        Remove-Item -LiteralPath $resolved -Recurse
    }
}
