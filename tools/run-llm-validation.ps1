param(
    [string]$QuestionBankPath = "",
    [ValidateSet("plan", "retrieval", "llm")]
    [string]$Mode = "plan",
    [string]$BackendBaseUrl = $env:SAAIA_VALIDATION_BACKEND_URL,
    [string]$ApiKey = $env:SAAIA_API_KEY,
    [string]$LlmBaseUrl = $env:SAAIA_VALIDATION_LLM_BASE_URL,
    [string]$LlmModel = $env:SAAIA_VALIDATION_LLM_MODEL,
    [string]$OutputDir = "",
    [string[]]$Ids = @(),
    [string[]]$Axis = @(),
    [string[]]$Difficulty = @(),
    [int]$Limit = 0,
    [int]$TopK = 8,
    [string]$Category = $env:SAAIA_VALIDATION_CATEGORY,
    [int]$MaxLlmSources = 8,
    [int]$MaxLlmContextChars = 5200,
    [int]$MaxCharsPerLlmSource = 650,
    [int]$TimeoutSeconds = 120,
    [int]$DelayMs = 0
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

function Resolve-RepoRoot {
    $dir = Split-Path -Parent $PSScriptRoot
    return (Resolve-Path $dir).Path
}

function Normalize-List {
    param([string[]]$Values)

    $items = New-Object System.Collections.Generic.List[string]
    foreach ($value in $Values) {
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        foreach ($part in ([string]$value -split "[,;]")) {
            if (-not [string]::IsNullOrWhiteSpace($part)) {
                $items.Add($part.Trim())
            }
        }
    }

    return $items.ToArray()
}

function Test-ContainsAny {
    param(
        [string]$Value,
        [string[]]$Needles
    )

    if ($Needles.Count -eq 0) {
        return $true
    }

    foreach ($needle in $Needles) {
        if ($Value.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Select-ValidationCases {
    param(
        [object[]]$Cases,
        [string[]]$Ids,
        [string[]]$Axis,
        [string[]]$Difficulty,
        [int]$Limit
    )

    $selected = @($Cases | Where-Object {
        (Test-ContainsAny ([string]$_.id) $Ids) -and
        (Test-ContainsAny ([string]$_.axis) $Axis) -and
        (Test-ContainsAny ([string]$_.difficulty) $Difficulty)
    })

    if ($Limit -gt 0) {
        $selected = @($selected | Select-Object -First $Limit)
    }

    return $selected
}

function Get-TextPreview {
    param(
        [string]$Text,
        [int]$MaxChars = 420
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return ""
    }

    $flat = ($Text -replace "\s+", " ").Trim()
    if ($flat.Length -le $MaxChars) {
        return $flat
    }

    return $flat.Substring(0, $MaxChars) + "..."
}

function ConvertTo-TsvCell {
    param([object]$Value)

    if ($null -eq $Value) {
        return ""
    }

    return ([string]$Value) -replace "`t", " " -replace "`r?`n", " "
}

function Write-Tsv {
    param(
        [object[]]$Rows,
        [string]$Path
    )

    $headers = @(
        "id",
        "axis",
        "difficulty",
        "corpusTarget",
        "theme",
        "mode",
        "httpStatus",
        "elapsedMs",
        "sourceCount",
        "targetMatched",
        "answerChars",
        "question",
        "answerPreview",
        "sourcesPreview",
        "expectedAnswerKind",
        "validationPoints",
        "error"
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(($headers -join "`t"))
    foreach ($row in $Rows) {
        $values = @($headers | ForEach-Object { ConvertTo-TsvCell $row.$_ })
        $lines.Add(($values -join "`t"))
    }

    [System.IO.File]::WriteAllLines($Path, $lines, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-HttpJson {
    param(
        [ValidateSet("GET", "POST")]
        [string]$Method,
        [string]$Url,
        [object]$Body = $null,
        [hashtable]$Headers = @{},
        [int]$TimeoutSeconds = 120
    )

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    foreach ($key in $Headers.Keys) {
        if (-not [string]::IsNullOrWhiteSpace([string]$Headers[$key])) {
            $client.DefaultRequestHeaders.Remove($key) | Out-Null
            $client.DefaultRequestHeaders.Add($key, [string]$Headers[$key])
        }
    }

    try {
        $started = Get-Date
        if ($Method -eq "GET") {
            $response = $client.GetAsync($Url).GetAwaiter().GetResult()
        }
        else {
            $json = ConvertTo-Json $Body -Depth 60 -Compress
            $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")
            $response = $client.PostAsync($Url, $content).GetAwaiter().GetResult()
        }

        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [ordered]@{
            ok = $response.IsSuccessStatusCode
            statusCode = [int]$response.StatusCode
            elapsedMs = [int]((Get-Date) - $started).TotalMilliseconds
            body = $text
        }
    }
    finally {
        $client.Dispose()
    }
}

function Get-RagSources {
    param([object]$Parsed)

    if ($null -eq $Parsed) {
        return @()
    }

    $hits = @()
    if ($Parsed.items) {
        $hits = @($Parsed.items)
    }
    elseif ($Parsed.matches) {
        $hits = @($Parsed.matches)
    }

    if ($hits.Count -eq 0) {
        return @()
    }

    return @($hits | ForEach-Object {
        [ordered]@{
            docName = $_.docName
            docPath = $_.docPath
            pageStart = $_.pageStart
            pageEnd = $_.pageEnd
            score = $_.score
            text = $_.text
        }
    })
}

function Format-SourcesForPrompt {
    param(
        [object[]]$Sources,
        [int]$MaxSources = 8,
        [int]$MaxCharsPerSource = 650,
        [int]$MaxTotalChars = 5200
    )

    if ($Sources.Count -eq 0) {
        return "Aucune source retrouvee."
    }

    $parts = New-Object System.Collections.Generic.List[string]
    $i = 1
    $usedChars = 0
    foreach ($source in @($Sources | Select-Object -First $MaxSources)) {
        $page = if ($source.pageStart) { "p.$($source.pageStart)" } else { "page inconnue" }
        $text = [string]$source.text
        if ($text.Length -gt $MaxCharsPerSource) {
            $text = $text.Substring(0, $MaxCharsPerSource) + "..."
        }

        $part = "[S$i] $($source.docName) ($page)`n$text"
        if (($usedChars + $part.Length) -gt $MaxTotalChars) {
            break
        }

        $parts.Add($part)
        $usedChars += $part.Length
        $i++
    }

    if ($parts.Count -eq 0) {
        return "Aucune source assez courte pour le contexte LLM."
    }

    return ($parts -join "`n`n")
}

function Get-TargetAliases {
    param([string]$TargetPart)

    $part = ([string]$TargetPart).Trim()
    if ([string]::IsNullOrWhiteSpace($part)) {
        return @()
    }

    $aliases = @{
        "Top30" = @("30-recettes-preferees-des-francais.pdf")
        "Nobilia" = @("nobilia-recettes-internationales-FR.pdf")
        "NEFF" = @("14911887_9001116052_NFFS4I_fr_fm.pdf")
        "Cemea" = @("si-on-cuisinait.pdf")
        "SIST" = @("livre-recette-sist-2025-web.pdf")
        "Etudiants" = @("9782317030376.pdf", "Je_cuisine_simplement.pdf")
        "JeCuisine" = @("Je_cuisine_simplement.pdf")
        "Moulinex" = @("Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf")
        "Chefbot" = @("chefbot_livre_de_recettes_fr.pdf")
        "Facilitemps" = @("facilitemps.pdf")
    }

    if ($aliases.ContainsKey($part)) {
        return @($aliases[$part])
    }

    return @($part)
}

function Split-TargetParts {
    param([string]$Target)

    $normalized = ([string]$Target).Trim()
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return @()
    }

    $parts = @($normalized -split "\s+vs\s+|\s*\+\s*|;" | ForEach-Object { $_.Trim() } | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })

    return $parts
}

function Test-TargetMatched {
    param(
        [string]$Target,
        [object[]]$Sources
    )

    if ([string]::IsNullOrWhiteSpace($Target) -or $Target -in @("Tous", "Multi-PDF", "Contexte conversationnel")) {
        return ""
    }

    $parts = @(Split-TargetParts $Target)
    if ($parts.Count -eq 0) {
        return ""
    }

    foreach ($part in $parts) {
        $candidates = @(Get-TargetAliases $part)
        $partMatched = $false

        foreach ($source in $Sources) {
            $haystack = "$($source.docName) $($source.docPath)"
            foreach ($candidate in $candidates) {
                if ($haystack.IndexOf($candidate, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $partMatched = $true
                    break
                }
            }

            if ($partMatched) {
                break
            }
        }

        if (-not $partMatched) {
            return "no"
        }
    }

    return "yes"
}

function Get-PreciseCuisineTitle {
    param([string]$Question)

    $s = ([string]$Question).Trim()
    if ([string]::IsNullOrWhiteSpace($s)) {
        return ""
    }

    $quoted = [regex]::Match($s, "[\u00ab`"'](?<title>[^\u00bb`"']{3,90})[\u00bb`"']")
    if ($quoted.Success) {
        return (Format-PreciseCuisineTitle $quoted.Groups["title"].Value)
    }

    $named = [regex]::Match(
        $s,
        "(?i)\b(?:recette|fiche)\s+(?:claire\s+)?(?:pour|de|du|de\s+la|des|d['’])\s+(?<title>[^:?.!,;]{3,90})")
    if ($named.Success) {
        return (Format-PreciseCuisineTitle $named.Groups["title"].Value)
    }

    return ""
}

function Format-PreciseCuisineTitle {
    param([string]$Value)

    $title = ([string]$Value).Trim(" ", ":", "-", ".", "?", "!", ",", ";")
    if ([string]::IsNullOrWhiteSpace($title)) {
        return ""
    }

    $title = [regex]::Replace(
        $title,
        "(?i)\s+(?:ingredients?|ingr[ée]dients?|etapes?|[ée]tapes?|temps|source|sources|portions?|materiel|mat[ée]riel|reglages?|r[ée]glages?)\b.*$",
        "").Trim()

    if ($title.Length -lt 3) {
        return ""
    }

    return $title
}

function Invoke-RagSearch {
    param([object]$Case)

    if ([string]::IsNullOrWhiteSpace($BackendBaseUrl)) {
        throw "BackendBaseUrl is required in retrieval/llm mode. Set -BackendBaseUrl or SAAIA_VALIDATION_BACKEND_URL."
    }

    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
        $headers["X-Api-Key"] = $ApiKey
    }

    $preciseTitle = Get-PreciseCuisineTitle ([string]$Case.question)
    $retrievalQueries = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($preciseTitle)) {
        $retrievalQueries.Add([string]$Case.question)
    } else {
        $retrievalQueries.Add($preciseTitle)
        $retrievalQueries.Add("$preciseTitle $($Case.question)")
    }

    $url = $BackendBaseUrl.TrimEnd("/") + "/rag/search"
    $response = $null
    $parsed = $null
    $sources = New-Object System.Collections.ArrayList
    $seenSources = New-Object System.Collections.Generic.HashSet[string]

    foreach ($query in $retrievalQueries) {
        if ([string]::IsNullOrWhiteSpace($query)) {
            continue
        }

        $body = [ordered]@{
            query = $query
            topK = $TopK
        }
        if (-not [string]::IsNullOrWhiteSpace($Category)) {
            $body.category = $Category
        }

        $currentResponse = Invoke-HttpJson -Method "POST" -Url $url -Body $body -Headers $headers -TimeoutSeconds $TimeoutSeconds
        if ($null -eq $response) {
            $response = $currentResponse
        }

        $currentParsed = $null
        if (-not [string]::IsNullOrWhiteSpace($currentResponse.body)) {
            $currentParsed = $currentResponse.body | ConvertFrom-Json
        }
        if ($null -eq $parsed) {
            $parsed = $currentParsed
        }

        foreach ($source in @(Get-RagSources $currentParsed)) {
            $key = "$($source.docPath)|$($source.pageStart)|$($source.excerpt)"
            if ($seenSources.Add($key)) {
                [void]$sources.Add($source)
            }
        }
    }

    return [ordered]@{
        response = $response
        parsed = $parsed
        sources = @($sources)
    }
}

function Invoke-LlmAnswer {
    param(
        [object]$Case,
        [object[]]$Sources
    )

    if ([string]::IsNullOrWhiteSpace($LlmBaseUrl)) {
        throw "LlmBaseUrl is required in llm mode. Set -LlmBaseUrl or SAAIA_VALIDATION_LLM_BASE_URL."
    }

    $model = if ([string]::IsNullOrWhiteSpace($LlmModel)) { "local" } else { $LlmModel }
    $context = Format-SourcesForPrompt `
        -Sources $Sources `
        -MaxSources $MaxLlmSources `
        -MaxCharsPerSource $MaxCharsPerLlmSource `
        -MaxTotalChars $MaxLlmContextChars
    $system = @"
Tu es SAAIA, assistant RAG local.
Reponds en francais, avec un ton naturel et utile.
Tu dois utiliser uniquement les sources fournies.
Si les sources ne contiennent pas assez d'information, dis-le clairement et n'invente pas la recette, les quantites, les temps ou les etapes.
Ne transforme pas une question simple en planning de semaine sauf si l'utilisateur demande explicitement un menu, une semaine, un planning ou du meal prep.
Pour une fiche recette precise, verifie que le titre ou les ingredients de cette recette sont presents dans les sources avant de repondre.
Pour une comparaison, separe clairement les recettes et signale quand une des versions manque dans les sources.
Pour une demande d'invention, d'amelioration ou de contournement des sources, refuse la partie non sourcee et propose seulement ce qui est etabli par les sources.
Ne fusionne pas deux recettes sans le signaler. Ne donne pas de quantites exactes si elles ne sont pas presentes dans les sources.
Termine par une section Sources concise.
"@

    $user = @"
Question utilisateur:
$($Case.question)

Critere attendu:
$($Case.expectedAnswerKind)

Points de validation:
$($Case.validationPoints)

Sources:
$context
"@

    $body = [ordered]@{
        model = $model
        stream = $false
        temperature = 0.1
        max_tokens = 900
        messages = @(
            [ordered]@{ role = "system"; content = $system },
            [ordered]@{ role = "user"; content = $user }
        )
    }

    $url = $LlmBaseUrl.TrimEnd("/") + "/v1/chat/completions"
    $response = Invoke-HttpJson -Method "POST" -Url $url -Body $body -TimeoutSeconds $TimeoutSeconds
    $answer = ""
    try {
        $parsed = $response.body | ConvertFrom-Json
        if ($parsed.choices -and $parsed.choices.Count -gt 0 -and $parsed.choices[0].message.content) {
            $answer = [string]$parsed.choices[0].message.content
        }
    }
    catch {
        $answer = ""
    }

    return [ordered]@{
        response = $response
        answer = $answer
    }
}

$repoRoot = Resolve-RepoRoot
if ([string]::IsNullOrWhiteSpace($QuestionBankPath)) {
    $QuestionBankPath = Join-Path $repoRoot "backend\SAAIA.Backend.Tests\Fixtures\retrieval_cuisine_validation.v1.json"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\llm-validation"
}

if ([string]::IsNullOrWhiteSpace($LlmBaseUrl)) {
    $LlmBaseUrl = "http://127.0.0.1:1234"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$bankPath = (Resolve-Path $QuestionBankPath).Path
$bank = Get-Content $bankPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($Category) -and [string]$bank.version -eq "cuisine-v1") {
    $Category = "Cuisine"
}
$idsFilter = Normalize-List $Ids
$axisFilter = Normalize-List $Axis
$difficultyFilter = Normalize-List $Difficulty
$cases = Select-ValidationCases -Cases @($bank.validationCases) -Ids $idsFilter -Axis $axisFilter -Difficulty $difficultyFilter -Limit $Limit

if ($cases.Count -eq 0) {
    throw "No validation case matched the requested filters."
}

$stamp = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$prefix = Join-Path $OutputDir "$($bank.version)-$Mode-$stamp"
$jsonlPath = "$prefix.jsonl"
$jsonPath = "$prefix.json"
$tsvPath = "$prefix.tsv"

$rows = New-Object System.Collections.Generic.List[object]
$jsonlWriter = [System.IO.StreamWriter]::new($jsonlPath, $false, [System.Text.UTF8Encoding]::new($false))
try {
    $index = 0
    foreach ($case in $cases) {
        $index++
        Write-Host "[$index/$($cases.Count)] $($case.id) $($case.axis): $($case.question)"

        $sources = @()
        $answer = ""
        $httpStatus = ""
        $elapsedMs = 0
        $errorText = ""

        try {
            if ($Mode -eq "retrieval" -or $Mode -eq "llm") {
                $rag = Invoke-RagSearch $case
                $sources = @($rag.sources)
                $httpStatus = $rag.response.statusCode
                $elapsedMs += [int]$rag.response.elapsedMs
            }

            if ($Mode -eq "llm") {
                $llm = Invoke-LlmAnswer -Case $case -Sources $sources
                $answer = $llm.answer
                $httpStatus = "$httpStatus/$($llm.response.statusCode)"
                $elapsedMs += [int]$llm.response.elapsedMs
            }
        }
        catch {
            $errorText = $_.Exception.Message
        }

        $sourcesPreview = Get-TextPreview (($sources | ForEach-Object { "$($_.docName) p.$($_.pageStart)" }) -join " | ") 640
        $row = [ordered]@{
            id = $case.id
            axis = $case.axis
            difficulty = $case.difficulty
            corpusTarget = $case.corpusTarget
            theme = $case.theme
            mode = $Mode
            httpStatus = $httpStatus
            elapsedMs = $elapsedMs
            sourceCount = $sources.Count
            targetMatched = Test-TargetMatched -Target ([string]$case.corpusTarget) -Sources $sources
            answerChars = $answer.Length
            question = $case.question
            answerPreview = Get-TextPreview $answer 900
            sourcesPreview = $sourcesPreview
            expectedAnswerKind = $case.expectedAnswerKind
            validationPoints = $case.validationPoints
            error = $errorText
        }

        $record = [ordered]@{
            runMode = $Mode
            bankVersion = $bank.version
            id = $case.id
            axis = $case.axis
            difficulty = $case.difficulty
            corpusTarget = $case.corpusTarget
            theme = $case.theme
            question = $case.question
            expectedAnswerKind = $case.expectedAnswerKind
            validationPoints = $case.validationPoints
            httpStatus = $httpStatus
            elapsedMs = $elapsedMs
            sources = $sources
            answer = $answer
            error = $errorText
        }

        $rows.Add($row)
        $jsonlWriter.WriteLine((ConvertTo-Json $record -Depth 80 -Compress))

        if ($DelayMs -gt 0) {
            Start-Sleep -Milliseconds $DelayMs
        }
    }
}
finally {
    $jsonlWriter.Dispose()
}

$summary = [ordered]@{
    generatedAt = (Get-Date).ToString("o")
    questionBankPath = $bankPath
    bankVersion = $bank.version
    mode = $Mode
    backendBaseUrl = $BackendBaseUrl
    llmBaseUrl = $LlmBaseUrl
    llmModel = $LlmModel
    category = $Category
    maxLlmSources = $MaxLlmSources
    maxLlmContextChars = $MaxLlmContextChars
    maxCharsPerLlmSource = $MaxCharsPerLlmSource
    selectedCount = $cases.Count
    filters = [ordered]@{
        ids = $idsFilter
        axis = $axisFilter
        difficulty = $difficultyFilter
        limit = $Limit
    }
    outputs = [ordered]@{
        json = $jsonPath
        jsonl = $jsonlPath
        tsv = $tsvPath
    }
    totals = [ordered]@{
        errors = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.error) }).Count
        withSources = @($rows | Where-Object { $_.sourceCount -gt 0 }).Count
        targetMatched = @($rows | Where-Object { $_.targetMatched -eq "yes" }).Count
        targetMissed = @($rows | Where-Object { $_.targetMatched -eq "no" }).Count
        withAnswer = @($rows | Where-Object { $_.answerChars -gt 0 }).Count
    }
    rows = $rows
}

[System.IO.File]::WriteAllText($jsonPath, (ConvertTo-Json $summary -Depth 80), [System.Text.UTF8Encoding]::new($false))
Write-Tsv -Rows $rows.ToArray() -Path $tsvPath

Write-Host ""
Write-Host "Validation run written:"
Write-Host "  JSON : $jsonPath"
Write-Host "  JSONL: $jsonlPath"
Write-Host "  TSV  : $tsvPath"
