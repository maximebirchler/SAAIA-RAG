namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class SourceContractVerifier
{
    private static void AddCanonicalEvidenceIdentityErrors(
        ICollection<SourceVerificationError> errors,
        EvidenceItem item)
    {
        void Add(string code, string message)
            => errors.Add(new SourceVerificationError(
                code,
                message,
                item.EvidenceId,
                SourceBackedPipelineStep.SourceVerifier));

        if (string.IsNullOrWhiteSpace(item.DocId))
        {
            Add(
                "missing_doc_id",
                $"Evidence id '{item.EvidenceId}' has no canonical document id.");
        }

        if (string.IsNullOrWhiteSpace(item.RevisionId))
        {
            Add(
                "missing_revision_id",
                $"Evidence id '{item.EvidenceId}' has no indexed revision id.");
        }

        if (!IsSha256Hex(item.SourceHash))
        {
            Add(
                "invalid_source_sha256",
                $"Evidence id '{item.EvidenceId}' does not carry a 64-character SHA-256 for the indexed revision.");
        }

        if (item.PageStart is > 0
            && item.PageEnd is > 0
            && item.PageEnd < item.PageStart)
        {
            Add(
                "invalid_page_range",
                $"Evidence id '{item.EvidenceId}' has a page range whose end precedes its start.");
        }

        var isCanonicalContentCard = string.Equals(
            item.SourceKind,
            "canonical_content_card",
            StringComparison.OrdinalIgnoreCase);
        if (isCanonicalContentCard && string.IsNullOrWhiteSpace(item.ContentCardId))
        {
            Add(
                "missing_content_card_id",
                $"Evidence id '{item.EvidenceId}' is a canonical content card without an explicit content-card id.");
        }

        if (isCanonicalContentCard
            && SourceBackedContentCardEvidenceContract
                .IsProoflessCanonicalContentCard(item))
        {
            Add(
                "missing_grounded_content_card_evidence",
                $"Evidence id '{item.EvidenceId}' is a canonical content card without effective source text or source-backed facts.");
        }

        if (!string.IsNullOrWhiteSpace(item.ChunkId)
            && item.ChunkId.StartsWith(
                "content-card:",
                StringComparison.OrdinalIgnoreCase))
        {
            Add(
                "overloaded_chunk_identity",
                $"Evidence id '{item.EvidenceId}' stores a content-card identity in chunkId instead of ContentCardId.");
        }

        var requiresChunkId = !isCanonicalContentCard
                              && (item.ToolName.StartsWith(
                                      "rag.",
                                      StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(
                                      item.ToolName,
                                      "documents.context",
                                      StringComparison.OrdinalIgnoreCase));
        if (requiresChunkId && string.IsNullOrWhiteSpace(item.ChunkId))
        {
            Add(
                "missing_chunk_id",
                $"Evidence id '{item.EvidenceId}' is chunk-backed but has no chunk id.");
        }
    }

    private static bool IsSha256Hex(string? value)
        => SourceBackedContentCardEvidenceContract.IsSha256Hex(value);
}
