using System.Text;
using System.Text.RegularExpressions;

internal static partial class DocumentSectionExtractor
{
    private static readonly HashSet<string> SingleNumberedConnectorTokens = new(StringComparer.Ordinal)
    {
        "a",
        "an",
        "and",
        "de",
        "des",
        "du",
        "et",
        "for",
        "of",
        "par",
        "para",
        "per",
        "por",
        "pour",
        "to",
        "und"
    };

    public static IReadOnlyList<ExtractedDocumentSection> Extract(IReadOnlyList<ExtractedPdfPage> pages)
    {
        if (pages.Count == 0)
            return Array.Empty<ExtractedDocumentSection>();

        var pageLines = pages
            .Select(p => new PageLines(
                p.PageNumber,
                SplitLines(p.Text)))
            .ToList();

        var duplicateFrequency = pageLines
            .SelectMany(p => p.Lines.Distinct(StringComparer.Ordinal))
            .GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var candidates = new List<SectionCandidate>();
        foreach (var page in pageLines)
        {
            for (var i = 0; i < page.Lines.Count; i++)
            {
                var line = page.Lines[i];
                if (duplicateFrequency.TryGetValue(line, out var repeats) && repeats >= 3)
                    continue;

                if (LooksLikeNavigationListLine(line, page.Lines, i))
                    continue;

                if (!TryClassifyHeading(line, out var level)
                    && !TryClassifyContextualMixedCaseHeading(line, page.Lines, i, out level))
                    continue;

                var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (LooksLikeInlineDenseHeadingNoise(line, page.Lines, i, words))
                    continue;
                if (LooksLikeContextualRosterHeadingNoise(line, page.Lines, i, words))
                    continue;
                if (LooksLikeWeakTitleCaseHeadingNoise(line, page.Lines, i, words))
                    continue;

                candidates.Add(new SectionCandidate(page.PageNumber, i + 1, line, level));
            }
        }

        var selectedCandidates = MergeAdjacentContinuationCandidates(
            FilterDenseLayoutNoise(candidates, pages.Count),
            pageLines);

        if (selectedCandidates.Count == 0)
        {
            return new[]
            {
                new ExtractedDocumentSection(
                    Ordinal: 0,
                    Title: "Document",
                    Level: 1,
                    PageStart: pages.Min(p => p.PageNumber),
                    PageEnd: pages.Max(p => p.PageNumber),
                    StartLine: null,
                    EndLine: null)
            };
        }

        var sections = new List<ExtractedDocumentSection>(selectedCandidates.Count);
        for (var idx = 0; idx < selectedCandidates.Count; idx++)
        {
            var current = selectedCandidates[idx];
            var next = idx + 1 < selectedCandidates.Count ? selectedCandidates[idx + 1] : null;
            var (pageEnd, endLine) = ResolveSectionEnd(current, next, pages.Max(p => p.PageNumber));

            sections.Add(new ExtractedDocumentSection(
                Ordinal: idx,
                Title: current.Title,
                Level: current.Level,
                PageStart: current.PageNumber,
                PageEnd: Math.Max(current.PageNumber, pageEnd),
                StartLine: current.LineNumber,
                EndLine: endLine));
        }

        return sections;
    }

    private static (int PageEnd, int? EndLine) ResolveSectionEnd(
        SectionCandidate current,
        SectionCandidate? next,
        int documentLastPage)
    {
        if (next is null)
            return (documentLastPage, null);

        if (next.PageNumber == current.PageNumber)
            return (current.PageNumber, Math.Max(current.LineNumber, next.LineNumber - 1));

        if (next.PageNumber > current.PageNumber && next.LineNumber > 1)
            return (next.PageNumber, next.LineNumber - 1);

        return (Math.Max(current.PageNumber, next.PageNumber - 1), null);
    }

    private static IReadOnlyList<SectionCandidate> FilterDenseLayoutNoise(
        IReadOnlyList<SectionCandidate> candidates,
        int pageCount)
    {
        if (candidates.Count == 0)
            return candidates;

        var denseThreshold = Math.Max(80, Math.Max(1, pageCount) * 4);
        if (candidates.Count < denseThreshold)
            return candidates;

        var highConfidence = candidates
            .Where(static candidate => LooksLikeHighConfidenceDenseSectionTitle(candidate.Title))
            .ToArray();

        return highConfidence.Length > 0 ? highConfidence : candidates;
    }

    private static IReadOnlyList<SectionCandidate> MergeAdjacentContinuationCandidates(
        IReadOnlyList<SectionCandidate> candidates,
        IReadOnlyList<PageLines> pageLines)
    {
        if (candidates.Count < 2)
            return candidates;

        var merged = new List<SectionCandidate>(candidates.Count);
        foreach (var candidate in candidates
                     .OrderBy(static candidate => candidate.PageNumber)
                     .ThenBy(static candidate => candidate.LineNumber))
        {
            if (merged.Count > 0)
            {
                var previous = merged[^1];
                if (candidate.PageNumber == previous.PageNumber
                    && candidate.LineNumber == previous.LineNumber + 1
                    && candidate.Level == previous.Level
                    && (LooksLikeHeadingContinuation(candidate.Title)
                        || LooksLikeUnmarkedSplitHeadingContinuation(previous, candidate, pageLines)
                        || LooksLikeContextualMixedCaseSplitHeadingContinuation(previous, candidate, pageLines)))
                {
                    merged[^1] = previous with
                    {
                        Title = NormalizeWhitespace($"{previous.Title} {candidate.Title}")
                    };
                    continue;
                }
            }

            merged.Add(candidate);
        }

        return merged;
    }

    private static bool LooksLikeUnmarkedSplitHeadingContinuation(
        SectionCandidate previous,
        SectionCandidate candidate,
        IReadOnlyList<PageLines> pageLines)
    {
        var previousTitle = NormalizeWhitespace(previous.Title);
        var candidateTitle = NormalizeWhitespace(candidate.Title);
        if (previousTitle.Length + candidateTitle.Length + 1 > 140)
            return false;

        if (candidateTitle.Any(static ch => char.IsDigit(ch) || ch is ':' or ';' or '|' or '\u2022'))
            return false;

        var previousWords = previousTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidateWords = candidateTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (previousWords.Length is < 3 or > 10 || candidateWords.Length is < 2 or > 4)
            return false;

        if (IsContinuationConnector(candidateWords[0]))
            return false;

        if (!LooksLikeMostlyUppercaseHeading(previousTitle) || !LooksLikeMostlyUppercaseHeading(candidateTitle))
            return false;

        return HasStructuredBodyImmediatelyAfter(candidate, pageLines);
    }

    private static bool HasStructuredBodyImmediatelyAfter(
        SectionCandidate candidate,
        IReadOnlyList<PageLines> pageLines)
    {
        var page = pageLines.FirstOrDefault(p => p.PageNumber == candidate.PageNumber);
        if (page is null)
            return false;

        var start = candidate.LineNumber;
        var inspected = 0;
        for (var i = start; i < page.Lines.Count && inspected < 5; i++, inspected++)
        {
            var line = page.Lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (LooksLikeStructuredBodyAfterSplitHeading(line, words))
                return true;

            if (TryClassifyHeading(line, out _))
                return false;
        }

        return false;
    }

    private static bool TryClassifyContextualMixedCaseHeading(
        string line,
        IReadOnlyList<string> lines,
        int index,
        out int level)
    {
        level = 1;
        var normalized = NormalizeWhitespace(line);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!LooksLikeMixedCaseContextualTitleCandidate(normalized, words))
            return false;

        if (CountStructuredMetadataLinesBefore(lines, index, maxLookback: 5) < 2)
            return false;

        return HasStructuredBodyAfterContextualHeading(lines, index);
    }

    private static bool LooksLikeMixedCaseContextualTitleCandidate(string line, IReadOnlyList<string> words)
    {
        if (line.Length is < 4 or > 120)
            return false;
        if (words.Count is < 1 or > 10)
            return false;
        if (line.Any(static ch => char.IsDigit(ch) || ch is ':' or ';' or '|' or '\u2022'))
            return false;
        if (line.EndsWith(".", StringComparison.Ordinal)
            || line.EndsWith(",", StringComparison.Ordinal)
            || line.EndsWith(";", StringComparison.Ordinal))
        {
            return false;
        }

        if (LooksLikeHeaderFooterLine(line)
            || LooksLikeShortMetadataValueFragment(line, words)
            || LooksLikeStructuredMetadataLine(line)
            || LooksLikeBodySentenceFragment(line, words))
        {
            return false;
        }

        var firstLetter = line.FirstOrDefault(char.IsLetter);
        if (firstLetter == default || !char.IsUpper(firstLetter))
            return false;

        var letterCount = line.Count(char.IsLetter);
        if (letterCount < 4)
            return false;

        var substantiveWords = words
            .Select(TrimToken)
            .Where(static word => word.Count(char.IsLetter) >= 3)
            .ToArray();
        if (substantiveWords.Length == 0)
            return false;

        return substantiveWords.Any(static word =>
        {
            var first = word.FirstOrDefault(char.IsLetter);
            return first != default && char.IsUpper(first);
        });
    }

    private static int CountStructuredMetadataLinesBefore(
        IReadOnlyList<string> lines,
        int index,
        int maxLookback)
    {
        var count = 0;
        var first = Math.Max(0, index - maxLookback);
        for (var i = index - 1; i >= first; i--)
        {
            var line = lines[i];
            if (LooksLikeStructuredMetadataLine(line))
                count++;
        }

        return count;
    }

    private static bool HasStructuredBodyAfterContextualHeading(IReadOnlyList<string> lines, int index)
    {
        var inspected = 0;
        for (var i = index + 1; i < lines.Count && inspected < 6; i++, inspected++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (LooksLikeStructuredBodyAfterSplitHeading(line, words))
                return true;

            if (LooksLikeMixedCaseContextualTitleCandidate(line, words))
                continue;

            if (TryClassifyHeading(line, out _))
                return false;
        }

        return false;
    }

    private static bool LooksLikeContextualMixedCaseSplitHeadingContinuation(
        SectionCandidate previous,
        SectionCandidate candidate,
        IReadOnlyList<PageLines> pageLines)
    {
        var previousTitle = NormalizeWhitespace(previous.Title);
        var candidateTitle = NormalizeWhitespace(candidate.Title);
        if (previousTitle.Length + candidateTitle.Length + 1 > 140)
            return false;

        var previousWords = previousTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidateWords = candidateTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!LooksLikeMixedCaseContextualTitleCandidate(previousTitle, previousWords)
            || !LooksLikeMixedCaseContextualTitleCandidate(candidateTitle, candidateWords))
        {
            return false;
        }

        return HasStructuredBodyImmediatelyAfter(candidate, pageLines);
    }

    private static bool LooksLikeStructuredBodyAfterSplitHeading(string line, IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return false;

        if (line.TrimStart().StartsWith('\u2022')
            && (words.Count >= 4 || QuantityTokenRegex().IsMatch(line)))
        {
            return true;
        }

        if (LooksLikeShortQuantityLeadFragment(line, words)
            || LooksLikeMeasureDenseFragment(line, words)
            || QuantityTokenRegex().IsMatch(line))
        {
            return true;
        }

        if (SingleNumberHeadingLeadRegex().IsMatch(line)
            && words.Count >= 4
            && words.Skip(1).Any(static word => word.Length >= 4 && char.IsLower(word[0])))
        {
            return true;
        }

        return LooksLikeBodySentenceFragment(line, words);
    }

    private static bool LooksLikeStructuredMetadataLine(string line)
    {
        var normalized = NormalizeWhitespace(line);
        if (normalized.Length is < 3 or > 80)
            return false;

        return ContextualHeadingMetadataRegex().IsMatch(normalized);
    }

    private static List<string> SplitLines(string text)
        => text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeWhitespace)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    private static string NormalizeWhitespace(string text)
        => Regex.Replace(text, @"\s+", " ").Trim();

    private static bool TryClassifyHeading(string line, out int level)
    {
        level = 1;

        if (line.Length < 3 || line.Length > 140)
            return false;

        if (line.EndsWith(".", StringComparison.Ordinal))
            return false;

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 16)
            return false;

        if (LooksLikeNavigationHeadingTitle(line))
            return false;
        if (LooksLikeHeaderFooterLine(line)
            || LooksLikeShortNumericOrRangeFragment(line, words)
            || LooksLikeShortQuantityLeadFragment(line, words)
            || LooksLikeSingleNumberedConnectorFragment(line, words)
            || LooksLikeShortMixedCaseOcrHeadingNoise(line, words)
            || LooksLikeShortLegendOrAbbreviationFragment(line, words)
            || LooksLikeShortTrailingPunctuationFragment(line, words)
            || LooksLikeSpacedLetterNoise(line)
            || LooksLikeMeasureDenseFragment(line, words)
            || LooksLikeShortMetadataValueFragment(line, words)
            || LooksLikeShortAcronymLeadFragment(line, words)
            || LooksLikeShortHyphenatedBodyFragment(line, words)
            || LooksLikeShortListValueFragment(line, words)
            || LooksLikeShortCommaSeparatedLabelFragment(line, words)
            || LooksLikeShortStatusValueFragment(line, words)
            || LooksLikeFigureOrTableCaptionLine(line)
            || LooksLikeAddressLine(line, words)
            || LooksLikeOrganizationFooterLine(line, words)
            || LooksLikeNumberedTableRowFragment(line, words)
            || LooksLikeBodySentenceFragment(line, words))
        {
            return false;
        }

        if (NumberedHeadingRegex().IsMatch(line))
        {
            var prefix = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
            level = Math.Clamp(prefix.Count(c => c == '.') + 1, 1, 6);
            return true;
        }

        var romanHeadingMatch = RomanHeadingRegex().Match(line);
        if (romanHeadingMatch.Success)
        {
            if (!LooksLikeRomanHeadingRemainder(romanHeadingMatch.Groups["heading"].Value))
                return false;

            level = 1;
            return true;
        }

        var letterCount = line.Count(char.IsLetter);
        if (letterCount < 3)
            return false;

        var uppercaseLetters = line.Count(char.IsUpper);
        var titleCaseWords = words.Count(static w => w.Length > 0 && char.IsUpper(w[0]));

        if (uppercaseLetters >= Math.Max(3, letterCount / 2))
            return true;

        if (titleCaseWords >= Math.Max(2, words.Length - 1))
            return true;

        return false;
    }

    private static bool LooksLikeHighConfidenceDenseSectionTitle(string line)
    {
        var normalized = NormalizeWhitespace(line);
        if (normalized.Length is < 4 or > 120)
            return false;

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 14)
            return false;

        if (LooksLikeHeaderFooterLine(normalized)
            || LooksLikeShortNumericOrRangeFragment(normalized, words)
            || LooksLikeShortQuantityLeadFragment(normalized, words)
            || LooksLikeShortLegendOrAbbreviationFragment(normalized, words)
            || LooksLikeShortTrailingPunctuationFragment(normalized, words)
            || LooksLikeMeasureDenseFragment(normalized, words)
            || LooksLikeShortMetadataValueFragment(normalized, words)
            || LooksLikeShortListValueFragment(normalized, words)
            || LooksLikeShortCommaSeparatedLabelFragment(normalized, words)
            || LooksLikeShortStatusValueFragment(normalized, words)
            || LooksLikeFigureOrTableCaptionLine(normalized)
            || LooksLikeAddressLine(normalized, words)
            || LooksLikeOrganizationFooterLine(normalized, words)
            || LooksLikeNumberedTableRowFragment(normalized, words)
            || LooksLikeBodySentenceFragment(normalized, words))
        {
            return false;
        }

        if (LooksLikeHierarchicalNumberedHeading(normalized))
            return true;

        if (LooksLikeSingleNumberedLayoutFragment(normalized, words))
            return false;

        if (LooksLikeMostlyUppercaseHeading(normalized))
            return true;

        return LooksLikeDenseTitleCaseHeading(normalized, words);
    }

    private static bool LooksLikeHierarchicalNumberedHeading(string line)
    {
        var match = NumberedHeadingRegex().Match(line);
        return match.Success && match.Value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].Contains('.', StringComparison.Ordinal);
    }

    private static bool LooksLikeSingleNumberedLayoutFragment(string line, IReadOnlyList<string> words)
    {
        if (!SingleNumberHeadingLeadRegex().IsMatch(line))
            return false;

        if (words.Count > 4)
            return true;

        return QuantityTokenRegex().IsMatch(line)
            || words.Skip(1).Any(static word =>
            {
                var token = TrimToken(word);
                return token.Length is <= 2 or >= 18;
            });
    }

    private static bool LooksLikeSingleNumberedConnectorFragment(string line, IReadOnlyList<string> words)
    {
        if (words.Count is < 3 or > 6)
            return false;

        var normalized = NormalizeWhitespace(line);
        if (!SingleNumberHeadingLeadRegex().IsMatch(normalized))
            return false;
        if (normalized.Contains('.', StringComparison.Ordinal))
            return false;

        var firstValueToken = TrimToken(words[1]).ToLowerInvariant();
        if (!SingleNumberedConnectorTokens.Contains(firstValueToken))
            return false;

        return words
            .Skip(1)
            .Select(TrimToken)
            .Where(static token => token.Length > 0)
            .All(static token =>
            {
                var first = token.FirstOrDefault(char.IsLetter);
                return first == default || char.IsLower(first);
            });
    }

    private static bool LooksLikeMostlyUppercaseHeading(string line)
    {
        var letters = line.Where(char.IsLetter).ToArray();
        if (letters.Length < 4)
            return false;

        var uppercase = letters.Count(char.IsUpper);
        return uppercase >= Math.Ceiling(letters.Length * 0.72);
    }

    private static bool LooksLikeDenseTitleCaseHeading(string line, IReadOnlyList<string> words)
    {
        if (line.Any(static ch => char.IsDigit(ch) || ch is ':' or ';' or '|' or '\u2022'))
            return false;
        if (words.Count is < 2 or > 10)
            return false;

        var letterCount = line.Count(char.IsLetter);
        if (letterCount < 8)
            return false;

        var titleCaseWords = words.Count(static word =>
        {
            var firstLetter = word.FirstOrDefault(char.IsLetter);
            return firstLetter != default && char.IsUpper(firstLetter);
        });

        return titleCaseWords >= Math.Max(2, words.Count - 1);
    }

    private static bool LooksLikeWeakTitleCaseHeadingNoise(
        string line,
        IReadOnlyList<string> lines,
        int index,
        IReadOnlyList<string> words)
    {
        if (!LooksLikeWeakStandaloneTitleCaseHeading(line, words))
            return false;

        return !HasNarrativeBodyAfterWeakTitle(lines, index);
    }

    private static bool LooksLikeWeakStandaloneTitleCaseHeading(string line, IReadOnlyList<string> words)
    {
        var normalized = NormalizeWhitespace(line);
        if (normalized.Length is < 4 or > 100)
            return false;
        if (words.Count is < 2 or > 8)
            return false;
        if (NumberedHeadingRegex().IsMatch(normalized)
            || RomanHeadingRegex().IsMatch(normalized)
            || LooksLikeMostlyUppercaseHeading(normalized))
        {
            return false;
        }
        if (normalized.Any(static ch => ch is ':' or ';' or '|' or '\u2022'))
            return false;
        if (LooksLikeFigureOrTableCaptionLine(normalized)
            || LooksLikeAddressLine(normalized, words)
            || LooksLikeOrganizationFooterLine(normalized, words)
            || LooksLikeShortTrailingPunctuationFragment(normalized, words)
            || LooksLikeShortListValueFragment(normalized, words)
            || LooksLikeShortCommaSeparatedLabelFragment(normalized, words)
            || LooksLikeShortStatusValueFragment(normalized, words)
            || LooksLikeMeasureDenseFragment(normalized, words))
        {
            return false;
        }

        var letterCount = normalized.Count(char.IsLetter);
        if (letterCount < 8)
            return false;

        var titleCaseWords = words.Count(static word =>
        {
            var firstLetter = word.FirstOrDefault(char.IsLetter);
            return firstLetter != default && char.IsUpper(firstLetter);
        });

        return titleCaseWords >= Math.Max(2, words.Count - 1);
    }

    private static bool HasNarrativeBodyAfterWeakTitle(IReadOnlyList<string> lines, int index)
    {
        var inspected = 0;
        for (var i = index + 1; i < lines.Count && inspected < 5; i++)
        {
            var line = NormalizeWhitespace(lines[i]);
            if (line.Length == 0)
                continue;

            inspected++;
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (LooksLikeFigureOrTableCaptionLine(line)
                || LooksLikeAddressLine(line, words)
                || LooksLikeOrganizationFooterLine(line, words)
                || LooksLikeNumberedTableRowFragment(line, words)
                || LooksLikeShortNumericOrRangeFragment(line, words)
                || LooksLikeMeasureDenseFragment(line, words))
            {
                continue;
            }

            if (TryClassifyHeading(line, out _))
                return false;

            if (LooksLikeNarrativeBodyLine(line, words))
                return true;
        }

        return false;
    }

    private static bool LooksLikeNarrativeBodyLine(string line, IReadOnlyList<string> words)
    {
        if (words.Count is < 5 or > 80)
            return false;

        var lowerWords = words.Count(static word =>
        {
            var firstLetter = word.FirstOrDefault(char.IsLetter);
            return firstLetter != default && char.IsLower(firstLetter);
        });
        if (lowerWords < Math.Max(3, words.Count / 2))
            return false;

        if (line.EndsWith(".", StringComparison.Ordinal)
            || line.EndsWith(";", StringComparison.Ordinal)
            || line.EndsWith(":", StringComparison.Ordinal))
        {
            return true;
        }

        return words.Any(static word =>
        {
            var token = TrimToken(word).ToLowerInvariant();
            return token is "is" or "are" or "was" or "were" or "be" or "being" or "been"
                or "shall" or "should" or "must" or "may";
        });
    }

    private static bool LooksLikeHeadingContinuation(string line)
    {
        var normalized = NormalizeWhitespace(line);
        if (normalized.Length is < 3 or > 90)
            return false;

        if (normalized.Any(char.IsDigit)
            || normalized.EndsWith(".", StringComparison.Ordinal)
            || normalized.EndsWith(",", StringComparison.Ordinal)
            || normalized.EndsWith(";", StringComparison.Ordinal))
        {
            return false;
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 8)
            return false;

        return IsContinuationConnector(words[0])
               && normalized.Count(char.IsLetter) >= 5
               && (LooksLikeMostlyUppercaseHeading(normalized) || LooksLikeDenseTitleCaseHeading(normalized, words));
    }

    private static bool LooksLikeInlineDenseHeadingNoise(
        string line,
        IReadOnlyList<string> lines,
        int index,
        IReadOnlyList<string> words)
    {
        if (words.Count != 1)
            return false;

        var token = TrimToken(words[0]);
        if (token.Length is < 2 or > 8)
            return false;

        if (!token.Any(char.IsLetter) || token.Any(char.IsLower))
            return false;

        var previous = index > 0 ? lines[index - 1] : string.Empty;
        var next = index + 1 < lines.Count ? lines[index + 1] : string.Empty;
        return LooksLikeDenseInlineContext(previous) && LooksLikeDenseInlineContext(next);
    }

    private static bool LooksLikeContextualRosterHeadingNoise(
        string line,
        IReadOnlyList<string> lines,
        int index,
        IReadOnlyList<string> words)
    {
        var normalized = NormalizeWhitespace(line).TrimEnd(':');
        var folded = FoldDiacritics(normalized).ToLowerInvariant();
        if (folded is "organization represented" or "organisation represented" or "name of representative"
            or "representative" or "representatives")
        {
            return true;
        }

        if (HasNarrativeBodyAfterWeakTitle(lines, index))
            return false;

        if (!LooksLikeRosterNameOrOrganizationFragment(normalized, words))
            return false;

        var contextLines = 0;
        var first = Math.Max(0, index - 4);
        var last = Math.Min(lines.Count - 1, index + 4);
        for (var i = first; i <= last; i++)
        {
            if (i == index)
                continue;
            if (LooksLikeRosterContextLine(lines[i]))
                contextLines++;
        }

        return contextLines >= 2;
    }

    private static bool LooksLikeRosterNameOrOrganizationFragment(string normalized, IReadOnlyList<string> words)
    {
        if (words.Count is < 2 or > 7)
            return false;
        if (normalized.Any(char.IsDigit) || normalized.Contains(':', StringComparison.Ordinal))
            return false;

        var folded = FoldDiacritics(normalized).ToLowerInvariant();
        if (folded.Contains("associate", StringComparison.Ordinal)
            || folded.Contains("consulting", StringComparison.Ordinal)
            || folded.Contains("corporation", StringComparison.Ordinal)
            || folded.Contains("company", StringComparison.Ordinal)
            || folded.Contains("institute", StringComparison.Ordinal)
            || folded.Contains("laborator", StringComparison.Ordinal)
            || folded.Contains("society", StringComparison.Ordinal)
            || folded.Contains("association", StringComparison.Ordinal)
            || normalized.Contains('&', StringComparison.Ordinal))
        {
            return true;
        }

        var nameLikeTokens = words
            .Select(TrimToken)
            .Where(static token => token.Length > 0)
            .Count(static token =>
            {
                var first = token.FirstOrDefault(char.IsLetter);
                return first != default && (char.IsUpper(first) || token.Length == 1);
            });

        return nameLikeTokens >= Math.Min(2, words.Count)
            && words.Any(static word => TrimToken(word).Length is >= 2 and <= 24);
    }

    private static bool LooksLikeRosterContextLine(string line)
    {
        var normalized = NormalizeWhitespace(line);
        if (normalized.Length == 0)
            return false;

        var folded = FoldDiacritics(normalized).ToLowerInvariant();
        return folded.Contains("(alt", StringComparison.Ordinal)
            || folded.Contains(" chairperson", StringComparison.Ordinal)
            || folded.Contains(" secretary", StringComparison.Ordinal)
            || folded.Contains(" representative", StringComparison.Ordinal)
            || folded.Contains(" association", StringComparison.Ordinal)
            || folded.Contains(" associates", StringComparison.Ordinal)
            || folded.Contains(" committee", StringComparison.Ordinal)
            || folded.Contains(" company", StringComparison.Ordinal)
            || folded.Contains(" corporation", StringComparison.Ordinal)
            || folded.Contains(" institute", StringComparison.Ordinal)
            || folded.Contains(" laboratory", StringComparison.Ordinal)
            || folded.Contains(" laboratories", StringComparison.Ordinal)
            || folded.Contains(" manufacturers", StringComparison.Ordinal)
            || folded.Contains(" society", StringComparison.Ordinal)
            || folded.Contains(" standards", StringComparison.Ordinal)
            || normalized.Contains('&', StringComparison.Ordinal);
    }

    private static bool LooksLikeDenseInlineContext(string line)
    {
        var normalized = NormalizeWhitespace(line);
        if (normalized.Length == 0)
            return false;

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return LooksLikeShortQuantityLeadFragment(normalized, words)
               || LooksLikeMeasureDenseFragment(normalized, words)
               || QuantityTokenRegex().IsMatch(normalized)
               || LooksLikeBodySentenceFragment(normalized, words);
    }

    private static bool LooksLikeShortNumericOrRangeFragment(string line, IReadOnlyList<string> words)
    {
        if (words.Count is < 1 or > 6)
            return false;
        if (line.Any(char.IsLetter))
            return false;

        return ShortNumericRangeFragmentRegex().IsMatch(NormalizeWhitespace(line));
    }

    private static bool LooksLikeShortQuantityLeadFragment(string line, IReadOnlyList<string> words)
    {
        if (words.Count is < 2 or > 8)
            return false;
        if (LooksLikeNumberedHeadingCandidate(line))
            return false;

        return ShortQuantityLeadFragmentRegex().IsMatch(NormalizeWhitespace(line));
    }

    private static bool LooksLikeShortMixedCaseOcrHeadingNoise(string line, IReadOnlyList<string> words)
    {
        if (words.Count != 2)
            return false;

        var first = TrimToken(words[0]);
        var second = TrimToken(words[1]);
        if (first.Length is < 5 or > 20 || second.Length != 1)
            return false;
        if (!first.Any(char.IsLetter) || first.Any(char.IsLower))
            return false;

        var secondLetter = second.FirstOrDefault(char.IsLetter);
        return secondLetter != default && char.IsLower(secondLetter);
    }

    private static bool LooksLikeNumberedHeadingCandidate(string line)
    {
        var match = NumberedHeadingRegex().Match(line);
        if (!match.Success)
            return false;

        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        var firstLetter = parts[1].FirstOrDefault(char.IsLetter);
        return firstLetter != default && char.IsUpper(firstLetter);
    }

    private static bool LooksLikeShortLegendOrAbbreviationFragment(string line, IReadOnlyList<string> words)
    {
        if (words.Count is < 3 or > 18)
            return false;

        var normalized = NormalizeWhitespace(line);
        var abbreviationDefinitions = AbbreviationDefinitionRegex().Matches(normalized).Count;
        if (abbreviationDefinitions >= 2)
            return true;

        return abbreviationDefinitions >= 1
            && (normalized.Contains('|', StringComparison.Ordinal)
                || normalized.Contains(';', StringComparison.Ordinal));
    }

    private static bool LooksLikeShortTrailingPunctuationFragment(string line, IReadOnlyList<string> words)
    {
        if (words.Count is < 1 or > 8)
            return false;

        var normalized = NormalizeWhitespace(line);
        return normalized.EndsWith(",", StringComparison.Ordinal)
            || normalized.EndsWith(";", StringComparison.Ordinal)
            || normalized.EndsWith("|", StringComparison.Ordinal);
    }

    private static bool LooksLikeHeaderFooterLine(string line)
    {
        var normalized = NormalizeWhitespace(line);
        if (normalized.Length == 0)
            return false;

        return HeaderFooterPageMarkerRegex().IsMatch(normalized)
            || HeaderFooterDateTimeRegex().IsMatch(normalized)
            || HeaderFooterWebOrIsbnRegex().IsMatch(normalized);
    }

    private static bool LooksLikeSpacedLetterNoise(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 4)
            return false;

        var singleLetterWords = words.Count(static word => word.Length == 1 && word.Any(char.IsLetter));
        return singleLetterWords >= 4 && singleLetterWords >= Math.Ceiling(words.Length * 0.6d);
    }

    private static bool LooksLikeMeasureDenseFragment(string line, IReadOnlyList<string> words)
    {
        var quantityCount = QuantityTokenRegex().Matches(line).Count;
        if (quantityCount >= 2)
            return true;

        var numericWords = words.Count(static word => word.Any(char.IsDigit));
        return numericWords >= 3 && words.Count <= 12;
    }

    private static bool LooksLikeShortMetadataValueFragment(string line, IReadOnlyList<string> words)
    {
        var normalized = NormalizeWhitespace(line).TrimEnd('.');
        if (words.Count is < 2 or > 8)
            return false;

        if (!normalized.Contains(':', StringComparison.Ordinal))
            return false;

        return QuantityTokenRegex().IsMatch(normalized)
            || MetadataDurationValueRegex().IsMatch(normalized);
    }

    private static bool LooksLikeShortAcronymLeadFragment(string line, IReadOnlyList<string> words)
    {
        if (words.Count is < 2 or > 4)
            return false;

        var lead = TrimToken(words[0]);
        if (lead.Length is < 1 or > 3 || !lead.Any(char.IsLetter) || !lead.All(char.IsUpper))
            return false;

        if (lead.Length == 1)
        {
            var rawLead = words[0].Trim();
            if (!rawLead.EndsWith(".", StringComparison.Ordinal) && !rawLead.EndsWith(")", StringComparison.Ordinal))
                return true;
        }

        return words
            .Skip(1)
            .Select(static word => word.FirstOrDefault(char.IsLetter))
            .Any(static ch => ch != default && char.IsLower(ch));
    }

    private static bool LooksLikeShortHyphenatedBodyFragment(string line, IReadOnlyList<string> words)
    {
        if (words.Count is < 3 or > 6)
            return false;

        if (!HyphenatedBodyLeadRegex().IsMatch(line))
            return false;

        return words
            .Skip(1)
            .Any(static word =>
            {
                var token = TrimToken(word);
                return token.Length is >= 1 and <= 4 && token.Any(char.IsLower);
            });
    }

    private static bool LooksLikeRomanHeadingRemainder(string heading)
    {
        var firstLetter = heading.FirstOrDefault(char.IsLetter);
        return firstLetter != default
            && (char.IsUpper(firstLetter) || !char.IsLower(firstLetter));
    }

    private static string TrimToken(string value)
        => value.Trim(' ', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '\u2018', '\u2019', '\u201c', '\u201d');

    private static bool IsContinuationConnector(string value)
    {
        var normalized = FoldDiacritics(TrimToken(value)).ToUpperInvariant();
        return normalized is "A" or "AL" or "ALLA" or "AND" or "AU" or "AUX" or "CON" or "DE" or "DEL" or "DELLA" or "DES" or "DU" or "ET" or "MIT" or "OF" or "THE" or "UND" or "WITH";
    }

    private static bool LooksLikeShortListValueFragment(string line, IReadOnlyList<string> words)
    {
        var normalized = NormalizeWhitespace(line);
        if (words.Count is < 2 or > 8)
            return false;

        if (LeadingNumberedLowercaseCommaFragmentRegex().IsMatch(normalized))
            return true;

        if (DanglingClosingParenthesisListFragmentRegex().IsMatch(normalized))
            return true;

        return normalized.EndsWith(",", StringComparison.Ordinal)
            && words.Count <= 6
            && words.Any(static word => word.Length >= 2 && char.IsLower(word[0]));
    }

    private static bool LooksLikeShortCommaSeparatedLabelFragment(string line, IReadOnlyList<string> words)
    {
        var normalized = NormalizeWhitespace(line);
        if (words.Count is < 2 or > 6)
            return false;
        if (!normalized.Contains(',', StringComparison.Ordinal))
            return false;
        if (normalized.EndsWith(".", StringComparison.Ordinal)
            || normalized.EndsWith(";", StringComparison.Ordinal)
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.Contains('|', StringComparison.Ordinal))
        {
            return false;
        }

        var folded = FoldDiacritics(normalized).ToLowerInvariant();
        if (folded.Contains(" and ", StringComparison.Ordinal)
            || folded.Contains(" et ", StringComparison.Ordinal)
            || folded.Contains(" und ", StringComparison.Ordinal)
            || folded.Contains(" of ", StringComparison.Ordinal)
            || folded.Contains(" de ", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(static part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3);
    }

    private static bool LooksLikeShortStatusValueFragment(string line, IReadOnlyList<string> words)
    {
        var normalized = NormalizeWhitespace(line);
        if (words.Count is < 2 or > 6)
            return false;
        if (normalized.EndsWith(".", StringComparison.Ordinal)
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.Contains('|', StringComparison.Ordinal))
        {
            return false;
        }

        var last = FoldDiacritics(TrimToken(words[^1])).ToLowerInvariant();
        if (last is not ("active" or "approved" or "archived" or "current" or "draft" or "inactive"
            or "obsolete" or "preliminary" or "released" or "valid" or "withdrawn"))
        {
            return false;
        }

        return words.Take(words.Count - 1).Any(static word =>
        {
            var firstLetter = word.FirstOrDefault(char.IsLetter);
            return firstLetter != default && char.IsLower(firstLetter);
        });
    }

    private static bool LooksLikeFigureOrTableCaptionLine(string line)
    {
        var normalized = NormalizeWhitespace(line);
        return FigureOrTableCaptionRegex().IsMatch(normalized);
    }

    private static bool LooksLikeAddressLine(string line, IReadOnlyList<string> words)
    {
        if (words.Count is < 3 or > 10)
            return false;

        return AddressLineRegex().IsMatch(NormalizeWhitespace(line));
    }

    private static bool LooksLikeOrganizationFooterLine(string line, IReadOnlyList<string> words)
    {
        if (words.Count is < 3 or > 10)
            return false;

        return OrganizationFooterRegex().IsMatch(NormalizeWhitespace(line));
    }

    private static bool LooksLikeNumberedTableRowFragment(string line, IReadOnlyList<string> words)
    {
        var normalized = NormalizeWhitespace(line);
        if (words.Count is < 4 or > 18)
            return false;

        var match = NumberedHeadingRegex().Match(normalized);
        if (!match.Success)
            return false;

        var prefix = normalized.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (!prefix.Contains('.', StringComparison.Ordinal) && !prefix.Contains(':', StringComparison.Ordinal))
            return false;

        return normalized.Contains('|', StringComparison.Ordinal)
            || NumberedBooleanMetadataRowRegex().IsMatch(normalized)
            || NumberedTrailingObligationRowRegex().IsMatch(normalized);
    }

    private static bool LooksLikeBodySentenceFragment(string line, IReadOnlyList<string> words)
    {
        if (words.Count < 4)
            return false;

        var firstLetter = line.FirstOrDefault(char.IsLetter);
        if (firstLetter != default && char.IsLower(firstLetter))
            return true;

        var lowerWords = words.Count(static word => word.Length >= 3 && char.IsLower(word[0]));
        return lowerWords >= Math.Max(5, words.Count - 2)
            && BodyFragmentVerbRegex().IsMatch(line);
    }

    private static bool LooksLikeNavigationHeadingTitle(string line)
    {
        var normalized = FoldDiacritics(NormalizeWhitespace(line)).ToLowerInvariant();
        return normalized.Contains("table des matieres", StringComparison.Ordinal)
            || normalized.Contains("table of contents", StringComparison.Ordinal)
            || normalized.Contains("inhaltsverzeichnis", StringComparison.Ordinal)
            || normalized.Contains("indice general", StringComparison.Ordinal)
            || normalized.Contains("indice de contenido", StringComparison.Ordinal)
            || normalized.Contains("indice de contenidos", StringComparison.Ordinal)
            || normalized.Contains("indice de materias", StringComparison.Ordinal)
            || normalized.Contains("indice analitico", StringComparison.Ordinal)
            || normalized is "sommaire" or "contents" or "sommario" or "sumario" or "indice" or "index" or "toc";
    }

    private static bool LooksLikeNavigationListLine(string line, IReadOnlyList<string> lines, int index)
    {
        if (!LooksLikePageReferenceLine(line))
            return false;

        var similarNeighbors = 0;
        var first = Math.Max(0, index - 3);
        var last = Math.Min(lines.Count - 1, index + 3);
        for (var i = first; i <= last; i++)
        {
            if (i == index)
                continue;
            if (LooksLikePageReferenceLine(lines[i]))
                similarNeighbors++;
        }

        return similarNeighbors >= 2
            || (DotLeaderPageReferenceRegex().IsMatch(line) && similarNeighbors >= 1);
    }

    private static bool LooksLikePageReferenceLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 160)
            return false;

        if (DotLeaderPageReferenceRegex().IsMatch(line))
            return true;

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 2 and <= 14
            && TrailingPageReferenceRegex().IsMatch(line)
            && !NumberedHeadingRegex().IsMatch(line)
            && !line.Contains(':', StringComparison.Ordinal);
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

    [GeneratedRegex(@"^\d+(?:\.\d+){0,5}\s+\S+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedHeadingRegex();

    [GeneratedRegex(@"^\d{1,4}\s+\S+", RegexOptions.CultureInvariant)]
    private static partial Regex SingleNumberHeadingLeadRegex();

    [GeneratedRegex(@"^\d{1,4}(?:\s*(?:[-\u2013\u2014\u2022\u00b7/]|to|a|and|et|und)\s*\d{1,4}|\s+\d{1,4})+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ShortNumericRangeFragmentRegex();

    [GeneratedRegex(@"^\d{1,4}(?:[,.]\d+)?\s+(?:\p{Ll}|g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|km|v|a|w|hz|rpm|s|sec|secs|seconds?|secondes?|min|mins?|minutes?|h|hr|hrs?|hours?|heures?|units?|unites?|items?|elements?|entries?|parts?|pieces?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ShortQuantityLeadFragmentRegex();

    [GeneratedRegex(@"(?:^|\s|[|;])[\p{Lu}\p{N}]{1,5}\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex AbbreviationDefinitionRegex();

    [GeneratedRegex(@"^(?:[IVXLCM]+)[\.\)]?\s+(?<heading>\S.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RomanHeadingRegex();

    [GeneratedRegex(@"\.{2,}\s*\d{1,5}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DotLeaderPageReferenceRegex();

    [GeneratedRegex(@"\s\d{1,5}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingPageReferenceRegex();

    [GeneratedRegex(@"\b(?:page|p\.?)\s*\d{1,5}\b|\b\d{1,5}\s*/\s*\d{1,5}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HeaderFooterPageMarkerRegex();

    [GeneratedRegex(@"\b\d{1,2}[:/]\d{1,2}(?::\d{2})?(?:[/:\-]\d{2,4})?\b|\b\d{1,2}/\d{1,2}/\d{2,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderFooterDateTimeRegex();

    [GeneratedRegex(@"\b(?:www\.|https?://|isbn|copyright)\b|©", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HeaderFooterWebOrIsbnRegex();

    [GeneratedRegex(@"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|km|nm|bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|%|pct|percent|pourcent|deg|degrees?|degres?|°|s|sec|secs|secondes?|seconds?|min|mins?|minutes?|h|hr|hrs?|hours?|kcal)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QuantityTokenRegex();

    [GeneratedRegex(@"\b(?:duration|dur[ée]e|dauer|durata|duraci[oó]n|tempo|time)\s*:\s*\d", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MetadataDurationValueRegex();

    [GeneratedRegex(@"^(?:(?:preparation|pr[e\u00e9]paration|prep(?:aration)?|duration|dur[e\u00e9]e|time|temps|difficulty|difficult[e\u00e9]|level|niveau)\s*:|\s*(?:pour|for|para|per)\s+\d{1,3}\b|\s*\d{1,3}\s*(?:min|minutes?|h|hr|hrs?|hours?|heures?)\b|(?:facile|easy|interm[e\u00e9]diaire|medium|difficile|hard|assez\s+cher|pas\s+cher|bon\s+march[e\u00e9]|cheap|expensive)\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ContextualHeadingMetadataRegex();

    [GeneratedRegex(@"^\p{Lu}[\p{Ll}\p{M}]{2,30}-(?:y|en|le|la|les|lui|leur|vous|nous|moi|toi|se|s)\b", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenatedBodyLeadRegex();

    [GeneratedRegex(@"^(?:figure|fig\.?|table|tableau|tabelle|abb\.?|abbildung|figura)\s+\d{1,4}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FigureOrTableCaptionRegex();

    [GeneratedRegex(@"^\d{1,6}\s+[\p{L}\p{M}'\u2019\.-]+(?:\s+[\p{L}\p{M}'\u2019\.-]+){0,6}\s+(?:road|rd\.?|street|st\.?|avenue|ave\.?|drive|dr\.?|lane|ln\.?|way|boulevard|blvd\.?|square|place|rue|route|strasse|stra\u00dfe|platz)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AddressLineRegex();

    [GeneratedRegex(@"^[\p{Lu}\p{N}]{2,8}\s*(?:[-\u2010-\u2015]|\u2014|\u2013)\s*[\p{Lu}][\p{L}\p{M}'\u2019-]+(?:\s+[\p{Lu}][\p{L}\p{M}'\u2019-]+){1,6}\s+(?:association|associates?|authority|commission|committee|company|corporation|council|federation|foundation|group|institute|institution|laborator(?:y|ies)|organization|organisation|society|standards?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OrganizationFooterRegex();

    [GeneratedRegex(@"^\d{1,2}(?:[\.:]\d{1,3}){1,5}\s+\S.{2,100}\b(?:no|yes|oui|non|ja|nein|si|s\u00ed)(?:\s*/\s*(?:no|yes|oui|non|ja|nein|si|s\u00ed))?[\p{L}\?]*\s+\d{1,4}\b.*(?:\b[mo0o]{1,2}\b|\([mo0o]{1,2}\))\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NumberedBooleanMetadataRowRegex();

    [GeneratedRegex(@"^\d{1,2}(?:[\.:]\d{1,3}){1,5}\s+\S.{2,100}\b(?:unspecified|characters?|language|langue|sprache|\d{1,4})\b.*(?:\b[mo0o]{1,2}\b|\([mo0o]{1,2}\))\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NumberedTrailingObligationRowRegex();

    [GeneratedRegex(@"^\d{1,4}\s+\p{Ll}[\p{L}'\u2019\-]{1,30}(?:\s+\p{L}[\p{L}'\u2019\-]{1,30}){0,5}\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumberedLowercaseCommaFragmentRegex();

    [GeneratedRegex(@"^[\p{Lu}][\p{L}'\u2019\-]{2,30}\s*,\s*[\p{Lu}][\p{L}'\u2019\-]{2,30}\)$", RegexOptions.CultureInvariant)]
    private static partial Regex DanglingClosingParenthesisListFragmentRegex();

    [GeneratedRegex(@"\b(?:add|adds|ajoute|ajoutez|combine|coupez|cut|heat|insert|lancez|mix|m[e\u00e9]langez|place|placez|pour|press|remove|set|stir|versez)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BodyFragmentVerbRegex();

    private sealed record PageLines(int PageNumber, List<string> Lines);
    private sealed record SectionCandidate(int PageNumber, int LineNumber, string Title, int Level);
}

internal sealed record ExtractedDocumentSection(
    int Ordinal,
    string Title,
    int Level,
    int PageStart,
    int PageEnd,
    int? StartLine,
    int? EndLine);
