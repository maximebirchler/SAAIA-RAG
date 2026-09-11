namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

/// <summary>
/// Mechanical outcome of observing the current backend catalog for a document
/// reference explicitly named by the semantic orchestrator.
/// </summary>
public enum SourceBackedDocumentResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    Inconclusive
}

/// <summary>
/// Effective documentary scope chosen by the semantic orchestrator. Catalog
/// resolution can validate this scope, but it cannot choose an alternative
/// source on the model's behalf.
/// </summary>
public enum SourceBackedDocumentScope
{
    RequestedDocument,
    AlternativeSources
}

public sealed record SourceBackedDocumentResolutionCandidate(
    string DocId,
    string DocPath,
    string DocName,
    string? RevisionId = null,
    string? SourceHash = null);

/// <summary>
/// Typed catalog observation. It establishes current identity only and is not
/// documentary content evidence: it must never receive an EvidenceId or be
/// inserted into an EvidenceBundle.
/// </summary>
public sealed record SourceBackedDocumentResolutionObservation(
    string RequestedReference,
    SourceBackedDocumentResolutionStatus Status,
    bool CatalogObservationComplete,
    IReadOnlyList<SourceBackedDocumentResolutionCandidate> Candidates,
    string ReasonCode,
    int PagesObserved,
    int ItemsObserved,
    bool ReachedSafetyLimit,
    long ElapsedMilliseconds,
    string? TechnicalError = null,
    int? ExactMatchCount = null);

public sealed record SourceBackedDocumentCatalogPage(
    IReadOnlyList<SourceBackedDocumentResolutionCandidate> Items,
    int Offset,
    int Limit,
    int? Total,
    bool EndOfList);

/// <summary>
/// Narrow mechanical port over the current, tenant-scoped backend catalog.
/// </summary>
public interface ISourceBackedNamedDocumentCatalogClient
{
    Task<SourceBackedDocumentCatalogPage> SearchAsync(
        string query,
        int limit,
        int offset,
        CancellationToken ct);
}

/// <summary>
/// Mechanical port that replaces a unique catalog identity with the active
/// revision identity exposed by the current backend. It returns identity only:
/// implementations must not expose documentary context as evidence.
/// </summary>
public interface ISourceBackedNamedDocumentIdentityHydrator
{
    Task<SourceBackedDocumentResolutionCandidate> HydrateAsync(
        SourceBackedDocumentResolutionCandidate candidate,
        CancellationToken ct);
}

/// <summary>
/// Resolves an explicitly named reference against the current backend catalog.
/// Implementations may use memory as a query hint, but never as proof of a
/// current match or absence.
/// </summary>
public interface ISourceBackedNamedDocumentResolver
{
    Task<SourceBackedDocumentResolutionObservation> ResolveAsync(
        string requestedReference,
        CancellationToken ct);
}
