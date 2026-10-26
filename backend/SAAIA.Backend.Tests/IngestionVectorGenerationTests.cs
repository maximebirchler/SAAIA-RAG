using System.Net;
using System.Text.Json;
using SAAIA.Backend.Retrieval;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionVectorGenerationTests
{
    [Theory]
    [InlineData('a', "e593078c-d8f0-8882-09cc-8a4979a48b33")]
    [InlineData('b', "28b3c020-b07d-ba93-44df-2b053615a518")]
    public void Point_identity_has_a_fixed_interoperable_sha256_and_guid_byte_order(char planCharacter, string expected)
        => Assert.Equal(Guid.Parse(expected), IngestionVectorPlan.PointId(
            Guid.Parse("11111111-2222-3333-4444-555555555555"), new string(planCharacter, 64)));

    [Fact]
    public void A_late_old_plan_upsert_cannot_replace_the_active_point_or_change_its_canonical_citation()
    {
        var canonical = Guid.NewGuid();
        var oldPlan = new string('a', 64);
        var activePlan = new string('b', 64);
        var oldPoint = IngestionVectorPlan.PointId(canonical, oldPlan);
        var activePoint = IngestionVectorPlan.PointId(canonical, activePlan);
        Assert.NotEqual(oldPoint, activePoint);
        Assert.Equal(activePoint, IngestionVectorPlan.PointId(canonical, activePlan));
        // Qdrant upserts replace an existing point by its point ID. Deliver A after B's commit.
        var store = new Dictionary<Guid, object>();
        store[activePoint] = Point(activePoint, activePlan, "Maximum 20 bar.");
        store[oldPoint] = Point(oldPoint, oldPlan, "Maximum 10 bar.");
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(new { result = store.Values }));
        var parsed = QdrantClient.ParseSearchResults(wire);
        var selected = parsed.Where(match => RetrievalCandidateSignals.IsActiveDenseMatchForRevision(match, 3, "abcdef", activePlan)).ToArray();
        var accepted = Assert.Single(selected);
        Assert.Equal("Maximum 20 bar.", accepted.Text);
        Assert.Equal(canonical.ToString(), accepted.ChunkId);

        object Point(Guid id, string plan, string text) => new
        {
            id, score = .9,
            payload = new { chunk_id = canonical.ToString(), text, ingestion_version = 3, hash_doc = "abcdef", ingestion_plan_fingerprint = plan }
        };
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "active", false)]
    [InlineData("active", null, false)]
    [InlineData("old", "active", false)]
    [InlineData("active", "active", true)]
    public void An_active_plan_is_strict_and_legacy_null_is_not_a_wildcard(string? payloadPlan, string? activePlan, bool expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { result = new[]
        {
            new { score = .9, payload = new { chunk_id = Guid.NewGuid(), ingestion_version = 3,
                hash_doc = "abcdef", ingestion_plan_fingerprint = payloadPlan } }
        } }));
        var match = Assert.Single(QdrantClient.ParseSearchResults(json));
        Assert.Equal(expected, RetrievalCandidateSignals.IsActiveDenseMatchForRevision(match, 3, "abcdef", activePlan));
        Assert.False(RetrievalCandidateSignals.IsActiveDenseMatchForRevision(match, 4, "abcdef", activePlan));
        Assert.False(RetrievalCandidateSignals.IsActiveDenseMatchForRevision(match, 3, "different", activePlan));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("document")]
    [InlineData("path")]
    [InlineData("category")]
    [InlineData("version")]
    public void Plan_identity_covers_the_scope_metadata_actually_written_to_vector_payloads(string changed)
    {
        var scope = new IngestionVectorIdentity(Guid.NewGuid(), Guid.NewGuid(), "Knowledge/Control.pdf", "knowledge");
        var different = changed switch
        {
            "tenant" => scope with { TenantId = Guid.NewGuid() },
            "document" => scope with { DocumentId = Guid.NewGuid() },
            "path" => scope with { DocumentPath = "Other/Control.pdf" },
            "version" => scope with { Version = 1 },
            _ => scope with { Category = "other" }
        };
        var rag = new RagOptions();
        var inputs = new object[] { new { text = "Reliable source passage.", page = 1 } };
        Assert.NotEqual(IngestionVectorPlan.Fingerprint("source", rag, 3, "raw", inputs, scope),
            IngestionVectorPlan.Fingerprint("source", rag, 3, "raw", inputs, different));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Point_verification_checks_point_identity_and_canonical_chunk_identity_separately(bool wrongChunk, bool expected)
    {
        var chunk = Guid.NewGuid();
        var point = IngestionVectorPlan.PointId(chunk, "plan");
        using var handler = new PointHandler(point, wrongChunk ? point : chunk);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") };
        Assert.Equal(expected, await IngestionVectorPlan.HasCompletePointsAsync(client, "fixture",
            [new(point, chunk)], "plan", CancellationToken.None));
    }

    private sealed class PointHandler(Guid id, Guid chunk) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { result = new[] { new { id, payload = new { chunk_id = chunk, ingestion_plan_fingerprint = "plan" } } } })) });
    }
}
