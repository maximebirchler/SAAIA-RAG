using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public void Evidence_ranking_prioritizes_content_matching_the_focused_query()
    {
        var generic = BuildEvidence(
            "E-GENERIC",
            "IEC 60079-14 présente des exigences générales et son domaine d'application.");
        var focused = BuildEvidence(
            "E-FOCUSED",
            "Si la pression ou le débit du gaz de protection baisse, une alarme est requise.");

        var ordered = OpenAiCompatibleAdvancedAnalysisProvider
            .OrderEvidenceForQuery(
                "IEC 60079-14 gaz de protection pression débit alarme",
                "IEC 60079-14",
                [generic, focused]);

        Assert.Equal("E-FOCUSED", ordered[0].Reference.EvidenceId);
    }

    [Fact]
    public void Multi_item_prompt_promotes_diverse_pages_from_the_best_supported_source()
    {
        var primaryDoc = Guid.NewGuid().ToString("D");
        var primaryRevision = Guid.NewGuid().ToString("D");
        var evidence = new[]
        {
            BuildEvidence("E-OTHER-1", "Other source.", "other.pdf"),
            BuildEvidence("E-SCOPE-1", "Collection scope.", "collection.pdf",
                primaryDoc, primaryRevision, 1),
            BuildEvidence("E-SCOPE-2", "Repeated scope.", "collection.pdf",
                primaryDoc, primaryRevision, 1),
            BuildEvidence("E-OTHER-2", "Other item.", "other.pdf"),
            BuildEvidence("E-INDEX", "Index with named items.", "collection.pdf",
                primaryDoc, primaryRevision, 3),
            BuildEvidence("E-ITEM", "Named item details.", "collection.pdf",
                primaryDoc, primaryRevision, 4)
        };
        var retrievalQueries = new Dictionary<string, HashSet<string>>
        {
            ["E-OTHER-1"] = ["q1"],
            ["E-OTHER-2"] = ["q2"],
            ["E-SCOPE-1"] = ["q1", "q2", "q3", "q4"],
            ["E-SCOPE-2"] = ["q1", "q2", "q3"],
            ["E-INDEX"] = ["q1", "q2", "q3"],
            ["E-ITEM"] = ["q1", "q2"]
        };
        var load = new AdvancedAnalysisLoadDescriptor
        {
            PlanKind = "multi_item",
            AnswerUnitCount = 5,
            AtomicEvidenceMode = "one_per_item"
        };

        var prioritized = OpenAiCompatibleAdvancedAnalysisProvider
            .PrioritizeCollectionEvidenceForPrompt(
                load,
                evidence,
                retrievalQueries);

        Assert.Equal(
            ["E-SCOPE-1", "E-INDEX", "E-ITEM", "E-SCOPE-2"],
            prioritized.Take(4).Select(static item =>
                item.Reference.EvidenceId));
        Assert.Equal(6, prioritized.Count);
    }

    [Fact]
    public void Document_scope_selector_requires_one_strong_identity_match()
    {
        Assert.Equal(
            "Certifications/PDF/NIST_CSF_2_0.pdf",
            AdvancedAnalysisToolGateway.SelectStrongDocumentScope(
                "NIST_CSF_2_0.pdf",
                [
                    "Certifications/PDF/NIST_CSF_2_0.pdf",
                    "Certifications/PDF/NIST_SP_800_37r2_RMF.pdf"
                ]));
        Assert.Null(AdvancedAnalysisToolGateway.SelectStrongDocumentScope(
            "IEC 60079-14",
            [
                "Normes/IEC 60079-14 2013.pdf",
                "Archives/IEC 60079-14 2017.pdf"
            ]));
    }

    [Fact]
    public async Task Live_document_hints_resolve_to_one_strong_catalog_scope_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_ADVANCED_DOCUMENT_SCOPE"),
                "1",
                StringComparison.Ordinal))
            return;
        var connectionString = Environment.GetEnvironmentVariable(
            "SAAIA_LIVE_ADVANCED_POSTGRES_CONNECTION_STRING");
        var tenantText = Environment.GetEnvironmentVariable(
            "SAAIA_LIVE_ADVANCED_TENANT_ID");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        Assert.True(Guid.TryParse(tenantText, out var tenantId));
        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        foreach (var hint in new[]
                 {
                     "FD CEN TR 15281 2023",
                     "IEC 60079-14",
                     "NIST_CSF_2_0.pdf"
                 })
        {
            var candidates = await AdvancedAnalysisToolGateway
                .ResolveDocumentScopeCandidatesAsync(
                dataSource,
                tenantId,
                hint,
                category: null,
                CancellationToken.None);
            Assert.True(
                AdvancedAnalysisToolGateway.SelectStrongDocumentScope(
                    hint,
                    candidates) is not null,
                $"No unambiguous scope for '{hint}'. Candidates: {string.Join(" | ", candidates)}");
        }
    }

    [Fact]
    public async Task Planner_search_and_writer_share_one_internal_provider_contract()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""
                {"queries":[{"query":"repas documentés petit-déjeuner","category":"Menus","topK":24}]}
                """),
            Completion("""
                {"outcome":"answered","answerText":"Lundi : porridge documenté [C1].","claims":[{"claimId":"C1","text":"Le porridge est documenté.","evidenceIds":["E1"]}]}
                """));
        var provider = CreateProvider(factory, apiKey: "server-secret");
        var evidence = BuildEvidence("E1", "Porridge aux pommes et cannelle.");
        var gateway = new RecordingToolGateway(evidence);

        var result = await provider.ExecuteAsync(
            BuildRequest(),
            gateway,
            CancellationToken.None);

        Assert.Equal("customer-server", provider.ProviderKey);
        Assert.Equal(AdvancedAnalysisProviderLocation.Internal, provider.Location);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal("qualified-model.gguf", result.ModelId);
        Assert.Equal(2, result.ProviderCallCount);
        Assert.Equal(200, result.InputTokens);
        Assert.Equal(100, result.OutputTokens);
        Assert.Equal(20, result.CachedInputTokens);
        Assert.Equal("E1", Assert.Single(result.Claims).EvidenceIds.Single());
        var search = Assert.Single(gateway.Searches);
        Assert.Equal("repas documentés petit-déjeuner", search.Query);
        Assert.Null(search.Category);
        Assert.Equal(24, search.TopK);
        Assert.Equal(2, factory.Requests.Count);
        Assert.All(factory.Requests, request =>
        {
            Assert.Equal(
                new Uri("http://advanced-llm:8080/v1/chat/completions"),
                request.Uri);
            Assert.Equal("Bearer", request.Authorization?.Scheme);
            Assert.Equal("server-secret", request.Authorization?.Parameter);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("qualified-model.gguf", body.RootElement
                .GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("json_object", body.RootElement
                .GetProperty("response_format")
                .GetProperty("type").GetString());
        });
        Assert.Contains("Porridge aux pommes", factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "distinguish neutral presentation coordinates from semantic",
            factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "Neutral coordinates such as weekdays",
            factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "qualifier in the request must remain explicit",
            factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "limits of the supplied evidence, not an exhaustive absence",
            factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "never as an exhaustive corpus inventory",
            factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "never label every neutral coordinate or every requested unit",
            factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "minimum remaining deficit for the decisive role or relation",
            factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "same relation-grounding rule applies inside an insufficiency",
            factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "generic or neighboring role is not interchangeable",
            factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            "common alternate terminology",
            factory.Requests[0].Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("server-secret", factory.Requests[0].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writer_receives_opaque_keys_for_same_source_evidence_composition()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""),
            Completion("""
                {"outcome":"answered","answerText":"Repas documenté [C1].","claims":[{"claimId":"C1","text":"Repas documenté.","evidenceIds":["E-SCOPE","E-ITEM"]}]}
                """));
        var provider = CreateProvider(factory);
        var docId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var gateway = new RecordingToolGateway(
            BuildEvidence(
                "E-SCOPE",
                "Toutes les recettes de ce livre sont simples pour étudiants.",
                "etudiants.pdf",
                docId,
                revisionId),
            BuildEvidence(
                "E-ITEM",
                "Index : quesadillas à la mozzarella.",
                "etudiants.pdf",
                docId,
                revisionId),
            BuildEvidence(
                "E-OTHER",
                "Recette provenant d'un autre document.",
                "autre.pdf"));

        var result = await provider.ExecuteAsync(
            BuildRequest(),
            gateway,
            CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        using var requestBody = JsonDocument.Parse(factory.Requests[1].Body);
        var writerUserJson = requestBody.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString();
        using var writerUser = JsonDocument.Parse(writerUserJson!);
        var promptEvidence = writerUser.RootElement
            .GetProperty("evidence")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("evidenceId").GetString()!,
                item => item.GetProperty("sourceKey").GetString()!);
        Assert.Equal(promptEvidence["E-SCOPE"], promptEvidence["E-ITEM"]);
        Assert.NotEqual(promptEvidence["E-SCOPE"], promptEvidence["E-OTHER"]);
        Assert.DoesNotContain(docId, factory.Requests[1].Body,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(revisionId, factory.Requests[1].Body,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never carry a scope statement across different",
            factory.Requests[1].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Planner_web_filters_and_untrusted_categories_are_removed_before_corpus_search()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""
                {"queries":[{"query":"site:recettes.example recettes petit-déjeuner faciles","category":"Petit-déjeuner","topK":20}]}
                """),
            Completion("""
                {"outcome":"answered","answerText":"Réponse sourcée [C1].","claims":[{"claimId":"C1","text":"Preuve.","evidenceIds":["E1"]}]}
                """));
        var provider = CreateProvider(factory);
        var gateway = new RecordingToolGateway(
            BuildEvidence("E1", "Petit-déjeuner documenté."));

        var result = await provider.ExecuteAsync(
            BuildRequest(),
            gateway,
            CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        var search = Assert.Single(gateway.Searches);
        Assert.Equal("recettes petit-déjeuner faciles", search.Query);
        Assert.Null(search.Category);
    }

    [Fact]
    public async Task Structured_grid_limits_planner_queries_to_semantic_columns()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""
                {"queries":[
                  {"query":"petits déjeuners","topK":20},
                  {"query":"déjeuners","topK":20},
                  {"query":"collations","topK":20},
                  {"query":"soupers","topK":20},
                  {"query":"menus hebdomadaires","topK":20},
                  {"query":"recettes faciles","topK":20}]}
                """),
            Completion("""
                {"outcome":"answered","answerText":"Réponse sourcée [C1].","claims":[{"claimId":"C1","text":"Preuve.","evidenceIds":["E1"]}]}
                """));
        var provider = CreateProvider(factory);
        var gateway = new RecordingToolGateway(
            BuildEvidence("E1", "Préparation documentée."));

        var result = await provider.ExecuteAsync(
            BuildRequest(),
            gateway,
            CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, gateway.Searches.Count);
        Assert.DoesNotContain(gateway.Searches,
            search => search.Query == "menus hebdomadaires");
    }

    [Fact]
    public async Task Explicit_document_comparison_excludes_neighboring_standards()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""
                {"queries":[{"query":"FD CEN TR 15281 2023 inertage oxygène","topK":20},{"query":"IEC 60079-14 2013 gaz de protection pression débit alarme","topK":20}]}
                """),
            Completion("""
                {"outcome":"answered","answerText":"CEN [C1] et IEC [C2].","claims":[{"claimId":"C1","text":"CEN.","evidenceIds":["E-FD"]},{"claimId":"C2","text":"IEC.","evidenceIds":["E-IEC"]}]}
                """));
        var provider = CreateProvider(factory);
        var gateway = new RecordingToolGateway(
            BuildEvidence(
                "E-WRONG",
                "WRONG EN 1127 CONTENT",
                "EN 1127-1 2019.pdf"),
            BuildEvidence(
                "E-FD",
                "CORRECT FD CONTENT",
                "FD CEN TR 15281 2023.pdf"),
            BuildEvidence(
                "E-IEC",
                "CORRECT IEC CONTENT",
                "IEC 60079-14 2013.pdf"));

        var result = await provider.ExecuteAsync(
            BuildComparisonRequest(),
            gateway,
            CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(
            ["E-FD", "E-IEC"],
            result.Claims
                .SelectMany(static claim => claim.EvidenceIds)
                .OrderBy(static id => id));
        Assert.Contains("CORRECT FD CONTENT", factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains("CORRECT IEC CONTENT", factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("WRONG EN 1127 CONTENT", factory.Requests[1].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_document_comparison_adds_one_required_query_per_document()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""
                {"queries":[{"query":"FD CEN TR 15281 2023 inertage oxygène","topK":20},{"query":"IEC 60079-14 2013 gaz de protection pression débit alarme","topK":20}]}
                """),
            Completion("""
                {"outcome":"answered","answerText":"CEN [C1] et IEC [C2].","claims":[{"claimId":"C1","text":"CEN.","evidenceIds":["E-FD"]},{"claimId":"C2","text":"IEC.","evidenceIds":["E-IEC"]}]}
                """));
        var provider = CreateProvider(factory);
        var gateway = new DocumentAwareToolGateway(
            BuildEvidence(
                "E-FD",
                "Contenu FD.",
                "FD CEN TR 15281 2023.pdf"),
            BuildEvidence(
                "E-IEC",
                "Contenu IEC.",
                "IEC 60079-14 2013.pdf"));

        var result = await provider.ExecuteAsync(
            BuildComparisonRequest(),
            gateway,
            CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Contains(gateway.Searches,
            search => search.Query.Contains("15281", StringComparison.Ordinal));
        Assert.Contains(gateway.Searches,
            search => search.Query.Contains("60079", StringComparison.Ordinal));
        Assert.Contains(gateway.Searches,
            search => search.DocumentHint?.Contains(
                "15281",
                StringComparison.Ordinal) == true);
        Assert.Contains(gateway.Searches,
            search => search.DocumentHint?.Contains(
                "60079",
                StringComparison.Ordinal) == true);
        Assert.Contains(gateway.Searches,
            search => search.Query.Contains("alarme", StringComparison.Ordinal)
                      && search.DocumentHint?.Contains(
                          "60079",
                          StringComparison.Ordinal) == true);
        Assert.Equal(2, result.ProviderCallCount);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Equal(
            ["E-FD", "E-IEC"],
            result.Claims.SelectMany(static claim => claim.EvidenceIds)
                .OrderBy(static id => id));
    }

    [Fact]
    public async Task Named_document_extraction_excludes_other_documents()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"CSF fonctions profils tiers","topK":20}]}"""),
            Completion("""
                {"outcome":"answered","answerText":"Point NIST [C1].","claims":[{"claimId":"C1","text":"Point NIST.","evidenceIds":["E-CSF"]}]}
                """));
        var provider = CreateProvider(factory);
        var gateway = new RecordingToolGateway(
            BuildEvidence(
                "E-CSF",
                "CSF 2.0 CONTENT",
                "NIST_CSF_2_0.pdf"),
            BuildEvidence(
                "E-OTHER",
                "OTHER NIST CONTENT",
                "NIST_SP_800_37r2_RMF.pdf"));

        var result = await provider.ExecuteAsync(
            BuildNamedDocumentRequest(),
            gateway,
            CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal("E-CSF", Assert.Single(result.Claims).EvidenceIds.Single());
        Assert.Contains("CSF 2.0 CONTENT", factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OTHER NIST CONTENT", factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Equal(2, result.ProviderCallCount);
    }

    [Fact]
    public async Task No_revalidated_evidence_returns_deterministic_insufficient_without_writer_call()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""
                {"queries":[{"query":"preuve absente","topK":12}]}
                """));
        var provider = CreateProvider(factory);
        var gateway = new RecordingToolGateway();

        var result = await provider.ExecuteAsync(
            BuildRequest(),
            gateway,
            CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Contains("pas assez de preuves", result.AnswerText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Claims);
        Assert.Single(factory.Requests);
        Assert.Single(gateway.Searches);
    }

    [Fact]
    public async Task Writer_cannot_cite_an_evidence_id_that_was_not_revalidated()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""),
            Completion("""
                {"outcome":"answered","answerText":"Réponse forgée [C1].","claims":[{"claimId":"C1","text":"Faux.","evidenceIds":["FORGED"]}]}
                """));
        var provider = CreateProvider(factory);
        var gateway = new RecordingToolGateway(
            BuildEvidence("E1", "Preuve réelle."));

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                gateway,
                CancellationToken.None));

        Assert.Equal("advanced_writer_evidence_id_invalid", error.ErrorCode);
        Assert.Equal(2, factory.Requests.Count);
    }

    [Fact]
    public async Task Writer_must_return_the_requested_number_of_answer_units()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""
                {"queries":[{"query":"FD CEN TR 15281 2023 inertage oxygène","topK":20},{"query":"IEC 60079-14 2013 gaz de protection pression débit alarme","topK":20}]}
                """),
            Completion("""
                {"outcome":"answered","answerText":"Une seule unité [C1].","claims":[{"claimId":"C1","text":"Une seule unité.","evidenceIds":["E-FD"]}]}
                """));
        var provider = CreateProvider(factory);
        var gateway = new RecordingToolGateway(
            BuildEvidence("E-FD", "Preuve CEN.", "FD CEN TR 15281 2023.pdf"),
            BuildEvidence("E-IEC", "Preuve IEC.", "IEC 60079-14 2013.pdf"));

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildComparisonRequest(),
                gateway,
                CancellationToken.None));

        Assert.Equal("advanced_writer_claim_count_invalid", error.ErrorCode);
    }

    [Fact]
    public async Task Writer_cannot_repeat_one_claim_to_fill_distinct_units()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""
                {"queries":[{"query":"FD CEN TR 15281 2023 inertage oxygène","topK":20},{"query":"IEC 60079-14 2013 gaz de protection pression débit alarme","topK":20}]}
                """),
            Completion("""
                {"outcome":"answered","answerText":"Même fait [C1]. Même fait [C2].","claims":[{"claimId":"C1","text":"Même fait.","evidenceIds":["E-FD"]},{"claimId":"C2","text":"Même fait.","evidenceIds":["E-IEC"]}]}
                """));
        var provider = CreateProvider(factory);
        var gateway = new RecordingToolGateway(
            BuildEvidence("E-FD", "Preuve CEN.", "FD CEN TR 15281 2023.pdf"),
            BuildEvidence("E-IEC", "Preuve IEC.", "IEC 60079-14 2013.pdf"));

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildComparisonRequest(),
                gateway,
                CancellationToken.None));

        Assert.Equal("advanced_writer_duplicate_claims", error.ErrorCode);
    }

    [Fact]
    public async Task Writer_answer_repairs_missing_claim_markers_once()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""),
            Completion("""
                {"outcome":"answered","answerText":"Réponse sans marqueur.","claims":[{"claimId":"C1","text":"Preuve.","evidenceIds":["E1"]}]}
                """),
            Completion("""
                {"outcome":"answered","answerText":"Réponse réparée [C1].","claims":[{"claimId":"C1","text":"Preuve.","evidenceIds":["E1"]}]}
                """));
        var provider = CreateProvider(factory);

        var result = await provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(
                    BuildEvidence("E1", "Preuve réelle.")),
                CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Contains("[C1]", result.AnswerText, StringComparison.Ordinal);
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Equal(3, factory.Requests.Count);
    }

    [Fact]
    public async Task Semantic_critic_replaces_a_supported_unit_erasing_insufficiency()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""),
            Completion("""
                {"outcome":"insufficient_documentation","answerText":"Les cinq unités sont non étayées [C1].","claims":[{"claimId":"C1","text":"Les cinq unités sont non étayées.","evidenceIds":["E1"]}]}
                """),
            Completion("""
                {"outcome":"insufficient_documentation","answerText":"Une unité est étayée; il en manque quatre [C1].","claims":[{"claimId":"C1","text":"La preuve étaye une unité et quatre unités supplémentaires restent nécessaires.","evidenceIds":["E1"]}]}
                """));
        var options = CreateOptions();
        options.SemanticCriticEnabled = true;
        options.CriticMaxTokens = 1_200;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            apiKey: null);

        var result = await provider.ExecuteAsync(
            BuildRequest(),
            new RecordingToolGateway(BuildEvidence(
                "E1",
                "Une unité explicitement documentée.")),
            CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Contains("il en manque quatre", result.AnswerText,
            StringComparison.Ordinal);
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Equal(3, factory.Requests.Count);
        Assert.Contains("Treat the candidate as an untrusted proposal",
            factory.Requests[2].Body,
            StringComparison.Ordinal);
        using var criticRequest = JsonDocument.Parse(factory.Requests[2].Body);
        var criticUserPrompt = criticRequest.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString()!;
        using var criticPayload = JsonDocument.Parse(criticUserPrompt);
        Assert.Contains("Les cinq unités sont non étayées",
            criticPayload.RootElement.GetProperty("candidate")
                .GetProperty("answerText").GetString(),
            StringComparison.Ordinal);
        Assert.Contains("Une unité explicitement documentée",
            criticPayload.RootElement.GetProperty("evidence")[0]
                .GetProperty("content").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Semantic_critic_fails_closed_on_an_invalid_result()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""),
            Completion("""
                {"outcome":"answered","answerText":"Réponse [C1].","claims":[{"claimId":"C1","text":"Réponse.","evidenceIds":["E1"]}]}
                """),
            Completion("""{"outcome":"answered","answerText":"truncated"""));
        var options = CreateOptions();
        options.SemanticCriticEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            apiKey: null);

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(BuildEvidence("E1", "Réponse.")),
                CancellationToken.None));

        Assert.Equal("advanced_critic_protocol_invalid", error.ErrorCode);
        Assert.Equal(3, factory.Requests.Count);
    }

    [Fact]
    public async Task Writer_repairs_an_internal_source_key_before_publication()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""),
            Completion("""
                {"outcome":"answered","answerText":"Repas — source internal-source-1 [C1].","claims":[{"claimId":"C1","text":"Repas issu de internal-source-1.","evidenceIds":["E1"]}]}
                """),
            Completion("""
                {"outcome":"answered","answerText":"Repas documenté [C1].","claims":[{"claimId":"C1","text":"Repas documenté.","evidenceIds":["E1"]}]}
                """));
        var provider = CreateProvider(factory);

        var result = await provider.ExecuteAsync(
            BuildRequest(),
            new RecordingToolGateway(BuildEvidence("E1", "Repas documenté.")),
            CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.DoesNotContain("internal-source-", result.AnswerText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Contains("Remove any internal sourceKey label",
            factory.Requests[2].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writer_repairs_one_malformed_protocol_response()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""),
            Completion("""{"outcome":"answered","answerText":"Réponse incomplète"""),
            Completion("""
                {"outcome":"answered","answerText":"Réponse réparée [C1].","claims":[{"claimId":"C1","text":"Preuve.","evidenceIds":["E1"]}]}
                """));
        var provider = CreateProvider(factory);

        var result = await provider.ExecuteAsync(
            BuildRequest(),
            new RecordingToolGateway(BuildEvidence("E1", "Preuve réelle.")),
            CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal("Réponse réparée [C1].", result.AnswerText);
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Equal(3, factory.Requests.Count);
        Assert.Contains("malformed", factory.Requests[2].Body,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Http_failure_exposes_only_status_code_not_response_body_or_secret()
    {
        using var factory = new QueuedHttpClientFactory(
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(
                    "upstream leaked server-secret and private corpus",
                    Encoding.UTF8,
                    "text/plain")
            });
        var provider = CreateProvider(factory, apiKey: "server-secret");

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(),
                CancellationToken.None));

        Assert.Equal("advanced_llm_http_502", error.ErrorCode);
        Assert.Equal("advanced_llm_http_502", error.Message);
        Assert.DoesNotContain("server-secret", error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("private corpus", error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_body_timeout_is_normalized_after_headers()
    {
        using var factory = new QueuedHttpClientFactory(
            CompletionStream(new OperationCanceledException()));
        var provider = CreateProvider(factory, apiKey: "server-secret");

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(),
                CancellationToken.None));

        Assert.Equal("advanced_llm_timeout", error.ErrorCode);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task Response_body_network_failure_is_normalized_after_headers()
    {
        using var factory = new QueuedHttpClientFactory(
            CompletionStream(new IOException("private transport detail")));
        var provider = CreateProvider(factory, apiKey: "server-secret");

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(),
                CancellationToken.None));

        Assert.Equal("advanced_llm_transport_error", error.ErrorCode);
        Assert.DoesNotContain("private transport detail", error.ToString(),
            StringComparison.Ordinal);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reclassified_as_provider_timeout()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""));
        var provider = CreateProvider(factory, apiKey: "server-secret");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(),
                cancellation.Token));
    }

    [Fact]
    public async Task OpenAi_dev_uses_external_policy_identity_and_terra_dialect_without_corpus_metadata()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"repas","topK":20}]}"""),
            Completion("""
                {"outcome":"answered","answerText":"Réponse sourcée [C1].","claims":[{"claimId":"C1","text":"Preuve.","evidenceIds":["E1"]}]}
                """));
        var options = CreateOptions();
        options.Provider = "openai-dev";
        options.LlmLocation = "external-service";
        options.LlmBaseUrl = "https://api.openai.com/v1";
        options.LlmModel = "gpt-5.6-terra";
        options.ReasoningEffort = "low";
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            "openai-secret");
        var gateway = new RecordingToolGateway(
            BuildEvidence("E1", "Preuve externe minimale."));

        var result = await provider.ExecuteAsync(
            BuildRequest(),
            gateway,
            CancellationToken.None);

        Assert.Equal(AdvancedAnalysisProviderLocation.ExternalService,
            provider.Location);
        Assert.Equal("openai-dev", provider.ProviderKey);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(0.001564m, result.EstimatedCostUsd);
        Assert.All(factory.Requests, request =>
        {
            using var body = JsonDocument.Parse(request.Body);
            Assert.True(body.RootElement.TryGetProperty(
                "max_completion_tokens", out _));
            Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
            Assert.False(body.RootElement.TryGetProperty("temperature", out _));
            Assert.Equal("low", body.RootElement
                .GetProperty("reasoning_effort").GetString());
        });
        Assert.DoesNotContain("Nutrition/menus.pdf", factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("menus.pdf", factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains("Preuve externe minimale", factory.Requests[1].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPod_bench_uses_the_same_contract_with_openai_compatible_dialect()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""
                {"queries":[{"query":"preuve qualifiée","topK":20}]}
                """, model: "Qwen/Qwen3-32B-AWQ"),
            Completion("""
                {"outcome":"answered","answerText":"Réponse sourcée [C1].","claims":[{"claimId":"C1","text":"Preuve.","evidenceIds":["E1"]}]}
                """, model: "Qwen/Qwen3-32B-AWQ"));
        var options = CreateOptions();
        options.Provider = "runpod-bench";
        options.LlmLocation = "external-service";
        options.LlmBaseUrl = "https://runpod.example/v1";
        options.LlmModel = "open-model-candidate";
        options.ExternalBudgetAuthorizedUsd = 5m;
        options.ExternalBudgetSoftLimitUsd = 4m;
        options.ExternalBudgetHardLimitUsd = 4.80m;
        options.ExternalMaximumCostPerJobUsd = 0.50m;
        options.ExternalInputUsdPerMillionTokens = 10m;
        options.ExternalCachedInputUsdPerMillionTokens = 10m;
        options.ExternalOutputUsdPerMillionTokens = 10m;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            "runpod-secret");

        var result = await provider.ExecuteAsync(
            BuildRequest(),
            new RecordingToolGateway(
                BuildEvidence("E1", "Preuve externe minimale.")),
            CancellationToken.None);

        Assert.Equal("runpod-bench", provider.ProviderKey);
        Assert.Equal(AdvancedAnalysisProviderLocation.ExternalService,
            provider.Location);
        Assert.Equal("open-model-candidate", result.ModelId);
        Assert.Equal(2, result.ProviderCallCount);
        Assert.Equal(0.003m, result.EstimatedCostUsd);
        Assert.All(factory.Requests, request =>
        {
            Assert.Equal(new Uri(
                "https://runpod.example/v1/chat/completions"), request.Uri);
            Assert.Equal("runpod-secret", request.Authorization?.Parameter);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("open-model-candidate", body.RootElement
                .GetProperty("model").GetString());
            Assert.True(body.RootElement.TryGetProperty("max_tokens", out _));
            Assert.False(body.RootElement.TryGetProperty(
                "max_completion_tokens", out _));
            Assert.False(body.RootElement.TryGetProperty(
                "reasoning_effort", out _));
        });
        Assert.DoesNotContain("Nutrition/menus.pdf", factory.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("menus.pdf", factory.Requests[1].Body,
            StringComparison.Ordinal);
        var ledgerEntries = File.ReadAllLines(
            options.ExternalUsageLedgerPath);
        Assert.Equal(2, ledgerEntries.Length);
        Assert.All(ledgerEntries, line =>
        {
            using var entry = JsonDocument.Parse(line);
            Assert.Equal("runpod-bench", entry.RootElement
                .GetProperty("provider").GetString());
            Assert.Equal("open-model-candidate", entry.RootElement
                .GetProperty("modelId").GetString());
            Assert.Equal("Qwen/Qwen3-32B-AWQ", entry.RootElement
                .GetProperty("observedModelId").GetString());
        });
    }

    [Fact]
    public async Task OpenAi_dev_retries_rate_limit_within_the_same_logical_call()
    {
        using var factory = new QueuedHttpClientFactory(
            RateLimited(),
            Completion("""{"queries":[{"query":"repas","topK":20}]}"""),
            Completion("""
                {"outcome":"answered","answerText":"Réponse sourcée [C1].","claims":[{"claimId":"C1","text":"Preuve.","evidenceIds":["E1"]}]}
                """));
        var options = CreateOptions();
        options.Provider = "openai-dev";
        options.LlmLocation = "external-service";
        options.LlmBaseUrl = "https://api.openai.com/v1";
        options.LlmModel = "gpt-5.6-terra";
        options.LlmMaximumHttpAttempts = 2;
        options.LlmMaximumRetryDelayMilliseconds = 100;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            "openai-secret");
        var request = BuildRequest();

        var result = await provider.ExecuteAsync(
            request,
            new RecordingToolGateway(
                BuildEvidence("E1", "Preuve externe minimale.")),
            CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(2, result.ProviderCallCount);
        Assert.Equal(3, factory.Requests.Count);
        var ledgerEntries = File.ReadAllLines(
                options.ExternalUsageLedgerPath)
            .Select(static line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.Equal(2, ledgerEntries.Length);
            Assert.Equal(1, ledgerEntries[0].RootElement
                .GetProperty("retryCount").GetInt32());
            Assert.Equal(2, ledgerEntries[0].RootElement
                .GetProperty("httpAttemptCount").GetInt32());
            Assert.Equal(0, ledgerEntries[1].RootElement
                .GetProperty("retryCount").GetInt32());
            Assert.All(ledgerEntries, entry =>
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.RootElement
                    .GetProperty("requestId").GetString()));
                Assert.Equal(request.JobId, entry.RootElement
                    .GetProperty("traceId").GetGuid());
                Assert.True(entry.RootElement
                    .GetProperty("elapsedMilliseconds").GetInt64() >= 0);
                Assert.Equal(JsonValueKind.Null, entry.RootElement
                    .GetProperty("timeToFirstTokenMilliseconds").ValueKind);
            });
            Assert.Equal(2, ledgerEntries
                .Select(entry => entry.RootElement
                    .GetProperty("requestId").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());
        }
        finally
        {
            foreach (var entry in ledgerEntries)
                entry.Dispose();
        }
    }

    [Fact]
    public async Task Internal_customer_server_does_not_require_or_send_an_api_key()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""
                {"queries":[]}
                """),
            Completion("""
                {"outcome":"answered","answerText":"Porridge documenté [C1].","claims":[{"claimId":"C1","text":"Le porridge est documenté.","evidenceIds":["E1"]}]}
                """));
        var provider = CreateProvider(factory, apiKey: null);
        var gateway = new RecordingToolGateway(BuildEvidence(
            "E1",
            "Porridge aux pommes et cannelle."));

        var result = await provider.ExecuteAsync(
            BuildRequest(),
            gateway,
            CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(2, factory.Requests.Count);
        Assert.All(factory.Requests, request =>
            Assert.Null(request.Authorization));
    }

    [Fact]
    public async Task OpenAi_dev_does_not_retry_a_rate_limit_beyond_job_delay_cap()
    {
        using var factory = new QueuedHttpClientFactory(
            RateLimited(TimeSpan.FromMinutes(30)));
        var options = CreateOptions();
        options.Provider = "openai-dev";
        options.LlmLocation = "external-service";
        options.LlmBaseUrl = "https://api.openai.com/v1";
        options.LlmModel = "gpt-5.6-terra";
        options.LlmMaximumHttpAttempts = 3;
        options.LlmMaximumRetryDelayMilliseconds = 60_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            "openai-secret");

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(),
                CancellationToken.None));

        Assert.Equal("advanced_llm_http_429", error.ErrorCode);
        Assert.True(error.IsRetryable);
        Assert.InRange(
            error.RetryAfterMilliseconds!.Value,
            1_799_000,
            1_800_000);
        Assert.Single(factory.Requests);
        using var ledgerEntry = JsonDocument.Parse(
            Assert.Single(File.ReadAllLines(options.ExternalUsageLedgerPath)));
        Assert.Equal(0m, ledgerEntry.RootElement
            .GetProperty("costUsd").GetDecimal());
        Assert.Equal("req-rate-limited", ledgerEntry.RootElement
            .GetProperty("providerRequestId").GetString());
        Assert.Equal(3, ledgerEntry.RootElement
            .GetProperty("rateLimitRequestLimit").GetInt64());
        Assert.Equal(0, ledgerEntry.RootElement
            .GetProperty("rateLimitRequestRemaining").GetInt64());
        Assert.Equal("21m30s", ledgerEntry.RootElement
            .GetProperty("rateLimitRequestReset").GetString());
        Assert.Equal(10_000, ledgerEntry.RootElement
            .GetProperty("rateLimitTokenLimit").GetInt64());
        Assert.Equal(0, ledgerEntry.RootElement
            .GetProperty("rateLimitTokenRemaining").GetInt64());
        Assert.Equal("2m15s", ledgerEntry.RootElement
            .GetProperty("rateLimitTokenReset").GetString());
        Assert.InRange(
            ledgerEntry.RootElement.GetProperty("retryAfterMilliseconds")
                .GetInt64(),
            1_799_000,
            1_800_000);
    }

    [Fact]
    public async Task External_provider_rejects_cleartext_non_https_endpoint()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = CreateOptions();
        options.Provider = "runpod-bench";
        options.LlmLocation = "external-service";
        options.LlmBaseUrl = "http://runpod.example/v1";
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            "runpod-secret");

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(),
                CancellationToken.None));

        Assert.Equal("advanced_external_llm_requires_https", error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task External_provider_rejects_missing_api_key_before_http_call()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = CreateOptions();
        options.Provider = "openai-dev";
        options.LlmLocation = "external-service";
        options.LlmBaseUrl = "https://api.openai.com/v1";
        options.LlmModel = "gpt-5.6-terra";
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            apiKey: null);

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(),
                CancellationToken.None));

        Assert.Equal("advanced_external_llm_api_key_missing", error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Provider_rejects_oversized_model_identity_without_truncation()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = CreateOptions();
        options.LlmModel = new string('m', 257);
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            apiKey: null);

        Assert.Equal(options.LlmModel, provider.ModelId);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(),
                CancellationToken.None));

        Assert.Equal("advanced_llm_model_invalid", error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Provider_rejects_oversized_provider_identity_without_truncation()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = CreateOptions();
        options.ProviderKey = new string('p', 101);
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            apiKey: null);

        Assert.Equal(options.ProviderKey, provider.ProviderKey);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(),
                CancellationToken.None));

        Assert.Equal("advanced_llm_provider_key_invalid", error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task External_profile_cannot_be_mislabeled_as_internal()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = CreateOptions();
        options.Provider = "openai-dev";
        options.LlmLocation = "internal";
        options.LlmBaseUrl = "https://api.openai.com/v1";
        options.LlmModel = "gpt-5.6-terra";
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            "openai-secret");

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(),
                CancellationToken.None));

        Assert.Equal(
            "advanced_llm_profile_location_mismatch",
            error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Internal_profile_cannot_be_mislabeled_as_external()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = CreateOptions();
        options.Provider = "customer-server";
        options.LlmLocation = "external-service";
        options.LlmBaseUrl = "https://llm.customer.example/v1";
        options.LlmModel = "qualified-model.gguf";
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            "internal-secret");

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                new RecordingToolGateway(),
                CancellationToken.None));

        Assert.Equal(
            "advanced_llm_profile_location_mismatch",
            error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public void OpenAi_budget_rejects_a_call_whose_reservation_exceeds_job_limit()
    {
        var options = CreateOptions();
        options.ExternalMaximumCostPerJobUsd = 0.0001m;
        var guard = new AdvancedAnalysisExternalBudgetGuard(
            options,
            "openai-dev",
            "gpt-5.6-terra");

        var error = Assert.Throws<AdvancedAnalysisProviderException>(() =>
            guard.Reserve(Guid.NewGuid(), "writer", 4_000, 4_000));

        Assert.Equal("advanced_external_budget_job_cost_limit", error.ErrorCode);
    }

    [Fact]
    public void OpenAi_budget_prices_the_reference_7000_input_1000_output_call()
    {
        var cost = AdvancedAnalysisExternalBudgetGuard.CalculateCost(
            inputTokens: 7_000,
            outputTokens: 1_000,
            cachedInputTokens: 0,
            CreateOptions());

        Assert.Equal(0.026m, cost);
    }

    [Fact]
    public void OpenAi_budget_keeps_per_job_call_limit_across_guard_restarts()
    {
        var options = CreateOptions();
        options.ExternalMaximumCallsPerJob = 1;
        var jobId = Guid.NewGuid();
        var first = new AdvancedAnalysisExternalBudgetGuard(
            options,
            "openai-dev",
            "gpt-5.6-terra");
        var reservation = first.Reserve(jobId, "planner", 100, 100);
        first.Complete(
            reservation,
            new AdvancedAnalysisLlmUsage(25, 10, 0));
        first.EndJob(jobId);

        var restarted = new AdvancedAnalysisExternalBudgetGuard(
            options,
            "openai-dev",
            "gpt-5.6-terra");
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() =>
            restarted.Reserve(jobId, "writer", 100, 100));

        Assert.Equal(
            "advanced_external_budget_job_call_limit",
            error.ErrorCode);
    }

    [Fact]
    public void OpenAi_budget_records_rejected_http_calls_without_reserved_cost()
    {
        var options = CreateOptions();
        var jobId = Guid.NewGuid();
        var guard = new AdvancedAnalysisExternalBudgetGuard(
            options,
            "openai-dev",
            "gpt-5.6-terra");
        var reservation = guard.Reserve(jobId, "writer", 28_000, 1_800);

        guard.Reject(reservation, "advanced_llm_http_429");
        guard.EndJob(jobId);

        using var entry = JsonDocument.Parse(
            Assert.Single(File.ReadAllLines(options.ExternalUsageLedgerPath)));
        Assert.Equal(0m, entry.RootElement.GetProperty("costUsd").GetDecimal());
        Assert.Equal("provider_http_rejected", entry.RootElement
            .GetProperty("usageSource").GetString());
        Assert.False(entry.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("advanced_llm_http_429", entry.RootElement
            .GetProperty("errorCode").GetString());
    }

    [Fact]
    public void OpenAi_budget_uses_provider_usage_for_a_billed_invalid_response()
    {
        var options = CreateOptions();
        var jobId = Guid.NewGuid();
        var guard = new AdvancedAnalysisExternalBudgetGuard(
            options,
            "openai-dev",
            "gpt-5.6-terra");
        var reservation = guard.Reserve(jobId, "writer", 28_000, 3_200);

        guard.Fail(
            reservation,
            "advanced_llm_content_missing",
            new AdvancedAnalysisLlmUsage(6_636, 1_800, 0));
        guard.EndJob(jobId);

        using var entry = JsonDocument.Parse(
            Assert.Single(File.ReadAllLines(options.ExternalUsageLedgerPath)));
        Assert.Equal(0.034872m,
            entry.RootElement.GetProperty("costUsd").GetDecimal());
        Assert.Equal("provider_usage", entry.RootElement
            .GetProperty("usageSource").GetString());
        Assert.False(entry.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("advanced_llm_content_missing", entry.RootElement
            .GetProperty("errorCode").GetString());
    }

    private static OpenAiCompatibleAdvancedAnalysisProvider CreateProvider(
        IHttpClientFactory factory,
        string? apiKey = null)
        => new(
            factory,
            CreateOptions(),
            apiKey);

    private static AdvancedAnalysisOptions CreateOptions()
        => new()
        {
            Provider = "customer-server",
            ProviderKey = "",
            LlmLocation = "internal",
            LlmBaseUrl = "http://advanced-llm:8080",
            LlmModel = "qualified-model.gguf",
            LlmTimeoutSeconds = 30,
            PlannerMaxTokens = 600,
            WriterMaxTokens = 2_000,
            MaximumPlanQueries = 8,
            MaximumEvidencePromptCharacters = 64_000,
            ExternalUsageLedgerPath = Path.Combine(
                Path.GetTempPath(),
                "saaia-advanced-test-" + Guid.NewGuid().ToString("N") + ".jsonl")
        };

    private static AdvancedAnalysisProviderRequest BuildRequest()
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-user",
            new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = Guid.NewGuid(),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                RequestText = "Construis un planning de repas 5 x 4 sourcé.",
                Language = "fr",
                OriginIntent = "structured_answer",
                ReasonCode = "advanced_capacity_required",
                TransferStage = "pre_retrieval",
                Load = new AdvancedAnalysisLoadDescriptor
                {
                    PlanKind = "bounded_grid",
                    Deliverable = "meal_plan",
                    AnswerUnitCount = 1,
                    AtomicEvidenceCount = 1,
                    RowCount = 5,
                    ColumnCount = 4,
                    StructuredLayout = true,
                    AtomicEvidenceType = "documented_preparation",
                    AtomicEvidenceMode = "one_per_cell",
                    SelectionPolicy = "distinct",
                    RowLabels = ["lundi", "mardi", "mercredi", "jeudi", "vendredi"],
                    Columns = ["petit-déjeuner", "déjeuner", "collation", "souper"]
                },
                ResearchState = new AdvancedAnalysisResearchState
                {
                    EvidenceRevalidationRequired = true,
                    MemoryIsEvidence = false
                }
            },
            [],
            []);

    private static AdvancedAnalysisProviderRequest BuildComparisonRequest()
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-user",
            new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = Guid.NewGuid(),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                RequestText = "Compare FD CEN/TR 15281:2023 avec IEC 60079-14:2013.",
                Language = "fr",
                OriginIntent = "structured_answer",
                ReasonCode = "advanced_capacity_required",
                TransferStage = "pre_retrieval",
                Load = new AdvancedAnalysisLoadDescriptor
                {
                    PlanKind = "comparison",
                    Deliverable = "comparaison des deux normes demandées",
                    AnswerUnitCount = 2,
                    AtomicEvidenceCount = 2,
                    AtomicEvidenceType = "comparative facts",
                    AtomicEvidenceMode = "one_per_document",
                    SelectionPolicy = "explicit_set",
                    RequestedDocumentName = "FD CEN TR 15281 2023"
                },
                ResearchState = new AdvancedAnalysisResearchState
                {
                    EvidenceRevalidationRequired = true,
                    MemoryIsEvidence = false
                }
            },
            [],
            []);

    private static AdvancedAnalysisProviderRequest BuildNamedDocumentRequest()
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-user",
            new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = Guid.NewGuid(),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                RequestText = "Extrais un point de NIST_CSF_2_0.pdf.",
                Language = "fr",
                OriginIntent = "structured_answer",
                ReasonCode = "advanced_capacity_required",
                TransferStage = "pre_retrieval",
                Load = new AdvancedAnalysisLoadDescriptor
                {
                    PlanKind = "multi_item",
                    Deliverable = "point documenté",
                    AnswerUnitCount = 1,
                    AtomicEvidenceCount = 1,
                    AtomicEvidenceType = "content_claim",
                    AtomicEvidenceMode = "one_per_item",
                    SelectionPolicy = "explicit_set",
                    RequestedDocumentName = "NIST_CSF_2_0.pdf",
                    BoundedNamedDocumentExtraction = true
                },
                ResearchState = new AdvancedAnalysisResearchState
                {
                    EvidenceRevalidationRequired = true,
                    MemoryIsEvidence = false
                }
            },
            [],
            []);

    private static AdvancedAnalysisResolvedEvidence BuildEvidence(
        string evidenceId,
        string content,
        string fileName = "menus.pdf",
        string? docId = null,
        string? revisionId = null,
        int pageStart = 1)
        => new(
            new AdvancedAnalysisResultEvidence
            {
                EvidenceId = evidenceId,
                DocId = docId ?? Guid.NewGuid().ToString("D"),
                RevisionId = revisionId ?? Guid.NewGuid().ToString("D"),
                FileName = fileName,
                DocPath = "Documents/" + fileName,
                SourceHash = "sha256:test",
                PageStart = pageStart,
                PageEnd = pageStart,
                ChunkId = Guid.NewGuid().ToString("D")
            },
            content);

    private static HttpResponseMessage Completion(
        string content,
        string? model = null)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                model,
                choices = new[]
                {
                    new { message = new { role = "assistant", content } }
                },
                usage = new
                {
                    prompt_tokens = 100,
                    completion_tokens = 50,
                    prompt_tokens_details = new { cached_tokens = 10 }
                }
            })
        };

    private static HttpResponseMessage CompletionStream(Exception exception)
        => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingReadStream(exception))
        };

    private static HttpResponseMessage RateLimited(
        TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(
            HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            retryAfter ?? TimeSpan.FromMilliseconds(1));
        response.Headers.TryAddWithoutValidation("x-request-id", "req-rate-limited");
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit-requests", "3");
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", "0");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset-requests", "21m30s");
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit-tokens", "10000");
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-tokens", "0");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset-tokens", "2m15s");
        return response;
    }

    private sealed class RecordingToolGateway : IAdvancedAnalysisToolGateway
    {
        private readonly IReadOnlyList<AdvancedAnalysisResolvedEvidence> _searchEvidence;
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = new();

        public RecordingToolGateway(
            params AdvancedAnalysisResolvedEvidence[] searchEvidence)
        {
            _searchEvidence = searchEvidence;
        }

        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = new();

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Searches.Add(request);
            foreach (var evidence in _searchEvidence)
            {
                if (_evidence.All(item => item.Reference.EvidenceId
                        != evidence.Reference.EvidenceId))
                {
                    _evidence.Add(evidence);
                }
            }
            return Task.FromResult(new AdvancedAnalysisSearchObservation(
                request.Query,
                _searchEvidence,
                [],
                1,
                Searches.Count));
        }
    }

    private sealed class DocumentAwareToolGateway(
        AdvancedAnalysisResolvedEvidence fdEvidence,
        AdvancedAnalysisResolvedEvidence iecEvidence) :
        IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];

        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence =>
            _evidence;

        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Searches.Add(request);
            var found = new List<AdvancedAnalysisResolvedEvidence>();
            if (request.Query.Contains("15281", StringComparison.Ordinal))
                found.Add(fdEvidence);
            if (request.Query.Contains("60079", StringComparison.Ordinal))
                found.Add(iecEvidence);
            foreach (var item in found)
            {
                if (_evidence.All(existing => existing.Reference.EvidenceId
                        != item.Reference.EvidenceId))
                    _evidence.Add(item);
            }
            return Task.FromResult(new AdvancedAnalysisSearchObservation(
                request.Query,
                found,
                [],
                1,
                Searches.Count));
        }
    }

    private sealed class QueuedHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;

        public QueuedHttpClientFactory(params HttpResponseMessage[] responses)
        {
            _client = new HttpClient(new QueuedHandler(responses, Requests));
        }

        public List<CapturedRequest> Requests { get; } = new();

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("advanced-analysis-llm", name);
            return _client;
        }

        public void Dispose() => _client.Dispose();

        private sealed class QueuedHandler(
            IEnumerable<HttpResponseMessage> responses,
            List<CapturedRequest> requests) : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _responses = new(responses);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                requests.Add(new CapturedRequest(
                    request.RequestUri!,
                    request.Headers.Authorization,
                    body));
                if (_responses.Count == 0)
                    throw new InvalidOperationException("No fake response remains.");
                return _responses.Dequeue();
            }
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
        string Body);

    private sealed class ThrowingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw exception;

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => Task.FromException<int>(exception);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(exception);

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
