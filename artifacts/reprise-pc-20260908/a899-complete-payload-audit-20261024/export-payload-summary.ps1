param([string]$RootPath=(Join-Path $PSScriptRoot 'synthetic-wire-payloads'))
$ErrorActionPreference='Stop'
$rows=@(Get-ChildItem -LiteralPath $RootPath -Directory | ForEach-Object {
    $protocol=$_.Name
    Get-ChildItem -LiteralPath $_.FullName -Filter '*.json' | Sort-Object Name | ForEach-Object {
        $body=Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -AsHashtable
        $messages=if($body.ContainsKey('input')){$body.input}else{$body.messages}
        $user=($messages | Where-Object {$_.role -eq 'user'} | Select-Object -Last 1).content | ConvertFrom-Json -AsHashtable
        [object[]]$evidence=if($user.ContainsKey('evidence')){@($user.evidence)}else{@()}
        $visibleChars=0
        $supportWindows=0
        foreach($entry in $evidence){
            $visibleChars+=$entry.content.Length
            foreach($window in $entry.supportingContentWindows){
                $supportWindows++
                $visibleChars+=$window.content.Length
            }
        }
        [pscustomobject][ordered]@{
            protocol=$protocol;role=$_.BaseName;model=$body.model
            request=$user.request;language=$user.language
            visibleEvidenceCount=$evidence.Count;visibleTextCharacters=$visibleChars
            supportWindowCount=$supportWindows
            inventoryCount=if($user.ContainsKey('candidateInventory')){@($user.candidateInventory.items).Count}else{$null}
            bodyVerifiedDistinctCount=if($user.ContainsKey('candidateInventory')){$user.candidateInventory.bodyVerifiedDistinctCount}else{$null}
            dossierCount=if($user.ContainsKey('candidateDossier')){$user.candidateDossier.bodyVerifiedDistinctCount}else{$null}
            serializedCharacters=([IO.File]::ReadAllText($_.FullName)).Length
        }
    }
})
$rows | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'payload-summary.json') -Encoding utf8
$rows | Select-Object protocol,role,model,visibleEvidenceCount,inventoryCount,bodyVerifiedDistinctCount,dossierCount | Format-Table -AutoSize
