using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class RetrievalChunkProjector
{
    private static readonly string ChunkSeparator = Environment.NewLine + Environment.NewLine;

    public static IReadOnlyList<ProjectedRetrievalChunk> Project(
        IReadOnlyList<Chunk> chunks,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units)
    {
        if (chunks.Count == 0)
            return Array.Empty<ProjectedRetrievalChunk>();

        var projected = new List<ProjectedRetrievalChunk>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var section = sections
                .Where(s => Overlaps(chunk.PageStart, chunk.PageEnd, s.PageStart, s.PageEnd))
                .OrderByDescending(s => OverlapScore(chunk.PageStart, chunk.PageEnd, s.PageStart, s.PageEnd))
                .ThenBy(s => s.Ordinal)
                .FirstOrDefault();

            var unit = units
                .Where(u => Overlaps(chunk.PageStart, chunk.PageEnd, u.PageStart, u.PageEnd))
                .OrderByDescending(u => OverlapScore(chunk.PageStart, chunk.PageEnd, u.PageStart, u.PageEnd))
                .ThenBy(u => Distance(chunk.ChunkIndex, u.Ordinal))
                .FirstOrDefault();

            projected.Add(CreateProjectedChunk(
                chunk.ChunkIndex,
                section?.Ordinal,
                unit?.Ordinal,
                chunk.PageStart,
                chunk.PageEnd,
                chunk.Text,
                ResolveExcerptOffsetStart(unit, chunk.Text),
                ResolveExcerptOffsetEnd(unit, chunk.Text),
                chunkType: "legacy_word_window_v1",
                sourceUnits: unit is null ? [] : [unit]));
        }

        return projected;
    }

    public static IReadOnlyList<ProjectedRetrievalChunk> ProjectStructureAware(
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        int maxWords,
        int overlapWords,
        int minWords)
    {
        if (units.Count == 0)
            return Array.Empty<ProjectedRetrievalChunk>();

        maxWords = Math.Max(1, maxWords);
        overlapWords = Math.Max(0, overlapWords);
        minWords = Math.Max(1, minWords);

        var orderedUnits = units
            .OrderBy(unit => unit.SectionOrdinal ?? int.MaxValue)
            .ThenBy(unit => unit.PageStart)
            .ThenBy(unit => unit.Ordinal)
            .ToList();
        var windowUnits = SelectWindowUnits(orderedUnits);

        var chunks = new List<ProjectedRetrievalChunk>();
        var chunkIndex = 0;

        foreach (var sectionGroup in windowUnits.GroupBy(unit => unit.SectionOrdinal))
        {
            var sectionUnits = sectionGroup
                .OrderBy(unit => unit.PageStart)
                .ThenBy(unit => unit.Ordinal)
                .ToList();

            if (sectionUnits.Count == 0)
                continue;

            var start = 0;
            while (start < sectionUnits.Count)
            {
                var window = new List<ExtractedDocumentUnit>();
                var tokenTotal = 0;
                var cursor = start;
                var stoppedBeforeStructuredBoundary = false;

                while (cursor < sectionUnits.Count)
                {
                    var candidate = sectionUnits[cursor];
                    var candidateTokens = Math.Max(1, candidate.TokenCount);

                    var startsStructuredBoundary = window.Count > 0
                        && (LooksLikeHighSignalUnit(candidate)
                            || LooksLikeStructuredItemBoundaryUnit(candidate)
                            || LooksLikePostFooterStructuredBodyBoundary(window[^1], candidate));
                    if (startsStructuredBoundary)
                    {
                        if (ShouldAppendDanglingStructuredContinuation(
                                window[^1],
                                candidate,
                                tokenTotal,
                                candidateTokens,
                                maxWords))
                        {
                            window.Add(candidate);
                            tokenTotal += candidateTokens;
                            cursor++;
                            continue;
                        }

                        stoppedBeforeStructuredBoundary = true;
                        break;
                    }

                    if (window.Count > 0 && tokenTotal + candidateTokens > maxWords)
                    {
                        if (ShouldAppendDanglingStructuredContinuation(
                                window[^1],
                                candidate,
                                tokenTotal,
                                candidateTokens,
                                maxWords))
                        {
                            window.Add(candidate);
                            tokenTotal += candidateTokens;
                            cursor++;
                            continue;
                        }

                        break;
                    }

                    window.Add(candidate);
                    tokenTotal += candidateTokens;
                    cursor++;
                }

                if (window.Count == 0)
                {
                    window.Add(sectionUnits[start]);
                    cursor = start + 1;
                    tokenTotal = Math.Max(1, sectionUnits[start].TokenCount);
                }

                var shouldKeepShortBoundaryLead = !stoppedBeforeStructuredBoundary
                    || tokenTotal >= minWords
                    || WindowContainsHighSignalUnit(window);
                if (tokenTotal >= minWords || (chunks.Count == 0 && shouldKeepShortBoundaryLead))
                {
                    var first = window[0];
                    var last = window[^1];
                    var chunkText = string.Join(ChunkSeparator, window.Select(unit => unit.Text));
                    var repairsDanglingStructuredContinuation = RepairsDanglingStructuredContinuation(window);
                    int? representativeUnit = window.Count == 1 || repairsDanglingStructuredContinuation
                        ? first.Ordinal
                        : null;

                    chunks.Add(CreateProjectedChunk(
                        chunkIndex++,
                        first.SectionOrdinal,
                        representativeUnit,
                        first.PageStart,
                        last.PageEnd,
                        chunkText,
                        first.OffsetStart,
                        last.OffsetEnd,
                        chunkType: ResolveStructureAwareChunkType(window, repairsDanglingStructuredContinuation),
                        sourceUnits: window));
                }

                if (cursor >= sectionUnits.Count)
                    break;

                start = ComputeNextStart(sectionUnits, start, cursor, overlapWords);
                if (stoppedBeforeStructuredBoundary
                    && start < cursor
                    && cursor < sectionUnits.Count
                    && (LooksLikeStructuredItemBoundaryUnit(sectionUnits[cursor])
                        || LooksLikePostFooterStructuredBodyBoundary(sectionUnits[Math.Max(start, cursor - 1)], sectionUnits[cursor])))
                {
                    start = cursor;
                }
            }
        }

        AddHighSignalUnitChunks(chunks, windowUnits, ref chunkIndex);
        AddFooterTitledItemWindowChunks(chunks, windowUnits, maxWords, ref chunkIndex);

        if (chunks.Count == 0)
        {
            if (windowUnits.Count == 0)
                return Array.Empty<ProjectedRetrievalChunk>();

            var fallback = windowUnits.Count > 0
                ? windowUnits
                : orderedUnits;
            var text = string.Join(ChunkSeparator, fallback.Select(unit => unit.Text));
            var first = fallback[0];
            var last = fallback[^1];
            chunks.Add(CreateProjectedChunk(
                0,
                first.SectionOrdinal,
                fallback.Count == 1 ? first.Ordinal : null,
                first.PageStart,
                last.PageEnd,
                text,
                first.OffsetStart,
                last.OffsetEnd,
                chunkType: "document_window_v1",
                sourceUnits: fallback));
        }

        return chunks;
    }

    private static IReadOnlyList<ExtractedDocumentUnit> SelectWindowUnits(IReadOnlyList<ExtractedDocumentUnit> orderedUnits)
    {
        var reliableUnits = orderedUnits
            .Where(ExtractionQualityPolicy.ShouldUseUnitForRetrievalWindow)
            .ToList();
        if (reliableUnits.Count > 0)
            return reliableUnits;

        var targetedFallbackUnits = orderedUnits
            .Where(ExtractionQualityPolicy.ShouldUseUnitForProfileCards)
            .ToList();
        return targetedFallbackUnits;
    }

    private static void AddHighSignalUnitChunks(
        List<ProjectedRetrievalChunk> chunks,
        IReadOnlyList<ExtractedDocumentUnit> orderedUnits,
        ref int chunkIndex)
    {
        var existingExactUnitOrdinals = chunks
            .Where(static chunk => chunk.UnitOrdinal.HasValue)
            .Select(static chunk => chunk.UnitOrdinal)
            .Where(static ordinal => ordinal.HasValue)
            .Select(static ordinal => ordinal!.Value)
            .ToHashSet();

        foreach (var unit in orderedUnits)
        {
            if (existingExactUnitOrdinals.Contains(unit.Ordinal))
                continue;
            if (ExtractionQualityPolicy.ShouldRestrictUnitToTargetedReferences(unit))
                continue;
            if (!LooksLikeHighSignalUnit(unit))
                continue;

            chunks.Add(CreateProjectedChunk(
                chunkIndex++,
                unit.SectionOrdinal,
                unit.Ordinal,
                unit.PageStart,
                unit.PageEnd,
                unit.Text,
                unit.OffsetStart,
                unit.OffsetEnd,
                chunkType: "unit_exact_v1",
                sourceUnits: [unit]));

            existingExactUnitOrdinals.Add(unit.Ordinal);
        }
    }

    private static void AddFooterTitledItemWindowChunks(
        List<ProjectedRetrievalChunk> chunks,
        IReadOnlyList<ExtractedDocumentUnit> orderedUnits,
        int maxWords,
        ref int chunkIndex)
    {
        if (orderedUnits.Count == 0)
            return;

        var units = orderedUnits
            .OrderBy(unit => unit.SectionOrdinal ?? int.MaxValue)
            .ThenBy(unit => unit.PageStart)
            .ThenBy(unit => unit.Ordinal)
            .ToList();

        var existingFingerprints = chunks
            .Select(static chunk => $"{chunk.PageStart}|{chunk.PageEnd}|{chunk.Text}")
            .ToHashSet(StringComparer.Ordinal);

        for (var i = 0; i < units.Count; i++)
        {
            var closingUnit = units[i];
            if (!TryExtractTrailingFooterTitle(closingUnit.Text, out var title))
                continue;

            var selected = new List<ExtractedDocumentUnit> { closingUnit };
            var tokenTotal = Math.Max(1, closingUnit.TokenCount);
            for (var j = i - 1; j >= 0; j--)
            {
                var candidate = units[j];
                var firstSelected = selected[0];
                if (candidate.SectionOrdinal != closingUnit.SectionOrdinal)
                    break;
                if (candidate.PageEnd < closingUnit.PageStart - 1)
                    break;
                if (ContainsTrailingStructuredFooterTitle(candidate.Text))
                    break;
                if (LooksLikeStructuredItemTitleLeadBoundary(candidate))
                    break;
                if (LooksLikePostFooterStructuredBodyBoundary(candidate, firstSelected))
                    break;

                var candidateTokens = Math.Max(1, candidate.TokenCount);
                if (tokenTotal + candidateTokens > maxWords)
                {
                    if (!ShouldAppendOverflowingFooterStructuredHead(
                            candidate,
                            selected,
                            tokenTotal,
                            candidateTokens,
                            maxWords))
                    {
                        break;
                    }
                }

                selected.Insert(0, candidate);
                tokenTotal += candidateTokens;
            }

            if (selected.Count < 2)
                continue;

            var first = selected[0];
            var last = selected[^1];
            if (chunks.Any(chunk =>
                    chunk.OffsetStart == first.OffsetStart
                    && chunk.OffsetEnd == last.OffsetEnd
                    && chunk.Text.StartsWith(title, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var text = $"{title}{ChunkSeparator}{string.Join(ChunkSeparator, selected.Select(unit => unit.Text))}";
            var fingerprint = $"{first.PageStart}|{last.PageEnd}|{NormalizeRetrievalText(text)}";
            if (!existingFingerprints.Add(fingerprint))
                continue;

            chunks.Add(CreateProjectedChunk(
                chunkIndex++,
                first.SectionOrdinal,
                null,
                first.PageStart,
                last.PageEnd,
                text,
                first.OffsetStart,
                last.OffsetEnd,
                chunkType: "footer_titled_item_window_v1",
                sourceUnits: selected));
        }
    }

    private static bool ShouldAppendOverflowingFooterStructuredHead(
        ExtractedDocumentUnit candidate,
        IReadOnlyList<ExtractedDocumentUnit> selected,
        int tokenTotal,
        int candidateTokens,
        int maxWords)
    {
        if (selected.Count == 0 || candidateTokens <= 0)
            return false;
        if (candidate.SectionOrdinal != selected[0].SectionOrdinal)
            return false;
        if (candidate.PageStart != selected[0].PageStart || candidate.PageEnd != selected[0].PageEnd)
            return false;
        if (ContainsTrailingStructuredFooterTitle(candidate.Text))
            return false;
        if (tokenTotal + candidateTokens > maxWords + Math.Max(80, maxWords / 2))
            return false;

        var candidateText = InsertStructuralBoundarySpaces(candidate.Text);
        var candidateNormalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        if (string.IsNullOrWhiteSpace(candidateNormalized) || LooksLikeReferenceList(candidateNormalized))
            return false;

        var selectedText = InsertStructuralBoundarySpaces(string.Join(ChunkSeparator, selected.Select(static unit => unit.Text)));
        var selectedNormalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(selectedText));
        var candidateHasInventory = ContainsItemizedSectionHeading(candidateNormalized)
            || CountStructuredInventoryQuantityEvidence(candidateText) >= 3
            || CountStructuredInventoryQuantityEvidence(candidateNormalized) >= 3;
        var selectedHasProcedure = ContainsProcedureSectionHeading(selectedNormalized)
            || CountNumberedSteps(selectedText) >= 2
            || CountNumberedSteps(selectedNormalized) >= 2;
        var selectedHasInventory = ContainsItemizedSectionHeading(selectedNormalized)
            || CountStructuredInventoryQuantityEvidence(selectedText) >= 3
            || CountStructuredInventoryQuantityEvidence(selectedNormalized) >= 3;

        return candidateHasInventory && selectedHasProcedure && !selectedHasInventory;
    }

    private static int CountStructuredInventoryQuantityEvidence(string text)
        => Regex.Matches(
                text,
                @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|km|nm|units?|unites?|items?|elements?|entries?|parts?|pieces?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Count;

    private static int CountStructuredQuantityUnitEvidence(string text)
        => Regex.Matches(
                text,
                @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|km|nm|bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|pct|percent|pourcent|deg|degrees?|degres?|c|f|units?|unites?|items?|elements?|entries?|parts?|pieces?|pages?|s|sec|secs|secondes?|seconds?|min|mins?|minutes?|h|hr|hrs?|hours?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Count;

    private static bool WindowContainsHighSignalUnit(IReadOnlyList<ExtractedDocumentUnit> window)
        => window.Any(LooksLikeHighSignalUnit);

    private static string ResolveStructureAwareChunkType(
        IReadOnlyList<ExtractedDocumentUnit> window,
        bool repairsDanglingStructuredContinuation = false)
    {
        if ((window.Count == 1 || repairsDanglingStructuredContinuation)
            && !ExtractionQualityPolicy.ShouldRestrictUnitToTargetedReferences(window[0]))
        {
            return "unit_exact_v1";
        }

        return "section_window_v1";
    }

    private static bool RepairsDanglingStructuredContinuation(IReadOnlyList<ExtractedDocumentUnit> window)
    {
        if (window.Count < 2)
            return false;

        for (var i = 1; i < window.Count; i++)
        {
            if (CanAppendDanglingStructuredContinuation(window[i - 1], window[i], int.MaxValue))
                return true;
        }

        return false;
    }

    private static bool ShouldAppendDanglingStructuredContinuation(
        ExtractedDocumentUnit previous,
        ExtractedDocumentUnit candidate,
        int tokenTotal,
        int candidateTokens,
        int maxWords)
    {
        if (!CanAppendDanglingStructuredContinuation(previous, candidate, maxWords))
            return false;

        var budgetOverflowLimit = maxWords + Math.Max(24, maxWords / 2);
        return tokenTotal + candidateTokens <= budgetOverflowLimit;
    }

    private static bool CanAppendDanglingStructuredContinuation(
        ExtractedDocumentUnit previous,
        ExtractedDocumentUnit candidate,
        int maxWords)
    {
        if (previous.SectionOrdinal != candidate.SectionOrdinal)
            return false;
        if (candidate.PageStart < previous.PageStart || candidate.PageStart > previous.PageEnd + 1)
            return false;
        if (candidate.TokenCount > Math.Max(48, maxWords))
            return false;
        if (ContainsTrailingStructuredFooterTitle(previous.Text))
            return false;
        if (!LooksLikeDanglingStructuredContinuationTail(previous.Text))
            return false;

        var normalizedCandidate = NormalizeRetrievalText(candidate.Text);
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || LooksLikeReferenceList(normalizedCandidate))
            return false;

        return StructuredItemEvidenceRegex().IsMatch(normalizedCandidate)
            || CountStructuredQuantityUnitEvidence(normalizedCandidate) > 0;
    }

    private static bool LooksLikeDanglingStructuredContinuationTail(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeRetrievalText(text);
        return DanglingStructuredContinuationTailRegex().IsMatch(normalized);
    }

    private static bool LooksLikeHighSignalUnit(ExtractedDocumentUnit unit)
    {
        if (unit.TokenCount < 10 || unit.Text.Length < 80)
            return false;

        var text = InsertStructuralBoundarySpaces(unit.Text);
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(text));
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (LooksLikeReferenceList(normalized))
            return false;

        var hasItemizedSection = ContainsItemizedSectionHeading(normalized);
        var hasProcedureSection = ContainsProcedureSectionHeading(normalized);
        var hasGovernanceSection = ContainsGovernanceSectionHeading(normalized);
        var hasCountOrSteps = CountOrStepMarkerRegex().IsMatch(text)
            || CountOrStepMarkerRegex().IsMatch(normalized)
            || CountNumberedSteps(text) >= 2
            || CountNumberedSteps(normalized) >= 2;

        return (hasItemizedSection && (hasProcedureSection || hasCountOrSteps))
            || (hasProcedureSection && (hasGovernanceSection || hasCountOrSteps))
            || (hasGovernanceSection && hasCountOrSteps);
    }

    private static bool LooksLikeStructuredItemBoundaryUnit(ExtractedDocumentUnit unit)
    {
        if (unit.TokenCount < 6 || string.IsNullOrWhiteSpace(unit.Text))
            return false;

        var text = InsertStructuralBoundarySpaces(unit.Text);
        var lead = text.Length <= 260 ? text : text[..260];
        if (StructuredContentLexicon.TryExtractStructuredItemTitleLead(lead, out _))
            return true;

        return StructuredItemTitleLeadRegex().IsMatch(lead)
            && !GenericStructuredCueLeadRegex().IsMatch(lead)
            && !SingleWordFooterBeforeLongTitleRegex().IsMatch(lead)
            && (StructuredContentLexicon.LooksLikeStructuredLeadMarker(lead)
                || StructuredItemEvidenceRegex().IsMatch(lead)
                || CountStructuredQuantityUnitEvidence(lead) > 0);
    }

    private static bool LooksLikeStructuredItemTitleLeadBoundary(ExtractedDocumentUnit unit)
    {
        if (unit.TokenCount < 6 || string.IsNullOrWhiteSpace(unit.Text))
            return false;

        var text = InsertStructuralBoundarySpaces(unit.Text);
        var lead = text.Length <= 260 ? text : text[..260];
        var titleLeadMatch = StructuredItemTitleLeadRegex().Match(lead);
        if (!titleLeadMatch.Success)
            return false;

        var titleLead = CleanEmbeddedTitleCandidate(titleLeadMatch.Value);
        return LooksLikeMostlyUppercaseTitle(titleLead)
            && !GenericStructuredCueLeadRegex().IsMatch(lead)
            && !SingleWordFooterBeforeLongTitleRegex().IsMatch(lead)
            && CountStructuredQuantityUnitEvidence(lead) > 0;
    }

    private static bool LooksLikePostFooterStructuredBodyBoundary(
        ExtractedDocumentUnit previous,
        ExtractedDocumentUnit candidate)
    {
        if (candidate.TokenCount < 6 || string.IsNullOrWhiteSpace(candidate.Text))
            return false;

        if (candidate.PageStart < previous.PageStart || candidate.PageStart > previous.PageEnd + 1)
            return false;

        if (!ContainsTrailingStructuredFooterTitle(previous.Text))
            return false;

        var candidateText = InsertStructuralBoundarySpaces(candidate.Text);
        var lead = candidateText.Length <= 260 ? candidateText : candidateText[..260];
        return LooksLikeStructuredBodyLead(lead);
    }

    private static bool LooksLikeStructuredBodyLead(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lead = InsertStructuralBoundarySpaces(text);
        if (lead.Length < 12)
            return false;

        return StructuredContentLexicon.LooksLikeStructuredLeadMarker(lead)
            || StructuredBodyLeadEvidenceRegex().IsMatch(lead)
            || StructuredItemEvidenceRegex().Matches(lead).Count >= 2
            || CountStructuredQuantityUnitEvidence(lead) >= 2;
    }

    private static bool ContainsTrailingStructuredFooterTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return TryExtractTrailingFooterTitle(text, out _);
    }

    private static bool TryExtractTrailingFooterTitle(string text, out string title)
    {
        title = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = InsertStructuralBoundarySpaces(text);
        if (StructuredItemEvidenceRegex().Matches(normalized).Count < 2
            && CountStructuredQuantityUnitEvidence(normalized) < 2
            && !StructuredContentLexicon.LooksLikeStructuredLeadMarker(normalized))
        {
            return false;
        }

        foreach (Match match in EmbeddedUppercaseTitleRegex().Matches(normalized))
        {
            var titleCandidate = CleanEmbeddedTitleCandidate(match.Groups["title"].Value);
            if (!IsUsefulFooterTitle(titleCandidate))
                continue;

            var after = normalized[(match.Index + match.Length)..];
            if (after.Length <= 170)
            {
                title = titleCandidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsUsefulFooterTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length is < 4 or > 90)
            return false;

        var tokenCount = CountTokens(title);
        if (tokenCount is < 1 or > 10)
            return false;

        if (!title.Any(char.IsLetter) || !LooksLikeMostlyUppercaseTitle(title))
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        return !EmbeddedTitleStopwords.Contains(normalized)
            && !ContainsGenericNavigationTitle(normalized);
    }

    private static bool ContainsItemizedSectionHeading(string normalized)
        => StructuredContentLexicon.ContainsItemizedCue(normalized)
            || ContainsAny(normalized, "resources", "ressources");

    private static bool ContainsProcedureSectionHeading(string normalized)
        => StructuredContentLexicon.ContainsRetrievalProcedureCue(normalized);

    private static bool ContainsGovernanceSectionHeading(string normalized)
        => StructuredContentLexicon.ContainsGovernanceCue(normalized);

    private static bool LooksLikeReferenceList(string normalized)
    {
        if (normalized.Contains("http", StringComparison.Ordinal)
            && CountOccurrences(normalized, "http") >= 2)
            return true;

        return normalized.Contains("references consultees", StringComparison.Ordinal);
    }

    private static string FoldDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string InsertStructuralBoundarySpaces(string text)
        => NormalizeRetrievalText(text);

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static int CountNumberedSteps(string text)
    {
        var count = 0;
        foreach (Match match in NumberedStepRegex().Matches(text))
            count++;

        return count;
    }

    private static int ComputeNextStart(
        IReadOnlyList<ExtractedDocumentUnit> units,
        int currentStart,
        int currentEndExclusive,
        int overlapWords)
    {
        if (overlapWords <= 0)
            return currentEndExclusive;

        var overlapTokenCount = 0;
        var nextStart = currentEndExclusive;
        for (var i = currentEndExclusive - 1; i >= currentStart; i--)
        {
            overlapTokenCount += Math.Max(1, units[i].TokenCount);
            nextStart = i;
            if (overlapTokenCount >= overlapWords)
                break;
        }

        return nextStart >= currentEndExclusive
            ? currentEndExclusive
            : Math.Max(nextStart, currentStart + 1);
    }

    private static ProjectedRetrievalChunk CreateProjectedChunk(
        int chunkIndex,
        int? sectionOrdinal,
        int? unitOrdinal,
        int pageStart,
        int pageEnd,
        string text,
        int? offsetStart,
        int? offsetEnd,
        string chunkType,
        IReadOnlyList<ExtractedDocumentUnit> sourceUnits)
    {
        var normalizedText = NormalizeRetrievalText(text);
        var prefixedText = PrefixDetectedEmbeddedTitle(normalizedText);
        var classification = RetrievalContentClassifier.ClassifyChunk(prefixedText, chunkType);
        var extractionQuality = ResolveExtractionQuality(sourceUnits);

        return new(
            ChunkIndex: chunkIndex,
            SectionOrdinal: sectionOrdinal,
            UnitOrdinal: unitOrdinal,
            PageStart: pageStart,
            PageEnd: pageEnd,
            Text: prefixedText,
            TokenCount: CountTokens(prefixedText),
            Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(prefixedText)),
            ChunkType: classification.ChunkType,
            OffsetStart: offsetStart,
            OffsetEnd: offsetEnd,
            ContentRole: classification.ContentRole,
            NavigationReason: classification.NavigationReason,
            OriginalChunkType: classification.OriginalChunkType,
            NavigationScore: classification.NavigationScore,
            ContentDensityScore: classification.ContentDensityScore,
            ExtractionTextStatus: extractionQuality.TextStatus,
            ExtractionTextSparse: extractionQuality.TextSparse,
            ExtractionOcrCandidate: extractionQuality.OcrCandidate,
            ExtractionQualitySignals: extractionQuality.Signals);
    }

    private static RetrievalChunkExtractionQuality ResolveExtractionQuality(IReadOnlyList<ExtractedDocumentUnit> units)
    {
        if (units.Count == 0)
            return new(null, false, false, []);

        var signals = units
            .SelectMany(static unit => unit.ExtractionQualitySignals ?? [])
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var status = ResolveWorstTextStatus(units.Select(static unit => unit.ExtractionTextStatus));
        return new RetrievalChunkExtractionQuality(
            status,
            units.Any(static unit => unit.ExtractionTextSparse),
            units.Any(static unit => unit.ExtractionOcrCandidate),
            signals);
    }

    private static string? ResolveWorstTextStatus(IEnumerable<string?> statuses)
    {
        var score = 0;
        string? result = null;
        foreach (var status in statuses)
        {
            var currentScore = status switch
            {
                "empty_text" => 4,
                "low_text" => 3,
                "ok" => 1,
                null or "" => 0,
                _ => 2
            };

            if (currentScore > score)
            {
                score = currentScore;
                result = status;
            }
        }

        return result;
    }

    private static string NormalizeRetrievalText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var withoutControls = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            withoutControls.Append(char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t'
                ? ' '
                : ch);
        }

        var normalized = withoutControls.ToString();
        normalized = LeadingCompactPageNumberRegex().Replace(normalized, string.Empty);
        normalized = DigitToStructuralLeadRegex().Replace(normalized, " ");
        normalized = LetterToNumericMeasureRegex().Replace(normalized, " ");
        normalized = LowerToCountNounBoundaryRegex().Replace(normalized, " ");
        normalized = ApostropheWordToNumericWordBoundaryRegex().Replace(normalized, "${word} ");
        normalized = LetterToBareNumberedStepRegex().Replace(normalized, " ");
        normalized = PunctuationToBareNumberedStepRegex().Replace(normalized, " ");
        normalized = PunctuationToNumericMeasureRegex().Replace(normalized, " ");
        normalized = UnitToDigitBoundaryRegex().Replace(normalized, "${unit} ");
        if (LooksQuantityDenseText(normalized))
        {
            normalized = PunctuationToLooseQuantityBoundaryRegex().Replace(normalized, " ");
            normalized = LowerToLooseQuantityBoundaryRegex().Replace(normalized, " ");
            normalized = UnitToDigitBoundaryRegex().Replace(normalized, "${unit} ");
        }

        normalized = UppercaseRunToElidedTitleCaseBoundaryRegex().Replace(normalized, " ");
        normalized = UppercaseRunToTitleCaseBoundaryRegex().Replace(normalized, " ");
        normalized = LowerToTitleCaseWordBoundaryRegex().Replace(normalized, " ");
        normalized = LowerToTitleCaseWordBoundaryRegex().Replace(normalized, " ");
        normalized = LowerOrDigitToStructuralLeadRegex().Replace(normalized, " ");
        normalized = StructuralBoundaryRegex().Replace(normalized, " ");
        normalized = HorizontalWhitespaceRegex().Replace(normalized, " ");
        normalized = ParagraphWhitespaceRegex().Replace(normalized, ChunkSeparator);
        return normalized.Trim();
    }

    private static bool LooksQuantityDenseText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var count = 0;
        foreach (Match _ in QuantityDenseMarkerRegex().Matches(text))
        {
            count++;
            if (count >= 3)
                return true;
        }

        return false;
    }

    private static string PrefixDetectedEmbeddedTitle(string text)
    {
        var title = ExtractEmbeddedTitle(text);
        if (string.IsNullOrWhiteSpace(title))
            return text;

        var normalizedText = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(text));
        var normalizedTitle = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        if (normalizedText.StartsWith(normalizedTitle, StringComparison.Ordinal))
            return text;

        return $"{title}{ChunkSeparator}{text}";
    }

    private static string? ExtractEmbeddedTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 80)
            return null;

        string? best = null;
        var bestScore = 0;
        foreach (Match match in EmbeddedUppercaseTitleRegex().Matches(text))
        {
            var candidate = CleanEmbeddedTitleCandidate(match.Groups["title"].Value);
            if (!IsUsefulEmbeddedTitle(candidate))
                continue;

            var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidate));
            var score = candidate.Length;
            if (match.Index > 40)
                score += 20;
            if (StructuredContextBeforeTitleRegex().IsMatch(text[..match.Index]))
                score += 30;
            if (StructuredContextAfterTitleRegex().IsMatch(text[Math.Min(text.Length, match.Index + match.Length)..]))
                score += 10;
            if (EmbeddedTitleStopwords.Contains(normalized))
                score -= 40;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best is not null && bestScore >= 30)
            return best;

        foreach (var candidate in StructuredContentLexicon.ExtractEmbeddedStructuredItemTitles(text, limit: 1))
        {
            var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidate));
            var index = normalized.Length == 0
                ? -1
                : FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(text))
                    .IndexOf(normalized, StringComparison.Ordinal);
            var score = candidate.Length + 35;
            if (index > 40)
                score += 20;
            if (EmbeddedTitleStopwords.Contains(normalized))
                score -= 40;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore >= 30 ? best : null;
    }

    private static string CleanEmbeddedTitleCandidate(string value)
    {
        var title = HorizontalWhitespaceRegex().Replace(value ?? string.Empty, " ").Trim();
        title = LeadingCompactPageNumberRegex().Replace(title, string.Empty);
        title = MostlyUppercaseTrailingMeasureNumberRegex().Replace(title, string.Empty);
        title = title.Trim(' ', '-', ':', ';', '.', ',', '|', '/', '\\', '(', ')', '*', '•');
        return HorizontalWhitespaceRegex().Replace(title, " ").Trim();
    }

    private static bool IsUsefulEmbeddedTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length is < 4 or > 90)
            return false;

        var tokenCount = CountTokens(title);
        if (tokenCount is < 2 or > 10)
            return false;

        if (!title.Any(char.IsLetter) || !LooksLikeMostlyUppercaseTitle(title))
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        if (EmbeddedTitleStopwords.Contains(normalized))
            return false;

        return !ContainsGenericNavigationTitle(normalized);
    }

    private static bool ContainsGenericNavigationTitle(string normalized)
        => normalized.Contains("table des matieres", StringComparison.Ordinal)
            || normalized.Contains("table of contents", StringComparison.Ordinal)
            || normalized.Contains("inhaltsverzeichnis", StringComparison.Ordinal)
            || normalized.Contains("indice general", StringComparison.Ordinal)
            || normalized.Contains("indice de contenido", StringComparison.Ordinal)
            || normalized.Contains("indice de contenidos", StringComparison.Ordinal)
            || normalized.Contains("indice de materias", StringComparison.Ordinal)
            || normalized.Contains("indice analitico", StringComparison.Ordinal)
            || normalized is "sommaire" or "contents" or "sommario" or "sumario" or "indice" or "toc";

    private static bool LooksLikeMostlyUppercaseTitle(string title)
    {
        var letters = title.Where(char.IsLetter).ToArray();
        if (letters.Length < 4)
            return false;

        var uppercase = letters.Count(char.IsUpper);
        return uppercase >= Math.Ceiling(letters.Length * 0.72);
    }

    private static int? ResolveExcerptOffsetStart(ExtractedDocumentUnit? unit, string excerpt)
    {
        if (unit?.OffsetStart is null || string.IsNullOrWhiteSpace(unit.Text) || string.IsNullOrWhiteSpace(excerpt))
            return null;

        var index = unit.Text.IndexOf(excerpt, StringComparison.Ordinal);
        return index >= 0
            ? unit.OffsetStart.Value + index
            : null;
    }

    private static int? ResolveExcerptOffsetEnd(ExtractedDocumentUnit? unit, string excerpt)
    {
        var start = ResolveExcerptOffsetStart(unit, excerpt);
        return start is null
            ? null
            : start.Value + excerpt.Length;
    }

    private static bool Overlaps(int startA, int endA, int startB, int endB)
        => startA <= endB && startB <= endA;

    private static int OverlapScore(int startA, int endA, int startB, int endB)
        => Math.Max(0, Math.Min(endA, endB) - Math.Max(startA, startB) + 1);

    private static int Distance(int left, int right)
        => Math.Abs(left - right);

    private static int CountTokens(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static readonly HashSet<string> EmbeddedTitleStopwords = new(StringComparer.Ordinal)
    {
        "materials",
        "material",
        "components",
        "component",
        "etapes",
        "steps",
        "method",
        "methods",
        "total time",
        "document",
        "page",
        "pages"
    };

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])(?:pour|for|para|per)\s+\d+|(?:^|[^\p{L}\p{N}])\d+\s*[\.)]\s+\p{L}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CountOrStepMarkerRegex();

    [GeneratedRegex(@"^\s*(?:\d{1,4}\s*)?(?:[\p{Lu}][\p{Lu}\p{Ll}'\u2019\-]{2,}|[\p{Lu}]{2,})(?:\s+(?:[\p{Lu}][\p{Lu}\p{Ll}'\u2019\-]{2,}|[\p{Lu}]{2,}|a|au|aux|de|des|du|la|le|les|et|with|and|of|the|to|con|al|alla|mit|und)){1,9}", RegexOptions.CultureInvariant)]
    private static partial Regex StructuredItemTitleLeadRegex();

    [GeneratedRegex(@"^\s*(?:preparation|pr[e\u00e9]paration|realisation|r[e\u00e9]alisation|technique|mat[e\u00e9]riel|materials?|components?|requirements?|items?|elements?|steps?|[e\u00e9]tapes?|temps(?:\s+total)?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GenericStructuredCueLeadRegex();

    [GeneratedRegex(@"^\s*[\p{Lu}]{4,}\s+(?:[\p{Lu}][\p{Lu}\p{Ll}'\u2019\-]{2,}\s+){2,}[\p{Lu}][\p{Lu}\p{Ll}'\u2019\-]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SingleWordFooterBeforeLongTitleRegex();

    [GeneratedRegex(@"\b(?:preparation|pr[e\u00e9]paration|realisation|r[e\u00e9]alisation|technique|temps\s+total|\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|elements?|parts?|pieces?|min|h))\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredItemEvidenceRegex();

    [GeneratedRegex(@"^\s*(?:\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|pieces?|min|h)\b|(?:materials?|components?|requirements?|items?|elements?|steps?|method|procedure|procedures?)\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredBodyLeadEvidenceRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])\d+\s*[\.)]\s+\p{L}", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedStepRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}\p{Nd}])(?=(?:Pour|For|Para|Per|Preparation|Pr[eé]paration|Realisation|R[eé]alisation|Materials?|Components?|Requirements?|Warnings?|Cautions?|Instructions?|Procedure|Procedures|Method|Methods|Etapes?|Steps?)\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuralBoundaryRegex();

    [GeneratedRegex(@"^\s*\d{1,6}(?=(?:Time|Temps|Total|Preparation|Pr[eé]paration|Materials?|Components?|Requirements?|Warnings?|Cautions?|Instructions?|Procedure|Procedures|Method|Methods|\p{Lu}(?:[\p{Ll}]{2,}|['\u2019]\p{Lu}{2,}|\p{Lu}{2,})))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadingCompactPageNumberRegex();

    [GeneratedRegex(@"(?<=\d)(?=(?:Time|Temps|Total|Preparation|Pr[eé]paration|Materials?|Components?|Requirements?|Warnings?|Cautions?|Instructions?|Procedure|Procedures|Method|Methods|\p{Lu}(?:[\p{Ll}]{2,}|['\u2019]\p{Lu}{2,}|\p{Lu}{2,})))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DigitToStructuralLeadRegex();

    [GeneratedRegex(@"(?<=[\p{L}])(?=\d+(?:[,.]\d+)?(?:\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|units?|unites?|items?|pieces?|pages?|s|sec|secs|secondes?|seconds?|min|h)(?:\b|\s)|[\.)]\s*\p{L}))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LetterToNumericMeasureRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}])(?=\d{1,4}(?:/\d{1,4})?\s+[\p{Ll}]{2,24}(?:\s|[,\.;:\)\]]|$))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LowerToCountNounBoundaryRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}])(?=\d{1,4}(?:/\d{1,4})?(?:[,.]\d+)?\s+\p{Ll})", RegexOptions.CultureInvariant)]
    private static partial Regex LowerToLooseQuantityBoundaryRegex();

    [GeneratedRegex(@"(?<=[\.!?:;\)\]\u00ae])(?=\d{1,4}(?:/\d{1,4})?(?:[,.]\d+)?\s+\p{Ll})", RegexOptions.CultureInvariant)]
    private static partial Regex PunctuationToLooseQuantityBoundaryRegex();

    [GeneratedRegex(@"\b(?<word>[\p{L}]+['\u2019][\p{L}]{2,20})(?=\d{1,4}\s+\p{Ll})", RegexOptions.CultureInvariant)]
    private static partial Regex ApostropheWordToNumericWordBoundaryRegex();

    [GeneratedRegex(@"(?<=[\p{L}])(?=\d+\s+\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex LetterToBareNumberedStepRegex();

    [GeneratedRegex(@"(?<=[\.!?])(?=\d+\s+\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex PunctuationToBareNumberedStepRegex();

    [GeneratedRegex(@"(?<=[\.!?:;\)\]\u00ae])(?=\d+(?:[,.]\d+)?(?:/\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|units?|unites?|items?|pieces?|s|sec|secs|secondes?|seconds?|min|h)(?:\b|(?=\d)))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PunctuationToNumericMeasureRegex();

    [GeneratedRegex(@"\b(?<unit>s|sec|secs|secondes?|seconds?|min|h|g|kg|mg|ml|cl|l|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|units?|unites?|items?|pieces?)(?=\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UnitToDigitBoundaryRegex();

    [GeneratedRegex(@"\b(?:items?|elements?|materials?|components?|requirements?|instructions?|\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|units?|unites?|items?|pieces?|s|sec|secs|secondes?|seconds?|min|h))\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QuantityDenseMarkerRegex();

    [GeneratedRegex(@"(?<=[\p{Lu}])(?=\p{Lu}\p{Ll}{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex UppercaseRunToTitleCaseBoundaryRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}])(?=\p{Lu}[\p{Ll}]{2,}\b)", RegexOptions.CultureInvariant)]
    private static partial Regex LowerToTitleCaseWordBoundaryRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}\p{Nd}])(?=(?:Temps|Total|Pour|For|Para|Per|With|Avec|Preparation|Pr[eé]paration|Materials?|Components?|Requirements?|Instructions?|Procedure|Procedures|Method|Steps?|Etapes?|[A-Z]{2,}\b))", RegexOptions.CultureInvariant)]
    private static partial Regex LowerOrDigitToStructuralLeadRegex();

    [GeneratedRegex(@"[ \t\f\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"(?:\s*\r?\n\s*){2,}", RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphWhitespaceRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<title>[\p{Lu}][\p{Lu}\p{Nd}'\u2019\-\s]{4,90}?)(?=(?:\s+\d{1,4}\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|min|h)\b|\s+\p{Lu}[\p{Ll}]{2,}|\s+\p{Lu}['\u2019]\p{Ll}{2,}|\s+\p{Ll}{1,4}\.|\s*$|[\.:\-\u2013\u2014]))", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedUppercaseTitleRegex();

    [GeneratedRegex(@"(?<=[\p{Lu}])\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex MostlyUppercaseTrailingMeasureNumberRegex();

    [GeneratedRegex(@"(?<=[\p{Lu}])(?=\p{Lu}['\u2019]\p{Ll}{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex UppercaseRunToElidedTitleCaseBoundaryRegex();

    [GeneratedRegex(@"\b(?:items?|elements?|materials?|components?|requirements?|instructions?|procedures?|method|steps?|etapes?|preparation|pr[eé]paration|temps total|total time|\d+\s*(?:units?|unites?|items?|pieces?|min|h))\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredContextBeforeTitleRegex();

    [GeneratedRegex(@"\b(?:items?|elements?|preparation|pr[eé]paration|materials?|components?|requirements?|instructions?|procedures?|method|steps?|\d+\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|pieces?))\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredContextAfterTitleRegex();

    [GeneratedRegex(@"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|elements?|parts?|pieces?)\s+(?:de|d['\u2019]|du|des|of)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DanglingStructuredContinuationTailRegex();
}

internal sealed record ProjectedRetrievalChunk(
    int ChunkIndex,
    int? SectionOrdinal,
    int? UnitOrdinal,
    int PageStart,
    int PageEnd,
    string Text,
    int TokenCount,
    byte[] Checksum,
    string ChunkType,
    int? OffsetStart = null,
    int? OffsetEnd = null,
    string ContentRole = RetrievalContentClassifier.ContentRole,
    string? NavigationReason = null,
    string? OriginalChunkType = null,
    double NavigationScore = 0.0,
    double ContentDensityScore = 0.0,
    string? ExtractionTextStatus = null,
    bool ExtractionTextSparse = false,
    bool ExtractionOcrCandidate = false,
    IReadOnlyList<string>? ExtractionQualitySignals = null);

internal sealed record RetrievalChunkExtractionQuality(
    string? TextStatus,
    bool TextSparse,
    bool OcrCandidate,
    IReadOnlyList<string> Signals);
