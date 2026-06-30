using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedEvidencePlannerObservabilityTests
{
    [Fact]
    public void Llm_exploration_parser_marks_planner_origin()
    {
        const string rawJson = """
            {
              "passes": [
                {
                  "label": "Planner Search",
                  "purpose": "find broader candidates",
                  "categoryScope": "Operations",
                  "docId": "doc-1",
                  "pageStart": 4,
                  "pageEnd": 5,
                  "queries": [
                    "maintenance weekly controls",
                    "evening control options"
                  ]
                }
              ]
            }
            """;

        var labels = ToolAgentOrchestrator.ParseSourceBackedLlmEvidenceExplorationPassLabelsForTests(rawJson);
        var queries = ToolAgentOrchestrator.ParseSourceBackedLlmEvidenceExplorationQueriesForTests(rawJson);
        var categories = ToolAgentOrchestrator.ParseSourceBackedLlmEvidenceExplorationCategoriesForTests(rawJson);
        var scopes = ToolAgentOrchestrator.ParseSourceBackedLlmEvidenceExplorationScopesForTests(rawJson);
        var origins = ToolAgentOrchestrator.ParseSourceBackedLlmEvidenceExplorationOriginsForTests(rawJson);

        Assert.Equal(new[] { "planner_search" }, labels);
        Assert.Equal(new[] { "Operations" }, categories);
        Assert.Equal(new[] { "llm_planner" }, origins);
        Assert.Equal(new[] { "maintenance weekly controls", "evening control options" }, queries);
        var scope = Assert.Single(scopes);
        Assert.Equal("doc-1", scope.DocId);
        Assert.Equal(4, scope.PageStart);
        Assert.Equal(5, scope.PageEnd);
    }

    [Fact]
    public void Llm_exploration_parser_rejects_noisy_ocr_sentence_queries()
    {
        const string rawJson = """
            {
              "passes": [
                {
                  "label": "planner search",
                  "queries": [
                    "repas donne parfois l'occasion de voya-ger, de decouvrir d'autres cultures, d'autresman",
                    "repas preparations semaine"
                  ]
                }
              ]
            }
            """;

        var queries = ToolAgentOrchestrator.ParseSourceBackedLlmEvidenceExplorationQueriesForTests(rawJson);

        var query = Assert.Single(queries);
        Assert.Equal("repas preparations semaine", query);
    }

    [Fact]
    public void Llm_exploration_planner_rejects_structured_axis_only_queries()
    {
        const string rawJson = """
            {
              "passes": [
                {
                  "label": "llm strategy",
                  "queries": [
                    "recettes lundi",
                    "recettes mardi",
                    "recettes mercredi",
                    "recettes jeudi",
                    "recettes vendredi",
                    "repas lundi",
                    "repas mardi",
                    "repas mercredi",
                    "repas jeudi",
                    "repas vendredi"
                  ]
                }
              ]
            }
            """;

        var queries = ToolAgentOrchestrator.ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
            rawJson,
            "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.",
            "fr");

        Assert.Empty(queries);
    }

    [Fact]
    public void Router_repair_fallback_preserves_explicit_missing_structured_axes()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.";

        var (queries, missingAfter) = ToolAgentOrchestrator.BuildStructuredRouterSearchAxisFallbackQueriesForTests(
            query,
            "fr",
            "petit dejeuner",
            "repas",
            "diner",
            "dejeuner");

        Assert.Empty(missingAfter);
        Assert.True(queries.Length <= 8);
        Assert.Contains(queries, q => q.Contains("souper", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => q.Contains("collation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Router_repair_fallback_does_not_count_one_global_query_as_slot_coverage()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.";

        var missingBefore = ToolAgentOrchestrator.DetectMissingStructuredRouterSearchAxesForTests(
            query,
            "fr",
            "repas plats",
            "repas recettes",
            "planning repas plats",
            "plan repas semaine lundi vendredi petit dejeuner diner souper gouter");

        Assert.Contains(missingBefore, axis => axis.Contains("petit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(missingBefore, axis => axis.Contains("souper", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(missingBefore, axis => axis.Contains("collation", StringComparison.OrdinalIgnoreCase)
                                             || axis.Contains("gouter", StringComparison.OrdinalIgnoreCase));

        var (queries, missingAfter) = ToolAgentOrchestrator.BuildStructuredRouterSearchAxisFallbackQueriesForTests(
            query,
            "fr",
            "repas plats",
            "repas recettes",
            "planning repas plats",
            "plan repas semaine lundi vendredi petit dejeuner diner souper gouter");

        Assert.Empty(missingAfter);
        Assert.True(queries.Length <= 8);
        Assert.Contains(queries, q => q.Contains("petit", StringComparison.OrdinalIgnoreCase)
                                    && q.Contains("dejeuner", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => q.Contains("souper", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => q.Contains("collation", StringComparison.OrdinalIgnoreCase)
                                    || q.Contains("gouter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Llm_exploration_catalog_category_scope_is_trusted_after_resolution()
    {
        Assert.True(ToolAgentOrchestrator.ShouldTrustSourceBackedExplorationPassCategoryScopeForTests(
            passOrigin: "llm_planner",
            passHasDocumentScope: false,
            resolvedPassCategoryScope: "Cuisine",
            passCategoryScope: "Cuisine",
            categoryScopeTrustedByCurrentEvidence: false,
            passCategoryScopeReusedFromInference: false));

        Assert.False(ToolAgentOrchestrator.ShouldTrustSourceBackedExplorationPassCategoryScopeForTests(
            passOrigin: "deterministic_seed",
            passHasDocumentScope: false,
            resolvedPassCategoryScope: "Cuisine",
            passCategoryScope: "Cuisine",
            categoryScopeTrustedByCurrentEvidence: false,
            passCategoryScopeReusedFromInference: false));

        Assert.False(ToolAgentOrchestrator.ShouldTrustSourceBackedExplorationPassCategoryScopeForTests(
            passOrigin: "llm_planner",
            passHasDocumentScope: true,
            resolvedPassCategoryScope: "Cuisine",
            passCategoryScope: "Cuisine",
            categoryScopeTrustedByCurrentEvidence: false,
            passCategoryScopeReusedFromInference: false));
    }

    [Fact]
    public void Llm_planner_category_decision_is_applied_only_to_unscoped_catalog_passes()
    {
        const string rawJson = """
            {
              "categoryDecision": {
                "categoryScope": "Operations",
                "decision": "use_scope",
                "confidence": "high",
                "reason": "The request matches the operations catalogue scope."
              },
              "passes": [
                {
                  "label": "unscoped catalogue pass",
                  "queries": [ "weekly maintenance controls" ]
                },
                {
                  "label": "already scoped pass",
                  "categoryScope": "Existing",
                  "queries": [ "existing operational checklist" ]
                },
                {
                  "label": "document scoped pass",
                  "docId": "doc-1",
                  "queries": [ "document page controls" ]
                }
              ]
            }
            """;

        var decision = ToolAgentOrchestrator.ParseSourceBackedLlmEvidenceExplorationCategoryDecisionForTests(rawJson);
        var (categories, updatedPassCount) = ToolAgentOrchestrator.ApplySourceBackedLlmCategoryScopeDecisionForTests(rawJson, decision.CategoryScope!);

        Assert.Equal("Operations", decision.CategoryScope);
        Assert.Equal("use_scope", decision.Decision);
        Assert.Equal(1, updatedPassCount);
        Assert.Equal(new[] { "Operations", "Existing", null }, categories);
    }

    [Fact]
    public void Llm_planner_preserves_valid_category_scope_when_structured_axis_queries_are_filtered()
    {
        const string query = "J'ai besoin d'un plan de maintenance du lundi au vendredi avec controles matin, midi et soir.";
        const string rawJson = """
            {
              "categoryDecision": {
                "categoryScope": "Operations",
                "decision": "use_scope",
                "confidence": "high",
                "reason": "Operations is the semantic container for maintenance planning documents."
              },
              "passes": [
                {
                  "label": "axis-only planning queries",
                  "queries": [ "lundi", "mardi", "mercredi", "jeudi", "vendredi" ]
                }
              ]
            }
            """;

        var result = ToolAgentOrchestrator.FilterAndApplySourceBackedLlmCategoryScopeDecisionForTests(
            rawJson,
            query,
            "fr",
            "Operations");

        Assert.True(result.AddedScopeOnlyPass);
        Assert.Equal(0, result.UpdatedPassCount);
        Assert.Equal(0, result.QueryCount);
        Assert.Equal(new[] { "llm_category_scope" }, result.Labels);
        Assert.Equal(new[] { "Operations" }, result.Categories);
    }

    [Fact]
    public void Llm_category_scope_adjudication_runs_only_for_unscoped_passes_with_category_hints()
    {
        const string query = "J'ai besoin d'un plan de maintenance du lundi au vendredi avec controles matin, midi et soir.";
        const string unscopedRawJson = """
            {
              "passes": [
                {
                  "label": "planner search",
                  "queries": [ "maintenance controles", "planning maintenance" ]
                }
              ]
            }
            """;
        const string scopedRawJson = """
            {
              "passes": [
                {
                  "label": "planner search",
                  "categoryScope": "Operations",
                  "queries": [ "maintenance controles", "planning maintenance" ]
                }
              ]
            }
            """;

        Assert.True(ToolAgentOrchestrator.ShouldRunLlmSourceBackedCategoryScopeAdjudicationForTests(
            unscopedRawJson,
            query,
            "fr",
            "- Operations | docs: 12 | lexicalScore: 2"));
        Assert.False(ToolAgentOrchestrator.ShouldRunLlmSourceBackedCategoryScopeAdjudicationForTests(
            scopedRawJson,
            query,
            "fr",
            "- Operations | docs: 12 | lexicalScore: 2"));
        Assert.False(ToolAgentOrchestrator.ShouldRunLlmSourceBackedCategoryScopeAdjudicationForTests(
            unscopedRawJson,
            query,
            "fr",
            "none"));
    }

    [Fact]
    public void Initial_llm_category_scope_adjudication_runs_before_unscoped_source_exploration_only()
    {
        const string query = "J'ai besoin d'un plan de maintenance du lundi au vendredi avec controles matin, midi et soir.";
        const string sourceExplorationArgs = """
            {
              "queries": [ "maintenance controles matin", "maintenance controles soir" ],
              "topK": 8,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """;
        const string alreadyScopedArgs = """
            {
              "queries": [ "maintenance controles matin" ],
              "category": "Operations",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """;
        const string documentScopedArgs = """
            {
              "queries": [ "maintenance controles matin" ],
              "docId": "doc-1",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """;
        const string normalSearchArgs = """
            {
              "queries": [ "maintenance controles matin" ],
              "topK": 8,
              "mode": "balanced"
            }
            """;

        Assert.True(ToolAgentOrchestrator.ShouldRunInitialLlmSourceBackedCategoryScopeAdjudicationForTests(
            sourceExplorationArgs,
            query));
        Assert.False(ToolAgentOrchestrator.ShouldRunInitialLlmSourceBackedCategoryScopeAdjudicationForTests(
            alreadyScopedArgs,
            query));
        Assert.False(ToolAgentOrchestrator.ShouldRunInitialLlmSourceBackedCategoryScopeAdjudicationForTests(
            documentScopedArgs,
            query));
        Assert.False(ToolAgentOrchestrator.ShouldRunInitialLlmSourceBackedCategoryScopeAdjudicationForTests(
            normalSearchArgs,
            query));
        Assert.False(ToolAgentOrchestrator.ShouldRunInitialLlmSourceBackedCategoryScopeAdjudicationForTests(
            sourceExplorationArgs,
            query,
            llmOrigin: false));
    }

    [Fact]
    public void Initial_llm_category_scope_application_marks_category_as_trusted_without_rewriting_queries()
    {
        const string rawArgsJson = """
            {
              "queries": [ "maintenance controles matin", "maintenance controles soir" ],
              "topK": 8,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """;

        var applied = ToolAgentOrchestrator.ApplyResolvedInitialSourceBackedCategoryScopeForTests(
            rawArgsJson,
            "Operations");

        Assert.Equal("Operations", applied.Category);
        Assert.Equal("Operations", applied.CategoryPath);
        Assert.True(applied.TrustCategoryScope);
        Assert.Equal(new[] { "maintenance controles matin", "maintenance controles soir" }, applied.Queries);
    }

    [Fact]
    public void Structured_planning_rejects_sentence_fragment_recap_candidate_titles()
    {
        Assert.True(ToolAgentOrchestrator.LooksLikeNoisyStructuredPlanningCandidateTitleForTests(
            "Voici un récapitulatif qui"));
        Assert.True(ToolAgentOrchestrator.LooksLikeNoisyStructuredPlanningCandidateTitleForTests(
            "Voici un recapitulatif des options"));
        Assert.True(ToolAgentOrchestrator.LooksLikeNoisyStructuredPlanningCandidateTitleForTests(
            "TERMESCHALEUR HUMIDE"));
        Assert.True(ToolAgentOrchestrator.LooksLikeNoisyStructuredPlanningCandidateTitleForTests(
            "Calibrer ajuster en petites valeurs un dispositif"));
        Assert.True(ToolAgentOrchestrator.LooksLikeNoisyStructuredPlanningCandidateTitleForTests(
            "Peler les poires et les couper en tranches"));
        Assert.False(ToolAgentOrchestrator.LooksLikeNoisyStructuredPlanningCandidateTitleForTests(
            "Légumes racines rôtis"));
        Assert.False(ToolAgentOrchestrator.LooksLikeNoisyStructuredPlanningCandidateTitleForTests(
            "Plan de maintenance A"));
    }

    [Fact]
    public void Deterministic_structured_planning_exploration_avoids_decorative_generic_queries()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.";

        var queries = ToolAgentOrchestrator.BuildPlanningExplorationRetrievalQueriesForTests(query);

        Assert.Contains(queries, q => q.Contains("souper", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => q.Contains("gouter", StringComparison.OrdinalIgnoreCase)
                                      || q.Contains("collation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, q => q.Contains("exemples", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, q => q.Contains("idees", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, q => q.Contains("suggestions", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, q => q.Contains("menus", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Llm_exploration_planner_rejects_day_fanout_when_it_misses_requested_slot_coverage()
    {
        const string rawJson = """
            {
              "passes": [
                {
                  "label": "llm strategy",
                  "queries": [
                    "repas options Lundi",
                    "repas options Mardi",
                    "repas options Mercredi",
                    "repas options Jeudi",
                    "repas options Vendredi",
                    "diner options Lundi",
                    "diner options Mardi",
                    "diner options Mercredi",
                    "diner options Jeudi",
                    "diner options Vendredi"
                  ]
                }
              ]
            }
            """;

        var queries = ToolAgentOrchestrator.ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
            rawJson,
            "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.",
            "fr");

        Assert.Empty(queries);
    }

    [Fact]
    public void Llm_exploration_planner_rejects_repetitive_single_slot_known_term_loop()
    {
        const string rawJson = """
            {
              "passes": [
                {
                  "label": "llm strategy",
                  "queries": [
                    "repas exemples",
                    "repas exemples gouter",
                    "repas gouter",
                    "repas gouter options",
                    "repas gouter exemples",
                    "repas gouter exemples gouter",
                    "repas exemples gouter options",
                    "repas exemples gouter exemples",
                    "repas exemples gouter exemples gouter"
                  ]
                }
              ]
            }
            """;

        var queries = ToolAgentOrchestrator.ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
            rawJson,
            "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.",
            "fr");

        Assert.Empty(queries);
    }

    [Fact]
    public void Llm_exploration_planner_rejects_repetitive_generic_detail_variants_without_slot_coverage()
    {
        const string rawJson = """
            {
              "passes": [
                {
                  "label": "llm strategy",
                  "queries": [
                    "repas exemples",
                    "repas details",
                    "repas ideaux",
                    "repas idees",
                    "repas menus",
                    "repas suggestions",
                    "repas menus ideaux",
                    "repas menus idees",
                    "repas menus detailles"
                  ]
                }
              ]
            }
            """;

        var queries = ToolAgentOrchestrator.ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
            rawJson,
            "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.",
            "fr");

        Assert.Empty(queries);
    }

    [Fact]
    public void Llm_exploration_planner_removes_decorative_fillers_but_keeps_slot_queries()
    {
        const string rawJson = """
            {
              "passes": [
                {
                  "label": "llm strategy",
                  "categoryScope": "Domain",
                  "queries": [
                    "repas souper",
                    "repas collation",
                    "repas petit-dejeuner",
                    "repas recettes",
                    "repas menus",
                    "repas idees",
                    "repas suggestions",
                    "repas exemples"
                  ]
                }
              ]
            }
            """;

        var queries = ToolAgentOrchestrator.ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
            rawJson,
            "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.",
            "fr");

        Assert.Contains(queries, query => query.Contains("souper", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, query => query.Contains("collation", StringComparison.OrdinalIgnoreCase)
                                        || query.Contains("gouter", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, query => query.Contains("petit-dejeuner", StringComparison.OrdinalIgnoreCase)
                                        || (query.Contains("petit", StringComparison.OrdinalIgnoreCase)
                                            && query.Contains("dejeuner", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("repas recettes", queries);
        Assert.DoesNotContain("repas menus", queries);
        Assert.DoesNotContain("repas idees", queries);
        Assert.DoesNotContain("repas suggestions", queries);
        Assert.DoesNotContain("repas exemples", queries);
    }

    [Fact]
    public void Llm_exploration_planner_rejects_decorative_examples_even_with_multiple_slots()
    {
        const string rawJson = """
            {
              "passes": [
                {
                  "label": "llm strategy",
                  "queries": [
                    "repas diner exemples",
                    "repas souper exemples",
                    "repas collation exemples",
                    "repas petit-dejeuner exemples",
                    "repas options exemples",
                    "repas exemples",
                    "repas recettes exemples",
                    "repas menus exemples",
                    "repas suggestions exemples",
                    "repas idees exemples"
                  ]
                }
              ]
            }
            """;

        var queries = ToolAgentOrchestrator.ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
            rawJson,
            "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.",
            "fr");

        Assert.Empty(queries);
    }

    [Fact]
    public void Llm_exploration_planner_keeps_balanced_structured_slot_coverage_without_specific_candidates()
    {
        const string rawJson = """
            {
              "passes": [
                {
                  "label": "llm strategy",
                  "queries": [
                    "petit-dejeuner options",
                    "diner options",
                    "souper options",
                    "collation options",
                    "gouter options"
                  ]
                }
              ]
            }
            """;

        var queries = ToolAgentOrchestrator.ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
            rawJson,
            "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.",
            "fr");

        Assert.Equal(
            new[]
            {
                "petit-dejeuner options",
                "diner options",
                "souper options",
                "collation options",
                "gouter options"
            },
            queries);
    }

    [Fact]
    public void Llm_exploration_planner_collapses_day_fanout_when_it_covers_requested_slot_types()
    {
        const string rawJson = """
            {
              "passes": [
                {
                  "label": "llm strategy",
                  "queries": [
                    "petit-dejeuner options Lundi",
                    "petit-dejeuner options Mardi",
                    "diner options Lundi",
                    "diner options Mardi",
                    "souper options Mercredi",
                    "souper options Jeudi",
                    "collation options Jeudi",
                    "collation options Vendredi"
                  ]
                }
              ]
            }
            """;

        var queries = ToolAgentOrchestrator.ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
            rawJson,
            "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.",
            "fr");

        Assert.Equal(
            new[]
            {
                "petit-dejeuner options",
                "diner options",
                "souper options",
                "collation options"
            },
            queries);
    }

    [Fact]
    public void Llm_exploration_planner_keeps_structured_slot_or_candidate_queries()
    {
        const string rawJson = """
            {
              "passes": [
                {
                  "label": "llm strategy",
                  "queries": [
                    "petit-dejeuner recettes",
                    "diner recettes",
                    "souper recettes",
                    "collation gouter",
                    "omelette lundi",
                    "salade mercredi"
                  ]
                }
              ]
            }
            """;

        var queries = ToolAgentOrchestrator.ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
            rawJson,
            "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.",
            "fr");

        Assert.Equal(
            new[]
            {
                "petit-dejeuner recettes",
                "diner recettes",
                "souper recettes",
                "collation gouter",
                "omelette lundi",
                "salade mercredi"
            },
            queries);
    }

    [Fact]
    public void Llm_exploration_parser_converts_router_style_rag_tool_calls()
    {
        const string rawJson = """
            {
              "intent": "rag.answer",
              "toolCalls": [
                {
                  "name": "rag.multi_search",
                  "args": {
                    "queries": ["repas preparations semaine", "options repas"],
                    "category": "Cuisine/PDF",
                    "researchMode": "source_exploration"
                  }
                }
              ]
            }
            """;

        var labels = ToolAgentOrchestrator.ParseSourceBackedLlmEvidenceExplorationPassLabelsForTests(rawJson);
        var queries = ToolAgentOrchestrator.ParseSourceBackedLlmEvidenceExplorationQueriesForTests(rawJson);
        var categories = ToolAgentOrchestrator.ParseSourceBackedLlmEvidenceExplorationCategoriesForTests(rawJson);

        Assert.Equal(new[] { "llm_tool_call" }, labels);
        Assert.Equal(new[] { "repas preparations semaine", "options repas" }, queries);
        Assert.Equal(new[] { "Cuisine/PDF" }, categories);
    }

    [Fact]
    public void Llm_evidence_planner_prompt_keeps_orchestrator_contract()
    {
        var prompt = ToolAgentOrchestrator.BuildSourceBackedLlmEvidenceExplorationSystemPromptForTests("fr");

        Assert.Contains("retrieval strategist", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tools you can orchestrate", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REQUEST_SHAPE", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("minimumCandidates", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only allowed scope values", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("broad category can fit", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not ask the user to broaden the search", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cuisine", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recette", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(prompt.Length < 3600, $"Planner system prompt is too large: {prompt.Length} chars");
    }

    [Fact]
    public void Llm_category_scope_prompt_allows_semantic_container_without_domain_hardcoding()
    {
        var prompt = ToolAgentOrchestrator.BuildSourceBackedLlmCategoryScopeSystemPromptForTests("fr");

        Assert.Contains("retrieval scope adjudicator", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only allowed scope values", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("broad category can fit", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("judge fit semantically", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lexical scores are weak sorting hints", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cuisine", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recette", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(prompt.Length < 2300, $"Category scope system prompt is too large: {prompt.Length} chars");
    }

    [Fact]
    public void Llm_evidence_planner_user_prompt_stays_compact_with_large_catalog()
    {
        var memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot()
        };
        for (var i = 0; i < 120; i++)
        {
            memory.CatalogSnapshotCache.Categories.Add(new ToolMemory.CategorySnapshot
            {
                DisplayName = $"Category {i:000} with a deliberately long display name",
                CategoryPath = $"Root/Department {i:000}/Very Long Category Path For Prompt Budget Checks",
                CategoryRef = $"cat-{i:000}",
                Ordinal = i,
                TotalDocuments = i + 1,
                Aliases = new() { $"alias-{i:000}-one", $"alias-{i:000}-two", $"alias-{i:000}-three" }
            });
        }

        var prompt = ToolAgentOrchestrator.BuildSourceBackedLlmEvidenceExplorationUserPromptForTests(
            new ToolResults(),
            "Je cherche un plan de repas du lundi au vendredi avec petit-dejeuner, diner, souper et collation a partir des documents disponibles, avec les sources utiles.",
            "fr",
            memory);

        var categoryLines = prompt
            .Split('\n')
            .Count(line => line.TrimStart().StartsWith("- Category ", StringComparison.Ordinal));

        Assert.True(prompt.Length < 8500, $"Planner user prompt is too large: {prompt.Length} chars");
        Assert.InRange(categoryLines, 1, 10);
        Assert.Contains("CATEGORY_HINTS", prompt, StringComparison.Ordinal);
        Assert.Contains("OUTPUT_RULES", prompt, StringComparison.Ordinal);
        Assert.Contains("Return at most 10 queries", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("structuredSearchGuidance", prompt, StringComparison.Ordinal);
        Assert.Contains("day-axis labels are placement targets", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not fan out the same query once per day/row/column", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("semantic container", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("broader than the specific task", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("semanticLabel", prompt, StringComparison.Ordinal);
        Assert.Contains("scopeValue", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Llm_evidence_planner_user_prompt_includes_relevant_working_notes_as_strategy_only()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.";
        var topicKey = ToolAgentOrchestrator.BuildSourceBackedResearchTopicKeyForTests(query, "fr");
        var shapeKey = ToolAgentOrchestrator.BuildSourceBackedResearchShapeKeyForTests(query);
        var memory = new ToolMemory();
        memory.ResearchWorkingNotes.Add(new ToolMemory.ResearchWorkingNote
        {
            TopicKey = topicKey,
            RequestShape = shapeKey,
            Label = "slot_balancing_inventory",
            Origin = "llm_planner",
            Purpose = "broaden weak snack slot coverage",
            Queries = new() { "gouter options", "collation dessert" },
            Outcome = "accepted",
            Accepted = true,
            ReasonAfter = "improved_candidate_coverage",
            CandidateDelta = 4,
            DistinctPageDelta = 3,
            UsableHitDelta = 5
        });
        memory.ResearchWorkingNotes.Add(new ToolMemory.ResearchWorkingNote
        {
            TopicKey = "fr|targeted|inertage procedure",
            RequestShape = "targeted",
            Label = "unrelated",
            Queries = new() { "inertage procedure" },
            Outcome = "accepted",
            Accepted = true
        });

        var prompt = ToolAgentOrchestrator.BuildSourceBackedLlmEvidenceExplorationUserPromptForTests(
            new ToolResults(),
            query,
            "fr",
            memory);

        Assert.Contains("WORKING_NOTES:", prompt);
        Assert.Contains("outcome=accepted", prompt);
        Assert.Contains("slot_balancing_inventory", prompt);
        Assert.Contains("gouter options", prompt);
        Assert.Contains("continue_related_pivot_with_new_facets", prompt);
        Assert.Contains("Do not treat WORKING_NOTES as source evidence", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inertage procedure", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_snapshot_exposes_exploration_pass_origin_and_scope()
    {
        var mem = new ToolMemory
        {
            LastRagQueries = new() { "inertage" },
            Execution =
            {
                LastRagEvidenceExploration = new()
                {
                    new ToolMemory.RagEvidenceExplorationTrace
                    {
                        Label = "planner_search",
                        Origin = "llm_planner",
                        Purpose = "find broader candidates",
                        Queries = new() { "inertage procedure", "inertage risques" },
                        CategoryScope = "ATEX",
                        DocId = "doc-1",
                        DocPath = "ATEX/Doc.pdf",
                        PageStart = 1,
                        PageEnd = 3,
                        ReasonBefore = "too_few_distinct_candidates",
                        ReasonAfter = "adequate_broad_coverage",
                        ScoreBefore = 12,
                        ScoreAfter = 38,
                        UsableHitsBefore = 1,
                        UsableHitsAfter = 4,
                        CandidateCountBefore = 1,
                        CandidateCountAfter = 4,
                        DistinctPagesBefore = 1,
                        DistinctPagesAfter = 3,
                        ElapsedMs = 123,
                        Accepted = true
                    }
                }
            }
        };
        mem.ResearchWorkingNotes.Add(new ToolMemory.ResearchWorkingNote
        {
            TopicKey = "fr|planning|inertage",
            RequestShape = "planning",
            Label = "planner_search",
            Origin = "llm_planner",
            Queries = new() { "inertage procedure" },
            Outcome = "accepted",
            Accepted = true,
            CandidateDelta = 3,
            DistinctPageDelta = 2
        });
        var sut = new ToolAgentOrchestrator(new ApiClient(), null!, mem);

        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "BuildAgentRuntimeSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var snapshot = Assert.IsAssignableFrom<Dictionary<string, object?>>(method!.Invoke(sut, Array.Empty<object>()));
        var rag = Assert.IsAssignableFrom<Dictionary<string, object?>>(snapshot["rag"]);
        var passes = Assert.IsAssignableFrom<Dictionary<string, object?>[]>(rag["explorationPasses"]);
        var pass = Assert.Single(passes);

        Assert.Equal("planner_search", pass["label"]);
        Assert.Equal("llm_planner", pass["origin"]);
        Assert.Equal("find broader candidates", pass["purpose"]);
        Assert.Equal("ATEX", pass["categoryScope"]);
        Assert.Equal("doc-1", pass["docId"]);
        Assert.Equal("ATEX/Doc.pdf", pass["docPath"]);
        Assert.Equal(1, pass["pageStart"]);
        Assert.Equal(3, pass["pageEnd"]);
        Assert.Equal(true, pass["accepted"]);

        Assert.Equal(1, rag["researchWorkingNoteCount"]);
        var notes = Assert.IsAssignableFrom<Dictionary<string, object?>[]>(rag["researchWorkingNotes"]);
        var note = Assert.Single(notes);
        Assert.Equal("planner_search", note["label"]);
        Assert.Equal("accepted", note["outcome"]);
        Assert.Equal(3, note["candidateDelta"]);
    }
}
