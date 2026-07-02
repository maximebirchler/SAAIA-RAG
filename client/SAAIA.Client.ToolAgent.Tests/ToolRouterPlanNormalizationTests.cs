using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ToolRouterPlanNormalizationTests
{
    [Theory]
    [InlineData("set_mode", "meta.set_mode")]
    [InlineData("documents.search", "inventory.find")]
    [InlineData("inventory.changed_since", "inventory.changed_since")]
    [InlineData("inventory.health", "inventory.health")]
    public void Normalize_router_intent_matches_v3_contract(string rawIntent, string expected)
    {
        Assert.Equal(expected, ToolAgentOrchestrator.NormalizeRouterIntentForTests(rawIntent));
    }

    [Fact]
    public void Router_json_repair_salvages_extra_closing_bracket_inside_tool_call_args()
    {
        const string malformed = """
        {
          "mode": "auto",
          "language": "fr",
          "intent": "rag.multi_search",
          "toolCalls": [
            {
              "name": "rag.multi_search",
              "args": {
                "queries": ["petit dejeuner recettes", "diner recettes"]],
                "topK": 8
              }
            }
          ],
          "needClarification": false
        }
        """;

        Assert.True(ToolAgentOrchestrator.TryRepairJsonObjectForParsingForTests(malformed, out var repaired), repaired);
        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var plan = JsonSerializer.Deserialize<RouterPlan>(
            repaired,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(plan);
        Assert.Equal("rag.multi_search", plan!.Intent);
        Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", plan.ToolCalls[0].Name);
        Assert.Equal(2, plan.ToolCalls[0].Args.GetProperty("queries").GetArrayLength());
    }

    [Fact]
    public void Stored_summary_store_intent_routes_to_backoffice_generate()
    {
        Assert.Equal("admin.summary.generate", ToolAgentOrchestrator.NormalizeRouterIntentForTests("summary.store"));
        Assert.Equal("admin.summary.generate", ToolAgentOrchestrator.NormalizeRouterIntentForTests("refresh_summary"));
    }

    [Fact]
    public void Sanitize_tool_calls_drops_client_side_admin_summary_submit()
    {
        var calls = ToolAgentOrchestrator.SanitizeToolCallsForTests(new RouterPlan.ToolCall
        {
            Name = "admin.summary.submit",
            Args = ParseArgs("""{"docRef":"manual.pdf","summaryText":"client generated"}""")
        });

        Assert.Empty(calls);
    }

    [Fact]
    public void Infer_intent_uses_documents_search_as_inventory_find()
    {
        var toolCall = new RouterPlan.ToolCall
        {
            Name = "documents.search",
            Args = ParseArgs("""{"q":"mettler"}""")
        };

        Assert.Equal("inventory.find", ToolAgentOrchestrator.InferIntentFromToolCallsForTests(toolCall));
    }

    [Fact]
    public void Infer_intent_uses_documents_list_with_changedSince_as_inventory_changed_since()
    {
        var toolCall = new RouterPlan.ToolCall
        {
            Name = "documents.list",
            Args = ParseArgs("""{"changedSince":"2026-04-10T00:00:00Z"}""")
        };

        Assert.Equal("inventory.changed_since", ToolAgentOrchestrator.InferIntentFromToolCallsForTests(toolCall));
    }

    [Fact]
    public void Normalize_tool_args_preserves_categoryRef_and_changedSince_for_documents_list()
    {
        var normalized = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "documents.list",
            """{"categoryRef":"cat_003","changedSince":"2026-04-10T12:34:56Z","limit":20,"offset":5}""");

        Assert.Equal("cat_003", normalized.GetProperty("categoryRef").GetString());
        Assert.Equal("2026-04-10T12:34:56.0000000+00:00", normalized.GetProperty("changedSince").GetString());
        Assert.Equal(20, normalized.GetProperty("limit").GetInt32());
        Assert.Equal(5, normalized.GetProperty("offset").GetInt32());
    }

    [Fact]
    public void Normalize_tool_args_preserves_categoryRef_for_documents_search()
    {
        var normalized = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "documents.search",
            """{"q":"guidance","categoryRef":"cat_002"}""");

        Assert.Equal("guidance", normalized.GetProperty("q").GetString());
        Assert.Equal("cat_002", normalized.GetProperty("categoryRef").GetString());
    }

    [Fact]
    public void Normalize_admin_summary_submit_does_not_promote_ui_language_to_docLanguage()
    {
        var uiOnly = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "admin.summary.submit",
            """{"docRef":"manual.pdf","language":"fr","summaryText":"x"}""");
        var explicitDocLanguage = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "admin.summary.submit",
            """{"docRef":"manual.pdf","docLanguage":"de","language":"fr","summaryText":"x"}""");
        var arbitraryDocLanguage = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "admin.summary.submit",
            """{"docRef":"manual.pdf","docLanguage":"nl","language":"fr","summaryText":"x"}""");

        Assert.Equal("und", uiOnly.GetProperty("docLanguage").GetString());
        Assert.Equal("de", explicitDocLanguage.GetProperty("docLanguage").GetString());
        Assert.Equal("nl", arbitraryDocLanguage.GetProperty("docLanguage").GetString());
    }

    [Fact]
    public void Normalize_live_summary_keeps_response_language_separate_from_document_language()
    {
        var normalized = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "rag.summarize_live",
            """{"docRef":"manual.pdf","language":"fr","responseLanguage":"de","docLanguage":"nl-BE","maxWords":90}""");

        Assert.Equal("fr", normalized.GetProperty("language").GetString());
        Assert.Equal("de", normalized.GetProperty("responseLanguage").GetString());
        Assert.Equal("nl-be", normalized.GetProperty("docLanguage").GetString());
    }

    [Theory]
    [InlineData("""{"query":"compare documents"}""", "auto")]
    [InlineData("""{"query":"compare documents","mode":"strict"}""", "strict")]
    [InlineData("""{"query":"compare documents","mode":"focused"}""", "focused")]
    [InlineData("""{"query":"compare documents","mode":"broad"}""", "broad")]
    public void Normalize_tool_args_preserves_rag_mode_intent_for_backend_mapping(string args, string expectedMode)
    {
        var normalized = ToolAgentOrchestrator.NormalizeToolArgsForTests("rag.search", args);

        Assert.Equal(expectedMode, normalized.GetProperty("mode").GetString());
    }

    [Fact]
    public void Normalize_tool_args_preserves_rag_document_and_page_scope()
    {
        var search = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "rag.search",
            """
            {
              "query": "target section",
              "filters": {
                "docId": "doc-123",
                "docPath": "Knowledge/Guide.pdf",
                "maxPerDoc": 4,
                "maxPerPage": 2,
                "pageStart": 12,
                "pageEnd": 14
              }
            }
            """);
        var multi = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "rag.multi_search",
            """
            {
              "queries": ["target section", "target page"],
              "docId": "doc-123",
              "docPath": "Knowledge/Guide.pdf",
              "maxPerDoc": 4,
              "maxPerPage": 2,
              "pageStart": 12,
              "pageEnd": 14
            }
            """);

        Assert.Equal("doc-123", search.GetProperty("docId").GetString());
        Assert.Equal("Knowledge/Guide.pdf", search.GetProperty("docPath").GetString());
        Assert.Equal(4, search.GetProperty("maxPerDoc").GetInt32());
        Assert.Equal(2, search.GetProperty("maxPerPage").GetInt32());
        Assert.Equal(12, search.GetProperty("pageStart").GetInt32());
        Assert.Equal(14, search.GetProperty("pageEnd").GetInt32());

        Assert.Equal("doc-123", multi.GetProperty("docId").GetString());
        Assert.Equal("Knowledge/Guide.pdf", multi.GetProperty("docPath").GetString());
        Assert.Equal(4, multi.GetProperty("maxPerDoc").GetInt32());
        Assert.Equal(2, multi.GetProperty("maxPerPage").GetInt32());
        Assert.Equal(12, multi.GetProperty("pageStart").GetInt32());
        Assert.Equal(14, multi.GetProperty("pageEnd").GetInt32());
    }

    [Fact]
    public void Normalize_tool_args_preserves_rag_research_surface_flags()
    {
        var search = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "rag.search",
            """
            {
              "query": "broad planning",
              "filters": {
                "researchMode": "source_exploration",
                "includeResearchSurfaces": true
              },
              "mode": "broad"
            }
            """);
        var multi = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "rag.multi_search",
            """
            {
              "queries": ["broad planning", "source options"],
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true,
              "mode": "broad"
            }
            """);

        Assert.Equal("broad", search.GetProperty("mode").GetString());
        Assert.Equal("source_exploration", search.GetProperty("researchMode").GetString());
        Assert.True(search.GetProperty("includeResearchSurfaces").GetBoolean());

        Assert.Equal("broad", multi.GetProperty("mode").GetString());
        Assert.Equal("source_exploration", multi.GetProperty("researchMode").GetString());
        Assert.True(multi.GetProperty("includeResearchSurfaces").GetBoolean());
    }

    [Fact]
    public void Sanitize_tool_calls_caps_rag_calls_to_five()
    {
        var calls = Enumerable.Range(1, 7)
            .Select(i => new RouterPlan.ToolCall
            {
                Name = i % 2 == 0 ? "rag.multi_search" : "rag.search",
                Args = ParseArgs("""{"query":"test"}""")
            })
            .ToArray();

        var sanitized = ToolAgentOrchestrator.SanitizeToolCallsForTests(calls);

        Assert.Equal(5, sanitized.Length);
        Assert.All(sanitized, call => Assert.StartsWith("rag.", call.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Documentary_defaults_convert_planning_rag_search_to_multi_search()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.search",
                    Args = ParseArgs("""{"query":"batch cooking cuisson parallele","topK":8,"mode":"balanced"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Tu peux me faire une idee de batch cooking avec cuisson parallele ?");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        Assert.True(call.Args.TryGetProperty("queries", out var queries));
        Assert.True(queries.GetArrayLength() > 1);
        Assert.True(call.Args.GetProperty("topK").GetInt32() >= 8);
    }

    [Fact]
    public void Documentary_defaults_merge_router_multi_search_with_robust_planning_queries()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.multi_search",
                    Args = ParseArgs("""{"queries":["parallel cooking methods"],"topK":3,"mode":"balanced"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Tu peux me faire une idee de batch cooking avec cuisson parallele ?");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains(queries, q => q.Contains("batch cooking", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("parallel cooking methods", queries);
    }

    [Fact]
    public void Documentary_defaults_widen_structured_weekly_planning_retrieval()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.search",
                    Args = ParseArgs("""{"query":"plan de repas semaine","topK":4,"mode":"balanced"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Je cherche un plan de repas pour la semaine, petit-dejeuner, midi et soir du lundi au vendredi.");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        Assert.True(call.Args.GetProperty("topK").GetInt32() >= 8);
        Assert.True(call.Args.GetProperty("queries").GetArrayLength() > 1);
    }

    [Fact]
    public void Documentary_defaults_repair_router_search_fragment_and_drop_sentence_category()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "auto",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.search",
                    Args = ParseArgs("""{"query":"et reponds avec les sources","topK":8,"category":"s documents si le controle SLA est mentionne et reponds","mode":"auto"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Cherche dans les documents si le controle SLA est mentionne et reponds avec les sources.");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.search", call.Name);
        var query = call.Args.GetProperty("query").GetString() ?? string.Empty;
        Assert.Contains("controle SLA", query, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("et reponds avec les sources", query, StringComparer.OrdinalIgnoreCase);
        Assert.True(
            !call.Args.TryGetProperty("category", out var category)
            || category.ValueKind == JsonValueKind.Null
            || string.IsNullOrWhiteSpace(category.GetString()));
    }

    [Theory]
    [InlineData("bonjour")]
    [InlineData("Dis simplement bonjour.")]
    [InlineData("Merci.")]
    public void Documentary_defaults_respect_llm_general_without_tools(string input)
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "auto",
            Origin = RouterPlanOrigin.Llm
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(plan, input);

        Assert.Equal("chat.general", plan.Intent);
        Assert.Empty(plan.ToolCalls);
    }

    [Fact]
    public void Documentary_defaults_preserve_explicit_llm_rag_even_for_short_phrase()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "auto",
            Origin = RouterPlanOrigin.Llm,
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.multi_search",
                    Args = ParseArgs("""{"queries":["Dis simplement bonjour.","bonjour"],"topK":8,"mode":"balanced"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(plan, "Dis simplement bonjour.");

        Assert.Equal("rag.answer", plan.Intent);
        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
    }

    [Fact]
    public void Evidence_exploration_respects_llm_general_without_tools()
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "auto",
            Origin = RouterPlanOrigin.Llm
        };

        Assert.True(ToolAgentOrchestrator.ShouldRespectLlmRouterGeneralWithoutToolsForTests(plan));
    }

    [Fact]
    public void Evidence_exploration_does_not_skip_when_llm_rag_tool_is_explicit()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "auto",
            Origin = RouterPlanOrigin.Llm,
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.search",
                    Args = ParseArgs("""{"query":"bonjour","topK":8}""")
                }
            }
        };

        Assert.False(ToolAgentOrchestrator.ShouldRespectLlmRouterGeneralWithoutToolsForTests(plan));
    }

    [Fact]
    public void Documentary_defaults_do_not_rewrite_llm_general_documentary_request()
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "auto",
            Origin = RouterPlanOrigin.Llm
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Je veux des informations sur l'inertage.");

        Assert.Equal("chat.general", plan.Intent);
        Assert.Empty(plan.ToolCalls);
    }

    [Fact]
    public void Documentary_defaults_do_not_widen_fragmentary_planning_words_without_documentary_intent()
    {
        const string userMessage = "Hebdomadaire contraintes.";
        Assert.False(ToolAgentOrchestrator.LooksLikeSourceBackedPlanningRequestForTests(userMessage));

        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "strict"
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(plan, userMessage);

        Assert.Empty(plan.ToolCalls);
    }

    [Fact]
    public void Documentary_defaults_widen_documentary_planning_when_document_scope_is_explicit()
    {
        const string userMessage = "Planning hebdomadaire dans les documents : contraintes matin et soir.";

        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "strict"
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(plan, userMessage);

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        Assert.True(call.Args.GetProperty("topK").GetInt32() >= 8);
        Assert.True(call.Args.GetProperty("queries").GetArrayLength() > 1);
    }

    [Fact]
    public void Documentary_defaults_convert_broad_source_backed_menu_search_to_multi_search()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.search",
                    Args = ParseArgs("""{"query":"J'ai pas de four, propose un menu faisable a la poele ou au robot","topK":8,"mode":"balanced"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "J'ai pas de four, propose un menu faisable a la poele ou au robot.");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains(queries, q => q.Contains("four", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => q.Contains("robot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Documentary_defaults_use_focused_topk_for_soft_choice_recommendations()
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.multi_search",
                    Args = ParseArgs("""{"queries":["dessert"],"topK":12,"mode":"balanced"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Quel dessert fran\u00e7ais choisir pour un repas chic ?");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        Assert.Equal(8, call.Args.GetProperty("topK").GetInt32());
        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Equal("Quel dessert fran\u00e7ais choisir pour un repas chic ?", queries[0]);
    }

    [Fact]
    public void Documentary_defaults_redirect_oriented_document_lists_to_rag()
    {
        var plan = new RouterPlan
        {
            Intent = "inventory.list",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "documents.list",
                    Args = ParseArgs("""{"q":"VX-12","limit":20}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Quels documents sont utiles pour comprendre VX-12 et pourquoi ?");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.answer", plan.Intent);
        Assert.True(call.Name is "rag.search" or "rag.multi_search");
    }

    [Fact]
    public void Source_backed_action_queries_include_individual_user_terms_for_broad_recall()
    {
        var queries = ToolAgentOrchestrator.BuildSourceBackedActionRetrievalQueriesForTests(
            "Je veux cuisiner au Companion/Chefbot uniquement : menu complet.");

        Assert.Contains(queries, q => string.Equals(q, "companion", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => string.Equals(q, "chefbot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Source_backed_planning_does_not_turn_countdown_cooking_into_weekly_days()
    {
        Assert.False(ToolAgentOrchestrator.LooksLikeSourceBackedPlanningRequestForTests(
            "Prepare un planning de cuisson a rebours pour un repas a 19h."));
        Assert.True(ToolAgentOrchestrator.LooksLikeSourceBackedCountdownPlanningRequestForTests(
            "Prépare un planning de cuisson à rebours pour un repas à 19h."));
    }

    [Fact]
    public void Source_backed_planning_with_deictic_variation_is_not_treated_as_missing_previous_item()
    {
        var query = "Je cherche a avoir un plan de repas pour la semaine, tu me proposes quoi pour que ca varie un peu ?";

        Assert.True(ToolAgentOrchestrator.LooksLikeSourceBackedPlanningRequestForTests(query));
        Assert.False(ToolAgentOrchestrator.LooksLikeUnresolvedSourceBackedDeicticFollowupForTests(query));
    }

    [Fact]
    public void Exact_item_pre_router_shortcut_skips_structured_planning_requests()
    {
        Assert.True(ToolAgentOrchestrator.ShouldSkipExactItemPreRouterShortcutForTests(
            "Je cherche à avoir un plan de repas pour la semaine, petit-déjeuner, midi et soir du lundi au vendredi."));
    }

    [Fact]
    public void Exact_item_pre_router_shortcut_still_allows_precise_item_requests()
    {
        Assert.False(ToolAgentOrchestrator.ShouldSkipExactItemPreRouterShortcutForTests(
            "Donne-moi la recette du coq au vin avec les sources."));
    }

    [Fact]
    public void Complete_action_request_with_deictic_word_is_not_treated_as_missing_previous_item()
    {
        Assert.False(ToolAgentOrchestrator.LooksLikeUnresolvedSourceBackedDeicticFollowupForTests(
            "Prepare ca sous forme de planning avec les documents securite et les procedures NIST."));
    }

    [Fact]
    public void Pure_deictic_followup_still_asks_for_the_missing_item()
    {
        Assert.True(ToolAgentOrchestrator.LooksLikeUnresolvedSourceBackedDeicticFollowupForTests(
            "Mets ca pour 4 personnes."));
    }

    [Fact]
    public void Documentary_defaults_add_typo_tolerant_queries_for_requested_recipe_titles()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.search",
                    Args = ParseArgs("""{"query":"Tu as la recette du boeuf bourguingnon ?","topK":8,"mode":"balanced"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Tu as la recette du boeuf bourguingnon ?");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains(queries, q => q.Contains("bourguignon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Documentary_defaults_force_comparative_request_to_focused_multi_search()
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "strict"
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Compare la paella francaise/top 30 et celle du livre international.");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);

        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains(queries, q => q.Contains("paella", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => q.Contains("Compare la paella", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => string.Equals(q, "paella", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Comparative_retrieval_focus_removes_document_scope_noise_without_losing_original_query()
    {
        var queries = ToolAgentOrchestrator.BuildComparativeRetrievalQueriesForTests(
            "Compare la paella francaise/top 30 et celle du livre international.");

        Assert.Contains(queries, q => q.Contains("Compare la paella", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("paella", queries);
        Assert.Contains("paella details", queries);
        Assert.Contains("paella procedure", queries);
        Assert.DoesNotContain(queries, q => string.Equals(q, "francaise top livre international", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Documentary_defaults_anchor_router_comparative_subqueries_to_focused_subject()
    {
        using var args = JsonDocument.Parse("""{"queries":["top 30","livre international"],"topK":8,"mode":"balanced"}""");
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            ToolCalls = new List<RouterPlan.ToolCall>
            {
                new()
                {
                    Name = "rag.multi_search",
                    Args = args.RootElement.Clone()
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Compare la paella francaise/top 30 et celle du livre international.");

        var call = Assert.Single(plan.ToolCalls);
        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains(queries, q => string.Equals(q, "paella top 30", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => string.Equals(q, "paella livre international", StringComparison.OrdinalIgnoreCase));
        Assert.True(call.Args.GetProperty("topK").GetInt32() >= 12);
        Assert.DoesNotContain("top 30", queries);
        Assert.DoesNotContain("livre international", queries);
    }

    [Fact]
    public void Normalize_multi_search_expands_comparative_queries_with_focused_subject()
    {
        var normalized = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "rag.multi_search",
            """{"queries":["Compare la paella francaise/top 30 et celle du livre international."],"topK":4,"mode":"balanced"}""");

        var queries = normalized.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains(queries, q => q.Contains("Compare la paella", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("paella", queries);
    }

    [Theory]
    [InlineData("rag.search", """{"query":"pressure valve","categoryRef":"cat_003","topK":4,"mode":"balanced"}""")]
    [InlineData("rag.multi_search", """{"queries":["pressure valve"],"categoryRef":"cat_003","topK":4,"mode":"balanced"}""")]
    public void Normalize_rag_tools_preserves_category_ref_scope(string toolName, string jsonArgs)
    {
        var normalized = ToolAgentOrchestrator.NormalizeToolArgsForTests(toolName, jsonArgs);

        Assert.Equal("cat_003", normalized.GetProperty("categoryRef").GetString());
    }

    [Theory]
    [InlineData("Donne-moi la recette du fondant au chocolat en mode rapide.", "fondant au chocolat")]
    [InlineData("Donne-moi la recette du coq au vin dans le livre international.", "coq au vin")]
    [InlineData("Quelles vitesses/temp\u00e9ratures pour la sauce b\u00e9arnaise ?", "sauce b\u00e9arnaise")]
    [InlineData("Je fais un atelier enfant, donne-moi les pizzas rigolotes.", "pizzas rigolotes")]
    [InlineData("Donne-moi la charlotte aux p\u00eaches.", "charlotte aux p\u00eaches")]
    [InlineData("Je cherche la fondue au chocolat pour un groupe d'enfants.", "fondue au chocolat")]
    [InlineData("Il me faut la tartiflette, ingr\u00e9dients + \u00e9tapes en version claire.", "tartiflette")]
    [InlineData("Combien de temps et quels ingr\u00e9dients pour le gratin dauphinois ?", "gratin dauphinois")]
    [InlineData("Peux-tu me donner la recette de base des cr\u00eapes ?", "cr\u00eapes")]
    [InlineData("C'est quoi la cr\u00eape \u00e0 Jo ?", "cr\u00eape \u00e0 Jo")]
    [InlineData("Adapte la charlotte aux p\u00eaches pour 20 enfants.", "charlotte aux p\u00eaches")]
    [InlineData("Fondue chocolat pour un anniversaire de 18 enfants : organisation + quantit\u00e9s.", "Fondue chocolat")]
    [InlineData("Donne-moi la raclette suisse du corpus.", "raclette suisse")]
    [InlineData("Calcule les quantit\u00e9s pour 10 bols de velout\u00e9.", "velout\u00e9")]
    [InlineData("Je fais les pizzas rigolotes avec 15 enfants, aide-moi \u00e0 organiser les quantit\u00e9s et les postes.", "pizzas rigolotes")]
    [InlineData("Tu peux me faire une fiche claire pour \u00ab Churros sauce chocolat \u00bb : ingr\u00e9dients, \u00e9tapes, temps et source ?", "Churros sauce chocolat")]
    public void Documentary_defaults_extract_direct_item_title_from_natural_request(string userMessage, string expectedTitle)
    {
        Assert.Equal(expectedTitle, ToolAgentOrchestrator.TryExtractRequestedItemTitleForTests(userMessage));

        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "strict"
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(plan, userMessage);

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains(expectedTitle, queries);
        Assert.Contains($"\"{expectedTitle}\"", queries);
        Assert.Equal(20, call.Args.GetProperty("topK").GetInt32());
    }

    [Theory]
    [InlineData("Menu dessert autour du chocolat : 4 options du plus simple au plus gourmand.")]
    [InlineData("Quelles recettes avec des lentilles corail existent dans les PDF ?")]
    [InlineData("R\u00e9ponds en markdown avec une checklist pour cuisiner le velout\u00e9 lentilles coco.")]
    [InlineData("Une recette fran\u00e7aise classique.")]
    public void Documentary_content_requests_force_rag_even_when_router_stays_general(string userMessage)
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "strict"
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(plan, userMessage);

        var call = Assert.Single(plan.ToolCalls);
        Assert.StartsWith("rag.", call.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("rag.answer", plan.Intent);
    }

    [Fact]
    public void Documentary_planning_defaults_use_compact_initial_probe_queries()
    {
        const string userMessage = "Je cherche à avoir un plan de repas pour la semaine, petit-déjeuner, midi et soir du lundi au vendredi.";
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "strict"
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(plan, userMessage);

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
        var explorationQueries = ToolAgentOrchestrator.BuildPlanningExplorationRetrievalQueriesForTests(userMessage);

        Assert.True(explorationQueries.Length > 12);
        Assert.InRange(queries.Length, 1, 6);
        Assert.DoesNotContain(queries, query => string.Equals(query, "sommaire", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => string.Equals(query, "table des matieres", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => string.Equals(query, "index", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => query.Contains("catalogue", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => query.Contains("liste", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, query => ContainsAnyNormalizedToken(query, "petit", "dejeuner", "midi", "soir"));
        Assert.DoesNotContain(queries, query => ContainsAnyNormalizedToken(query, "collation", "gouter", "dessert"));
        Assert.DoesNotContain(queries, query => query.StartsWith("Je cherche", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => query.Contains("source", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => query.Contains("proposer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => query.Contains("partir", StringComparison.OrdinalIgnoreCase));
        Assert.All(queries, query => Assert.True(query.Length <= 90, $"Unexpectedly long initial probe query: {query}"));
        Assert.Equal(
            queries.Length,
            queries.Select(ToolAgentOrchestrator.NormalizeRagQueryForTests).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            queries.Length,
            queries
                .Select(ToolAgentOrchestrator.NormalizeInitialSourceBackedPlanningProbeFamilyKeyForTests)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(12, call.Args.GetProperty("topK").GetInt32());
        Assert.Equal("source_exploration", call.Args.GetProperty("researchMode").GetString());
        Assert.True(call.Args.GetProperty("includeResearchSurfaces").GetBoolean());

        static bool ContainsAnyNormalizedToken(string query, params string[] tokens)
        {
            var normalized = ToolAgentOrchestrator.NormalizeRagQueryForTests(query);
            return tokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Documentary_planning_normalization_compacts_noisy_router_exploration_queries()
    {
        const string userMessage = "Je cherche à avoir un plan de repas pour la semaine, petit-déjeuner, midi et soir du lundi au vendredi.";
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.multi_search",
                    Args = ParseArgs("""{"queries":["sommaire","table des matieres","index","sections principales","contents","table of contents","sections","indice","index repas","repas index","liste repas","repas liste"],"topK":20,"mode":"broad","researchMode":"source_exploration","includeResearchSurfaces":true}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(plan, userMessage);

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
        var explorationQueries = ToolAgentOrchestrator.BuildPlanningExplorationRetrievalQueriesForTests(userMessage);

        Assert.True(explorationQueries.Length > 12);
        Assert.InRange(queries.Length, 1, 6);
        Assert.DoesNotContain(queries, query => string.Equals(query, "sommaire", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => string.Equals(query, "table des matieres", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => string.Equals(query, "index", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => query.Contains("catalogue", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => query.Contains("liste", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, query => ContainsAnyNormalizedToken(query, "petit", "dejeuner", "midi", "soir"));
        Assert.DoesNotContain(queries, query => ContainsAnyNormalizedToken(query, "collation", "gouter", "dessert"));
        Assert.DoesNotContain(queries, query => query.StartsWith("Je cherche", StringComparison.OrdinalIgnoreCase));
        Assert.All(queries, query => Assert.True(query.Length <= 90, $"Unexpectedly long initial probe query: {query}"));
        Assert.Equal(
            queries.Length,
            queries.Select(ToolAgentOrchestrator.NormalizeRagQueryForTests).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(12, call.Args.GetProperty("topK").GetInt32());
        Assert.Equal("broad", call.Args.GetProperty("mode").GetString());
        Assert.Equal("source_exploration", call.Args.GetProperty("researchMode").GetString());
        Assert.True(call.Args.GetProperty("includeResearchSurfaces").GetBoolean());

        static bool ContainsAnyNormalizedToken(string query, params string[] tokens)
        {
            var normalized = ToolAgentOrchestrator.NormalizeRagQueryForTests(query);
            return tokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Vague_weekly_meal_plan_initial_probes_avoid_filler_and_navigation_terms()
    {
        const string userMessage = "Je ne sais pas quoi faire pour les repas de cette semaine, tu peux me proposer un plan de repas pour la semaine a partir des documents disponibles, avec les sources utiles ?";

        var queries = ToolAgentOrchestrator.BuildInitialSourceBackedPlanningProbeQueriesForTests(userMessage);

        Assert.InRange(queries.Length, 1, 6);
        Assert.Contains(queries, query => query.Contains("repas", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => ContainsProbeToken(query, "sais"));
        Assert.DoesNotContain(queries, query => ContainsProbeToken(query, "pas"));
        Assert.DoesNotContain(queries, query => ContainsProbeToken(query, "table"));
        Assert.DoesNotContain(queries, query => ContainsProbeToken(query, "matieres"));
        Assert.DoesNotContain(queries, query => ContainsProbeToken(query, "liste"));
        Assert.DoesNotContain(queries, query => ContainsProbeToken(query, "catalogue"));
        Assert.DoesNotContain(queries, query => ContainsProbeToken(query, "elements"));
        Assert.DoesNotContain(queries, query => ContainsProbeToken(query, "candidats"));
        Assert.All(queries, query => Assert.True(query.Length <= 90, $"Unexpectedly long initial probe query: {query}"));

        static bool ContainsProbeToken(string query, string token)
            => ToolAgentOrchestrator.NormalizeRagQueryForTests(query)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(token, StringComparer.Ordinal);
    }

    [Fact]
    public void Detailed_weekly_meal_plan_initial_probe_keeps_user_supplied_content_kind_and_axes()
    {
        const string userMessage = "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi en y mettant petit-dejeuner, diner, souper et collation. Utilise uniquement les recettes et sources disponibles, evite les doublons de sources inutiles et donne un format clair, user-friendly.";

        var queries = ToolAgentOrchestrator.BuildInitialSourceBackedPlanningProbeQueriesForTests(userMessage);
        var intentQuery = Assert.Single(queries, query =>
        {
            var normalized = ToolAgentOrchestrator.NormalizeRagQueryForTests(query);
            return normalized.Contains("recettes", StringComparison.Ordinal)
                && normalized.Contains("repas", StringComparison.Ordinal)
                && normalized.Contains("lundi", StringComparison.Ordinal)
                && normalized.Contains("vendredi", StringComparison.Ordinal)
                && normalized.Contains("petit", StringComparison.Ordinal)
                && normalized.Contains("dejeuner", StringComparison.Ordinal)
                && normalized.Contains("diner", StringComparison.Ordinal)
                && normalized.Contains("souper", StringComparison.Ordinal)
                && normalized.Contains("collation", StringComparison.Ordinal);
        });

        Assert.DoesNotContain("doublons", intentQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("friendly", intentQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("format", intentQuery, StringComparison.OrdinalIgnoreCase);
        Assert.True(intentQuery.Length <= 90, $"Unexpectedly long initial probe query: {intentQuery}");
    }

    [Fact]
    public void Detailed_weekly_meal_plan_exploration_covers_each_requested_meal_slot_before_query_budget_cutoff()
    {
        const string userMessage = "J'ai besoin que tu me asses un plan de repas pour la semaine du lundi au vrendredi en y mettant petit-dejeuner, diner, souper et gouter / collation, avec les sources utiles.";

        var primaryQueries = ToolAgentOrchestrator.BuildPlanningRetrievalQueriesForTests(userMessage);
        var explorationQueries = ToolAgentOrchestrator.BuildPlanningExplorationRetrievalQueriesForTests(userMessage);
        var firstBudgetedExplorationQueries = explorationQueries.Take(12).ToArray();

        Assert.DoesNotContain(primaryQueries, query => ContainsNormalizedToken(query, "besoin"));
        Assert.DoesNotContain(primaryQueries, query => ContainsNormalizedToken(query, "asses"));
        Assert.DoesNotContain(primaryQueries, query => ContainsNormalizedToken(query, "utiles"));
        Assert.DoesNotContain(primaryQueries, query => ContainsNormalizedToken(query, "sources"));
        Assert.Contains(firstBudgetedExplorationQueries, query => ContainsNormalizedToken(query, "petit") && ContainsNormalizedToken(query, "dejeuner"));
        Assert.Contains(firstBudgetedExplorationQueries, query => ContainsNormalizedToken(query, "diner"));
        Assert.Contains(firstBudgetedExplorationQueries, query => ContainsNormalizedToken(query, "souper"));
        Assert.Contains(firstBudgetedExplorationQueries, query => ContainsNormalizedToken(query, "collation"));
        Assert.Contains(firstBudgetedExplorationQueries, query => ContainsNormalizedToken(query, "gouter"));
        Assert.DoesNotContain(firstBudgetedExplorationQueries, query => ContainsNormalizedToken(query, "dessert"));
        Assert.DoesNotContain(firstBudgetedExplorationQueries, query => string.Equals(query, "sommaire", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(firstBudgetedExplorationQueries, query => string.Equals(query, "index", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            firstBudgetedExplorationQueries,
            query => ContainsNormalizedToken(query, "options") || ContainsNormalizedToken(query, "candidats"));

        static bool ContainsNormalizedToken(string query, string token)
            => ToolAgentOrchestrator.NormalizeRagQueryForTests(query)
                .Contains(token, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentary_defaults_clear_router_clarification_when_request_has_precise_source_target()
    {
        var plan = new RouterPlan
        {
            Intent = "clarification",
            Language = "fr",
            Mode = "strict",
            NeedClarification = true,
            ClarificationQuestions = new() { "Quel document ?" }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Combien de temps et quels ingr\u00e9dients pour le gratin dauphinois ?");

        Assert.False(plan.NeedClarification);
        Assert.Empty(plan.ClarificationQuestions);
        Assert.Equal("rag.answer", plan.Intent);
        Assert.Contains(plan.ToolCalls, call => string.Equals(call.Name, "rag.multi_search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Documentary_defaults_do_not_treat_deictic_followup_as_exact_item_title()
    {
        Assert.Null(ToolAgentOrchestrator.TryExtractRequestedItemTitleForTests(
            "Après une recette, l'utilisateur dit : 'mets-moi ça pour 2 personnes'."));
    }

    [Fact]
    public void Documentary_defaults_treat_wanted_named_item_as_precise_lookup()
    {
        const string userMessage = "Je veux la paella, mais je sais plus dans quel livre elle est.";
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "strict"
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(plan, userMessage);

        Assert.Equal("paella", ToolAgentOrchestrator.TryExtractRequestedItemTitleForTests(userMessage));
        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        Assert.Equal(20, call.Args.GetProperty("topK").GetInt32());

        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains("paella", queries, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(queries, q => q.Contains("je sais plus", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Documentary_defaults_widen_exact_item_card_multi_search_to_keep_linked_context()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.multi_search",
                    Args = ParseArgs("""{"queries":["recette"],"topK":2,"mode":"balanced"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Tu peux me faire la recette des churros sauce chocolat avec ingredients et etapes ?");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        Assert.Equal(20, call.Args.GetProperty("topK").GetInt32());

        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.InRange(queries.Length, 2, 8);
        Assert.Contains(queries, q => q.Contains("churros sauce chocolat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Documentary_defaults_force_ranking_request_to_focused_multi_search()
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "strict"
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Quel dessert est le plus technique ?");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);

        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains(queries, q => q.Contains("Quel dessert", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("dessert technique", queries);
    }

    [Fact]
    public void Comparative_documentary_requests_count_separate_explicit_files()
    {
        const string query = "Compare NFPA 79 2024 Electrical Standard for Industrial Machinery.pdf et UL 508A 2018 Industrial Control Panels - Scan.pdf sur machine industrielle vs panneaux industriels.";

        Assert.True(ToolAgentOrchestrator.LooksLikeComparativeDocumentaryRequestForTests(query));
        Assert.Equal(2, ToolAgentOrchestrator.CountExplicitDocumentFileReferencesForTests(query));

        var queries = ToolAgentOrchestrator.BuildComparativeRetrievalQueriesForTests(query);
        Assert.Contains(queries, q => q.Contains("NFPA 79 2024 Electrical Standard for Industrial Machinery.pdf", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => q.Contains("UL 508A 2018 Industrial Control Panels - Scan.pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Documentary_defaults_convert_source_adaptation_request_to_focused_multi_search()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.search",
                    Args = ParseArgs("""{"query":"Comment alleger les desserts chocolates en sucre ? Dis bien ce qui vient des PDF et ce qui est adaptation.","topK":8,"mode":"balanced"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Comment alleger les desserts chocolates en sucre ? Dis bien ce qui vient des PDF et ce qui est adaptation.");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);

        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains(queries, q => q.Contains("dessert", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => q.Contains("chocolat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => q.Contains("sucre", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Documentary_defaults_convert_version_traceability_search_to_multi_search()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "rag.search",
                    Args = ParseArgs("""{"query":"comment signaler qu'il existe un corrigendum","topK":5,"mode":"balanced"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Comment signaler qu'il existe un corrigendum pour ce document sans confondre les versions ?");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        Assert.True(call.Args.GetProperty("topK").GetInt32() >= 12);

        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains(queries, q => q.Contains("corrigendum", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => q.Contains("versions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Documentary_defaults_replace_summary_plan_for_source_adaptation_request()
    {
        var plan = new RouterPlan
        {
            Intent = "rag.summarize_doc",
            Language = "fr",
            Mode = "strict",
            ToolCalls = new()
            {
                new RouterPlan.ToolCall
                {
                    Name = "summary.flow",
                    Args = ParseArgs("""{"docRef":"desserts"}""")
                }
            }
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Comment alleger les desserts chocolates en sucre ? Dis bien ce qui vient des PDF et ce qui est adaptation.");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        Assert.Equal("rag.answer", plan.Intent);

        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Assert.Contains(queries, q => q.Contains("chocolat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, q => q.Contains("sucre", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ranking_documentary_request_overrides_premature_router_clarification()
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            NeedClarification = true,
            ClarificationQuestions = new() { "Tu veux comparer comment ?" }
        };

        ToolAgentOrchestrator.ApplySourceBackedClarificationOverrideForTests(
            plan,
            "Quel dessert est le plus technique ?");

        Assert.False(plan.NeedClarification);
        Assert.Empty(plan.ClarificationQuestions);
        Assert.Equal("rag.answer", plan.Intent);
    }

    [Fact]
    public void Exact_item_card_request_overrides_premature_router_clarification()
    {
        var plan = new RouterPlan
        {
            Intent = "clarification",
            Language = "fr",
            NeedClarification = true,
            ClarificationQuestions = new() { "Quel document ?" }
        };

        ToolAgentOrchestrator.ApplySourceBackedClarificationOverrideForTests(
            plan,
            "Tu peux me faire une fiche claire pour \u00ab Churros sauce chocolat \u00bb : ingr\u00e9dients, \u00e9tapes, temps et source ?");

        Assert.False(plan.NeedClarification);
        Assert.Empty(plan.ClarificationQuestions);
        Assert.Equal("rag.answer", plan.Intent);
    }

    [Theory]
    [InlineData("Compare la paella francaise/top 30 et celle du livre international.")]
    [InlineData("Quel dessert est le plus technique ?")]
    public void Documentary_probe_does_not_preempt_comparative_rag_requests(string query)
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "strict"
        };

        Assert.False(ToolAgentOrchestrator.ShouldRunDocumentaryProbeForTests(query, plan));
    }

    [Fact]
    public void Source_backed_action_request_overrides_premature_router_clarification()
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            NeedClarification = true,
            ClarificationQuestions = new() { "Quel type de recette cherchez-vous ?" }
        };

        ToolAgentOrchestrator.ApplySourceBackedClarificationOverrideForTests(
            plan,
            "Tu peux me faire une idee de batch cooking avec cuisson parallele ?");

        Assert.False(plan.NeedClarification);
        Assert.Empty(plan.ClarificationQuestions);
        Assert.Equal("rag.answer", plan.Intent);
    }

    [Fact]
    public void Broad_documentary_information_request_overrides_premature_router_clarification()
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            NeedClarification = true,
            ClarificationQuestions = new() { "Quel document voulez-vous utiliser ?" }
        };

        ToolAgentOrchestrator.ApplySourceBackedClarificationOverrideForTests(
            plan,
            "Je veux des informations sur l'inertage.");

        Assert.False(plan.NeedClarification);
        Assert.Empty(plan.ClarificationQuestions);
        Assert.Equal("rag.answer", plan.Intent);
    }

    [Fact]
    public void Broad_documentary_information_request_gets_expanded_rag_defaults()
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr"
        };

        ToolAgentOrchestrator.ApplyDocumentaryRagDefaultsForTests(
            plan,
            "Je veux des informations sur l'inertage.");

        var call = Assert.Single(plan.ToolCalls);
        Assert.Equal("rag.multi_search", call.Name);
        var queries = call.Args.GetProperty("queries").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
        Assert.Contains(queries, q => q.Contains("inertage", StringComparison.OrdinalIgnoreCase));
        Assert.True(call.Args.GetProperty("topK").GetInt32() >= 12);
    }

    [Theory]
    [InlineData("Passe en mode strict", "strict")]
    [InlineData("mets le mode standard", "standard")]
    [InlineData("mode auto", "auto")]
    public void Localized_strings_detect_mode_change(string text, string expectedMode)
    {
        var detected = LocalizedStrings.TryDetectModePreferenceChange(text, out var mode);

        Assert.True(detected);
        Assert.Equal(expectedMode, mode);
    }

    private static JsonElement ParseArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
