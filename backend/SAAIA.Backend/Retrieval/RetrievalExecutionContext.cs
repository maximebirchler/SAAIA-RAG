

namespace SAAIA.Backend.Retrieval;

/// <summary>
/// Per-execution identity and resources. Search has no dependency on an HTTP request;
/// HTTP and background jobs construct this context at their respective boundaries.
/// </summary>
internal sealed record RetrievalExecutionContext(
    Guid TenantId,
    string RequestId,
    CancellationToken CancellationToken,
    TeiWorkloadGovernor? TeiGovernor = null,
    ILogger? Logger = null);
