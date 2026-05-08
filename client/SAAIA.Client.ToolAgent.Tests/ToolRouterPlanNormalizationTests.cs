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
