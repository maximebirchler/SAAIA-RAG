using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public async Task Planner_search_and_writer_share_one_internal_provider_contract()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""
                {"queries":[{"query":"repas documentés petit-déjeuner","category":"Menus","topK":24}]}
                """),
            Completion("""
                {"outcome":"answered","answerText":"Lundi : porridge documenté.","claims":[{"claimId":"C1","text":"Le porridge est documenté.","evidenceIds":["E1"]}]}
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
        Assert.Equal("Menus", search.Category);
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
        Assert.DoesNotContain("server-secret", factory.Requests[0].Body,
            StringComparison.Ordinal);
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
                {"outcome":"answered","answerText":"Réponse forgée.","claims":[{"claimId":"C1","text":"Faux.","evidenceIds":["FORGED"]}]}
                """));
        var provider = CreateProvider(factory);
        var gateway = new RecordingToolGateway(
            BuildEvidence("E1", "Preuve réelle."));

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(
            () => provider.ExecuteAsync(
                BuildRequest(),
                gateway,
                CancellationToken.None));

        Assert.Equal("advanced_writer_protocol_invalid", error.ErrorCode);
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
    public async Task OpenAi_dev_uses_external_policy_identity_and_terra_dialect_without_corpus_metadata()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"repas","topK":20}]}"""),
            Completion("""
                {"outcome":"answered","answerText":"Réponse sourcée.","claims":[{"claimId":"C1","text":"Preuve.","evidenceIds":["E1"]}]}
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
                    AnswerUnitCount = 20,
                    AtomicEvidenceCount = 20,
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

    private static AdvancedAnalysisResolvedEvidence BuildEvidence(
        string evidenceId,
        string content)
        => new(
            new AdvancedAnalysisResultEvidence
            {
                EvidenceId = evidenceId,
                DocId = Guid.NewGuid().ToString("D"),
                RevisionId = Guid.NewGuid().ToString("D"),
                FileName = "menus.pdf",
                DocPath = "Nutrition/menus.pdf",
                SourceHash = "sha256:test",
                PageStart = 1,
                PageEnd = 1,
                ChunkId = Guid.NewGuid().ToString("D")
            },
            content);

    private static HttpResponseMessage Completion(string content)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
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

    private sealed class RecordingToolGateway : IAdvancedAnalysisToolGateway
    {
        private readonly AdvancedAnalysisResolvedEvidence? _searchEvidence;
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = new();

        public RecordingToolGateway(
            AdvancedAnalysisResolvedEvidence? searchEvidence = null)
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
            if (_searchEvidence is not null
                && _evidence.All(item => item.Reference.EvidenceId
                    != _searchEvidence.Reference.EvidenceId))
            {
                _evidence.Add(_searchEvidence);
            }
            return Task.FromResult(new AdvancedAnalysisSearchObservation(
                request.Query,
                _searchEvidence is null ? [] : [_searchEvidence],
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
}
