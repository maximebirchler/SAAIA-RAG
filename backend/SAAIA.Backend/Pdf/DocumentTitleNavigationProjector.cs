using System.Text.RegularExpressions;

internal static partial class DocumentTitleNavigationProjector
{
    private const int MaxTitleAnchors = 600;
    private const int MaxNavigationEntries = 1200;

    internal static ProjectedDocumentTitleNavigationIndex Project(
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks,
        ProjectedDocumentProfile documentProfile)
        => Project(sections, units, retrievalChunks, retrievalChunks, documentProfile);

    internal static ProjectedDocumentTitleNavigationIndex Project(
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> targetRetrievalChunks,
        IReadOnlyList<ProjectedRetrievalChunk> navigationSourceRetrievalChunks,
        ProjectedDocumentProfile documentProfile)
    {
        var anchors = BuildTitleAnchors(sections, targetRetrievalChunks, documentProfile);
        var navigationEntries = BuildNavigationEntries(units, navigationSourceRetrievalChunks, targetRetrievalChunks, anchors);

        return new ProjectedDocumentTitleNavigationIndex(anchors, navigationEntries);
    }

    private static IReadOnlyList<ProjectedDocumentTitleAnchor> BuildTitleAnchors(
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks,
        ProjectedDocumentProfile documentProfile)
    {
        var candidates = new List<TitleAnchorCandidate>();
        foreach (var section in sections.OrderBy(static section => section.Ordinal))
        {
            AddAnchorCandidate(
                candidates,
                sourceKind: "section",
                sourceOrdinal: section.Ordinal,
                title: section.Title,
                pageStart: section.PageStart,
                pageEnd: section.PageEnd,
                sectionOrdinal: section.Ordinal,
                unitOrdinal: null,
                chunkIndex: null,
                contentCardIndex: null,
                confidence: 0.94);
        }

        var cardIndex = 0;
        foreach (var card in documentProfile.ContentCards)
        {
            if (!ShouldUseContentCardAsTitleAnchor(card))
            {
                cardIndex++;
                continue;
            }

            AddAnchorCandidate(
                candidates,
                sourceKind: "content_card",
                sourceOrdinal: cardIndex,
                title: card.Title,
                pageStart: card.PageStart,
                pageEnd: card.PageEnd,
                sectionOrdinal: null,
                unitOrdinal: null,
                chunkIndex: null,
                contentCardIndex: cardIndex,
                confidence: string.Equals(card.Kind, "section", StringComparison.OrdinalIgnoreCase) ? 0.90 : 0.82);
            cardIndex++;
        }

        foreach (var chunk in retrievalChunks
            .Where(static chunk => !RetrievalContentClassifier.IsPredominantlyNavigationContent(
                chunk.ContentRole,
                chunk.ChunkType,
                chunk.NavigationScore,
                chunk.ContentDensityScore))
            .OrderBy(static chunk => chunk.ChunkIndex))
        {
            var leadTitle = ExtractLeadTitle(chunk.Text);
            if (string.IsNullOrWhiteSpace(leadTitle))
                continue;

            AddAnchorCandidate(
                candidates,
                sourceKind: "chunk_lead",
                sourceOrdinal: chunk.ChunkIndex,
                title: leadTitle,
                pageStart: chunk.PageStart,
                pageEnd: chunk.PageEnd,
                sectionOrdinal: chunk.SectionOrdinal,
                unitOrdinal: chunk.UnitOrdinal,
                chunkIndex: chunk.ChunkIndex,
                contentCardIndex: null,
                confidence: 0.70);
        }

        return candidates
            .GroupBy(static candidate => $"{candidate.NormalizedTitle}|{candidate.PageStart}|{candidate.PageEnd}", StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static candidate => candidate.Confidence)
                .ThenBy(static candidate => SourceKindPriority(candidate.SourceKind))
                .ThenBy(static candidate => candidate.SourceOrdinal ?? int.MaxValue)
                .First())
            .OrderBy(static candidate => candidate.PageStart ?? int.MaxValue)
            .ThenBy(static candidate => SourceKindPriority(candidate.SourceKind))
            .ThenBy(static candidate => candidate.SourceOrdinal ?? int.MaxValue)
            .Take(MaxTitleAnchors)
            .Select(static (candidate, index) => new ProjectedDocumentTitleAnchor(
                AnchorIndex: index,
                SourceKind: candidate.SourceKind,
                SourceOrdinal: candidate.SourceOrdinal,
                Title: candidate.Title,
                NormalizedTitle: candidate.NormalizedTitle,
                TitleTokens: candidate.TitleTokens,
                PageStart: candidate.PageStart,
                PageEnd: candidate.PageEnd,
                SectionOrdinal: candidate.SectionOrdinal,
                UnitOrdinal: candidate.UnitOrdinal,
                ChunkIndex: candidate.ChunkIndex,
                ContentCardIndex: candidate.ContentCardIndex,
                Confidence: candidate.Confidence))
            .ToArray();
    }

    private static IReadOnlyList<ProjectedDocumentNavigationEntry> BuildNavigationEntries(
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> navigationSourceRetrievalChunks,
        IReadOnlyList<ProjectedRetrievalChunk> targetRetrievalChunks,
        IReadOnlyList<ProjectedDocumentTitleAnchor> anchors)
    {
        var entries = new List<ProjectedDocumentNavigationEntry>();
        var seenEntryKeys = new HashSet<string>(StringComparer.Ordinal);
        var maxPage = Math.Max(
            units.Count == 0 ? 0 : units.Max(static unit => unit.PageEnd),
            Math.Max(
                navigationSourceRetrievalChunks.Count == 0 ? 0 : navigationSourceRetrievalChunks.Max(static chunk => chunk.PageEnd),
                targetRetrievalChunks.Count == 0 ? 0 : targetRetrievalChunks.Max(static chunk => chunk.PageEnd)));

        foreach (var chunk in navigationSourceRetrievalChunks
            .Where(static chunk =>
                string.Equals(chunk.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase)
                || string.Equals(chunk.ContentRole, RetrievalContentClassifier.MixedNavigationContentRole, StringComparison.OrdinalIgnoreCase)
                || RetrievalContentClassifier.IsNavigationChunkType(chunk.ChunkType))
            .OrderBy(static chunk => chunk.ChunkIndex))
        {
            foreach (var parsed in ParseNavigationLines(
                chunk.Text,
                maxPage,
                anchors,
                IsStrongInlineNavigationSource(chunk, maxPage),
                AllowsUnanchoredInlineNavigation(chunk)))
            {
                if (entries.Count >= MaxNavigationEntries)
                    break;

                if (!seenEntryKeys.Add($"{parsed.NormalizedLabel}|{parsed.TargetPage}"))
                    continue;

                var resolved = ResolveNavigationTarget(parsed, anchors, targetRetrievalChunks);
                if (string.Equals(resolved.ResolutionMethod, "page_unresolved", StringComparison.OrdinalIgnoreCase)
                    && !ShouldKeepUnresolvedNavigationEntry(chunk, parsed, resolved, maxPage))
                {
                    continue;
                }

                entries.Add(new ProjectedDocumentNavigationEntry(
                    EntryIndex: entries.Count,
                    SourcePage: chunk.PageStart,
                    SourceChunkIndex: chunk.ChunkIndex,
                    SourceUnitOrdinal: chunk.UnitOrdinal,
                    Label: parsed.Label,
                    NormalizedLabel: parsed.NormalizedLabel,
                    LabelTokens: parsed.LabelTokens,
                    TargetPageStart: resolved.TargetPageStart,
                    TargetPageEnd: resolved.TargetPageEnd,
                    TargetAnchorIndex: resolved.TargetAnchorIndex,
                    TargetChunkIndex: resolved.TargetChunkIndex,
                    ResolutionMethod: resolved.ResolutionMethod,
                    Confidence: resolved.Confidence));
            }
        }

        return entries;
    }

    private static IEnumerable<ParsedNavigationEntry> ParseNavigationLines(
        string text,
        int maxPage,
        IReadOnlyList<ProjectedDocumentTitleAnchor> anchors,
        bool allowInlineNavigation,
        bool allowInlineFallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidateLines = SplitNavigationCandidateLines(text);
        var structuredLineCount = 0;
        for (var lineIndex = 0; lineIndex < candidateLines.Count; lineIndex++)
        {
            var line = NormalizeNavigationCandidateLine(candidateLines[lineIndex]);
            if (line.Length < 5 || line.Length > 220)
                continue;

            if (InlinePageNumberRegex().Matches(line).Count > 1
                && !ExplicitNavigationLeaderBeforePageRegex().IsMatch(line))
            {
                continue;
            }

            var parsed = TryParseNavigationLine(line, maxPage);
            if (parsed is null
                && TryParseWrappedNavigationLine(
                    candidateLines,
                    lineIndex,
                    line,
                    maxPage,
                    anchors,
                    out var wrappedParsed))
            {
                parsed = wrappedParsed;
                lineIndex++;
            }

            if (parsed is null)
                continue;
            structuredLineCount++;

            var label = ResolveInlineNavigationLabel(parsed.Label, parsed.TargetPage, anchors, allowInlineFallback);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            var canonicalParsed = string.Equals(
                    label,
                    parsed.Label,
                    StringComparison.Ordinal)
                ? parsed
                : CreateParsedNavigationEntry(
                    label,
                    parsed.TargetPage,
                    maxPage);
            if (canonicalParsed is not null && seen.Add($"{canonicalParsed.NormalizedLabel}|{canonicalParsed.TargetPage}"))
                yield return canonicalParsed;
        }

        if (!allowInlineNavigation || structuredLineCount >= 2)
            yield break;

        var inlineEntries = ParseInlineNavigationEntries(text, maxPage, anchors, allowInlineFallback).ToArray();
        if (inlineEntries.Length < 2)
            yield break;

        foreach (var parsed in inlineEntries)
        {
            if (seen.Add($"{parsed.NormalizedLabel}|{parsed.TargetPage}"))
                yield return parsed;
        }
    }

    private static bool TryParseWrappedNavigationLine(
        IReadOnlyList<string> candidateLines,
        int lineIndex,
        string firstLine,
        int maxPage,
        IReadOnlyList<ProjectedDocumentTitleAnchor> anchors,
        out ParsedNavigationEntry? parsed)
    {
        parsed = null;
        if (lineIndex + 1 >= candidateLines.Count)
            return false;

        var continuationLine = NormalizeNavigationCandidateLine(candidateLines[lineIndex + 1]);
        if (continuationLine.Length < 5
            || continuationLine.Length > 220
            || !ExplicitNavigationLeaderBeforePageRegex().IsMatch(continuationLine))
        {
            return false;
        }

        var combinedLine = CollapseWhitespace($"{firstLine} {continuationLine}");
        if (combinedLine.Length > 220)
            return false;

        var candidate = TryParseNavigationLine(combinedLine, maxPage);
        if (candidate is null)
            return false;

        // Joining wrapped lines is only accepted when the reconstructed title has
        // exact mechanical identity with a title already observed in the document.
        // This prevents a page header or an unrelated preceding line from being
        // attached to an otherwise valid navigation entry.
        if (!anchors.Any(anchor => string.Equals(
                anchor.NormalizedTitle,
                candidate.NormalizedLabel,
                StringComparison.Ordinal)))
        {
            return false;
        }

        parsed = candidate;
        return true;
    }

    private static string NormalizeNavigationCandidateLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var normalized = NavigationTableColumnSeparatorRegex().Replace(
            line,
            " ");
        normalized = LongNavigationLeaderRunRegex().Replace(
            normalized,
            " .. ");
        return CollapseWhitespace(normalized);
    }

    private static IReadOnlyList<string> SplitNavigationCandidateLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (lines.Length > 1)
            return lines;

        return CleanSoftNavigationBreakRegex()
            .Split(normalized)
            .Select(CollapseWhitespace)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static ParsedNavigationEntry? TryParseNavigationLine(string line, int maxPage)
    {
        foreach (var regex in NavigationLineRegexes())
        {
            var match = regex.Match(line);
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups["page"].Value, out var page))
                continue;

            var parsed = CreateParsedNavigationEntry(
                match.Groups["label"].Value,
                page,
                maxPage,
                preserveLeadingSectionNumber: true);
            if (parsed is not null)
                return parsed;
        }

        return null;
    }

    private static IEnumerable<ParsedNavigationEntry> ParseInlineNavigationEntries(
        string text,
        int maxPage,
        IReadOnlyList<ProjectedDocumentTitleAnchor> anchors,
        bool allowFallback)
    {
        var compactText = CollapseWhitespace(text);
        if (compactText.Length < 12)
            yield break;

        var pageMatches = InlinePageNumberRegex().Matches(compactText);
        if (pageMatches.Count < 2)
            yield break;

        var previousPageEnd = 0;
        foreach (Match pageMatch in pageMatches)
        {
            if (!int.TryParse(pageMatch.Groups["page"].Value, out var page))
            {
                previousPageEnd = pageMatch.Index + pageMatch.Length;
                continue;
            }

            var segmentLength = pageMatch.Index - previousPageEnd;
            var segment = segmentLength <= 0 ? string.Empty : compactText.Substring(previousPageEnd, segmentLength);
            previousPageEnd = pageMatch.Index + pageMatch.Length;

            var label = ResolveInlineNavigationLabel(segment, page, anchors, allowFallback);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            var parsed = CreateParsedNavigationEntry(label, page, maxPage);
            if (parsed is not null)
                yield return parsed;
        }
    }

    private static string? ResolveInlineNavigationLabel(
        string segment,
        int targetPage,
        IReadOnlyList<ProjectedDocumentTitleAnchor> anchors,
        bool allowFallback)
    {
        var sourceLabel = CollapseWhitespace(segment).Trim(
            ' ',
            '.',
            ',',
            ':',
            ';',
            '-',
            '|',
            '\u2013',
            '\u2014',
            '\u2022',
            '\u00b7');
        var label = StripNavigationDecorations(sourceLabel);
        if (string.IsNullOrWhiteSpace(label))
            return null;

        var normalizedSourceLabel =
            TitleAnchorNormalizer.NormalizeTitle(sourceLabel);
        var normalizedSegment = TitleAnchorNormalizer.NormalizeTitle(label);
        var anchorMatch = anchors
            .Select(anchor => new
            {
                Anchor = anchor,
                MatchRank = string.Equals(
                        normalizedSourceLabel,
                        anchor.NormalizedTitle,
                        StringComparison.Ordinal)
                    ? 0
                    : string.Equals(
                        normalizedSegment,
                        anchor.NormalizedTitle,
                        StringComparison.Ordinal)
                        ? 1
                        : EndsWithNormalizedPhrase(
                            normalizedSourceLabel,
                            anchor.NormalizedTitle)
                            ? 2
                            : EndsWithNormalizedPhrase(
                                normalizedSegment,
                                anchor.NormalizedTitle)
                                ? 3
                                : EndsWithNormalizedPhrase(
                                    anchor.NormalizedTitle,
                                    normalizedSegment)
                                    ? 4
                                    : 9,
                Distance = PageDistance(anchor.PageStart, targetPage)
            })
            .Where(static item => item.MatchRank < 9)
            .OrderBy(static item => item.MatchRank)
            .ThenBy(static item => item.Distance)
            .ThenByDescending(static item => item.Anchor.TitleTokens.Count)
            .ThenByDescending(static item => item.Anchor.NormalizedTitle.Length)
            .ThenByDescending(static item => item.Anchor.Confidence)
            .FirstOrDefault();

        if (anchorMatch is not null)
            return anchorMatch.Anchor.Title;

        var tokens = TitleAnchorNormalizer.BuildTitleTokens(label, maxTokens: 16);
        var approximateAnchorMatch = anchors
            .Where(static anchor =>
                anchor.PageStart.HasValue)
            .GroupBy(
                static anchor =>
                    $"{anchor.NormalizedTitle}|{anchor.PageStart}",
                StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static anchor =>
                    anchor.Confidence)
                .First())
            .Select(anchor => new
            {
                Anchor = anchor,
                CharacterSimilarity =
                    ComputeNormalizedCharacterSimilarity(
                        normalizedSegment,
                        anchor.NormalizedTitle),
                TokenSimilarity = Math.Min(
                    TitleAnchorNormalizer.ComputeTokenOverlapScore(
                        tokens,
                        anchor.TitleTokens),
                    TitleAnchorNormalizer.ComputeTokenOverlapScore(
                        anchor.TitleTokens,
                        tokens)),
                Distance = PageDistance(
                    anchor.PageStart,
                    targetPage)
            })
            .Where(static item =>
                item.CharacterSimilarity >= 0.88
                || item.TokenSimilarity >= 0.90)
            .OrderByDescending(static item =>
                item.CharacterSimilarity)
            .ThenByDescending(static item =>
                item.TokenSimilarity)
            .ThenBy(static item => item.Distance)
            .ThenByDescending(static item =>
                item.Anchor.Confidence)
            .FirstOrDefault();
        if (approximateAnchorMatch is not null)
            return approximateAnchorMatch.Anchor.Title;

        return allowFallback && label.Length <= 90 && tokens.Length is >= 2 and <= 12
            ? label
            : null;
    }

    private static ParsedNavigationEntry? CreateParsedNavigationEntry(
        string label,
        int page,
        int maxPage,
        bool preserveLeadingSectionNumber = false)
    {
        if (page <= 0 || (maxPage > 0 && page > Math.Max(maxPage + 25, maxPage * 2)))
            return null;

        label = preserveLeadingSectionNumber
            ? TrimNavigationEdgeDecorations(label)
            : StripNavigationDecorations(label);
        if (label.Length > 180)
            label = label[..180].Trim();

        if (!TitleAnchorNormalizer.IsUsefulTitleCandidate(label))
            return null;

        var normalized = TitleAnchorNormalizer.NormalizeTitle(label);
        var tokens = TitleAnchorNormalizer.BuildTitleTokens(label);
        return tokens.Length == 0
            ? null
            : new ParsedNavigationEntry(label, normalized, tokens, page);
    }

    private static NavigationResolution ResolveNavigationTarget(
        ParsedNavigationEntry entry,
        IReadOnlyList<ProjectedDocumentTitleAnchor> anchors,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks)
    {
        var exactAnchor = anchors
            .Where(anchor => string.Equals(anchor.NormalizedTitle, entry.NormalizedLabel, StringComparison.Ordinal))
            .OrderBy(anchor => PageDistance(anchor.PageStart, entry.TargetPage))
            .ThenByDescending(static anchor => anchor.Confidence)
            .FirstOrDefault();

        if (exactAnchor is not null)
        {
            return ResolveAnchoredNavigationTarget(
                entry,
                exactAnchor,
                retrievalChunks,
                "title_exact",
                exactAnchor.PageStart == entry.TargetPage ? 0.96 : 0.88);
        }

        var tokenAnchor = anchors
            .Select(anchor => new
            {
                Anchor = anchor,
                Score = TitleAnchorNormalizer.ComputeTokenOverlapScore(entry.LabelTokens, anchor.TitleTokens)
            })
            .Where(item => item.Score >= 0.80)
            .OrderBy(item => PageDistance(item.Anchor.PageStart, entry.TargetPage))
            .ThenByDescending(item => item.Score)
            .ThenByDescending(item => item.Anchor.Confidence)
            .FirstOrDefault();

        if (tokenAnchor is not null)
        {
            return ResolveAnchoredNavigationTarget(
                entry,
                tokenAnchor.Anchor,
                retrievalChunks,
                "title_token_overlap",
                Math.Min(0.86, 0.70 + (tokenAnchor.Score * 0.16)));
        }

        var targetChunk = retrievalChunks
            .Where(chunk =>
                chunk.PageStart <= entry.TargetPage
                && entry.TargetPage <= chunk.PageEnd
                && !IsNavigationChunk(chunk))
            .OrderByDescending(static chunk => chunk.ContentDensityScore)
            .ThenBy(static chunk => chunk.ChunkIndex)
            .FirstOrDefault();

        if (targetChunk is not null)
        {
            return new NavigationResolution(
                targetChunk.PageStart,
                targetChunk.PageEnd,
                null,
                targetChunk.ChunkIndex,
                "page_content_chunk",
                0.72);
        }

        var nearbyTargetChunk = retrievalChunks
            .Where(chunk =>
                chunk.PageStart <= entry.TargetPage + 2
                && chunk.PageEnd >= entry.TargetPage - 2
                && !IsNavigationChunk(chunk))
            .Select(chunk => new
            {
                Chunk = chunk,
                LabelTokenOverlap = CountLabelTokenOverlap(entry.LabelTokens, chunk.Text),
                Distance = PageRangeDistance(chunk.PageStart, chunk.PageEnd, entry.TargetPage),
                IsBeforeTarget = chunk.PageEnd < entry.TargetPage
            })
            .OrderByDescending(static item => item.LabelTokenOverlap)
            .ThenBy(static item => item.Distance)
            .ThenBy(static item => item.IsBeforeTarget ? 1 : 0)
            .ThenByDescending(static item => item.Chunk.ContentDensityScore)
            .ThenBy(static item => item.Chunk.ChunkIndex)
            .Select(static item => item.Chunk)
            .FirstOrDefault();

        return nearbyTargetChunk is null
            ? new NavigationResolution(entry.TargetPage, entry.TargetPage, null, null, "page_unresolved", 0.48)
            : new NavigationResolution(
                nearbyTargetChunk.PageStart,
                nearbyTargetChunk.PageEnd,
                null,
                nearbyTargetChunk.ChunkIndex,
                "nearby_page_content_chunk",
                0.70);
    }

    private static bool ShouldKeepUnresolvedNavigationEntry(
        ProjectedRetrievalChunk sourceChunk,
        ParsedNavigationEntry entry,
        NavigationResolution resolution,
        int maxPage)
    {
        if (!string.Equals(resolution.ResolutionMethod, "page_unresolved", StringComparison.OrdinalIgnoreCase))
            return false;
        if (resolution.TargetPageStart <= 0)
            return false;
        if (entry.LabelTokens.Count < 2)
            return false;

        var reason = sourceChunk.NavigationReason ?? string.Empty;
        if (string.Equals(reason, "table_of_contents", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "explicit_index_marker", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var earlyWindow = maxPage <= 0 ? 6 : Math.Max(6, (int)Math.Ceiling(maxPage * 0.08));
        return sourceChunk.NavigationScore >= 0.93
            && sourceChunk.PageStart <= earlyWindow;
    }

    private static NavigationResolution ResolveAnchoredNavigationTarget(
        ParsedNavigationEntry entry,
        ProjectedDocumentTitleAnchor anchor,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks,
        string resolutionMethod,
        double confidence)
    {
        var fallbackPageStart = anchor.PageStart ?? entry.TargetPage;
        var fallbackPageEnd = anchor.PageEnd ?? anchor.PageStart ?? entry.TargetPage;
        var resolvedChunk = FindBestSubstantiveContentChunkNearAnchor(entry, anchor, retrievalChunks);
        if (resolvedChunk is null)
        {
            return new NavigationResolution(
                fallbackPageStart,
                fallbackPageEnd,
                anchor.AnchorIndex,
                anchor.ChunkIndex,
                resolutionMethod,
                confidence);
        }

        var method = resolutionMethod;
        if (!PageRangesOverlap(resolvedChunk.PageStart, resolvedChunk.PageEnd, fallbackPageStart, fallbackPageEnd))
        {
            method = resolvedChunk.PageStart > fallbackPageEnd
                ? $"{resolutionMethod}_forward_content_chunk"
                : $"{resolutionMethod}_nearby_content_chunk";
            confidence = Math.Min(confidence, 0.84);
        }

        return new NavigationResolution(
            resolvedChunk.PageStart,
            resolvedChunk.PageEnd,
            anchor.AnchorIndex,
            resolvedChunk.ChunkIndex,
            method,
            confidence);
    }

    private static ProjectedRetrievalChunk? FindBestSubstantiveContentChunkNearAnchor(
        ParsedNavigationEntry entry,
        ProjectedDocumentTitleAnchor anchor,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks)
    {
        var pageStart = anchor.PageStart ?? entry.TargetPage;
        var pageEnd = anchor.PageEnd ?? anchor.PageStart ?? entry.TargetPage;

        if (anchor.ChunkIndex.HasValue)
        {
            var anchoredChunk = retrievalChunks.FirstOrDefault(chunk => chunk.ChunkIndex == anchor.ChunkIndex.Value);
            if (anchoredChunk is not null
                && IsSubstantiveContentChunk(anchoredChunk, anchor.TitleTokens)
                && !LooksLikeTrailingTitleLead(anchoredChunk, anchor))
            {
                return anchoredChunk;
            }
        }

        var overlappingChunks = retrievalChunks
            .Where(chunk => PageRangesOverlap(chunk.PageStart, chunk.PageEnd, pageStart, pageEnd)
                && IsSubstantiveContentChunk(chunk, anchor.TitleTokens)
                && !LooksLikeTrailingTitleLead(chunk, anchor))
            .OrderBy(chunk => PageRangeDistance(chunk.PageStart, chunk.PageEnd, entry.TargetPage))
            .ThenByDescending(static chunk => chunk.ContentDensityScore)
            .ThenByDescending(static chunk => chunk.TokenCount)
            .ThenBy(static chunk => chunk.ChunkIndex)
            .ToArray();
        if (overlappingChunks.Length > 0)
            return overlappingChunks[0];

        var anchorPageLooksWeak = retrievalChunks
            .Where(chunk => PageRangesOverlap(chunk.PageStart, chunk.PageEnd, pageStart, pageEnd)
                && !IsNavigationChunk(chunk))
            .All(chunk => LooksLikeHeaderOnlyChunk(chunk, anchor.TitleTokens) || LooksLikeTrailingTitleLead(chunk, anchor));

        if (!anchorPageLooksWeak)
            return null;

        return retrievalChunks
            .Where(chunk => chunk.PageStart <= pageEnd + 2
                && chunk.PageEnd >= pageStart + 1
                && IsSubstantiveContentChunk(chunk, anchor.TitleTokens))
            .OrderBy(chunk => chunk.PageStart < pageStart ? 1 : 0)
            .ThenBy(chunk => PageRangeDistance(chunk.PageStart, chunk.PageEnd, entry.TargetPage))
            .ThenByDescending(static chunk => chunk.ContentDensityScore)
            .ThenByDescending(static chunk => chunk.TokenCount)
            .ThenBy(static chunk => chunk.ChunkIndex)
            .FirstOrDefault();
    }

    private static bool IsSubstantiveContentChunk(ProjectedRetrievalChunk chunk, IReadOnlyList<string> titleTokens)
    {
        if (IsNavigationChunk(chunk) || LooksLikeHeaderOnlyChunk(chunk, titleTokens))
            return false;

        if (chunk.TokenCount >= 36)
            return true;

        if (chunk.TokenCount >= 24
            && (chunk.ContentDensityScore >= 0.45 || StructuredContentLexicon.ContainsStructuredAnswerCue(chunk.Text)))
        {
            return true;
        }

        return string.Equals(chunk.ContentRole, RetrievalContentClassifier.ContentRole, StringComparison.OrdinalIgnoreCase)
            && chunk.TokenCount >= 18
            && chunk.ContentDensityScore >= 0.60;
    }

    private static bool LooksLikeHeaderOnlyChunk(ProjectedRetrievalChunk chunk, IReadOnlyList<string> titleTokens)
    {
        if (IsNavigationChunk(chunk))
            return true;

        var shortLimit = Math.Max(22, titleTokens.Count + 12);
        if (chunk.TokenCount > shortLimit)
            return false;

        if (chunk.ContentDensityScore < 0.35)
            return true;

        var collapsed = CollapseWhitespace(chunk.Text);
        if (collapsed.Length < 180)
            return true;

        return !StructuredContentLexicon.ContainsStructuredAnswerCue(collapsed);
    }

    private static bool LooksLikeTrailingTitleLead(ProjectedRetrievalChunk chunk, ProjectedDocumentTitleAnchor anchor)
    {
        if (chunk.PageStart >= (anchor.PageStart ?? int.MaxValue))
            return false;

        var normalizedText = TitleAnchorNormalizer.NormalizeTitle(chunk.Text);
        if (string.IsNullOrWhiteSpace(normalizedText) || normalizedText.Length < 80)
            return false;

        var normalizedTitle = anchor.NormalizedTitle;
        var position = normalizedText.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (position < 0 && anchor.TitleTokens.Count >= 2)
        {
            var first = anchor.TitleTokens[0];
            position = normalizedText.IndexOf(first, StringComparison.Ordinal);
        }

        if (position < 0)
            return false;

        return position >= Math.Max(80, (int)Math.Round(normalizedText.Length * 0.65, MidpointRounding.AwayFromZero));
    }

    private static bool IsNavigationChunk(ProjectedRetrievalChunk chunk)
        => RetrievalContentClassifier.IsPredominantlyNavigationContent(
            chunk.ContentRole,
            chunk.ChunkType,
            chunk.NavigationScore,
            chunk.ContentDensityScore);

    private static bool PageRangesOverlap(int leftStart, int leftEnd, int? rightStart, int? rightEnd)
    {
        if (!rightStart.HasValue || !rightEnd.HasValue)
            return false;

        return leftStart <= rightEnd.Value && leftEnd >= rightStart.Value;
    }

    private static void AddAnchorCandidate(
        List<TitleAnchorCandidate> candidates,
        string sourceKind,
        int? sourceOrdinal,
        string? title,
        int? pageStart,
        int? pageEnd,
        int? sectionOrdinal,
        int? unitOrdinal,
        int? chunkIndex,
        int? contentCardIndex,
        double confidence)
    {
        title = CollapseWhitespace(title ?? string.Empty);
        if (!TitleAnchorNormalizer.IsUsefulTitleCandidate(title))
            return;

        var normalized = TitleAnchorNormalizer.NormalizeTitle(title);
        var tokens = TitleAnchorNormalizer.BuildTitleTokens(title);
        if (tokens.Length == 0)
            return;

        candidates.Add(new TitleAnchorCandidate(
            sourceKind,
            sourceOrdinal,
            title,
            normalized,
            tokens,
            pageStart,
            pageEnd ?? pageStart,
            sectionOrdinal,
            unitOrdinal,
            chunkIndex,
            contentCardIndex,
            confidence));
    }

    private static bool ShouldUseContentCardAsTitleAnchor(DocumentProfileContentCard card)
    {
        if (card.PageStart is not > 0)
            return false;

        if (card.PageEnd is > 0 && card.PageEnd < card.PageStart)
            return false;

        return true;
    }

    private static string? ExtractLeadTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var line = text.Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

        if (string.IsNullOrWhiteSpace(line))
            line = text;

        line = CollapseWhitespace(line);
        var match = CleanLeadTitleRegex().Match(line);
        return match.Success ? CollapseWhitespace(match.Groups["title"].Value) : null;
    }

    private static string StripNavigationDecorations(string label)
    {
        label = LeadingNavigationNumberRegex().Replace(label, string.Empty);
        return TrimNavigationEdgeDecorations(label);
    }

    private static string TrimNavigationEdgeDecorations(string label)
        => label.Trim(
            ' ',
            '.',
            ',',
            ':',
            ';',
            '-',
            '|',
            '\u2013',
            '\u2014',
            '\u2022',
            '\u00b7');

    private static string CollapseWhitespace(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespaceRegex().Replace(value, " ").Trim();

    private static int PageDistance(int? page, int targetPage)
        => page is null ? int.MaxValue : Math.Abs(page.Value - targetPage);

    private static int PageRangeDistance(int pageStart, int pageEnd, int targetPage)
    {
        if (pageStart <= targetPage && targetPage <= pageEnd)
            return 0;
        if (targetPage < pageStart)
            return pageStart - targetPage;
        return targetPage - pageEnd;
    }

    private static int CountLabelTokenOverlap(IReadOnlyList<string> labelTokens, string? text)
    {
        if (labelTokens.Count == 0 || string.IsNullOrWhiteSpace(text))
            return 0;

        var normalizedText = " " + TitleAnchorNormalizer.NormalizeTitle(text) + " ";
        return labelTokens
            .Distinct(StringComparer.Ordinal)
            .Count(token => normalizedText.Contains(" " + token + " ", StringComparison.Ordinal));
    }

    private static int SourceKindPriority(string sourceKind)
        => sourceKind switch
        {
            "section" => 0,
            "content_card" => 1,
            "chunk_lead" => 2,
            _ => 9
        };

    private static bool IsStrongInlineNavigationSource(ProjectedRetrievalChunk chunk, int maxPage)
    {
        var reason = chunk.NavigationReason ?? string.Empty;
        if (string.Equals(reason, "table_of_contents", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "explicit_index_marker", StringComparison.OrdinalIgnoreCase))
            return true;

        if (chunk.NavigationScore >= 0.90)
            return true;

        if (chunk.NavigationScore < 0.80)
            return false;

        if (!string.Equals(reason, "inline_page_number_list", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(reason, "numeric_title_catalog", StringComparison.OrdinalIgnoreCase))
            return false;

        var earlyWindow = maxPage <= 0 ? 6 : Math.Max(6, (int)Math.Ceiling(maxPage * 0.08));
        return chunk.PageStart <= earlyWindow;
    }

    private static bool AllowsUnanchoredInlineNavigation(ProjectedRetrievalChunk chunk)
    {
        var reason = chunk.NavigationReason ?? string.Empty;
        return string.Equals(reason, "table_of_contents", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "explicit_index_marker", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsNormalizedPhrase(string haystack, string needle)
        => !string.IsNullOrWhiteSpace(haystack)
            && !string.IsNullOrWhiteSpace(needle)
            && (string.Equals(haystack, needle, StringComparison.Ordinal)
                || haystack.StartsWith(needle + " ", StringComparison.Ordinal)
                || haystack.EndsWith(" " + needle, StringComparison.Ordinal)
                || haystack.Contains(" " + needle + " ", StringComparison.Ordinal));

    private static bool EndsWithNormalizedPhrase(string haystack, string needle)
        => !string.IsNullOrWhiteSpace(haystack)
            && !string.IsNullOrWhiteSpace(needle)
            && (string.Equals(haystack, needle, StringComparison.Ordinal)
                || haystack.EndsWith(" " + needle, StringComparison.Ordinal));

    private static double ComputeNormalizedCharacterSimilarity(
        string left,
        string right)
    {
        var compactLeft = CompactNormalizedTitle(left);
        var compactRight = CompactNormalizedTitle(right);
        if (compactLeft.Length == 0 || compactRight.Length == 0)
            return 0.0;
        if (string.Equals(
                compactLeft,
                compactRight,
                StringComparison.Ordinal))
        {
            return 1.0;
        }

        var previous = new int[compactRight.Length + 1];
        var current = new int[compactRight.Length + 1];
        for (var column = 0; column <= compactRight.Length; column++)
            previous[column] = column;

        for (var row = 1; row <= compactLeft.Length; row++)
        {
            current[0] = row;
            for (var column = 1;
                 column <= compactRight.Length;
                 column++)
            {
                var substitutionCost =
                    compactLeft[row - 1] == compactRight[column - 1]
                        ? 0
                        : 1;
                current[column] = Math.Min(
                    Math.Min(
                        current[column - 1] + 1,
                        previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return 1.0
               - previous[compactRight.Length]
               / (double)Math.Max(
                   compactLeft.Length,
                   compactRight.Length);
    }

    private static string CompactNormalizedTitle(string value)
        => string.Concat(
            value.Where(static character =>
                char.IsLetterOrDigit(character)));

    private static Regex[] NavigationLineRegexes()
        =>
        [
            CleanLabelLeaderPageRegex(),
            CleanNumberedLabelLeaderPageRegex(),
            CleanCompactLabelPageRegex(),
            CleanPageThenLabelRegex()
        ];

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?:\s{3,}|\s+[\.\u00b7\u2022]{2,}\s+)", RegexOptions.CultureInvariant)]
    private static partial Regex CleanSoftNavigationBreakRegex();

    [GeneratedRegex(@"\s*\|\s*", RegexOptions.CultureInvariant)]
    private static partial Regex NavigationTableColumnSeparatorRegex();

    [GeneratedRegex(@"[\.\u00b7\u2022]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongNavigationLeaderRunRegex();

    [GeneratedRegex(@"[\.\u00b7\u2022]{2,}\s*\d{1,4}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitNavigationLeaderBeforePageRegex();

    [GeneratedRegex(@"^(?<label>.{3,180}?)(?:\s*[\.\u00b7\u2022]{2,}\s*|\s+[-\u2013\u2014]\s+|\s{2,})(?<page>\d{1,4})(?![\d.])\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CleanLabelLeaderPageRegex();

    [GeneratedRegex(@"^(?:\d{1,3}(?:[.)\-:]\d{1,3})*[.)\-:]?\s+)?(?<label>.{3,180}?)(?:\s*[\.\u00b7\u2022]{2,}\s*|\s+[-\u2013\u2014]\s+|\s{2,}|\s+page\s+)(?<page>\d{1,4})(?![\d.])\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CleanNumberedLabelLeaderPageRegex();

    [GeneratedRegex(@"^(?<label>[\p{L}\p{N}][\p{L}\p{N}'\u2019/&+(),:;.\-\s]{3,170}?[^\d\s])\s+(?<![\d.])(?<page>\d{1,4})(?![\d.])\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CleanCompactLabelPageRegex();

    [GeneratedRegex(@"^(?:p(?:age)?\.?\s*)?(?<![\d.])(?<page>\d{1,4})(?![\d.])\s+[-\u2013\u2014:]\s+(?<label>.{3,180})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CleanPageThenLabelRegex();

    [GeneratedRegex(@"(?<![\d.])(?<page>\d{1,4})(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex InlinePageNumberRegex();

    [GeneratedRegex(@"^(?<title>.{4,120}?)(?:\s{2,}|[.:;\u2013\u2014-]\s+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CleanLeadTitleRegex();

    [GeneratedRegex(@"(?:\s{3,}|\s+[\.·•]{2,}\s+)", RegexOptions.CultureInvariant)]
    private static partial Regex SoftNavigationBreakRegex();

    [GeneratedRegex(@"^(?<label>.{3,180}?)(?:\s*[\.·•]{2,}\s*|\s+[-–—]\s+|\s{2,})(?<page>\d{1,4})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelLeaderPageRegex();

    [GeneratedRegex(@"^(?:\d{1,3}(?:[.)\-:]\d{1,3})*[.)\-:]?\s+)?(?<label>.{3,180}?)(?:\s*[\.·•]{2,}\s*|\s+[-–—]\s+|\s{2,}|\s+page\s+)(?<page>\d{1,4})\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NumberedLabelLeaderPageRegex();

    [GeneratedRegex(@"^(?<label>[\p{L}\p{N}][\p{L}\p{N}'’/&+(),:;.\-\s]{3,170}?[^\d\s])\s+(?<page>\d{1,4})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactLabelPageRegex();

    [GeneratedRegex(@"^(?:p(?:age)?\.?\s*)?(?<page>\d{1,4})\s+[-–—:]\s+(?<label>.{3,180})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PageThenLabelRegex();

    [GeneratedRegex(@"^\s*(?:\d{1,3}(?:[.)\-:]\d{1,3})*[.)\-:]?\s+|[ivxlcdm]{1,8}[.)\-:]\s+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadingNavigationNumberRegex();

    [GeneratedRegex(@"^(?<title>.{4,120}?)(?:\s{2,}|[.:;–—-]\s+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadTitleRegex();

    private sealed record TitleAnchorCandidate(
        string SourceKind,
        int? SourceOrdinal,
        string Title,
        string NormalizedTitle,
        IReadOnlyList<string> TitleTokens,
        int? PageStart,
        int? PageEnd,
        int? SectionOrdinal,
        int? UnitOrdinal,
        int? ChunkIndex,
        int? ContentCardIndex,
        double Confidence);

    private sealed record ParsedNavigationEntry(
        string Label,
        string NormalizedLabel,
        IReadOnlyList<string> LabelTokens,
        int TargetPage);

    private sealed record NavigationResolution(
        int? TargetPageStart,
        int? TargetPageEnd,
        int? TargetAnchorIndex,
        int? TargetChunkIndex,
        string ResolutionMethod,
        double Confidence);
}

internal sealed record ProjectedDocumentTitleNavigationIndex(
    IReadOnlyList<ProjectedDocumentTitleAnchor> TitleAnchors,
    IReadOnlyList<ProjectedDocumentNavigationEntry> NavigationEntries);

internal sealed record ProjectedDocumentTitleAnchor(
    int AnchorIndex,
    string SourceKind,
    int? SourceOrdinal,
    string Title,
    string NormalizedTitle,
    IReadOnlyList<string> TitleTokens,
    int? PageStart,
    int? PageEnd,
    int? SectionOrdinal,
    int? UnitOrdinal,
    int? ChunkIndex,
    int? ContentCardIndex,
    double Confidence);

internal sealed record ProjectedDocumentNavigationEntry(
    int EntryIndex,
    int SourcePage,
    int? SourceChunkIndex,
    int? SourceUnitOrdinal,
    string Label,
    string NormalizedLabel,
    IReadOnlyList<string> LabelTokens,
    int? TargetPageStart,
    int? TargetPageEnd,
    int? TargetAnchorIndex,
    int? TargetChunkIndex,
    string ResolutionMethod,
    double Confidence);
