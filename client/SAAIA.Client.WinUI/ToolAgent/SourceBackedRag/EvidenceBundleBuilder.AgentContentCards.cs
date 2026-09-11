using System.Collections.ObjectModel;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class EvidenceBundleBuilder
{
    private static void AddAgentMatchedContentCardItems(
        ICollection<EvidenceItem> items,
        EvidenceItem parent,
        JsonElement source)
    {
        var matchedCards = ReadMatchedContentCards(source);
        if (!matchedCards.HasValue || matchedCards.Value.ValueKind != JsonValueKind.Array)
            return;

        var ordinal = 0;
        foreach (var card in matchedCards.Value.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object)
                continue;

            var title = GetString(card, "title", "name", "label");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            ordinal++;
            var proof = ReadContentCardProof(card);
            var hasGroundedEvidence = !string.IsNullOrWhiteSpace(proof);
            var excerpt = NormalizeCardText(title, proof);
            var cardId = GetString(card, "contentCardId", "content_card_id", "id")
                         ?? $"card-{parent.Rank}-{ordinal}";
            var pageStart = GetInt(card, "pageStart", "page_start") ?? parent.PageStart;
            var pageEnd = GetInt(card, "pageEnd", "page_end") ?? parent.PageEnd ?? pageStart;
            var hints = new Dictionary<string, string>(
                parent.SelectionHints,
                StringComparer.OrdinalIgnoreCase)
            {
                ["canonicalContentCard"] = "true",
                ["contentCardId"] = cardId,
                ["hasGroundedEvidence"] = hasGroundedEvidence ? "true" : "false",
                // The backend card title is the immutable, source-provided
                // identity of this canonical item. Preserve it mechanically so
                // the semantic judge can evaluate that exact identity without
                // spending a separate LLM call rediscovering it from headings.
                ["sourceAnchorLabel"] = title.Trim()
            };
            AddHint(card, hints, "kind");
            AddHint(card, hints, "profileVersion");
            AddHint(card, hints, "queryScore");
            AddHint(card, hints, "headingPath");
            AddHint(card, hints, "sectionLevel");
            AddHint(card, hints, "sourceChunkIndex");
            var singleCard = JsonSerializer.SerializeToElement(new[] { card.Clone() });

            items.Add(parent with
            {
                EvidenceId = string.Empty,
                SourceKind = "canonical_content_card",
                PageStart = pageStart,
                PageEnd = pageEnd,
                ChunkId = null,
                ContentCardId = cardId,
                Excerpt = excerpt,
                NormalizedExcerpt = NormalizeEvidenceText(excerpt),
                MatchedContentCards = singleCard,
                SelectionHints = new ReadOnlyDictionary<string, string>(hints),
                RiskFlags = parent.RiskFlags
                    .Where(static flag =>
                        !string.Equals(flag, "empty_excerpt", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(flag, "orientation_only", StringComparison.OrdinalIgnoreCase))
                    .Concat(hasGroundedEvidence
                        ? Array.Empty<string>()
                        : new[]
                        {
                            SourceBackedContentCardEvidenceContract
                                .MissingGroundedEvidenceRisk
                        })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Lineage = parent.Lineage
                    .Concat(new[] { "content_card:" + cardId })
                    .ToArray()
            });
        }
    }

    private static string NormalizeCardText(string title, string? proof)
    {
        var value = string.IsNullOrWhiteSpace(proof)
            ? title
            : title + ". " + proof;
        return string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
