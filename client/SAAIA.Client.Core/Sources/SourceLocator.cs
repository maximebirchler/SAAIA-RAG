namespace SAAIA.Client.Core.Sources;

/// <summary>Identity and requested page, never a claim that a PDF viewer rendered that page.</summary>
public sealed record SourceLocator(string? DocumentPath, int? Page, string? SourceHash = null,
    string? RevisionId = null, string? ChunkId = null, string? AnchorId = null,
    string? ContentCardId = null, bool RequireExactSourceHash = false, string? OriginTraceId = null,
    string? ResolutionIssue = null, int? PageEnd = null)
{
    public string IdentityAssurance => ResolutionIssue is not null ? "blocked_ambiguous_identity" : !string.IsNullOrWhiteSpace(SourceHash)
        ? "exact_hash_requested" : "identity_unverified";
}

/// <summary>Bounded identity lookup lets inline links use the same locator as their source card.</summary>
public sealed class SourceLocatorRegistry(int capacity = 512)
{
    private readonly object _gate = new();
    private readonly List<SourceLocator> _items = new();
    public void Register(SourceLocator locator)
    {
        if (string.IsNullOrWhiteSpace(locator.DocumentPath)) return;
        lock (_gate)
        {
            _items.RemoveAll(x => x == locator);
            _items.Add(locator);
            if (_items.Count > Math.Clamp(capacity, 1, 4_096)) _items.RemoveAt(0);
        }
    }

    public SourceLocator Resolve(string path, int? page)
    {
        lock (_gate)
        {
            var matches = _items.Where(x => string.Equals(Normalize(x.DocumentPath), Normalize(path), StringComparison.OrdinalIgnoreCase)
                && (x.Page == page || (x.Page is > 0 && page >= x.Page && page <= x.PageEnd))).ToArray();
            // Multiple indexed versions of the same link are deliberately not resolved by guesswork.
            if (matches.Select(x => (x.SourceHash?.Trim().ToLowerInvariant(), x.RevisionId?.Trim().ToLowerInvariant())).Distinct().Count() == 1)
                return matches[^1] with { Page = page };
            if (matches.Length > 0)
                return new(path, page, RequireExactSourceHash: true, ResolutionIssue: "ambiguous_indexed_revision");
        }
        return new(path, page);
    }
    private static string Normalize(string? path) => (path ?? "").Replace('\\', '/').Trim();
}
