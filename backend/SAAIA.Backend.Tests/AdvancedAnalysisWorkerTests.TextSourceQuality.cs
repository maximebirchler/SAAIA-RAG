using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Models;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [InlineData("find_source_text")]
    [InlineData("read_source")]
    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    public async Task Text_source_quality_from_actual_sql_metadata_survives_resolver_and_canonical_tools(string operation)
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");
        const string content = "Observed guide\nHold below 17 bar before opening the documented assembly.";
        var seed = await database.SeedEvidenceAsync("quality-text-source", content);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                UPDATE retrieval_chunks SET metadata = @metadata::jsonb
                WHERE tenant_id=@tenant AND retrieval_chunk_id=@chunk;
                """, new { tenant = seed.TenantId, chunk = seed.ChunkId, metadata = JsonSerializer.Serialize(new
                {
                    extractionTextStatus = "ok", extractionOcrCandidate = false,
                    extractionQualitySignals = new[] { "regional_text_repair_inconclusive", "figure_semantics_not_extracted" }
                }) });
        }
        var settings = BuildAdvancedOptions();
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(settings));
        var initial = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { DocId = seed.DocId.ToString(), ChunkId = seed.ChunkId.ToString() }], CancellationToken.None);
        Assert.True(initial.IsValid);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var httpFactory = new UnavailableHttpClientFactory();
        var rag = Options.Create(BuildRagOptions());
        var gateway = new AdvancedAnalysisToolGateway(Guid.NewGuid(), seed.TenantId, initial.Evidence,
            database.DataSource, rag.Value, httpFactory, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance), resolver, settings);

        var observation = await gateway.SearchAsync(new AdvancedAnalysisSearchRequest("17 bar", TopK: 1,
            DocId: seed.DocId.ToString(), DocPath: seed.DocPath, RevisionId: seed.RevisionId.ToString(),
            Operation: operation, PageStart: operation == "read_source" ? 2 : null,
            PageEnd: operation == "read_source" ? 2 : null), CancellationToken.None);

        foreach (var evidence in new[] { Assert.Single(initial.Evidence), Assert.Single(observation.Evidence), Assert.Single(gateway.Evidence) })
        {
            Assert.Equal(content, evidence.Content);
            using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var quality = serialized.RootElement.GetProperty("sourceQuality");
            Assert.Equal("ok", quality.GetProperty("textStatus").GetString());
            Assert.False(quality.GetProperty("ocrCandidate").GetBoolean());
            Assert.Equal("not_verified", quality.GetProperty("semanticAccuracy").GetString());
            Assert.Equal(new[] { "regional_text_repair_inconclusive", "figure_semantics_not_extracted" },
                quality.GetProperty("signals").EnumerateArray().Select(item => item.GetString()).ToArray());
        }
    }
}
