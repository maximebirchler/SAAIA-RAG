namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static bool IsMechanicallyResolvableNavigationLocator(
        EvidenceItem item)
    {
        // A title card without an excerpt is not a citation. Its observed
        // canonical identity and page can still locate the text to be read.
        if (SourceBackedContentCardEvidenceContract.IsProoflessCanonicalContentCard(item))
        {
            return Guid.TryParse(item.DocId, out _)
                   && Guid.TryParse(item.RevisionId, out _)
                   && Guid.TryParse(item.ContentCardId, out _)
                   && item.SourceHash is { Length: 64 } hash
                   && hash.All(Uri.IsHexDigit)
                   && IsRequestedDocumentIdentityEligible(item)
                   && item.PageStart is > 0
                   && (item.PageEnd is null || item.PageEnd >= item.PageStart);
        }

        if (!string.Equals(
                item.SourceKind,
                "navigation_map",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasDocumentIdentity =
            !string.IsNullOrWhiteSpace(item.DocPath)
            || !string.IsNullOrWhiteSpace(item.DocId)
            || !string.IsNullOrWhiteSpace(item.DocName);
        if (!hasDocumentIdentity)
            return false;

        if (!string.IsNullOrWhiteSpace(item.ChunkId))
            return true;

        return item.PageStart is > 0
               && (item.PageEnd is null
                   || item.PageEnd.Value >= item.PageStart.Value);
    }

    private static string? ValidateObservedDocumentLocator(
        SourceBackedAgentToolCall call,
        string internalToolName,
        EvidenceBundle bundle)
    {
        if (!string.Equals(
                internalToolName,
                "documents.context",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var requestedChunkId = GetString(call.Arguments, "chunkId")?.Trim()
                               ?? string.Empty;
        if (requestedChunkId.Length == 0 || bundle.Items.Count == 0)
            return null;

        var observedChunkItems = bundle.Items
            .Where(item => string.Equals(
                item.ChunkId?.Trim(),
                requestedChunkId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (observedChunkItems.Length == 0)
            return "documents_context_chunk_id_not_observed";

        var requestedDocId = GetString(call.Arguments, "docId")?.Trim()
                             ?? string.Empty;
        var requestedDocPath = GetString(call.Arguments, "docPath")?.Trim()
                               ?? string.Empty;
        var requestedDocRef = GetString(call.Arguments, "docRef")?.Trim()
                              ?? string.Empty;
        var identityMatches = observedChunkItems.Any(item =>
            MatchesObservedLocatorValue(requestedDocId, item.DocId)
            && MatchesObservedLocatorValue(requestedDocPath, item.DocPath)
            && (requestedDocRef.Length == 0
                || MatchesObservedLocatorValue(requestedDocRef, item.DocPath)
                || MatchesObservedLocatorValue(requestedDocRef, item.DocName)
                || MatchesObservedLocatorValue(requestedDocRef, item.DocId)));
        return identityMatches
            ? null
            : "documents_context_locator_identity_mismatch";
    }

    private static bool MatchesObservedLocatorValue(
        string requested,
        string? observed)
        => requested.Length == 0
           || string.Equals(
               requested,
               observed?.Trim(),
               StringComparison.OrdinalIgnoreCase);
}
