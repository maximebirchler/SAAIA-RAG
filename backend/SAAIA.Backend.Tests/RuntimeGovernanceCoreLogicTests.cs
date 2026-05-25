using SAAIA.Backend;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RuntimeGovernanceCoreLogicTests
{
    [Fact]
    public async Task RuntimeLlmCapacityPlanService_reads_capacity_plan_from_configured_path()
    {
        var previousPath = Environment.GetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH");
        var tempRoot = Path.Combine(Path.GetTempPath(), "saaia-llm-capacity-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempRoot);
            var planPath = Path.Combine(tempRoot, "llm.capacity-plan.json");
            await File.WriteAllTextAsync(planPath, """
            {
              "version": "1.0",
              "plannedAt": "2026-04-29T12:00:00Z",
              "licenseSeats": 25,
              "profile": "small-server",
              "repo": "bartowski/Qwen2.5-3B-Instruct-GGUF",
              "file": "Qwen2.5-3B-Instruct-Q4_K_M.gguf",
              "modelId": "qwen2.5-3b-instruct-q4-k-m",
              "placement": "server",
              "instances": 1,
              "slotsPerInstance": 3,
              "totalSlots": 3,
              "queueLimit": 40,
              "perUserActiveLimit": 1,
              "perUserQueuedLimit": 2,
              "reason": "test plan",
              "hardware": {
                "cpuCount": 8,
                "totalRamMiB": 32768,
                "gpuName": "RTX test",
                "gpuVramMiB": 8192
              },
              "llamaArgs": {
                "ctxSize": 8192,
                "batchSize": 512,
                "uBatchSize": 128,
                "nGpuLayers": "all"
              }
            }
            """);
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", planPath);

            var service = new RuntimeLlmCapacityPlanService(
                new StubHostEnvironment { ContentRootPath = tempRoot },
                Options.Create(new LicenseOptions { Seats = 25 }));
            var response = await service.GetCapacityAsync(new RuntimeLlmQueueManager(), CancellationToken.None);

            Assert.Equal("ok", response.Status);
            Assert.Equal(25, response.CurrentLicenseSeats);
            Assert.True(response.PlanMatchesLicense);
            Assert.False(response.ReplanRequired);
            Assert.Equal(planPath, response.Path);
            Assert.NotNull(response.Plan);
            Assert.Equal(25, response.Plan!.LicenseSeats);
            Assert.Equal("Qwen2.5-3B-Instruct-Q4_K_M.gguf", response.Plan.ModelFile);
            Assert.Equal(3, response.Queue.TotalSlots);
            Assert.Equal(40, response.Queue.QueueLimit);
            Assert.Equal(3, response.Queue.AvailableSlots);
            Assert.Empty(response.Recommendations);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", previousPath);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeLlmCapacityPlanService_reports_missing_plan_without_throwing()
    {
        var previousPath = Environment.GetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH");
        var tempRoot = Path.Combine(Path.GetTempPath(), "saaia-llm-capacity-missing-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempRoot);
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", null);

            var service = new RuntimeLlmCapacityPlanService(new StubHostEnvironment { ContentRootPath = tempRoot });
            var response = await service.GetCapacityAsync(new RuntimeLlmQueueManager(), CancellationToken.None);

            Assert.Equal("missing", response.Status);
            Assert.True(response.ReplanRequired);
            Assert.False(response.PlanMatchesLicense);
            Assert.Null(response.Plan);
            Assert.Equal(1, response.Queue.TotalSlots);
            Assert.Equal(10, response.Queue.QueueLimit);
            Assert.Contains(response.Recommendations, item => item.Contains("-AutoPlan", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", previousPath);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeLlmCapacityPlanService_marks_plan_stale_when_license_seats_change()
    {
        var previousPath = Environment.GetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH");
        var tempRoot = Path.Combine(Path.GetTempPath(), "saaia-llm-capacity-stale-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempRoot);
            var planPath = Path.Combine(tempRoot, "llm.capacity-plan.json");
            await File.WriteAllTextAsync(planPath, """
            {
              "version": "v3.1-server-capacity",
              "plannedAt": "2026-04-29T12:00:00Z",
              "licenseSeats": 10,
              "modelId": "qwen2.5-3b-instruct-q4-k-m",
              "repo": "bartowski/Qwen2.5-3B-Instruct-GGUF",
              "file": "Qwen2.5-3B-Instruct-Q4_K_M.gguf",
              "profile": "server-low-capacity",
              "instances": 1,
              "slotsPerInstance": 2,
              "totalSlots": 2,
              "queueLimit": 20,
              "perUserActiveLimit": 1,
              "perUserQueuedLimit": 1
            }
            """);
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", planPath);

            var service = new RuntimeLlmCapacityPlanService(
                new StubHostEnvironment { ContentRootPath = tempRoot },
                Options.Create(new LicenseOptions { Seats = 25 }));
            var response = await service.GetCapacityAsync(new RuntimeLlmQueueManager(), CancellationToken.None);

            Assert.Equal("ok", response.Status);
            Assert.Equal(25, response.CurrentLicenseSeats);
            Assert.NotNull(response.Plan);
            Assert.Equal(10, response.Plan!.LicenseSeats);
            Assert.False(response.PlanMatchesLicense);
            Assert.True(response.ReplanRequired);
            Assert.Contains(response.Recommendations, item => item.Contains("changed from 10 to 25", StringComparison.OrdinalIgnoreCase));

            var queuePlan = await service.GetQueuePlanAsync(CancellationToken.None);
            Assert.Null(queuePlan);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", previousPath);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void RuntimeLlmQueueManager_enforces_slot_and_per_user_limits()
    {
        var manager = new RuntimeLlmQueueManager();
        var plan = new AdminRuntimeLlmCapacityPlanDto(
            Version: "1.0",
            GeneratedAt: null,
            LicenseSeats: 100,
            Profile: "test",
            ModelRepo: null,
            ModelFile: null,
            ModelLabel: null,
            Placement: "server",
            Instances: 1,
            SlotsPerInstance: 2,
            TotalSlots: 2,
            QueueLimit: 1,
            PerUserActiveLimit: 1,
            PerUserQueuedLimit: 1,
            Notes: null,
            Hardware: null,
            LlamaArgs: null);

        using var userA = manager.TryAcquire("user-a", plan);
        Assert.NotNull(userA);
        Assert.Null(manager.TryAcquire("user-a", plan));

        using var userB = manager.TryAcquire("user-b", plan);
        Assert.NotNull(userB);
        Assert.Null(manager.TryAcquire("user-c", plan));

        Assert.True(manager.TryRegisterQueued("user-c", plan));
        Assert.False(manager.TryRegisterQueued("user-d", plan));
        manager.ReleaseQueued("user-c");

        var snapshot = manager.GetSnapshot(plan);
        Assert.Equal(2, snapshot.Active);
        Assert.Equal(0, snapshot.AvailableSlots);
        Assert.Equal(0, snapshot.Queued);
    }

    [Fact]
    public async Task RuntimeLlmCapacityPlanService_caps_multi_instance_plan_without_router()
    {
        var previousPath = Environment.GetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH");
        var previousRouter = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_MULTI_INSTANCE_ROUTER_ENABLED");
        var tempRoot = Path.Combine(Path.GetTempPath(), "saaia-llm-capacity-router-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempRoot);
            var planPath = Path.Combine(tempRoot, "llm.capacity-plan.json");
            await File.WriteAllTextAsync(planPath, """
            {
              "version": "v3.1-server-capacity",
              "plannedAt": "2026-04-29T12:00:00Z",
              "licenseSeats": 25,
              "modelId": "qwen-server-test",
              "repo": "test/repo",
              "file": "test.gguf",
              "profile": "server-multi",
              "instances": 3,
              "slotsPerInstance": 2,
              "totalSlots": 6,
              "queueLimit": 20,
              "perUserActiveLimit": 2,
              "perUserQueuedLimit": 2
            }
            """);
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", planPath);
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_MULTI_INSTANCE_ROUTER_ENABLED", null);

            var service = new RuntimeLlmCapacityPlanService(
                new StubHostEnvironment { ContentRootPath = tempRoot },
                Options.Create(new LicenseOptions { Seats = 25 }));
            var response = await service.GetCapacityAsync(new RuntimeLlmQueueManager(), CancellationToken.None);

            Assert.Equal("ok", response.Status);
            Assert.Equal(3, response.Plan!.Instances);
            Assert.Equal(6, response.Plan.TotalSlots);
            Assert.Equal(2, response.Queue.TotalSlots);
            Assert.Equal(2, response.Queue.AvailableSlots);
            Assert.Contains(response.Recommendations, item => item.Contains("single Chat:LlmBaseUrl", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", previousPath);
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_MULTI_INSTANCE_ROUTER_ENABLED", previousRouter);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_keeps_query_terms_without_domain_translation()
    {
        var terms = RagEndpoints.BuildLexicalContentFallbackTerms("Je veux les documents qui parlent d'inertage");

        Assert.Contains("inertage", terms);
        Assert.DoesNotContain("inerting", terms);
        Assert.DoesNotContain("inert", terms);
        Assert.DoesNotContain("documents", terms);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_bridges_generic_surface_treatment_terms()
    {
        var terms = RagEndpoints.BuildLexicalContentFallbackTerms(
            "Retrouve les documents qui parlent de poudrage et donne les passages utiles.");

        Assert.Contains("poudrage", terms);
        Assert.Contains("powder", terms);
        Assert.Contains("powder coating", terms);
        Assert.Contains("powder coatings", terms);
        Assert.DoesNotContain("documents", terms);
    }

    [Fact]
    public void ExpandRetrievalQuery_bridges_surface_treatment_terms_without_category_dependency()
    {
        var expanded = RagEndpoints.ExpandRetrievalQuery("poudrage", "construction");
        var expandedOtherCategory = RagEndpoints.ExpandRetrievalQuery("poudrage", "catalogue commercial");

        Assert.Contains("powder coating", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expanded, expandedOtherCategory);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_extracts_generic_singular_phrases()
    {
        var terms = RagEndpoints.BuildLexicalContentFallbackTerms(
            "Compare les deux rapports techniques du corpus : differences de methode et style.");

        Assert.Contains("rapport", terms);
        Assert.Contains("technique", terms);
        Assert.Contains("rapport technique", terms);
        Assert.DoesNotContain("methode", terms);
        Assert.DoesNotContain("corpus", terms);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_folds_accents_and_common_ligatures()
    {
        var terms = RagEndpoints.BuildLexicalContentFallbackTerms(
            "Tu peux me faire une fiche claire pour « Bœuf à l'aïoli épicé » ?");

        Assert.Contains("boeuf", terms);
        Assert.Contains("bœuf", terms);
        Assert.Contains("aioli", terms);
        Assert.Contains("aïoli", terms);
        Assert.Contains("epice", terms);
        Assert.Contains("boeuf aioli", terms);
        Assert.Contains("bœuf aïoli", terms);

        var accentTerms = RagEndpoints.BuildLexicalContentFallbackTerms(
            "Sauce bearnaise, bechamel, eclairs et entrecote");
        Assert.Contains("bearnaise", accentTerms);
        Assert.Contains("bechamel", accentTerms);
        Assert.Contains("eclairs", accentTerms);
        Assert.Contains("entrecote", accentTerms);
        Assert.DoesNotContain("steak", accentTerms);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_keeps_adjacent_word_number_document_cues()
    {
        var terms = RagEndpoints.BuildLexicalContentFallbackTerms(
            "Compare la paella francaise/top 30 et celle du livre international.");

        Assert.Contains("paella", terms);
        Assert.Contains("top 30", terms);
        Assert.DoesNotContain("30", terms);
        Assert.DoesNotContain("30 et", terms);

        var technicalTerms = RagEndpoints.BuildLexicalContentFallbackTerms(
            "Compare ISO 27001 avec API v2 dans les notes de migration.");

        Assert.Contains("iso 27001", technicalTerms);
        Assert.Contains("api v2", technicalTerms);
        Assert.DoesNotContain("27001", technicalTerms);
    }

    [Fact]
    public void ExpandRetrievalQuery_uses_query_terms_not_category_name()
    {
        var query = "Quelle sauce avec une entrec\u00f4te ?";
        var expanded = RagEndpoints.ExpandRetrievalQuery(query, "cuisine");
        var expandedOtherCategory = RagEndpoints.ExpandRetrievalQuery(query, "atex");

        Assert.Contains("entrecote", expanded);
        Assert.DoesNotContain("steak", expanded);
        Assert.DoesNotContain("rumsteck", expanded);
        Assert.DoesNotContain("viande rouge", expanded);
        Assert.DoesNotContain("sauces trempettes", expanded);
        Assert.Equal(expanded, expandedOtherCategory);

        var unchanged = RagEndpoints.ExpandRetrievalQuery("procedure onboarding fournisseur", "hr");
        Assert.Equal("procedure onboarding fournisseur", unchanged);
    }

    [Fact]
    public void ShouldSupplementSparseWithLexicalFallback_uses_generic_query_signal()
    {
        Assert.True(RagEndpoints.ShouldSupplementSparseWithLexicalFallback("cuisine", "activité cuisine avec des enfants"));
        Assert.True(RagEndpoints.ShouldSupplementSparseWithLexicalFallback("atex", "quelle sauce avec une entrecôte"));
        Assert.True(RagEndpoints.ShouldSupplementSparseWithLexicalFallback("hr", "procedure onboarding fournisseur"));
        Assert.True(RagEndpoints.ShouldSupplementSparseWithLexicalFallback(null, "manual ABC-123 pressure valve"));
        Assert.False(RagEndpoints.ShouldSupplementSparseWithLexicalFallback(null, "ok merci"));
    }

    [Fact]
    public void ResolveEffectiveSearchMode_infers_broad_for_comparative_intent_without_domain_hardcoding()
    {
        Assert.Equal("broad", RagEndpoints.ResolveEffectiveSearchMode(null, "Compare trois procedures et dis laquelle choisir."));
        Assert.Equal("broad", RagEndpoints.ResolveEffectiveSearchMode(null, "Quel document est le plus technique ?"));
        Assert.Equal("broad", RagEndpoints.ResolveEffectiveSearchMode(null, "Quels documents sont disponibles dans la base ?"));
        Assert.Equal("broad", RagEndpoints.ResolveEffectiveSearchMode(null, "Tu peux me faire une vue d'ensemble des documents par grands themes ?"));
        Assert.Equal("broad", RagEndpoints.ResolveEffectiveSearchMode(null, "Which manuals are available in the knowledge base?"));
        Assert.Equal("broad", RagEndpoints.ResolveEffectiveSearchMode("", "Which report is the most relevant?"));
        Assert.Equal("focused", RagEndpoints.ResolveEffectiveSearchMode("focused", "Compare les options."));
        Assert.Equal("balanced", RagEndpoints.ResolveEffectiveSearchMode(null, "Donne-moi la procedure d'installation."));
    }

    [Fact]
    public void Comparative_document_diversity_uses_wider_candidates_and_one_chunk_per_doc()
    {
        Assert.True(RagEndpoints.ShouldPreferComparativeDocumentDiversity("Compare les options disponibles."));
        Assert.True(RagEndpoints.ShouldPreferComparativeDocumentDiversity("Qual documento e o mais tecnico?"));
        Assert.True(RagEndpoints.ShouldPreferDocumentDiversity("Quels livres vois-tu dans cette base documentaire ?"));
        Assert.True(RagEndpoints.ShouldPreferDocumentDiversity("Fammi una panoramica dei documenti disponibili."));
        Assert.False(RagEndpoints.ShouldPreferDocumentDiversity("Donne-moi la procedure d'installation."));
        Assert.True(RagEndpoints.ShouldSkipChunkRetrieversForDocumentOverview("Quels documents sont disponibles dans la base ?", hasDocScope: false, mode: "balanced"));
        Assert.False(RagEndpoints.ShouldSkipChunkRetrieversForDocumentOverview("Quels documents sont disponibles dans la base ?", hasDocScope: true, mode: "balanced"));
        Assert.False(RagEndpoints.ShouldSkipChunkRetrieversForDocumentOverview("Quels documents sont disponibles dans la base ?", hasDocScope: false, mode: "focused"));
        Assert.True(RagEndpoints.ShouldRequireDocumentOverviewProfileMatch("Quels documents parlent d'inertage ?"));
        Assert.True(RagEndpoints.ShouldRequireDocumentOverviewProfileMatch("Which documents mention nitrogen blanketing?"));
        Assert.False(RagEndpoints.ShouldRequireDocumentOverviewProfileMatch("Quels documents sont disponibles dans la base ?"));
        Assert.False(RagEndpoints.ShouldRequireDocumentOverviewProfileMatch("Quels livres de recettes tu vois dans la base et a quoi ils servent chacun ?"));
        Assert.Equal(120, RagEndpoints.ResolveDefaultCandidateCount("broad", preferComparativeDiversity: true, topK: 8));
        Assert.Equal(96, RagEndpoints.ResolveDefaultCandidateCount("broad", preferComparativeDiversity: false, topK: 8));
        Assert.Equal(1, RagEndpoints.ResolveDefaultMaxPerDoc("broad", preferComparativeDiversity: true, topK: 8));
        Assert.Equal(2, RagEndpoints.ResolveDefaultMaxPerDoc("broad", preferComparativeDiversity: false, topK: 8));
    }

    [Fact]
    public void Document_level_selection_keeps_profile_matches_in_ranked_order_for_overview_queries()
    {
        var chunk = CreateRetrievalMatch("chunk", "Knowledge/manual.pdf", "unit_exact_v1", 0.91);
        var profile = CreateRetrievalMatch("profile", "Knowledge/manual.pdf", "document_profile", 0.86);
        var ranked = new[] { profile, chunk };

        Assert.Equal(["chunk", "profile"], RagEndpoints.OrderMatchesForSelection(ranked, prioritizeDocumentProfiles: false).Select(static match => match.ChunkId));
        Assert.Equal(["profile", "chunk"], RagEndpoints.OrderMatchesForSelection(ranked, prioritizeDocumentProfiles: true).Select(static match => match.ChunkId));
    }

    [Fact]
    public void Document_profile_matches_are_not_pruned_as_navigation()
    {
        var profile = CreateRetrievalMatch("profile", "Knowledge/list.pdf", "document_profile", 0.32) with
        {
            Text = "Ce document comprend une liste de contenus disponibles et un apercu des sujets traites."
        };

        Assert.False(RagEndpoints.LooksLikeNavigationalChunk(profile));
        Assert.False(RagEndpoints.LooksLikeGlossaryChunk(profile));
    }

    [Fact]
    public void Comparative_subject_anchor_uses_requested_subject_not_the_criterion()
    {
        var tokens = RagEndpoints.ExtractComparativeSubjectAnchorTokens("Quel dessert est le plus technique ?");

        Assert.Contains("dessert", tokens);
        Assert.DoesNotContain("technique", tokens);
        Assert.True(RagEndpoints.ContainsComparativeSubjectAnchor(tokens, "Desserts et patisserie."));
        Assert.False(RagEndpoints.ContainsComparativeSubjectAnchor(tokens, "Technique très simple pour une procedure."));
    }

    [Fact]
    public void CalibrateFusedMatches_penalizes_comparative_criterion_only_matches()
    {
        var criterionOnly = new RagMatch(
            0.96,
            "doc-criterion",
            "Knowledge/technique.pdf",
            "technique.pdf",
            1,
            1,
            "chunk-criterion",
            0,
            "Cette procedure exige une technique tres simple.",
            1,
            "hash-criterion",
            "Matched profile title: technique tres simple\nCette procedure exige une technique tres simple.",
            "sparse_bm25_v1",
            1,
            1,
            "Procedure",
            "Procedure",
            "unit_exact_v1",
            null,
            null,
            null);
        var subjectMatch = new RagMatch(
            0.78,
            "doc-subject",
            "Knowledge/desserts.pdf",
            "desserts.pdf",
            2,
            2,
            "chunk-subject",
            1,
            "Desserts: eclairs, profiteroles et autres patisseries.",
            1,
            "hash-subject",
            "Desserts: eclairs, profiteroles et autres patisseries.",
            "sparse_bm25_v1",
            1,
            1,
            "Desserts",
            "Desserts",
            "unit_exact_v1",
            null,
            null,
            null);

        var ranked = RagEndpoints.CalibrateFusedMatches(
            "Quel dessert est le plus technique ?",
            [criterionOnly, subjectMatch]);

        Assert.Equal("doc-subject", ranked[0].DocId);
    }

    [Fact]
    public void SuppressNavigationalNoise_removes_glossary_chunks_unless_definition_is_requested()
    {
        var glossary = new RagMatch(
            0.98,
            "doc-glossary",
            "Knowledge/glossary.pdf",
            "glossary.pdf",
            1,
            1,
            "chunk-glossary",
            0,
            "NNAPPER : Recouvrir de sauce. PAPIER PARCHEMIN : Papier de cuisson. POCHER : Cuire dans un liquide. RESERVER : Mettre de cote.",
            1,
            "hash-glossary",
            "NNAPPER : Recouvrir de sauce. PAPIER PARCHEMIN : Papier de cuisson. POCHER : Cuire dans un liquide. RESERVER : Mettre de cote.",
            "sparse_bm25_v1",
            1,
            1,
            "Glossaire",
            "Glossaire",
            "section_window_v1",
            null,
            null,
            null);
        var content = new RagMatch(
            0.78,
            "doc-content",
            "Knowledge/content.pdf",
            "content.pdf",
            2,
            2,
            "chunk-content",
            1,
            "Dessert au chocolat. Ingredients: chocolat, sucre. Preparation: cuire puis refroidir.",
            1,
            "hash-content",
            "Dessert au chocolat. Ingredients: chocolat, sucre. Preparation: cuire puis refroidir.",
            "sparse_bm25_v1",
            1,
            1,
            "Dessert",
            "Dessert",
            "unit_exact_v1",
            null,
            null,
            null);

        var filtered = RagEndpoints.SuppressNavigationalNoise(
            "Quel dessert est le plus technique ?",
            [glossary, content]);
        var definition = RagEndpoints.SuppressNavigationalNoise(
            "Que veut dire pocher ?",
            [glossary, content]);

        Assert.True(RagEndpoints.LooksLikeGlossaryChunk(glossary));
        Assert.DoesNotContain(filtered, match => match.DocId == "doc-glossary");
        Assert.Contains(definition, match => match.DocId == "doc-glossary");
    }

    [Fact]
    public void EvaluateSelectionUpdate_rejects_authorization_when_capability_is_stale()
    {
        var current = new AdminRuntimeCapabilityStateDto(
            Key: "core.retrieval",
            DisplayName: "Core retrieval",
            Family: "core",
            RuntimeKey: "retrieval-stack",
            Implemented: true,
            DesiredEnabled: true,
            Installed: true,
            Configured: true,
            Healthy: true,
            Qualified: true,
            Authorized: false,
            Selected: false,
            ProfileKey: "default-local",
            PassCount: 3,
            Stale: true,
            PersistedAuthorized: false,
            PersistedSelected: false,
            EffectiveAuthorized: false,
            EffectiveSelected: false);

        var decision = RuntimeCapabilitySelectionCoordinator.EvaluateSelectionUpdate(
            current,
            new AdminRuntimeCapabilitySelectionRequestDto(
                DesiredEnabled: true,
                Authorized: true,
                Selected: false));

        Assert.False(decision.Accepted);
        Assert.Equal("capability must be requalified because its qualification is stale", decision.Error);
        Assert.Equal(current, decision.State);
    }

    [Fact]
    public void EvaluateSelectionUpdate_disabling_capability_clears_authorization_and_selection()
    {
        var current = new AdminRuntimeCapabilityStateDto(
            Key: "core.retrieval",
            DisplayName: "Core retrieval",
            Family: "core",
            RuntimeKey: "retrieval-stack",
            Implemented: true,
            DesiredEnabled: true,
            Installed: true,
            Configured: true,
            Healthy: true,
            Qualified: true,
            Authorized: true,
            Selected: true,
            ProfileKey: "default-local",
            PassCount: 3,
            PersistedAuthorized: true,
            PersistedSelected: true,
            EffectiveAuthorized: true,
            EffectiveSelected: true);

        var decision = RuntimeCapabilitySelectionCoordinator.EvaluateSelectionUpdate(
            current,
            new AdminRuntimeCapabilitySelectionRequestDto(
                DesiredEnabled: false,
                Authorized: null,
                Selected: null));

        Assert.True(decision.Accepted);
        Assert.False(decision.State.DesiredEnabled);
        Assert.False(decision.State.Authorized);
        Assert.False(decision.State.Selected);
        Assert.False(decision.State.EffectiveAuthorized);
        Assert.False(decision.State.EffectiveSelected);
    }

    [Fact]
    public void BuildDefaultState_for_capability_b_reflects_disabled_backoffice_runtime()
    {
        var definition = Assert.Single(
            RuntimeCapabilityRegistry.Definitions,
            item => item.Key == "capability_b.backoffice_generation");
        var previous = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");

        try
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", null);

            var state = RuntimeCapabilityStateResolver.BuildDefaultState(
                definition,
                new RuntimeGovernanceOptions(),
                new RagOptions());

            Assert.True(state.Implemented);
            Assert.True(state.Installed);
            Assert.False(state.Configured);
            Assert.False(state.Qualified);
            Assert.False(state.Selected);
            Assert.NotNull(state.Details);
            Assert.Equal("backoffice_disabled", state.Details!["status"]);
            Assert.Equal(false, state.Details["backofficeEnabled"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previous);
        }
    }

    [Fact]
    public void CapabilityBIngestionIdle_blocks_when_ingestion_is_active()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        var snapshot = RuntimeCapabilityBIngestionIdleCoordinator.Evaluate(
            activeIngestionJobs: 2,
            lastIngestionActivityAt: now.AddHours(-2),
            requiredIdleDelay: TimeSpan.FromMinutes(15),
            now);

        Assert.False(snapshot.IsIdle);
        Assert.Equal("ingestion_active", snapshot.Reason);
        Assert.Equal(2, snapshot.ActiveIngestionJobs);
        Assert.Null(snapshot.IdleFor);
    }

    [Fact]
    public void CapabilityBIngestionIdle_waits_after_recent_ingestion_activity()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        var snapshot = RuntimeCapabilityBIngestionIdleCoordinator.Evaluate(
            activeIngestionJobs: 0,
            lastIngestionActivityAt: now.AddMinutes(-5),
            requiredIdleDelay: TimeSpan.FromMinutes(15),
            now);

        Assert.False(snapshot.IsIdle);
        Assert.Equal("ingestion_recent", snapshot.Reason);
        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.IdleFor);
    }

    [Fact]
    public void CapabilityBIngestionIdle_allows_summary_work_after_idle_delay()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        var snapshot = RuntimeCapabilityBIngestionIdleCoordinator.Evaluate(
            activeIngestionJobs: 0,
            lastIngestionActivityAt: now.AddMinutes(-20),
            requiredIdleDelay: TimeSpan.FromMinutes(15),
            now);

        Assert.True(snapshot.IsIdle);
        Assert.Equal("ingestion_idle", snapshot.Reason);
        Assert.Equal(TimeSpan.FromMinutes(20), snapshot.IdleFor);
    }

    [Fact]
    public void CapabilityBIngestionIdle_allows_summary_work_when_no_ingestion_history_exists()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        var snapshot = RuntimeCapabilityBIngestionIdleCoordinator.Evaluate(
            activeIngestionJobs: 0,
            lastIngestionActivityAt: null,
            requiredIdleDelay: TimeSpan.FromMinutes(15),
            now);

        Assert.True(snapshot.IsIdle);
        Assert.Equal("no_ingestion_activity", snapshot.Reason);
        Assert.Null(snapshot.IdleFor);
    }

    [Fact]
    public void CapabilityBRagIdle_blocks_when_interactive_retrieval_is_active()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        var snapshot = RuntimeCapabilityBRagIdleCoordinator.Evaluate(
            activeRetrievals: 2,
            lastActivityAt: now.AddMinutes(-10),
            requiredIdleDelay: TimeSpan.FromMinutes(2),
            now);

        Assert.False(snapshot.IsIdle);
        Assert.Equal("rag_active", snapshot.Reason);
        Assert.Equal(2, snapshot.ActiveRetrievals);
        Assert.Null(snapshot.IdleFor);
    }

    [Fact]
    public void CapabilityBRagIdle_waits_after_recent_interactive_retrieval()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        var snapshot = RuntimeCapabilityBRagIdleCoordinator.Evaluate(
            activeRetrievals: 0,
            lastActivityAt: now.AddSeconds(-30),
            requiredIdleDelay: TimeSpan.FromMinutes(2),
            now);

        Assert.False(snapshot.IsIdle);
        Assert.Equal("rag_recent", snapshot.Reason);
        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.IdleFor);
    }

    [Fact]
    public void CapabilityBRagIdle_allows_summary_work_after_idle_delay()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        var snapshot = RuntimeCapabilityBRagIdleCoordinator.Evaluate(
            activeRetrievals: 0,
            lastActivityAt: now.AddMinutes(-3),
            requiredIdleDelay: TimeSpan.FromMinutes(2),
            now);

        Assert.True(snapshot.IsIdle);
        Assert.Equal("rag_idle", snapshot.Reason);
        Assert.Equal(TimeSpan.FromMinutes(3), snapshot.IdleFor);
    }

    [Fact]
    public void CapabilityBRagIdle_tracker_marks_active_then_recent_then_idle()
    {
        RuntimeCapabilityBRagIdleCoordinator.ResetForTests();
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        var options = new RuntimeGovernanceOptions
        {
            CapabilityBRequireRagIdleForExecution = true,
            CapabilityBRagIdleDelaySeconds = 120
        };

        using (RuntimeCapabilityBRagIdleCoordinator.BeginInteractiveRetrieval(options, now))
        {
            var active = RuntimeCapabilityBRagIdleCoordinator.LoadSnapshot(options, now);

            Assert.False(active.IsIdle);
            Assert.Equal("rag_active", active.Reason);
            Assert.Equal(1, active.ActiveRetrievals);
        }

        var recent = RuntimeCapabilityBRagIdleCoordinator.LoadSnapshot(options, now.AddSeconds(30));
        var idle = RuntimeCapabilityBRagIdleCoordinator.LoadSnapshot(options, now.AddMinutes(3));

        Assert.False(recent.IsIdle);
        Assert.Equal("rag_recent", recent.Reason);
        Assert.True(idle.IsIdle);
        Assert.Equal("rag_idle", idle.Reason);

        RuntimeCapabilityBRagIdleCoordinator.ResetForTests();
    }

    [Fact]
    public void CapabilityBIdleSchedulerDto_exposes_policy_for_client_surfaces()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        var options = new RuntimeGovernanceOptions
        {
            CapabilityBRequireIngestionIdleForExecution = true,
            CapabilityBAutoEnqueueWhenIngestionIdleEnabled = true,
            CapabilityBIngestionIdleDelaySeconds = 900,
            CapabilityBAutoEnqueueBatchSize = 7
        };
        var snapshot = RuntimeCapabilityBIngestionIdleCoordinator.Evaluate(
            activeIngestionJobs: 0,
            lastIngestionActivityAt: now.AddMinutes(-5),
            requiredIdleDelay: TimeSpan.FromSeconds(options.CapabilityBIngestionIdleDelaySeconds),
            now);

        var dto = RuntimeCapabilityBIngestionIdleCoordinator.BuildSchedulerDto(options, snapshot);

        Assert.True(dto.Enabled);
        Assert.True(dto.RequiresIngestionIdle);
        Assert.True(dto.AutoEnqueueEnabled);
        Assert.False(dto.IsIdle);
        Assert.Equal("waiting_for_ingestion_idle", dto.State);
        Assert.Equal(900, dto.RequiredIdleSeconds);
        Assert.Equal(300, dto.IdleForSeconds);
        Assert.Equal(7, dto.AutoEnqueueBatchSize);
    }

    private static RagMatch CreateRetrievalMatch(string chunkId, string docPath, string chunkType, double score)
        => new(
            Score: score,
            DocId: Guid.NewGuid().ToString(),
            DocPath: docPath,
            DocName: Path.GetFileName(docPath),
            PageStart: 1,
            PageEnd: 1,
            ChunkId: chunkId,
            ChunkIndex: 0,
            Text: chunkType,
            IngestionVersion: 1,
            HashDoc: "abc",
            EmbedText: chunkType,
            EmbeddingBasis: chunkType == "document_profile" ? "document_profile_v1" : "unit_exact_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: chunkType,
            HeadingPath: chunkType,
            ChunkType: chunkType,
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SAAIA.Backend.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
