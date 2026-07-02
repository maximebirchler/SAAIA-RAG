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
        var windowUnits = SelectWindowUnits(orderedUnits, minWords);

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

                    if (window.Count > 0 && LooksLikeCompleteNewPageBoundary(window[^1], candidate))
                    {
                        stoppedBeforeStructuredBoundary = true;
                        break;
                    }

                    var startsStructuredBoundary = window.Count > 0
                        && (LooksLikeHighSignalUnit(candidate)
                            || LooksLikeStructuredItemBoundaryUnit(candidate)
                            || LooksLikeShortStandaloneTitleBoundaryUnit(candidate)
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

                var shouldKeepWindow = tokenTotal >= minWords
                    || ShouldKeepShortRetrievalWindow(window, tokenTotal, minWords);
                if (shouldKeepWindow)
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
                    && (LooksLikeHighSignalUnit(sectionUnits[cursor])
                        || LooksLikeStructuredItemBoundaryUnit(sectionUnits[cursor])
                        || LooksLikeShortStandaloneTitleBoundaryUnit(sectionUnits[cursor])
                        || LooksLikePostFooterStructuredBodyBoundary(sectionUnits[Math.Max(start, cursor - 1)], sectionUnits[cursor])
                        || LooksLikeCompleteNewPageBoundary(sectionUnits[Math.Max(start, cursor - 1)], sectionUnits[cursor])))
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

    private static IReadOnlyList<ExtractedDocumentUnit> SelectWindowUnits(
        IReadOnlyList<ExtractedDocumentUnit> orderedUnits,
        int minWords)
    {
        var reliableUnits = orderedUnits
            .Where(ExtractionQualityPolicy.ShouldUseUnitForRetrievalWindow)
            .Where(unit => !LooksLikeLowSubstanceStandaloneUnit(unit, minWords))
            .ToList();
        if (reliableUnits.Count > 0)
            return reliableUnits;

        var targetedFallbackUnits = orderedUnits
            .Where(ExtractionQualityPolicy.ShouldUseUnitForProfileCards)
            .Where(unit => !LooksLikeLowSubstanceStandaloneUnit(unit, minWords))
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

    private static bool LooksLikeLowSubstanceStandaloneUnit(ExtractedDocumentUnit unit, int minWords)
    {
        if (minWords <= 8)
            return false;
        if (unit.TokenCount >= 8 || string.IsNullOrWhiteSpace(unit.Text))
            return false;
        if (LooksLikeShortStandaloneTitleBoundaryUnit(unit))
            return false;

        var text = InsertStructuralBoundarySpaces(unit.Text).Trim();
        var normalized = text.Trim(' ', '\t', '-', '\u2013', '\u2014', ',', ';', ':', '.', '|', '/', '\\', '(', ')', '[', ']');
        if (normalized.Length == 0)
            return true;

        if (LowSubstanceNumericRangeRegex().IsMatch(normalized))
            return true;

        if (CountStructuredQuantityUnitEvidence(normalized) > 0)
            return true;

        if (unit.TokenCount <= 3)
            return true;

        if (normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2026' or '\u2022'))
            return false;

        var meaningfulWords = SubstantiveWordRegex().Matches(normalized).Count;
        return meaningfulWords < 3;
    }

    private static bool ShouldKeepShortRetrievalWindow(
        IReadOnlyList<ExtractedDocumentUnit> window,
        int tokenTotal,
        int minWords)
    {
        if (window.Count == 0 || tokenTotal >= minWords)
            return false;

        return WindowContainsHighSignalUnit(window)
            || WindowLooksLikeShortTitleWithStructuredBody(window)
            || WindowLooksLikeSubstantiveShortBoundaryLead(window, minWords);
    }

    private static bool WindowLooksLikeShortTitleWithStructuredBody(IReadOnlyList<ExtractedDocumentUnit> window)
    {
        if (window.Count is < 2 or > 3)
            return false;
        if (!LooksLikeShortStandaloneTitleBoundaryUnit(window[0]))
            return false;

        var bodyText = InsertStructuralBoundarySpaces(string.Join(ChunkSeparator, window.Skip(1).Select(static unit => unit.Text)));
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(bodyText));
        if (string.IsNullOrWhiteSpace(normalized) || LooksLikeReferenceList(normalized))
            return false;

        return LooksLikeStructuredBodyLead(bodyText)
            || ContainsProcedureSectionHeading(normalized)
            || CountNumberedSteps(bodyText) >= 1
            || CountStructuredQuantityUnitEvidence(bodyText) >= 1;
    }

    private static bool WindowLooksLikeSubstantiveShortBoundaryLead(
        IReadOnlyList<ExtractedDocumentUnit> window,
        int minWords)
    {
        if (window.Count != 1)
            return false;

        var unit = window[0];
        if (unit.TokenCount < Math.Min(12, Math.Max(8, minWords / 2)))
            return false;

        var text = InsertStructuralBoundarySpaces(unit.Text);
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(text));
        if (string.IsNullOrWhiteSpace(normalized) || LooksLikeReferenceList(normalized))
            return false;

        return text.Any(static ch => ch is '.' or '!' or '?' or '\u2026' or '\u2022')
            || CountStructuredQuantityUnitEvidence(text) > 0
            || CountStructuredQuantityUnitEvidence(normalized) > 0;
    }

    private static bool LooksLikeShortStandaloneTitleBoundaryUnit(ExtractedDocumentUnit unit)
    {
        if (unit.TokenCount is < 2 or > 10 || string.IsNullOrWhiteSpace(unit.Text))
            return false;

        var text = InsertStructuralBoundarySpaces(unit.Text).Trim();
        if (text.Length is < 6 or > 120)
            return false;
        if (text.Any(static ch => ch is '.' or '!' or '?' or '\u2026' or '\u2022' or ':'))
            return false;
        if (CountStructuredQuantityUnitEvidence(text) > 0)
            return false;
        if (GenericStructuredCueLeadRegex().IsMatch(text))
            return false;

        var first = FirstNonWhitespaceOrDefault(text);
        if (first is null || !char.IsLetter(first.Value) || !char.IsUpper(first.Value))
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(text));
        if (string.IsNullOrWhiteSpace(normalized) || LooksLikeReferenceList(normalized))
            return false;

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var contentTokens = tokens.Count(static token =>
            token.Any(char.IsLetter)
            && token.Length >= 3
            && !ShortTitleConnectorTokens.Contains(
                FoldDiacritics(token.Trim(' ', '-', ':', ';', ',', '.', '|', '/', '\\', '(', ')')).ToLowerInvariant()));

        return contentTokens >= 1;
    }

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

        var budgetOverflowLimit = maxWords + 18;
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

        var normalizedCandidate = NormalizeRetrievalText(candidate.Text);
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || LooksLikeReferenceList(normalizedCandidate))
            return false;

        if (LooksLikeDanglingStructuredContinuationTail(previous.Text)
            && (StructuredItemEvidenceRegex().IsMatch(normalizedCandidate)
                || CountStructuredQuantityUnitEvidence(normalizedCandidate) > 0))
        {
            return true;
        }

        return LooksLikeShortStructuredContinuationTail(candidate);
    }

    private static bool LooksLikeDanglingStructuredContinuationTail(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeRetrievalText(text);
        return DanglingStructuredContinuationTailRegex().IsMatch(normalized);
    }

    private static bool LooksLikeShortStructuredContinuationTail(ExtractedDocumentUnit candidate)
    {
        if (candidate.TokenCount is < 4 or > 24 || string.IsNullOrWhiteSpace(candidate.Text))
            return false;

        var normalized = NormalizeRetrievalText(candidate.Text);
        if (string.IsNullOrWhiteSpace(normalized) || LooksLikeReferenceList(normalized))
            return false;
        if (LooksLikeStructuredItemBoundaryUnit(candidate) || LooksLikeStructuredItemTitleLeadBoundary(candidate))
            return false;

        var first = FirstNonWhitespaceOrDefault(normalized);
        if (first is null)
            return false;

        var startsAsProseContinuation = char.IsLetter(first.Value) && char.IsLower(first.Value);
        var startsAsStructuredQuantityTail = char.IsDigit(first.Value)
            && CountStructuredQuantityUnitEvidence(normalized) >= 1;

        if (!startsAsProseContinuation && !startsAsStructuredQuantityTail)
            return false;

        return normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2026')
            || CountStructuredQuantityUnitEvidence(normalized) > 0
            || EndsWithShortNumericFooter(normalized);
    }

    private static char? FirstNonWhitespaceOrDefault(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
                return ch;
        }

        return null;
    }

    private static bool EndsWithShortNumericFooter(string text)
    {
        var normalized = NormalizeRetrievalText(text);
        var match = ShortNumericFooterRegex().Match(normalized);
        return match.Success;
    }

    private static bool LooksLikeCompleteNewPageBoundary(
        ExtractedDocumentUnit previous,
        ExtractedDocumentUnit candidate)
    {
        if (previous.SectionOrdinal != candidate.SectionOrdinal)
            return false;
        if (candidate.PageStart <= previous.PageEnd)
            return false;
        if (!EndsLikeCompleteRetrievalContent(previous.Text))
            return false;
        if (!StartsWithUppercaseDigitOrBullet(candidate.Text))
            return false;

        var normalizedCandidate = NormalizeRetrievalText(candidate.Text);
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || LooksLikeReferenceList(normalizedCandidate))
            return false;

        if (candidate.PageStart > previous.PageEnd + 1)
            return true;
        if (LooksLikeStructuredItemBoundaryUnit(candidate) || LooksLikeStructuredBodyLead(candidate.Text))
            return true;

        return candidate.TokenCount <= 64
            && LooksLikeCompleteSubstantiveBoundarySource(previous)
            && (candidate.Text.Any(static ch => ch is '.' or '!' or '?' or '\u2026' or '\u2022')
                || CountStructuredQuantityUnitEvidence(normalizedCandidate) > 0);
    }

    private static bool LooksLikeCompleteSubstantiveBoundarySource(ExtractedDocumentUnit unit)
    {
        if (LooksLikeHighSignalUnit(unit))
            return true;
        if (unit.TokenCount < 48 || string.IsNullOrWhiteSpace(unit.Text))
            return false;

        var normalized = NormalizeRetrievalText(unit.Text);
        if (string.IsNullOrWhiteSpace(normalized) || LooksLikeReferenceList(normalized))
            return false;

        return normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2026')
            && (StructuredItemEvidenceRegex().IsMatch(normalized)
                || CountStructuredQuantityUnitEvidence(normalized) >= 1
                || CountNumberedSteps(normalized) >= 1
                || LooksLikeStructuredBodyLead(normalized));
    }

    private static bool EndsLikeCompleteRetrievalContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
            return false;

        return trimmed[^1] is '.' or '!' or '?' or '\u2026' or '"' or '\u201d' or '\u00bb';
    }

    private static bool StartsWithUppercaseDigitOrBullet(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            if (ch is '\u2022' or '-' or '\u2013' or '\u2014')
                return true;
            if (char.IsDigit(ch))
                return true;
            if (char.IsLetter(ch))
                return char.IsUpper(ch);

            return false;
        }

        return false;
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
        if (LooksLikeMetadataPrefixedStructuredItemBoundary(lead))
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

    private static bool LooksLikeMetadataPrefixedStructuredItemBoundary(string lead)
    {
        if (!TryStripLeadingStructuredMetadataEntries(lead, out var body))
            return false;
        if (body.Length < 24 || GenericStructuredCueLeadRegex().IsMatch(body))
            return false;

        if (LooksLikeLooseMetadataPrefixedStructuredItemBoundary(body))
            return true;

        var titleLeadMatch = StructuredItemTitleLeadRegex().Match(body);
        if (!titleLeadMatch.Success)
            return false;

        var title = CleanEmbeddedTitleCandidate(titleLeadMatch.Value);
        if (!IsUsefulMetadataPrefixedTitle(title))
            return false;

        var afterTitle = body[Math.Min(body.Length, titleLeadMatch.Length)..];
        if (CountTokens(afterTitle) < 6)
            return false;

        var metadataOrQuantityEvidence = LeadingStructuredMetadataEntryRegex().IsMatch(lead)
            || StructuredItemEvidenceRegex().IsMatch(lead)
            || CountStructuredQuantityUnitEvidence(lead) > 0;
        var bodyEvidence = afterTitle.Any(static ch => ch is '.' or '!' or '?' or ';')
            || CountStructuredQuantityUnitEvidence(afterTitle) > 0
            || CountNumberedSteps(afterTitle) > 0;

        return metadataOrQuantityEvidence && bodyEvidence;
    }

    private static bool LooksLikeLooseMetadataPrefixedStructuredItemBoundary(string body)
    {
        var markerIndex = FindLooseStructuredBodyMarkerIndex(body);
        if (markerIndex is < 8 or > 130)
            return false;

        var title = CleanEmbeddedTitleCandidate(body[..markerIndex]);
        if (!IsUsefulMetadataPrefixedTitle(title))
            return false;

        var afterMarker = body[markerIndex..];
        if (CountTokens(afterMarker) < 6)
            return false;

        return afterMarker.Contains('\u2022', StringComparison.Ordinal)
            || CountNumberedSteps(afterMarker) > 0
            || CountStructuredQuantityUnitEvidence(afterMarker) > 0
            || afterMarker.Any(static ch => ch is '.' or '!' or '?' or ';');
    }

    private static int FindLooseStructuredBodyMarkerIndex(string body)
    {
        var bulletIndex = body.IndexOf('\u2022', StringComparison.Ordinal);
        if (bulletIndex >= 0)
            return bulletIndex;

        var markerMatch = LooseStructuredBodyMarkerRegex().Match(body);
        return markerMatch.Success ? markerMatch.Index : -1;
    }

    private static bool TryStripLeadingStructuredMetadataEntries(string lead, out string body)
    {
        body = HorizontalWhitespaceRegex().Replace(lead ?? string.Empty, " ").TrimStart();
        var strippedAny = false;

        for (var i = 0; i < 4; i++)
        {
            var match = LeadingStructuredMetadataEntryRegex().Match(body);
            if (!match.Success)
                break;

            var rest = match.Groups["rest"].Value.TrimStart();
            if (rest.Length < 12)
                break;

            strippedAny = true;
            body = rest;
        }

        return strippedAny && body.Length > 0;
    }

    private static bool IsUsefulMetadataPrefixedTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length is < 4 or > 90)
            return false;

        var tokenCount = CountTokens(title);
        if (tokenCount is < 2 or > 10)
            return false;
        if (!title.Any(char.IsLetter) || title.Count(char.IsDigit) > 2)
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        if (string.IsNullOrWhiteSpace(normalized)
            || EmbeddedTitleStopwords.Contains(normalized)
            || ContainsGenericNavigationTitle(normalized))
        {
            return false;
        }

        var firstToken = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstToken is null || firstToken.Length >= 5 && firstToken.EndsWith("ez", StringComparison.Ordinal))
            return false;

        return title.Any(char.IsUpper);
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

        if (LooksLikeSpacedLetterFooterTitleNoise(title))
            return false;

        if (title.Any(char.IsDigit) && tokenCount <= 3)
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        return !EmbeddedTitleStopwords.Contains(normalized)
            && !ContainsGenericNavigationTitle(normalized);
    }

    private static bool LooksLikeSpacedLetterFooterTitleNoise(string title)
    {
        var tokens = title
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(static token => token.Trim(' ', '-', '\u2010', '\u2011', '\u2012', '\u2013', '\u2014', '.', ',', ';', ':', '|', '/', '\\', '(', ')'))
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length < 4)
            return false;

        var singleLetterTokens = tokens.Count(static token => token.Length == 1 && token.Any(char.IsLetter));
        return singleLetterTokens >= 4 && singleLetterTokens >= Math.Ceiling(tokens.Length * 0.60d);
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
        var sourceUnitOrdinals = sourceUnits
            .Select(static unit => unit.Ordinal)
            .Distinct()
            .OrderBy(static ordinal => ordinal)
            .ToArray();
        var chunkComposition = sourceUnitOrdinals.Length switch
        {
            0 => "unknown_source_units",
            1 => "single_unit",
            _ => "multi_unit_window"
        };

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
            ExtractionQualitySignals: extractionQuality.Signals,
            SourceUnitOrdinals: sourceUnitOrdinals,
            SourceUnitStartOrdinal: sourceUnitOrdinals.Length == 0 ? null : sourceUnitOrdinals[0],
            SourceUnitEndOrdinal: sourceUnitOrdinals.Length == 0 ? null : sourceUnitOrdinals[^1],
            SourceUnitCount: sourceUnitOrdinals.Length,
            ChunkComposition: chunkComposition);
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
            var titleGroup = match.Groups["title"];
            var candidate = CleanEmbeddedTitleCandidate(titleGroup.Value);
            if (!IsUsefulEmbeddedTitle(candidate))
                continue;
            if (LooksLikeEmbeddedTitleInsideQuantityRun(text, titleGroup.Index, titleGroup.Length))
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
            var exactIndex = text.IndexOf(candidate, StringComparison.Ordinal);
            if (exactIndex >= 0 && LooksLikeEmbeddedTitleInsideQuantityRun(text, exactIndex, candidate.Length))
                continue;

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

    private static bool LooksLikeEmbeddedTitleInsideQuantityRun(string text, int index, int length)
    {
        if (string.IsNullOrWhiteSpace(text) || index <= 0 || length <= 0 || index >= text.Length)
            return false;

        var afterStart = Math.Min(text.Length, index + length);
        var after = text[afterStart..];
        if (ScaleBasisAfterEmbeddedTitleRegex().IsMatch(after))
            return false;

        var afterWindow = after[..Math.Min(after.Length, 120)];
        if (!QuantityAfterEmbeddedTitleRegex().IsMatch(afterWindow))
            return false;

        var before = text[..index];
        var clause = GetTrailingEmbeddedTitlePrefixClause(before);
        if (clause.Length < 6)
            return false;

        return NumericPhraseBeforeEmbeddedTitleRegex().IsMatch(clause)
            || QuantityDenseMarkerRegex().Matches(clause).Count >= 2;
    }

    private static string GetTrailingEmbeddedTitlePrefixClause(string before)
    {
        if (string.IsNullOrWhiteSpace(before))
            return string.Empty;

        var normalized = HorizontalWhitespaceRegex().Replace(before, " ").TrimEnd();
        var boundary = normalized.LastIndexOfAny(['!', '?', '\r', '\n']);
        var clause = boundary >= 0 ? normalized[(boundary + 1)..] : normalized;
        clause = clause.Trim();
        return clause.Length <= 180 ? clause : clause[^180..];
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

    private static readonly HashSet<string> ShortTitleConnectorTokens = new(StringComparer.Ordinal)
    {
        "a",
        "an",
        "and",
        "au",
        "aux",
        "avec",
        "con",
        "de",
        "del",
        "des",
        "di",
        "du",
        "et",
        "for",
        "la",
        "le",
        "les",
        "of",
        "the",
        "to",
        "und",
        "with"
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

    [GeneratedRegex(@"^\s*[\p{L}][\p{L}'\u2019\-]{1,24}(?:\s+[\p{L}][\p{L}'\u2019\-]{1,24}){0,3}\s*:\s*(?:\d+(?:[,.]\d+)?\s*(?:s|sec|secs|secondes?|seconds?|min|mins?|minutes?|h|hr|hrs?|hours?|heures?|j|jr|jours?|d|days?|dias?|g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|km|units?|unites?|items?|pieces?)\b|[^\.;!?]{1,36})(?<rest>\s+.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadingStructuredMetadataEntryRegex();

    [GeneratedRegex(@"\s(?:[\u2022*]\s+|\d{1,3}\s*[\.)]\s+\p{L})", RegexOptions.CultureInvariant)]
    private static partial Regex LooseStructuredBodyMarkerRegex();

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

    [GeneratedRegex(@"^\s*(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ScaleBasisAfterEmbeddedTitleRegex();

    [GeneratedRegex(@"^\s*(?:[\p{L}\p{N}'\u2019\-\.\u00ae&]+\s+){0,8}\d+(?:[,.]\d+)?\s*[\p{L}%]{1,12}\.?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QuantityAfterEmbeddedTitleRegex();

    [GeneratedRegex(@"\b\d+(?:[,.]\d+)?(?:\s+[\p{L}\p{N}'\u2019\.\-\u00ae&]{1,24}){0,10}\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NumericPhraseBeforeEmbeddedTitleRegex();

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

    [GeneratedRegex(@"(?:^|\s)\d{1,4}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNumericFooterRegex();

    [GeneratedRegex(@"^\d{1,4}(?:\s*(?:[-\u2013\u2014\u2022\u00b7/]|to|a|and|et|und)\s*\d{1,4}|\s+\d{1,4})+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LowSubstanceNumericRangeRegex();

    [GeneratedRegex(@"\p{L}[\p{L}\p{M}'\u2019\-]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex SubstantiveWordRegex();
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
    IReadOnlyList<string>? ExtractionQualitySignals = null,
    IReadOnlyList<int>? SourceUnitOrdinals = null,
    int? SourceUnitStartOrdinal = null,
    int? SourceUnitEndOrdinal = null,
    int? SourceUnitCount = null,
    string? ChunkComposition = null);

internal sealed record RetrievalChunkExtractionQuality(
    string? TextStatus,
    bool TextSparse,
    bool OcrCandidate,
    IReadOnlyList<string> Signals);
