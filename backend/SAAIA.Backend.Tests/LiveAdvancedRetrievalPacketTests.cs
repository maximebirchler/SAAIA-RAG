using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Auth;
using SAAIA.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Backend.Tests;

public sealed class LiveAdvancedRetrievalPacketTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Advanced_meal_searches_expose_revalidated_content_cards_when_explicitly_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_RUN_ADVANCED_RETRIEVAL_PACKET_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_RUN_ADVANCED_RETRIEVAL_PACKET_TEST=1 for the read-only live retrieval probe.");
            return;
        }

        var artifactDirectory = Require(
            "SAAIA_ADVANCED_RETRIEVAL_ARTIFACT_DIR");
        Directory.CreateDirectory(artifactDirectory);
        await using var dataSource = NpgsqlDataSource.Create(
            Require("SAAIA_ADVANCED_RETRIEVAL_DATABASE"));
        var tenantId = await ResolveTenantIdAsync(dataSource);
        using var httpFactory = new LiveHttpClientFactory(
            Require("SAAIA_ADVANCED_RETRIEVAL_QDRANT_KEY"));
        using var services = new ServiceCollection().BuildServiceProvider();
        var rag = new RagOptions
        {
            QdrantBaseUrl = Require("SAAIA_ADVANCED_RETRIEVAL_QDRANT_URL"),
            QdrantCollection = "knowledge_base",
            QdrantApiKey = Require("SAAIA_ADVANCED_RETRIEVAL_QDRANT_KEY"),
            EmbeddingsBaseUrl = Require("SAAIA_ADVANCED_RETRIEVAL_EMBEDDINGS_URL"),
            EmbeddingsModel = Require("SAAIA_ADVANCED_RETRIEVAL_EMBEDDINGS_MODEL"),
            EnableRerank = false,
            DefaultTopK = 20,
            MaxTopK = 60,
            SearchMaxConcurrency = 2,
            SearchQueueLimit = 4,
            SearchDenseEmbeddingTimeoutSeconds = 30,
            SearchSparseCommandTimeoutSeconds = 30
        };
        var advanced = new AdvancedAnalysisOptions
        {
            MaximumToolCalls = 8,
            MaximumSearchTopK = 60,
            MaximumAccumulatedEvidenceItems = 256,
            MaximumEvidenceCharactersPerItem = 24_000,
            MaximumEvidenceCharactersTotal = 512_000,
            MaximumToolElapsedMilliseconds = 300_000
        };
        var gateway = new AdvancedAnalysisToolGateway(
            Guid.NewGuid(),
            tenantId,
            [],
            dataSource,
            rag,
            httpFactory,
            services,
            new RagSearchBulkhead(
                Options.Create(rag),
                NullLogger<RagSearchBulkhead>.Instance),
            new AdvancedAnalysisEvidenceResolver(
                dataSource,
                Options.Create(advanced)),
            advanced);
        var queries = new[]
        {
            "recettes petit-déjeuner déjeuner du matin brunch noms titres",
            "recettes déjeuner midi repas noms titres",
            "recettes collation encas pause noms titres",
            "recettes souper dîner repas du soir noms titres"
        };
        var categories = await gateway.ListCategoriesAsync(
            CancellationToken.None);
        var observations = new List<AdvancedAnalysisSearchObservation>();
        foreach (var query in queries)
        {
            observations.Add(await gateway.SearchAsync(
                new AdvancedAnalysisSearchRequest(
                    query,
                    Category: "Cuisine",
                    TopK: 20,
                    MaxPerDocument: 8,
                    MaxPerPage: 2),
                CancellationToken.None));
        }

        var artifactPath = Path.Combine(
            artifactDirectory,
            "retrieval-packet.private.json");
        await File.WriteAllTextAsync(
            artifactPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "saaia-advanced-retrieval-packet-v1",
                capturedAtUtc = DateTimeOffset.UtcNow,
                readOnly = true,
                availableCategories = categories,
                queries = observations.Select(observation => new
                {
                    observation.Query,
                    observation.ToolCallNumber,
                    observation.ElapsedMilliseconds,
                    evidenceCount = observation.Evidence.Count,
                    contentCardCount = observation.Evidence.Count(item =>
                        !string.IsNullOrWhiteSpace(item.Reference.ContentCardId)),
                    sourceChunkCount = observation.Evidence.Count(item =>
                        !string.IsNullOrWhiteSpace(item.Reference.ChunkId)),
                    evidence = observation.Evidence.Select(item => new
                    {
                        kind = !string.IsNullOrWhiteSpace(
                            item.Reference.ContentCardId)
                            ? "content_card"
                            : "source_chunk",
                        reference = item.Reference,
                        item.Content
                    })
                })
            }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.All(observations, observation =>
            Assert.NotEmpty(observation.Evidence));
        Assert.Contains("Cuisine", categories, StringComparer.OrdinalIgnoreCase);
        Assert.True(observations.Sum(observation =>
                observation.Evidence.Count(item =>
                    !string.IsNullOrWhiteSpace(item.Reference.ContentCardId))) > 0,
            "The live canonical retrieval packet did not expose any revalidated content card.");
        output.WriteLine("Artifact: " + artifactPath);
    }

    [Fact]
    public async Task Advanced_job_audit_exports_revalidated_tool_history_when_explicitly_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_RUN_ADVANCED_JOB_AUDIT_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_RUN_ADVANCED_JOB_AUDIT_TEST=1 for the read-only live job audit.");
            return;
        }

        var artifactDirectory = Require(
            "SAAIA_ADVANCED_RETRIEVAL_ARTIFACT_DIR");
        Directory.CreateDirectory(artifactDirectory);
        await using var dataSource = NpgsqlDataSource.Create(
            Require("SAAIA_ADVANCED_RETRIEVAL_DATABASE"));
        var tenantId = await ResolveTenantIdAsync(dataSource);
        var jobId = Guid.Parse(Require("SAAIA_ADVANCED_JOB_AUDIT_JOB_ID"));
        var options = new AdvancedAnalysisOptions
        {
            MaximumEvidenceCharactersPerItem = 24_000,
            MaximumEvidenceCharactersTotal = 512_000
        };
        var resolver = new AdvancedAnalysisEvidenceResolver(
            dataSource,
            Options.Create(options));
        var history = await new AdvancedAnalysisJobStore(dataSource)
            .LoadToolHistoryAsync(tenantId, jobId, CancellationToken.None);
        var auditedEvents = new List<object>();
        foreach (var toolEvent in history)
        {
            var references = toolEvent.Evidence
                .Select(ToEvidenceReference)
                .ToArray();
            var resolution = await resolver.ResolveAsync(
                tenantId,
                references,
                CancellationToken.None);
            Assert.True(resolution.IsValid, resolution.ErrorCode);
            var rankedEvidence = OpenAiCompatibleAdvancedAnalysisProvider
                .OrderEvidenceForQuery(
                    toolEvent.Request.Query,
                    toolEvent.Request.DocumentHint,
                    resolution.Evidence);
            auditedEvents.Add(new
            {
                toolEvent.EventSequence,
                toolEvent.AttemptCount,
                toolEvent.ToolName,
                toolEvent.Status,
                request = toolEvent.Request,
                toolEvent.DegradedRetrievers,
                toolEvent.ElapsedMilliseconds,
                toolEvent.ErrorCode,
                evidence = resolution.Evidence.Select(item => new
                {
                    reference = item.Reference,
                    item.ExactTitle,
                    item.Content
                }),
                rankedEvidenceIds = rankedEvidence.Select(item =>
                    item.Reference.EvidenceId)
            });
        }

        await using var resultCommand = dataSource.CreateCommand("""
            SELECT handoff::text, result::text, status, last_error_code
            FROM advanced_analysis_jobs
            WHERE tenant_id = $1 AND job_id = $2
            LIMIT 1;
            """);
        resultCommand.Parameters.AddWithValue(tenantId);
        resultCommand.Parameters.AddWithValue(jobId);
        await using var resultReader = await resultCommand.ExecuteReaderAsync();
        Assert.True(await resultReader.ReadAsync());
        var handoffJson = resultReader.GetString(0);
        var resultJson = resultReader.IsDBNull(1)
            ? null
            : resultReader.GetString(1);
        var jobStatus = resultReader.GetString(2);
        var lastErrorCode = resultReader.IsDBNull(3)
            ? null
            : resultReader.GetString(3);
        using var handoffDocument = JsonDocument.Parse(handoffJson);
        using var resultDocument = string.IsNullOrWhiteSpace(resultJson)
            ? null
            : JsonDocument.Parse(resultJson);
        var artifactPath = Path.Combine(
            artifactDirectory,
            "advanced-job-audit.private.json");
        await File.WriteAllTextAsync(
            artifactPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "saaia-advanced-job-audit-v1",
                capturedAtUtc = DateTimeOffset.UtcNow,
                readOnly = true,
                jobId,
                jobStatus,
                lastErrorCode,
                handoff = handoffDocument.RootElement,
                toolEvents = auditedEvents,
                result = resultDocument?.RootElement
            }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.NotEmpty(history);
        Assert.All(history, item => Assert.Equal("succeeded", item.Status));
        output.WriteLine("Artifact: " + artifactPath);
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(name + " is required.");
        return value.Trim();
    }

    private static async Task<Guid> ResolveTenantIdAsync(
        NpgsqlDataSource dataSource)
    {
        var configured = Environment.GetEnvironmentVariable(
            "SAAIA_ADVANCED_RETRIEVAL_TENANT_ID");
        if (!string.IsNullOrWhiteSpace(configured))
            return Guid.Parse(configured.Trim());

        var apiKey = Environment.GetEnvironmentVariable(
            "SAAIA_ADVANCED_RETRIEVAL_API_KEY");
        var pepper = Environment.GetEnvironmentVariable(
            "SAAIA_ADVANCED_RETRIEVAL_AUTH_PEPPER");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await using var principalCommand = dataSource.CreateCommand("""
                SELECT tenant_id
                FROM api_keys
                WHERE revoked_at IS NULL
                  AND key_prefix = $1
                  AND key_hash = $2
                LIMIT 1;
                """);
            principalCommand.Parameters.AddWithValue(ApiKeyAuth.Prefix(apiKey));
            principalCommand.Parameters.AddWithValue(
                ApiKeyAuth.Sha256Bytes(apiKey, pepper));
            var principalTenant = await principalCommand.ExecuteScalarAsync();
            if (principalTenant is Guid principalTenantId)
                return principalTenantId;
            throw new InvalidOperationException(
                "The live probe API key does not resolve to an active tenant.");
        }

        await using var command = dataSource.CreateCommand("""
            SELECT d.tenant_id
            FROM documents d
            JOIN document_revisions dr
              ON dr.tenant_id = d.tenant_id
             AND dr.doc_id = d.doc_id
             AND dr.indexed_version = d.indexed_version
            JOIN retrieval_chunks rc
              ON rc.tenant_id = dr.tenant_id
             AND rc.revision_id = dr.revision_id
            WHERE d.status = 'indexed'
              AND lower(d.category) = 'cuisine'
            GROUP BY d.tenant_id
            ORDER BY COUNT(*) DESC
            LIMIT 1;
            """);
        var resolved = await command.ExecuteScalarAsync();
        if (resolved is Guid tenantId)
            return tenantId;
        throw new InvalidOperationException(
            "No indexed Cuisine tenant was found for the live probe.");
    }

    private static AdvancedAnalysisEvidenceReference ToEvidenceReference(
        AdvancedAnalysisResultEvidence source)
        => new()
        {
            EvidenceId = source.EvidenceId,
            DocId = source.DocId,
            RevisionId = source.RevisionId,
            FileName = source.FileName,
            DocPath = source.DocPath,
            SourceHash = source.SourceHash,
            PageStart = source.PageStart,
            PageEnd = source.PageEnd,
            ChunkId = source.ChunkId,
            AnchorId = source.AnchorId,
            ContentCardId = source.ContentCardId
        };

    private sealed class LiveHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly ConcurrentBag<HttpClient> _clients = [];
        private readonly string _qdrantApiKey;

        public LiveHttpClientFactory(string qdrantApiKey)
        {
            _qdrantApiKey = qdrantApiKey;
        }

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient();
            if (string.Equals(name, "qdrant", StringComparison.Ordinal))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "api-key",
                    _qdrantApiKey);
            }
            _clients.Add(client);
            return client;
        }

        public void Dispose()
        {
            foreach (var client in _clients)
                client.Dispose();
        }
    }
}
