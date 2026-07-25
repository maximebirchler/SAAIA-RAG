[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:12662",
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$ProfileName = "qwen3-native-agent-loop",
    [ValidateRange(0.0, 2.0)]
    [double]$Temperature = 0.7,
    [ValidateRange(0.0, 1.0)]
    [double]$TopP = 0.8,
    [ValidateRange(0, 200)]
    [int]$TopK = 20,
    [ValidateRange(0.0, 1.0)]
    [double]$MinP = 0.0,
    [int]$Seed = 42,
    [ValidateRange(1, 8)]
    [int]$MaximumTurns = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-FunctionTool {
    param(
        [string]$Name,
        [string]$Description,
        [Collections.IDictionary]$Properties,
        [string[]]$Required = @()
    )

    [ordered]@{
        type = "function"
        function = [ordered]@{
            name = $Name
            description = $Description
            parameters = [ordered]@{
                type = "object"
                properties = $Properties
                required = $Required
                additionalProperties = $false
            }
        }
    }
}

function Get-ToolCalls {
    param([AllowNull()][object]$Message)

    if ($null -eq $Message -or
        $null -eq $Message.PSObject.Properties["tool_calls"] -or
        $null -eq $Message.tool_calls) {
        return @()
    }

    @($Message.tool_calls)
}

function Convert-Arguments {
    param([AllowNull()][object]$Call)

    try {
        if ($null -eq $Call -or $null -eq $Call.function) {
            return $null
        }

        $raw = $Call.function.arguments
        if ($raw -is [string]) {
            return $raw | ConvertFrom-Json
        }

        return $raw
    }
    catch {
        return $null
    }
}

function Get-RequestError {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)

    $message = $ErrorRecord.Exception.Message
    try {
        $response = $ErrorRecord.Exception.Response
        if ($null -ne $response) {
            $stream = $response.GetResponseStream()
            if ($null -ne $stream) {
                $reader = [IO.StreamReader]::new($stream)
                try {
                    $body = $reader.ReadToEnd()
                    if (-not [string]::IsNullOrWhiteSpace($body)) {
                        $message = "$message BODY=$body"
                    }
                }
                finally {
                    $reader.Dispose()
                }
            }
        }
    }
    catch {
        # Preserve the original request error if the response body is unavailable.
    }

    return $message
}

function Get-ToolCallKey {
    param([object]$Call)

    $arguments = if ($null -ne $Call.function.arguments) {
        [string]$Call.function.arguments
    } else {
        ""
    }
    return "$([string]$Call.function.name)|$arguments"
}

function Invoke-AgentCompletion {
    param(
        [string]$ModelId,
        [object[]]$Messages,
        [object[]]$Tools,
        [int]$MaxTokens
    )

    $payload = [ordered]@{
        model = $ModelId
        messages = $Messages
        tools = $Tools
        tool_choice = "auto"
        parallel_tool_calls = $true
        temperature = $Temperature
        top_p = $TopP
        top_k = $TopK
        min_p = $MinP
        presence_penalty = 0.0
        frequency_penalty = 0.0
        seed = $Seed
        max_tokens = $MaxTokens
        stream = $false
    }

    Invoke-RestMethod `
        -Method Post `
        -Uri "$BaseUrl/v1/chat/completions" `
        -ContentType "application/json" `
        -Body ($payload | ConvertTo-Json -Depth 40 -Compress) `
        -TimeoutSec 300
}

function New-AssistantHistoryMessage {
    param([object]$Message)

    $history = [ordered]@{
        role = "assistant"
        content = if ($null -eq $Message.content) { "" } else { [string]$Message.content }
    }
    $calls = @(Get-ToolCalls $Message)
    if ($calls.Count -gt 0) {
        $history.tool_calls = $calls
    }
    return $history
}

function Invoke-MockedTool {
    param(
        [string]$ScenarioName,
        [object]$Call
    )

    $name = [string]$Call.function.name
    $arguments = Convert-Arguments $Call

    if ($ScenarioName -eq "named_document_multiturn") {
        switch ($name) {
            "documents_navigation" {
                return [ordered]@{
                    ok = $true
                    entries = @(
                        [ordered]@{
                            docRef = "HydraulicPumpManual.pdf"
                            pageStart = 42
                            pageEnd = 43
                            label = "HX-42 - Couvercle et couple de serrage"
                        }
                    )
                }
            }
            "rag_search" {
                return [ordered]@{
                    ok = $true
                    hits = @(
                        [ordered]@{
                            evidenceId = "S1"
                            docRef = "HydraulicPumpManual.pdf"
                            page = 42
                            chunkId = "hydraulic-pump-manual:42:3"
                            text = "Pour la pompe HX-42, serrer les boulons du couvercle en croix a 85 N m."
                        }
                    )
                }
            }
            "documents_context" {
                return [ordered]@{
                    ok = $true
                    evidence = @(
                        [ordered]@{
                            evidenceId = "S1"
                            docRef = "HydraulicPumpManual.pdf"
                            page = 42
                            chunkId = "hydraulic-pump-manual:42:3"
                            text = "Pour la pompe HX-42, serrer les boulons du couvercle en croix a 85 N m."
                        }
                    )
                }
            }
            "memory_read" {
                return [ordered]@{ ok = $true; facts = @() }
            }
        }
    }

    if ($ScenarioName -eq "meal_plan_autonomous") {
        switch ($name) {
            "memory_read" {
                return [ordered]@{
                    ok = $true
                    facts = @(
                        "Aucune allergie ou preference alimentaire durable n'est enregistree."
                    )
                }
            }
            "documents_navigation" {
                return [ordered]@{
                    ok = $true
                    entries = @(
                        [ordered]@{ docRef = "Cuisine/Je_cuisine_simplement.pdf"; pageStart = 35; pageEnd = 39; label = "Petits dejeuners" },
                        [ordered]@{ docRef = "Cuisine/Repas_equilibres.pdf"; pageStart = 12; pageEnd = 28; label = "Dejeuners" },
                        [ordered]@{ docRef = "Cuisine/Collations_saines.pdf"; pageStart = 8; pageEnd = 16; label = "Collations" },
                        [ordered]@{ docRef = "Cuisine/Recettes_du_soir.pdf"; pageStart = 20; pageEnd = 44; label = "Soupers" }
                    )
                }
            }
            "rag_search" {
                $allHits = @(
                    [ordered]@{ evidenceId = "S1"; meal = "petit-dejeuner"; title = "Porridge pomme-cannelle"; docRef = "Cuisine/Je_cuisine_simplement.pdf"; page = 35 },
                    [ordered]@{ evidenceId = "S2"; meal = "petit-dejeuner"; title = "Tartines ricotta et poire"; docRef = "Cuisine/Je_cuisine_simplement.pdf"; page = 36 },
                    [ordered]@{ evidenceId = "S3"; meal = "petit-dejeuner"; title = "Omelette aux fines herbes"; docRef = "Cuisine/Je_cuisine_simplement.pdf"; page = 37 },
                    [ordered]@{ evidenceId = "S4"; meal = "petit-dejeuner"; title = "Yaourt, avoine et petits fruits"; docRef = "Cuisine/Je_cuisine_simplement.pdf"; page = 38 },
                    [ordered]@{ evidenceId = "S5"; meal = "petit-dejeuner"; title = "Pain perdu aux fruits"; docRef = "Cuisine/Je_cuisine_simplement.pdf"; page = 39 },
                    [ordered]@{ evidenceId = "S6"; meal = "dejeuner"; title = "Salade de lentilles et feta"; docRef = "Cuisine/Repas_equilibres.pdf"; page = 12 },
                    [ordered]@{ evidenceId = "S7"; meal = "dejeuner"; title = "Wrap au poulet et crudites"; docRef = "Cuisine/Repas_equilibres.pdf"; page = 15 },
                    [ordered]@{ evidenceId = "S8"; meal = "dejeuner"; title = "Soupe de legumes et tartine"; docRef = "Cuisine/Repas_equilibres.pdf"; page = 18 },
                    [ordered]@{ evidenceId = "S9"; meal = "dejeuner"; title = "Bowl quinoa et pois chiches"; docRef = "Cuisine/Repas_equilibres.pdf"; page = 22 },
                    [ordered]@{ evidenceId = "S10"; meal = "dejeuner"; title = "Pates au thon et tomates"; docRef = "Cuisine/Repas_equilibres.pdf"; page = 28 },
                    [ordered]@{ evidenceId = "S11"; meal = "collation"; title = "Pomme et amandes"; docRef = "Cuisine/Collations_saines.pdf"; page = 8 },
                    [ordered]@{ evidenceId = "S12"; meal = "collation"; title = "Houmous et batonnets de carotte"; docRef = "Cuisine/Collations_saines.pdf"; page = 10 },
                    [ordered]@{ evidenceId = "S13"; meal = "collation"; title = "Yaourt nature et noix"; docRef = "Cuisine/Collations_saines.pdf"; page = 12 },
                    [ordered]@{ evidenceId = "S14"; meal = "collation"; title = "Banane et beurre d'arachide"; docRef = "Cuisine/Collations_saines.pdf"; page = 14 },
                    [ordered]@{ evidenceId = "S15"; meal = "collation"; title = "Fromage et raisins"; docRef = "Cuisine/Collations_saines.pdf"; page = 16 },
                    [ordered]@{ evidenceId = "S16"; meal = "souper"; title = "Saumon, riz et brocoli"; docRef = "Cuisine/Recettes_du_soir.pdf"; page = 20 },
                    [ordered]@{ evidenceId = "S17"; meal = "souper"; title = "Curry de pois chiches"; docRef = "Cuisine/Recettes_du_soir.pdf"; page = 26 },
                    [ordered]@{ evidenceId = "S18"; meal = "souper"; title = "Poulet roti et legumes"; docRef = "Cuisine/Recettes_du_soir.pdf"; page = 31 },
                    [ordered]@{ evidenceId = "S19"; meal = "souper"; title = "Chili vegetarien"; docRef = "Cuisine/Recettes_du_soir.pdf"; page = 37 },
                    [ordered]@{ evidenceId = "S20"; meal = "souper"; title = "Gratin de poisson et poireaux"; docRef = "Cuisine/Recettes_du_soir.pdf"; page = 44 }
                )
                $requestedDocRef = if ($null -ne $arguments -and
                    $null -ne $arguments.PSObject.Properties["docRef"]) {
                    [string]$arguments.docRef
                } else {
                    ""
                }
                $selectedHits = if ([string]::IsNullOrWhiteSpace($requestedDocRef)) {
                    $allHits
                } else {
                    @($allHits | Where-Object { [string]$_.docRef -eq $requestedDocRef })
                }
                return [ordered]@{
                    ok = $true
                    note = "$($selectedHits.Count) propositions distinctes sont disponibles. Chaque element conserve son fichier et sa page."
                    hits = @($selectedHits)
                }
            }
            "documents_context" {
                $docRef = if ($null -ne $arguments) { [string]$arguments.docRef } else { "" }
                return [ordered]@{
                    ok = $true
                    docRef = $docRef
                    note = "Les propositions retournees par rag_search sont les unites citables exactes de ces pages."
                }
            }
        }
    }

    return [ordered]@{
        ok = $false
        error = "Unknown mocked tool '$name' for scenario '$ScenarioName'."
    }
}

function Get-AgentTools {
    $emptyProperties = [ordered]@{}

    @(
        (New-FunctionTool `
            -Name "memory_read" `
            -Description "Lire les preferences utilisateur durables pertinentes. La memoire aide a personnaliser, mais ne constitue jamais une source documentaire." `
            -Properties ([ordered]@{
                purpose = [ordered]@{ type = "string"; description = "Ce que le LLM veut verifier dans la memoire." }
            }) `
            -Required @("purpose")),
        (New-FunctionTool `
            -Name "documents_navigation" `
            -Description "Explorer les documents, sommaires, sections et plages de pages disponibles." `
            -Properties ([ordered]@{
                categoryPath = [ordered]@{ type = "string" }
                query = [ordered]@{ type = "string" }
            })),
        (New-FunctionTool `
            -Name "rag_search" `
            -Description "Rechercher des passages et unites citables dans la base documentaire." `
            -Properties ([ordered]@{
                query = [ordered]@{ type = "string" }
                categoryPath = [ordered]@{ type = "string" }
                docRef = [ordered]@{ type = "string" }
                topK = [ordered]@{ type = "integer"; minimum = 1; maximum = 40 }
            }) `
            -Required @("query")),
        (New-FunctionTool `
            -Name "documents_context" `
            -Description "Lire un document ou une plage de pages ciblee a partir d'une ancre deja connue." `
            -Properties ([ordered]@{
                docRef = [ordered]@{ type = "string" }
                pageStart = [ordered]@{ type = "integer"; minimum = 1 }
                pageEnd = [ordered]@{ type = "integer"; minimum = 1 }
            }) `
            -Required @("docRef"))
    )
}

function Get-Scenarios {
    @(
        [pscustomobject]@{
            name = "no_tool_rewrite"
            maximumTurns = 1
            maxTokens = 180
            user = "Reformule en francais professionnel : le rapport est pas fini."
        },
        [pscustomobject]@{
            name = "named_document_multiturn"
            maximumTurns = [math]::Min(3, $MaximumTurns)
            maxTokens = 320
            user = "Selon le manuel HydraulicPumpManual.pdf, quel est le couple de serrage des boulons du couvercle de la pompe HX-42 ? Reponds avec le fichier et la page."
        },
        [pscustomobject]@{
            name = "meal_plan_autonomous"
            maximumTurns = $MaximumTurns
            maxTokens = 1800
            user = "J'ai besoin d'un planning de repas du lundi au vendredi incluant petit-dejeuner, dejeuner, collation et souper. Utilise la base documentaire, fais un tableau clair, cite le fichier et la page de chaque proposition et n'invente rien."
        }
    )
}

function Measure-Scenario {
    param(
        [object]$Scenario,
        [object[]]$Turns
    )

    $calls = @($Turns | ForEach-Object { @($_.toolCalls) })
    $callKeys = @($calls | ForEach-Object { Get-ToolCallKey $_ })
    $duplicates = @($callKeys | Group-Object | Where-Object Count -gt 1)
    $finalTurn = $Turns | Select-Object -Last 1
    $finalContent = if ($null -ne $finalTurn) { [string]$finalTurn.content } else { "" }
    $reasons = [Collections.Generic.List[string]]::new()
    $score = 0
    $maximumScore = 0

    if ($Scenario.name -eq "no_tool_rewrite") {
        $maximumScore = 2
        if ($calls.Count -eq 0) { $score++ } else { $reasons.Add("unnecessary_tool_call") }
        if (-not [string]::IsNullOrWhiteSpace($finalContent)) { $score++ } else { $reasons.Add("direct_answer_missing") }
    }
    elseif ($Scenario.name -eq "named_document_multiturn") {
        $maximumScore = 6
        if ($calls.Count -gt 0) { $score++ } else { $reasons.Add("evidence_tool_missing") }
        if (@($calls | Where-Object { $_.function.name -in @("rag_search", "documents_context") }).Count -gt 0) {
            $score++
        } else {
            $reasons.Add("source_retrieval_missing")
        }
        if (@($calls | Where-Object {
            $args = Convert-Arguments $_
            $null -ne $args -and
            $null -ne $args.PSObject.Properties["docRef"] -and
            [string]$args.docRef -eq "HydraulicPumpManual.pdf"
        }).Count -gt 0) {
            $score++
        } else {
            $reasons.Add("exact_docref_not_preserved")
        }
        if ($finalContent -match "(?i)85\s*N[\s\u00B7.-]*m") { $score++ } else { $reasons.Add("answer_value_missing") }
        if ($finalContent -match "(?i)HydraulicPumpManual\.pdf") { $score++ } else { $reasons.Add("answer_file_missing") }
        if ($finalContent -match "(?i)(?:page|p\.)\s*42|HydraulicPumpManual\.pdf[^\r\n]{0,40}42") { $score++ } else { $reasons.Add("answer_page_missing") }
    }
    else {
        $maximumScore = 8
        if ($calls.Count -gt 0) { $score++ } else { $reasons.Add("tool_use_missing") }
        if (@($calls | Where-Object { $_.function.name -eq "rag_search" }).Count -gt 0) {
            $score++
        } else {
            $reasons.Add("rag_search_missing")
        }
        if ($duplicates.Count -eq 0) { $score++ } else { $reasons.Add("duplicate_tool_call") }
        if (-not [string]::IsNullOrWhiteSpace($finalContent)) { $score++ } else { $reasons.Add("final_answer_missing") }
        $days = @("lundi", "mardi", "mercredi", "jeudi", "vendredi")
        if (@($days | Where-Object { $finalContent -match "(?i)\b$_\b" }).Count -eq 5) {
            $score++
        } else {
            $reasons.Add("five_days_missing")
        }
        $mealTypes = @("petit-dejeuner", "dejeuner", "collation", "souper")
        $normalized = $finalContent.Normalize([Text.NormalizationForm]::FormD) -replace "\p{Mn}", ""
        if (@($mealTypes | Where-Object { $normalized -match "(?i)\b$_\b" }).Count -eq 4) {
            $score++
        } else {
            $reasons.Add("four_meal_types_missing")
        }
        $knownIds = 1..20 | ForEach-Object { "S$_" }
        $citedIds = @([regex]::Matches($finalContent, "(?i)\bS(?:[1-9]|1[0-9]|20)\b") | ForEach-Object { $_.Value.ToUpperInvariant() } | Select-Object -Unique)
        if ($citedIds.Count -ge 10) { $score++ } else { $reasons.Add("insufficient_evidence_labels") }
        if ($finalContent -match "(?i)Cuisine/.+\.pdf" -and $finalContent -match "(?i)(?:pages?|p\.)\s*\d+") {
            $score++
        } else {
            $reasons.Add("file_page_citations_missing")
        }
    }

    [pscustomobject]@{
        score = $score
        maximumScore = $maximumScore
        reasons = @($reasons)
        totalToolCalls = $calls.Count
        duplicateToolCallCount = $duplicates.Count
        finalAnswerChars = $finalContent.Length
    }
}

$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
$safeName = (($ProfileName -replace "[^A-Za-z0-9._-]", "-").Trim("-"))
if ([string]::IsNullOrWhiteSpace($safeName)) {
    $safeName = "qwen3-native-agent-loop"
}
$artifactPath = Join-Path $resolvedOutputDirectory "$safeName.json"

$models = Invoke-RestMethod -Method Get -Uri "$BaseUrl/v1/models" -TimeoutSec 10
$props = Invoke-RestMethod -Method Get -Uri "$BaseUrl/props" -TimeoutSec 10
$modelId = [string]$models.data[0].id
$tools = @(Get-AgentTools)
$scenarioResults = [Collections.Generic.List[object]]::new()
$startedAt = Get-Date

$systemPrompt = @"
Tu es l'orchestrateur semantique principal d'un assistant RAG.
Tu connais les outils disponibles et choisis librement de n'en appeler aucun,
d'en appeler un ou plusieurs, selon la demande, les observations deja recues
et ce qu'il reste a verifier. Aucun nombre ni ordre d'outils n'est impose.
Ne repete pas une action identique sans raison. La memoire personnalise mais ne
prouve rien. Pour toute affirmation documentaire, utilise seulement les preuves
actuelles et conserve exactement evidenceId, fichier et page. Quand les preuves
sont suffisantes, reponds directement a l'utilisateur avec des citations
verifiables. Si elles ne le sont pas, poursuis la recherche ou explique la limite.
"@.Trim()

foreach ($scenario in Get-Scenarios) {
    Write-Output "NATIVE_AGENT_SCENARIO_START name=$($scenario.name)"
    $messages = [Collections.Generic.List[object]]::new()
    $messages.Add([ordered]@{ role = "system"; content = $systemPrompt })
    $messages.Add([ordered]@{ role = "user"; content = $scenario.user })
    $turns = [Collections.Generic.List[object]]::new()
    $scenarioWatch = [Diagnostics.Stopwatch]::StartNew()
    $requestError = $null

    for ($turn = 1; $turn -le $scenario.maximumTurns; $turn++) {
        try {
            $response = Invoke-AgentCompletion `
                -ModelId $modelId `
                -Messages @($messages) `
                -Tools $tools `
                -MaxTokens $scenario.maxTokens
            $message = $response.choices[0].message
            $calls = @(Get-ToolCalls $message)
            $turns.Add([pscustomobject]@{
                turn = $turn
                finishReason = [string]$response.choices[0].finish_reason
                promptTokens = [int]$response.usage.prompt_tokens
                completionTokens = [int]$response.usage.completion_tokens
                content = if ($null -eq $message.content) { "" } else { [string]$message.content }
                toolCalls = $calls
            })

            if ($calls.Count -eq 0) {
                break
            }

            $messages.Add((New-AssistantHistoryMessage $message))
            foreach ($call in $calls) {
                $toolResult = Invoke-MockedTool -ScenarioName $scenario.name -Call $call
                $messages.Add([ordered]@{
                    role = "tool"
                    tool_call_id = [string]$call.id
                    name = [string]$call.function.name
                    content = ($toolResult | ConvertTo-Json -Depth 30 -Compress)
                })
            }
        }
        catch {
            $requestError = Get-RequestError $_
            break
        }
    }

    $scenarioWatch.Stop()
    $measurement = Measure-Scenario -Scenario $scenario -Turns @($turns)
    $scenarioResults.Add([pscustomobject]@{
        name = $scenario.name
        succeeded = [string]::IsNullOrWhiteSpace($requestError)
        elapsedMs = [int]$scenarioWatch.ElapsedMilliseconds
        score = $measurement.score
        maximumScore = $measurement.maximumScore
        reasons = @($measurement.reasons)
        totalToolCalls = $measurement.totalToolCalls
        duplicateToolCallCount = $measurement.duplicateToolCallCount
        finalAnswerChars = $measurement.finalAnswerChars
        error = $requestError
        turns = @($turns)
    })
    Write-Output "NATIVE_AGENT_SCENARIO_DONE name=$($scenario.name) score=$($measurement.score)/$($measurement.maximumScore) calls=$($measurement.totalToolCalls)"
}

$totalScore = ($scenarioResults | Measure-Object -Property score -Sum).Sum
$maximumScore = ($scenarioResults | Measure-Object -Property maximumScore -Sum).Sum
$artifact = [ordered]@{
    schemaVersion = 1
    capturedAt = (Get-Date).ToUniversalTime().ToString("o")
    profileName = $ProfileName
    baseUrl = $BaseUrl
    modelId = $modelId
    generation = [ordered]@{
        temperature = $Temperature
        topP = $TopP
        topK = $TopK
        minP = $MinP
        presencePenalty = 0.0
        frequencyPenalty = 0.0
        seed = $Seed
    }
    runtime = [ordered]@{
        contextSize = [int]$props.default_generation_settings.n_ctx
        chatTemplateCapabilities = $props.chat_template_caps
    }
    elapsedMs = [int]((Get-Date) - $startedAt).TotalMilliseconds
    scenarioCount = $scenarioResults.Count
    successfulScenarioCount = @($scenarioResults | Where-Object succeeded).Count
    fullyCorrectScenarioCount = @($scenarioResults | Where-Object { $_.score -eq $_.maximumScore }).Count
    totalScore = $totalScore
    maximumScore = $maximumScore
    scoreRatio = [math]::Round($totalScore / [math]::Max(1, $maximumScore), 4)
    results = $scenarioResults
}

$artifact | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $artifactPath -Encoding UTF8
$artifact | ConvertTo-Json -Depth 40
