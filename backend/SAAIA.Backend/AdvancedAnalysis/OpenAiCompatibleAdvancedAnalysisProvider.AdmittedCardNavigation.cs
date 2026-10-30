using System.Collections.ObjectModel;
using System.Text.Json;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record AdmittedCardLocator(string ExactTitle, string SourceKey,
        int PhysicalPageStart, int PhysicalPageEnd);

    private sealed record AdmittedCardNavigation(int ActiveCardEvidenceCount, int UnlocatableCardEvidenceCount,
        int ObservedLocatorCount, int ListedLocatorCount, int OmittedLocatorCount,
        int WireCharacters, int WireCharacterLimit, int MaximumListedLocators,
        IReadOnlyList<AdmittedCardLocator> Locators, string Instruction);

    private sealed class AdmittedCardSourceCatalogue(IList<PromptSourceHandle> sources,
        AdmittedCardNavigation navigation) : ReadOnlyCollection<PromptSourceHandle>(sources)
    {
        public AdmittedCardNavigation Navigation { get; } = navigation;
    }

    private static IReadOnlyList<PromptSourceHandle> BuildAdmittedCardSourceCatalogue(
        AdvancedAnalysisLoadDescriptor load, IReadOnlyList<AdvancedAnalysisResolvedEvidence> active,
        IReadOnlyList<PromptSourceHandle> sources, int configuredMaximum)
    {
        if (!load.StructuredLayout || load.AnswerUnitCount < 2
            || ResolveSelectionMode(load) != "repeatable_named_items") return sources;

        // Source membership and aliases come only from this active revalidated
        // set. A remembered alias must never resurrect a retired card or source.
        var sourceKeys = sources.ToDictionary(source => string.Join("|", source.SourceReference.DocId,
            source.SourceReference.RevisionId, source.SourceReference.SourceHash), source => source.SourceKey,
            StringComparer.OrdinalIgnoreCase);
        var cards = active.Where(item => !string.IsNullOrWhiteSpace(item.Reference.ContentCardId)
            && item.SourceObservation is null && item.NativeObservation is null).ToArray();
        if (cards.Length == 0) return sources;
        var observed = new List<AdmittedCardLocator>();
        var seen = new HashSet<AdmittedCardLocator>();
        var unlocatable = 0;
        foreach (var card in cards)
        {
            if (string.IsNullOrWhiteSpace(card.Reference.EvidenceId) || string.IsNullOrWhiteSpace(card.ExactTitle)
                || card.Reference.PageStart < 1 || card.Reference.PageEnd < card.Reference.PageStart
                || !sourceKeys.TryGetValue(BuildCanonicalSourceIdentity(card, card.Reference.EvidenceId!), out var key))
            { unlocatable++; continue; }
            // Record equality is exact: do not normalize titles or merge across
            // doc/revision/hash scopes. Identical locators can share one read.
            var locator = new AdmittedCardLocator(card.ExactTitle, key, card.Reference.PageStart, card.Reference.PageEnd);
            if (seen.Add(locator)) observed.Add(locator);
        }

        const int maximumListed = 128;
        var limit = Math.Min(configuredMaximum / 4, 8_000);
        const string instruction = "Navigation only: exact titles and physical pages of active admitted content cards, including bodies omitted from this call. These are not evidence, citations, suitability decisions, distinct-item requirements or corpus counts. Use an offered read_source from physicalPageStart within its declared page-window limit, or find_source_text with the exact title, then assess the returned body. The full locator span does not prove complete body coverage. Omitted locators remain undisplayed admitted metadata; existing source lookup and budgets still apply. Counts describe this active admitted set only.";
        var baseline = MeasureEscapedDocumentaryTextCharacters(JsonSerializer.Serialize(SourceCatalogueForPrompt(sources), JsonOptions));
        AdmittedCardSourceCatalogue Measured(IReadOnlyList<AdmittedCardLocator> listed)
        {
            var navigation = new AdmittedCardNavigation(cards.Length, unlocatable, observed.Count, listed.Count,
                observed.Count - listed.Count, 0, limit, maximumListed, listed, instruction);
            // The count of wire characters is itself serialized. Reach its
            // short numeric fixed point using the same nested JSON escaping.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var wrapped = new AdmittedCardSourceCatalogue(sources.ToList(), navigation);
                var measured = MeasureEscapedDocumentaryTextCharacters(JsonSerializer.Serialize(SourceCatalogueForPrompt(wrapped), JsonOptions)) - baseline;
                if (navigation.WireCharacters == measured) return wrapped;
                navigation = navigation with { WireCharacters = measured };
            }
            throw new AdvancedAnalysisProviderException("advanced_card_navigation_budget_invalid");
        }
        var listed = new List<AdmittedCardLocator>();
        var result = Measured(listed.ToArray());
        if (result.Navigation.WireCharacters > limit)
            throw new AdvancedAnalysisProviderException("advanced_card_navigation_budget_exceeded");
        foreach (var locator in observed)
        {
            if (listed.Count == maximumListed) break;
            var candidate = Measured(listed.Append(locator).ToArray());
            if (candidate.Navigation.WireCharacters > limit) continue;
            listed.Add(locator);
            result = candidate;
        }
        // Whole locators only. Skipping one oversized title must not hide a
        // later short locator; omitted counts retain both budget and 128 limits.
        return result;
    }
}
