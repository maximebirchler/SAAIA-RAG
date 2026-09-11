using System.Globalization;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed record SourceBackedCanonicalContentCard(
    string ValueRef,
    string SourceCardId,
    string EvidenceId,
    string Title,
    string? Kind,
    string? Proof,
    string? Document,
    int? PageStart,
    int? PageEnd,
    string? ParentExcerpt);

internal static class SourceBackedCanonicalContentCardInventory
{
    internal const string ExactSourceItemSelectionMode = "exact_source_item_selection";
    private const int HardCardLimit = 40;
    internal const int CompatibilityCardLimit = HardCardLimit;
    // Measured with Qwen3-4B Q5_K_M on a real 120-card structured inventory.
    // A 30/20 split reduced call count but degraded complete five-day coverage
    // to four snacks and three suppers. The 24/10 split retained 28 legitimate
    // cards with at least five choices for every requested meal column. These
    // are context-size limits only; semantic selection remains with the LLM.
    internal const int CompatibilityBatchSize = 10;
    internal const int ShortlistBatchSize = 24;
    internal const int ShortlistBatchSelectionLimit = ShortlistBatchSize;
    private const int CandidatePoolHardLimit = 120;
    private const int StructuredCardBudgetLimit = 24;
    private const int ProofSpanLimit = 5;
    private const int ProofCharacterLimit = 520;

    internal static bool IsEnabled(SourceBackedIntake intake)
        => string.Equals(
            intake.StructuredCellValueMode,
            ExactSourceItemSelectionMode,
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsValueRefAllowedForColumn(
        SourceBackedIntake intake,
        string columnLabel,
        string valueRef)
    {
        if (intake.CanonicalColumnValueRefs is null)
            return true;

        return intake.CanonicalColumnValueRefs.TryGetValue(columnLabel, out var allowedRefs)
               && allowedRefs.Contains(valueRef, StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> GetAllowedColumns(
        SourceBackedIntake intake,
        string valueRef)
        => intake.CanonicalColumnValueRefs is null
            ? Array.Empty<string>()
            : intake.CanonicalColumnValueRefs
                .Where(pair => pair.Value.Contains(valueRef, StringComparer.OrdinalIgnoreCase))
                .Select(static pair => pair.Key)
                .ToArray();

    internal static IReadOnlyList<SourceBackedCanonicalContentCard> Build(
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        IReadOnlyList<string>? evidenceIds)
    {
        if (!IsEnabled(intake))
            return Array.Empty<SourceBackedCanonicalContentCard>();

        if (intake.CanonicalContentCards is not null)
        {
            if (evidenceIds is null)
                return intake.CanonicalContentCards;

            var allowedEvidenceIds = evidenceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return intake.CanonicalContentCards
                .Where(card => allowedEvidenceIds.Contains(card.EvidenceId))
                .ToArray();
        }

        var selectedItems = SelectItems(bundle, evidenceIds);
        if (selectedItems.Count == 0)
            return Array.Empty<SourceBackedCanonicalContentCard>();

        var limits = ResolveCardLimits(intake, selectedItems);
        var cards = new List<SourceBackedCanonicalContentCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selectedItems)
        {
            var itemLimit = limits.TryGetValue(item.EvidenceId, out var allocated)
                ? allocated
                : 0;
            if (itemLimit <= 0 || !item.MatchedContentCards.HasValue)
                continue;

            var ordinal = 0;
            foreach (var card in EnumerateCards(item.MatchedContentCards.Value))
            {
                ordinal++;
                if (cards.Count >= HardCardLimit || ordinal > itemLimit)
                    break;

                var title = ReadString(card, "title", "name", "label");
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var sourceCardId = ReadString(card, "contentCardId", "content_card_id", "id")
                                   ?? $"{item.EvidenceId}:card:{ordinal.ToString(CultureInfo.InvariantCulture)}";
                var identity = $"{item.EvidenceId}|{sourceCardId}";
                if (!seen.Add(identity))
                    continue;

                var evidence = ReadObject(card, "evidence", "contentEvidence");
                var pageStart = ReadInt(card, "pageStart", "page_start") ?? item.PageStart;
                var pageEnd = ReadInt(card, "pageEnd", "page_end") ?? item.PageEnd ?? pageStart;
                cards.Add(new SourceBackedCanonicalContentCard(
                    $"V{cards.Count + 1:000}",
                    sourceCardId.Trim(),
                    item.EvidenceId,
                    CollapseWhitespace(title),
                    ReadString(card, "kind", "type", "contentRole"),
                    ReadProof(card, evidence),
                    item.DocPath ?? item.DocName,
                    pageStart,
                    pageEnd,
                    CollapseWhitespace(item.Excerpt)));
            }

            if (cards.Count >= HardCardLimit)
                break;
        }

        return cards;
    }

    private static IReadOnlyDictionary<string, int> ResolveCardLimits(
        SourceBackedIntake intake,
        IReadOnlyList<EvidenceItem> evidenceItems)
    {
        var shape = SourceBackedStructuredTableShapeBuilder.TryCreateFromIntake(intake);
        if (shape is null || evidenceItems.Count == 0)
        {
            return evidenceItems.ToDictionary(
                static item => item.EvidenceId,
                static _ => 1,
                StringComparer.OrdinalIgnoreCase);
        }

        var remainingBudget = Math.Clamp(
            shape.RowLabels.Count * shape.ColumnHeaders.Count,
            1,
            StructuredCardBudgetLimit);
        var availableCounts = evidenceItems
            .Select(static item => item.MatchedContentCards.HasValue
                ? EnumerateCards(item.MatchedContentCards.Value).Count()
                : 0)
            .ToArray();
        var limits = new int[evidenceItems.Count];

        while (remainingBudget > 0)
        {
            var allocatedAny = false;
            for (var index = 0;
                 index < evidenceItems.Count && remainingBudget > 0;
                 index++)
            {
                if (limits[index] >= availableCounts[index])
                    continue;

                limits[index]++;
                remainingBudget--;
                allocatedAny = true;
            }

            if (!allocatedAny)
                break;
        }

        var result = new Dictionary<string, int>(
            evidenceItems.Count,
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < evidenceItems.Count; index++)
            result[evidenceItems[index].EvidenceId] = limits[index];

        return result;
    }

    internal static IReadOnlyList<SourceBackedCanonicalContentCard> BuildCandidatePool(
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        IReadOnlyList<string>? evidenceIds)
    {
        if (!IsEnabled(intake))
            return Array.Empty<SourceBackedCanonicalContentCard>();

        if (intake.CanonicalContentCards is not null)
        {
            if (evidenceIds is null)
                return intake.CanonicalContentCards;

            var allowedEvidenceIds = evidenceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return intake.CanonicalContentCards
                .Where(card => allowedEvidenceIds.Contains(card.EvidenceId))
                .ToArray();
        }

        var selectedItems = SelectItems(bundle, evidenceIds);
        var cardsByItem = selectedItems
            .Where(static item => item.MatchedContentCards.HasValue)
            .Select(item => new
            {
                Item = item,
                Cards = EnumerateCards(item.MatchedContentCards!.Value).ToArray()
            })
            .Where(static entry => entry.Cards.Length > 0)
            .ToArray();
        if (cardsByItem.Length == 0)
            return Array.Empty<SourceBackedCanonicalContentCard>();

        // Interleave card ordinals across parent evidence so a hard context cap
        // does not silently expose only the first few documents. This is a
        // mechanical breadth policy; semantic legitimacy remains an LLM choice.
        var result = new List<SourceBackedCanonicalContentCard>();
        var seenSourceCards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxOrdinal = cardsByItem.Max(static entry => entry.Cards.Length);
        for (var ordinalIndex = 0;
             ordinalIndex < maxOrdinal && result.Count < CandidatePoolHardLimit;
             ordinalIndex++)
        {
            foreach (var entry in cardsByItem)
            {
                if (ordinalIndex >= entry.Cards.Length)
                    continue;

                var card = entry.Cards[ordinalIndex];
                var title = ReadString(card, "title", "name", "label");
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var sourceCardId = ReadString(card, "contentCardId", "content_card_id", "id")
                                   ?? $"{entry.Item.EvidenceId}:card:{ordinalIndex + 1}";
                var document = entry.Item.DocPath ?? entry.Item.DocName;
                var sourceIdentity = $"{document}|{sourceCardId}";
                if (!seenSourceCards.Add(sourceIdentity))
                    continue;

                var evidence = ReadObject(card, "evidence", "contentEvidence");
                var pageStart = ReadInt(card, "pageStart", "page_start")
                                ?? entry.Item.PageStart;
                var pageEnd = ReadInt(card, "pageEnd", "page_end")
                              ?? entry.Item.PageEnd
                              ?? pageStart;
                result.Add(new SourceBackedCanonicalContentCard(
                    $"V{result.Count + 1:000}",
                    sourceCardId.Trim(),
                    entry.Item.EvidenceId,
                    CollapseWhitespace(title),
                    ReadString(card, "kind", "type", "contentRole"),
                    ReadProof(card, evidence),
                    document,
                    pageStart,
                    pageEnd,
                    CollapseWhitespace(entry.Item.Excerpt)));

                if (result.Count >= CandidatePoolHardLimit)
                    break;
            }
        }

        return result;
    }

    private static IReadOnlyList<EvidenceItem> SelectItems(
        EvidenceBundle bundle,
        IReadOnlyList<string>? evidenceIds)
    {
        if (evidenceIds is null)
            return bundle.Items;

        var byId = bundle.ById;
        return evidenceIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .ToArray();
    }

    private static IEnumerable<JsonElement> EnumerateCards(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var card in element.EnumerateArray())
            {
                if (card.ValueKind == JsonValueKind.Object)
                    yield return card;
            }

            yield break;
        }

        if (element.ValueKind != JsonValueKind.Object)
            yield break;

        if (TryGetProperty(
                element,
                out var nested,
                "matchedContentCards",
                "matched_content_cards",
                "contentCards",
                "content_cards",
                "cards")
            && nested.ValueKind == JsonValueKind.Array)
        {
            foreach (var card in nested.EnumerateArray())
            {
                if (card.ValueKind == JsonValueKind.Object)
                    yield return card;
            }

            yield break;
        }

        yield return element;
    }

    private static string? ReadProof(JsonElement card, JsonElement? evidence)
    {
        var direct = ReadString(evidence, "sourceText", "source_text", "text", "excerpt")
                     ?? ReadString(card, "sourceText", "source_text");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            var directProof = CollapseWhitespace(direct);
            return directProof.Length <= ProofCharacterLimit
                ? directProof
                : directProof[..ProofCharacterLimit].TrimEnd() + "...";
        }

        if (!TryGetProperty(evidence, out var facts, "facts", "factList")
            || facts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = facts
            .EnumerateArray()
            .Where(static fact => fact.ValueKind == JsonValueKind.Object)
            .Select(fact =>
                ReadString(fact, "sourceText", "source_text", "text", "value", "normalizedValue"))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ProofSpanLimit)
            .ToArray();
        if (values.Length == 0)
            return null;

        var proof = string.Join(" / ", values);
        return proof.Length <= ProofCharacterLimit
            ? proof
            : proof[..ProofCharacterLimit].TrimEnd() + "...";
    }

    private static JsonElement? ReadObject(JsonElement element, params string[] names)
        => TryGetProperty(element, out var value, names) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? ReadString(JsonElement? element, params string[] names)
    {
        if (!element.HasValue)
            return null;

        return ReadString(element.Value, names);
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static int? ReadInt(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement? element, out JsonElement value, params string[] names)
    {
        if (!element.HasValue)
        {
            value = default;
            return false;
        }

        return TryGetProperty(element.Value, out value, names);
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // Callers pass aliases in semantic preference order. Respect that
            // order rather than whichever property happens to be serialized
            // first (for example, prefer sourceText over a preceding numeric
            // value when constructing an immutable card's proof).
            foreach (var name in names)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }
        }

        value = default;
        return false;
    }

    internal static string CollapseWhitespace(string? value)
        => string.Join(
            " ",
            (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
