using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Backend.Tests;

public sealed class LiveAdvancedAnalysisCriticReplayTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public async Task Terra_critic_audits_captured_a849_meal_candidate_when_explicitly_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_RUN_ADVANCED_CRITIC_REPLAY"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_RUN_ADVANCED_CRITIC_REPLAY=1 for the explicitly paid Critic replay.");
            return;
        }

        var fixturePath = Path.GetFullPath(Require("SAAIA_ADVANCED_CRITIC_REPLAY_FIXTURE"));
        var artifactDirectory = Path.GetFullPath(Require("SAAIA_ADVANCED_CRITIC_REPLAY_ARTIFACT_DIR"));
        Directory.CreateDirectory(artifactDirectory);
        var fixtureBytes = await File.ReadAllBytesAsync(fixturePath);
        var fixture = JsonSerializer.Deserialize<CriticReplayFixture>(fixtureBytes, JsonOptions)
            ?? throw new InvalidOperationException("The Critic replay fixture is invalid.");
        Assert.Equal("saaia.meal-critic-replay.v1", fixture.Schema);
        Assert.Equal(53, fixture.EvidenceCount);
        Assert.Equal(20, fixture.CandidateClaimCount);
        Assert.Equal(fixture.Source.WriterCandidateSha256,
            Sha256(fixture.WriterCandidateJson));
        Assert.Equal(fixture.Source.PlannerCompletionSha256,
            Sha256(fixture.PlannerCompletionJson));

        var ledgerPath = Path.GetFullPath(Require("SAAIA_ADVANCED_PROVIDER_LEDGER_PATH"));
        var options = new AdvancedAnalysisOptions
        {
            Provider = "openai-dev",
            LlmLocation = "external-service",
            LlmBaseUrl = "https://api.openai.com/v1",
            LlmModel = "gpt-5.6-terra",
            LlmApiKeyRef = "ENV:SAAIA_ADVANCED_LLM_API_KEY",
            ReasoningEffort = "low",
            SynthesisReasoningEffort = "high",
            SynthesisPromptStyle = "agent",
            LlmTimeoutSeconds = 600,
            LlmMaximumHttpAttempts = 1,
            PlannerMaxTokens = 512,
            WriterMaxTokens = 8_192,
            SemanticCriticEnabled = true,
            CriticMaxTokens = 8_192,
            MaximumPlanQueries = 8,
            AdaptiveResearchEnabled = true,
            NativeResearchToolsEnabled = true,
            NativeResearchApiProtocol = "responses",
            NativeResearchTopology = "agent",
            NativeResearchMaximumHistoryCharacters = 32_768,
            NativeResearchWorkspaceEnabled = true,
            NativeResearchActiveProposalEnabled = true,
            CandidateBindingFeedbackEnabled = true,
            MaximumEvidencePromptCharacters = 14_000,
            ExternalBudgetAuthorizedUsd = 40m,
            ExternalBudgetSoftLimitUsd = RequireDecimal(
                "SAAIA_ADVANCED_CRITIC_REPLAY_SOFT_LIMIT_USD"),
            ExternalBudgetHardLimitUsd = RequireDecimal(
                "SAAIA_ADVANCED_CRITIC_REPLAY_HARD_LIMIT_USD"),
            ExternalMaximumCostPerJobUsd = RequireDecimal(
                "SAAIA_ADVANCED_CRITIC_REPLAY_MAXIMUM_COST_USD"),
            ExternalMaximumCallsPerJob = RequireInt(
                "SAAIA_ADVANCED_CRITIC_REPLAY_MAXIMUM_CALLS"),
            ExternalInputUsdPerMillionTokens = 2m,
            ExternalCachedInputUsdPerMillionTokens = 0.20m,
            ExternalOutputUsdPerMillionTokens = 12m,
            ExternalUsageLedgerPath = ledgerPath,
            DevelopmentTraceDirectory = Path.Combine(artifactDirectory, "private-traces")
        };
        var request = BuildRequest(fixture);
        var evidence = BuildEvidence(fixture.Evidence);
        var replayPlannerJson = BuildReplayPlannerJson();
        var gateway = new ReplayEvidenceGateway(evidence, fixture.Evidence);
        using var factory = new CriticReplayHttpClientFactory(
            replayPlannerJson,
            fixture.WriterCandidateJson);
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            factory,
            options,
            Require("SAAIA_ADVANCED_LLM_API_KEY"));
        AdvancedAnalysisProviderResult? result = null;
        string? error = null;
        var startedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            result = await provider.ExecuteAsync(
                request,
                gateway,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name + ": " + exception.Message;
            throw;
        }
        finally
        {
            var candidate = JsonSerializer.Deserialize<CandidateResult>(
                fixture.WriterCandidateJson,
                JsonOptions);
            await File.WriteAllTextAsync(
                Path.Combine(artifactDirectory, "critic-replay-result.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "saaia.meal-critic-replay-result.v1",
                    startedAtUtc,
                    endedAtUtc = DateTimeOffset.UtcNow,
                    fixturePath,
                    fixtureSha256 = Sha256(fixtureBytes),
                    capturedPlannerSha256 = fixture.Source.PlannerCompletionSha256,
                    replayPlannerSha256 = Sha256(replayPlannerJson),
                    request.JobId,
                    syntheticPlannerCalls = factory.SyntheticPlannerCalls,
                    syntheticWriterCalls = factory.SyntheticWriterCalls,
                    liveProviderCalls = factory.LiveProviderCalls,
                    totalHttpCalls = factory.TotalCalls,
                    searches = gateway.Searches,
                    capturedCandidate = candidate,
                    result,
                    error,
                    externalContentAuthorized = true,
                    newPurchases = false,
                    productStatus = "TESTE_NON_APPROUVE"
                }, JsonOptions),
                Encoding.UTF8);
        }

        Assert.NotNull(result);
        Assert.Equal(1, factory.SyntheticPlannerCalls);
        Assert.Equal(1, factory.SyntheticWriterCalls);
        Assert.InRange(factory.LiveProviderCalls, 1,
            Math.Max(1, options.ExternalMaximumCallsPerJob - 2));
        Assert.InRange(result.ProviderCallCount, 3,
            options.ExternalMaximumCallsPerJob);
        Assert.False(string.IsNullOrWhiteSpace(result.AnswerText));
        output.WriteLine("Artifact: " + artifactDirectory);
    }

    private static AdvancedAnalysisProviderRequest BuildRequest(CriticReplayFixture fixture)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "a851-live-critic-replay",
            new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = Guid.NewGuid(),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                RequestText = fixture.RequestText,
                Language = fixture.Language,
                OriginIntent = "rag.answer",
                ReasonCode = "structured_answer_units_at_or_above_6",
                TransferStage = "before_retrieval",
                Load = fixture.Load,
                ResearchState = new AdvancedAnalysisResearchState
                {
                    MemoryIsEvidence = false,
                    EvidenceRevalidationRequired = true
                },
                DataPolicy = new AdvancedAnalysisDataPolicy
                {
                    ExternalProviderContentAuthorized = true,
                    ExternalProviderMetadataAuthorized = true,
                    AuthorizationSource = "explicit_user_authorization_all_purchased_credits_2026-09-18"
                }
            },
            [],
            []);

    private static string BuildReplayPlannerJson()
        => JsonSerializer.Serialize(new
        {
            selectionMode = "distinct_named_items",
            queries = new[]
            {
                new { query = "a851 replay candidats génériques", topK = 60 },
                new { query = "a851 replay recettes petit-déjeuner", topK = 60 },
                new { query = "a851 replay recettes déjeuner", topK = 60 },
                new { query = "a851 replay recettes collation", topK = 60 },
                new { query = "a851 replay recettes souper", topK = 60 }
            }
        }, JsonOptions);

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence> BuildEvidence(
        IReadOnlyList<FixtureEvidence> fixtureEvidence)
    {
        var sourceOverviews = fixtureEvidence
            .Where(item => item.SourceOverview is not null)
            .GroupBy(item => item.SourceKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().SourceOverview!,
                StringComparer.Ordinal);
        return fixtureEvidence.Select(item =>
        {
            var sourceSlug = item.SourceKey.Replace("internal-source-", "source-",
                StringComparison.Ordinal);
            var reference = new AdvancedAnalysisResultEvidence
            {
                EvidenceId = item.EvidenceId,
                DocId = "a849-" + sourceSlug,
                RevisionId = "a849-revision-" + sourceSlug,
                FileName = sourceSlug + ".pdf",
                DocPath = "private-a849/" + sourceSlug + ".pdf",
                SourceHash = "sha256:a849-replay-" + sourceSlug,
                PageStart = item.PhysicalPageStart,
                PageEnd = item.PhysicalPageEnd,
                ChunkId = item.EvidenceKind == "source_chunk"
                    ? "chunk-" + item.EvidenceId
                    : null,
                ContentCardId = item.EvidenceKind == "content_card"
                    ? "card-" + item.EvidenceId
                    : null
            };
            sourceOverviews.TryGetValue(item.SourceKey, out var overview);
            return new AdvancedAnalysisResolvedEvidence(
                reference,
                item.Content,
                item.CandidateTitle,
                overview is null
                    ? null
                    : new AdvancedAnalysisSourceOverview(
                        overview.FirstIndexedPhysicalPage,
                        overview.LastIndexedPhysicalPage,
                        overview.CanonicalChunkCount));
        }).ToArray();
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(name + " is required.");
        return value.Trim();
    }

    private static decimal RequireDecimal(string name)
        => decimal.Parse(Require(name), NumberStyles.Number,
            CultureInfo.InvariantCulture);

    private static int RequireInt(string name)
        => int.Parse(Require(name), NumberStyles.Integer,
            CultureInfo.InvariantCulture);

    private static string Sha256(string value)
        => Sha256(Encoding.UTF8.GetBytes(value));

    private static string Sha256(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class ReplayEvidenceGateway : IAdvancedAnalysisToolGateway
    {
        private readonly IReadOnlyList<AdvancedAnalysisResolvedEvidence> _sourceEvidence;
        private readonly IReadOnlyList<FixtureEvidence> _fixtureEvidence;
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];

        public ReplayEvidenceGateway(
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> sourceEvidence,
            IReadOnlyList<FixtureEvidence> fixtureEvidence)
        {
            _sourceEvidence = sourceEvidence;
            _fixtureEvidence = fixtureEvidence;
            Assert.Equal(_sourceEvidence.Count, _fixtureEvidence.Count);
        }

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Searches.Add(request);
            var requestedColumn = request.Query switch
            {
                "a851 replay recettes petit-déjeuner" => "Petit-déjeuner",
                "a851 replay recettes déjeuner" => "Déjeuner",
                "a851 replay recettes collation" => "Collation",
                "a851 replay recettes souper" => "Souper",
                _ => string.Empty
            };
            var found = _sourceEvidence.Where((_, index) =>
                requestedColumn.Length == 0
                    ? _fixtureEvidence[index].TargetColumns.Count == 0
                    : _fixtureEvidence[index].TargetColumns.Contains(
                        requestedColumn,
                        StringComparer.OrdinalIgnoreCase)).ToArray();
            foreach (var item in found)
            {
                if (_evidence.All(existing => existing.Reference.EvidenceId
                    != item.Reference.EvidenceId))
                {
                    _evidence.Add(item);
                }
            }
            return Task.FromResult(new AdvancedAnalysisSearchObservation(
                request.Query,
                found,
                [],
                1,
                Searches.Count));
        }
    }

    private sealed class CriticReplayHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly CriticReplayHandler _handler;
        private readonly HttpClient _client;

        public CriticReplayHttpClientFactory(string plannerJson, string writerJson)
        {
            _handler = new CriticReplayHandler(plannerJson, writerJson);
            _client = new HttpClient(_handler, disposeHandler: false);
        }

        public int TotalCalls => _handler.TotalCalls;
        public int SyntheticPlannerCalls => _handler.SyntheticPlannerCalls;
        public int SyntheticWriterCalls => _handler.SyntheticWriterCalls;
        public int LiveProviderCalls => _handler.LiveProviderCalls;
        public HttpClient CreateClient(string name) => _client;

        public void Dispose()
        {
            _client.Dispose();
            _handler.Dispose();
        }
    }

    private sealed class CriticReplayHandler(
        string plannerJson,
        string writerJson) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _live =
            new(new HttpClientHandler(), disposeHandler: true);
        private int _totalCalls;
        private int _syntheticPlannerCalls;
        private int _syntheticWriterCalls;
        private int _liveProviderCalls;

        public int TotalCalls => Volatile.Read(ref _totalCalls);
        public int SyntheticPlannerCalls => Volatile.Read(ref _syntheticPlannerCalls);
        public int SyntheticWriterCalls => Volatile.Read(ref _syntheticWriterCalls);
        public int LiveProviderCalls => Volatile.Read(ref _liveProviderCalls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _totalCalls);
            if (call == 1)
            {
                Interlocked.Increment(ref _syntheticPlannerCalls);
                return Task.FromResult(FakeChatCompletion(plannerJson));
            }
            if (call == 2)
            {
                Interlocked.Increment(ref _syntheticWriterCalls);
                return Task.FromResult(FakeNativeResponse(writerJson));
            }
            Interlocked.Increment(ref _liveProviderCalls);
            return _live.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _live.Dispose();
            base.Dispose(disposing);
        }

        private static HttpResponseMessage FakeChatCompletion(string content)
            => new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    model = "gpt-5.6-terra",
                    choices = new[]
                    {
                        new { message = new { role = "assistant", content } }
                    },
                    usage = new
                    {
                        prompt_tokens = 0,
                        completion_tokens = 0,
                        prompt_tokens_details = new
                        {
                            cached_tokens = 0,
                            cache_write_tokens = 0
                        }
                    }
                })
            };

        private static HttpResponseMessage FakeNativeResponse(string content)
            => new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    model = "gpt-5.6-terra",
                    status = "completed",
                    output = new[]
                    {
                        new
                        {
                            type = "message",
                            role = "assistant",
                            content = new[]
                            {
                                new { type = "output_text", text = content }
                            }
                        }
                    },
                    usage = new
                    {
                        input_tokens = 0,
                        output_tokens = 0,
                        input_tokens_details = new
                        {
                            cached_tokens = 0,
                            cache_write_tokens = 0
                        }
                    }
                })
            };
    }

    private sealed class CriticReplayFixture
    {
        public string Schema { get; init; } = string.Empty;
        public FixtureSource Source { get; init; } = new();
        public string RequestText { get; init; } = string.Empty;
        public string Language { get; init; } = "fr";
        public AdvancedAnalysisLoadDescriptor Load { get; init; } = new();
        public string PlannerCompletionJson { get; init; } = string.Empty;
        public string WriterCandidateJson { get; init; } = string.Empty;
        public List<FixtureEvidence> Evidence { get; init; } = [];
        public int EvidenceCount { get; init; }
        public int CandidateClaimCount { get; init; }
    }

    private sealed class FixtureSource
    {
        public string PlannerCompletionSha256 { get; init; } = string.Empty;
        public string WriterCandidateSha256 { get; init; } = string.Empty;
    }

    private sealed class FixtureEvidence
    {
        public string EvidenceId { get; init; } = string.Empty;
        public string SourceKey { get; init; } = string.Empty;
        public string EvidenceKind { get; init; } = string.Empty;
        public string? CandidateTitle { get; init; }
        public string Content { get; init; } = string.Empty;
        public List<string> TargetColumns { get; init; } = [];
        public int PhysicalPageStart { get; init; }
        public int PhysicalPageEnd { get; init; }
        public FixtureSourceOverview? SourceOverview { get; init; }
    }

    private sealed class FixtureSourceOverview
    {
        public int FirstIndexedPhysicalPage { get; init; }
        public int LastIndexedPhysicalPage { get; init; }
        public int CanonicalChunkCount { get; init; }
    }

    private sealed class CandidateResult
    {
        public string Outcome { get; init; } = string.Empty;
        public string AnswerText { get; init; } = string.Empty;
        public List<AdvancedAnalysisResultClaim> Claims { get; init; } = [];
    }
}
