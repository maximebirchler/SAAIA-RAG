using System.Collections.ObjectModel;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class EvidenceBundleBuilder
{
    private static void AddDocumentContentCardItems(
        ICollection<EvidenceItem> items,
        ToolResults.Item toolItem,
        string userQuestion,
        int toolSequence)
    {
        if (toolItem.Result.ValueKind != JsonValueKind.Object
            || !TryGetProperty(toolItem.Result, "items", out var contentCards)
            || contentCards.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var queryUsed = GetString(toolItem.Result, "query", "q") ?? userQuestion;
        var ordinal = 0;
        foreach (var card in contentCards.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object)
                continue;

            var title = GetString(card, "title", "name", "label");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            ordinal++;
            var cardId = GetString(card, "contentCardId", "content_card_id", "id")
                         ?? $"inventory-card-{toolSequence}-{ordinal}";
            var pageStart = GetInt(card, "pageStart", "page_start", "page");
            var pageEnd = GetInt(card, "pageEnd", "page_end") ?? pageStart;
            var proof = ReadContentCardProof(card);
            var hasGroundedEvidence = !string.IsNullOrWhiteSpace(proof);
            var excerpt = NormalizeCardText(title, proof);
            var docId = GetString(card, "docId", "doc_id", "documentId");
            var revisionId = GetString(card, "revisionId", "revision_id", "indexedVersion");
            var lineage = new List<string>
            {
                $"tool:{toolItem.ToolName}",
                $"toolSequence:{toolSequence}",
                "content_card:" + cardId
            };
            var sourceHash = ResolveContentCardSourceHash(
                items,
                docId,
                revisionId,
                GetString(card, "sourceHash", "source_hash", "hash"),
                lineage);
            var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["canonicalContentCard"] = "true",
                ["contentCardId"] = cardId,
                ["hasGroundedEvidence"] = hasGroundedEvidence ? "true" : "false",
                // A canonical card already carries its exact source identity.
                // Supplying it as the resolved anchor avoids a redundant LLM
                // label-resolution pass; semantic suitability remains audited.
                ["sourceAnchorLabel"] = title.Trim()
            };
            AddHint(card, hints, "kind");
            AddHint(card, hints, "profileVersion");
            AddHint(card, hints, "queryScore");
            AddHint(card, hints, "headingPath");
            AddHint(card, hints, "sectionLevel");
            AddHint(card, hints, "sourceChunkIndex");

            var risks = new List<string>();
            if (pageStart is null && pageEnd is null)
                risks.Add("missing_page_anchor");
            if (!hasGroundedEvidence)
            {
                risks.Add(
                    SourceBackedContentCardEvidenceContract.MissingGroundedEvidenceRisk);
            }

            items.Add(new EvidenceItem(
                EvidenceId: string.Empty,
                SourceKind: "canonical_content_card",
                ToolName: toolItem.ToolName,
                QueryUsed: queryUsed,
                DocId: docId,
                DocName: GetString(card, "docName", "doc_name", "documentName", "fileName"),
                DocPath: GetString(card, "docPath", "doc_path", "path", "documentPath"),
                SourceHash: sourceHash,
                RevisionId: revisionId,
                PageStart: pageStart,
                PageEnd: pageEnd,
                ChunkId: null,
                Excerpt: excerpt,
                NormalizedExcerpt: NormalizeEvidenceText(excerpt),
                Score: GetDouble(card, "queryScore", "score"),
                Rank: ordinal,
                CategoryPath: GetString(card, "categoryPath", "category_path", "category"),
                DocLanguage: null,
                ProfileLanguage: null,
                ExtractionQuality: null,
                MatchedContentCards: JsonSerializer.SerializeToElement(new[] { card.Clone() }),
                SelectionHints: new ReadOnlyDictionary<string, string>(hints),
                CodeHints: BuildCodeHints(toolItem, toolSequence),
                RiskFlags: risks,
                Lineage: lineage)
            {
                ContentCardId = cardId
            });
        }
    }

    internal static string? ReadContentCardProof(JsonElement card)
    {
        if (!TryGetProperty(card, "evidence", out var evidence)
            || evidence.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var direct = GetString(evidence, "sourceText", "quote", "excerpt", "text", "content");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (TryGetProperty(evidence, "facts", out var canonicalFacts)
            && canonicalFacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var fact in canonicalFacts.EnumerateArray())
            {
                if (fact.ValueKind != JsonValueKind.Object
                    || !string.Equals(
                        GetString(fact, "kind"),
                        "canonical_section_context",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        GetString(fact, "label"),
                        "section_excerpt",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sectionExcerpt = GetString(
                    fact,
                    "sourceText",
                    "quote",
                    "excerpt",
                    "text");
                if (!string.IsNullOrWhiteSpace(sectionExcerpt))
                    return sectionExcerpt;
            }
        }

        var proofParts = new List<string>();
        foreach (var arrayName in new[] { "facts", "quantityFacts" })
        {
            if (!TryGetProperty(evidence, arrayName, out var facts)
                || facts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var fact in facts.EnumerateArray())
            {
                if (fact.ValueKind != JsonValueKind.Object)
                    continue;
                var sourceText = GetString(fact, "sourceText", "quote", "excerpt", "text");
                if (!string.IsNullOrWhiteSpace(sourceText)
                    && !proofParts.Contains(sourceText, StringComparer.OrdinalIgnoreCase))
                {
                    proofParts.Add(sourceText);
                }
                if (proofParts.Count >= 3)
                    break;
            }

            if (proofParts.Count >= 3)
                break;
        }

        return proofParts.Count == 0 ? null : string.Join(" | ", proofParts);
    }

    private static string? ResolveContentCardSourceHash(
        IEnumerable<EvidenceItem> existingItems,
        string? docId,
        string? revisionId,
        string? receivedSourceHash,
        ICollection<string> lineage)
    {
        if (SourceBackedContentCardEvidenceContract.IsSha256Hex(receivedSourceHash))
            return receivedSourceHash!.Trim();

        if (string.IsNullOrWhiteSpace(docId)
            || string.IsNullOrWhiteSpace(revisionId))
        {
            return receivedSourceHash;
        }

        var compatibleHashes = existingItems
            .Where(item =>
                string.Equals(item.DocId?.Trim(), docId.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    item.RevisionId?.Trim(),
                    revisionId.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                && SourceBackedContentCardEvidenceContract.IsSha256Hex(item.SourceHash))
            .Select(static item => item.SourceHash!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (compatibleHashes.Length != 1)
            return receivedSourceHash;

        lineage.Add("source_hash:evidence_bundle_document_revision");
        return compatibleHashes[0];
    }

    private static void AddHint(
        JsonElement source,
        IDictionary<string, string> target,
        string name)
    {
        if (!TryGetProperty(source, name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            or JsonValueKind.Object or JsonValueKind.Array)
        {
            return;
        }

        target[name] = value.ToString();
    }
}
