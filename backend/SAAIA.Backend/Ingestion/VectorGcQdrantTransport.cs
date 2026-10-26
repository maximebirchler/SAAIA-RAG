using System.Net.Http.Headers;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SAAIA.Backend.Security;

/// <summary>Only exact IDs from an immutable manifest; never current-target substitution.</summary>
internal sealed class VectorGcQdrantTransport(IHttpClientFactory clients, IHostEnvironment environment,
    IOptions<RagOptions> ragOptions, IOptions<VectorGcOptions> gcOptions) : IVectorGcTransport
{
    public async Task<VectorGcBatchResult> CollectAsync(VectorGcPlan plan, IReadOnlyList<IngestionVectorPoint> points, CancellationToken ct)
    {
        var target = plan.Target;
        var options = gcOptions.Value;
        if (target.Key != plan.TargetKey || (options.RequireTargetRevision && string.IsNullOrWhiteSpace(target.Revision)))
            return new("blocked_target_unattested", false, 0, 0);
        var rag = ragOptions.Value;
        string? inlineKey = null;
        string? keyRef;
        string mode;
        bool allowUnauthenticated;
        if (options.AllowCurrentTarget && target == VectorTargetIdentity.From(rag))
        {
            inlineKey = rag.QdrantApiKey;
            keyRef = rag.QdrantApiKeyRef;
            mode = rag.QdrantAuthMode;
            allowUnauthenticated = !environment.IsProduction() || !rag.RequireQdrantAuthInProd;
        }
        else
        {
            var approved = options.ApprovedTargets.FirstOrDefault(t =>
                VectorTargetIdentity.Create(t.Endpoint, t.Collection, t.Revision) == target);
            if (approved is null) return new("blocked_target_unapproved", false, 0, 0);
            keyRef = approved.ApiKeyRef;
            mode = approved.AuthMode;
            allowUnauthenticated = approved.AllowUnauthenticated;
        }
        var key = SecretRefResolver.Resolve(inlineKey, keyRef, environment.ContentRootPath, out _);
        if (string.IsNullOrWhiteSpace(key) && !allowUnauthenticated)
            return new("blocked_target_credentials", false, 0, 0);
        using var client = clients.CreateClient("vector-gc");
        client.BaseAddress = new Uri(target.Endpoint);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove("api-key");
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (mode.Equals("bearer", StringComparison.OrdinalIgnoreCase) || mode.Equals("authorization", StringComparison.OrdinalIgnoreCase))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            else client.DefaultRequestHeaders.TryAddWithoutValidation("api-key", key.Trim());
        }
        return await CollectExactAsync(client, plan, points, ct);
    }

    internal static async Task<VectorGcBatchResult> CollectExactAsync(HttpClient client, VectorGcPlan plan,
        IReadOnlyList<IngestionVectorPoint> points, CancellationToken ct)
    {
        if (points.Count > 256 || points.Select(p => p.PointId).Distinct().Count() != points.Count
            || points.Any(p => p.PointId != IngestionVectorPlan.PointId(p.ChunkId, plan.Fingerprint)))
            return new("manifest_identity_mismatch", false, 0, 0);
        if (points.Count == 0) return new("absence_observed", true, 0, 0);
        var path = $"/collections/{Uri.EscapeDataString(plan.TargetCollection)}/points";
        var observation = await ReadVerifiedAsync(client, path, plan, points, ct);
        if (observation.Points is not { } found) return new(observation.Code, false, 0, 0);
        if (found.Length == 0) return new("absence_observed", true, points.Count, 0);
        using var request = new HttpRequestMessage(HttpMethod.Post, path + "/delete?wait=true")
        { Content = JsonContent.Create(new { points = found }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK) return new($"delete_http_{(int)response.StatusCode}", false, 0, 0);
        using var acknowledgement = await ReadBoundedJsonAsync(response, ct);
        if (!IsOk(acknowledgement.RootElement)
            || !acknowledgement.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "completed")
            return new("delete_not_completed", false, 0, 0);
        var remaining = await ReadVerifiedAsync(client, path, plan, points, ct);
        return remaining.Points is { Length: 0 }
            ? new(plan.PreviouslySwept ? "reappearance_removed" : "absence_observed", true, points.Count, found.Length)
            : new(remaining.Points is null ? remaining.Code : plan.PreviouslySwept ? "reappearance_not_verified" : "absence_not_verified", false, 0, found.Length);
    }

    private sealed record ReadObservation(Guid[]? Points, string Code);

    private static async Task<ReadObservation> ReadVerifiedAsync(HttpClient client, string path, VectorGcPlan plan,
        IReadOnlyList<IngestionVectorPoint> points, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new
            {
                ids = points.Select(p => p.PointId), with_vector = false,
                with_payload = new[] { "tenant_id", "doc_id", "ingestion_version", "ingestion_plan_fingerprint", "chunk_id" }
            })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK) return new(null, $"read_http_{(int)response.StatusCode}");
        using var json = await ReadBoundedJsonAsync(response, ct);
        if (!IsOk(json.RootElement) || !json.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array
            || result.GetArrayLength() > points.Count) return new(null, "read_response_invalid");
        var expected = points.ToDictionary(p => p.PointId, p => p.ChunkId);
        var found = new HashSet<Guid>();
        foreach (var point in result.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idValue.GetString(), out var id) || !expected.TryGetValue(id, out var chunk) || !found.Add(id)
                || !point.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || !GuidProperty(payload, "tenant_id", plan.TenantId) || !GuidProperty(payload, "doc_id", plan.DocId)
                || !GuidProperty(payload, "chunk_id", chunk)
                || !payload.TryGetProperty("ingestion_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var parsedVersion) || parsedVersion != plan.Generation
                || !payload.TryGetProperty("ingestion_plan_fingerprint", out var fp) || fp.ValueKind != JsonValueKind.String || fp.GetString() != plan.Fingerprint)
                return new(null, "point_identity_mismatch");
        }
        return new(found.ToArray(), "identity_verified");
    }

    private static bool GuidProperty(JsonElement payload, string name, Guid expected)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) && id == expected;

    private static bool IsOk(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "ok";

    private static async Task<JsonDocument> ReadBoundedJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("vector_gc_response_too_large");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("vector_gc_response_too_large");
            output.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(output.ToArray());
    }
}
