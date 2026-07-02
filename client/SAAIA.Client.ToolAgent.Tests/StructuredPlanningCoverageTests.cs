using System.Text.Json;
using System.Net;
using System.Globalization;
using System.Reflection;
using System.Text;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class StructuredPlanningCoverageTests
{
    [Fact]
    public void Structured_planning_does_not_allow_writer_before_requested_slots_have_supported_candidates()
    {
        const string query = "Je cherche a avoir un plan documente pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(14);

        Assert.Equal(15, ToolAgentOrchestrator.ResolveSourceBackedPlanningTargetItemCountForTests(query));
        Assert.Equal(15, ToolAgentOrchestrator.ResolveMinimumSourceBackedPlanningCandidateCountForTests(query, 15, hasStructuredAxes: true));
        Assert.False(ToolAgentOrchestrator.ShouldAllowWriterForPartialSourceBackedPlanningForTests(toolResults, query, "fr"));
    }

    [Fact]
    public void Structured_planning_partial_candidate_set_exposes_no_decorative_sources()
    {
        const string query = "Je cherche a avoir un plan documente pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(14);

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var sourceKeys = ToolAgentOrchestrator.BuildSourceBackedPlanningDraftSourceKeysForTests(toolResults, "fr", query);

        Assert.True(string.IsNullOrWhiteSpace(answer));
        Assert.Empty(sourceKeys);
    }

    [Fact]
    public void Weekly_day_and_meal_plan_is_gated_even_without_explicit_source_wording()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";

        Assert.True(ToolAgentOrchestrator.ShouldGateStructuredSourceBackedPlanningCoverageForTests(query));
    }

    [Fact]
    public void Weekly_day_and_meal_plan_is_gated_with_accents_and_ui_prefix_noise()
    {
        const string query = "aJe cherche \u00e0 avoir un plan de repas pour la semaine, petit-d\u00e9jeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(15);

        Assert.True(ToolAgentOrchestrator.ShouldGateStructuredSourceBackedPlanningCoverageForTests(query));
        Assert.Equal(15, ToolAgentOrchestrator.ResolveSourceBackedPlanningTargetItemCountForTests(query));
        Assert.False(ToolAgentOrchestrator.ShouldAllowWriterForPartialSourceBackedPlanningForTests(toolResults, query, "fr"));
    }

    [Fact]
    public void Structured_planning_allows_writer_when_requested_slots_have_supported_candidates()
    {
        const string query = "Je cherche a avoir un plan documente pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(15);

        Assert.True(ToolAgentOrchestrator.ShouldAllowWriterForPartialSourceBackedPlanningForTests(toolResults, query, "fr"));
    }

    [Fact]
    public void Structured_planning_routes_supported_candidate_coverage_through_writer()
    {
        const string query = "Je cherche a avoir un plan documente pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(15);

        Assert.False(ToolAgentOrchestrator.ShouldUseWriterForBroadSourceBackedPlanningForTests(toolResults, query));
        Assert.False(ToolAgentOrchestrator.ShouldPreferWriterForPolishedSourceBackedAnswerForTests(toolResults, query));
        Assert.True(ToolAgentOrchestrator.ShouldRequireWriterForBroadDocumentaryFinalForTests(toolResults, query, "fr"));
        Assert.True(ToolAgentOrchestrator.ShouldRouteSourceBackedAnswerThroughWriterForTests(toolResults, query, "fr"));
    }

    [Fact]
    public void Structured_planning_allows_source_backed_writer_repair()
    {
        const string query = "Je cherche a avoir un plan documente pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";

        Assert.True(ToolAgentOrchestrator.ShouldAllowSourceBackedWriterRepairForCurrentTurnForTests(query));
    }

    [Fact]
    public void Structured_planning_confirmation_envelope_routes_partial_coverage_to_writer_when_useful()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var envelope = $"PREVIOUS_USER_REQUEST: {query} USER_CONFIRMED_BROADER_SOURCE_SEARCH: ok vas y RESOLVED_REQUEST: {query}";
        var toolResults = BuildConcreteRecipePlanningResults();

        Assert.Equal(query, ToolAgentOrchestrator.ResolveSourceBackedFallbackIntentQueryForTests(envelope));
        Assert.True(ToolAgentOrchestrator.ShouldGateStructuredSourceBackedPlanningCoverageForTests(query));
        Assert.True(ToolAgentOrchestrator.ShouldRequireWriterForBroadDocumentaryFinalForTests(toolResults, envelope, "fr"));
        Assert.True(ToolAgentOrchestrator.ShouldRouteSourceBackedAnswerThroughWriterForTests(toolResults, envelope, "fr"));
        Assert.True(string.IsNullOrWhiteSpace(ToolAgentOrchestrator.TryBuildInsufficientStructuredPlanningBeforeWriterAnswerForTests(toolResults, envelope, "fr")));
    }

    [Fact]
    public void Structured_planning_confirmation_envelope_rebuilds_when_writer_items_are_unsupported()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var envelope = $"PREVIOUS_USER_REQUEST: {query} USER_CONFIRMED_BROADER_SOURCE_SEARCH: ok vas y RESOLVED_REQUEST: {query}";
        var toolResults = BuildPlanningCandidateResults(15);
        const string writerAnswer = """
            Voici un plan de repas pour la semaine :
            - Petit-dejeuner : Smoothie invente
            - Dejeuner : Salade inventee
            - Diner : Plat invente
            """;

        var finalized = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            writerAnswer,
            toolResults,
            envelope,
            "fr");

        Assert.True(ToolAgentOrchestrator.ShouldRequireWriterForBroadDocumentaryFinalForTests(toolResults, envelope, "fr"));
        Assert.True(ToolAgentOrchestrator.ShouldRouteSourceBackedAnswerThroughWriterForTests(toolResults, envelope, "fr"));
        Assert.True(finalized.Applied);
        Assert.Equal("structured_planning_rejected_unsupported", finalized.Resolution);
        Assert.Equal(0, finalized.SourceCount);
        Assert.Equal(0, CountInlineOpenTokens(finalized.Answer));
        Assert.DoesNotContain("Smoothie invente", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Salade inventee", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Plat invente", finalized.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_planning_finalizer_preserves_supported_writer_answer()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(15);
        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(
            toolResults,
            query,
            "fr",
            maxItems: 15);
        var writerAnswer = string.Join(
            Environment.NewLine,
            titles.Select((title, index) => $"- Item {index + 1}: {title}"));

        var finalized = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            writerAnswer,
            toolResults,
            query,
            "fr");

        Assert.True(finalized.Applied);
        Assert.Equal("structured_planning_supported_writer", finalized.Resolution);
        Assert.Equal(15, finalized.SupportedItemCount);
        Assert.Equal(15, finalized.SourceCount);
        Assert.Contains("Omelette aux herbes", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supported_rebuild", finalized.Resolution, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_planning_rejects_polished_items_that_are_not_the_retrieved_candidates()
    {
        const string query = "Je cherche a avoir un plan documente pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(15);
        var inventedAnswer = """
            Voici un plan de repas pour la semaine :

            Lundi :
            - Petit-déjeuner : Smoothie à l'ananas et au lait d'amande
            - Déjeuner : Salade de quinoa aux légumes et tofu grillé
            - Dîner : Poulet rôti avec des pommes de terre et des légumes vapeur

            Mardi :
            - Petit-déjeuner : Yogourt grec aux fruits rouges
            - Déjeuner : Pâtes aux champignons et au pesto
            - Dîner : Poisson grillé avec des légumes au four

            Mercredi :
            - Petit-déjeuner : Pain complet avec compote de pommes
            - Déjeuner : Salade de poulet aux champignons
            - Dîner : Gratin inventé aux courgettes bleues

            Jeudi :
            - Petit-déjeuner : Smoothie à l'orange et à l'ananas
            - Déjeuner : Tacos de poisson grillé
            - Dîner : Ratatouille avec des pâtes

            Vendredi :
            - Petit-déjeuner : Smoothie à l'avoine et aux noix
            - Déjeuner : Tacos de poulet grillé
            - Dîner : Poisson grillé avec des légumes sautés
            """;

        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            inventedAnswer,
            toolResults,
            query,
            "fr");

        Assert.Equal(15, stats.ItemCount);
        Assert.Equal(0, stats.SupportedItemCount);
        Assert.True(ToolAgentOrchestrator.ShouldRejectUnsupportedPlanningAnswerForFinalForTests(inventedAnswer, toolResults, query, "fr"));
    }

    [Fact]
    public void Structured_planning_finalizer_never_keeps_unsupported_writer_plan_or_decorative_sources()
    {
        const string query = "aJe cherche à avoir un plan de repas pour la semaine, petit-déjeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildGenericPlanningContextResults();
        const string inventedAnswer = """
            Voici un plan de repas pour la semaine, petit-déjeuner, midi et soir du lundi au vendredi, basé sur les informations disponibles :

            Lundi :
            - Petit-déjeuner : Smoothie à l'ananas et au lait de soja
            - Déjeuner : Salade de quinoa avec légumes et tofu grillé
            - Dîner : Poulet rôti avec des pommes de terre et des légumes vapeur

            Mardi :
            - Petit-déjeuner : Yogourt grec aux fruits rouges et à l'abricot
            - Déjeuner : Pâtes aux champignons et aux herbes
            - Dîner : Poisson grillé avec des légumes au four

            Mercredi :
            - Petit-déjeuner : Pain complet avec compote de pommes et oeufs
            - Déjeuner : Salade de quinoa avec légumes et tofu grillé
            - Dîner : Poulet rôti avec des pommes de terre et des légumes vapeur

            Jeudi :
            - Petit-déjeuner : Smoothie à l'ananas et au lait de soja
            - Déjeuner : Salade de quinoa avec légumes et tofu grillé
            - Dîner : Poulet rôti avec des pommes de terre et des légumes vapeur

            Vendredi :
            - Petit-déjeuner : Yogourt grec aux fruits rouges et à l'abricot
            - Déjeuner : Pâtes aux champignons et aux herbes
            - Dîner : Poisson grillé avec des légumes au four
            """;

        var final = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            inventedAnswer,
            toolResults,
            query,
            "fr");

        Assert.True(final.Applied);
        Assert.Equal("structured_planning_rejected_unsupported", final.Resolution);
        Assert.Equal(0, final.SourceCount);
        Assert.Empty(final.SourceKeys);
        Assert.DoesNotContain("Smoothie à l'ananas", final.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Salade de quinoa", final.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sources", final.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_planning_deterministic_answer_uses_only_supported_candidates()
    {
        const string query = "Je cherche a avoir un plan documente pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(15);

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 24);
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.Contains("Omelette aux herbes", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Macaroni tex mex", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[[open|Knowledge/category/source-1.pdf|1|source-1.pdf", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[[open|Knowledge/category/source-15.pdf|15|source-15.pdf", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(15, CountInlineOpenTokens(answer));
        Assert.Equal(15, stats.ItemCount);
        Assert.Equal(15, stats.SupportedItemCount);
        Assert.Equal(15, stats.SourceCount);
        var sources = ToolAgentOrchestrator.DeriveSourcesFromSupportedPlanningAnswerItemsForTests(
            answer,
            toolResults,
            query,
            "fr");
        Assert.Equal(15, sources.Count);
        Assert.Equal(15, sources.Select(static source => $"{source.DocPath}|{source.PageStart}|{source.PageEnd}").Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.False(ToolAgentOrchestrator.ShouldRejectUnsupportedPlanningAnswerForFinalForTests(answer, toolResults, query, "fr"));
    }

    [Fact]
    public void Vague_weekly_meal_plan_rebuilds_seven_supported_daily_options()
    {
        const string query = "Je ne sais pas quoi faire pour les repas de cette semaine, tu peux me proposer un plan de repas pour la semaine a partir des documents disponibles, avec les sources utiles ?";
        var toolResults = BuildMainMealPlanningCandidateResults(7);

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");
        var finalized = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            "Voici un plan invente qui ne doit pas etre conserve.",
            toolResults,
            query,
            "fr");

        Assert.True(ToolAgentOrchestrator.ShouldGateStructuredSourceBackedPlanningCoverageForTests(query));
        Assert.Equal(7, ToolAgentOrchestrator.ResolveSourceBackedPlanningTargetItemCountForTests(query));
        Assert.True(
            answer.Contains("Jour 7", StringComparison.OrdinalIgnoreCase),
            $"answer={answer}{Environment.NewLine}{string.Join(Environment.NewLine, ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr"))}");
        Assert.Equal(7, CountInlineOpenTokens(answer));
        Assert.Equal(7, stats.ItemCount);
        Assert.Equal(7, stats.SupportedItemCount);
        Assert.Equal(7, stats.SourceCount);
        Assert.True(finalized.Applied);
        Assert.Contains("supported_rebuild", finalized.Resolution, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, finalized.SupportedItemCount);
        Assert.Equal(7, finalized.SourceCount);
        Assert.Equal(7, finalized.SourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Vague_weekly_meal_plan_prefers_main_dishes_over_desserts_when_available()
    {
        const string query = "Je ne sais pas quoi faire pour les repas de cette semaine, tu peux me proposer un plan de repas pour la semaine a partir des documents disponibles, avec les sources utiles ?";
        var toolResults = BuildMealPlanningResultsWithHighRankedDesserts();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 7);
        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var finalized = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            "Voici un plan invente qui ne doit pas etre conserve.",
            toolResults,
            query,
            "fr");

        Assert.Contains("Paella mixte", titles);
        Assert.Contains("Quiche lorraine", titles);
        Assert.DoesNotContain(titles, static title => title.Contains("Millefeuille", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Tiramisu", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Paella mixte", answer);
        Assert.Contains("Quiche lorraine", answer);
        Assert.DoesNotContain("Millefeuille", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tiramisu", answer, StringComparison.OrdinalIgnoreCase);
        Assert.True(finalized.Applied);
        Assert.Contains("supported_rebuild", finalized.Resolution, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, finalized.SourceCount);
        Assert.Equal(7, finalized.SourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Vague_weekly_meal_plan_does_not_fill_missing_meal_slot_with_dessert()
    {
        const string query = "Je ne sais pas quoi faire pour les repas de cette semaine, tu peux me proposer un plan de repas pour la semaine a partir des documents disponibles, avec les sources utiles ?";
        var toolResults = BuildMealPlanningResultsWithSixMainDishesAndDessert();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 7);
        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);

        Assert.Equal(6, titles.Length);
        Assert.DoesNotContain(titles, static title => title.Contains("Millefeuille", StringComparison.OrdinalIgnoreCase));
        Assert.False(ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"));
        Assert.True(string.IsNullOrWhiteSpace(answer));
    }

    [Fact]
    public void Vague_weekly_meal_plan_exploration_prioritizes_generic_candidate_queries()
    {
        const string query = "Je ne sais pas quoi faire pour les repas de cette semaine, tu peux me proposer un plan de repas pour la semaine a partir des documents disponibles, avec les sources utiles ?";

        var queries = ToolAgentOrchestrator.BuildPlanningExplorationRetrievalQueriesForTests(query);
        var earlyQueries = queries.Take(12).ToArray();

        Assert.Contains("repas options", earlyQueries);
        Assert.Contains("options repas", earlyQueries);
        Assert.Contains("repas candidats", earlyQueries);
        Assert.Contains("candidats repas", earlyQueries);
    }

    [Fact]
    public void Weekly_meal_plan_exploration_keeps_recipe_queries_out_of_navigation_indexes()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";

        var planningQueries = ToolAgentOrchestrator.BuildPlanningExplorationRetrievalQueriesForTests(query);
        var candidateQueries = ToolAgentOrchestrator.BuildSourceBackedCandidateDiscoveryRetrievalQueriesForTests(query);
        var allQueries = planningQueries.Concat(candidateQueries).ToArray();

        Assert.Contains(allQueries, query => LooksLikeSlotCandidateQuery(query, "petit-dejeuner"));
        Assert.Contains(allQueries, query => LooksLikeSlotCandidateQuery(query, "diner"));
        Assert.Contains(allQueries, query => LooksLikeSlotCandidateQuery(query, "souper"));
        Assert.Contains(allQueries, query => LooksLikeSlotCandidateQuery(query, "gouter")
                                            || LooksLikeSlotCandidateQuery(query, "collation"));
        Assert.DoesNotContain(allQueries, LooksLikeNavigationIndexQuery);

        static bool LooksLikeSlotCandidateQuery(string value, string slot)
        {
            var normalized = value.ToLowerInvariant();
            return normalized.Contains(slot, StringComparison.Ordinal)
                && (normalized.Contains("options", StringComparison.Ordinal)
                    || normalized.Contains("candidats", StringComparison.Ordinal)
                    || normalized.Contains("propositions", StringComparison.Ordinal)
                    || normalized.Contains("preparations", StringComparison.Ordinal));
        }

        static bool LooksLikeNavigationIndexQuery(string value)
        {
            var normalized = value.ToLowerInvariant();
            return normalized.Contains("sommaire", StringComparison.Ordinal)
                || normalized.Contains("table des matieres", StringComparison.Ordinal)
                || normalized.Contains("sections principales", StringComparison.Ordinal)
                || string.Equals(normalized, "index", StringComparison.Ordinal)
                || normalized.StartsWith("index ", StringComparison.Ordinal)
                || normalized.EndsWith(" index", StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Explicit_weekly_meal_plan_exploration_does_not_start_with_generic_option_queries()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi en y mettant petit-dejeuner, diner, souper et gouter / collation chaque jour.";

        var queries = ToolAgentOrchestrator.BuildPlanningExplorationRetrievalQueriesForTests(query);
        var earlyQueries = queries.Take(8).ToArray();

        Assert.DoesNotContain("repas options", earlyQueries);
        Assert.DoesNotContain("options repas", earlyQueries);
        Assert.Contains(earlyQueries, q => LooksLikeSlotCandidateQuery(q));

        static bool LooksLikeSlotCandidateQuery(string value)
        {
            var normalized = value.ToLowerInvariant();
            return (normalized.Contains("petit-dejeuner", StringComparison.Ordinal)
                    || normalized.Contains("diner", StringComparison.Ordinal)
                    || normalized.Contains("souper", StringComparison.Ordinal)
                    || normalized.Contains("gouter", StringComparison.Ordinal)
                    || normalized.Contains("collation", StringComparison.Ordinal))
                && (normalized.Contains("options", StringComparison.Ordinal)
                    || normalized.Contains("candidats", StringComparison.Ordinal)
                    || normalized.Contains("propositions", StringComparison.Ordinal)
                    || normalized.Contains("preparations", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Structured_meal_planning_rejects_storage_or_conservation_fragments_as_candidates()
    {
        Assert.True(ToolAgentOrchestrator.LooksLikeNoisyStructuredPlanningCandidateTitleForTests(
            "N Ou surle comptoir, place Bi semaine au frigo, en bottes, ya Romarin"));
        Assert.True(ToolAgentOrchestrator.LooksLikeNoisyStructuredPlanningCandidateTitleForTests(
            "Congeler ou steriliser pour faire une conserve"));
    }

    [Fact]
    public void Weekly_meal_plan_llm_planner_rewrites_abstract_weekly_menu_queries_into_concrete_slot_candidate_queries()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et user-friendly, et ajoute seulement les sources vraiment utiles.";
        const string rawJson = """
        {
          "categoryDecision": {
            "categoryScope": "Cuisine",
            "decision": "use",
            "confidence": "high",
            "reason": "Cuisine is the semantic container for meal planning."
          },
          "passes": [
            {
              "label": "structured_meal_plan",
              "purpose": "Find meal-plan evidence by requested slots.",
              "categoryScope": "Cuisine",
              "queries": [
                "repas semaine cuisine",
                "plan repas semaine petit-déjeuner",
                "plan repas semaine dîner",
                "plan repas semaine souper",
                "plan repas semaine collation",
                "repas semaine cuisine détails",
                "repas semaine cuisine suggestions",
                "repas semaine cuisine idées",
                "repas semaine cuisine exemples",
                "repas semaine cuisine menus"
              ]
            }
          ]
        }
        """;

        var queries = ToolAgentOrchestrator.ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
            rawJson,
            query,
            "fr",
            plannedCategoryScope: "Cuisine");

        Assert.DoesNotContain(queries, q => ToolAgentOrchestrator.NormalizeRagQueryForTests(q).Contains("plan repas semaine", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, q => ToolAgentOrchestrator.NormalizeRagQueryForTests(q).Contains("repas semaine cuisine", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, query => LooksLikeSlotCandidateQuery(query, "petit-dejeuner"));
        Assert.Contains(queries, query => LooksLikeSlotCandidateQuery(query, "diner"));
        Assert.Contains(queries, query => LooksLikeSlotCandidateQuery(query, "souper"));
        Assert.Contains(queries, query => LooksLikeSlotCandidateQuery(query, "gouter")
                                        || LooksLikeSlotCandidateQuery(query, "collation"));
        Assert.InRange(queries.Length, 4, 10);

        static bool LooksLikeSlotCandidateQuery(string value, string slot)
        {
            var normalized = value.ToLowerInvariant();
            return normalized.Contains(slot, StringComparison.Ordinal)
                && (normalized.Contains("options", StringComparison.Ordinal)
                    || normalized.Contains("candidats", StringComparison.Ordinal)
                    || normalized.Contains("propositions", StringComparison.Ordinal)
                    || normalized.Contains("preparations", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Structured_planning_waits_for_committed_candidate_search_before_standalone_anchor_followup()
    {
        const string query = "Prepare un plan hebdomadaire source, matin midi et soir, du lundi au vendredi, a partir des documents disponibles.";
        var toolResults = new ToolResults();
        var diversifiedResults = BuildExplicitPagedCardEvidencePlanningResults(12);

        Assert.False(ToolAgentOrchestrator.ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPassForTests(
            toolResults,
            query,
            "fr",
            acceptedAnyExplorationPass: false));
        Assert.False(ToolAgentOrchestrator.ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPassForTests(
            toolResults,
            query,
            "fr",
            acceptedAnyExplorationPass: true));
        Assert.True(ToolAgentOrchestrator.ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPassForTests(
            diversifiedResults,
            query,
            "fr",
            acceptedAnyExplorationPass: true));
    }

    [Fact]
    public void Structured_planning_defers_anchor_followup_while_candidate_bank_is_sparse()
    {
        const string query = "Prepare un plan hebdomadaire source, matin midi et soir, du lundi au vendredi, a partir des documents disponibles.";
        var sparseResults = BuildExplicitPagedCardEvidencePlanningResults(6);
        var broaderResults = BuildExplicitPagedCardEvidencePlanningResults(12);

        Assert.True(ToolAgentOrchestrator.ShouldDeferSparseSourceBackedPlanningAnchorFollowupForTests(
            sparseResults,
            query,
            "fr"));
        Assert.False(ToolAgentOrchestrator.ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPassForTests(
            sparseResults,
            query,
            "fr",
            acceptedAnyExplorationPass: true));
        Assert.False(ToolAgentOrchestrator.ShouldDeferSparseSourceBackedPlanningAnchorFollowupForTests(
            broaderResults,
            query,
            "fr"));
    }

    [Fact]
    public void Broad_non_structured_requests_can_still_use_anchor_followup_without_committed_search()
    {
        const string query = "Donne moi une liste sourcee des procedures disponibles dans les documents.";
        var toolResults = new ToolResults();

        Assert.True(ToolAgentOrchestrator.ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPassForTests(
            toolResults,
            query,
            "fr",
            acceptedAnyExplorationPass: false));
    }

    [Fact]
    public void Weekly_meal_plan_exploration_suppresses_generic_anchor_discovery_until_concrete_recipe_candidates_cover_slots()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var toolResults = BuildConcreteRecipePlanningResults();

        var labels = ToolAgentOrchestrator.BuildSourceBackedEvidenceExplorationPassLabelsForTests(toolResults, query, "fr");
        var queries = ToolAgentOrchestrator.BuildSourceBackedEvidenceExplorationPassQueriesForTests(toolResults, query, "fr");

        Assert.Contains("planning_exploration", labels);
        Assert.Contains("candidate_discovery", labels);
        Assert.Contains("slot_balancing_inventory", labels);
        Assert.Contains("candidate_inventory", labels);
        Assert.DoesNotContain("navigation_discovery", labels);
        Assert.DoesNotContain("anchor_discovery", labels);
        Assert.Contains("petit-dejeuner options", queries);
        Assert.Contains("gouter options", queries);
        Assert.Contains("diner options", queries);
        Assert.Contains("souper options", queries);
        Assert.Contains("candidats petit-dejeuner", queries);
        Assert.Contains("candidats gouter", queries);
    }

    [Fact]
    public void Weekly_meal_plan_llm_planner_sees_missing_slot_coverage_before_anchor_followup()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et user-friendly, et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildMealPlanningResultsWithoutSnackCoverage();

        var prompt = ToolAgentOrchestrator.BuildSourceBackedLlmEvidenceExplorationUserPromptForTests(
            toolResults,
            query,
            "fr");

        Assert.Contains("PLANNING_COVERAGE_TRACE:", prompt);
        Assert.Contains("stage=slot_fit", prompt);
        Assert.Contains("snack_pool=0", prompt);
        Assert.Contains("snack_route_pool=0", prompt);
        Assert.Contains("snack_route_fit_pool=0", prompt);
        Assert.True(ToolAgentOrchestrator.ShouldDeferAnchorFollowupAfterAcceptedLlmPlannerPassForTests(
            toolResults,
            query,
            "fr",
            remainingPlannerRounds: 1));
    }

    [Fact]
    public void Weekly_meal_plan_llm_planner_sees_failed_axis_queries_and_untried_user_aliases()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildMealPlanningResultsAfterFailedCollationRuns();

        var prompt = ToolAgentOrchestrator.BuildSourceBackedLlmEvidenceExplorationUserPromptForTests(
            toolResults,
            query,
            "fr");

        Assert.Contains("WEAK_OR_UNDERCOVERED_AXES:", prompt);
        Assert.Contains("axis=collation", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failed_or_low_hit_queries=", prompt);
        Assert.Contains("collation recettes", prompt);
        Assert.Contains("collation plats", prompt);
        Assert.Contains("=> 0 hit(s)", prompt);
        Assert.Contains("user_terms=collation, gouter", prompt);
        Assert.Contains("suggested_pivots=gouter, encas", prompt);
        Assert.Contains("avoid repeating failed_or_low_hit_queries", prompt);
    }

    [Fact]
    public void Weekly_meal_plan_with_weekdays_supper_and_snack_builds_structured_user_friendly_grid()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var toolResults = BuildSlotAwareMealPlanningResultsWithNoisyHighRankedItems();

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 24);
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");
        var answerItems = ToolAgentOrchestrator.ExtractConcretePlanningAnswerItemsForTests(answer);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.True(ToolAgentOrchestrator.ShouldGateStructuredSourceBackedPlanningCoverageForTests(query));
        Assert.Equal(20, ToolAgentOrchestrator.ResolveSourceBackedPlanningTargetItemCountForTests(query));
        Assert.True(
            ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"),
            $"Expected adequate coverage. titles={string.Join(" | ", titles)} answer={answer}");
        Assert.True(ToolAgentOrchestrator.HasStructuredSourceBackedPlanningTargetCandidateCoverageForStopForTests(toolResults, query, "fr"));
        Assert.True(
            answer.Contains("Lundi :", StringComparison.OrdinalIgnoreCase),
            $"Expected a complete weekday grid. titles={string.Join(" | ", titles)} answer={answer}{Environment.NewLine}{string.Join(Environment.NewLine, trace)}");
        var normalizedAnswer = RemoveDiacriticsForAssertion(answer);
        Assert.Contains("Vendredi :", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Petit-dejeuner", normalizedAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Diner", normalizedAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Souper", normalizedAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Collation", normalizedAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(20, CountInlineOpenTokens(answer));
        Assert.True(
            stats.ItemCount == 20,
            $"Detected items:{Environment.NewLine}{string.Join(Environment.NewLine, answerItems.Select((item, index) => $"{index + 1}. {item}"))}{Environment.NewLine}answer={answer}");
        Assert.Equal(20, stats.SupportedItemCount);
        Assert.Equal(20, stats.SourceCount);
    }

    [Fact]
    public void Final_ui_weekly_meal_plan_keeps_only_useful_distinct_cited_sources()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et user-friendly, et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildSlotAwareMealPlanningResultsWithNoisyHighRankedItems();

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");
        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(
            toolResults,
            query,
            "fr",
            maxItems: 24);
        var sourceKeys = ToolAgentOrchestrator.BuildSourceBackedPlanningDraftSourceKeysForTests(toolResults, "fr", query);
        var finalized = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            "Voici un brouillon non fiable qui ne doit pas survivre.",
            toolResults,
            query,
            "fr");

        Assert.False(ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"));
        Assert.True(string.IsNullOrWhiteSpace(answer) || CountInlineOpenTokens(answer) < 20, answer);
        Assert.True(stats.ItemCount < 20);
        Assert.True(stats.SupportedItemCount < 20);
        Assert.True(stats.SourceCount < 20);
        Assert.True(sourceKeys.Length < 20);
        Assert.Equal(sourceKeys.Length, sourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(finalized.Applied);
        Assert.True(finalized.SourceCount > 0, finalized.Answer);
        Assert.True(finalized.SourceCount < 20, finalized.Answer);
        Assert.Equal(finalized.SourceKeys.Length, finalized.SourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(CountInlineOpenTokens(finalized.Answer) > 0, finalized.Answer);
        Assert.DoesNotContain("Voici quelques idees pour remplacer votre", titles, StringComparer.OrdinalIgnoreCase);
        var normalizedFinalAnswer = RemoveDiacriticsForAssertion(finalized.Answer);
        Assert.Contains("directement utilisables", normalizedFinalAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Voici quelques idees pour remplacer votre", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("brouillon non fiable", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trop limitees", normalizedFinalAnswer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_planning_prefers_quoted_item_title_over_reference_source_label()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et user-friendly, et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildMealPlanningResultsWithReferenceAttributionBeforeQuotedRecipe();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(
            toolResults,
            query,
            "fr",
            maxItems: 4);

        Assert.Contains("Brownies aux haricots noirs", titles, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("CUISINE FUTEE PARENTS PRESSES", titles, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SAUTE VEGETARIENPLATS PRINCI", "Saute vegetarien")]
    [InlineData("Saut\u00e9 v\u00e9g\u00e9tarienplats princi", "Saut\u00e9 v\u00e9g\u00e9tarien")]
    [InlineData("LEGUMINEUSES AU CARI PLATS PRINCI- PAUX", "Legumineuses au cari")]
    [InlineData("FAJITAS DEJ EUNER A JOSIANE", "Fajitas dejeuner a josiane")]
    [InlineData("Muffins pommes et cheddar Variante 2 Variante 3 Muffins pommes et cheddar", "Muffins pommes et cheddar")]
    public void Source_backed_planning_display_title_strips_ocr_heading_and_variant_noise(
        string rawTitle,
        string expected)
    {
        Assert.Equal(expected, ToolAgentOrchestrator.FormatSourceBackedPlanningDisplayTitleForTests(rawTitle));
    }

    [Fact]
    public void Structured_weekly_meal_plan_does_not_stop_generic_exploration_when_sources_are_still_short()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var toolResults = BuildPlanningCandidateResultsWithNearUniqueSources(20, distinctPages: 18);

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");
        var sourceKeys = ToolAgentOrchestrator.BuildSourceBackedPlanningDraftSourceKeysForTests(toolResults, "fr", query);

        Assert.False(ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"));
        Assert.False(ToolAgentOrchestrator.HasStructuredSourceBackedPlanningTargetCandidateCoverageForStopForTests(toolResults, query, "fr"));
        Assert.True(string.IsNullOrWhiteSpace(answer));
        Assert.Equal(0, stats.ItemCount);
        Assert.Equal(0, stats.SupportedItemCount);
        Assert.Equal(0, stats.SourceCount);
        Assert.Empty(sourceKeys);
    }

    [Fact]
    public void Structured_weekly_meal_plan_backfills_ranked_page_duplicate_with_distinct_source()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var toolResults = BuildPlanningCandidateResultsWithTopPageDuplicateAndDistinctBackfill();

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");
        var sourceKeys = ToolAgentOrchestrator.BuildSourceBackedPlanningDraftSourceKeysForTests(toolResults, "fr", query);

        Assert.True(
            ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"),
            $"answer={answer}{Environment.NewLine}{string.Join(Environment.NewLine, ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr"))}");
        Assert.True(ToolAgentOrchestrator.HasStructuredSourceBackedPlanningTargetCandidateCoverageForStopForTests(toolResults, query, "fr"));
        Assert.Contains("Barres aux cereales", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(20, CountInlineOpenTokens(answer));
        Assert.Equal(20, stats.ItemCount);
        Assert.Equal(20, stats.SupportedItemCount);
        Assert.Equal(20, stats.SourceCount);
        Assert.Equal(20, sourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("Knowledge/Meals/source-20.pdf|20|20", sourceKeys);
    }

    [Fact]
    public void Weekly_meal_plan_assigns_sources_to_compatible_slots_and_skips_non_meal_titles()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildSlotAwareMealPlanningResultsWithNoisyHighRankedItems();

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");
        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 64);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");
        Assert.False(
            ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"),
            $"answer={answer} titles={string.Join(" | ", titles)} trace={string.Join(" || ", trace.Where(line => line.Contains("slot_fit", StringComparison.OrdinalIgnoreCase) || line.Contains("summary", StringComparison.OrdinalIgnoreCase) || line.Contains("decision=accepted", StringComparison.OrdinalIgnoreCase)))}");
        Assert.True(
            string.IsNullOrWhiteSpace(answer) || CountInlineOpenTokens(answer) < 20,
            $"{answer}{Environment.NewLine}{string.Join(Environment.NewLine, trace.Where(static line => line.Contains("slot_fit", StringComparison.OrdinalIgnoreCase) || line.Contains("scope", StringComparison.OrdinalIgnoreCase)))}");
        Assert.True(stats.ItemCount < 20);
        Assert.True(stats.SupportedItemCount < 20);
        Assert.True(stats.SourceCount < 20);
        Assert.DoesNotContain("Lundi :", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(trace, static line => line.Contains("stage=slot_fit", StringComparison.OrdinalIgnoreCase)
            || line.Contains("stage=summary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Weekly_meal_plan_uses_concrete_sourced_generic_candidates_for_main_slots()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildMealPlanningResultsWithGenericMainSlotCandidates();

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");
        var sourceKeys = ToolAgentOrchestrator.BuildSourceBackedPlanningDraftSourceKeysForTests(toolResults, "fr", query);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.True(
            ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"),
            $"answer={answer}{Environment.NewLine}{string.Join(Environment.NewLine, trace)}");
        Assert.True(ToolAgentOrchestrator.HasStructuredSourceBackedPlanningTargetCandidateCoverageForStopForTests(toolResults, query, "fr"));
        Assert.Contains("Ragout de legumes", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Curry de pois chiches", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(20, CountInlineOpenTokens(answer));
        Assert.Equal(20, stats.ItemCount);
        Assert.Equal(20, stats.SupportedItemCount);
        Assert.Equal(20, stats.SourceCount);
        Assert.Equal(20, sourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var slotFitLine = Assert.Single(trace, static line => line.Contains("stage=slot_fit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("assigned_slots=20", slotFitLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("primary_pools=", slotFitLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("neutral_pool=", slotFitLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(trace, static line => line.Contains("decision=accepted", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Ragout de legumes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Weekly_meal_plan_deterministic_gate_rejects_wrong_slot_fillers_and_uses_better_options_when_sufficient()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildMealPlanningResultsWithRoutedDessertAndSpreadDecoys();

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.False(
            ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"),
            $"answer={answer}{Environment.NewLine}{string.Join(Environment.NewLine, trace)}");
        Assert.InRange(CountInlineOpenTokens(answer), 1, 19);
        Assert.DoesNotContain("Lundi :", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(trace, static line => line.Contains("stage=slot_fit", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("assigned_slots=20", StringComparison.OrdinalIgnoreCase)
            && line.Contains("primary_pools=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Final_ui_weekly_meal_plan_replaces_writer_answer_with_wrong_slot_fillers()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildMealPlanningResultsWithRoutedDessertAndSpreadDecoys();
        var badWriterAnswer = string.Join(
            Environment.NewLine,
            new[]
            {
                "Lundi :",
                "- Petit-déjeuner : Omelette italienne",
                "- Dîner : Bucatini a l'amatriciana",
                "- Souper : Crème catalane",
                "- Collation : Houmous de betterave",
                "Mardi :",
                "- Petit-déjeuner : Muffins aux pommes",
                "- Dîner : Nouilles sauce cacahuete",
                "- Souper : Boeuf jardiniere",
                "- Collation : Tarte tomate et chevre"
            });

        var finalized = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            badWriterAnswer,
            toolResults,
            query,
            "fr");

        Assert.True(finalized.Applied);
        Assert.Equal(20, CountInlineOpenTokens(finalized.Answer));
        Assert.Contains("Lundi", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Vendredi", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Brouillon", finalized.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Weekly_meal_plan_does_not_fill_missing_breakfast_slots_with_desserts_or_main_dishes()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildUiLikeMealPlanningResultsWithInsufficientBreakfastCoverage();

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.False(
            ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"),
            $"answer={answer}{Environment.NewLine}{string.Join(Environment.NewLine, trace)}");
        Assert.False(ToolAgentOrchestrator.HasStructuredSourceBackedPlanningTargetCandidateCoverageForStopForTests(toolResults, query, "fr"));
        Assert.True(string.IsNullOrWhiteSpace(answer) || CountInlineOpenTokens(answer) < 20, answer);
        Assert.DoesNotContain("Petit-déjeuner : Nouilles", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Petit-déjeuner : Glaçage", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Petit-déjeuner : Tiramisu", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Petit-déjeuner : Profiteroles", answer, StringComparison.OrdinalIgnoreCase);
        var slotFitLine = Assert.Single(trace, static line => line.Contains("stage=slot_fit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("assigned_slots=20", slotFitLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Weekly_meal_plan_route_backfills_distinct_breakfast_candidate_without_recycling_sources()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildMealPlanningResultsWithBreakfastRouteBackfillCandidate();

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");
        var sourceKeys = ToolAgentOrchestrator.BuildSourceBackedPlanningDraftSourceKeysForTests(toolResults, "fr", query);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.False(
            ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"),
            $"answer={answer}{Environment.NewLine}{string.Join(Environment.NewLine, trace)}");
        Assert.True(string.IsNullOrWhiteSpace(answer) || CountInlineOpenTokens(answer) < 20, answer);
        Assert.True(stats.SourceCount < 20);
        Assert.True(sourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() < 20);
        Assert.DoesNotContain("tourner", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lundi :", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Petit-déjeuner : Tiramisu", answer, StringComparison.OrdinalIgnoreCase);
        var slotFitLine = Assert.Single(trace, static line => line.Contains("stage=slot_fit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("assigned_slots=20", slotFitLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Weekly_meal_plan_uses_retrieval_query_routes_to_fill_observed_recipe_slots()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var toolResults = BuildObservedSlotRoutedMealPlanningResults();

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.False(
            ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"),
            $"answer={answer}{Environment.NewLine}{string.Join(Environment.NewLine, trace)}");
        Assert.True(CountInlineOpenTokens(answer) > 0, answer);
        Assert.True(stats.SourceCount > 0);
        Assert.DoesNotContain("tourner", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lundi :", answer, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            trace.Any(static line => line.Contains("stage=slot_fit", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("assigned_slots=20", StringComparison.OrdinalIgnoreCase)
                && line.Contains("routed_pool=", StringComparison.OrdinalIgnoreCase)
                && line.Contains("primary_pools=", StringComparison.OrdinalIgnoreCase)
                && line.Contains("alternative_pools=", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, trace.Where(static line => line.Contains("stage=slot_fit", StringComparison.OrdinalIgnoreCase))));
    }

    [Fact]
    public void Final_ui_weekly_meal_plan_rotates_sourced_slot_candidates_after_filtered_shortage()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var toolResults = BuildObservedSlotRoutedMealPlanningResults();

        var finalized = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            "Brouillon LLM a remplacer.",
            toolResults,
            query,
            "fr");
        var normalizedAnswer = RemoveDiacriticsForAssertion(finalized.Answer);

        Assert.True(finalized.Applied);
        Assert.True(finalized.SourceCount > 0, finalized.Answer);
        Assert.True(finalized.SourceCount < 20, finalized.Answer);
        Assert.Equal(finalized.SourceKeys.Length, finalized.SourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(20, CountInlineOpenTokens(finalized.Answer));
        Assert.Contains("Lundi", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Vendredi", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Petit-dejeuner", normalizedAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Collation", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("je les fais donc tourner", normalizedAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Houmous de betterave", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pate d'artichaut", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Brouillon LLM", finalized.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Weekly_meal_plan_preserves_slot_route_when_duplicate_candidate_is_found_through_multiple_queries()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Ajoute seulement les sources utiles.";
        var toolResults = BuildMealPlanningResultsWithDuplicateSnackRouteCandidate();

        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");
        var slotFitLine = Assert.Single(trace, static line => line.Contains("stage=slot_fit", StringComparison.OrdinalIgnoreCase));
        var acceptedLines = trace
            .Where(static line => line.Contains("stage=candidate", StringComparison.OrdinalIgnoreCase)
                && line.Contains("decision=accepted", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.DoesNotContain("assigned_slots=20", slotFitLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("route_evidence=", slotFitLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("primary_pools=", slotFitLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alternative_pools=", slotFitLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(acceptedLines, static line => line.Contains("Muffins aux petits fruits", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Weekly_meal_plan_accepts_light_snacks_from_neutral_routes_without_promoting_standalone_dips()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Ajoute seulement les sources utiles.";
        var toolResults = BuildMealPlanningResultsWithNeutralRouteLightSnacksAndDipDecoy();

        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");
        var slotFitLine = Assert.Single(trace, static line => line.Contains("stage=slot_fit", StringComparison.OrdinalIgnoreCase));
        var acceptedLines = trace
            .Where(static line => line.Contains("stage=candidate", StringComparison.OrdinalIgnoreCase)
                && line.Contains("decision=accepted", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.DoesNotContain("assigned_slots=20", slotFitLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("route_evidence=", slotFitLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("primary_pools=", slotFitLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alternative_pools=", slotFitLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(acceptedLines, static line => line.Contains("Muffins aux petits fruits", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(acceptedLines, static line => line.Contains("Scones aux canneberges", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Weekly_meal_plan_writer_inventory_exposes_route_and_slot_fit_for_llm_adjudication()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var toolResults = BuildObservedSlotRoutedMealPlanningResults();

        var inventory = ToolAgentOrchestrator.BuildSourceBackedCandidateLeadsForWriterForTests(toolResults, query, "fr");

        Assert.Contains("slotRoute=", inventory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slotFit=", inventory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retrievalQuery=", inventory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("candidateKey=", inventory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pageKey=", inventory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slotRoute=\"main_meal\"", inventory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Weekly_meal_plan_writer_inventory_keeps_source_pages_when_candidate_extraction_is_sparse()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Ajoute seulement les sources utiles.";
        var toolResults = BuildSparsePlanningCandidateAndContextResults();

        var inventory = ToolAgentOrchestrator.BuildSourceBackedCandidateLeadsForWriterForTests(toolResults, query, "fr");

        Assert.Contains("role=\"item\"", inventory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("role=\"source_page\"", inventory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pageKey=", inventory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retrievalQuery=", inventory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planification de menus", inventory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Weekly_meal_plan_candidate_adjudication_prompt_exposes_routes_sources_and_json_contract()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var toolResults = BuildObservedSlotRoutedMealPlanningResults();

        var system = ToolAgentOrchestrator.BuildSourceBackedCandidateAdjudicationSystemPromptForTests("fr");
        var prompt = ToolAgentOrchestrator.BuildSourceBackedCandidateAdjudicationUserPromptForTests(toolResults, query, "fr");

        Assert.True(ToolAgentOrchestrator.ShouldRunSourceBackedCandidateAdjudicationForWriterForTests(toolResults, query, "fr"));
        Assert.Contains("Do not answer the user", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("any source category or domain", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USER_REQUEST:", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REQUEST_SHAPE:", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PLANNING_COVERAGE_TRACE:", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EVIDENCE_INVENTORY:", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COMPACT_TOOL_RESULTS_EXCERPT:", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EVIDENCE_ITEM", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slotRoute=", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slotFit=", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retrievalQuery=", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"decision\": \"use_candidates|partial|insufficient\"", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"sourceUseful\": true", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"duplicateOf\": null", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"missing\"", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Source_backed_candidate_adjudication_normalizer_accepts_only_json_objects_with_decision_or_items()
    {
        var valid = ToolAgentOrchestrator.NormalizeSourceBackedCandidateAdjudicationJsonForTests("""
```json
{
  "decision": "partial",
  "reason": "usable candidates exist but one slot is missing",
  "items": [
    {
      "candidateKey": "doc#p1#title",
      "valid": true,
      "sourceUseful": true,
      "duplicateOf": null
    }
  ],
  "missing": []
}
```
""");

        Assert.NotNull(valid);
        Assert.Contains("\"decision\"", valid!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"partial\"", valid!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ToolAgentOrchestrator.NormalizeSourceBackedCandidateAdjudicationJsonForTests("not json"));
        Assert.Null(ToolAgentOrchestrator.NormalizeSourceBackedCandidateAdjudicationJsonForTests("""{"reason":"missing required shape"}"""));
    }

    [Fact]
    public void Structured_planning_generic_user_axes_do_not_anchor_filter_llm_routed_candidates()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildMealPlanningResultsWhereOnlyMainItemsMentionGenericPlanAxes();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 64);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.Contains(titles, static title => title.Contains("Barres aux cereales", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("Yaourt fruits", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("Compote pommes", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            trace.Any(static line => line.Contains("stage=candidate_pool", StringComparison.OrdinalIgnoreCase)
                && line.Contains("route_snack=5", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, trace));
    }

    [Fact]
    public void Structured_meal_planning_cleans_observed_recipe_card_titles_and_skips_generic_inventory_titles()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var toolResults = BuildObservedDirtyRecipeCardMealPlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 20);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");
        var strictTitles = ToolAgentOrchestrator.StrictSourceBackedOptionTitlesForTests(toolResults, query);
        var joined = string.Join(" | ", titles);
        var joinedStrict = string.Join(" | ", strictTitles);

        Assert.True(
            titles.Any(static title => title.Contains("NOUILLES", StringComparison.OrdinalIgnoreCase)
                && title.Contains("CACAHOU", StringComparison.OrdinalIgnoreCase)),
            "titles=" + joined + Environment.NewLine + string.Join(Environment.NewLine, trace));
        Assert.Contains(titles, static title => title.Contains("Pizzas rigolotes", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            titles.Any(static title => title.Contains("fourr", StringComparison.OrdinalIgnoreCase)),
            "titles=" + joined + Environment.NewLine + "strictTitles=" + joinedStrict + Environment.NewLine + string.Join(Environment.NewLine, trace));
        Assert.Contains(titles, static title => title.Contains("COURGE SPAGHETTI", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("JARDINI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => string.Equals(title, "SPAGHETTI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => string.Equals(title, "NOUILLES Re", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("INGREDIENT", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("INGR", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Equals("Patisserie salee", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Equals("Pâtisserie salée", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Equals("Plats au four", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("KAKILES", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => RemoveDiacriticsForAssertion(title).Contains("A-COTES", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Lorsqu", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Gratiner", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Blanchir", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.EndsWith(" 12", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("stage=candidate_pool", StringComparison.OrdinalIgnoreCase)
            && line.Contains("raw_candidates=", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(joined));
    }

    [Fact]
    public void Structured_meal_planning_keeps_backend_like_recipe_cards_and_drops_partial_duplicates()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var toolResults = BuildObservedBackendRecipeInventoryMealPlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 24);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");
        var strictTitles = ToolAgentOrchestrator.StrictSourceBackedOptionTitlesForTests(toolResults, query);
        var joined = string.Join(" | ", titles);
        var joinedStrict = string.Join(" | ", strictTitles);

        Assert.Contains(titles, static title => title.Contains("Croquettes de poulet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("Macaroni TEX-MEX", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            titles.Any(static title => title.Contains("NOUILLES SAUTEES", StringComparison.OrdinalIgnoreCase)),
            "titles=" + joined + Environment.NewLine + "strictTitles=" + joinedStrict + Environment.NewLine + string.Join(Environment.NewLine, trace));
        Assert.True(
            titles.Any(static title => title.Contains("TA LASAGNE", StringComparison.OrdinalIgnoreCase)),
            "titles=" + joined + Environment.NewLine + "strictTitles=" + joinedStrict + Environment.NewLine + string.Join(Environment.NewLine, trace));
        Assert.Contains(titles, static title => title.Contains("Brochettes de poisson", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("Truite rotie", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("BOULETTES DE POULET", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("SALADE DE BETTERAVES", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("Fruits en beignets", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("SCONES AUX CANNEBERGES", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("HOUMOUS DE BETTERAVE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Equals("MACARONI TEX", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Poissons", StringComparison.OrdinalIgnoreCase) && title.Contains("douce", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Equals("Recettes faciles", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("PLATS PRINCI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("CON GELO", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("KAKILES", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("stage=candidate_pool", StringComparison.OrdinalIgnoreCase)
            && line.Contains("raw_candidates=", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(joined));
    }

    [Fact]
    public void Structured_meal_planning_cleans_live_ocr_context_suffixes_and_field_fragments()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                BuildObservedBackendRecipeInventoryHit(
                    new[] { "SERTSDES-SERTSSCONES AUX CANNEBERGES" },
                    "SCONES AUX CANNEBERGES",
                    "SCONES AUX CANNEBERGES. Ingredients : farine, canneberges, lait et beurre. Preparation : melanger, former les scones, cuire au four et servir.",
                    "petit-dejeuner recettes",
                    40,
                    includeCardEvidence: true),
                BuildObservedBackendRecipeInventoryHit(
                    new[] { "Sel et poivre LEGUMINEUSES AU CARI PLATS PRINCI- PAUX", "LEGUMINEUSES AU CARI PLATS PRINCI- PAUX" },
                    "LEGUMINEUSES AU CARI",
                    "LEGUMINEUSES AU CARI. Ingredients : legumineuses, sel, poivre et cari. Preparation : mijoter, assaisonner et servir.",
                    "diner plats",
                    41,
                    includeCardEvidence: true),
                BuildObservedBackendRecipeInventoryHit(
                    new[] { "Persil" },
                    "Moules marinieres",
                    "Moules marinieres Pour 6 personnes : moules, echalotes, celeri, vin blanc, beurre et persil. Preparation : faire revenir, cuire les moules et servir.",
                    "diner recettes",
                    42,
                    includeCardEvidence: true),
                BuildObservedBackendRecipeInventoryHit(
                    new[] { "P 2 pendant", "Sel et poivre" },
                    "Galettes au gruau",
                    "Galettes au gruau. Ingredients : gruau, lait, oeuf, sel et poivre. Preparation : melanger, former les galettes, cuire et servir.",
                    "petit-dejeuner recettes",
                    43,
                    includeCardEvidence: true)
            }
        });
        using var doc = JsonDocument.Parse(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = doc.RootElement.Clone() });

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 8);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");
        var joined = string.Join(" | ", titles);

        Assert.Contains(titles, static title => title.Contains("SCONES AUX CANNEBERGES", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("LEGUMINEUSES AU CARI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("SERTSDES", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Sel et poivre", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("PLATS PRINCI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => string.Equals(title, "Persil", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("P 2 pendant", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => title.Contains("Galettes au gruau", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("stage=candidate_pool", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(joined));
    }

    [Fact]
    public void Structured_meal_planning_rejects_observed_inventory_section_titles_even_with_recipe_cues()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var noisyTitles = new[]
        {
            "4 \u00c0 A VOIR DANS SON CON GELO!",
            "Poissons d\u2019eau douce",
            "PANIER VAPEUR",
            "Sel Poivre Persil Placer la lame dans le r\u00e9cipient",
            "D\u00e9guster apr\u00e8s avoir d\u00e9cor\u00e9",
            "Farcir les \u0153ufs",
            "Couvrir avec un film plastique et r\u00e9server au r\u00e9frig\u00e9rateur",
            "RECETTES FACILES AVEC L\u00c9GUMINEUSES BROWNIES",
            "RECETTES FACILES",
            "INGR\u00c9DIENTS",
            "I pinc\u00e9e de persil hach\u00e9",
            "I branche thym",
            "Apporter une collation",
            "Je mange",
            "Je mange un fruit",
            "Voici quelques",
            "Plats au four",
            "P\u00e2tisserie sal\u00e9e",
            "Volailles Poule",
            "Volailles Poule Modes",
        };
        var payload = JsonSerializer.Serialize(new
        {
            hits = noisyTitles
                .Select((title, index) => BuildObservedSlotRoutedRecipeHit(title, "gouter recettes", index + 1))
                .Concat(new[] { BuildObservedSlotRoutedRecipeHit("SCONES AUX CANNEBERGES", "gouter recettes", 9) })
                .ToArray(),
        });
        using var doc = JsonDocument.Parse(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = doc.RootElement.Clone() });

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 8);
        var joined = string.Join(" | ", titles);

        Assert.Contains(titles, static title => title.Contains("SCONES AUX CANNEBERGES", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("CON GELO", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Poissons", StringComparison.OrdinalIgnoreCase) && title.Contains("douce", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("PANIER VAPEUR", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Placer la lame", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("D\u00e9guster", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Farcir", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Couvrir avec un film", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("RECETTES FACILES", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("BROWNIES", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("INGR", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("pinc", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("branche thym", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Apporter", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Je mange", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Voici quelques", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Equals("Plats au four", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Equals("P\u00e2tisserie sal\u00e9e", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Equals("Volailles Poule", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Equals("Volailles Poule Modes", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(joined));
    }

    [Fact]
    public void Structured_meal_planning_cleans_variant_suffixes_and_drops_truncated_or_audience_titles()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                BuildObservedSlotRoutedRecipeHit("Scones aux canne", "petit-dejeuner recettes", 1),
                BuildObservedSlotRoutedRecipeHit("Scones aux canneberges", "petit-dejeuner recettes", 2),
                BuildObservedSlotRoutedRecipeHit("Muffins pommes et cheddar Variante 2 Variante 3 Muffins pommes et cheddar", "recettes petit-dejeuner", 3),
                BuildObservedSlotRoutedRecipeHit("Parents presses", "gouter recettes", 4),
                BuildObservedSlotRoutedRecipeHit("BROWNIES AUX HARICOTS JR Da Bu", "recettes collation", 5)
            }
        });
        using var doc = JsonDocument.Parse(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = doc.RootElement.Clone() });

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 8);
        var joined = string.Join(" | ", titles);

        Assert.Contains(titles, static title => string.Equals(title, "Scones aux canneberges", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => string.Equals(title, "Scones aux canne", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => string.Equals(title, "Muffins pommes et cheddar", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Variante", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Parents presses", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, static title => string.Equals(title, "Brownies aux haricots", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("JR Da Bu", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(joined));
    }

    [Fact]
    public void Structured_meal_planning_keeps_page_embedded_recipe_title_when_page_text_starts_after_title()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[] { BuildObservedPageEmbeddedRecipeTitleWithClippedPageTextHit() }
        });
        using var doc = JsonDocument.Parse(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = doc.RootElement.Clone() });

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 8);
        var joined = string.Join(" | ", titles);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query);

        Assert.True(
            titles.Any(static title => title.Contains("Omelette italienne", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, trace));
        Assert.DoesNotContain(titles, static title => title.Contains("Rincer le basilic", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Verser un quart", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(joined));
    }

    [Fact]
    public void Structured_meal_planning_extracts_leading_ocr_title_before_compact_context_label()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[] { BuildObservedLeadingOcrContextLabelHit() }
        });
        using var doc = JsonDocument.Parse(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = doc.RootElement.Clone() });

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 8);
        var joined = string.Join(" | ", titles);
        const string directProof = "FAJITAS DEJEUNER A JOSIANEPETITS DEJ TRUCS CULINAIRES 1. Il est possible de faire revenir les legumes quelques minutes. INGREDIENTS : tortillas, oeufs, legumes et sauce. PREPARATION : garnir, chauffer et servir.";
        var directExtraction = ToolAgentOrchestrator.ExtractPlanItemTitleV2ForTests(directProof);
        var cleanedDirectExtraction = ToolAgentOrchestrator.FormatSourceBackedPlanningDisplayTitleForTests(directExtraction);
        var strictTitles = ToolAgentOrchestrator.StrictSourceBackedOptionTitlesForTests(toolResults, query);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.True(
            titles.Any(static title => title.Contains("FAJITAS", StringComparison.OrdinalIgnoreCase)
                && title.Contains("JOSIANE", StringComparison.OrdinalIgnoreCase)
                && !title.Contains("PETITS DEJ", StringComparison.OrdinalIgnoreCase)),
            $"directExtraction={directExtraction}; cleaned={cleanedDirectExtraction}; strict={string.Join(" | ", strictTitles)}; titles={joined}{Environment.NewLine}{string.Join(Environment.NewLine, trace)}");
        Assert.DoesNotContain(titles, static title => title.Contains("PETITS DEJ", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(joined));
    }

    [Fact]
    public void Structured_planning_rejects_short_delimited_field_value_fragments_as_candidates()
    {
        const string query = "Je cherche un plan documente pour la semaine avec plusieurs options concretes et des sources utiles.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                BuildGenericSlotRoutedRecipeHit("Documented option alpha", "options concretes", 1),
                BuildDelimitedFieldValueFragmentHit(),
                BuildInlineDelimitedListFragmentHit()
            }
        });
        using var doc = JsonDocument.Parse(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = doc.RootElement.Clone() });

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 8);

        Assert.Contains(titles, static title => title.Contains("Documented option alpha", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Clamp, bracket", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Salt, pepper", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Vague_weekly_meal_plan_is_not_adequate_with_only_four_supported_options()
    {
        const string query = "Je ne sais pas quoi faire pour les repas de cette semaine, tu peux me proposer un plan de repas pour la semaine a partir des documents disponibles, avec les sources utiles ?";
        var toolResults = BuildPlanningCandidateResults(4);

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);

        Assert.Equal(7, ToolAgentOrchestrator.ResolveSourceBackedPlanningTargetItemCountForTests(query));
        Assert.Equal(7, ToolAgentOrchestrator.ResolveMinimumSourceBackedPlanningCandidateCountForTests(query, 7, hasStructuredAxes: false));
        Assert.False(ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"));
        Assert.True(string.IsNullOrWhiteSpace(answer));
    }

    [Fact]
    public void Weekly_meal_plan_with_useful_partial_recipe_coverage_returns_sourced_candidate_bank()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et user-friendly, et ajoute seulement les sources vraiment utiles.";
        var toolResults = BuildObservedPartialSlotRoutedMealPlanningResults();

        var answer = ToolAgentOrchestrator.TryBuildInsufficientStructuredPlanningBeforeWriterAnswerForTests(toolResults, query, "fr");
        var normalized = RemoveDiacriticsForAssertion(answer);

        Assert.False(ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(toolResults, query, "fr"));
        Assert.Contains("sources recuperees couvrent seulement une partie", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Scones aux canneberges", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nouilles sauce cacahuete", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Compote de pommes", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Houmous de betterave", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(9, CountInlineOpenTokens(answer));
        Assert.DoesNotContain("trop limitees", normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Vague_weekly_meal_plan_writer_guard_uses_raw_adequate_research_over_truncated_writer_context()
    {
        const string query = "Je ne sais pas quoi faire pour les repas de cette semaine, tu peux me proposer un plan de repas pour la semaine a partir des documents disponibles, avec les sources utiles ?";
        var rawToolResults = BuildPlanningCandidateResults(15);
        var writerToolResults = BuildPlanningCandidateResults(3);
        AddToolJson(
            rawToolResults,
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docPath = "Knowledge/category/weekly-anchor.pdf",
                        docName = "weekly-anchor.pdf",
                        pageStart = 99,
                        pageEnd = 99,
                        excerpt = "Repas de semaine : Omelette aux herbes. Ingredients: 2 documented components. Preparation: add, cook and serve this documented item.",
                        fullText = "Repas de semaine : Omelette aux herbes. Ingredients: 2 documented components. Preparation: add, cook and serve this documented item.",
                        matchedContentCards = new[] { new { title = "Omelette aux herbes", kind = "unit_lead" } },
                        score = 0.95
                    }
                }
            });

        Assert.True(ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(rawToolResults, query, "fr"));
        Assert.False(ToolAgentOrchestrator.IsSourceBackedPlanningCoverageAdequateForTests(writerToolResults, query, "fr"));
        Assert.True(string.IsNullOrWhiteSpace(ToolAgentOrchestrator.TryBuildInsufficientStructuredPlanningBeforeWriterAnswerForTests(rawToolResults, query, "fr")));
        Assert.False(string.IsNullOrWhiteSpace(ToolAgentOrchestrator.TryBuildInsufficientStructuredPlanningBeforeWriterAnswerForTests(writerToolResults, query, "fr")));
        Assert.Equal(
            "raw_tool_results",
            ToolAgentOrchestrator.ResolveStructuredPlanningWriterGuardBasisForTests(rawToolResults, writerToolResults, query, "fr"));
    }

    [Fact]
    public void Structured_planning_rejects_generic_timing_context_as_plan_items()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildGenericPlanningContextResults();

        Assert.False(ToolAgentOrchestrator.ShouldAllowWriterForPartialSourceBackedPlanningForTests(toolResults, query, "fr"));

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");
        Assert.True(titles.Length == 0, "Unexpected candidates: " + string.Join(" | ", titles));

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        Assert.True(string.IsNullOrWhiteSpace(answer));
    }

    [Fact]
    public void Structured_planning_trace_exposes_query_passes_and_rejected_generic_context()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildGenericPlanningContextResults();

        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.Contains(trace, static line => line.Contains("stage=scope", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("stage=query|pass=primary", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("stage=query|pass=exploration", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("decision=rejected", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("decision=rejected", StringComparison.OrdinalIgnoreCase)
            && line.Contains("reason=", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            trace.Any(static line => line.Contains("decision=rejected", StringComparison.OrdinalIgnoreCase)
                && HasPrecisePlanningCandidateRejectionReason(line)),
            string.Join(Environment.NewLine, trace.Where(static line => line.Contains("decision=rejected", StringComparison.OrdinalIgnoreCase))));
        Assert.DoesNotContain(trace, static line => line.Contains("reason=unusable_or_generic_candidate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(trace, static line => line.Contains("decision=accepted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("accepted_candidates=0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rag_trace_line_uses_stable_event_tags_and_bounded_fields()
    {
        var line = ToolAgentOrchestrator.BuildRagTraceLineForTests(
            "rag.search.query",
            ("query", "chercher un document tres precis avec beaucoup de contexte pour verifier la troncature et la stabilite de la ligne"),
            ("hits", 3),
            ("busy", false),
            ("tools", new[] { "rag.search", "rag.multi_search" }));

        Assert.StartsWith("[RAG_TRACE ", line);
        Assert.Contains("event=rag.search.query", line);
        Assert.Contains("trace_id=test-trace", line);
        Assert.Contains("seq=7", line);
        Assert.Contains("elapsed_ms=123", line);
        Assert.Contains("query=", line);
        Assert.Contains("hits=3", line);
        Assert.Contains("busy=false", line);
        Assert.Contains("tools=[", line);
    }

    [Fact]
    public void Structured_planning_trace_exposes_accepted_concrete_recipe_candidates()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(15);

        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.Contains(trace, static line => line.Contains("decision=accepted", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Omelette aux herbes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("decision=accepted", StringComparison.OrdinalIgnoreCase)
            && line.Contains("candidate_key=", StringComparison.OrdinalIgnoreCase)
            && line.Contains("page_key=", StringComparison.OrdinalIgnoreCase)
            && line.Contains("strict_evidence=true", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("accepted_candidates=15", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("stage=summary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Broad_planning_support_accepts_exact_candidate_title()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildConcreteRecipePlanningResults();
        const string answer = """
            Lundi :
            - Petit-dejeuner : Smoothie banane lait de coco
            """;

        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");

        Assert.Equal(1, stats.ItemCount);
        Assert.Equal(1, stats.SupportedItemCount);
        Assert.Equal(1, stats.SourceCount);
    }

    [Fact]
    public void Broad_planning_support_rejects_candidate_title_with_unproved_added_terms()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildConcreteRecipePlanningResults();
        const string answer = """
            Lundi :
            - Petit-dejeuner : Smoothie banane lait de coco avec miel et graines de chia
            """;

        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");

        Assert.Equal(1, stats.ItemCount);
        Assert.Equal(0, stats.SupportedItemCount);
        Assert.Equal(0, stats.SourceCount);
        Assert.True(ToolAgentOrchestrator.ShouldRejectUnsupportedPlanningAnswerForFinalForTests(answer, toolResults, query, "fr"));
    }

    [Fact]
    public void Broad_planning_support_rejects_long_item_when_candidate_title_has_unproved_qualifiers()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildConcreteRecipePlanningResults();
        const string answer = """
            Lundi :
            - Petit-dejeuner : Smoothie banane lait de coco ingredients preparation add cook serve avec truffe caviar
            """;

        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");

        Assert.Equal(1, stats.ItemCount);
        Assert.Equal(0, stats.SupportedItemCount);
        Assert.Equal(0, stats.SourceCount);
        Assert.True(ToolAgentOrchestrator.ShouldRejectUnsupportedPlanningAnswerForFinalForTests(answer, toolResults, query, "fr"));
    }

    [Fact]
    public void Structured_planning_candidate_validation_keeps_real_candidates_when_generic_context_is_mixed_in()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildMixedGenericAndRecipePlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 20);
        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");

        Assert.Equal(15, titles.Length);
        Assert.DoesNotContain(titles, static title => title.Contains("Repas leger entre minuit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Cuisiner un ou deux repas par semaine", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, static line => line.Contains("decision=accepted", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Omelette aux herbes", StringComparison.OrdinalIgnoreCase)
            && line.Contains("strict_evidence=true", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            trace.Any(static line => line.Contains("decision=rejected", StringComparison.OrdinalIgnoreCase)
                && HasPrecisePlanningCandidateRejectionReason(line)),
            string.Join(Environment.NewLine, trace.Where(static line => line.Contains("decision=rejected", StringComparison.OrdinalIgnoreCase))));
        Assert.DoesNotContain(trace, static line => line.Contains("reason=unusable_or_generic_candidate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(trace, static line => line.Contains("decision=accepted", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Repas leger entre minuit", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(15, stats.ItemCount);
        Assert.Equal(15, stats.SupportedItemCount);
        Assert.Equal(15, stats.SourceCount);
    }

    [Fact]
    public void Structured_planning_readable_fallback_rejects_generic_timing_context()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildGenericPlanningContextResults();

        var answer = ToolAgentOrchestrator.BuildReadableSourceBackedCandidateListFallbackAnswerForTests(
            toolResults,
            query,
            "fr");

        Assert.True(string.IsNullOrWhiteSpace(answer));
    }

    [Fact]
    public void Structured_planning_readable_fallback_rejects_profile_and_detached_card_options()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildProfileOnlyPlanningResults();
        foreach (var item in BuildDetachedExplicitPagedCardEvidencePlanningResults().Items)
            toolResults.Items.Add(item);

        var answer = ToolAgentOrchestrator.BuildReadablePartialPlanningEvidenceAnswerForTests(
            toolResults,
            query,
            "fr");
        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.Empty(titles);
        Assert.Contains(trace, static line => line.Contains("decision=rejected", StringComparison.OrdinalIgnoreCase)
            && line.Contains("missing_direct_candidate_evidence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Smoothie", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Omelette", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Options directement utilisables", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Structured_planning_runtime_blocks_writer_when_retrieval_has_only_generic_context()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        const string genericPlanningPayload = """
        {
          "items": [
            {
              "score": 0.95,
              "docPath": "Cuisine/facilitemps.pdf",
              "docName": "facilitemps.pdf",
              "pageStart": 38,
              "pageEnd": 38,
              "text": "Cuisiner un ou deux repas par semaine. Conseils generaux d'organisation et de preparation.",
              "fullText": "Cuisiner un ou deux repas par semaine. Conseils generaux d'organisation et de preparation.",
              "matchedContentCards": [ { "title": "Cuisiner un ou deux repas par semaine", "kind": "unit_lead" } ]
            },
            {
              "score": 0.93,
              "docPath": "Cuisine/livre-recette-sist-2025-web.pdf",
              "docName": "livre-recette-sist-2025-web.pdf",
              "pageStart": 10,
              "pageEnd": 10,
              "text": "Petit dejeuner ou collation au lever. Dejeuner a la mi-journee. Diner leger le soir.",
              "fullText": "Petit dejeuner ou collation au lever. Dejeuner a la mi-journee. Diner leger le soir.",
              "matchedContentCards": [ { "title": "Repas leger entre minuit et 1 heure du matin", "kind": "unit_lead" } ]
            }
          ]
        }
        """;

        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/rag/search" or "/rag/multi-search" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(genericPlanningPayload, Encoding.UTF8, "application/json")
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var llm = new RouterOnlyLlmClient(
            """
            {
              "mode": "strict",
              "language": "fr",
              "intent": "rag.answer",
              "responseFormat": "auto",
              "needClarification": false,
              "clarificationQuestions": [],
              "reasoningTracePublic": [],
              "riskFlags": [],
              "memoryUpdate": null,
              "routerConfidence": 0.99,
              "toolCalls": [
                {
                  "name": "rag.multi_search",
                  "args": {
                    "queries": [
                      "plan repas semaine petit dejeuner dejeuner diner",
                      "recettes faciles petit dejeuner dejeuner diner"
                    ],
                    "topK": 8,
                    "mode": "deep"
                  }
                }
              ]
            }
            """);

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm, new ToolMemory());

        var (answer, sources) = await sut.RunAsync(
            Array.Empty<(string role, string content)>(),
            query,
            CancellationToken.None);

        Assert.Contains("sources", answer, StringComparison.OrdinalIgnoreCase);
        var normalizedAnswer = RemoveDiacriticsForAssertion(answer);
        Assert.True(
            normalizedAnswer.Contains("trop limit", StringComparison.OrdinalIgnoreCase)
            || normalizedAnswer.Contains("pas encore trouve assez", StringComparison.OrdinalIgnoreCase),
            answer);
        Assert.DoesNotContain("Smoothie", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Salade de quinoa", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Poulet roti", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Null(sources);
        Assert.NotEmpty(llm.RouterRequests);
        Assert.Empty(llm.WriterRequests);
    }

    [Fact]
    public async Task Structured_planning_with_no_invention_policy_uses_exploration_instead_of_source_policy_search()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi. N'invente rien.";
        const string genericPlanningPayload = """
        {
          "items": [
            {
              "score": 0.95,
              "docPath": "Cuisine/facilitemps.pdf",
              "docName": "facilitemps.pdf",
              "pageStart": 38,
              "pageEnd": 38,
              "text": "Cuisiner un ou deux repas par semaine. Conseils generaux d'organisation et de preparation.",
              "fullText": "Cuisiner un ou deux repas par semaine. Conseils generaux d'organisation et de preparation.",
              "matchedContentCards": [ { "title": "Cuisiner un ou deux repas par semaine", "kind": "unit_lead" } ]
            }
          ]
        }
        """;
        var requestedPaths = new List<string>();
        var requestedBodies = new List<string>();

        var handler = new StubHttpHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.AbsolutePath);
            requestedBodies.Add(req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return req.RequestUri!.AbsolutePath switch
            {
                "/rag/search" or "/rag/multi-search" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(genericPlanningPayload, Encoding.UTF8, "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var llm = new RouterOnlyLlmClient(
            """
            {
              "mode": "strict",
              "language": "fr",
              "intent": "rag.answer",
              "responseFormat": "auto",
              "needClarification": false,
              "clarificationQuestions": [],
              "reasoningTracePublic": [],
              "riskFlags": [],
              "memoryUpdate": null,
              "routerConfidence": 0.99,
              "toolCalls": [
                {
                  "name": "rag.multi_search",
                  "args": {
                    "queries": ["plan repas semaine"],
                    "topK": 4,
                    "mode": "balanced"
                  }
                }
              ]
            }
            """);

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm, new ToolMemory());

        await sut.RunAsync(
            Array.Empty<(string role, string content)>(),
            query,
            CancellationToken.None);

        var requestLog = string.Join("\n---\n", requestedPaths.Zip(requestedBodies, (path, body) => $"{path}\n{body}"));
        Assert.Contains(requestedPaths, static path => string.Equals(path, "/rag/search", StringComparison.Ordinal));
        Assert.Contains(requestedBodies, static body =>
            body.Contains("\"researchMode\":\"source_exploration\"", StringComparison.OrdinalIgnoreCase)
            && body.Contains("\"includeResearchSurfaces\":true", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestedBodies, static body =>
            body.Contains("recettes", StringComparison.OrdinalIgnoreCase)
            || body.Contains("plats", StringComparison.OrdinalIgnoreCase)
            || body.Contains("petit-dejeuner", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requestedBodies, LooksLikeNavigationDiscoveryBody);
        Assert.DoesNotContain(requestedBodies, static body =>
            body.Contains("\"mode\":\"balanced\"", StringComparison.OrdinalIgnoreCase)
            && body.Contains("plan repas semaine", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(llm.RouterRequests);
        Assert.Empty(llm.WriterRequests);

        static bool LooksLikeNavigationDiscoveryBody(string body)
            => body.Contains("sommaire", StringComparison.OrdinalIgnoreCase)
               || body.Contains("table des matieres", StringComparison.OrdinalIgnoreCase)
               || body.Contains("sections principales", StringComparison.OrdinalIgnoreCase)
               || body.Contains("\"index\"", StringComparison.OrdinalIgnoreCase)
               || body.Contains(" index ", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_planning_readable_fallback_does_not_emit_partial_grid_as_complete_leads()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildConcreteRecipePlanningResults();

        var answer = ToolAgentOrchestrator.BuildReadableSourceBackedCandidateListFallbackAnswerForTests(
            toolResults,
            query,
            "fr");

        Assert.True(string.IsNullOrWhiteSpace(answer));
    }

    [Fact]
    public void Structured_planning_rejects_current_ui_like_unsupported_meal_plan()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(15);
        var unsupportedAnswer = """
            Voici un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi, base sur les informations disponibles :

            Lundi :
            - Petit-dejeuner : Smoothie a l'ananas et au lait de soja
            - Dejeuner : Salade de quinoa avec legumes et tofu grille
            - Diner : Poulet roti avec des pommes de terre et des legumes vapeur

            Mardi :
            - Petit-dejeuner : Yogourt grec aux fruits rouges et a l'abricot
            - Dejeuner : Pates aux champignons et aux herbes
            - Diner : Poisson grille avec des legumes au four

            Mercredi :
            - Petit-dejeuner : Pain complet avec compote de pommes et oeufs
            - Dejeuner : Salade de quinoa avec legumes et tofu grille
            - Diner : Poulet roti avec des pommes de terre et des legumes vapeur

            Jeudi :
            - Petit-dejeuner : Smoothie a l'ananas et au lait de soja
            - Dejeuner : Salade de quinoa avec legumes et tofu grille
            - Diner : Poulet roti avec des pommes de terre et des legumes vapeur

            Vendredi :
            - Petit-dejeuner : Yogourt grec aux fruits rouges et a l'abricot
            - Dejeuner : Pates aux champignons et aux herbes
            - Diner : Poisson grille avec des legumes au four
            """;

        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            unsupportedAnswer,
            toolResults,
            query,
            "fr");

        Assert.Equal(15, stats.ItemCount);
        Assert.Equal(0, stats.SupportedItemCount);
        Assert.Equal(0, stats.SourceCount);
        Assert.True(ToolAgentOrchestrator.ShouldRejectUnsupportedPlanningAnswerForFinalForTests(unsupportedAnswer, toolResults, query, "fr"));
    }

    [Fact]
    public void Structured_planning_finalizer_removes_sources_from_unsupported_writer_plan()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildConcreteRecipePlanningResults();
        var unsupportedAnswer = """
            Voici un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi, base sur les informations disponibles :

            Lundi :
            - Petit-dejeuner : Smoothie a l'ananas et au lait de soja
            - Dejeuner : Salade de quinoa avec legumes et tofu grille
            - Diner : Poulet roti avec des pommes de terre et des legumes vapeur

            Mardi :
            - Petit-dejeuner : Yogourt grec aux fruits rouges et a l'abricot
            - Dejeuner : Pates aux champignons et aux herbes
            - Diner : Poisson grille avec des legumes au four
            """;

        var finalized = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            unsupportedAnswer,
            toolResults,
            query,
            "fr");

        Assert.True(finalized.Applied);
        Assert.Equal(0, finalized.SourceCount);
        Assert.Contains("rejected", finalized.Resolution, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Smoothie a l'ananas", finalized.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Documentary_planning_finalizer_rejects_partial_source_coverage_instead_of_showing_decorative_sources()
    {
        const string query = "Je veux un plan documente pour organiser des repas rapides avec les sources disponibles.";
        var toolResults = BuildConcreteRecipePlanningResults();
        var partiallySupportedAnswer = """
            Voici une proposition :

            - Smoothie banane lait de coco
            - Salade de quinoa avec legumes et tofu grille
            """;

        var finalized = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            partiallySupportedAnswer,
            toolResults,
            query,
            "fr");

        Assert.True(finalized.Applied);
        Assert.True(finalized.ItemCount >= 2);
        Assert.True(finalized.SupportedItemCount < finalized.ItemCount);
        Assert.Equal(0, finalized.SourceCount);
        Assert.Contains("rejected", finalized.Resolution, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Salade de quinoa", finalized.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_planning_finalizer_rebuilds_when_supported_candidates_cover_the_request()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(15);
        var unsupportedAnswer = """
            Voici un plan de repas pour la semaine :
            - Petit-dejeuner : Smoothie invente
            - Dejeuner : Salade inventee
            - Diner : Plat invente
            """;

        var finalized = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            unsupportedAnswer,
            toolResults,
            query,
            "fr");

        Assert.True(finalized.Applied);
        Assert.Equal(15, finalized.SourceCount);
        Assert.Equal(15, finalized.SourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        for (var i = 1; i <= 15; i++)
        {
            Assert.Contains($"Knowledge/category/source-{i}.pdf|{i}|{i}", finalized.SourceKeys);
        }

        Assert.Equal(15, finalized.SupportedItemCount);
        Assert.Contains("supported_rebuild", finalized.Resolution, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Omelette aux herbes", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Macaroni tex mex", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Smoothie invente", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Salade inventee", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Plat invente", finalized.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_planning_finalizer_prefers_rebuilt_answer_even_when_writer_items_are_supported()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(15);
        var writerAnswer = """
            Voici un plan de repas pour la semaine :

            Lundi :
            - Petit-dejeuner : Omelette aux herbes
            - Dejeuner : Croquettes de poulet
            - Diner : Salade de lentilles

            Mardi :
            - Petit-dejeuner : Gratin de courgettes
            - Dejeuner : Poisson au four
            - Diner : Riz aux legumes

            Mercredi :
            - Petit-dejeuner : Pates tomate basilic
            - Dejeuner : Poulet au citron
            - Diner : Galettes au gruau

            Jeudi :
            - Petit-dejeuner : Sandwich au tofu
            - Dejeuner : Soupe de legumes
            - Diner : Quiche aux champignons

            Vendredi :
            - Petit-dejeuner : Brownies aux haricots noirs
            - Dejeuner : Smoothie banane coco
            - Diner : Macaroni tex mex
            """;

        var finalized = ToolAgentOrchestrator.FinalizeSourceBackedPlanningResponseForTests(
            writerAnswer,
            toolResults,
            query,
            "fr");

        Assert.True(finalized.Applied);
        Assert.Equal(15, finalized.SourceCount);
        Assert.Equal(15, finalized.SupportedItemCount);
        Assert.Contains("supported_rebuild", finalized.Resolution, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[[open|Knowledge/category/source-1.pdf|1|source-1.pdf", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[[open|Knowledge/category/source-15.pdf|15|source-15.pdf", finalized.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(15, CountInlineOpenTokens(finalized.Answer));
        Assert.DoesNotContain("\nSource:", finalized.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_planning_rejects_empty_slot_plan_even_when_it_looks_formatted()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningCandidateResults(15);
        var malformedPlan = """
            Voici un plan de repas pour la semaine :

            Lundi :
            - Petit-dejeuner :
            - Dejeuner :
            - Diner :

            Mardi :
            - Petit-dejeuner :
            - Dejeuner :
            - Diner :

            Mercredi :
            - Petit-dejeuner :
            - Dejeuner :
            - Diner :
            """;

        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            malformedPlan,
            toolResults,
            query,
            "fr");

        Assert.Equal(0, stats.SupportedItemCount);
        Assert.True(ToolAgentOrchestrator.ShouldRejectUnsupportedPlanningAnswerForFinalForTests(malformedPlan, toolResults, query, "fr"));
    }

    [Fact]
    public void Structured_planning_drops_visible_sources_that_are_not_cited_in_the_answer()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var sources = new[]
        {
            new ToolMemory.SourceRef
            {
                DocPath = "Cuisine/facilitemps.pdf",
                DocName = "facilitemps.pdf",
                Label = "facilitemps.pdf",
                PageStart = 91,
                PageEnd = 91
            }
        };

        var reconciled = ToolAgentOrchestrator.ReconcileRequiredVisibleSourcesWithFinalAnswerForTests(
            "Voici un plan :\n- Petit-dejeuner : Smoothie invente\n- Dejeuner : Salade inventee\n- Diner : Plat invente",
            sources,
            query);

        Assert.Empty(reconciled);
    }

    [Fact]
    public void Structured_planning_deduplicates_visible_source_aliases_for_the_same_page()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var sources = new[]
        {
            new ToolMemory.SourceRef
            {
                DocPath = @"C:\saaia-repo\documents\Cuisine\facilitemps.pdf",
                DocName = "facilitemps.pdf",
                Label = "facilitemps.pdf",
                SourceHash = "sha256:same",
                PageStart = 91,
                PageEnd = 91
            },
            new ToolMemory.SourceRef
            {
                DocPath = "Cuisine/facilitemps.pdf",
                DocName = "facilitemps.pdf",
                Label = "facilitemps.pdf",
                SourceHash = "sha256:same",
                PageStart = 91,
                PageEnd = 91
            }
        };

        var reconciled = ToolAgentOrchestrator.ReconcileRequiredVisibleSourcesWithFinalAnswerForTests(
            "Voici un plan :\n- Petit-dejeuner : Smoothie documente (facilitemps.pdf p.91)",
            sources,
            query);

        Assert.Single(reconciled);
        Assert.Equal(91, reconciled[0].PageStart);
    }

    [Fact]
    public void Structured_planning_deduplicates_visible_source_ranges_for_the_same_display_page()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var sources = new[]
        {
            new ToolMemory.SourceRef
            {
                DocPath = "Cuisine/livre-recette-sist-2025-web.pdf",
                DocName = "livre-recette-sist-2025-web.pdf",
                Label = "livre-recette-sist-2025-web.pdf",
                PageStart = 10,
                PageEnd = 10
            },
            new ToolMemory.SourceRef
            {
                DocPath = "Cuisine/livre-recette-sist-2025-web.pdf",
                DocName = "livre-recette-sist-2025-web.pdf",
                Label = "livre-recette-sist-2025-web.pdf",
                PageStart = 10,
                PageEnd = 20
            }
        };

        var reconciled = ToolAgentOrchestrator.ReconcileRequiredVisibleSourcesWithFinalAnswerForTests(
            "Voici un plan :\n- Petit-dejeuner : Collation documentee (livre-recette-sist-2025-web.pdf p.10)",
            sources,
            query);

        Assert.Single(reconciled);
        Assert.Equal(10, reconciled[0].PageStart);
    }

    [Fact]
    public void Structured_planning_does_not_treat_profile_titles_as_final_page_proof()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildProfileOnlyPlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");
        Assert.True(titles.Length == 0, "Unexpected candidates: " + string.Join(" | ", titles));

        var answer = """
            Voici un plan de repas pour la semaine :

            Lundi :
            - Petit-dejeuner : Smoothie a l'ananas et au lait de soja
            - Dejeuner : Salade de quinoa avec legumes et tofu grille
            - Diner : Poulet roti avec pommes de terre

            Mardi :
            - Petit-dejeuner : Yaourt grec aux fruits rouges
            - Dejeuner : Pates aux champignons
            - Diner : Poisson grille avec legumes
            """;

        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");

        Assert.Equal(6, stats.ItemCount);
        Assert.Equal(0, stats.SupportedItemCount);
        Assert.Equal(0, stats.SourceCount);
        Assert.True(ToolAgentOrchestrator.ShouldRejectUnsupportedPlanningAnswerForFinalForTests(answer, toolResults, query, "fr"));
    }

    [Fact]
    public void Structured_planning_exploration_reads_candidate_lists_before_falling_back_to_close_passages()
    {
        const string query = "Je cherche a avoir un plan documente pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";

        var queries = ToolAgentOrchestrator.BuildPlanningExplorationRetrievalQueriesForTests(query);

        var navigationIndex = Array.FindIndex(queries, q => string.Equals(q, "sommaire", StringComparison.OrdinalIgnoreCase));
        var candidateIndex = Array.FindIndex(queries, q => q.Contains("options", StringComparison.OrdinalIgnoreCase)
                                                          || q.Contains("candidats", StringComparison.OrdinalIgnoreCase)
                                                          || q.Contains("propositions", StringComparison.OrdinalIgnoreCase)
                                                          || q.Contains("preparations", StringComparison.OrdinalIgnoreCase));
        Assert.True(candidateIndex >= 0, "Expected concrete candidate discovery queries.");
        Assert.True(navigationIndex < 0 || candidateIndex < navigationIndex, "Candidate discovery should be tried before navigation-only probes.");
        Assert.DoesNotContain(queries.Take(8), q => string.Equals(q, "sommaire", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries.Take(8), q => string.Equals(q, "index", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries.Take(12), q => q.Contains("options", StringComparison.OrdinalIgnoreCase)
                                            || q.Contains("candidats", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries.Take(12), q => q.Contains("petit-dejeuner", StringComparison.OrdinalIgnoreCase)
                                            || q.Contains("midi", StringComparison.OrdinalIgnoreCase)
                                            || q.Contains("soir", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Structured_planning_document_navigation_followup_uses_older_page_anchors()
    {
        const string query = "Je cherche a avoir un plan documente pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningNavigationAnchorResults();

        var passes = ToolAgentOrchestrator.BuildSourceBackedDocumentScopedRouteAnchorFollowupPassesForTests(
            toolResults,
            query,
            "fr");

        var pass = Assert.Single(passes, p => string.Equals(p.DocPath, "Knowledge/weekly-options.pdf", StringComparison.OrdinalIgnoreCase)
                                             && p.PageStart == 42);
        Assert.Equal(43, pass.PageEnd);
        Assert.Contains(pass.Queries, q => q.Contains("Tarte tomates moutarde", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pass.Queries, q => q.Contains("page 42", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Structured_planning_document_navigation_followup_rejects_subjectless_standard_reference_anchors()
    {
        const string query = "Je ne sais pas quoi faire pour les repas de cette semaine, tu peux me proposer un plan de repas pour la semaine a partir des documents disponibles, avec les sources utiles ?";
        var toolResults = BuildOffTopicStandardNavigationAnchorResults();

        var passes = ToolAgentOrchestrator.BuildSourceBackedDocumentScopedRouteAnchorFollowupPassesForTests(
            toolResults,
            query,
            "fr");
        var globalQueries = ToolAgentOrchestrator.BuildSourceBackedRouteAnchorFollowupRetrievalQueriesForTests(
            toolResults,
            query,
            "fr");

        Assert.Empty(passes);
        Assert.DoesNotContain(globalQueries, q => q.Contains("DIN EN 1090", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Structured_meal_planning_navigation_followup_skips_observed_non_recipe_anchors()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.";
        var toolResults = BuildNoisyMealPlanningNavigationAnchorResults();

        var passes = ToolAgentOrchestrator.BuildSourceBackedDocumentScopedRouteAnchorFollowupPassesForTests(
            toolResults,
            query,
            "fr");
        var globalQueries = ToolAgentOrchestrator.BuildSourceBackedRouteAnchorFollowupRetrievalQueriesForTests(
            toolResults,
            query,
            "fr");
        var allQueries = passes.SelectMany(static pass => pass.Queries).Concat(globalQueries).ToArray();

        Assert.Contains(allQueries, static query => query.Contains("Muffins aux pommes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("INGR", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("PLANIFICATION DES REPAS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Référence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Reference", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("À partir de", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Sauce tomate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Sauce barbecue", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Repas de fetes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Graphic Impression", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("mayonnaise", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Sonde de rotissage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("MCRC01072538", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Guide de nuit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("parents presses", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Nombre de portions", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Fondation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Presentation d'une fiche", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Possibilite de l'evolution", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Quantites donnees", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("FOURCHETTESCOM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Dansune cocotte", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("poignee de feuilles", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("CUISINE DE NUIT", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("REFRIGERATEUR", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => RemoveDiacriticsForAssertion(query).Contains("A VOS COCOS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("FONCTION DES SAISONS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("PORTIONS INDIVIDUELLES", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("EXACTITUDE DES PRIX", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("PETITS MANGEURS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("UTILISE SI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("CRAQUE LINS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("COU PER EN TRANCHES", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Temps de", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("SPATULE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("Elle remue", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("AUX TOMATES SECHEES", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => string.Equals(query, "AU FROMAGE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Structured_meal_planning_navigation_followup_skips_ocr_organization_anchor_and_keeps_recipe_title()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.";
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "documents.navigation",
            new
            {
                items = new[]
                {
                    new
                    {
                        docId = "meal-nav-brownies",
                        docPath = "Knowledge/Meals/facilitemps.pdf",
                        docName = "facilitemps.pdf",
                        categoryPath = "Knowledge/Meals",
                        sectionTitle = "FONDATON OLO",
                        pageStart = 29,
                        targetPageStart = 29,
                        targetPageEnd = 29,
                        hasTargetAnchor = true,
                        confidence = 0.92
                    },
                    new
                    {
                        docId = "meal-nav-brownies",
                        docPath = "Knowledge/Meals/facilitemps.pdf",
                        docName = "facilitemps.pdf",
                        categoryPath = "Knowledge/Meals",
                        sectionTitle = "BROWNIES AUX HARICOTS JR Da Bu",
                        pageStart = 29,
                        targetPageStart = 29,
                        targetPageEnd = 29,
                        hasTargetAnchor = true,
                        confidence = 0.92
                    }
                }
            });

        var passes = ToolAgentOrchestrator.BuildSourceBackedDocumentScopedRouteAnchorFollowupPassesForTests(
            toolResults,
            query,
            "fr");
        var allQueries = passes.SelectMany(static pass => pass.Queries).ToArray();

        Assert.Contains(allQueries, static query => query.Contains("BROWNIES AUX HARICOTS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allQueries, static query => query.Contains("FONDATON", StringComparison.OrdinalIgnoreCase));
        var browniesPass = Assert.Single(passes);
        Assert.Contains("BROWNIES AUX HARICOTS", browniesPass.Queries[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_meal_planning_navigation_followup_prioritizes_complete_title_over_contained_fragment()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.";
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "documents.navigation",
            new
            {
                items = new[]
                {
                    new
                    {
                        docId = "meal-nav-cheese",
                        docPath = "Knowledge/Meals/recipes.pdf",
                        docName = "recipes.pdf",
                        categoryPath = "Knowledge/Meals",
                        sectionTitle = "AU FROMAGE",
                        pageStart = 22,
                        targetPageStart = 22,
                        targetPageEnd = 22,
                        hasTargetAnchor = true,
                        confidence = 0.92
                    },
                    new
                    {
                        docId = "meal-nav-cheese",
                        docPath = "Knowledge/Meals/recipes.pdf",
                        docName = "recipes.pdf",
                        categoryPath = "Knowledge/Meals",
                        sectionTitle = "CRÈME AU FROMAGE",
                        pageStart = 22,
                        targetPageStart = 22,
                        targetPageEnd = 22,
                        hasTargetAnchor = true,
                        confidence = 0.92
                    }
                }
            });

        var pass = Assert.Single(ToolAgentOrchestrator.BuildSourceBackedDocumentScopedRouteAnchorFollowupPassesForTests(
            toolResults,
            query,
            "fr"));

        Assert.Contains("CRÈME AU FROMAGE", pass.Queries[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            pass.Queries.Take(12),
            static query => string.Equals(query, "AU FROMAGE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Structured_meal_planning_navigation_followup_skips_candidate_titles_already_seen_in_rag_results()
    {
        const string query = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.";
        var toolResults = BuildAlreadyObservedMealPlanningNavigationAnchorResults();

        var passes = ToolAgentOrchestrator.BuildSourceBackedDocumentScopedRouteAnchorFollowupPassesForTests(
            toolResults,
            query,
            "fr");
        var allQueries = passes.SelectMany(static pass => pass.Queries).ToArray();

        Assert.DoesNotContain(allQueries, static query => query.Contains("Croquettes de poulet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(allQueries, static query => query.Contains("Muffins aux pommes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Structured_planning_summary_followup_preserves_page_metadata_for_bounded_search()
    {
        const string query = "Je cherche a avoir un plan documente pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildPlanningSummaryAnchorResults();

        var passes = ToolAgentOrchestrator.BuildSourceBackedDocumentScopedRouteAnchorFollowupPassesForTests(
            toolResults,
            query,
            "fr");

        var pass = Assert.Single(passes, p => string.Equals(p.DocPath, "Knowledge/summaries.pdf", StringComparison.OrdinalIgnoreCase)
                                             && p.PageStart == 18);
        Assert.Equal(18, pass.PageEnd);
        Assert.Contains(pass.Queries, q => q.Contains("Soupe froide concombre", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pass.Queries, q => q.Contains("page 18", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Structured_planning_llm_exploration_plan_accepts_document_and_page_scope()
    {
        var raw = """
            {
              "passes": [
                {
                  "label": "toc_followup",
                  "purpose": "read the page behind a table-of-contents clue",
                  "categoryScope": "Knowledge",
                  "docId": "doc-42",
                  "docPath": "Knowledge/manual.pdf",
                  "pageStart": 12,
                  "pageEnd": 14,
                  "queries": ["Tarte fine page 12"]
                }
              ]
            }
            """;

        var scopes = ToolAgentOrchestrator.ParseSourceBackedLlmEvidenceExplorationScopesForTests(raw);

        var scope = Assert.Single(scopes);
        Assert.Equal("toc_followup", scope.Label);
        Assert.Equal("doc-42", scope.DocId);
        Assert.Equal("Knowledge/manual.pdf", scope.DocPath);
        Assert.Equal(12, scope.PageStart);
        Assert.Equal(14, scope.PageEnd);
    }

    [Fact]
    public void Structured_planning_does_not_treat_recipe_index_pages_as_final_candidates()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildRecipeIndexOnlyPlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.True(titles.Length == 0, "Unexpected candidates: " + string.Join(" | ", titles));
        Assert.True(string.IsNullOrWhiteSpace(ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query)));
    }

    [Fact]
    public void Structured_planning_rejects_explicit_paged_card_evidence_when_it_is_only_index_navigation()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildExplicitPagedIndexCardPlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.DoesNotContain("Croquettes de poulet", titles);
        Assert.True(string.IsNullOrWhiteSpace(ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query)));
    }

    [Fact]
    public void Structured_planning_rejects_floating_card_title_without_page_local_proof()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildFloatingCardTitlePlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.DoesNotContain("Croquettes de poulet", titles);
        Assert.True(string.IsNullOrWhiteSpace(ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query)));
    }

    [Fact]
    public void Structured_planning_accepts_concrete_page_recipe_candidates()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildConcreteRecipePlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.Contains("Smoothie banane lait de coco", titles);
        Assert.Contains("Volaille a l'ananas et poivron rouge", titles);
    }

    [Fact]
    public void Structured_planning_extracts_french_ocr_recipe_titles_without_card_titles()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildFrenchOcrRecipePlanningResultsWithoutCards();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.Contains("SMOOTHIE VERT", titles);
        Assert.Contains("GALETTES AU GRUAU", titles);
        Assert.DoesNotContain(titles, static title => title.Contains("Ingredients", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Preparation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Structured_planning_accepts_recipe_card_when_local_proof_is_only_contextual_snippet()
    {
        const string query = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docPath = "Cuisine/PDF/Je_cuisine_simplement.pdf",
                        docName = "Je_cuisine_simplement.pdf",
                        pageStart = 55,
                        pageEnd = 55,
                        sectionTitle = "SCONES AUX CANNEBERGES",
                        headingPath = "SCONES AUX CANNEBERGES",
                        excerpt = "",
                        fullText = "",
                        contextualSnippet = "Matched direct_title_token_route: petit dejeuner recette SCONES AUX CANNEBERGES DES-SERTS Ingredients : farine, lait, beurre et canneberges. Preparation : melanger, cuire et servir.",
                        matchedContentCards = new[] { new { title = "SCONES AUX CANNEBERGES", kind = "page_embedded_title" } },
                        score = 0.86
                    }
                }
            });

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr", maxItems: 8);
        var trace = ToolAgentOrchestrator.BuildSourceBackedPlanningTraceLinesForTests(toolResults, query, "fr");

        Assert.True(
            titles.Contains("SCONES AUX CANNEBERGES", StringComparer.OrdinalIgnoreCase),
            "titles=" + string.Join(" | ", titles) + Environment.NewLine + string.Join(Environment.NewLine, trace));
        Assert.Contains(trace, static line => line.Contains("decision=accepted", StringComparison.OrdinalIgnoreCase)
            && line.Contains("SCONES AUX CANNEBERGES", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Structured_planning_accepts_short_page_local_candidates_with_long_evidence_bridge()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildShortTitleRecipePlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.Contains("Gratin dauphinois", titles);
        Assert.Contains("Osso buco", titles);
        Assert.Contains("Paella mixte", titles);
    }

    [Fact]
    public void Structured_planning_rejects_field_labels_and_ocr_fragments_as_candidates()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildFieldLabelPlanningFragmentResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.DoesNotContain("Ingredients", titles);
        Assert.DoesNotContain("Nombre de portions", titles);
        Assert.DoesNotContain(titles, static title => title.Contains("Nombre de galettes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Fagonner", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Ingrédients", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Elements", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("outils", StringComparison.OrdinalIgnoreCase));
        Assert.True(titles.Length == 0, "Unexpected candidates: " + string.Join(" | ", titles));
    }

    [Fact]
    public void Structured_planning_rejects_observed_recipe_ocr_and_procedure_noise()
    {
        const string query = "Je ne sais pas quoi faire pour les repas de cette semaine, propose un plan de repas a partir des documents disponibles.";
        var toolResults = BuildObservedNoisyRecipePlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.DoesNotContain(titles, static title => title.Contains("CON SERVATION", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("IDEES DE REPAS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Egoutter", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("CON COMBRE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Le poulet cuit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Rincer les blancs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Duree Apres le signal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Hacher Couper", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("TERMESCHALEUR", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, static title => title.Contains("Origins, institutional arrangements", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Salade de lentilles", titles);
        Assert.Contains("Gratin dauphinois", titles);
    }

    [Fact]
    public void Structured_planning_display_titles_strip_trailing_ocr_suffix_and_humanize_uppercase()
    {
        Assert.Equal(
            "Gaeties au gruau de base",
            ToolAgentOrchestrator.FormatSourceBackedPlanningDisplayTitleForTests("GAETIES AU GRUAU DE BASE O"));
        Assert.Equal(
            "Scones aux canneberges",
            ToolAgentOrchestrator.FormatSourceBackedPlanningDisplayTitleForTests("SCONES AUX CANNEBERGES"));
        Assert.Equal(
            "Plan de maintenance A",
            ToolAgentOrchestrator.FormatSourceBackedPlanningDisplayTitleForTests("PLAN DE MAINTENANCE A"));
    }

    [Fact]
    public void Structured_planning_rejects_pretty_plan_when_items_do_not_match_candidate_sources()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildConcreteRecipePlanningResults();
        var unsupportedAnswer = """
            Voici un plan de repas pour la semaine :

            Lundi :
            - Petit-dejeuner : Smoothie a l'ananas et au lait de soja
            - Dejeuner : Salade de quinoa aux legumes
            - Diner : Poulet roti avec pommes de terre

            Mardi :
            - Petit-dejeuner : Yaourt grec aux fruits rouges
            - Dejeuner : Pates aux champignons
            - Diner : Poisson grille avec legumes
            """;

        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            unsupportedAnswer,
            toolResults,
            query,
            "fr");

        Assert.Equal(6, stats.ItemCount);
        Assert.Equal(0, stats.SupportedItemCount);
        Assert.Equal(0, stats.SourceCount);
        Assert.True(ToolAgentOrchestrator.ShouldRejectUnsupportedPlanningAnswerForFinalForTests(unsupportedAnswer, toolResults, query, "fr"));
    }

    [Fact]
    public void Structured_planning_does_not_validate_card_title_with_distant_unrelated_page_recipe_proof()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildDistantUnrelatedPageRecipeProofResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.DoesNotContain("Smoothie a l'ananas", titles);
    }

    [Fact]
    public void Structured_planning_does_not_validate_index_title_with_nearby_unrelated_recipe_proof()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildNearbyUnrelatedPageRecipeProofResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.DoesNotContain("Smoothie a l'ananas", titles);
        Assert.Contains("Fondant au chocolat", titles);
    }

    [Fact]
    public void Structured_planning_does_not_treat_card_title_as_page_proof_when_evidence_lacks_title()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildCardTitleOnlyPlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.DoesNotContain("Smoothie a l'ananas", titles);
        Assert.True(string.IsNullOrWhiteSpace(ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query)));
    }

    [Fact]
    public void Structured_planning_does_not_treat_card_evidence_as_page_local_recipe_proof()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildCardEvidenceOnlyPlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.DoesNotContain("Smoothie a l'ananas", titles);
        Assert.True(string.IsNullOrWhiteSpace(ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query)));
    }

    [Fact]
    public void Structured_planning_rejects_card_when_fact_text_proves_a_different_recipe()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildMixedPageMismatchedCardEvidencePlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.Contains("Macaroni tex mex", titles);
        Assert.Contains("Brownies aux haricots noirs", titles);
        Assert.DoesNotContain("Smoothie a l'ananas", titles);
        Assert.DoesNotContain(titles, static title => title.Contains("Collection pratique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Structured_planning_accepts_explicit_paged_card_evidence_as_local_recipe_proof()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildExplicitPagedCardEvidencePlanningResults(15);

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");
        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var stats = ToolAgentOrchestrator.AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            answer,
            toolResults,
            query,
            "fr");
        var sources = ToolAgentOrchestrator.DeriveSourcesFromSupportedPlanningAnswerItemsForTests(
            answer,
            toolResults,
            query,
            "fr");

        Assert.Contains("Omelette aux herbes", titles);
        Assert.Contains("Macaroni tex mex", titles);
        Assert.Contains("Macaroni tex mex", answer);
        Assert.Equal(15, stats.ItemCount);
        Assert.Equal(15, stats.SupportedItemCount);
        Assert.Equal(15, sources.Count);
        Assert.Equal(15, sources.Select(static source => $"{source.DocPath}|{source.PageStart}|{source.PageEnd}").Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Structured_planning_extracts_multiple_local_candidates_from_compound_card_title()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildCompoundCardTitlePlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.Contains("Omelette aux herbes", titles);
        Assert.Contains("Macaroni tex mex", titles);
    }

    [Fact]
    public void Structured_planning_card_candidate_does_not_suppress_distinct_page_local_fallback()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildCardPlusDistinctFallbackPlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");

        Assert.Contains("Omelette aux herbes", titles);
        Assert.Contains("Macaroni tex mex", titles);
    }

    [Fact]
    public void Structured_planning_rejects_explicit_paged_card_when_primary_page_does_not_contain_the_recipe()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildDetachedExplicitPagedCardEvidencePlanningResults();

        var titles = ToolAgentOrchestrator.SourceBackedPlanningCandidateTitlesForTests(toolResults, query, "fr");
        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);

        Assert.DoesNotContain("Omelette aux herbes", titles);
        Assert.True(string.IsNullOrWhiteSpace(answer));
    }

    [Fact]
    public void Structured_planning_draft_sources_are_the_pages_used_by_the_generated_plan()
    {
        const string query = "Je cherche a avoir un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.";
        var toolResults = BuildExplicitPagedCardEvidencePlanningResults(15);

        var answer = ToolAgentOrchestrator.BuildSourceBackedPlanningAnswerForTests(toolResults, "fr", query);
        var sourceKeys = ToolAgentOrchestrator.BuildSourceBackedPlanningDraftSourceKeysForTests(toolResults, "fr", query);

        Assert.Contains("Omelette aux herbes", answer);
        Assert.Contains("Macaroni tex mex", answer);
        Assert.Equal(15, sourceKeys.Length);
        Assert.Equal(15, sourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        for (var i = 1; i <= 15; i++)
        {
            Assert.Contains($"Knowledge/category/card-source-{i}.pdf|{i + 9}|{i + 9}", sourceKeys);
        }
    }

    private static ToolResults BuildPlanningCandidateResults(int count)
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = ExplicitPagedCardRecipeTitles.Take(count)
                .Select((title, index) => new
                {
                    docPath = $"Knowledge/category/source-{index + 1}.pdf",
                    docName = $"source-{index + 1}.pdf",
                    pageStart = index + 1,
                    pageEnd = index + 1,
                    excerpt = $"{title}. Ingredients: 2 documented components. Preparation: add, cook and serve this documented item.",
                    fullText = $"{title}. Ingredients: 2 documented components. Preparation: add, cook and serve this documented item.",
                    matchedContentCards = new[] { new { title, kind = "unit_lead" } },
                    score = 0.95
                })
                .ToArray()
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildMainMealPlanningCandidateResults(int count)
    {
        var mainMealTitles = new[]
        {
            "Omelette aux herbes",
            "Croquettes de poulet",
            "Salade de lentilles",
            "Gratin de courgettes",
            "Poisson au four",
            "Riz aux legumes",
            "Pates tomate basilic",
            "Poulet au citron",
            "Sandwich au tofu",
            "Soupe de legumes"
        };
        var payload = JsonSerializer.Serialize(new
        {
            hits = mainMealTitles.Take(count)
                .Select((title, index) => new
                {
                    docPath = $"Knowledge/category/main-source-{index + 1}.pdf",
                    docName = $"main-source-{index + 1}.pdf",
                    pageStart = index + 1,
                    pageEnd = index + 1,
                    excerpt = $"{title}. Ingredients: 2 documented components. Preparation: add, cook and serve this documented item.",
                    fullText = $"{title}. Ingredients: 2 documented components. Preparation: add, cook and serve this documented item.",
                    matchedContentCards = new[] { new { title, kind = "unit_lead" } },
                    score = 0.95
                })
                .ToArray()
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildPlanningCandidateResultsWithNearUniqueSources(int count, int distinctPages)
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = ExplicitPagedCardRecipeTitles.Take(count)
                .Select((title, index) =>
                {
                    var sourceIndex = Math.Min(index + 1, distinctPages);
                    return new
                    {
                        docPath = $"Knowledge/category/source-{sourceIndex}.pdf",
                        docName = $"source-{sourceIndex}.pdf",
                        pageStart = sourceIndex,
                        pageEnd = sourceIndex,
                        excerpt = $"{title}. Ingredients: 2 documented components. Preparation: add, cook and serve this documented item.",
                        fullText = $"{title}. Ingredients: 2 documented components. Preparation: add, cook and serve this documented item.",
                        matchedContentCards = new[] { new { title, kind = "unit_lead" } },
                        score = 0.95
                    };
                })
                .ToArray()
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildPlanningCandidateResultsWithTopPageDuplicateAndDistinctBackfill()
    {
        var breakfast = new[]
        {
            "Porridge pommes cannelle",
            "Galettes au gruau",
            "Smoothie banane coco",
            "Scones aux canneberges",
            "Crepes legeres"
        };
        var main = new[]
        {
            "Ragout de legumes",
            "Chili doux aux haricots",
            "Courgettes farcies",
            "Poisson citron herbes",
            "Poulet olives carottes",
            "Risotto champignons",
            "Quinoa pois chiches",
            "Riz saute legumes",
            "Tortilla pommes terre",
            "Curry de pois chiches"
        };
        var snack = new[]
        {
            "Compote pommes",
            "Bouchees dattes cacao",
            "Yaourt fruits",
            "Crackers graines"
        };
        var hits = breakfast
            .Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "petit-dejeuner options", index + 1))
            .Concat(main.Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "diner options", breakfast.Length + index + 1)))
            .Concat(snack.Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "collation options", breakfast.Length + main.Length + index + 1)))
            .Concat(new[]
            {
                BuildGenericSlotRoutedRecipeHitOnPage("Muffins aux petits fruits", "gouter options", "slot-route-19", 19, 100),
                BuildGenericSlotRoutedRecipeHitOnPage("Barres aux cereales", "collation options", "source-20", 20, 101)
            })
            .ToArray();
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.search",
            new { hits });
        return toolResults;
    }

    private static ToolResults BuildSlotAwareMealPlanningResultsWithNoisyHighRankedItems()
    {
        var entries = new[]
        {
            ("SAUCE TOMATE", "tomates, ail et herbes", 0.995),
            ("MISE EN PLACE", "preparer les ingredients avant la recette", 0.994),
            ("Huile d'olive", "ingredient de base pour assaisonner", 0.993),
            ("CUISINE FUTEE PARENTS PRESSES", "titre de livre et contexte editorial", 0.992),
            ("Voici quelques idees pour remplacer votre", "phrase de transition et non item concret", 0.9915),
            ("SAUCE BOLOGNAISE", "tomates, viande et aromates", 0.991),
            ("Boeuf bourguignon", "boeuf, carottes, oignons et bouillon", 0.940),
            ("Gratin dauphinois", "pommes de terre, creme, lait et ail", 0.939),
            ("Osso buco", "jarret de veau, carottes et tomates", 0.938),
            ("Salade de lentilles", "lentilles, carottes, oignons et vinaigrette", 0.937),
            ("Paella mixte", "riz, poulet, moules, crevettes et chorizo", 0.936),
            ("Pizzas rigolotes", "pate a pizza, tomates, fromage et legumes", 0.935),
            ("Cassoulet toulousain", "haricots, saucisse et confit", 0.934),
            ("Tomates farcies", "tomates, farce, riz et herbes", 0.933),
            ("Quiche aux champignons", "pate, oeufs, creme et champignons", 0.932),
            ("Curry de pois chiches", "pois chiches, epices, tomates et riz", 0.931),
            ("Galettes au gruau", "avoine, lait et oeufs", 0.930),
            ("Smoothie banane coco", "banane, lait de coco et fruits", 0.929),
            ("Compote aux poires", "poires, pommes et cannelle", 0.928),
            ("Scones aux canneberges", "farine, lait, beurre et canneberges", 0.927),
            ("Riz gluant au lait de coco et mangue", "riz gluant, lait de coco et mangue", 0.926),
            ("Brownies aux haricots noirs", "haricots noirs, cacao et sucre", 0.925),
            ("Barres aux cereales", "avoine, graines et fruits secs", 0.924),
            ("Tarte aux pommes", "pommes, pate et cannelle", 0.923),
            ("Beignets espagnols", "farine, oeufs et sucre", 0.922),
            ("Muffins aux bleuets", "bleuets, farine, lait et oeufs", 0.921),
            ("KAKILES A-COTES", "fragment OCR non exploitable", 0.920)
        };
        var payload = JsonSerializer.Serialize(new
        {
            hits = entries
                .Select((entry, index) => new
                {
                    docPath = $"Knowledge/category/slot-aware-{index + 1}.pdf",
                    docName = $"slot-aware-{index + 1}.pdf",
                    pageStart = index + 1,
                    pageEnd = index + 1,
                    excerpt = $"{entry.Item1}. Ingredients : {entry.Item2}. Preparation : preparer, cuire et servir.",
                    fullText = $"{entry.Item1}. Ingredients : {entry.Item2}. Preparation : preparer, cuire et servir.",
                    matchedContentCards = new[] { new { title = entry.Item1, kind = "unit_lead" } },
                    score = entry.Item3
                })
                .ToArray()
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildMealPlanningResultsWithReferenceAttributionBeforeQuotedRecipe()
    {
        const string text = "Reference : CUISINE FUTEE PARENTS PRESSES. \u00ab Brownies aux haricots noirs \u00bb. "
                            + "(En ligne). Ingredients : haricots noirs, oeufs, sucre, cacao et huile. "
                            + "Preparation : reduire les haricots en puree, melanger, verser dans le moule et cuire au four.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/facilitemps.pdf",
                    docName = "facilitemps.pdf",
                    pageStart = 29,
                    pageEnd = 29,
                    excerpt = text,
                    fullText = text,
                    matchedContentCards = new[] { new { title = "CUISINE FUTEE PARENTS PRESSES", kind = "unit_lead" } },
                    score = 0.99
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildMealPlanningResultsWithHighRankedDesserts()
    {
        var entries = new[]
        {
            ("Millefeuille aux framboises", "pate feuilletee, creme, framboises et sucre", 0.99),
            ("Tarte au citron meringuee", "pate, citron, oeufs, sucre et meringue", 0.98),
            ("Tiramisu italien", "mascarpone, cafe, biscuits et cacao", 0.97),
            ("Profiteroles au chocolat", "choux, glace vanille et sauce chocolat", 0.96),
            ("Salade de lentilles", "lentilles, carottes, oignons et vinaigrette", 0.88),
            ("Pizzas rigolotes", "pate a pizza, tomates, fromage et legumes", 0.87),
            ("Gratin dauphinois", "pommes de terre, lait, creme, ail et fromage", 0.86),
            ("Osso buco", "jarret de veau, carottes, tomates et bouillon", 0.85),
            ("Moules marinieres", "moules, oignons, persil et vin blanc", 0.84),
            ("Paella mixte", "riz, poulet, moules, calamars, crevettes et chorizo", 0.83),
            ("Quiche lorraine", "pate brisee, oeufs, creme et lardons", 0.82)
        };
        var payload = JsonSerializer.Serialize(new
        {
            hits = entries
                .Select((entry, index) => new
                {
                    docPath = $"Knowledge/category/meal-plan-{index + 1}.pdf",
                    docName = $"meal-plan-{index + 1}.pdf",
                    pageStart = index + 1,
                    pageEnd = index + 1,
                    excerpt = $"{entry.Item1}. Pour 4 personnes. Ingredients : {entry.Item2}. Preparation : preparer, cuire et servir.",
                    fullText = $"{entry.Item1}. Pour 4 personnes. Ingredients : {entry.Item2}. Preparation : preparer, cuire et servir.",
                    matchedContentCards = new[] { new { title = entry.Item1, kind = "unit_lead" } },
                    score = entry.Item3
                })
                .ToArray()
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildMealPlanningResultsWithSixMainDishesAndDessert()
    {
        var entries = new[]
        {
            ("Salade de lentilles", "lentilles, carottes, oignons et vinaigrette", 0.93),
            ("Pizzas rigolotes", "pate a pizza, tomates, fromage et legumes", 0.92),
            ("Gratin dauphinois", "pommes de terre, lait, creme, ail et fromage", 0.91),
            ("Osso buco", "jarret de veau, carottes, tomates et bouillon", 0.90),
            ("Moules marinieres", "moules, oignons, persil et vin blanc", 0.89),
            ("Paella mixte", "riz, poulet, moules, calamars, crevettes et chorizo", 0.88),
            ("Millefeuille aux framboises", "pate feuilletee, creme, framboises et sucre", 0.99)
        };
        var payload = JsonSerializer.Serialize(new
        {
            hits = entries
                .Select((entry, index) => new
                {
                    docPath = $"Knowledge/category/six-meals-{index + 1}.pdf",
                    docName = $"six-meals-{index + 1}.pdf",
                    pageStart = index + 1,
                    pageEnd = index + 1,
                    excerpt = $"{entry.Item1}. Pour 4 personnes. Ingredients : {entry.Item2}. Preparation : preparer, cuire et servir.",
                    fullText = $"{entry.Item1}. Pour 4 personnes. Ingredients : {entry.Item2}. Preparation : preparer, cuire et servir.",
                    matchedContentCards = new[] { new { title = entry.Item1, kind = "unit_lead" } },
                    score = entry.Item3
                })
                .ToArray()
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildPlanningNavigationAnchorResults()
    {
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "documents.navigation",
            new
            {
                items = new[]
                {
                    new
                    {
                        docId = "doc-old",
                        docPath = "Knowledge/weekly-options.pdf",
                        docName = "weekly-options.pdf",
                        categoryPath = "Knowledge",
                        sectionTitle = "Tarte tomates moutarde",
                        pageStart = 42,
                        hasTargetAnchor = true,
                        confidence = 0.9
                    },
                    new
                    {
                        docId = "doc-old",
                        docPath = "Knowledge/weekly-options.pdf",
                        docName = "weekly-options.pdf",
                        categoryPath = "Knowledge",
                        sectionTitle = "Compote pomme poire",
                        pageStart = 44,
                        hasTargetAnchor = true,
                        confidence = 0.8
                    }
                }
            });

        for (var i = 0; i < 4; i++)
        {
            AddToolJson(
                toolResults,
                "documents.navigation",
                new
                {
                    items = new[]
                    {
                        new
                        {
                            docId = $"doc-new-{i}",
                            docPath = $"Knowledge/generic-{i}.pdf",
                            docName = $"generic-{i}.pdf",
                            categoryPath = "Knowledge",
                            label = $"Generic heading {i}",
                            targetPageStart = i + 1,
                            hasTargetAnchor = true,
                            confidence = 0.7
                        }
                    }
                });
        }

        return toolResults;
    }

    private static ToolResults BuildPlanningSummaryAnchorResults()
    {
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "summary.search",
            new
            {
                items = new[]
                {
                    new
                    {
                        title = "Soupe froide concombre",
                        docId = "doc-summary",
                        docPath = "Knowledge/summaries.pdf",
                        docName = "summaries.pdf",
                        categoryPath = "Knowledge",
                        sourcePage = 18,
                        matchedContentCards = new[]
                        {
                            new
                            {
                                title = "Soupe froide concombre",
                                signals = new[] { "concombre", "preparation" }
                            }
                        }
                    }
                }
            });

        return toolResults;
    }

    private static ToolResults BuildOffTopicStandardNavigationAnchorResults()
    {
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "documents.navigation",
            new
            {
                items = new[]
                {
                    new
                    {
                        docId = "standard-doc",
                        docPath = "Construction/PDF/EN 1090-1 2010 Execution of steel structures and aluminium structures - Part 1 Requirements for conformity assessment of structural components.pdf",
                        docName = "EN 1090-1 2010 Execution of steel structures and aluminium structures.pdf",
                        categoryPath = "Construction/PDF",
                        sectionTitle = "DIN EN 1090",
                        pageStart = 1,
                        targetPageStart = 1,
                        targetPageEnd = 1,
                        hasTargetAnchor = true,
                        confidence = 0.94
                    }
                }
            });

        return toolResults;
    }

    private static ToolResults BuildNoisyMealPlanningNavigationAnchorResults()
    {
        var toolResults = new ToolResults();
        var anchors = new[]
        {
            ("INGRÉDIENTSPRÉPARATION1", 34),
            ("SAUCE TOMATE", 34),
            ("PLANIFICATION DES REPAS", 9),
            ("27 Référence", 36),
            ("À partir de 5-6 ans, votre enfant peut", 36),
            ("COLWELL PRÉPARATION• Préchauffer la mijoteuse à la plus basse température (Low)", 13),
            ("Sauce barbecue", 13),
            ("Repas de fetes", 14),
            ("Imprime par Graphic Impression - Tel", 14),
            ("Preparer la mayonnaise dans un bol oudans le pot a confiture", 40),
            ("Sonde de rotissage", 12),
            ("MCRC01072538_SE_Pfirsich_Gefluegelspiesse", 12),
            ("Guide de nuit En soiree, realisez", 18),
            ("Collection parents presses", 19),
            ("NOIRS Ingredients Nombre de portions Temps de", 21),
            ("Fondation OLO", 22),
            ("Presentation d'une fiche", 23),
            ("Possibilite de l'evolution de la recette", 24),
            ("Quantites donnees pour 6 personnes", 25),
            ("CINQ FOURCHETTESCOM", 26),
            ("Dansune cocotte allant au four, faire 1", 27),
            ("petite poignee de feuilles de menthe", 28),
            ("CUISINE DE NUITI En soiree, realisez", 29),
            ("DANS SON REFRIGERATEUR", 30),
            ("A VOS COCOSI", 31),
            ("FONCTION DES SAISONS", 32),
            ("EVITEZ D'ACHETER DES PRODUITS EN PORTIONS INDIVIDUELLES", 33),
            ("POLITIQUE D'EXACTITUDE DES PRIX", 34),
            ("PETITS MANGEURS", 35),
            ("UTILISE SI", 36),
            ("CRAQUE LINS CASSES CA REPOUSSE TOUT SEULW", 37),
            ("COU PER EN TRANCHES", 38),
            ("MACARONI TEX-MEX Temps de", 39),
            ("SPATULE DE MELANGE", 40),
            ("Elle remue, rassemble et melange", 41),
            ("AUX TOMATES SECHEES ET AUX NOISETTES", 42),
            ("AU FROMAGE", 43),
            ("Muffins aux pommes", 60)
        };

        AddToolJson(
            toolResults,
            "documents.navigation",
            new
            {
                items = anchors
                    .Select((anchor, index) => new
                    {
                        docId = $"meal-nav-{index}",
                        docPath = $"Knowledge/Meals/noisy-nav-{index}.pdf",
                        docName = $"noisy-nav-{index}.pdf",
                        categoryPath = "Knowledge/Meals",
                        sectionTitle = anchor.Item1,
                        pageStart = anchor.Item2,
                        targetPageStart = anchor.Item2,
                        targetPageEnd = anchor.Item2,
                        hasTargetAnchor = true,
                        confidence = 0.92
                    })
                    .ToArray()
            });

        return toolResults;
    }

    private static ToolResults BuildAlreadyObservedMealPlanningNavigationAnchorResults()
    {
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.search",
            new
            {
                hits = new[]
                {
                    BuildObservedSlotRoutedRecipeHit("Croquettes de poulet", "diner recettes", 1)
                }
            });

        var anchors = new[]
        {
            ("Croquettes de poulet", 20),
            ("Muffins aux pommes", 60)
        };

        AddToolJson(
            toolResults,
            "documents.navigation",
            new
            {
                items = anchors
                    .Select((anchor, index) => new
                    {
                        docId = $"meal-observed-nav-{index}",
                        docPath = $"Knowledge/Meals/observed-nav-{index}.pdf",
                        docName = $"observed-nav-{index}.pdf",
                        categoryPath = "Knowledge/Meals",
                        sectionTitle = anchor.Item1,
                        pageStart = anchor.Item2,
                        targetPageStart = anchor.Item2,
                        targetPageEnd = anchor.Item2,
                        hasTargetAnchor = true,
                        confidence = 0.92
                    })
                    .ToArray()
            });

        return toolResults;
    }

    private static ToolResults BuildObservedSlotRoutedMealPlanningResults()
    {
        var recipes = new[]
        {
            ("Scones aux canneberges", "petit-dejeuner recettes"),
            ("Muffins aux pommes", "recettes petit-dejeuner"),
            ("Galettes au gruau", "dejeuners recettes"),
            ("Smoothie vert", "brunch recettes"),
            ("Pain aux bananes", "petit-dejeuner recettes"),
            ("Nouilles sauce cacahuete", "diner recettes"),
            ("Tarte tomate et chevre", "recettes diner"),
            ("Oeufs mimosa aux sardines", "plats principaux recettes"),
            ("Crepes epaisses fourrees", "recettes plats principaux"),
            ("Courge spaghetti", "souper recettes"),
            ("Pizzas rigolotes", "recettes souper"),
            ("Omelette italienne", "diner recettes"),
            ("Macaroni tex mex", "plats principaux recettes"),
            ("Volaille a l'ananas", "recettes plats principaux"),
            ("Salade de lentilles", "souper recettes"),
            ("Houmous de betterave", "gouter recettes"),
            ("Pate d'artichaut", "recettes gouter"),
            ("Bouchees dattes cacao", "collation recettes"),
            ("Compote de pommes", "recettes collation"),
            ("Brownies aux haricots noirs", "dessert recettes")
        };
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.search",
            new
            {
                hits = recipes
                    .Select((recipe, index) => BuildObservedSlotRoutedRecipeHit(recipe.Item1, recipe.Item2, index + 1))
                    .ToArray()
            });

        return toolResults;
    }

    private static ToolResults BuildMealPlanningResultsWithRoutedDessertAndSpreadDecoys()
    {
        var recipes = new[]
        {
            ("Omelette italienne", "petit-dejeuner recettes"),
            ("Muffins aux pommes", "recettes petit-dejeuner"),
            ("Scones aux canneberges", "dejeuners recettes"),
            ("Compote de pommes", "petit-dejeuner recettes"),
            ("Crepes epaisses fourrees", "brunch recettes"),
            ("Crème catalane", "souper recettes"),
            ("Bucatini a l'amatriciana", "diner recettes"),
            ("Nouilles sauce cacahuete", "recettes diner"),
            ("Boeuf jardiniere", "souper recettes"),
            ("Saute vegetarien", "plats principaux recettes"),
            ("Tarte tomate et chevre", "recettes diner"),
            ("Courge spaghetti", "souper recettes"),
            ("Oeufs mimosa aux sardines", "plats principaux recettes"),
            ("Pizzas rigolotes", "recettes souper"),
            ("Quenelles de pommes de terre", "diner recettes"),
            ("Legumineuses au cari", "recettes plats principaux"),
            ("Houmous de betterave", "collation recettes"),
            ("Milk-shake chocolat", "gouter recettes"),
            ("Muffins aux petits fruits", "recettes gouter"),
            ("Compote aux poires", "recettes collation"),
            ("Beignets espagnols", "dessert recettes"),
            ("Brownies aux haricots noirs", "collation recettes"),
        };
        var payload = JsonSerializer.Serialize(new
        {
            hits = recipes
                .Select((recipe, index) => BuildGenericSlotRoutedRecipeHit(recipe.Item1, recipe.Item2, index + 1))
                .ToArray()
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildMealPlanningResultsWithGenericMainSlotCandidates()
    {
        var breakfast = new[]
        {
            "Porridge pommes cannelle",
            "Muffins aux bleuets",
            "Galettes au gruau",
            "Smoothie banane coco",
            "Crepes legeres"
        };
        var main = new[]
        {
            "Ragout de legumes",
            "Chili doux aux haricots",
            "Courgettes farcies",
            "Poisson citron herbes",
            "Poulet olives carottes",
            "Risotto champignons",
            "Quinoa pois chiches",
            "Riz saute legumes",
            "Tortilla pommes terre",
            "Curry de pois chiches"
        };
        var snack = new[]
        {
            "Barres aux cereales",
            "Compote pommes",
            "Bouchees dattes cacao",
            "Yaourt fruits",
            "Crackers graines"
        };

        var hits = breakfast
            .Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "petit-dejeuner options", index + 1))
            .Concat(main.Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "selection documentee", breakfast.Length + index + 1)))
            .Concat(snack.Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "collation options", breakfast.Length + main.Length + index + 1)))
            .ToArray();
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.search",
            new { hits });
        return toolResults;
    }

    private static ToolResults BuildMealPlanningResultsWithDuplicateSnackRouteCandidate()
    {
        var breakfast = new[]
        {
            "Porridge pommes cannelle",
            "Galettes au gruau",
            "Smoothie banane coco",
            "Scones aux canneberges",
            "Crepes legeres"
        };
        var main = new[]
        {
            "Ragout de legumes",
            "Chili doux aux haricots",
            "Courgettes farcies",
            "Poisson citron herbes",
            "Poulet olives carottes",
            "Risotto champignons",
            "Quinoa pois chiches",
            "Riz saute legumes",
            "Tortilla pommes terre",
            "Curry de pois chiches"
        };
        var snack = new[]
        {
            "Compote pommes",
            "Bouchees dattes cacao",
            "Yaourt fruits",
            "Crackers graines"
        };

        var hits = breakfast
            .Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "petit-dejeuner options", index + 1))
            .Concat(main.Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "diner options", breakfast.Length + index + 1)))
            .Concat(snack.Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "collation options", breakfast.Length + main.Length + index + 1)))
            .Concat(new[]
            {
                BuildGenericSlotRoutedRecipeHitOnPage("Muffins aux petits fruits", "petit-dejeuner options", "duplicate-muffins", 220, 100),
                BuildGenericSlotRoutedRecipeHitOnPage("Muffins aux petits fruits", "gouter options", "duplicate-muffins", 220, 101)
            })
            .ToArray();
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.search",
            new { hits });
        return toolResults;
    }

    private static ToolResults BuildMealPlanningResultsWithNeutralRouteLightSnacksAndDipDecoy()
    {
        var breakfast = new[]
        {
            "Porridge pommes cannelle",
            "Omelette aux herbes",
            "Galettes au gruau",
            "Smoothie banane coco",
            "Crepes legeres"
        };
        var main = new[]
        {
            "Ragout de legumes",
            "Chili doux aux haricots",
            "Courgettes farcies",
            "Poisson citron herbes",
            "Poulet olives carottes",
            "Risotto champignons",
            "Quinoa pois chiches",
            "Riz saute legumes",
            "Tortilla pommes terre",
            "Curry de pois chiches"
        };
        var snack = new[]
        {
            "Muffins aux petits fruits",
            "Scones aux canneberges",
            "Compote pommes",
            "Yaourt fruits",
            "Bouchees dattes cacao"
        };

        var hits = breakfast
            .Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "petit-dejeuner options", index + 1))
            .Concat(main.Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "diner options", breakfast.Length + index + 1)))
            .Concat(snack.Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "selection documentee", breakfast.Length + main.Length + index + 1)))
            .Concat(new[]
            {
                BuildGenericSlotRoutedRecipeHit("Houmous de betterave", "gouter options", breakfast.Length + main.Length + snack.Length + 1)
            })
            .ToArray();
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.search",
            new { hits });
        return toolResults;
    }

    private static ToolResults BuildMealPlanningResultsWithoutSnackCoverage()
    {
        var breakfast = new[]
        {
            "Porridge pommes cannelle",
            "Muffins aux bleuets",
            "Galettes au gruau",
            "Smoothie banane coco",
            "Scones aux canneberges"
        };
        var main = new[]
        {
            "Ragout de legumes",
            "Chili doux aux haricots",
            "Courgettes farcies",
            "Poisson citron herbes",
            "Poulet olives carottes",
            "Risotto champignons",
            "Quinoa pois chiches",
            "Riz saute legumes",
            "Tortilla pommes terre",
            "Curry de pois chiches"
        };

        var hits = breakfast
            .Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "petit-dejeuner options", index + 1))
            .Concat(main.Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "selection documentee", breakfast.Length + index + 1)))
            .ToArray();
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.search",
            new { hits });
        return toolResults;
    }

    private static ToolResults BuildMealPlanningResultsAfterFailedCollationRuns()
    {
        var breakfast = new[]
        {
            "Porridge pommes cannelle",
            "Muffins aux bleuets",
            "Galettes au gruau",
            "Smoothie banane coco",
            "Scones aux canneberges"
        };
        var main = new[]
        {
            "Ragout de legumes",
            "Chili doux aux haricots",
            "Courgettes farcies",
            "Poisson citron herbes",
            "Poulet olives carottes",
            "Risotto champignons",
            "Quinoa pois chiches",
            "Riz saute legumes",
            "Tortilla pommes terre",
            "Curry de pois chiches"
        };

        var hits = breakfast
            .Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "petit-dejeuner options", index + 1))
            .Concat(main.Select((title, index) => BuildGenericSlotRoutedRecipeHit(title, "diner options", breakfast.Length + index + 1)))
            .ToArray();
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.multi_search",
            new
            {
                hits,
                meta = new
                {
                    queryRuns = new[]
                    {
                        new { query = "petit-dejeuner options", hitCount = breakfast.Length },
                        new { query = "diner options", hitCount = main.Length },
                        new { query = "collation recettes", hitCount = 0 },
                        new { query = "collation plats", hitCount = 0 }
                    }
                }
            });
        return toolResults;
    }

    private static ToolResults BuildUiLikeMealPlanningResultsWithInsufficientBreakfastCoverage()
    {
        var recipes = new[]
        {
            ("Scones aux canneberges", "petit-dejeuner recettes"),
            ("Muffins aux pommes", "recettes petit-dejeuner"),
            ("Galettes au gruau", "dejeuners recettes"),
            ("Smoothie vert", "brunch recettes"),
            ("Nouilles sauce cacahuete", "repas petit-dejeuner"),
            ("Bisque de crevettes", "recettes diner"),
            ("Boeuf jardiniere", "recettes souper"),
            ("Boeuf bourguignon", "diner recettes"),
            ("Cassoulet toulousain", "recettes diner"),
            ("Gratin dauphinois", "souper recettes"),
            ("Osso buco", "recettes souper"),
            ("Moules marinieres", "plats principaux recettes"),
            ("Quiche lorraine traditionnelle", "recettes plats principaux"),
            ("Tomates farcies", "recettes diner"),
            ("Paella mixte", "souper recettes"),
            ("Houmous de betterave", "recettes gouter"),
            ("Glacage au sucre, avec blanc d'oeuf", "repas petit-dejeuner"),
            ("Brownies aux haricots jr da bu", "recettes collation"),
            ("Tiramisu italien", "dessert recettes"),
            ("Profiteroles au chocolat", "dessert recettes"),
            ("Fondant au chocolat", "recettes collation"),
            ("Ile flottante", "recettes dessert")
        };
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.search",
            new
            {
                hits = recipes
                    .Select((recipe, index) => BuildGenericSlotRoutedRecipeHit(recipe.Item1, recipe.Item2, index + 1))
                    .ToArray()
            });

        return toolResults;
    }

    private static ToolResults BuildMealPlanningResultsWithBreakfastRouteBackfillCandidate()
    {
        var recipes = new[]
        {
            ("Scones aux canneberges", "petit-dejeuner recettes"),
            ("Muffins aux pommes", "recettes petit-dejeuner"),
            ("Galettes au gruau", "dejeuners recettes"),
            ("Smoothie vert", "brunch recettes"),
            ("Boeuf bourguignon", "diner recettes"),
            ("Gratin dauphinois", "recettes diner"),
            ("Osso buco", "souper recettes"),
            ("Salade de lentilles", "recettes souper"),
            ("Paella mixte", "plats principaux recettes"),
            ("Pizzas rigolotes", "recettes plats principaux"),
            ("Cassoulet toulousain", "diner recettes"),
            ("Tomates farcies", "recettes diner"),
            ("Quiche lorraine traditionnelle", "souper recettes"),
            ("Macaroni tex mex", "plats principaux recettes"),
            ("Houmous de betterave", "gouter recettes"),
            ("Compote de pommes", "recettes collation"),
            ("Bouchees dattes cacao", "collation recettes"),
            ("Brownies aux haricots noirs", "recettes collation"),
            ("Beignets espagnols", "dessert recettes"),
            ("Tartine avocat fromage frais", "petit-dejeuner recettes")
        };
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.search",
            new
            {
                hits = recipes
                    .Select((recipe, index) => BuildGenericSlotRoutedRecipeHit(recipe.Item1, recipe.Item2, index + 1))
                    .ToArray()
            });

        return toolResults;
    }

    private static ToolResults BuildObservedPartialSlotRoutedMealPlanningResults()
    {
        var recipes = new[]
        {
            ("Scones aux canneberges", "petit-dejeuner recettes"),
            ("Nouilles sauce cacahuete", "diner recettes"),
            ("Tarte tomate et chevre", "recettes diner"),
            ("Houmous de betterave", "gouter recettes"),
            ("Muffins aux pommes", "recettes petit-dejeuner"),
            ("Oeufs mimosa aux sardines", "plats principaux recettes"),
            ("Pizzas rigolotes", "recettes souper"),
            ("Compote de pommes", "recettes collation"),
            ("Galettes au gruau", "dejeuners recettes"),
            ("Courge spaghetti", "souper recettes"),
        };
        var toolResults = new ToolResults();
        AddToolJson(
            toolResults,
            "rag.search",
            new
            {
                hits = recipes
                    .Select((recipe, index) => BuildObservedSlotRoutedRecipeHit(recipe.Item1, recipe.Item2, index + 1))
                    .ToArray()
            });

        return toolResults;
    }

    private static ToolResults BuildObservedDirtyRecipeCardMealPlanningResults()
    {
        var recipes = new[]
        {
            ("INGRÉDIENTSNOUILLES SAUCE CACAHOUÈTE || NOUILLES SAUCE CACAHOUETE", "NOUILLES SAUCE CACAHOUETE", "diner recettes"),
            ("Ingrédients Technique || Pizzas rigolotes", "Pizzas rigolotes", "recettes souper"),
            ("Pâtisserie salée || Crêpes épaisses fourrées", "Crêpes épaisses fourrées", "recettes plats principaux"),
            ("COURGE SPAGHETTILES À KAKILES À-CÔTÉS", "COURGE SPAGHETTI", "souper recettes"),
            ("COURGE SPAGHETTI \u00c0-C\u00d4T\u00c9S", "COURGE SPAGHETTI", "souper recettes"),
            ("SPAGHETTI", "COURGE SPAGHETTI", "souper recettes"),
            ("B\u0152UF JARDINI\u00c8RE 12", "B\u0152UF JARDINI\u00c8RE", "diner recettes"),
            ("Lorsqu'ils cuisinent", "Lorsqu'ils cuisinent", "gouter recettes"),
            ("Gratiner Recouvrir un plat de fromage et le mettre au four", "Gratiner Recouvrir un plat de fromage et le mettre au four", "diner recettes"),
            ("Blanchir Plonger quelques minutes un aliment dans l'eau bouillante", "Blanchir Plonger quelques minutes un aliment dans l'eau bouillante", "collation recettes"),
            ("Plats au four || Macaroni TEX-MEX", "Macaroni TEX-MEX", "plats recettes")
        };
        var payload = JsonSerializer.Serialize(new
        {
            hits = recipes
                .Select((recipe, index) => BuildObservedDirtyRecipeCardHit(recipe.Item1, recipe.Item2, recipe.Item3, index + 1))
                .ToArray()
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildObservedBackendRecipeInventoryMealPlanningResults()
    {
        var hits = new[]
        {
            BuildObservedBackendRecipeInventoryHit(
                new[] { "Croquettes de poulet" },
                "Croquettes de poulet",
                "Croquettes de poulet. Volailles. Modes de preparation Frire. Categories de recettes Volailles. Pour 4 portions, env. 20 pieces.",
                "recettes",
                1,
                includeCardEvidence: true),
            BuildObservedBackendRecipeInventoryHit(
                new[] { "RECETTES FACILES", "MACARONI TEX-MEX", "MACARONI TEX" },
                "Macaroni TEX-MEX",
                "MACARONI TEX-MEX Temps de cuisson 20 min. Ingredients : macaroni, legumes, epices. Preparation : faire cuire, melanger et servir.",
                "legumineuses recettes",
                2,
                includeCardEvidence: false),
            BuildObservedBackendRecipeInventoryHit(
                new[] { "NOUILLES Re", "NOUILLES SAUTEES AUX LEGUMES ET CREVETTES", "SAUTEES AUX LEGUMES ET CREVETTES" },
                "NOUILLES SAUTEES AUX LEGUMES ET CREVETTES",
                "NOUILLES SAUTEES AUX LEGUMES ET CREVETTES. Ingredients : nouilles chinoises, crevettes, legumes. Preparation : cuire les nouilles, faire sauter et servir.",
                "pates recettes",
                3,
                includeCardEvidence: true),
            BuildObservedBackendRecipeInventoryHit(
                new[] { "HUITRES ET ANETH FRAIS PLATS PRINCI- PAUX", "TA LASAGNE PLATS PRINCI- PAUX" },
                "TA LASAGNE",
                "TA LASAGNE. Ingredients : sauce tomate, pates, fromage et legumes. Preparation : monter la lasagne, cuire au four et servir.",
                "plats recettes",
                4,
                includeCardEvidence: true),
            BuildObservedBackendRecipeInventoryHit(
                new[] { "Poissons d'eau douce", "Brochettes de poisson mediterraneennes" },
                "Brochettes de poisson mediterraneennes",
                "Brochettes de poisson mediterraneennes. Ingredients : poisson, legumes et marinade. Preparation : enfiler sur brochettes, griller et servir.",
                "poisson recettes",
                5,
                includeCardEvidence: false),
            BuildObservedBackendRecipeInventoryHit(
                new[] { "Poissons d\u2019eau douce", "Brochettes de poisson grillees" },
                "Brochettes de poisson grillees",
                "Brochettes de poisson grillees. Ingredients : poisson, legumes et citron. Preparation : enfiler sur brochettes, griller et servir.",
                "poisson recettes",
                10,
                includeCardEvidence: false),
            BuildObservedBackendRecipeInventoryHit(
                new[] { "Poissons d'eau de mer", "Truite rotie" },
                "Truite rotie",
                "Truite rotie. Ingredients : truite, pommes de terre et herbes. Preparation : rotir au four et servir chaud.",
                "poisson recettes",
                6,
                includeCardEvidence: false),
            BuildObservedBackendRecipeInventoryHit(
                new[] { "4 \u00c0 A VOIR DANS SON CON GELO!", "SCONES AUX CANNEBERGES" },
                "SCONES AUX CANNEBERGES",
                "SCONES AUX CANNEBERGES. Ingredients : farine, canneberges, lait et beurre. Preparation : melanger, former les scones, cuire au four et servir.",
                "gouter recettes",
                11,
                includeCardEvidence: true),
            BuildObservedBackendRecipeInventoryHit(
                new[] { "KAKILES \u00c0-C\u00d4T\u00c9S", "HOUMOUS DE BETTERAVE" },
                "HOUMOUS DE BETTERAVE",
                "HOUMOUS DE BETTERAVE. Ingredients : betterave, pois chiches et citron. Preparation : mixer, assaisonner et servir avec des crudites.",
                "collation recettes",
                12,
                includeCardEvidence: true),
            BuildObservedBackendRecipeInventoryHit(
                new[] { "INGREDIENTSBOULETTES DE POULET", "BOULETTES DE POULET" },
                "BOULETTES DE POULET",
                "BOULETTES DE POULET. Ingredients : poulet, ail, herbes et sauce tomate. Preparation : former les boulettes, cuire et servir.",
                "legumineuses recettes",
                7,
                includeCardEvidence: true),
            BuildObservedBackendRecipeInventoryHit(
                new[] { "SALADE DE BETTERAVES ET CAROTTES MARINEESLES A", "Servir cette salade" },
                "SALADE DE BETTERAVES ET CAROTTES MARINEES",
                "SALADE DE BETTERAVES ET CAROTTES MARINEES. Ingredients : betteraves, carottes et vinaigrette. Preparation : cuire, trancher, mariner et servir.",
                "legumineuses recettes",
                8,
                includeCardEvidence: true),
            BuildObservedBackendRecipeInventoryHit(
                new[] { "DESSERTS. DESSERTS", "Fruits en beignets" },
                "Fruits en beignets",
                "Fruits en beignets. Ingredients : oeufs, farine, fruits et huile. Preparation : preparer la pate, frire les fruits et servir.",
                "desserts faciles recettes",
                9,
                includeCardEvidence: false)
        };
        var payload = JsonSerializer.Serialize(new { hits });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildMealPlanningResultsWhereOnlyMainItemsMentionGenericPlanAxes()
    {
        var mainTitles = new[]
        {
            "Ragout de legumes",
            "Curry de pois chiches",
            "Riz saute legumes",
            "Poulet olives carottes",
            "Poisson citron herbes",
            "Quinoa pois chiches",
            "Tortilla pommes terre",
            "Salade de lentilles",
            "Pates tomate basilic",
            "Courgettes farcies"
        };
        var snackTitles = new[]
        {
            "Barres aux cereales",
            "Yaourt fruits",
            "Compote pommes",
            "Bouchees dattes cacao",
            "Crackers graines"
        };
        var breakfastTitles = new[]
        {
            "Scones aux canneberges",
            "Galettes au gruau",
            "Muffins aux bleuets",
            "Porridge pommes cannelle",
            "Smoothie banane coco"
        };

        var hits = mainTitles
            .Select((title, index) => BuildGenericSlotRoutedRecipeHitWithProof(
                title,
                retrievalQuery: index % 2 == 0 ? "Repas diner" : "Repas souper",
                page: index + 1,
                proofLead: "Repas de semaine"))
            .Concat(snackTitles.Select((title, index) => BuildGenericSlotRoutedRecipeHitWithProof(
                title,
                retrievalQuery: "Repas gouter",
                page: 40 + index,
                proofLead: title)))
            .Concat(breakfastTitles.Select((title, index) => BuildGenericSlotRoutedRecipeHitWithProof(
                title,
                retrievalQuery: "Petit-dejeuner recette",
                page: 80 + index,
                proofLead: title)))
            .ToArray();
        var payload = JsonSerializer.Serialize(new { hits });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static object BuildGenericSlotRoutedRecipeHitWithProof(
        string title,
        string retrievalQuery,
        int page,
        string proofLead)
    {
        var proof = $"{proofLead}. {title}. Ingredients : ingredient principal, accompagnement et assaisonnement. Preparation : preparer, cuire si necessaire et servir.";
        return new
        {
            retrievalQuery,
            retrievalQueryIndex = page % 4,
            docPath = $"Knowledge/Meals/routed-generic-anchor-{page}.pdf",
            docName = $"routed-generic-anchor-{page}.pdf",
            categoryPath = "Knowledge/Meals",
            pageStart = page,
            pageEnd = page,
            sectionTitle = title,
            headingPath = title,
            excerpt = proof,
            fullText = proof,
            matchedContentCards = new[]
            {
                new
                {
                    title,
                    kind = "page_embedded_title",
                    pageStart = (int?)page,
                    pageEnd = (int?)page,
                    evidence = new
                    {
                        facts = new[]
                        {
                            new
                            {
                                kind = "recipe",
                                label = title,
                                value = "ingredient principal, accompagnement et assaisonnement",
                                sourceText = proof,
                                pageStart = (int?)page,
                                pageEnd = (int?)page
                            }
                        }
                    }
                }
            },
            score = 0.98
        };
    }

    private static object BuildObservedSlotRoutedRecipeHit(string title, string retrievalQuery, int index)
    {
        var page = index;
        var proof = $"{title}. Ingredients : ingredient principal, accompagnement et assaisonnement. Preparation : preparer, cuire si necessaire et servir.";
        return new
        {
            retrievalQuery,
            retrievalQueryIndex = index % 4,
            docPath = $"Cuisine/PDF/observed-slot-route-{index}.pdf",
            docName = $"observed-slot-route-{index}.pdf",
            pageStart = page,
            pageEnd = page,
            sectionTitle = title,
            headingPath = title,
            excerpt = proof,
            fullText = $"Page recette. {proof} Note : source locale complete pour le plan de repas.",
            contextualSnippet = $"Matched direct_title_token_route: {retrievalQuery} {proof}",
            matchedContentCards = new[]
            {
                new
                {
                    title,
                    kind = "page_embedded_title",
                    pageStart = page,
                    pageEnd = page,
                    evidence = new
                    {
                        facts = new[]
                        {
                            new
                            {
                                kind = "recipe",
                                label = title,
                                value = "ingredient principal, accompagnement et assaisonnement",
                                sourceText = proof,
                                pageStart = (int?)page,
                                pageEnd = (int?)page
                            }
                        }
                    }
                }
            },
            score = 0.98
        };
    }

    private static object BuildGenericSlotRoutedRecipeHit(string title, string retrievalQuery, int index)
    {
        return BuildGenericSlotRoutedRecipeHitOnPage(title, retrievalQuery, $"slot-route-{index}", index, index);
    }

    private static object BuildGenericSlotRoutedRecipeHitOnPage(
        string title,
        string retrievalQuery,
        string docSlug,
        int page,
        int index)
    {
        var proof = $"{title}. Ingredients : ingredient principal, accompagnement et assaisonnement. Preparation : preparer, cuire si necessaire et servir.";
        return new
        {
            retrievalQuery,
            retrievalQueryIndex = index % 4,
            docPath = $"Knowledge/Meals/{docSlug}.pdf",
            docName = $"{docSlug}.pdf",
            categoryPath = "Knowledge/Meals",
            pageStart = page,
            pageEnd = page,
            sectionTitle = title,
            headingPath = title,
            excerpt = proof,
            fullText = $"Page recette. {proof} Note : source locale complete pour le plan de repas.",
            contextualSnippet = $"Matched direct_title_token_route: {retrievalQuery} {proof}",
            matchedContentCards = new[]
            {
                new
                {
                    title,
                    kind = "page_embedded_title",
                    pageStart = page,
                    pageEnd = page,
                    evidence = new
                    {
                        facts = new[]
                        {
                            new
                            {
                                kind = "recipe",
                                label = title,
                                value = "ingredient principal, accompagnement et assaisonnement",
                                sourceText = proof,
                                pageStart = (int?)page,
                                pageEnd = (int?)page
                            }
                        }
                    }
                }
            },
            score = 0.98
        };
    }

    private static object BuildObservedDirtyRecipeCardHit(string rawCardTitle, string proofTitle, string retrievalQuery, int index)
    {
        var page = 80 + index;
        var proof = $"{proofTitle}. Ingredients : ingredient principal, accompagnement et assaisonnement. Preparation : preparer, cuire si necessaire et servir.";
        return new
        {
            retrievalQuery,
            retrievalQueryIndex = index % 4,
            docPath = $"Cuisine/PDF/observed-dirty-recipe-{index}.pdf",
            docName = $"observed-dirty-recipe-{index}.pdf",
            pageStart = page,
            pageEnd = page,
            sectionTitle = rawCardTitle,
            headingPath = rawCardTitle,
            excerpt = proof,
            fullText = $"Page recette. {proof} Note : source locale complete pour le plan de repas.",
            contextualSnippet = $"Matched direct_title_token_route: {retrievalQuery} {proof}",
            matchedContentCards = new[]
            {
                new
                {
                    title = rawCardTitle,
                    kind = "page_embedded_title",
                    pageStart = page,
                    pageEnd = page,
                    evidence = new
                    {
                        facts = new[]
                        {
                            new
                            {
                                kind = "recipe",
                                label = proofTitle,
                                value = "ingredient principal, accompagnement et assaisonnement",
                                sourceText = proof,
                                pageStart = (int?)page,
                                pageEnd = (int?)page
                            }
                        }
                    }
                }
            },
            score = 0.95
        };
    }

    private static object BuildObservedBackendRecipeInventoryHit(
        string[] cardTitles,
        string proofTitle,
        string proof,
        string retrievalQuery,
        int index,
        bool includeCardEvidence)
    {
        var page = 130 + index;
        return new
        {
            retrievalQuery,
            retrievalQueryIndex = index % 5,
            docPath = $"Cuisine/PDF/backend-like-recipe-{index}.pdf",
            docName = $"backend-like-recipe-{index}.pdf",
            pageStart = page,
            pageEnd = page,
            sectionTitle = cardTitles[0],
            headingPath = cardTitles[0],
            excerpt = proof,
            fullText = $"Page recette. {proof} Note : source locale complete pour le plan de repas.",
            contextualSnippet = $"Matched direct_title_token_route: {retrievalQuery} {proof}",
            matchedContentCards = cardTitles.Select((title, cardIndex) => new
            {
                title,
                kind = cardIndex == 0 ? "page_embedded_title" : "exact_lead",
                pageStart = (int?)page,
                pageEnd = (int?)page,
                evidence = includeCardEvidence
                    ? new
                    {
                        scaleBasis = new
                        {
                            count = 4,
                            label = "portions"
                        },
                        facts = new[]
                        {
                            new
                            {
                                kind = "recipe",
                                label = proofTitle,
                                value = "source locale recette",
                                sourceText = proof,
                                pageStart = (int?)page,
                                pageEnd = (int?)page
                            }
                        }
                    }
                    : null
            }).ToArray(),
            score = 0.94
        };
    }

    private static object BuildObservedPageEmbeddedRecipeTitleWithClippedPageTextHit()
    {
        const int page = 59;
        const string title = "Omelette italienne";
        const string pageProof = "Patisserie salee Plats vegetariens Modes de preparation Frire Categories de recettes Plats vegetariens Pour 4 portions. INGREDIENTS : tomates sechees, mozzarella, 8 oeufs, creme, basilic frais et beurre. PREPARATION : rincer le basilic, verser la preparation aux oeufs dans la poele, cuire puis servir chaud.";
        return new
        {
            retrievalQuery = "plats recettes",
            retrievalQueryIndex = 2,
            docPath = "Cuisine/PDF/observed-clipped-page-title.pdf",
            docName = "observed-clipped-page-title.pdf",
            pageStart = page,
            pageEnd = page + 1,
            sectionTitle = "PLATS AUX OEUFS. PLATS AUX OEUFS",
            headingPath = "PLATS AUX OEUFS. PLATS AUX OEUFS",
            excerpt = pageProof,
            fullText = pageProof,
            contextualSnippet = "Matched direct_title_token_route: plats recettes " + pageProof,
            matchedContentCards = new object[]
            {
                new
                {
                    title,
                    kind = "page_embedded_title",
                    pageStart = (int?)page,
                    pageEnd = (int?)page,
                    signals = new[] { title, "omelette", "italienne", "oeufs", "basilic" },
                    evidence = new
                    {
                        quantityFacts = new[]
                        {
                            new
                            {
                                value = 58,
                                unit = "omelette",
                                label = title,
                                sourceText = "58 Omelette italienne [Index]"
                            }
                        },
                        facts = new[]
                        {
                            new
                            {
                                kind = "quantity",
                                label = title,
                                value = "58",
                                unit = "omelette",
                                sourceText = "58 Omelette italienne [Index]",
                                pageStart = (int?)null,
                                pageEnd = (int?)null
                            }
                        }
                    }
                },
                new
                {
                    title = "Rincer le basilic et le secouer pour le secher",
                    kind = "exact_lead",
                    pageStart = (int?)(page + 1),
                    pageEnd = (int?)(page + 1),
                    signals = new[] { "rincer", "basilic", "secher", "poele" }
                },
                new
                {
                    title = "Verser un quart de la preparation aux oeufs dans la poele",
                    kind = "exact_lead",
                    pageStart = (int?)(page + 1),
                    pageEnd = (int?)(page + 1),
                    signals = new[] { "verser", "quart", "oeufs", "poele" }
                }
            },
            score = 0.97
        };
    }

    private static object BuildObservedLeadingOcrContextLabelHit()
    {
        const int page = 35;
        const string proof = "FAJITAS DEJEUNER A JOSIANEPETITS DEJ TRUCS CULINAIRES 1. Il est possible de faire revenir les legumes quelques minutes. INGREDIENTS : tortillas, oeufs, legumes et sauce. PREPARATION : garnir, chauffer et servir.";
        return new
        {
            retrievalQuery = "petit-dejeuner",
            retrievalQueryIndex = 0,
            docPath = "Knowledge/Meals/ocr-context-label.pdf",
            docName = "ocr-context-label.pdf",
            categoryPath = "Knowledge/Meals",
            pageStart = page,
            pageEnd = page,
            sectionTitle = "PETITS DEJ TRUCS CULINAIRES",
            headingPath = "PETITS DEJ TRUCS CULINAIRES",
            excerpt = proof,
            fullText = proof,
            contextualSnippet = "Matched direct_title_token_route: petit-dejeuner " + proof,
            score = 0.96
        };
    }

    private static object BuildDelimitedFieldValueFragmentHit()
    {
        const int page = 72;
        const string title = "Clamp, bracket";
        const string proof = "Maintenance package. Components : Clamp, bracket. Procedure : inspect, tighten and record the documented operation.";
        return new
        {
            retrievalQuery = "options concretes",
            retrievalQueryIndex = 1,
            docPath = "Knowledge/Procedures/field-fragment.pdf",
            docName = "field-fragment.pdf",
            categoryPath = "Knowledge/Procedures",
            pageStart = page,
            pageEnd = page,
            sectionTitle = title,
            headingPath = title,
            excerpt = proof,
            fullText = proof,
            contextualSnippet = "Matched direct_title_token_route: options concretes " + proof,
            score = 0.95
        };
    }

    private static object BuildInlineDelimitedListFragmentHit()
    {
        const int page = 73;
        const string title = "Salt, pepper";
        const string proof = "Documented option beta. Materials include washers, salt, pepper. Procedure : inspect, tighten and record the documented operation.";
        return new
        {
            retrievalQuery = "options concretes",
            retrievalQueryIndex = 2,
            docPath = "Knowledge/Procedures/inline-list-fragment.pdf",
            docName = "inline-list-fragment.pdf",
            categoryPath = "Knowledge/Procedures",
            pageStart = page,
            pageEnd = page,
            sectionTitle = title,
            headingPath = title,
            excerpt = proof,
            fullText = proof,
            contextualSnippet = "Matched direct_title_token_route: options concretes " + proof,
            score = 0.94
        };
    }

    private static void AddToolJson(ToolResults toolResults, string toolName, object payload)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = doc.RootElement.Clone()
        });
    }

    private static int CountInlineOpenTokens(string value)
        => value.Split(new[] { "[[open|" }, StringSplitOptions.None).Length - 1;

    private static bool HasPrecisePlanningCandidateRejectionReason(string line)
        => line.Contains("reason=generic_planning_context", StringComparison.OrdinalIgnoreCase)
           || line.Contains("reason=planning_frame_or_advice", StringComparison.OrdinalIgnoreCase)
           || line.Contains("reason=weak_candidate_title", StringComparison.OrdinalIgnoreCase)
           || line.Contains("reason=procedure_sentence_title", StringComparison.OrdinalIgnoreCase)
           || line.Contains("reason=not_concrete_candidate_title", StringComparison.OrdinalIgnoreCase)
           || line.Contains("reason=missing_candidate_title", StringComparison.OrdinalIgnoreCase)
           || line.Contains("reason=missing_direct_candidate_evidence", StringComparison.OrdinalIgnoreCase);

    private static ToolResults BuildExplicitPagedCardEvidencePlanningResults(int count)
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = ExplicitPagedCardRecipeTitles.Take(count)
                .Select((title, index) => new
                {
                    localEvidence = $"{title}. Procedure : etape 1 verifier l'element documente. Quantite : {index + 1} unite. Materiel : support local.",
                    docPath = $"Knowledge/category/card-source-{index + 1}.pdf",
                    docName = $"card-source-{index + 1}.pdf",
                    pageStart = index + 10,
                    pageEnd = index + 10,
                    excerpt = $"{title}. Procedure : etape 1 verifier l'element documente. Quantite : {index + 1} unite. Materiel : support local.",
                    fullText = $"Introduction de page. {title}. Procedure : etape 1 verifier l'element documente. Quantite : {index + 1} unite. Materiel : support local. Fin de page.",
                    matchedContentCards = new[]
                    {
                        new
                        {
                            title,
                            kind = "page_embedded_title",
                            pageStart = index + 10,
                            pageEnd = index + 10,
                            evidence = new
                            {
                                quantityFacts = new[]
                                {
                                    new
                                    {
                                        value = 1,
                                        unit = "portion",
                                        label = title,
                                        sourceText = $"{title}. Procedure : etape 1 verifier l'element documente. Quantite : {index + 1} unite. Materiel : support local."
                                    }
                                },
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "instruction",
                                        label = title,
                                        value = "element documente et verifie",
                                        sourceText = $"{title}. Procedure : etape 1 verifier l'element documente. Quantite : {index + 1} unite. Materiel : support local.",
                                        pageStart = index + 10,
                                        pageEnd = index + 10
                                    }
                                }
                            }
                        }
                    },
                    score = 0.95
                })
                .ToArray()
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildCompoundCardTitlePlanningResults()
    {
        const string firstTitle = "Omelette aux herbes";
        const string secondTitle = "Macaroni tex mex";
        var firstEvidence = $"{firstTitle}. Ingredients : 3 oeufs et herbes. Preparation : battre, cuire et servir.";
        var secondEvidence = $"{secondTitle}. Ingredients : pates, haricots et epices. Preparation : cuire, melanger et servir.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/compound-card.pdf",
                    docName = "compound-card.pdf",
                    pageStart = 42,
                    pageEnd = 42,
                    excerpt = $"{firstEvidence} {secondEvidence}",
                    fullText = $"{firstEvidence} {secondEvidence}",
                    matchedContentCards = new[]
                    {
                        new
                        {
                            title = $"{firstTitle} | {secondTitle}",
                            kind = "page_embedded_title",
                            pageStart = 42,
                            pageEnd = 42,
                            evidence = new
                            {
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "instruction",
                                        label = firstTitle,
                                        value = "element documente et verifie",
                                        sourceText = firstEvidence,
                                        pageStart = 42,
                                        pageEnd = 42
                                    },
                                    new
                                    {
                                        kind = "instruction",
                                        label = secondTitle,
                                        value = "element documente et verifie",
                                        sourceText = secondEvidence,
                                        pageStart = 42,
                                        pageEnd = 42
                                    }
                                }
                            }
                        }
                    },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildCardPlusDistinctFallbackPlanningResults()
    {
        const string cardTitle = "Omelette aux herbes";
        const string fallbackTitle = "Macaroni tex mex";
        var cardEvidence = $"{cardTitle}. Ingredients : 3 oeufs et herbes. Preparation : battre, cuire et servir.";
        var fallbackEvidence = $"{fallbackTitle}. Ingredients : pates, haricots et epices. Preparation : cuire, melanger et servir.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/card-plus-fallback.pdf",
                    docName = "card-plus-fallback.pdf",
                    pageStart = 43,
                    pageEnd = 43,
                    excerpt = $"{fallbackEvidence} {cardEvidence}",
                    fullText = $"{fallbackEvidence} {cardEvidence}",
                    matchedContentCards = new[]
                    {
                        new
                        {
                            title = cardTitle,
                            kind = "page_embedded_title",
                            pageStart = 43,
                            pageEnd = 43,
                            evidence = new
                            {
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "instruction",
                                        label = cardTitle,
                                        value = "element documente et verifie",
                                        sourceText = cardEvidence,
                                        pageStart = 43,
                                        pageEnd = 43
                                    }
                                }
                            }
                        }
                    },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildDetachedExplicitPagedCardEvidencePlanningResults()
    {
        const string title = "Omelette aux herbes";
        const string sourceText = "Omelette aux herbes. Ingredients : 3 oeufs, herbes. Preparation : battre, cuire et servir.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/detached-card-source.pdf",
                    docName = "detached-card-source.pdf",
                    pageStart = 18,
                    pageEnd = 18,
                    excerpt = "Cette page contient uniquement des conseils generaux sur l'organisation des repas.",
                    fullText = "Cette page contient uniquement des conseils generaux sur l'organisation des repas et ne cite aucune recette concrete.",
                    matchedContentCards = new[]
                    {
                        new
                        {
                            title,
                            kind = "page_embedded_title",
                            pageStart = 18,
                            pageEnd = 18,
                            evidence = new
                            {
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "instruction",
                                        label = title,
                                        value = "element documente par une carte detachee",
                                        sourceText,
                                        pageStart = 18,
                                        pageEnd = 18
                                    }
                                }
                            }
                        }
                    },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ApiClient CreateApiClient(HttpMessageHandler handler)
    {
        var sut = new ApiClient();
        sut.Configure("http://localhost:5122", "test-api-key", "test-user");

        var field = typeof(ApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(sut, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5122")
        });

        return sut;
    }

    private static string RemoveDiacriticsForAssertion(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class RouterOnlyLlmClient(string routerCompletion) : ILlmClient
    {
        public List<IReadOnlyList<(string role, string content)>> RouterRequests { get; } = new();

        public List<IReadOnlyList<(string role, string content)>> WriterRequests { get; } = new();

        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
        {
            if (forceJson)
            {
                RouterRequests.Add(messages);
                return Task.FromResult(routerCompletion);
            }

            WriterRequests.Add(messages);
            throw new InvalidOperationException("The writer must not run when structured planning evidence contains only generic context.");
        }

        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
        {
            WriterRequests.Add(messages);
            throw new InvalidOperationException("The streaming writer must not run when structured planning evidence contains only generic context.");
        }
    }

    private static ToolResults BuildCardTitleOnlyPlanningResults()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/card-only.pdf",
                    docName = "card-only.pdf",
                    pageStart = 42,
                    pageEnd = 42,
                    excerpt = "Ingredients : 2 bananes, 25 cl de lait de coco. Preparation : mixez puis servez frais.",
                    fullText = "Ingredients : 2 bananes, 25 cl de lait de coco. Preparation : mixez puis servez frais.",
                    matchedContentCards = new[]
                    {
                        new
                        {
                            title = "Smoothie a l'ananas",
                            kind = "unit_lead",
                            evidence = new
                            {
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "ingredient",
                                        label = "Ingredients",
                                        value = "2 bananes, 25 cl de lait de coco",
                                        sourceText = "Ingredients : 2 bananes, 25 cl de lait de coco. Preparation : mixez puis servez frais."
                                    }
                                }
                            }
                        }
                    },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildCardEvidenceOnlyPlanningResults()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/card-evidence-only.pdf",
                    docName = "card-evidence-only.pdf",
                    pageStart = 38,
                    pageEnd = 38,
                    excerpt = "Cette page donne des conseils generaux d'organisation des repas et de conservation des aliments.",
                    fullText = "Cette page donne des conseils generaux d'organisation des repas et de conservation des aliments.",
                    matchedContentCards = new[]
                    {
                        new
                        {
                            title = "Smoothie a l'ananas",
                            kind = "unit_lead",
                            evidence = new
                            {
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "ingredient",
                                        label = "Smoothie a l'ananas",
                                        value = "ananas, lait, banane",
                                        sourceText = "Smoothie a l'ananas. Ingredients : ananas, lait, banane. Preparation : mixer et servir frais."
                                    }
                                }
                            }
                        }
                    },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildRecipeIndexOnlyPlanningResults()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/index.pdf",
                    docName = "index.pdf",
                    pageStart = 177,
                    pageEnd = 177,
                    excerpt = "Index des recettes : Smoothie banane lait de coco 309; Volaille a l'ananas et poivron rouge 331; Tarte citron 338.",
                    fullText = "Index des recettes : Smoothie banane lait de coco 309; Volaille a l'ananas et poivron rouge 331; Tarte citron 338.",
                    matchedContentCards = new[] { new { title = "Smoothie banane lait de coco", kind = "unit_lead" } },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildExplicitPagedIndexCardPlanningResults()
    {
        const string indexText = "Index des recettes : Croquettes de poulet 18; Volaille a l'ananas et poivron rouge 331; Tarte citron 338.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/card-index.pdf",
                    docName = "card-index.pdf",
                    pageStart = 18,
                    pageEnd = 18,
                    excerpt = indexText,
                    fullText = indexText,
                    matchedContentCards = new[]
                    {
                        new
                        {
                            title = "Croquettes de poulet",
                            kind = "page_embedded_title",
                            pageStart = 18,
                            pageEnd = 18,
                            evidence = new
                            {
                                quantityFacts = new[]
                                {
                                    new
                                    {
                                        value = 18,
                                        unit = "page",
                                        label = "Croquettes de poulet",
                                        sourceText = indexText
                                    }
                                },
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "index",
                                        label = "Croquettes de poulet",
                                        value = "page 18",
                                        sourceText = indexText,
                                        pageStart = 18,
                                        pageEnd = 18
                                    }
                                }
                            }
                        }
                    },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildFloatingCardTitlePlanningResults()
    {
        const string pageRouteText = "Volailles Poule Modes de preparation Frire Categories de recettes Volailles Pour 4 portions, env. 20 pieces.";
        const string floatingCardEvidence = "18 Croquettes de poulet [Index: ] MCRC010 Modes de preparation Frire Categories de recettes Volailles.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/title-anchor-route.pdf",
                    docName = "title-anchor-route.pdf",
                    pageStart = 19,
                    pageEnd = 19,
                    excerpt = pageRouteText,
                    fullText = pageRouteText,
                    contextualSnippet = "Matched title_anchor_route: Volailles Poule Modes de preparation Frire Categories de recettes Volailles.",
                    selectionHints = new
                    {
                        evidenceRole = "actionable_item",
                        actionabilityScore = 11,
                        supportScore = 0,
                        fragmentScore = 0,
                        navigationScore = 0,
                        qualityPenalty = 4
                    },
                    matchedContentCards = new[]
                    {
                        new
                        {
                            title = "Croquettes de poulet",
                            kind = "page_embedded_title",
                            pageStart = 19,
                            pageEnd = 19,
                            evidence = new
                            {
                                quantityFacts = new[]
                                {
                                    new
                                    {
                                        value = 18,
                                        unit = "pieces",
                                        label = "Croquettes de poulet",
                                        sourceText = floatingCardEvidence
                                    }
                                },
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "recipe_index",
                                        label = "Croquettes de poulet",
                                        value = "page 18",
                                        sourceText = floatingCardEvidence,
                                        pageStart = 19,
                                        pageEnd = 19
                                    }
                                }
                            }
                        }
                    },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildMixedPageMismatchedCardEvidencePlanningResults()
    {
        const string macaroniProof = "Macaroni tex mex. Ingredients : macaroni, haricots rouges, tomates. Preparation : cuire les pates, melanger la garniture et servir chaud.";
        const string browniesProof = "Brownies aux haricots noirs. Ingredients : haricots noirs, cacao, sucre. Preparation : melanger, cuire 25 minutes et laisser refroidir.";
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/mixed-page.pdf",
                    docName = "mixed-page.pdf",
                    pageStart = 29,
                    pageEnd = 29,
                    excerpt = $"{macaroniProof} {browniesProof}",
                    fullText = $"{macaroniProof} {browniesProof}",
                    matchedContentCards = new[]
                    {
                        new
                        {
                            title = "Macaroni tex mex",
                            kind = "exact_lead",
                            pageStart = 29,
                            pageEnd = 29,
                            evidence = new
                            {
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "recipe",
                                        label = "Macaroni tex mex",
                                        value = "macaroni, haricots rouges, tomates",
                                        sourceText = macaroniProof,
                                        pageStart = (int?)29,
                                        pageEnd = (int?)29
                                    }
                                }
                            }
                        },
                        new
                        {
                            title = "Smoothie a l'ananas",
                            kind = "unit_lead",
                            pageStart = 29,
                            pageEnd = 29,
                            evidence = new
                            {
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "recipe",
                                        label = "Smoothie a l'ananas",
                                        value = "haricots noirs, cacao, sucre",
                                        sourceText = browniesProof,
                                        pageStart = (int?)29,
                                        pageEnd = (int?)29
                                    }
                                }
                            }
                        },
                        new
                        {
                            title = "Collection pratique parents presses",
                            kind = "unit_lead",
                            pageStart = 29,
                            pageEnd = 29,
                            evidence = new
                            {
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "recipe",
                                        label = "Collection pratique parents presses",
                                        value = "macaroni, haricots rouges, tomates",
                                        sourceText = macaroniProof,
                                        pageStart = (int?)29,
                                        pageEnd = (int?)29
                                    }
                                }
                            }
                        },
                        new
                        {
                            title = "Brownies aux haricots noirs",
                            kind = "page_embedded_title",
                            pageStart = 29,
                            pageEnd = 29,
                            evidence = new
                            {
                                facts = new[]
                                {
                                    new
                                    {
                                        kind = "recipe",
                                        label = "Brownies aux haricots noirs",
                                        value = "haricots noirs, cacao, sucre",
                                        sourceText = browniesProof,
                                        pageStart = (int?)29,
                                        pageEnd = (int?)29
                                    }
                                }
                            }
                        }
                    },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildDistantUnrelatedPageRecipeProofResults()
    {
        var distantGap = new string('x', 900);
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/mixed-page.pdf",
                    docName = "mixed-page.pdf",
                    pageStart = 42,
                    pageEnd = 42,
                    excerpt = $"Smoothie a l'ananas. Sommaire uniquement. {distantGap} Fondant au chocolat. Ingredients : 200 g chocolat. Preparation : cuire 20 minutes puis servir.",
                    fullText = $"Smoothie a l'ananas. Sommaire uniquement. {distantGap} Fondant au chocolat. Ingredients : 200 g chocolat. Preparation : cuire 20 minutes puis servir.",
                    matchedContentCards = new[] { new { title = "Smoothie a l'ananas", kind = "unit_lead" } },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildNearbyUnrelatedPageRecipeProofResults()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/nearby-mixed-page.pdf",
                    docName = "nearby-mixed-page.pdf",
                    pageStart = 42,
                    pageEnd = 42,
                    excerpt = "Smoothie a l'ananas. Sommaire uniquement. Fondant au chocolat. Ingredients : 200 g chocolat. Preparation : cuire 20 minutes puis servir.",
                    fullText = "Smoothie a l'ananas. Sommaire uniquement. Fondant au chocolat. Ingredients : 200 g chocolat. Preparation : cuire 20 minutes puis servir.",
                    matchedContentCards = new[]
                    {
                        new
                        {
                            title = "Smoothie a l'ananas",
                            kind = "unit_lead"
                        },
                        new
                        {
                            title = "Fondant au chocolat",
                            kind = "unit_lead"
                        }
                    },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildConcreteRecipePlanningResults()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/recipe-1.pdf",
                    docName = "recipe-1.pdf",
                    pageStart = 12,
                    pageEnd = 12,
                    excerpt = "Smoothie banane lait de coco. Ingredients : 2 bananes, 25 cl de lait de coco. Preparation : mixez puis servez frais.",
                    fullText = "Smoothie banane lait de coco. Ingredients : 2 bananes, 25 cl de lait de coco. Preparation : mixez puis servez frais.",
                    matchedContentCards = new[] { new { title = "Smoothie banane lait de coco", kind = "unit_lead" } },
                    score = 0.95
                },
                new
                {
                    docPath = "Knowledge/category/recipe-2.pdf",
                    docName = "recipe-2.pdf",
                    pageStart = 18,
                    pageEnd = 18,
                    excerpt = "Volaille a l'ananas et poivron rouge. Ingredients : 4 escalopes de volaille, 1 ananas, 2 poivrons rouges. Preparation : cuire la volaille puis ajouter les legumes.",
                    fullText = "Volaille a l'ananas et poivron rouge. Ingredients : 4 escalopes de volaille, 1 ananas, 2 poivrons rouges. Preparation : cuire la volaille puis ajouter les legumes.",
                    matchedContentCards = new[] { new { title = "Volaille a l'ananas et poivron rouge", kind = "unit_lead" } },
                    score = 0.92
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildSparsePlanningCandidateAndContextResults()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new object[]
            {
                BuildGenericSlotRoutedRecipeHit("Omelette aux herbes", "diner recettes", 1),
                new
                {
                    retrievalQuery = "repas semaine",
                    retrievalQueryIndex = 1,
                    docPath = "Knowledge/Planning/menu-planning-guide.pdf",
                    docName = "menu-planning-guide.pdf",
                    categoryPath = "Knowledge/Planning",
                    pageStart = 22,
                    pageEnd = 22,
                    sectionTitle = "Planification de menus",
                    headingPath = "Planification de menus",
                    excerpt = "Planification de menus : construire une liste d'options concretes, verifier les sources disponibles, puis repartir les elements utiles dans les creneaux demandes.",
                    fullText = "Planification de menus : construire une liste d'options concretes, verifier les sources disponibles, puis repartir les elements utiles dans les creneaux demandes. La page sert de contexte pour organiser un planning sans inventer les elements absents.",
                    contextualSnippet = "Contexte source pour organiser un planning multi-creneaux quand toutes les cases ne sont pas encore couvertes.",
                    score = 0.87
                },
                new
                {
                    retrievalQuery = "options semaine",
                    retrievalQueryIndex = 2,
                    docPath = "Knowledge/Planning/source-selection.pdf",
                    docName = "source-selection.pdf",
                    categoryPath = "Knowledge/Planning",
                    pageStart = 23,
                    pageEnd = 23,
                    sectionTitle = "Selection des sources",
                    headingPath = "Selection des sources",
                    excerpt = "Selection des sources : garder les pages utiles, eviter les doublons et separer les preuves concretes du simple contexte.",
                    fullText = "Selection des sources : garder les pages utiles, eviter les doublons et separer les preuves concretes du simple contexte. Cette page aide le redacteur a citer seulement les sources qui appuient vraiment la reponse.",
                    contextualSnippet = "Contexte source pour filtrer les references redondantes ou trop vagues.",
                    score = 0.82
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildFrenchOcrRecipePlanningResultsWithoutCards()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Cuisine/PDF/ocr-recettes.pdf",
                    docName = "ocr-recettes.pdf",
                    pageStart = 21,
                    pageEnd = 21,
                    excerpt = "SMOOTHIE VERT Ingr\u00e9dients : banane, epinards, lait et yogourt. Pr\u00e9paration : mixer tous les ingredients et servir frais.",
                    fullText = "Page 21 recettes rapides. SMOOTHIE VERT Ingr\u00e9dients : banane, epinards, lait et yogourt. Pr\u00e9paration : mixer tous les ingredients et servir frais.",
                    contextualSnippet = "SMOOTHIE VERT Ingr\u00e9dients : banane, epinards, lait et yogourt. Pr\u00e9paration : mixer tous les ingredients.",
                    score = 0.95
                },
                new
                {
                    docPath = "Cuisine/PDF/ocr-recettes.pdf",
                    docName = "ocr-recettes.pdf",
                    pageStart = 22,
                    pageEnd = 22,
                    excerpt = "GALETTES AU GRUAU Ingredients : gruau, banane, oeuf et cannelle. Preparation : former des galettes et cuire a la poele.",
                    fullText = "Page 22 collation. GALETTES AU GRUAU Ingredients : gruau, banane, oeuf et cannelle. Preparation : former des galettes et cuire a la poele.",
                    contextualSnippet = "GALETTES AU GRUAU Ingredients : gruau, banane, oeuf et cannelle. Preparation : former des galettes.",
                    score = 0.94
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildShortTitleRecipePlanningResults()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/short-title-1.pdf",
                    docName = "short-title-1.pdf",
                    pageStart = 7,
                    pageEnd = 7,
                    excerpt = "Gratin dauphinois Pour 4 personnes 1,5 kg de pommes de terre, 50 cl de lait entier, 40 cl de creme fraiche, ail, sel et poivre. Preparation : prechauffez le four, tranchez les pommes de terre, assemblez et cuisez 60 minutes.",
                    fullText = "Gratin dauphinois Pour 4 personnes 1,5 kg de pommes de terre, 50 cl de lait entier, 40 cl de creme fraiche, ail, sel et poivre. Preparation : prechauffez le four, tranchez les pommes de terre, assemblez et cuisez 60 minutes.",
                    matchedContentCards = new[] { new { title = "Gratin dauphinois", kind = "unit_lead" } },
                    score = 0.95
                },
                new
                {
                    docPath = "Knowledge/category/short-title-2.pdf",
                    docName = "short-title-2.pdf",
                    pageStart = 8,
                    pageEnd = 8,
                    excerpt = "Osso buco Pour 4 personnes 4 tranches de jarret de veau, carottes, tomates, bouillon et vin blanc. Preparation : faites dorer la viande, ajoutez les legumes, puis laissez mijoter 90 minutes.",
                    fullText = "Osso buco Pour 4 personnes 4 tranches de jarret de veau, carottes, tomates, bouillon et vin blanc. Preparation : faites dorer la viande, ajoutez les legumes, puis laissez mijoter 90 minutes.",
                    matchedContentCards = new[] { new { title = "Osso buco", kind = "unit_lead" } },
                    score = 0.94
                },
                new
                {
                    docPath = "Knowledge/category/short-title-3.pdf",
                    docName = "short-title-3.pdf",
                    pageStart = 9,
                    pageEnd = 9,
                    excerpt = "Paella mixte Pour 6 personnes riz, poulet, fruits de mer, poivrons, petits pois, safran et bouillon. Preparation : faites revenir les ingredients, ajoutez le riz et laissez cuire jusqu'a absorption.",
                    fullText = "Paella mixte Pour 6 personnes riz, poulet, fruits de mer, poivrons, petits pois, safran et bouillon. Preparation : faites revenir les ingredients, ajoutez le riz et laissez cuire jusqu'a absorption.",
                    matchedContentCards = new[] { new { title = "Paella mixte", kind = "unit_lead" } },
                    score = 0.93
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildFieldLabelPlanningFragmentResults()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/category/field-label-1.pdf",
                    docName = "field-label-1.pdf",
                    pageStart = 31,
                    pageEnd = 31,
                    excerpt = "Ingredients : 300 g de farine, 2 oeufs, 25 cl de lait. Preparation : melanger, cuire et servir.",
                    fullText = "Ingredients : 300 g de farine, 2 oeufs, 25 cl de lait. Preparation : melanger, cuire et servir.",
                    matchedContentCards = new[] { new { title = "Ingredients", kind = "unit_lead" } },
                    score = 0.95
                },
                new
                {
                    docPath = "Knowledge/category/field-label-2.pdf",
                    docName = "field-label-2.pdf",
                    pageStart = 32,
                    pageEnd = 32,
                    excerpt = "Nombre de portions 6. Ingredients : farine, oeufs, lait. Preparation : fouetter puis cuire.",
                    fullText = "Nombre de portions 6. Ingredients : farine, oeufs, lait. Preparation : fouetter puis cuire.",
                    matchedContentCards = new[] { new { title = "Nombre de portions", kind = "unit_lead" } },
                    score = 0.95
                },
                new
                {
                    docPath = "Knowledge/category/field-label-3.pdf",
                    docName = "field-label-3.pdf",
                    pageStart = 33,
                    pageEnd = 33,
                    excerpt = "I 60 E Nombre de galettes Ingredients : farine, beurre et eau. Preparation : cuire sur une plaque chaude.",
                    fullText = "I 60 E Nombre de galettes Ingredients : farine, beurre et eau. Preparation : cuire sur une plaque chaude.",
                    matchedContentCards = new[] { new { title = "I 60 E Nombre de galettes", kind = "unit_lead" } },
                    score = 0.95
                },
                new
                {
                    docPath = "Knowledge/category/field-label-4.pdf",
                    docName = "field-label-4.pdf",
                    pageStart = 34,
                    pageEnd = 34,
                    excerpt = "Fagonner desbouletteset lescuire sur une plaque. Ingredients : viande hachee, chapelure et oeuf. Preparation : former puis cuire.",
                    fullText = "Fagonner desbouletteset lescuire sur une plaque. Ingredients : viande hachee, chapelure et oeuf. Preparation : former puis cuire.",
                    matchedContentCards = new[] { new { title = "Fagonner desbouletteset lescuire sur", kind = "unit_lead" } },
                    score = 0.95
                },
                new
                {
                    docPath = "Knowledge/category/field-label-5.pdf",
                    docName = "field-label-5.pdf",
                    pageStart = 35,
                    pageEnd = 35,
                    excerpt = "Ingredients * Verser l'huile dans la poele. Ingredients : huile, oignons et tomate. Preparation : verser puis cuire.",
                    fullText = "Ingredients * Verser l'huile dans la poele. Ingredients : huile, oignons et tomate. Preparation : verser puis cuire.",
                    matchedContentCards = new[] { new { title = "Ingrédients * Verser l'huile dans la poêle", kind = "unit_lead" } },
                    score = 0.95
                },
                new
                {
                    docPath = "Knowledge/category/field-label-6.pdf",
                    docName = "field-label-6.pdf",
                    pageStart = 36,
                    pageEnd = 36,
                    excerpt = "Elements. Ingredients : farine, oeufs et lait. Preparation : melanger puis cuire.",
                    fullText = "Elements. Ingredients : farine, oeufs et lait. Preparation : melanger puis cuire.",
                    matchedContentCards = new[] { new { title = "Éléments", kind = "unit_lead" } },
                    score = 0.95
                },
                new
                {
                    docPath = "Knowledge/category/field-label-7.pdf",
                    docName = "field-label-7.pdf",
                    pageStart = 37,
                    pageEnd = 37,
                    excerpt = "Nos outils FACILITEMPS. Catalogue d'outils et conseils generaux. Preparation : verifier les pages utiles.",
                    fullText = "Nos outils FACILITEMPS. Catalogue d'outils et conseils generaux. Preparation : verifier les pages utiles.",
                    matchedContentCards = new[] { new { title = "Nos outils FACILITEMPS", kind = "unit_lead" } },
                    score = 0.95
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildObservedNoisyRecipePlanningResults()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Cuisine/PDF/facilitemps.pdf",
                    docName = "facilitemps.pdf",
                    pageStart = 16,
                    pageEnd = 16,
                    excerpt = "LA CON SERVATION DU ROTI DE PALETTE IDEES DE REPAS. Ingredients : roti, sauce et legumes. Preparation : cuire, conserver et servir.",
                    fullText = "LA CON SERVATION DU ROTI DE PALETTE IDEES DE REPAS. Ingredients : roti, sauce et legumes. Preparation : cuire, conserver et servir.",
                    matchedContentCards = new[] { new { title = "LA CON SERVATION DU ROTI DE PALETTE IDEES DE REPAS", kind = "unit_lead" } },
                    score = 0.96
                },
                new
                {
                    docPath = "Cuisine/PDF/facilitemps.pdf",
                    docName = "facilitemps.pdf",
                    pageStart = 32,
                    pageEnd = 32,
                    excerpt = "Egoutter le boeuf hache une fois cuit. Ingredients : boeuf hache, oignons et sauce. Preparation : egoutter puis cuire.",
                    fullText = "Egoutter le boeuf hache une fois cuit. Ingredients : boeuf hache, oignons et sauce. Preparation : egoutter puis cuire.",
                    matchedContentCards = new[] { new { title = "Egoutter le boeuf hache une fois cuit", kind = "unit_lead" } },
                    score = 0.95
                },
                new
                {
                    docPath = "Cuisine/PDF/facilitemps.pdf",
                    docName = "facilitemps.pdf",
                    pageStart = 44,
                    pageEnd = 44,
                    excerpt = "SAUCE AU CON COMBRE. Ingredients : concombre, yogourt et fines herbes. Preparation : melanger et servir.",
                    fullText = "SAUCE AU CON COMBRE. Ingredients : concombre, yogourt et fines herbes. Preparation : melanger et servir.",
                    matchedContentCards = new[] { new { title = "SAUCE AU CON COMBRE", kind = "unit_lead" } },
                    score = 0.94
                },
                new
                {
                    docPath = "Cuisine/PDF/facilitemps.pdf",
                    docName = "facilitemps.pdf",
                    pageStart = 57,
                    pageEnd = 57,
                    excerpt = "Le poulet cuit. Ingredients : poulet cuit, bouillon et legumes. Preparation : rechauffer puis servir.",
                    fullText = "Le poulet cuit. Ingredients : poulet cuit, bouillon et legumes. Preparation : rechauffer puis servir.",
                    matchedContentCards = new[] { new { title = "Le poulet cuit", kind = "unit_lead" } },
                    score = 0.93
                },
                new
                {
                    docPath = "Knowledge/Meals/facilitemps.pdf",
                    docName = "facilitemps.pdf",
                    pageStart = 60,
                    pageEnd = 60,
                    excerpt = "Rincer les blancs de poulet sous l'eau froide et eponger. Ingredients : poulet, eau et papier. Preparation : rincer puis cuire.",
                    fullText = "Rincer les blancs de poulet sous l'eau froide et eponger. Ingredients : poulet, eau et papier. Preparation : rincer puis cuire.",
                    matchedContentCards = new[] { new { title = "Rincer les blancs de poulet sous l'eau froide et eponger", kind = "unit_lead" } },
                    score = 0.925
                },
                new
                {
                    docPath = "Knowledge/Meals/facilitemps.pdf",
                    docName = "facilitemps.pdf",
                    pageStart = 61,
                    pageEnd = 61,
                    excerpt = "Duree Apres le signal, faire cuire 30. Ingredients : eau et riz. Preparation : cuire et servir.",
                    fullText = "Duree Apres le signal, faire cuire 30. Ingredients : eau et riz. Preparation : cuire et servir.",
                    matchedContentCards = new[] { new { title = "Duree Apres le signal, faire cuire 30", kind = "unit_lead" } },
                    score = 0.915
                },
                new
                {
                    docPath = "Cuisine/PDF/si-on-cuisinait.pdf",
                    docName = "si-on-cuisinait.pdf",
                    pageStart = 71,
                    pageEnd = 71,
                    excerpt = "Salade de lentilles. Ingredients : lentilles, carottes, oignons et vinaigrette. Preparation : cuire les lentilles, refroidir, assaisonner et servir.",
                    fullText = "Salade de lentilles. Ingredients : lentilles, carottes, oignons et vinaigrette. Preparation : cuire les lentilles, refroidir, assaisonner et servir.",
                    matchedContentCards = new[] { new { title = "Salade de lentilles", kind = "unit_lead" } },
                    score = 0.92
                },
                new
                {
                    docPath = "Cuisine/PDF/si-on-cuisinait.pdf",
                    docName = "si-on-cuisinait.pdf",
                    pageStart = 72,
                    pageEnd = 72,
                    excerpt = "Gratin dauphinois. Ingredients : pommes de terre, lait, creme, ail et fromage. Preparation : trancher les pommes de terre, assembler et cuire au four.",
                    fullText = "Gratin dauphinois. Ingredients : pommes de terre, lait, creme, ail et fromage. Preparation : trancher les pommes de terre, assembler et cuire au four.",
                    matchedContentCards = new[] { new { title = "Gratin dauphinois", kind = "unit_lead" } },
                    score = 0.91
                },
                new
                {
                    docPath = "Reports/IPCC_SR15_Full_Report.pdf",
                    docName = "IPCC_SR15_Full_Report.pdf",
                    pageStart = 45,
                    pageEnd = 45,
                    excerpt = "Origins, institutional arrangements, and design. Ingredients : rice, vegetables and sauce. Preparation : combine, heat and serve.",
                    fullText = "Origins, institutional arrangements, and design. Ingredients : rice, vegetables and sauce. Preparation : combine, heat and serve.",
                    matchedContentCards = new[] { new { title = "Origins, institutional arrangements, and design", kind = "unit_lead" } },
                    score = 0.99
                },
                new
                {
                    docPath = "Cuisine/PDF/glossaire-techniques.pdf",
                    docName = "glossaire-techniques.pdf",
                    pageStart = 4,
                    pageEnd = 4,
                    excerpt = "Hacher Couper en petits morce aux un aliment. Ingredients : legumes, couteau et planche. Preparation : lire la definition avant de commencer.",
                    fullText = "Glossaire des techniques. Hacher Couper en petits morce aux un aliment. Ingredients : legumes, couteau et planche. Preparation : lire la definition avant de commencer.",
                    matchedContentCards = new[] { new { title = "Hacher Couper en petits morce aux un aliment", kind = "unit_lead" } },
                    score = 0.98
                },
                new
                {
                    docPath = "Knowledge/Glossary/definitions.pdf",
                    docName = "definitions.pdf",
                    pageStart = 5,
                    pageEnd = 5,
                    excerpt = "TERMESCHALEUR HUMIDE. Procedure : consulter le lexique et choisir ensuite une option documentee.",
                    fullText = "TERMESCHALEUR HUMIDE. Procedure : consulter le lexique et choisir ensuite une option documentee.",
                    matchedContentCards = new[] { new { title = "TERMESCHALEUR HUMIDE", kind = "unit_lead" } },
                    score = 0.97
                }
            }
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildGenericPlanningContextResults()
    {
        var titles = new[]
        {
            "Repas leger entre minuit et 1 heure du matin",
            "Cuisiner un ou deux repas par semaine",
            "Collation vers 16h",
            "Sieste l'apres-midi et coucher avant",
            "Sa Diner"
        };
        var payload = JsonSerializer.Serialize(new
        {
            hits = titles
                .Select((title, index) => new
                {
                    docPath = $"Knowledge/category/context-{index + 1}.pdf",
                    docName = $"context-{index + 1}.pdf",
                    pageStart = index + 1,
                    pageEnd = index + 1,
                    excerpt = $"{title}. Conseils generaux d'organisation et horaires.",
                    fullText = $"{title}. Conseils generaux d'organisation et horaires.",
                    matchedContentCards = new[] { new { title, kind = "unit_lead" } },
                    score = 0.95
                })
                .ToArray()
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static ToolResults BuildMixedGenericAndRecipePlanningResults()
    {
        var toolResults = BuildGenericPlanningContextResults();
        foreach (var item in BuildPlanningCandidateResults(15).Items)
            toolResults.Items.Add(item);

        return toolResults;
    }

    private static ToolResults BuildProfileOnlyPlanningResults()
    {
        var titles = new[]
        {
            "Smoothie a l'ananas et au lait de soja",
            "Salade de quinoa avec legumes et tofu grille",
            "Poulet roti avec pommes de terre",
            "Yaourt grec aux fruits rouges",
            "Pates aux champignons",
            "Poisson grille avec legumes"
        };
        var payload = JsonSerializer.Serialize(new
        {
            hits = titles
                .Select((title, index) => new
                {
                    docPath = $"Knowledge/category/profile-only-{index + 1}.pdf",
                    docName = $"profile-only-{index + 1}.pdf",
                    pageStart = index + 1,
                    pageEnd = index + 1,
                    excerpt = "Conseils generaux pour organiser les repas et verifier les contraintes avant de choisir les elements concrets.",
                    fullText = "Conseils generaux pour organiser les repas et verifier les contraintes avant de choisir les elements concrets.",
                    contextualSnippet = $"Matched profile title: {title}. Document: profile-only-{index + 1}.pdf",
                    matchedContentCards = new[] { new { title, kind = "unit_lead" } },
                    score = 0.95
                })
                .ToArray()
        });
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized.Clone() });
        return toolResults;
    }

    private static readonly string[] ExplicitPagedCardRecipeTitles =
    {
        "Omelette aux herbes",
        "Galettes au gruau",
        "Smoothie banane coco",
        "Muffins aux bleuets",
        "Scones aux canneberges",
        "Croquettes de poulet",
        "Salade de lentilles",
        "Gratin de courgettes",
        "Poisson au four",
        "Riz aux legumes",
        "Pates tomate basilic",
        "Poulet au citron",
        "Sandwich au tofu",
        "Soupe de legumes",
        "Macaroni tex mex",
        "Quiche aux champignons",
        "Veloute de potiron",
        "Taboule aux herbes",
        "Curry de pois chiches",
        "Tarte aux pommes",
        "Brownies aux haricots noirs",
        "Compote aux poires"
    };

    private static readonly string[] SlotAwareMealRecipeTitles =
    {
        "Galettes au gruau",
        "Smoothie banane coco",
        "Compote aux poires",
        "Scones aux canneberges",
        "Riz gluant au lait de coco et mangue",
        "Boeuf bourguignon",
        "Gratin dauphinois",
        "Osso buco",
        "Moules marinieres",
        "Paella mixte",
        "Pizzas rigolotes",
        "Cassoulet toulousain",
        "Tomates farcies",
        "Quiche aux champignons",
        "Curry de pois chiches",
        "Brownies aux haricots noirs",
        "Houmous de betterave",
        "Tarte aux pommes",
        "Beignets espagnols",
        "Muffins aux bleuets"
    };
}
