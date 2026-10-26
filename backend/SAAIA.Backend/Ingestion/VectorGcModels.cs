using System.Security.Cryptography;
using System.Text.Json;

internal sealed record VectorTargetIdentity(string Endpoint, string Collection, string Revision)
{
    public string Key => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { endpoint = Endpoint, collection = Collection, revision = Revision }))).ToLowerInvariant();

    public static VectorTargetIdentity Create(string endpoint, string collection, string? revision)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath.Trim('/') is not "" || string.IsNullOrWhiteSpace(collection) || collection.Length > 256)
            throw new InvalidOperationException("vector_target_identity_invalid");
        return new(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'), collection, revision ?? "");
    }

    public static VectorTargetIdentity From(RagOptions options)
        => Create(options.QdrantBaseUrl, options.QdrantCollection, options.QdrantRuntimeRevision);
}

internal sealed class VectorGcPlan
{
    public Guid PlanId { get; set; }
    public Guid TenantId { get; set; }
    public Guid DocId { get; set; }
    public int Generation { get; set; }
    public string Fingerprint { get; set; } = "";
    public string TargetKey { get; set; } = "";
    public string TargetEndpoint { get; set; } = "";
    public string TargetCollection { get; set; } = "";
    public string TargetRevision { get; set; } = "";
    public int PointCount { get; set; }
    public int Cursor { get; set; }
    public Guid Owner { get; set; }
    public bool PreviouslySwept { get; set; }
    public VectorTargetIdentity Target => VectorTargetIdentity.Create(TargetEndpoint, TargetCollection, TargetRevision);
}

internal sealed record VectorGcBatchResult(string Code, bool AbsenceVerified, int AbsentCount, int DeleteAcknowledgedCount);
internal sealed record VectorGcRoundResult(int RetiredPlans, int ClaimedPlans, int VerifiedAbsentPoints, int DeferredPlans);

internal interface IVectorGcTransport
{
    Task<VectorGcBatchResult> CollectAsync(VectorGcPlan plan, IReadOnlyList<IngestionVectorPoint> points, CancellationToken ct);
}
