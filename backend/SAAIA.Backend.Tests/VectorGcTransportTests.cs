using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class VectorGcTransportTests
{
    [Trait("Profile", "Unit")]
    [Theory]
    [InlineData(401)]
    [InlineData(503)]
    [InlineData(404)]
    public async Task Exact_gc_reports_http_failure_separately_from_payload_identity(int status)
    {
        var (plan, point) = Fixture();
        var handler = new ScriptedHandler((_, _, _) => new((HttpStatusCode)status)
            { Content = new StringContent("private upstream body must not become a reason code") });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://controlled.invalid") };
        var result = await VectorGcQdrantTransport.CollectExactAsync(client, plan, [point], CancellationToken.None);
        Assert.Equal("read_http_" + status, result.Code);
        Assert.False(result.AbsenceVerified);
        Assert.Equal(1, handler.Calls);
    }

    [Trait("Profile", "Unit")]
    [Fact]
    public async Task Exact_gc_reappearance_is_observed_and_removed_on_a_later_sweep()
    {
        var (plan, point) = Fixture();
        plan.PreviouslySwept = true;
        var handler = new ScriptedHandler((_, _, step) => step == 2
            ? Reply(new { status = "ok", result = new { status = "completed" } }) : PointReply(plan, point, step == 1));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://controlled.invalid") };
        var result = await VectorGcQdrantTransport.CollectExactAsync(client, plan, [point], CancellationToken.None);
        Assert.True(result.AbsenceVerified);
        Assert.Equal("reappearance_removed", result.Code);
        Assert.Equal(3, handler.Calls);
    }

    [Trait("Profile", "Unit")]
    [Fact]
    public async Task Exact_gc_does_not_accept_http_accepted_as_a_terminal_delete()
    {
        var (plan, point) = Fixture();
        var handler = new ScriptedHandler((_, _, step) =>
        {
            if (step == 1) return PointReply(plan, point, true);
            var response = Reply(new { status = "ok", result = new { status = "completed" } });
            response.StatusCode = HttpStatusCode.Accepted;
            return response;
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://controlled.invalid") };
        var result = await VectorGcQdrantTransport.CollectExactAsync(client, plan, [point], CancellationToken.None);
        Assert.False(result.AbsenceVerified);
        Assert.Equal("delete_http_202", result.Code);
        Assert.Equal(2, handler.Calls);
    }

    [Trait("Profile", "Unit")]
    [Theory]
    [InlineData(206, false)]
    [InlineData(206, true)]
    [InlineData(202, false)]
    [InlineData(202, true)]
    public async Task Exact_gc_does_not_treat_partial_or_accepted_read_as_complete_absence(int status, bool readback)
    {
        var (plan, point) = Fixture();
        var handler = new ScriptedHandler((_, _, step) =>
        {
            if (!readback || step == 3)
            {
                var response = PointReply(plan, point, false);
                response.StatusCode = (HttpStatusCode)status;
                return response;
            }
            return step == 2 ? Reply(new { status = "ok", result = new { status = "completed" } }) : PointReply(plan, point, true);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://controlled.invalid") };
        var result = await VectorGcQdrantTransport.CollectExactAsync(client, plan, [point], CancellationToken.None);
        Assert.False(result.AbsenceVerified, "A partial/accepted HTTP read cannot establish complete absence, even with plausible JSON.");
    }

    [Trait("Profile", "Unit")]
    [Fact]
    public async Task Exact_gc_deletes_only_registered_ids_and_verifies_absence()
    {
        var (plan, point) = Fixture();
        var handler = new ScriptedHandler((request, body, step) =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(HttpMethod.Post, request.Method);
            if (step == 2)
            {
                Assert.Equal("/collections/owned/points/delete?wait=true", request.RequestUri!.PathAndQuery);
                Assert.Equal(new[] { "points" }, json.RootElement.EnumerateObject().Select(p => p.Name));
                Assert.Equal(point.PointId, json.RootElement.GetProperty("points")[0].GetGuid());
                return Reply(new { status = "ok", result = new { status = "completed" } });
            }
            Assert.Equal("/collections/owned/points", request.RequestUri!.AbsolutePath);
            Assert.False(json.RootElement.GetProperty("with_vector").GetBoolean());
            return PointReply(plan, point, step == 1);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://controlled.invalid") };
        var result = await VectorGcQdrantTransport.CollectExactAsync(client, plan, [point], CancellationToken.None);
        Assert.True(result.AbsenceVerified);
        Assert.Equal(1, result.AbsentCount);
        Assert.Equal(1, result.DeleteAcknowledgedCount);
        Assert.Equal(3, handler.Calls);
    }

    [Trait("Profile", "Unit")]
    [Theory]
    [InlineData("tenant_id")]
    [InlineData("doc_id")]
    [InlineData("chunk_id")]
    [InlineData("ingestion_version")]
    [InlineData("ingestion_plan_fingerprint")]
    [InlineData("unexpected_id")]
    [InlineData("duplicate")]
    public async Task Exact_gc_quarantines_identity_mismatch_without_sending_delete(string field)
    {
        var (plan, point) = Fixture();
        var handler = new ScriptedHandler((_, _, _) => PointReply(plan, point, true, field));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://controlled.invalid") };
        var result = await VectorGcQdrantTransport.CollectExactAsync(client, plan, [point], CancellationToken.None);
        Assert.False(result.AbsenceVerified);
        Assert.Equal(field == "duplicate" ? "read_response_invalid" : "point_identity_mismatch", result.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Trait("Profile", "Unit")]
    [Theory]
    [InlineData("acknowledged", false, "delete_not_completed", 2)]
    [InlineData("completed", true, "absence_not_verified", 3)]
    public async Task Exact_gc_does_not_confuse_acknowledgement_with_absence(string status, bool remains, string code, int calls)
    {
        var (plan, point) = Fixture();
        var handler = new ScriptedHandler((_, _, step) => step == 2
            ? Reply(new { status = "ok", result = new { status } }) : PointReply(plan, point, step == 1 || remains));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://controlled.invalid") };
        var result = await VectorGcQdrantTransport.CollectExactAsync(client, plan, [point], CancellationToken.None);
        Assert.False(result.AbsenceVerified);
        Assert.Equal(code, result.Code);
        Assert.Equal(calls, handler.Calls);
    }

    [Trait("Profile", "Unit")]
    [Fact]
    public async Task Exact_gc_observes_already_absent_ids_without_a_delete()
    {
        var (plan, point) = Fixture();
        var handler = new ScriptedHandler((_, _, _) => PointReply(plan, point, false));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://controlled.invalid") };
        var result = await VectorGcQdrantTransport.CollectExactAsync(client, plan, [point], CancellationToken.None);
        Assert.True(result.AbsenceVerified);
        Assert.Equal(0, result.DeleteAcknowledgedCount);
        Assert.Equal(1, handler.Calls);
    }

    [Trait("Profile", "Unit")]
    [Fact]
    public async Task Exact_gc_never_creates_a_missing_collection()
    {
        var (plan, point) = Fixture();
        var handler = new ScriptedHandler((_, _, _) => new(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://controlled.invalid") };
        var result = await VectorGcQdrantTransport.CollectExactAsync(client, plan, [point], CancellationToken.None);
        Assert.False(result.AbsenceVerified);
        Assert.Equal(1, handler.Calls);
    }

    [Trait("Profile", "Unit")]
    [Fact]
    public async Task Exact_gc_rejects_unregistered_physical_id_without_network()
    {
        var (plan, point) = Fixture();
        var handler = new ScriptedHandler((_, _, _) => throw new InvalidOperationException("No request expected"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://controlled.invalid") };
        var result = await VectorGcQdrantTransport.CollectExactAsync(client, plan, [new(Guid.NewGuid(), point.ChunkId)], CancellationToken.None);
        Assert.False(result.AbsenceVerified);
        Assert.Equal(0, handler.Calls);
    }

    [Trait("Profile", "Unit")]
    [Fact]
    public async Task Historical_target_is_not_redirected_to_current_config_or_its_credentials()
    {
        var (plan, point) = Fixture();
        var factory = new SingleFactory(new ScriptedHandler((_, _, _) => throw new InvalidOperationException("No request expected")));
        var transport = new VectorGcQdrantTransport(factory, new EnvironmentStub(), Options.Create(new RagOptions
        { QdrantBaseUrl = "http://other.invalid", QdrantCollection = "new", QdrantRuntimeRevision = "new", QdrantApiKey = "synthetic-current-key" }),
            Options.Create(new VectorGcOptions()));
        var result = await transport.CollectAsync(plan, [point], CancellationToken.None);
        Assert.Equal("blocked_target_unapproved", result.Code);
        Assert.Equal(0, factory.Created);
    }

    [Trait("Profile", "Unit")]
    [Fact]
    public async Task Approved_historical_target_uses_its_own_binding_and_no_current_inline_key()
    {
        var (plan, point) = Fixture();
        var handler = new ScriptedHandler((request, _, _) =>
        {
            Assert.Equal("controlled.invalid", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("api-key"));
            return PointReply(plan, point, false);
        });
        var factory = new SingleFactory(handler);
        var transport = new VectorGcQdrantTransport(factory, new EnvironmentStub(), Options.Create(new RagOptions
        { QdrantBaseUrl = "http://other.invalid", QdrantApiKey = "synthetic-current-key" }), Options.Create(new VectorGcOptions
        {
            ApprovedTargets = [new() { Endpoint = plan.TargetEndpoint, Collection = plan.TargetCollection,
                Revision = plan.TargetRevision, AllowUnauthenticated = true }]
        }));
        Assert.True((await transport.CollectAsync(plan, [point], CancellationToken.None)).AbsenceVerified);
        Assert.Equal(1, factory.Created);
    }

    [Trait("Profile", "Unit")]
    [Theory]
    [InlineData("http://name:secret@controlled.invalid")]
    [InlineData("http://controlled.invalid?key=secret")]
    [InlineData("http://controlled.invalid/#fragment")]
    [InlineData("file:///tmp/collection")]
    public void Target_identity_rejects_ambiguous_or_credential_bearing_urls(string endpoint)
        => Assert.Throws<InvalidOperationException>(() => VectorTargetIdentity.Create(endpoint, "owned", "v1"));

    internal static (VectorGcPlan Plan, IngestionVectorPoint Point) Fixture()
    {
        var target = VectorTargetIdentity.Create("http://controlled.invalid", "owned", "qualified-runtime-v1");
        var plan = new VectorGcPlan { PlanId = Guid.NewGuid(), TenantId = Guid.NewGuid(), DocId = Guid.NewGuid(), Generation = 1,
            Fingerprint = new string('a', 64), TargetEndpoint = target.Endpoint, TargetCollection = target.Collection,
            TargetRevision = target.Revision, TargetKey = target.Key, PointCount = 1 };
        var chunk = Guid.NewGuid();
        return (plan, new(IngestionVectorPlan.PointId(chunk, plan.Fingerprint), chunk));
    }

    internal static HttpResponseMessage PointReply(VectorGcPlan plan, IngestionVectorPoint point, bool present, string? corrupt = null)
    {
        var payload = new Dictionary<string, object> { ["tenant_id"] = plan.TenantId, ["doc_id"] = plan.DocId,
            ["chunk_id"] = point.ChunkId, ["ingestion_version"] = plan.Generation, ["ingestion_plan_fingerprint"] = plan.Fingerprint };
        if (corrupt is not null && payload.ContainsKey(corrupt)) payload[corrupt] = "wrong-identity";
        var entry = new { id = corrupt == "unexpected_id" ? Guid.NewGuid() : point.PointId, payload };
        object[] entries = !present ? [] : corrupt == "duplicate" ? [entry, entry] : [entry];
        return Reply(new { status = "ok", result = entries });
    }

    internal static HttpResponseMessage Reply(object value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    internal sealed class ScriptedHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct), ++Calls);
    }

    private sealed class SingleFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public int Created { get; private set; }
        public HttpClient CreateClient(string name) { Created++; Assert.Equal("vector-gc", name); return new(handler, false); }
    }

    private sealed class EnvironmentStub : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "controlled";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
