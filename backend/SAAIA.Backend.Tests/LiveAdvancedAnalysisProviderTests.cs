using System.Diagnostics;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Backend.Tests;

public sealed class LiveAdvancedAnalysisProviderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Advanced_provider_builds_complete_synthetic_meal_grid_when_explicitly_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_RUN_ADVANCED_PROVIDER_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_RUN_ADVANCED_PROVIDER_TEST=1 for the explicitly paid provider probe.");
            return;
        }

        var providerMode = Require("SAAIA_ADVANCED_ANALYSIS_PROVIDER");
        var baseUrl = Require("SAAIA_ADVANCED_LLM_BASE_URL");
        var model = Require("SAAIA_ADVANCED_LLM_MODEL");
        var apiKey = Require("SAAIA_ADVANCED_LLM_API_KEY");
        var artifactDirectory = Require("SAAIA_ADVANCED_PROVIDER_ARTIFACT_DIR");
        Directory.CreateDirectory(artifactDirectory);
        var options = new AdvancedAnalysisOptions
        {
            Provider = providerMode,
            LlmBaseUrl = baseUrl,
            LlmModel = model,
            LlmApiKeyRef = "ENV:SAAIA_ADVANCED_LLM_API_KEY",
            ReasoningEffort = "low",
            LlmTimeoutSeconds = 600,
            PlannerMaxTokens = 1_200,
            WriterMaxTokens = 4_096,
            MaximumPlanQueries = 8,
            MaximumEvidencePromptCharacters = 120_000,
            ExternalBudgetAuthorizedUsd = 25m,
            ExternalBudgetSoftLimitUsd = 20m,
            ExternalBudgetHardLimitUsd = 24m,
            ExternalMaximumCostPerJobUsd = 0.50m,
            ExternalMaximumCallsPerJob = 4,
            ExternalInputUsdPerMillionTokens = 2m,
            ExternalCachedInputUsdPerMillionTokens = 0.2m,
            ExternalOutputUsdPerMillionTokens = 12m,
            ExternalUsageLedgerPath = Require(
                "SAAIA_ADVANCED_PROVIDER_LEDGER_PATH")
        };
        using var httpClientFactory = new LiveHttpClientFactory();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(
            httpClientFactory,
            options,
            apiKey);
        var evidence = BuildSyntheticMealEvidence();
        var gateway = new SyntheticEvidenceGateway(evidence);
        var request = BuildRequest();
        var stopwatch = Stopwatch.StartNew();
        AdvancedAnalysisProviderResult? result = null;
        string? error = null;
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
            stopwatch.Stop();
            await File.WriteAllTextAsync(
                Path.Combine(artifactDirectory, "result.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "saaia-advanced-provider-live-v1",
                    provider = provider.ProviderKey,
                    providerLocation = provider.Location.ToString(),
                    model,
                    elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    searches = gateway.Searches,
                    result,
                    error,
                    syntheticEvidenceOnly = true,
                    productStatus = "TESTE_NON_APPROUVE"
                }, new JsonSerializerOptions { WriteIndented = true }));
        }

        Assert.NotNull(result);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(2, result.ProviderCallCount);
        Assert.False(string.IsNullOrWhiteSpace(result.AnswerText));
        Assert.Contains("lundi", result.AnswerText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vendredi", result.AnswerText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("petit-déjeuner", result.AnswerText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("souper", result.AnswerText,
            StringComparison.OrdinalIgnoreCase);
        var cited = result.Claims
            .SelectMany(static claim => claim.EvidenceIds)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(20, cited.Count);
        Assert.All(evidence, item =>
            Assert.Contains(item.Reference.EvidenceId, cited));
        Assert.NotEmpty(gateway.Searches);
        output.WriteLine("Artifact: " + artifactDirectory);
    }

    private static AdvancedAnalysisProviderRequest BuildRequest()
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "synthetic-live-test",
            new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = Guid.NewGuid(),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                RequestText = "Construis un planning de repas du lundi au vendredi avec petit-déjeuner, déjeuner, collation et souper. Utilise exactement les vingt préparations documentées, une seule fois chacune, et cite chaque cellule. N'invente rien.",
                Language = "fr",
                OriginIntent = "rag.answer",
                ReasonCode = "structured_answer_units_at_or_above_6",
                TransferStage = "before_retrieval",
                Load = new AdvancedAnalysisLoadDescriptor
                {
                    PlanKind = "structured_layout",
                    Deliverable = "planning de repas sourcé",
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
                    MemoryIsEvidence = false,
                    EvidenceRevalidationRequired = true
                },
                DataPolicy = new AdvancedAnalysisDataPolicy
                {
                    ExternalProviderContentAuthorized = true,
                    ExternalProviderMetadataAuthorized = true,
                    AuthorizationSource = "explicit_synthetic_live_test"
                }
            },
            [],
            []);

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence>
        BuildSyntheticMealEvidence()
    {
        var names = new[]
        {
            "Porridge aux pommes", "Tartine de pois chiches", "Yaourt aux poires", "Omelette aux herbes",
            "Salade de lentilles", "Riz aux légumes", "Soupe de haricots", "Pâtes aux épinards",
            "Compote de prunes", "Noix et abricots", "Galette d'avoine", "Concombre au fromage blanc",
            "Gratin de courgettes", "Curry de pois cassés", "Polenta aux champignons", "Poivrons farcis",
            "Semoule aux carottes", "Quiche aux poireaux", "Dahl de lentilles corail", "Orge aux légumes"
        };
        return names.Select((name, index) =>
            new AdvancedAnalysisResolvedEvidence(
                new AdvancedAnalysisResultEvidence
                {
                    EvidenceId = $"E{index + 1:00}",
                    DocId = Guid.NewGuid().ToString("D"),
                    RevisionId = Guid.NewGuid().ToString("D"),
                    FileName = "synthetic-meals.md",
                    DocPath = "synthetic/synthetic-meals.md",
                    SourceHash = "sha256:synthetic-live-test",
                    PageStart = index + 1,
                    PageEnd = index + 1,
                    ChunkId = Guid.NewGuid().ToString("D")
                },
                $"Préparation documentée {index + 1:00} : {name}. Cette preuve autorise uniquement le nom exact de cette préparation."))
            .ToArray();
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(name + " is required.");
        return value.Trim();
    }

    private sealed class SyntheticEvidenceGateway(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> sourceEvidence)
        : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = new();
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = new();

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Searches.Add(request);
            foreach (var item in sourceEvidence)
            {
                if (_evidence.All(existing => existing.Reference.EvidenceId
                    != item.Reference.EvidenceId))
                {
                    _evidence.Add(item);
                }
            }
            return Task.FromResult(new AdvancedAnalysisSearchObservation(
                request.Query,
                sourceEvidence,
                [],
                1,
                Searches.Count));
        }
    }

    private sealed class LiveHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new();
        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }
}
