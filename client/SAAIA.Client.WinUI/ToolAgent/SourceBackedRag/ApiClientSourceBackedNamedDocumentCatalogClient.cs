using System.Text.Json;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal sealed class SourceBackedCatalogObservationDisagreementException
    : Exception
{
    public SourceBackedCatalogObservationDisagreementException(
        IReadOnlyList<SourceBackedDocumentResolutionCandidate> candidates,
        int pagesObserved,
        int itemsObserved)
        : base("Current catalog observations disagree on exact identities.")
    {
        Candidates = candidates;
        PagesObserved = pagesObserved;
        ItemsObserved = itemsObserved;
    }

    public IReadOnlyList<SourceBackedDocumentResolutionCandidate> Candidates
    {
        get;
    }

    public int PagesObserved { get; }
    public int ItemsObserved { get; }
}

/// <summary>
/// Adapts ApiClient's normalized documents response to the narrow catalog port
/// used by exact named-document resolution.
/// </summary>
public sealed class ApiClientSourceBackedNamedDocumentCatalogClient
    : ISourceBackedNamedDocumentCatalogClient,
      ISourceBackedNamedDocumentIdentityHydrator
{
    private const int MaximumObservationPages = 60;

    private readonly ApiClient _api;

    public ApiClientSourceBackedNamedDocumentCatalogClient(ApiClient api)
        => _api = api ?? throw new ArgumentNullException(nameof(api));

    public Task<SourceBackedDocumentResolutionCandidate> HydrateAsync(
        SourceBackedDocumentResolutionCandidate candidate,
        CancellationToken ct)
        => new ApiClientSourceBackedNamedDocumentIdentityHydrator(_api)
            .HydrateAsync(candidate, ct);

    public async Task<SourceBackedDocumentCatalogPage> SearchAsync(
        string query,
        int limit,
        int offset,
        CancellationToken ct)
    {
        if (offset != 0)
        {
            throw new InvalidOperationException(
                "Consensus catalog observations must start at offset zero.");
        }

        var pageSize = Math.Clamp(limit, 1, 200);
        var v2 = await ObserveV2Async(query, pageSize, ct)
            .ConfigureAwait(false);
        var unified = await ObserveUnifiedAsync(query, pageSize, ct)
            .ConfigureAwait(false);
        var v2Eligible = ResolutionCandidates(query, v2.Items);
        var unifiedEligible = ResolutionCandidates(query, unified.Items);
        if (!v2Eligible.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(unifiedEligible.Keys))
        {
            var observed = v2Eligible.Values
                .Concat(unifiedEligible.Values)
                .GroupBy(CanonicalIdentityKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(static candidate => candidate.DocPath,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(static candidate => candidate.DocId,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            throw new SourceBackedCatalogObservationDisagreementException(
                observed,
                v2.PagesObserved + unified.PagesObserved,
                v2.ItemsObserved + unified.ItemsObserved);
        }

        var agreed = v2Eligible.Values
            .OrderBy(static candidate => candidate.DocPath,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.DocId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SourceBackedDocumentCatalogPage(
            agreed,
            Offset: 0,
            Limit: pageSize,
            Total: agreed.Length,
            EndOfList: true);
    }

    private async Task<CompleteCatalogObservation> ObserveV2Async(
        string query,
        int pageSize,
        CancellationToken ct)
    {
        var items = new List<SourceBackedDocumentResolutionCandidate>();
        var offset = 0;
        var pages = 0;
        var itemsObserved = 0;
        while (pages < MaximumObservationPages)
        {
            var payload = await _api.DocumentsSearchAsync(
                    query,
                    categoryPath: null,
                    categoryRef: null,
                    pageSize,
                    offset,
                    ct)
                .ConfigureAwait(false);
            var page = ParsePage(payload, pageSize, offset);
            pages++;
            itemsObserved += page.Items.Count;
            items.AddRange(page.Items);
            if (page.EndOfList)
            {
                return new CompleteCatalogObservation(
                    items,
                    pages,
                    itemsObserved);
            }

            if (page.Items.Count == 0)
            {
                throw new InvalidDataException(
                    "The V2 current catalog pagination did not advance.");
            }

            offset = checked(page.Offset + page.Items.Count);
        }

        throw new InvalidDataException(
            "The V2 current catalog page safety limit was reached.");
    }

    private async Task<CompleteCatalogObservation> ObserveUnifiedAsync(
        string query,
        int pageSize,
        CancellationToken ct)
    {
        var items = new List<SourceBackedDocumentResolutionCandidate>();
        var offset = 0;
        var pages = 0;
        var itemsObserved = 0;
        while (pages < MaximumObservationPages)
        {
            var response = await _api.DocumentsCatalogAsync(
                    category: null,
                    query,
                    pageSize,
                    offset,
                    ct)
                .ConfigureAwait(false);
            var responseLimit = response.Limit > 0
                ? response.Limit
                : pageSize;
            if (response.Offset != offset || responseLimit <= 0)
            {
                throw new InvalidDataException(
                    "The unified current catalog pagination is invalid.");
            }

            var pageItems = ParseUnifiedItems(response.Items);
            pages++;
            itemsObserved += pageItems.Count;
            items.AddRange(pageItems);
            if (response.Items.Count < responseLimit)
            {
                return new CompleteCatalogObservation(
                    items,
                    pages,
                    itemsObserved);
            }

            if (response.Items.Count == 0)
            {
                throw new InvalidDataException(
                    "The unified current catalog pagination did not advance.");
            }

            offset = checked(response.Offset + response.Items.Count);
        }

        throw new InvalidDataException(
            "The unified current catalog page safety limit was reached.");
    }

    private static IReadOnlyList<SourceBackedDocumentResolutionCandidate>
        ParseUnifiedItems(IEnumerable<DocumentCatalogItem> items)
    {
        var candidates = new List<SourceBackedDocumentResolutionCandidate>();
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Status)
                && !string.Equals(
                    item.Status,
                    "indexed",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var docPath = item.DocPath?.Trim() ?? string.Empty;
            var docName = string.IsNullOrWhiteSpace(item.DocName)
                ? ReadFileName(docPath)
                : item.DocName.Trim();
            if (item.DocId == Guid.Empty
                || string.IsNullOrWhiteSpace(docPath)
                || string.IsNullOrWhiteSpace(docName))
            {
                throw new InvalidDataException(
                    "A unified current catalog item has no canonical identity.");
            }

            candidates.Add(new SourceBackedDocumentResolutionCandidate(
                item.DocId.ToString("D"),
                docPath,
                docName,
                RevisionId: null,
                SourceHash: null));
        }

        return candidates;
    }

    private static Dictionary<string, SourceBackedDocumentResolutionCandidate>
        ResolutionCandidates(
            string query,
            IEnumerable<SourceBackedDocumentResolutionCandidate> candidates)
    {
        var exact = new Dictionary<string,
            SourceBackedDocumentResolutionCandidate>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (SourceBackedNamedDocumentResolver.IsExactMatch(query, candidate)
                || SourceBackedNamedDocumentResolver
                    .IsAbbreviatedIdentityMatch(query, candidate)
                || SourceBackedNamedDocumentResolver
                    .IsUniqueFormalDesignatorCandidate(query, candidate)
                || SourceBackedNamedDocumentResolver
                    .IsFormalDesignatorCatalogQueryMatch(query, candidate))
            {
                exact.TryAdd(CanonicalIdentityKey(candidate), candidate);
            }
        }

        return exact;
    }

    private static string CanonicalIdentityKey(
        SourceBackedDocumentResolutionCandidate candidate)
        => string.Join(
            "|",
            candidate.DocId.Trim(),
            NormalizePath(candidate.DocPath),
            NormalizePath(candidate.DocName));

    private static string NormalizePath(string? value)
        => (value ?? string.Empty)
            .Trim()
            .Replace('\\', '/')
            .Trim('/');

    private static string ReadFileName(string path)
    {
        var normalized = NormalizePath(path);
        var separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private sealed record CompleteCatalogObservation(
        IReadOnlyList<SourceBackedDocumentResolutionCandidate> Items,
        int PagesObserved,
        int ItemsObserved);

    internal static SourceBackedDocumentCatalogPage ParsePage(
        JsonElement payload,
        int requestedLimit,
        int requestedOffset)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("items", out var itemsNode)
            || itemsNode.ValueKind != JsonValueKind.Array
            || !payload.TryGetProperty("endOfList", out var endNode)
            || endNode.ValueKind is not JsonValueKind.True
                and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                "The current document catalog response is incomplete.");
        }

        var responseOffset = ReadOptionalInt32(payload, "offset")
                             ?? requestedOffset;
        var responseLimit = ReadOptionalInt32(payload, "limit")
                            ?? requestedLimit;
        var total = ReadOptionalInt32(payload, "total");
        if (responseOffset < 0 || responseLimit <= 0 || total is < 0)
        {
            throw new InvalidDataException(
                "The current document catalog pagination is invalid.");
        }

        var items = new List<SourceBackedDocumentResolutionCandidate>();
        foreach (var item in itemsNode.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "A current document catalog item is invalid.");
            }

            var docId = ReadString(item, "docId");
            var docPath = ReadString(item, "docPath");
            var docName = ReadString(item, "docName");
            if (string.IsNullOrWhiteSpace(docId)
                || string.IsNullOrWhiteSpace(docPath))
            {
                throw new InvalidDataException(
                    "A current document catalog item has no canonical identity.");
            }

            if (string.IsNullOrWhiteSpace(docName))
                docName = ReadFileName(docPath);
            if (string.IsNullOrWhiteSpace(docName))
            {
                throw new InvalidDataException(
                    "A current document catalog item has no document name.");
            }

            items.Add(new SourceBackedDocumentResolutionCandidate(
                docId,
                docPath,
                docName,
                RevisionId: null,
                SourceHash: null));
        }

        return new SourceBackedDocumentCatalogPage(
            items,
            responseOffset,
            responseLimit,
            total,
            endNode.GetBoolean());
    }

    private static int? ReadOptionalInt32(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var node)
            || node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return node.ValueKind == JsonValueKind.Number
               && node.TryGetInt32(out var value)
            ? value
            : throw new InvalidDataException(
                "The current document catalog pagination is malformed.");
    }

    private static string? ReadString(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var node)
           && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

}
