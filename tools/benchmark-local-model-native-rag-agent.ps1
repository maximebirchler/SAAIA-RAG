[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LlamaServerPath,

    [Parameter(Mandatory = $true)]
    [string]$ModelPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$ProfileName = "native-rag-agent",
    [int]$Port = 18358,
    [int]$GpuLayers = 65,
    [int]$ContextSize = 8192,
    [int]$BatchSize = 1024,
    [int]$UbatchSize = 256,
    [int]$Threads = 4,
    [ValidateSet("on", "off", "auto")]
    [string]$FlashAttention = "on",
    [ValidateRange(0.0, 2.0)]
    [double]$Temperature = 0.0,
    [ValidateRange(0.0, 1.0)]
    [double]$TopP = 1.0,
    [ValidateRange(0, 200)]
    [int]$TopK = 40,
    [ValidateRange(0.0, 1.0)]
    [double]$MinP = 0.0,
    [int]$Seed = 42,
    [ValidateSet("full-copy", "id-only")]
    [string]$FinalContract = "full-copy",
    [switch]$SwaFull
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Wait-LlamaReady {
    param([string]$BaseUrl, [int]$TimeoutSeconds = 180)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-RestMethod -Method Get -Uri "$BaseUrl/v1/models" -TimeoutSec 3
            if ($response.data -and $response.data.Count -gt 0) {
                return $response
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }

    throw "llama-server did not become ready within ${TimeoutSeconds}s."
}

function Stop-LlamaServer {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $Process.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
    }
    catch {
        Write-Warning "Unable to stop llama-server PID $($Process.Id): $($_.Exception.Message)"
    }
}

function ConvertTo-ComparisonText {
    param([AllowNull()][object]$Value)

    $decomposed = ([string]$Value).Normalize([Text.NormalizationForm]::FormD)
    $characters = foreach ($character in $decomposed.ToCharArray()) {
        if (
            [Globalization.CharUnicodeInfo]::GetUnicodeCategory($character) -ne
            [Globalization.UnicodeCategory]::NonSpacingMark
        ) {
            $character
        }
    }
    $normalized = (-join $characters).Normalize([Text.NormalizationForm]::FormC)
    return $normalized.
        Replace(([string][char]0x0153), "oe").
        Replace(([string][char]0x0152), "OE").
        Replace(([string][char]0x00E6), "ae").
        Replace(([string][char]0x00C6), "AE")
}

function Test-Contains {
    param([AllowNull()][object]$Value, [string]$Needle)

    $normalizedValue = ConvertTo-ComparisonText $Value
    $normalizedNeedle = ConvertTo-ComparisonText $Needle
    return $normalizedValue.IndexOf(
        $normalizedNeedle,
        [StringComparison]::OrdinalIgnoreCase
    ) -ge 0
}

function Get-ToolCalls {
    param([AllowNull()][object]$Message)

    if ($null -eq $Message -or
        $null -eq $Message.PSObject.Properties["tool_calls"] -or
        $null -eq $Message.tool_calls) {
        return @()
    }

    return @($Message.tool_calls)
}

function Invoke-ChatCompletion {
    param(
        [string]$BaseUrl,
        [Collections.IDictionary]$Payload,
        [int]$TimeoutSeconds = 600
    )

    $json = $Payload | ConvertTo-Json -Depth 60 -Compress
    $utf8Body = [Text.Encoding]::UTF8.GetBytes($json)
    return Invoke-RestMethod `
        -Method Post `
        -Uri "$BaseUrl/v1/chat/completions" `
        -ContentType "application/json; charset=utf-8" `
        -Body $utf8Body `
        -TimeoutSec $TimeoutSeconds
}

function New-EvidenceItem {
    param(
        [string]$Id,
        [string]$Slot,
        [string]$Title,
        [string]$File,
        [int]$Page
    )

    return [pscustomobject]@{
        evidenceId = $Id
        slot = $Slot
        title = $Title
        source = [pscustomobject]@{
            file = $File
            page = $Page
        }
        excerpt = "Recette complete de $Title, classee comme $Slot."
    }
}

function Get-SyntheticEvidence {
    return @(
        (New-EvidenceItem "B1" "breakfast" "Porridge pomme et cannelle" "Petits-dejeuners.pdf" 12),
        (New-EvidenceItem "B2" "breakfast" "Tartines avocat et oeuf" "Petits-dejeuners.pdf" 18),
        (New-EvidenceItem "B3" "breakfast" "Pancakes aux myrtilles" "Cuisine-familiale.pdf" 31),
        (New-EvidenceItem "B4" "breakfast" "Muesli aux fruits frais" "Cuisine-familiale.pdf" 36),
        (New-EvidenceItem "B5" "breakfast" "Omelette aux fines herbes" "Recettes-du-quotidien.pdf" 9),
        (New-EvidenceItem "L1" "lunch" "Salade de quinoa aux legumes" "Repas-equilibres.pdf" 22),
        (New-EvidenceItem "L2" "lunch" "Wrap de poulet et crudites" "Repas-equilibres.pdf" 27),
        (New-EvidenceItem "L3" "lunch" "Curry de pois chiches" "Cuisine-familiale.pdf" 74),
        (New-EvidenceItem "L4" "lunch" "Pates aux tomates roties" "Recettes-du-quotidien.pdf" 41),
        (New-EvidenceItem "L5" "lunch" "Bol de riz au saumon" "Recettes-du-quotidien.pdf" 48),
        (New-EvidenceItem "S1" "snack" "Compote pomme-poire" "Collations-maison.pdf" 8),
        (New-EvidenceItem "S2" "snack" "Muffin a la banane" "Collations-maison.pdf" 14),
        (New-EvidenceItem "S3" "snack" "Yaourt aux fruits rouges" "Collations-maison.pdf" 19),
        (New-EvidenceItem "S4" "snack" "Batonnets de legumes et houmous" "Repas-equilibres.pdf" 63),
        (New-EvidenceItem "S5" "snack" "Biscuit avoine et chocolat" "Cuisine-familiale.pdf" 92),
        (New-EvidenceItem "D1" "dinner" "Saumon au four et brocoli" "Soupers-simples.pdf" 16),
        (New-EvidenceItem "D2" "dinner" "Poulet roti aux legumes" "Soupers-simples.pdf" 23),
        (New-EvidenceItem "D3" "dinner" "Lasagnes aux epinards" "Cuisine-familiale.pdf" 108),
        (New-EvidenceItem "D4" "dinner" "Soupe de lentilles corail" "Recettes-du-quotidien.pdf" 67),
        (New-EvidenceItem "D5" "dinner" "Gratin de cabillaud" "Soupers-simples.pdf" 39)
    )
}

function New-RagMultiSearchTool {
    return [ordered]@{
        type = "function"
        function = [ordered]@{
            name = "rag_multi_search"
            description = @"
Search the indexed knowledge base for several source-backed meal facets in one
call. Each query is executed independently, so it must include its target meal
type. When the user supplied no cuisine, ingredient or dietary preference, use
only the target meal type plus generic recipe or meal words. Do not invent meal
candidates or narrowing preferences before the search.
"@.Trim()
            parameters = [ordered]@{
                type = "object"
                properties = [ordered]@{
                    requests = [ordered]@{
                        type = "array"
                        minItems = 4
                        maxItems = 4
                        items = [ordered]@{
                            type = "object"
                            properties = [ordered]@{
                                target = [ordered]@{
                                    type = "string"
                                    enum = @("breakfast", "lunch", "snack", "dinner")
                                }
                                categoryPath = [ordered]@{
                                    type = "string"
                                    enum = @("Cuisine")
                                }
                                query = [ordered]@{
                                    type = "string"
                                    minLength = 3
                                    maxLength = 160
                                }
                            }
                            required = @("target", "categoryPath", "query")
                            additionalProperties = $false
                        }
                    }
                }
                required = @("requests")
                additionalProperties = $false
            }
        }
    }
}

function New-MealPlanSchema {
    param(
        [object[]]$Evidence,
        [ValidateSet("full-copy", "id-only")]
        [string]$Contract
    )

    $evidenceIds = @($Evidence | ForEach-Object { [string]$_.evidenceId })
    $sourceFiles = @(
        $Evidence |
            ForEach-Object { [string]$_.source.file } |
            Select-Object -Unique
    )
    $mealProperties = [ordered]@{
        slot = [ordered]@{
            type = "string"
            enum = @("breakfast", "lunch", "snack", "dinner")
        }
        evidenceId = [ordered]@{
            type = "string"
            enum = $evidenceIds
        }
    }
    $mealRequired = @("slot", "evidenceId")
    if ($Contract -eq "full-copy") {
        $mealProperties["title"] = [ordered]@{ type = "string" }
        $mealProperties["source"] = [ordered]@{
            type = "object"
            properties = [ordered]@{
                file = [ordered]@{
                    type = "string"
                    enum = $sourceFiles
                }
                page = [ordered]@{
                    type = "integer"
                    minimum = 1
                }
            }
            required = @("file", "page")
            additionalProperties = $false
        }
        $mealRequired += @("title", "source")
    }
    return [ordered]@{
        type = "object"
        properties = [ordered]@{
            days = [ordered]@{
                type = "array"
                minItems = 5
                maxItems = 5
                items = [ordered]@{
                    type = "object"
                    properties = [ordered]@{
                        day = [ordered]@{
                            type = "string"
                            enum = @("lundi", "mardi", "mercredi", "jeudi", "vendredi")
                        }
                        meals = [ordered]@{
                            type = "array"
                            minItems = 4
                            maxItems = 4
                            items = [ordered]@{
                                type = "object"
                                properties = $mealProperties
                                required = $mealRequired
                                additionalProperties = $false
                            }
                        }
                    }
                    required = @("day", "meals")
                    additionalProperties = $false
                }
            }
        }
        required = @("days")
        additionalProperties = $false
    }
}

function Measure-RetrievalPlan {
    param([object[]]$Calls)

    $score = 0
    $reasons = [Collections.Generic.List[string]]::new()
    $arguments = $null

    if ($Calls.Count -eq 1) {
        $score++
    }
    else {
        $reasons.Add("tool_call_count_wrong")
    }

    $call = $Calls | Select-Object -First 1
    if ($null -ne $call -and
        $null -ne $call.function -and
        [string]$call.function.name -eq "rag_multi_search") {
        $score++
    }
    else {
        $reasons.Add("tool_name_wrong")
    }

    try {
        $arguments = ([string]$call.function.arguments) | ConvertFrom-Json
        $score++
    }
    catch {
        $reasons.Add("tool_arguments_invalid_json")
    }

    $requests = if ($null -ne $arguments) { @($arguments.requests) } else { @() }
    if ($requests.Count -eq 4) {
        $score++
    }
    else {
        $reasons.Add("request_count_wrong")
    }

    $targetTerms = @{
        breakfast = @("breakfast", "petit-dejeuner", "petit dejeuner", "matinal")
        lunch = @("lunch", "dejeuner", "midi")
        snack = @("snack", "collation", "gouter")
        dinner = @("dinner", "diner", "souper", "soir")
    }
    $allowedQueryWords = @(
        "breakfast", "petit", "dejeuner", "matinal",
        "lunch", "midi", "snack", "collation", "gouter",
        "dinner", "diner", "souper", "soir", "repas", "meal", "meals",
        "recette", "recettes", "recipe", "recipes", "plat", "plats",
        "idee", "idees", "option", "options",
        "de", "du", "des", "pour", "et", "the", "for", "and"
    )
    foreach ($target in @("breakfast", "lunch", "snack", "dinner")) {
        $matches = @($requests | Where-Object { [string]$_.target -eq $target })
        if ($matches.Count -eq 1) {
            $score++
        }
        else {
            $reasons.Add("target_coverage_$target")
            continue
        }

        $request = $matches[0]
        if ([string]$request.categoryPath -eq "Cuisine") {
            $score++
        }
        else {
            $reasons.Add("category_wrong_$target")
        }

        $query = [string]$request.query
        if (@($targetTerms[$target] | Where-Object { Test-Contains $query $_ }).Count -gt 0) {
            $score++
        }
        else {
            $reasons.Add("query_target_missing_$target")
        }

        $queryWords = @(
            [regex]::Matches(
            $query,
                "[\p{L}\p{N}]+"
            ) |
                ForEach-Object {
                    (ConvertTo-ComparisonText $_.Value).ToLowerInvariant()
                }
        )
        $ungroundedWords = @(
            $queryWords |
                Where-Object { $allowedQueryWords -notcontains $_ }
        )
        $looksEnumerated =
            $query.Contains(":") -or
            $query.Contains(",") -or
            $query.Contains(";")
        if (
            $queryWords.Count -ge 2 -and
            $queryWords.Count -le 8 -and
            -not $looksEnumerated -and
            $ungroundedWords.Count -eq 0
        ) {
            $score++
        }
        else {
            $reasons.Add("query_adds_unrequested_semantics_or_is_unfocused_$target")
        }
    }

    return [pscustomobject]@{
        score = $score
        maximumScore = 20
        reasons = @($reasons)
        arguments = $arguments
    }
}

function Measure-FinalPlan {
    param(
        [AllowNull()][object]$Plan,
        [object[]]$AvailableEvidence,
        [ValidateSet("full-copy", "id-only")]
        [string]$Contract
    )

    $maximumScore = if ($Contract -eq "full-copy") { 87 } else { 47 }
    $score = 0
    $reasons = [Collections.Generic.List[string]]::new()
    if ($null -eq $Plan) {
        return [pscustomobject]@{
            score = 0
            maximumScore = $maximumScore
            reasons = @("final_json_invalid")
        }
    }
    $score++

    $byId = @{}
    foreach ($item in $AvailableEvidence) {
        $byId[[string]$item.evidenceId] = $item
    }
    $expectedDays = @("lundi", "mardi", "mercredi", "jeudi", "vendredi")
    $expectedSlots = @("breakfast", "lunch", "snack", "dinner")
    $usedEvidenceIds = [Collections.Generic.List[string]]::new()
    $days = @($Plan.days)

    foreach ($day in $expectedDays) {
        $dayMatches = @($days | Where-Object { [string]$_.day -eq $day })
        if ($dayMatches.Count -eq 1) {
            $score++
        }
        else {
            $reasons.Add("day_coverage_$day")
            continue
        }

        $meals = @($dayMatches[0].meals)
        foreach ($slot in $expectedSlots) {
            $mealMatches = @($meals | Where-Object { [string]$_.slot -eq $slot })
            if ($mealMatches.Count -eq 1) {
                $score++
            }
            else {
                $reasons.Add("slot_coverage_${day}_$slot")
                continue
            }

            $meal = $mealMatches[0]
            $evidenceId = [string]$meal.evidenceId
            $usedEvidenceIds.Add($evidenceId)
            $evidence = if ($byId.ContainsKey($evidenceId)) { $byId[$evidenceId] } else { $null }
            if ($null -ne $evidence -and [string]$evidence.slot -eq $slot) {
                $score++
            }
            else {
                $reasons.Add("unsupported_or_wrong_facet_${day}_$slot")
            }

            if ($Contract -eq "full-copy") {
                $normalizedMealTitle = ConvertTo-ComparisonText $meal.title
                $normalizedEvidenceTitle = if ($null -ne $evidence) {
                    ConvertTo-ComparisonText $evidence.title
                }
                else {
                    ""
                }
                if (
                    $null -ne $evidence -and
                    $normalizedMealTitle.Equals(
                        $normalizedEvidenceTitle,
                        [StringComparison]::OrdinalIgnoreCase
                    )
                ) {
                    $score++
                }
                else {
                    $reasons.Add("title_not_preserved_${day}_$slot")
                }

                if ($null -ne $evidence -and
                    [string]$meal.source.file -eq [string]$evidence.source.file -and
                    [int]$meal.source.page -eq [int]$evidence.source.page) {
                    $score++
                }
                else {
                    $reasons.Add("source_binding_wrong_${day}_$slot")
                }
            }
        }
    }

    if (@($usedEvidenceIds | Select-Object -Unique).Count -eq 20) {
        $score++
    }
    else {
        $reasons.Add("evidence_reused_or_cells_missing")
    }

    return [pscustomobject]@{
        score = $score
        maximumScore = $maximumScore
        reasons = @($reasons)
    }
}

function New-ProjectedPlan {
    param(
        [AllowNull()][object]$Plan,
        [object[]]$AvailableEvidence
    )

    if ($null -eq $Plan) {
        return $null
    }
    $byId = @{}
    foreach ($item in $AvailableEvidence) {
        $byId[[string]$item.evidenceId] = $item
    }
    $projectedDays = foreach ($day in @($Plan.days)) {
        $projectedMeals = foreach ($meal in @($day.meals)) {
            $evidenceId = [string]$meal.evidenceId
            $evidence = if ($byId.ContainsKey($evidenceId)) {
                $byId[$evidenceId]
            }
            else {
                $null
            }
            [pscustomobject]@{
                slot = [string]$meal.slot
                title = if ($null -ne $evidence) { [string]$evidence.title } else { $null }
                evidenceId = $evidenceId
                source = if ($null -ne $evidence) { $evidence.source } else { $null }
            }
        }
        [pscustomobject]@{
            day = [string]$day.day
            meals = @($projectedMeals)
        }
    }
    return [pscustomobject]@{ days = @($projectedDays) }
}

$serverPath = [IO.Path]::GetFullPath($LlamaServerPath)
$resolvedModelPath = [IO.Path]::GetFullPath($ModelPath)
$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) {
    throw "llama-server not found: $serverPath"
}
if (-not (Test-Path -LiteralPath $resolvedModelPath -PathType Leaf)) {
    throw "Model not found: $resolvedModelPath"
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
$safeName = (($ProfileName -replace "[^A-Za-z0-9._-]", "-").Trim("-"))
if ([string]::IsNullOrWhiteSpace($safeName)) {
    $safeName = "native-rag-agent"
}
$stdoutPath = Join-Path $resolvedOutputDirectory "$safeName-server.stdout.log"
$stderrPath = Join-Path $resolvedOutputDirectory "$safeName-server.stderr.log"
$artifactPath = Join-Path $resolvedOutputDirectory "$safeName-native-rag.json"
$baseUrl = "http://127.0.0.1:$Port"
$serverArguments = @(
    "--model", $resolvedModelPath,
    "--host", "127.0.0.1",
    "--port", [string]$Port,
    "--ctx-size", [string]$ContextSize,
    "--batch-size", [string]$BatchSize,
    "--ubatch-size", [string]$UbatchSize,
    "--threads", [string]$Threads,
    "--threads-batch", [string]$Threads,
    "--n-gpu-layers", [string]$GpuLayers,
    "--flash-attn", $FlashAttention,
    "--split-mode", "none",
    "--device", "CUDA0",
    "--parallel", "1",
    "--cache-type-k", "f16",
    "--cache-type-v", "f16",
    "--jinja",
    "--no-webui"
)
if ($SwaFull) {
    $serverArguments += "--swa-full"
}

$process = $null
$startedAt = Get-Date
try {
    $process = Start-Process `
        -FilePath $serverPath `
        -ArgumentList $serverArguments `
        -WorkingDirectory (Split-Path $serverPath) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru
    $models = Wait-LlamaReady -BaseUrl $baseUrl
    $modelId = [string]$models.data[0].id
    $props = Invoke-RestMethod -Method Get -Uri "$baseUrl/props" -TimeoutSec 10
    $tool = New-RagMultiSearchTool
    $allEvidence = @(Get-SyntheticEvidence)
    $finalContractInstruction = if ($FinalContract -eq "id-only") {
        @"
Apres le resultat de l'outil, choisis seulement le couple slot/evidenceId pour
chaque case. Le logiciel projettera ensuite mecaniquement le titre, le fichier
et la page depuis l'EvidenceBundle.
"@.Trim()
    }
    else {
        @"
Apres le resultat de l'outil, chaque case doit reprendre un titre fourni, son
evidenceId, son fichier et sa page exacts.
"@.Trim()
    }
    $systemPrompt = @"
Tu es l'orchestrateur semantique principal d'un assistant RAG. Pour repondre a
une demande documentaire, utilise d'abord l'outil de recherche. Pour le planning
demande, fais une recherche ciblee pour chacun des quatre types de repas dans un
seul appel. Le moteur execute chaque query independamment : repete donc le type
de repas concerne dans chaque query. Cette demande ne contient aucune preference
de cuisine, d'ingredient ou de nutrition : limite chaque query au type de repas
et a des mots generiques comme "recette" ou "repas". N'invente aucun plat ni
preference avant la recherche. Construis exactement cinq jours et quatre repas
par jour. $finalContractInstruction Utilise chaque preuve au plus une fois
puisque vingt recettes sont disponibles.
"@.Trim()
    $userPrompt = @"
J'ai besoin d'un planning de repas pour la semaine, du lundi au vendredi,
incluant chaque jour un petit-dejeuner, un dejeuner, une collation et un souper.
Le planning doit etre entierement fonde sur la base de connaissances Cuisine et
chaque proposition doit etre tracable jusqu'a son fichier et sa page.
"@.Trim()

    Write-Output "NATIVE_RAG_PHASE_START phase=retrieval_plan"
    $firstPayload = [ordered]@{
        model = $modelId
        messages = @(
            [ordered]@{ role = "system"; content = $systemPrompt },
            [ordered]@{ role = "user"; content = $userPrompt }
        )
        tools = @($tool)
        tool_choice = "auto"
        temperature = $Temperature
        top_p = $TopP
        top_k = $TopK
        min_p = $MinP
        seed = $Seed
        max_tokens = 1000
        stream = $false
    }
    $firstWatch = [Diagnostics.Stopwatch]::StartNew()
    $firstResponse = Invoke-ChatCompletion -BaseUrl $baseUrl -Payload $firstPayload
    $firstWatch.Stop()
    $firstMessage = $firstResponse.choices[0].message
    $toolCalls = @(Get-ToolCalls $firstMessage)
    $retrievalEvaluation = Measure-RetrievalPlan -Calls $toolCalls
    Write-Output (
        "NATIVE_RAG_PHASE_DONE phase=retrieval_plan score={0}/{1}" -f
        $retrievalEvaluation.score,
        $retrievalEvaluation.maximumScore)

    $requestedTargets = @()
    if ($null -ne $retrievalEvaluation.arguments) {
        $requestedTargets = @(
            $retrievalEvaluation.arguments.requests |
                ForEach-Object { [string]$_.target } |
                Select-Object -Unique
        )
    }
    $availableEvidence = @(
        $allEvidence |
            Where-Object { $requestedTargets -contains [string]$_.slot }
    )
    $toolResult = [ordered]@{
        evidenceBundleVersion = 1
        itemCount = $availableEvidence.Count
        items = $availableEvidence
    }

    $finalPlan = $null
    $finalContent = $null
    $finalResponse = $null
    $finalError = $null
    $finalWatch = [Diagnostics.Stopwatch]::StartNew()
    if ($toolCalls.Count -gt 0) {
        Write-Output "NATIVE_RAG_PHASE_START phase=source_bound_plan"
        try {
            $primaryCall = $toolCalls[0]
            $assistantMessage = [ordered]@{
                role = "assistant"
                content = $firstMessage.content
                tool_calls = @($primaryCall)
            }
            $toolMessage = [ordered]@{
                role = "tool"
                tool_call_id = [string]$primaryCall.id
                name = "rag_multi_search"
                content = ($toolResult | ConvertTo-Json -Depth 20 -Compress)
            }
            $finalPayload = [ordered]@{
                model = $modelId
                messages = @(
                    [ordered]@{ role = "system"; content = $systemPrompt },
                    [ordered]@{ role = "user"; content = $userPrompt },
                    $assistantMessage,
                    $toolMessage
                )
                tools = @($tool)
                tool_choice = "none"
                response_format = [ordered]@{
                    type = "json_schema"
                    json_schema = [ordered]@{
                        name = "source_bound_weekly_meal_plan"
                        strict = $true
                        schema = New-MealPlanSchema `
                            -Evidence $availableEvidence `
                            -Contract $FinalContract
                    }
                }
                temperature = $Temperature
                top_p = $TopP
                top_k = $TopK
                min_p = $MinP
                seed = $Seed
                max_tokens = 4096
                stream = $false
            }
            $finalResponse = Invoke-ChatCompletion `
                -BaseUrl $baseUrl `
                -Payload $finalPayload `
                -TimeoutSeconds 900
            $finalContent = [string]$finalResponse.choices[0].message.content
            $finalPlan = $finalContent | ConvertFrom-Json
        }
        catch {
            $finalError = $_.Exception.Message
        }
        Write-Output "NATIVE_RAG_PHASE_DONE phase=source_bound_plan"
    }
    else {
        $finalError = "No native tool call was produced."
    }
    $finalWatch.Stop()
    $finalEvaluation = Measure-FinalPlan `
        -Plan $finalPlan `
        -AvailableEvidence $availableEvidence `
        -Contract $FinalContract
    $projectedPlan = New-ProjectedPlan `
        -Plan $finalPlan `
        -AvailableEvidence $availableEvidence

    $totalScore = $retrievalEvaluation.score + $finalEvaluation.score
    $maximumScore = $retrievalEvaluation.maximumScore + $finalEvaluation.maximumScore
    $artifact = [ordered]@{
        schemaVersion = 1
        capturedAt = (Get-Date).ToUniversalTime().ToString("o")
        profileName = $ProfileName
        modelId = $modelId
        modelPath = $resolvedModelPath
        finalContract = $FinalContract
        generation = [ordered]@{
            temperature = $Temperature
            topP = $TopP
            topK = $TopK
            minP = $MinP
            seed = $Seed
        }
        runtime = [ordered]@{
            contextSize = $ContextSize
            batchSize = $BatchSize
            ubatchSize = $UbatchSize
            threads = $Threads
            gpuLayers = $GpuLayers
            flashAttention = $FlashAttention
            swaFull = [bool]$SwaFull
        }
        chatTemplateCapabilities = $props.chat_template_caps
        elapsedMs = [int]((Get-Date) - $startedAt).TotalMilliseconds
        retrievalPhase = [ordered]@{
            elapsedMs = [int]$firstWatch.ElapsedMilliseconds
            finishReason = [string]$firstResponse.choices[0].finish_reason
            promptTokens = [int]$firstResponse.usage.prompt_tokens
            completionTokens = [int]$firstResponse.usage.completion_tokens
            score = $retrievalEvaluation.score
            maximumScore = $retrievalEvaluation.maximumScore
            reasons = @($retrievalEvaluation.reasons)
            messageContent = [string]$firstMessage.content
            toolCalls = $toolCalls
        }
        finalPhase = [ordered]@{
            elapsedMs = [int]$finalWatch.ElapsedMilliseconds
            finishReason = if ($null -ne $finalResponse) {
                [string]$finalResponse.choices[0].finish_reason
            }
            else {
                $null
            }
            promptTokens = if ($null -ne $finalResponse) {
                [int]$finalResponse.usage.prompt_tokens
            }
            else {
                $null
            }
            completionTokens = if ($null -ne $finalResponse) {
                [int]$finalResponse.usage.completion_tokens
            }
            else {
                $null
            }
            score = $finalEvaluation.score
            maximumScore = $finalEvaluation.maximumScore
            reasons = @($finalEvaluation.reasons)
            content = $finalContent
            error = $finalError
        }
        projectedPlan = $projectedPlan
        availableEvidenceCount = $availableEvidence.Count
        totalScore = $totalScore
        maximumScore = $maximumScore
        scoreRatio = [math]::Round($totalScore / [math]::Max(1, $maximumScore), 4)
        stdoutLog = $stdoutPath
        stderrLog = $stderrPath
    }
    $artifact |
        ConvertTo-Json -Depth 60 |
        Set-Content -LiteralPath $artifactPath -Encoding UTF8
    $artifact | ConvertTo-Json -Depth 60
}
finally {
    Stop-LlamaServer -Process $process
}
