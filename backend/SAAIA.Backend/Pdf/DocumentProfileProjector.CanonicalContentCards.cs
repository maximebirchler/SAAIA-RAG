internal static partial class DocumentProfileProjector
{
    private const string CanonicalSectionAnchorKind =
        "canonical_section_anchor";
    private const string CanonicalSectionHeadingSignal =
        "canonical_section_heading";
    private const string CanonicalSectionContextSignal =
        "canonical_section_context";

    private static IReadOnlyList<DocumentProfileContentCard>
        BuildCanonicalSectionContentCards(
            IReadOnlyList<ExtractedDocumentSection> sections,
            IReadOnlyList<ExtractedDocumentUnit> units,
            IReadOnlyList<ExtractedExactMatchEntry> exactMatchEntries,
            IReadOnlyList<string> keywords)
    {
        var candidates = new List<DocumentProfileContentCardCandidate>();
        var orderedSections = sections
            .OrderBy(static section => section.Ordinal)
            .ToArray();
        var headingPathBySectionOrdinal =
            ContextualTextProjector.BuildHeadingPathMap(orderedSections);
        var unitsBySectionOrdinal = units
            .Where(static unit => unit.SectionOrdinal.HasValue)
            .GroupBy(static unit => unit.SectionOrdinal!.Value)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static unit => unit.Ordinal)
                    .ToArray());

        for (var sectionIndex = 0;
             sectionIndex < orderedSections.Length
             && candidates.Count < MaxContentCards;
             sectionIndex++)
        {
            var section = orderedSections[sectionIndex];
            var title = NormalizeCanonicalSectionHeading(section.Title);
            if (!IsMechanicallyUsableCanonicalSectionHeading(
                    title,
                    section.PageStart,
                    section.PageEnd))
            {
                continue;
            }

            var contextUnit = ResolveCanonicalSectionContextUnit(
                section,
                unitsBySectionOrdinal);
            if (contextUnit is null)
            {
                // A structural heading remains available through document
                // navigation, but a canonical content card is directly
                // citable. Do not manufacture several cards around the same
                // descendant chunk or expose a heading-only card as proof.
                continue;
            }
            // The retrieval projector rebases unreliable visual Docling
            // heading levels inside the actual page reading context. Prefer
            // that exact chunk-local path over the broader section tree,
            // whose source levels can otherwise create false parentage.
            var headingPath = !string.IsNullOrWhiteSpace(
                contextUnit.HeadingPath)
                ? contextUnit.HeadingPath
                : headingPathBySectionOrdinal.GetValueOrDefault(
                    section.Ordinal);
            var sectionLevel = contextUnit.HeadingLevel
                               ?? section.Level;
            var evidence = BuildCanonicalSectionEvidence(
                section,
                title,
                headingPath,
                sectionLevel,
                contextUnit);
            string[] signals =
            [
                CanonicalSectionHeadingSignal,
                CanonicalSectionContextSignal
            ];

            candidates.Add(new(
                new(
                    title,
                    section.PageStart,
                    section.PageStart,
                    CanonicalSectionAnchorKind,
                    signals,
                    evidence,
                    ContentCardId: null),
                Score: 100));
        }

        foreach (var entry in exactMatchEntries
                     .Where(static entry =>
                         entry.Kind is "standard_ref" or "code_ref")
                     .OrderBy(static entry => entry.EntryIndex))
        {
            var title = CleanTitleCandidate(entry.Text);
            if (!LooksLikeTechnicalIdentifier(title)
                || !IsPlausibleTechnicalContentCardIdentifier(title))
            {
                continue;
            }

            AddContentCardCandidate(
                candidates,
                title,
                entry.PageStart,
                entry.PageEnd,
                entry.Kind,
                entry.Text,
                keywords,
                score: 88);
        }

        return NormalizeContentCards(
            OrderContentCardsForBalancedCoverage(candidates));
    }

    private static ExtractedDocumentUnit? ResolveCanonicalSectionContextUnit(
        ExtractedDocumentSection section,
        IReadOnlyDictionary<int, ExtractedDocumentUnit[]> unitsBySectionOrdinal)
    {
        if (!unitsBySectionOrdinal.TryGetValue(
                section.Ordinal,
                out var sectionUnits))
        {
            return null;
        }

        return sectionUnits.FirstOrDefault(static unit =>
            !string.IsNullOrWhiteSpace(unit.Text));
    }

    private static DocumentProfileCardEvidence BuildCanonicalSectionEvidence(
        ExtractedDocumentSection section,
        string title,
        string? headingPath,
        int sectionLevel,
        ExtractedDocumentUnit? contextUnit)
    {
        var facts = new List<DocumentProfileEvidenceFact>
        {
            new(
                Kind: "canonical_heading",
                Label: "section_heading",
                Value: title,
                Unit: null,
                SourceText: title,
                PageStart: section.PageStart,
                PageEnd: section.PageStart),
            new(
                Kind: "canonical_structure",
                Label: "section_level",
                Value: sectionLevel.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Unit: null,
                SourceText: null,
                PageStart: section.PageStart,
                PageEnd: section.PageStart)
        };
        if (!string.IsNullOrWhiteSpace(headingPath))
        {
            facts.Add(new(
                Kind: "canonical_structure",
                Label: "heading_path",
                Value: headingPath,
                Unit: null,
                SourceText: null,
                PageStart: section.PageStart,
                PageEnd: section.PageStart));
        }

        if (contextUnit is not null)
        {
            facts.Add(new(
                Kind: "canonical_source",
                Label: "retrieval_chunk_index",
                Value: contextUnit.Ordinal.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Unit: null,
                SourceText: null,
                PageStart: contextUnit.PageStart,
                PageEnd: contextUnit.PageEnd));
            facts.Add(new(
                Kind: "canonical_section_context",
                Label: "section_excerpt",
                Value: null,
                Unit: null,
                SourceText: contextUnit.Text,
                PageStart: contextUnit.PageStart,
                PageEnd: contextUnit.PageEnd));
        }

        return new(
            SchemaVersion: "canonical_section_anchor_evidence_v1",
            ScaleBasis: null,
            QuantityFacts: [],
            NonScalableReasons: [],
            Confidence: null,
            Language: null,
            Facts: facts);
    }
}
