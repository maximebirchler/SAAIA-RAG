using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionIntegrityTests
{
    [Fact]
    public async Task Every_extractor_can_read_the_same_snapshot_despite_replacing_the_original()
    {
        var directory = Path.Combine(Path.GetTempPath(), "saaia-source-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "input.pdf");
        string? snapshotPath = null;
        try
        {
            await File.WriteAllTextAsync(original, "Maximum pressure 10 bar.");
            await using (var snapshot = await IngestionSourceSnapshot.CreateAsync(original, CancellationToken.None))
            {
                snapshotPath = snapshot.Path;
                var native = await File.ReadAllTextAsync(snapshot.Path);
                await File.WriteAllTextAsync(original, "Maximum pressure 20 bar.");
                var docling = await File.ReadAllTextAsync(snapshot.Path);
                var ocr = await File.ReadAllTextAsync(snapshot.Path);
                Assert.Equal(native, docling);
                Assert.Equal(native, ocr);
                Assert.Contains("10 bar", ocr);
                Assert.Equal("input.pdf", Path.GetFileName(snapshot.Path));
                var failure = await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.AssertSourceUnchangedAsync(original, CancellationToken.None));
                Assert.Equal("source_changed_during_ingestion", failure.Message);
            }
            Assert.False(File.Exists(snapshotPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(snapshotPath)));
            Assert.True(File.Exists(original));
        }
        finally { File.Delete(original); Directory.Delete(directory); }
    }

    [Fact]
    public async Task Unchanged_source_matches_the_hash_of_the_exact_snapshot_bytes()
    {
        var original = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(original, "Interdit à −5 °C ; limite 1,5 bar.");
            await using var snapshot = await IngestionSourceSnapshot.CreateAsync(original, CancellationToken.None);
            await snapshot.AssertSourceUnchangedAsync(original, CancellationToken.None);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(original)), snapshot.Hash);
        }
        finally { File.Delete(original); }
    }

    [Fact]
    public void A_checkpoint_requires_identical_projected_content_target_and_model_revision()
    {
        var rag = new RagOptions { EmbeddingsModel = "model", EmbeddingsModelRevision = "rev1", QdrantCollection = "first" };
        var inputs = new object[] { new { text = "Maximum 1.5 bar", page = 1 } };
        var fingerprint = IngestionVectorPlan.Fingerprint("abc", rag, 3, "raw_passage_v1", inputs);
        var checkpoint = new JobRepo.ResumeCheckpointState { SourceHash = "abc", ChunkTotal = 2, ProgressTotal = 2,
            ProgressCurrent = 1, EmbeddingModel = "model", EmbeddingInputFormat = "raw_passage_v1", VectorPlanFingerprint = fingerprint };
        Assert.True(IngestionVectorPlan.CanResume(checkpoint, "abc", 2, "model", "raw_passage_v1", fingerprint));
        Assert.NotEqual(fingerprint, IngestionVectorPlan.Fingerprint("abc", rag, 3, "raw_passage_v1", [new { text = "Maximum 15 bar", page = 1 }]));
        rag.QdrantCollection = "second";
        var changedTarget = IngestionVectorPlan.Fingerprint("abc", rag, 3, "raw_passage_v1", inputs);
        Assert.False(IngestionVectorPlan.CanResume(checkpoint, "abc", 2, "model", "raw_passage_v1", changedTarget));
        rag.QdrantCollection = "first";
        rag.EmbeddingsModelRevision = "rev2";
        Assert.NotEqual(fingerprint, IngestionVectorPlan.Fingerprint("abc", rag, 3, "raw_passage_v1", inputs));
        rag.EmbeddingsModelRevision = "rev1";
        Assert.NotEqual(fingerprint, IngestionVectorPlan.Fingerprint("abc", rag, 4, "raw_passage_v1", inputs));
        checkpoint.VectorPlanFingerprint = null;
        Assert.False(IngestionVectorPlan.CanResume(checkpoint, "abc", 2, "model", "raw_passage_v1", fingerprint));
    }

    [Theory]
    [InlineData("model_revision")]
    [InlineData("collection")]
    [InlineData("ocr_language")]
    [InlineData("chunk_size")]
    [InlineData("docling_force_ocr")]
    [InlineData("ocr_timeout")]
    [InlineData("ocr_page_timeout")]
    public void Published_pipeline_identity_changes_when_unchanged_source_requires_reprocessing(string change)
    {
        var rag = new RagOptions();
        var ingestion = new IngestionOptions();
        var document = new DocumentIntelligenceOptions();
        var original = IngestionPipelineIdentity.Compute(rag, ingestion, document);
        Assert.Equal(original, IngestionPipelineIdentity.Compute(rag, ingestion, document));
        switch (change)
        {
            case "model_revision": rag.EmbeddingsModelRevision = "new-model-revision"; break;
            case "collection": rag.QdrantCollection = "new_collection"; break;
            case "ocr_language": ingestion.OcrLanguages = "fra"; break;
            case "chunk_size": ingestion.ChunkMaxWords = 170; break;
            case "docling_force_ocr": document.ForceOcr = true; break;
            case "ocr_timeout": ingestion.OcrTimeoutSeconds = 7200; break;
            case "ocr_page_timeout": ingestion.OcrImagePageTimeoutSeconds = 360; break;
        }
        Assert.NotEqual(original, IngestionPipelineIdentity.Compute(rag, ingestion, document));
        Assert.Equal(64, original.Length);
    }

    [Theory]
    [InlineData("complete", true)]
    [InlineData("missing", false)]
    [InlineData("duplicate", false)]
    [InlineData("stale-plan", false)]
    [InlineData("wrong-id", false)]
    [InlineData("missing-result", false)]
    public async Task Publication_verifies_each_acknowledged_point_instead_of_trusting_the_checkpoint(string mode, bool expected)
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        using var handler = new PointsHandler(ids, mode);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") };
        Assert.Equal(expected, await IngestionVectorPlan.HasCompletePointsAsync(client, "test_collection",
            ids.Select(id => new IngestionVectorPoint(id, id)).ToArray(), "plan", CancellationToken.None));
        Assert.Equal("/collections/test_collection/points", handler.Path);
    }

    private sealed class PointsHandler(Guid[] ids, string mode) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(ids.Length, body.RootElement.GetProperty("ids").GetArrayLength());
            var resultIds = mode switch { "missing" => ids.Take(1), "duplicate" => [ids[0], ids[0]], "wrong-id" => [ids[0], Guid.NewGuid()], _ => ids.AsEnumerable() };
            var json = mode == "missing-result" ? "{}" : JsonSerializer.Serialize(new { result = resultIds.Select(id => new
            {
                id, payload = new { chunk_id = id.ToString(), ingestion_plan_fingerprint = mode == "stale-plan" ? "old-plan" : "plan" }
            }) });
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
