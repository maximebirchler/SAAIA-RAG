[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LlamaServerPath,

    [Parameter(Mandatory = $true)]
    [string]$ModelPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$ProfileName = "model-quality-suite",
    [int]$Port = 18345,
    [int]$GpuLayers = 65,
    [int]$ContextSize = 4096,
    [int]$BatchSize = 1024,
    [int]$UbatchSize = 256,
    [int]$Threads = 4,
    [ValidateSet("on", "off", "auto")]
    [string]$FlashAttention = "on",
    [ValidateSet("f16", "q8_0", "q4_0")]
    [string]$CacheTypeK = "f16",
    [ValidateSet("f16", "q8_0", "q4_0")]
    [string]$CacheTypeV = "f16",
    [switch]$SwaFull,
    [ValidateRange(0.0, 2.0)]
    [double]$Temperature = 0.0,
    [ValidateRange(0.0, 1.0)]
    [double]$TopP = 1.0,
    [ValidateRange(0, 200)]
    [int]$TopK = 40,
    [ValidateRange(0.0, 1.0)]
    [double]$MinP = 0.0,
    [ValidateRange(0.5, 8.0)]
    [double]$MaxTokenScale = 1.0,
    [int]$Seed = 42,
    [ValidateSet("native", "merge-system-into-user")]
    [string]$MessageMode = "native",
    [ValidateSet("json-schema", "prompted-json")]
    [string]$OutputMode = "json-schema",
    [string[]]$ScenarioNames = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Wait-LlamaReady {
    param(
        [string]$BaseUrl,
        [int]$TimeoutSeconds = 120
    )

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

function Test-TextContains {
    param(
        [AllowNull()]
        [object]$Value,
        [string]$Needle
    )

    return ([string]$Value).IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function New-Evaluation {
    param(
        [double]$Score,
        [double]$MaximumScore,
        [string[]]$Reasons
    )

    return [pscustomobject]@{
        score = $Score
        maximumScore = $MaximumScore
        reasons = @($Reasons)
    }
}

function New-ScenarioDefinitions {
    $sourceProperties = [ordered]@{
        file = [ordered]@{ type = "string" }
        page = [ordered]@{ type = "integer" }
    }
    $sourceRequired = @("file", "page")

    $simpleRag = [pscustomobject]@{
        name = "simple_rag_exact_source"
        maxTokens = 160
        maximumScore = 4
        system = @"
You are the semantic decision maker in a source-backed RAG assistant.
Answer only from the evidence supplied by the user. Select the evidence that
directly answers the question, preserve its mechanical file and page location,
and do not cite distractors. Return only the requested JSON object.
"@.Trim()
        user = @"
QUESTION: What tightening torque is specified for the HX-42 pump cover bolts?

EVIDENCE:
[E1] FILE=HydraulicPumpManual.pdf PAGE=17
HX-42 pump cover bolts: tighten uniformly to 42 N-m.

[E2] FILE=HydraulicPumpManual.pdf PAGE=19
HX-41 inspection cover bolts: tighten to 28 N-m.

[E3] FILE=SafetyPoster.pdf PAGE=2
Wear eye protection during maintenance.
"@.Trim()
        schema = [ordered]@{
            type = "object"
            properties = [ordered]@{
                answer = [ordered]@{ type = "string" }
                evidenceIds = [ordered]@{
                    type = "array"
                    items = [ordered]@{
                        type = "string"
                        enum = @("E1", "E2", "E3")
                    }
                    minItems = 1
                    maxItems = 3
                }
                source = [ordered]@{
                    type = "object"
                    properties = $sourceProperties
                    required = $sourceRequired
                    additionalProperties = $false
                }
            }
            required = @("answer", "evidenceIds", "source")
            additionalProperties = $false
        }
        evaluate = {
            param($parsed)
            $score = 0
            $reasons = [System.Collections.Generic.List[string]]::new()
            $answer = [string]$parsed.answer
            if ((Test-TextContains $answer "42") -and
                ((Test-TextContains $answer "N") -or (Test-TextContains $answer "newton"))) {
                $score++
            } else {
                $reasons.Add("answer_value_or_unit_wrong")
            }
            $ids = @($parsed.evidenceIds | ForEach-Object { [string]$_ })
            if ($ids.Count -eq 1 -and $ids[0] -eq "E1") {
                $score++
            } else {
                $reasons.Add("evidence_selection_wrong")
            }
            if ([string]$parsed.source.file -eq "HydraulicPumpManual.pdf") {
                $score++
            } else {
                $reasons.Add("source_file_wrong")
            }
            if ([int]$parsed.source.page -eq 17) {
                $score++
            } else {
                $reasons.Add("source_page_wrong")
            }
            New-Evaluation $score 4 $reasons
        }
    }

    $conflictingRag = [pscustomobject]@{
        name = "simple_rag_revision_conflict"
        maxTokens = 180
        maximumScore = 5
        system = @"
You are the semantic decision maker in a source-backed RAG assistant.
Resolve explicit document revision conflicts from the supplied evidence.
Answer from the currently applicable source, preserve its mechanical file and
page, and do not cite superseded evidence. Return only the requested JSON.
"@.Trim()
        user = @"
QUESTION: What is the currently applicable tightening torque for the HX-42
pump cover bolts?

EVIDENCE:
[E1] FILE=HydraulicPumpManual-RevA.pdf PAGE=17 STATUS=SUPERSEDED
Revision A specified 42 N-m for HX-42 pump cover bolts.

[E2] FILE=ServiceBulletin-HX42-RevC.pdf PAGE=4 STATUS=CURRENT
Revision C replaces the Rev A value. HX-42 pump cover bolts must be tightened
uniformly to 46 N-m.

[E3] FILE=HydraulicPumpManual-RevA.pdf PAGE=19 STATUS=SUPERSEDED
HX-41 inspection cover bolts were specified at 28 N-m.
"@.Trim()
        schema = [ordered]@{
            type = "object"
            properties = [ordered]@{
                answer = [ordered]@{ type = "string" }
                evidenceIds = [ordered]@{
                    type = "array"
                    items = [ordered]@{
                        type = "string"
                        enum = @("E1", "E2", "E3")
                    }
                    minItems = 1
                    maxItems = 3
                }
                source = [ordered]@{
                    type = "object"
                    properties = $sourceProperties
                    required = $sourceRequired
                    additionalProperties = $false
                }
            }
            required = @("answer", "evidenceIds", "source")
            additionalProperties = $false
        }
        evaluate = {
            param($parsed)
            $score = 0
            $reasons = [System.Collections.Generic.List[string]]::new()
            $answer = [string]$parsed.answer
            if ((Test-TextContains $answer "46") -and
                ((Test-TextContains $answer "N") -or (Test-TextContains $answer "newton"))) {
                $score++
            } else {
                $reasons.Add("current_answer_value_or_unit_wrong")
            }
            $ids = @($parsed.evidenceIds | ForEach-Object { [string]$_ })
            if ($ids -contains "E2") {
                $score++
            } else {
                $reasons.Add("current_evidence_missing")
            }
            if ($ids -notcontains "E1" -and $ids -notcontains "E3") {
                $score++
            } else {
                $reasons.Add("superseded_evidence_cited")
            }
            if ([string]$parsed.source.file -eq "ServiceBulletin-HX42-RevC.pdf") {
                $score++
            } else {
                $reasons.Add("current_source_file_wrong")
            }
            if ([int]$parsed.source.page -eq 4) {
                $score++
            } else {
                $reasons.Add("current_source_page_wrong")
            }
            New-Evaluation $score 5 $reasons
        }
    }

    $sourceAssignment = [pscustomobject]@{
        name = "slot_source_assignment"
        maxTokens = 260
        maximumScore = 7
        system = @"
You are the semantic evidence selector for a source-backed meal planner.
Choose the item whose evidence is semantically suitable for each requested
slot. Preserve the evidence identifier, file and page exactly. Do not use a
warning or a breakfast recipe for dinner. Return only the requested JSON.
"@.Trim()
        user = @"
REQUESTED_SLOTS:
- monday_breakfast
- monday_dinner

EVIDENCE:
[E1] FILE=FamilyMeals.pdf PAGE=12
Red lentil and vegetable stew. A complete evening meal served hot.

[E2] FILE=KitchenSafety.pdf PAGE=3
Never heat a sealed container in a microwave.

[E3] FILE=Breakfasts.pdf PAGE=7
Apple-cinnamon overnight oats. Prepare the night before for breakfast.
"@.Trim()
        schema = [ordered]@{
            type = "object"
            properties = [ordered]@{
                assignments = [ordered]@{
                    type = "array"
                    minItems = 2
                    maxItems = 2
                    items = [ordered]@{
                        type = "object"
                        properties = [ordered]@{
                            slot = [ordered]@{
                                type = "string"
                                enum = @("monday_breakfast", "monday_dinner")
                            }
                            item = [ordered]@{ type = "string" }
                            evidenceId = [ordered]@{
                                type = "string"
                                enum = @("E1", "E2", "E3")
                            }
                            file = [ordered]@{ type = "string" }
                            page = [ordered]@{ type = "integer" }
                        }
                        required = @("slot", "item", "evidenceId", "file", "page")
                        additionalProperties = $false
                    }
                }
            }
            required = @("assignments")
            additionalProperties = $false
        }
        evaluate = {
            param($parsed)
            $score = 0
            $reasons = [System.Collections.Generic.List[string]]::new()
            $assignments = @($parsed.assignments)
            $breakfast = $assignments |
                Where-Object { [string]$_.slot -eq "monday_breakfast" } |
                Select-Object -First 1
            $dinner = $assignments |
                Where-Object { [string]$_.slot -eq "monday_dinner" } |
                Select-Object -First 1

            if ($assignments.Count -eq 2 -and $null -ne $breakfast -and $null -ne $dinner) {
                $score++
            } else {
                $reasons.Add("slot_coverage_wrong")
            }
            if ($null -ne $breakfast -and [string]$breakfast.evidenceId -eq "E3") {
                $score++
            } else {
                $reasons.Add("breakfast_evidence_wrong")
            }
            if ($null -ne $breakfast -and [string]$breakfast.file -eq "Breakfasts.pdf") {
                $score++
            } else {
                $reasons.Add("breakfast_file_wrong")
            }
            if ($null -ne $breakfast -and [int]$breakfast.page -eq 7) {
                $score++
            } else {
                $reasons.Add("breakfast_page_wrong")
            }
            if ($null -ne $dinner -and [string]$dinner.evidenceId -eq "E1") {
                $score++
            } else {
                $reasons.Add("dinner_evidence_wrong")
            }
            if ($null -ne $dinner -and [string]$dinner.file -eq "FamilyMeals.pdf") {
                $score++
            } else {
                $reasons.Add("dinner_file_wrong")
            }
            if ($null -ne $dinner -and [int]$dinner.page -eq 12) {
                $score++
            } else {
                $reasons.Add("dinner_page_wrong")
            }
            New-Evaluation $score 7 $reasons
        }
    }

    $memoryPolicy = [pscustomobject]@{
        name = "memory_relevance_and_supersession"
        maxTokens = 180
        maximumScore = 5
        system = @"
You are the LLM orchestrator responsible for deciding which memories are
relevant. Apply current explicit constraints, ignore superseded preferences,
and report the memory identifiers actually used. Return only JSON.
"@.Trim()
        user = @"
CURRENT_REQUEST:
Create a meal plan that respects my current dietary constraints.

MEMORIES:
[M1] Old preference from 2024: the user likes salmon twice per week.
[M2] Current preference from 2026: the user follows a vegetarian diet.
[M3] Current medical-style food constraint: the user is lactose intolerant.

M2 explicitly supersedes incompatible older food preferences.
"@.Trim()
        schema = [ordered]@{
            type = "object"
            properties = [ordered]@{
                appliedMemoryIds = [ordered]@{
                    type = "array"
                    items = [ordered]@{
                        type = "string"
                        enum = @("M1", "M2", "M3")
                    }
                    minItems = 1
                    maxItems = 3
                }
                dietarySummary = [ordered]@{
                    type = "string"
                    minLength = 3
                    maxLength = 320
                }
                excludedItems = [ordered]@{
                    type = "array"
                    items = [ordered]@{
                        type = "string"
                        minLength = 1
                        maxLength = 120
                    }
                    minItems = 1
                    maxItems = 8
                }
            }
            required = @("appliedMemoryIds", "dietarySummary", "excludedItems")
            additionalProperties = $false
        }
        evaluate = {
            param($parsed)
            $score = 0
            $reasons = [System.Collections.Generic.List[string]]::new()
            $ids = @($parsed.appliedMemoryIds | ForEach-Object { [string]$_ })
            if ($ids -contains "M2") { $score++ } else { $reasons.Add("vegetarian_memory_missing") }
            if ($ids -contains "M3") { $score++ } else { $reasons.Add("lactose_memory_missing") }
            if ($ids -notcontains "M1") { $score++ } else { $reasons.Add("superseded_memory_applied") }
            $summary = [string]$parsed.dietarySummary
            if ((Test-TextContains $summary "veget") -and
                ((Test-TextContains $summary "lactose") -or (Test-TextContains $summary "dairy"))) {
                $score++
            } else {
                $reasons.Add("dietary_summary_incomplete")
            }
            $excluded = (@($parsed.excludedItems) -join " ")
            if ((Test-TextContains $excluded "salmon") -or (Test-TextContains $excluded "fish")) {
                $score++
            } else {
                $reasons.Add("superseded_animal_item_not_excluded")
            }
            New-Evaluation $score 5 $reasons
        }
    }

    $memoryDistractors = [pscustomobject]@{
        name = "memory_recency_with_distractors"
        maxTokens = 220
        maximumScore = 7
        system = @"
You are the LLM orchestrator responsible for deciding which memories are
relevant. Apply current explicit constraints, ignore superseded and unrelated
memories, and report only the memory identifiers actually used. Return JSON.
"@.Trim()
        user = @"
CURRENT_REQUEST:
Create a meal plan that respects my current dietary constraints.

MEMORIES IN NON-CHRONOLOGICAL ORDER:
[M4] Current interface preference: the user wants software menus in French.
[M2] Current preference from 2026: the user follows a vegetarian diet.
[M1] Old preference from 2024: the user likes salmon twice per week.
[M3] Current food constraint: the user is lactose intolerant.

M2 explicitly supersedes incompatible older food preferences. M4 is unrelated
to the requested meal content.
"@.Trim()
        schema = [ordered]@{
            type = "object"
            properties = [ordered]@{
                appliedMemoryIds = [ordered]@{
                    type = "array"
                    items = [ordered]@{
                        type = "string"
                        enum = @("M1", "M2", "M3", "M4")
                    }
                    minItems = 1
                    maxItems = 4
                }
                dietarySummary = [ordered]@{
                    type = "string"
                    minLength = 3
                    maxLength = 320
                }
                excludedItems = [ordered]@{
                    type = "array"
                    items = [ordered]@{
                        type = "string"
                        minLength = 1
                        maxLength = 120
                    }
                    minItems = 1
                    maxItems = 8
                }
            }
            required = @("appliedMemoryIds", "dietarySummary", "excludedItems")
            additionalProperties = $false
        }
        evaluate = {
            param($parsed)
            $score = 0
            $reasons = [System.Collections.Generic.List[string]]::new()
            $ids = @($parsed.appliedMemoryIds | ForEach-Object { [string]$_ })
            if ($ids -contains "M2") { $score++ } else { $reasons.Add("vegetarian_memory_missing") }
            if ($ids -contains "M3") { $score++ } else { $reasons.Add("lactose_memory_missing") }
            if ($ids -notcontains "M1") { $score++ } else { $reasons.Add("superseded_memory_applied") }
            if ($ids -notcontains "M4") { $score++ } else { $reasons.Add("unrelated_memory_applied") }
            $summary = [string]$parsed.dietarySummary
            if ((Test-TextContains $summary "veget") -and
                ((Test-TextContains $summary "lactose") -or (Test-TextContains $summary "dairy"))) {
                $score++
            } else {
                $reasons.Add("dietary_summary_incomplete")
            }
            $excluded = (@($parsed.excludedItems) -join " ")
            if ((Test-TextContains $excluded "salmon") -or (Test-TextContains $excluded "fish")) {
                $score++
            } else {
                $reasons.Add("superseded_animal_item_not_excluded")
            }
            if ((Test-TextContains $excluded "lactose") -or (Test-TextContains $excluded "dairy")) {
                $score++
            } else {
                $reasons.Add("dairy_exclusion_missing")
            }
            New-Evaluation $score 7 $reasons
        }
    }

    $router = [pscustomobject]@{
        name = "llm_orchestration_plan"
        maxTokens = 180
        maximumScore = 7
        system = @"
You are the primary semantic orchestrator. Decide the intent and the sequence
of tools required. Code will only execute tools and enforce mechanical
contracts. A weekly meal plan with personal constraints requires memory,
source retrieval, structured planning and a source-backed writer. Return JSON.
"@.Trim()
        user = @"
USER_QUESTION:
J'ai besoin d'un planning de repas du lundi au vendredi avec petit-dejeuner,
dejeuner, collation et souper, adapte a mes preferences et fonde sur notre base
de recettes.

The request is complete: do not ask a clarification question.
"@.Trim()
        schema = [ordered]@{
            type = "object"
            properties = [ordered]@{
                intent = [ordered]@{
                    type = "string"
                    enum = @("simple_rag", "structured_plan", "no_rag")
                }
                steps = [ordered]@{
                    type = "array"
                    items = [ordered]@{
                        type = "string"
                        enum = @(
                            "memory.read",
                            "rag.search",
                            "planning.compose",
                            "writer.source_backed"
                        )
                    }
                    minItems = 1
                    maxItems = 4
                }
                requiresClarification = [ordered]@{ type = "boolean" }
            }
            required = @("intent", "steps", "requiresClarification")
            additionalProperties = $false
        }
        evaluate = {
            param($parsed)
            $score = 0
            $reasons = [System.Collections.Generic.List[string]]::new()
            if ([string]$parsed.intent -eq "structured_plan") {
                $score++
            } else {
                $reasons.Add("intent_wrong")
            }
            $steps = @($parsed.steps | ForEach-Object { [string]$_ })
            foreach ($required in @(
                "memory.read",
                "rag.search",
                "planning.compose",
                "writer.source_backed"
            )) {
                if ($steps -contains $required) {
                    $score++
                } else {
                    $reasons.Add("missing_$($required.Replace('.', '_'))")
                }
            }
            $expectedOrder = @(
                "memory.read",
                "rag.search",
                "planning.compose",
                "writer.source_backed"
            )
            if (($steps -join "|") -eq ($expectedOrder -join "|")) {
                $score++
            } else {
                $reasons.Add("tool_order_wrong")
            }
            if (-not [bool]$parsed.requiresClarification) {
                $score++
            } else {
                $reasons.Add("unnecessary_clarification")
            }
            New-Evaluation $score 7 $reasons
        }
    }

    $simpleRouter = [pscustomobject]@{
        name = "llm_orchestration_simple_rag"
        maxTokens = 160
        maximumScore = 7
        system = @"
You are the primary semantic orchestrator. Decide the intent and only the tools
required for the request. Code will execute tools and enforce mechanical
contracts. A factual question about a named manual requires source retrieval
and a source-backed writer, but no personal memory or structured planner.
Return JSON.
"@.Trim()
        user = @"
USER_QUESTION:
Selon le manuel HydraulicPumpManual.pdf, quel est le couple de serrage des
boulons du couvercle de la pompe HX-42 ?

The request is complete: do not ask a clarification question.
"@.Trim()
        schema = [ordered]@{
            type = "object"
            properties = [ordered]@{
                intent = [ordered]@{
                    type = "string"
                    enum = @("simple_rag", "structured_plan", "no_rag")
                }
                steps = [ordered]@{
                    type = "array"
                    items = [ordered]@{
                        type = "string"
                        enum = @(
                            "memory.read",
                            "rag.search",
                            "planning.compose",
                            "writer.source_backed"
                        )
                    }
                    minItems = 1
                    maxItems = 4
                }
                requiresClarification = [ordered]@{ type = "boolean" }
            }
            required = @("intent", "steps", "requiresClarification")
            additionalProperties = $false
        }
        evaluate = {
            param($parsed)
            $score = 0
            $reasons = [System.Collections.Generic.List[string]]::new()
            if ([string]$parsed.intent -eq "simple_rag") {
                $score++
            } else {
                $reasons.Add("intent_wrong")
            }
            $steps = @($parsed.steps | ForEach-Object { [string]$_ })
            if ($steps -contains "rag.search") { $score++ } else { $reasons.Add("missing_rag_search") }
            if ($steps -contains "writer.source_backed") { $score++ } else { $reasons.Add("missing_writer_source_backed") }
            if ($steps -notcontains "memory.read") { $score++ } else { $reasons.Add("unnecessary_memory_read") }
            if ($steps -notcontains "planning.compose") { $score++ } else { $reasons.Add("unnecessary_planning_compose") }
            if (($steps -join "|") -eq "rag.search|writer.source_backed") {
                $score++
            } else {
                $reasons.Add("tool_order_or_extra_steps_wrong")
            }
            if (-not [bool]$parsed.requiresClarification) {
                $score++
            } else {
                $reasons.Add("unnecessary_clarification")
            }
            New-Evaluation $score 7 $reasons
        }
    }

    $retrievalFacetPlan = [pscustomobject]@{
        name = "retrieval_plan_four_facets"
        maxTokens = 520
        maximumScore = 27
        system = @"
You are the semantic retrieval planner. Build focused searches for each target
facet supplied by the user. Preserve every target identifier exactly once.
Each query must preserve the active dietary requirements and describe what
should be found in the knowledge base. Do not invent dish names, ingredients,
or answer candidates before retrieval: the searches exist to discover them.
Do not repeat the whole user question and do not ask for clarification when the
request is complete. Return only the requested JSON object.
"@.Trim()
        user = @"
USER_REQUEST:
Prepare a Monday-to-Friday meal plan with breakfast, lunch, snack and dinner.
Every meal must be vegetarian and lactose-free.
The recipe knowledge base is under category Cuisine.

TARGETS:
- breakfast
- lunch
- snack
- dinner
"@.Trim()
        schema = [ordered]@{
            type = "object"
            properties = [ordered]@{
                requiresClarification = [ordered]@{ type = "boolean" }
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
                            toolName = [ordered]@{
                                type = "string"
                                enum = @("rag.search")
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
                        required = @("target", "toolName", "categoryPath", "query")
                        additionalProperties = $false
                    }
                }
            }
            required = @("requiresClarification", "requests")
            additionalProperties = $false
        }
        evaluate = {
            param($parsed)
            $score = 0
            $reasons = [System.Collections.Generic.List[string]]::new()
            if (-not [bool]$parsed.requiresClarification) {
                $score++
            } else {
                $reasons.Add("unnecessary_clarification")
            }

            $requests = @($parsed.requests)
            $targetTerms = @{
                breakfast = @("breakfast", "petit-dejeuner", "petit déjeuner", "morning meal", "repas matinal")
                lunch = @("lunch", "dejeuner", "déjeuner", "midday meal", "repas de midi")
                snack = @("snack", "collation", "gouter", "goûter")
                dinner = @("dinner", "souper", "evening meal", "repas du soir", "repas de soirée")
            }
            $vegetarianTerms = @("vegetarian", "vegan", "sans viande")
            $lactoseFreeTerms = @(
                "lactose-free",
                "lactose free",
                "without lactose",
                "sans lactose",
                "dairy-free",
                "dairy free"
            )
            foreach ($target in @("breakfast", "lunch", "snack", "dinner")) {
                $matches = @(
                    $requests |
                        Where-Object { [string]$_.target -eq $target }
                )
                if ($matches.Count -eq 1) {
                    $score++
                } else {
                    $reasons.Add("target_coverage_$target")
                    continue
                }

                $request = $matches[0]
                if (
                    [string]$request.toolName -eq "rag.search" -and
                    [string]$request.categoryPath -eq "Cuisine"
                ) {
                    $score++
                } else {
                    $reasons.Add("tool_or_category_$target")
                }

                $query = [string]$request.query
                if (@($targetTerms[$target] | Where-Object { Test-TextContains $query $_ }).Count -gt 0) {
                    $score++
                } else {
                    $reasons.Add("query_semantics_$target")
                }

                if (@($vegetarianTerms | Where-Object { Test-TextContains $query $_ }).Count -gt 0) {
                    $score++
                } else {
                    $reasons.Add("query_dropped_vegetarian_constraint_$target")
                }

                if (@($lactoseFreeTerms | Where-Object { Test-TextContains $query $_ }).Count -gt 0) {
                    $score++
                } else {
                    $reasons.Add("query_dropped_lactose_free_constraint_$target")
                }

                $queryWordCount = [regex]::Matches(
                    $query,
                    "[\p{L}\p{N}]+(?:[-'][\p{L}\p{N}]+)*"
                ).Count
                $looksLikeCandidateEnumeration =
                    $query.Contains(":") -or
                    $query.Contains(",") -or
                    $query.Contains(";")
                if (
                    $queryWordCount -ge 3 -and
                    $queryWordCount -le 12 -and
                    -not $looksLikeCandidateEnumeration
                ) {
                    $score++
                } else {
                    $reasons.Add("query_not_focused_or_invents_candidates_$target")
                }
            }

            $normalizedQueries = @(
                $requests |
                    ForEach-Object { ([string]$_.query).Trim().ToLowerInvariant() }
            )
            if (@($normalizedQueries | Select-Object -Unique).Count -eq 4) {
                $score++
            } else {
                $reasons.Add("duplicate_queries")
            }
            if ($requests.Count -eq 4) {
                $score++
            } else {
                $reasons.Add("request_count_wrong")
            }
            New-Evaluation $score 27 $reasons
        }
    }

    $evidenceSufficiency = [pscustomobject]@{
        name = "evidence_sufficiency_for_reusable_grid"
        maxTokens = 360
        maximumScore = 7
        system = @"
You are the semantic evidence judge for a source-backed planner. Decide whether
the supplied evidence is sufficient. The same suitable item may be reused on
different days, so one valid item per requested slot is enough. Select only
evidence that satisfies the active requirements. Return only the requested JSON.
"@.Trim()
        user = @"
REQUEST:
Build a Monday-to-Friday plan with breakfast, lunch, snack and dinner.

ACTIVE REQUIREMENTS:
- Vegetarian.
- Lactose-free.

EVIDENCE:
[E_B1] Breakfast: apple-cinnamon overnight oats, vegetarian and lactose-free.
[E_B2] Breakfast: Greek yogurt with berries, contains dairy.
[E_L1] Lunch: lentil and roasted vegetable salad, vegetarian and lactose-free.
[E_L2] Lunch: tuna sandwich, contains fish.
[E_S1] Snack: apple with almonds, vegetarian and lactose-free.
[E_S2] Snack: cheese cubes with crackers, contains dairy.
[E_D1] Dinner: chickpea and spinach curry, vegetarian and lactose-free.
[E_D2] Dinner: salmon with cream sauce, contains fish and dairy.
"@.Trim()
        schema = [ordered]@{
            type = "object"
            properties = [ordered]@{
                decision = [ordered]@{
                    type = "string"
                    enum = @("answer", "need_more_evidence", "clarify")
                }
                selectedEvidenceIds = [ordered]@{
                    type = "array"
                    items = [ordered]@{
                        type = "string"
                        enum = @("E_B1", "E_B2", "E_L1", "E_L2", "E_S1", "E_S2", "E_D1", "E_D2")
                    }
                    minItems = 1
                    maxItems = 8
                }
                missingEvidenceNotes = [ordered]@{
                    type = "array"
                    items = [ordered]@{ type = "string" }
                    maxItems = 8
                }
            }
            required = @("decision", "selectedEvidenceIds", "missingEvidenceNotes")
            additionalProperties = $false
        }
        evaluate = {
            param($parsed)
            $score = 0
            $reasons = [System.Collections.Generic.List[string]]::new()
            if ([string]$parsed.decision -eq "answer") {
                $score++
            } else {
                $reasons.Add("decision_wrong")
            }

            $ids = @($parsed.selectedEvidenceIds | ForEach-Object { [string]$_ })
            foreach ($required in @("E_B1", "E_L1", "E_S1", "E_D1")) {
                if ($ids -contains $required) {
                    $score++
                } else {
                    $reasons.Add("missing_$required")
                }
            }
            if (@($ids | Where-Object { $_ -in @("E_B2", "E_L2", "E_S2", "E_D2") }).Count -eq 0) {
                $score++
            } else {
                $reasons.Add("incompatible_evidence_selected")
            }
            if (@($parsed.missingEvidenceNotes).Count -eq 0) {
                $score++
            } else {
                $reasons.Add("invented_missing_evidence")
            }
            New-Evaluation $score 7 $reasons
        }
    }

    $mealGrid = [pscustomobject]@{
        name = "meal_plan_twenty_cells"
        maxTokens = 1200
        maximumScore = 62
        system = @"
You are the semantic planner for a source-backed weekly meal plan.
Fill every day and slot exactly once. Use only evidence that matches the slot's
meal type. Reuse is allowed when necessary, but every cell must carry one valid
evidence identifier. Return only the JSON object required by the schema.
"@.Trim()
        user = @"
Build a Monday-to-Friday plan with breakfast, lunch, snack and dinner each day.

ACTIVE USER REQUIREMENTS:
- Vegetarian.
- Lactose-free.

SOURCE-BACKED ITEM POOL:
[E_B1] Apple-cinnamon overnight oats - breakfast, vegetarian and lactose-free.
[E_B2] Banana-peanut oatmeal - breakfast, vegetarian and lactose-free.
[E_B3] Greek yogurt with berries - breakfast, contains dairy.
[E_L1] Lentil and roasted vegetable salad - lunch, vegetarian and lactose-free.
[E_L2] Hummus and grilled vegetable wrap - lunch, vegetarian and lactose-free.
[E_L3] Tuna salad sandwich - lunch, contains fish.
[E_S1] Apple with almonds - snack, vegetarian and lactose-free.
[E_S2] Carrot sticks with hummus - snack, vegetarian and lactose-free.
[E_S3] Cheese cubes with crackers - snack, contains dairy.
[E_D1] Chickpea and spinach curry - dinner, vegetarian and lactose-free.
[E_D2] Three-bean vegetable chili - dinner, vegetarian and lactose-free.
[E_D3] Salmon with cream sauce - dinner, contains fish and dairy.

Use the exact English day and slot values required by the schema.
For title, copy exactly the meal title between the evidence identifier and
the slot description. Do not put the evidence identifier or slot in title.
"@.Trim()
        schema = [ordered]@{
            type = "object"
            properties = [ordered]@{
                cells = [ordered]@{
                    type = "array"
                    minItems = 20
                    maxItems = 20
                    items = [ordered]@{
                        type = "object"
                        properties = [ordered]@{
                            day = [ordered]@{
                                type = "string"
                                enum = @("Monday", "Tuesday", "Wednesday", "Thursday", "Friday")
                            }
                            slot = [ordered]@{
                                type = "string"
                                enum = @("breakfast", "lunch", "snack", "dinner")
                            }
                            title = [ordered]@{ type = "string" }
                            evidenceId = [ordered]@{
                                type = "string"
                                enum = @(
                                    "E_B1", "E_B2", "E_B3",
                                    "E_L1", "E_L2", "E_L3",
                                    "E_S1", "E_S2", "E_S3",
                                    "E_D1", "E_D2", "E_D3"
                                )
                            }
                        }
                        required = @("day", "slot", "title", "evidenceId")
                        additionalProperties = $false
                    }
                }
            }
            required = @("cells")
            additionalProperties = $false
        }
        evaluate = {
            param($parsed)
            $score = 0
            $reasons = [System.Collections.Generic.List[string]]::new()
            $cells = @($parsed.cells)
            $days = @("Monday", "Tuesday", "Wednesday", "Thursday", "Friday")
            $slots = @("breakfast", "lunch", "snack", "dinner")
            $allowed = @{
                breakfast = @("E_B1", "E_B2")
                lunch = @("E_L1", "E_L2")
                snack = @("E_S1", "E_S2")
                dinner = @("E_D1", "E_D2")
            }
            $expectedTitles = @{
                E_B1 = "Apple-cinnamon overnight oats"
                E_B2 = "Banana-peanut oatmeal"
                E_B3 = "Greek yogurt with berries"
                E_L1 = "Lentil and roasted vegetable salad"
                E_L2 = "Hummus and grilled vegetable wrap"
                E_L3 = "Tuna salad sandwich"
                E_S1 = "Apple with almonds"
                E_S2 = "Carrot sticks with hummus"
                E_S3 = "Cheese cubes with crackers"
                E_D1 = "Chickpea and spinach curry"
                E_D2 = "Three-bean vegetable chili"
                E_D3 = "Salmon with cream sauce"
            }

            $seen = [System.Collections.Generic.HashSet[string]]::new(
                [StringComparer]::Ordinal)
            foreach ($day in $days) {
                foreach ($slot in $slots) {
                    $cell = $cells |
                        Where-Object {
                            [string]$_.day -eq $day -and [string]$_.slot -eq $slot
                        } |
                        Select-Object -First 1
                    if ($null -ne $cell) {
                        $score++
                        $seen.Add("$day|$slot") | Out-Null
                    } else {
                        $reasons.Add("missing_$($day.ToLowerInvariant())_$slot")
                        continue
                    }

                    if ($allowed[$slot] -contains [string]$cell.evidenceId) {
                        $score++
                    } else {
                        $reasons.Add("wrong_evidence_$($day.ToLowerInvariant())_$slot")
                    }

                    $evidenceId = [string]$cell.evidenceId
                    $expectedTitle = [string]$expectedTitles[$evidenceId]
                    if (
                        -not [string]::IsNullOrWhiteSpace($expectedTitle) -and
                        [string]::Equals(
                            ([string]$cell.title).Trim(),
                            $expectedTitle,
                            [StringComparison]::Ordinal)
                    ) {
                        $score++
                    } else {
                        $reasons.Add("title_source_mismatch_$($day.ToLowerInvariant())_$slot")
                    }
                }
            }
            if ($cells.Count -eq 20 -and $seen.Count -eq 20) {
                $score++
            } else {
                $reasons.Add("cell_count_or_uniqueness_wrong")
            }
            if (@($cells | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.title) }).Count -eq 0) {
                $score++
            } else {
                $reasons.Add("empty_title")
            }
            New-Evaluation $score 62 $reasons
        }
    }

    $scenarios = @(
        $simpleRag,
        $conflictingRag,
        $sourceAssignment,
        $memoryPolicy,
        $memoryDistractors,
        $router,
        $simpleRouter,
        $retrievalFacetPlan,
        $evidenceSufficiency,
        $mealGrid)
    if ($ScenarioNames.Count -gt 0) {
        $requestedNames = @(
            $ScenarioNames |
                ForEach-Object { $_ -split "," } |
                ForEach-Object { $_.Trim() } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        )
        $scenarios = @($scenarios | Where-Object { $_.name -in $requestedNames })
        if ($scenarios.Count -eq 0) {
            throw "None of the requested ScenarioNames matched a known scenario."
        }
    }

    return $scenarios
}

function Invoke-Scenario {
    param(
        [string]$BaseUrl,
        [string]$ModelId,
        [object]$Scenario
    )

    $messages = if ($MessageMode -eq "merge-system-into-user") {
        @(
            [ordered]@{
                role = "user"
                content = "SYSTEM INSTRUCTIONS:`n$($Scenario.system)`n`nUSER REQUEST:`n$($Scenario.user)"
            }
        )
    } else {
        @(
            [ordered]@{ role = "system"; content = $Scenario.system },
            [ordered]@{ role = "user"; content = $Scenario.user }
        )
    }
    if ($OutputMode -eq "prompted-json") {
        $schemaJson = $Scenario.schema | ConvertTo-Json -Depth 40 -Compress
        $messages[-1].content = [string]$messages[-1].content +
            "`n`nOUTPUT_JSON_SCHEMA:`n$schemaJson`nReturn one JSON object only."
    }

    $effectiveMaxTokens = [math]::Max(
        1,
        [int][math]::Ceiling([double]$Scenario.maxTokens * $MaxTokenScale))
    $payload = [ordered]@{
        model = $ModelId
        messages = $messages
        temperature = $Temperature
        top_p = $TopP
        top_k = $TopK
        min_p = $MinP
        seed = $Seed
        max_tokens = $effectiveMaxTokens
        stream = $false
    }
    if ($OutputMode -eq "json-schema") {
        $payload.response_format = [ordered]@{
            type = "json_schema"
            json_schema = [ordered]@{
                name = $Scenario.name
                strict = $true
                schema = $Scenario.schema
            }
        }
    }
    $body = $payload | ConvertTo-Json -Depth 40 -Compress
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-RestMethod `
            -Method Post `
            -Uri "$BaseUrl/v1/chat/completions" `
            -ContentType "application/json" `
            -Body $body `
            -TimeoutSec 300
        $stopwatch.Stop()
        $content = [string]$response.choices[0].message.content
        try {
            $parsed = $content | ConvertFrom-Json
            $evaluation = & $Scenario.evaluate $parsed
            return [pscustomobject]@{
                name = $Scenario.name
                succeeded = $true
                jsonValid = $true
                elapsedMs = $stopwatch.ElapsedMilliseconds
                maxTokens = $effectiveMaxTokens
                finishReason = [string]$response.choices[0].finish_reason
                promptTokens = [int]$response.usage.prompt_tokens
                completionTokens = [int]$response.usage.completion_tokens
                score = $evaluation.score
                maximumScore = $evaluation.maximumScore
                scoreRatio = [math]::Round(
                    $evaluation.score / [math]::Max(1, $evaluation.maximumScore),
                    4)
                reasons = $evaluation.reasons
                content = $content
                error = $null
            }
        }
        catch {
            return [pscustomobject]@{
                name = $Scenario.name
                succeeded = $true
                jsonValid = $false
                elapsedMs = $stopwatch.ElapsedMilliseconds
                maxTokens = $effectiveMaxTokens
                finishReason = [string]$response.choices[0].finish_reason
                promptTokens = [int]$response.usage.prompt_tokens
                completionTokens = [int]$response.usage.completion_tokens
                score = 0
                maximumScore = $Scenario.maximumScore
                scoreRatio = 0
                reasons = @("invalid_json")
                content = $content
                error = $_.Exception.Message
            }
        }
    }
    catch {
        $stopwatch.Stop()
        return [pscustomobject]@{
            name = $Scenario.name
            succeeded = $false
            jsonValid = $false
            elapsedMs = $stopwatch.ElapsedMilliseconds
            maxTokens = $effectiveMaxTokens
            finishReason = "request_failed"
            promptTokens = $null
            completionTokens = $null
            score = 0
            maximumScore = $Scenario.maximumScore
            scoreRatio = 0
            reasons = @("request_failed")
            content = $null
            error = $_.Exception.Message
        }
    }
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
if ($Port -lt 1024 -or $Port -gt 65535) {
    throw "Port must be between 1024 and 65535."
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
$safeName = (($ProfileName -replace "[^A-Za-z0-9._-]", "-").Trim("-"))
if ([string]::IsNullOrWhiteSpace($safeName)) {
    $safeName = "model-quality-suite"
}

$stdoutPath = Join-Path $resolvedOutputDirectory "$safeName-server.stdout.log"
$stderrPath = Join-Path $resolvedOutputDirectory "$safeName-server.stderr.log"
$artifactPath = Join-Path $resolvedOutputDirectory "$safeName-quality.json"
$baseUrl = "http://127.0.0.1:$Port"
$arguments = [Collections.Generic.List[string]]::new()
foreach ($pair in @(
    @("--model", $resolvedModelPath),
    @("--host", "127.0.0.1"),
    @("--port", [string]$Port),
    @("--ctx-size", [string]$ContextSize),
    @("--batch-size", [string]$BatchSize),
    @("--ubatch-size", [string]$UbatchSize),
    @("--threads", [string]$Threads),
    @("--threads-batch", [string]$Threads),
    @("--n-gpu-layers", [string]$GpuLayers),
    @("--flash-attn", $FlashAttention),
    @("--split-mode", "none"),
    @("--device", "CUDA0"),
    @("--parallel", "1"),
    @("--cache-type-k", $CacheTypeK),
    @("--cache-type-v", $CacheTypeV),
    @("--metrics", ""),
    @("--no-webui", "")
)) {
    $arguments.Add($pair[0])
    if (-not [string]::IsNullOrEmpty($pair[1])) {
        $arguments.Add($pair[1])
    }
}
if ($SwaFull) {
    $arguments.Add("--swa-full")
}

$process = $null
$startedAt = Get-Date
try {
    $process = Start-Process `
        -FilePath $serverPath `
        -ArgumentList $arguments `
        -WorkingDirectory (Split-Path $serverPath) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru
    $modelsResponse = Wait-LlamaReady -BaseUrl $baseUrl
    $loadMs = [int]((Get-Date) - $startedAt).TotalMilliseconds
    $modelId = [string]$modelsResponse.data[0].id
    $scenarios = New-ScenarioDefinitions
    $results = [Collections.Generic.List[object]]::new()
    foreach ($scenario in $scenarios) {
        Write-Output "QUALITY_SCENARIO_START name=$($scenario.name)"
        $result = Invoke-Scenario -BaseUrl $baseUrl -ModelId $modelId -Scenario $scenario
        $results.Add($result)
        Write-Output (
            "QUALITY_SCENARIO_DONE name={0} score={1}/{2} elapsedMs={3}" -f
            $result.name,
            $result.score,
            $result.maximumScore,
            $result.elapsedMs)
    }

    $totalScore = ($results | Measure-Object -Property score -Sum).Sum
    $maximumScore = ($results | Measure-Object -Property maximumScore -Sum).Sum
    $artifact = [ordered]@{
        schemaVersion = 2
        capturedAt = (Get-Date).ToUniversalTime().ToString("o")
        profileName = $ProfileName
        runtime = [ordered]@{
            executablePath = $serverPath
            modelPath = $resolvedModelPath
            modelId = $modelId
            gpuLayers = $GpuLayers
            contextSize = $ContextSize
            batchSize = $BatchSize
            ubatchSize = $UbatchSize
            threads = $Threads
            flashAttention = $FlashAttention
            cacheTypeK = $CacheTypeK
            cacheTypeV = $CacheTypeV
            swaFull = [bool]$SwaFull
        }
        generation = [ordered]@{
            temperature = $Temperature
            topP = $TopP
            topK = $TopK
            minP = $MinP
            seed = $Seed
            maxTokenScale = $MaxTokenScale
            messageMode = $MessageMode
            outputMode = $OutputMode
        }
        loadMs = $loadMs
        elapsedMs = [int]((Get-Date) - $startedAt).TotalMilliseconds
        scenarioCount = $results.Count
        successfulScenarioCount = @($results | Where-Object { $_.succeeded }).Count
        validJsonScenarioCount = @($results | Where-Object { $_.jsonValid }).Count
        fullyCorrectScenarioCount = @(
            $results | Where-Object { $_.score -eq $_.maximumScore }
        ).Count
        totalScore = $totalScore
        maximumScore = $maximumScore
        scoreRatio = [math]::Round($totalScore / [math]::Max(1, $maximumScore), 4)
        results = $results
        stdoutLog = $stdoutPath
        stderrLog = $stderrPath
    }
    $artifact | ConvertTo-Json -Depth 30 |
        Set-Content -LiteralPath $artifactPath -Encoding UTF8
    $artifact | ConvertTo-Json -Depth 30
}
finally {
    Stop-LlamaServer -Process $process
}
