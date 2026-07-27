using System.Text.RegularExpressions;

internal static partial class OcrNoiseFilter
{
    internal static string RemoveSpacedLetterRunNoise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var cleanedLines = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var cleaned = RemoveSpacedLetterRunNoiseFromLine(line);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                if (string.IsNullOrWhiteSpace(line))
                    cleanedLines.Add(string.Empty);
                continue;
            }

            cleanedLines.Add(cleaned);
        }

        return string.Join('\n', cleanedLines).Trim();
    }

    internal static bool LooksLikeProbableNoiseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length >= 180 && LooksLikePreservableEntityRosterOrContactListText(text))
            return false;

        if (LooksLikeSpacedLetterRunNoise(text))
            return true;

        if (LooksLikeShortGluedOcrNoiseText(text))
            return true;

        if (LooksLikeDamagedMetadataPrefix(text))
            return true;

        if (LooksLikeShortMetadataResidue(text))
            return true;

        if (LooksLikeNoisyMetadataOnlyLine(text))
            return true;

        if (LooksLikeShortMetadataScheduleOnlyLine(text))
            return true;

        if (LooksLikeShortStandaloneLayoutMetadataOnlyLine(text))
            return true;

        if (LooksLikeShortBrokenProseFragment(text))
            return true;

        if (LooksLikeShortBrokenHeaderFooterFragment(text))
            return true;

        if (LooksLikeShortPageHeaderFooterFragment(text))
            return true;

        if (text.Length < 32)
            return false;

        if (text.Length > 180)
            return LooksLikeProbableLongNoiseText(text);

        return LooksLikeProbableNoiseSegment(text);
    }

    internal static bool LooksLikeProbableNoisePublishedUnitText(string? text)
    {
        if (!LooksLikeProbableNoiseText(text))
            return false;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);
        return RetrievalContentClassifier.IsPredominantlyNavigationContent(
                signal.ContentRole,
                chunkType: null,
                signal.NavigationScore,
                signal.ContentDensityScore)
            || signal.ContentDensityScore < 0.35;
    }

    internal static string RemoveTrailingNoisySupplement(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        normalized = RemoveLeadingNoisyMetadataSupplement(normalized);
        normalized = RemoveLeadingShortLayoutPrefixBeforeStructuredLabels(normalized);
        normalized = RemoveInlineIndexReferenceTail(normalized);
        normalized = RemoveTrailingDecorativeMetadataScheduleSupplement(normalized);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        if (normalized.Length < 120)
            return RemoveTrailingDamagedFragmentAfterCompleteContent(normalized);

        foreach (Match match in TrailingSupplementStartRegex().Matches(normalized))
        {
            if (match.Index < 50)
                continue;

            var prefix = normalized[..match.Index].Trim();
            var suffix = normalized[match.Index..].Trim();
            var suffixStartsWithNoisyMetadata = NoisyMetadataLeadMarkerRegex().IsMatch(suffix);
            if (match.Index >= normalized.Length - 32 && !suffixStartsWithNoisyMetadata)
                continue;

            if (prefix.Length < 40 || suffix.Length < 32 && !suffixStartsWithNoisyMetadata)
                continue;

            if (!EndsLikeCompleteContentBeforeSupplement(prefix))
            {
                if (suffixStartsWithNoisyMetadata
                    && (LooksLikePreservableTechnicalFigureOrChartText(prefix)
                        || LooksLikePreservableTechnicalReferenceText(prefix)
                        || LooksLikePreservableStructuredContentText(prefix)))
                {
                    return prefix;
                }

                if (suffixStartsWithNoisyMetadata
                    || LooksLikeProbableNoiseText(suffix) && LooksLikeFragmentBeforeNoisyMetadata(prefix))
                {
                    return string.Empty;
                }

                continue;
            }

            if (suffixStartsWithNoisyMetadata
                || LooksLikeProbableNoiseText(suffix)
                || LooksLikeDamagedLayoutSupplement(suffix)
                || LooksLikeMetadataLayoutDamagedSupplement(suffix))
                return prefix;
        }

        return RemoveTrailingDamagedFragmentAfterCompleteContent(normalized);
    }

    private static string RemoveTrailingDecorativeMetadataScheduleSupplement(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 32)
            return text;

        for (var index = text.Length - 2; index >= 16; index--)
        {
            var ch = text[index];
            if (ch is not ('.' or '!' or '?' or '\u2026'))
                continue;
            if (ch == '.' && LooksLikeInlineSingleLetterAbbreviationPeriod(text, index))
                continue;

            var prefix = text[..(index + 1)].Trim();
            var suffix = text[(index + 1)..].Trim();
            if (prefix.Length < 16 || suffix.Length is < 12 or > 180)
                continue;
            if (LooksLikeShortStandaloneLayoutMetadataOnlyLine(suffix))
                return prefix;
        }

        return text;
    }

    internal static string RemoveInlineIndexReferenceTail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        var match = InlineIndexReferenceTailRegex().Match(normalized);
        if (!match.Success)
            return normalized;

        var lead = match.Groups["lead"].Value.Trim(' ', '-', ':', ';', ',', '.', '|', '/', '\\');
        var tail = match.Groups["tail"].Value.Trim();
        if (lead.Length < 4 || !lead.Any(char.IsLetter))
            return normalized;
        if (!LooksLikeDenseInternalReferenceTail(tail))
            return normalized;

        return lead;
    }

    private static bool LooksLikeDenseInternalReferenceTail(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 16)
            return false;

        var compact = Regex.Replace(text, @"\s+", string.Empty);
        var codeMatches = InternalReferenceCodeRegex().Matches(compact);
        if (codeMatches.Count >= 2)
            return true;
        if (codeMatches.Count == 1 && compact.Length >= 32)
            return true;

        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 8)
            .ToArray();

        var mixedCodeTokens = tokens.Count(static token =>
            token.Any(char.IsDigit)
            && token.Any(IsLatinLetter)
            && token.Count(ch => ch is '_' or '-') >= 1);

        return mixedCodeTokens >= 2;
    }

    private static bool LooksLikeProbableLongNoiseText(string text)
    {
        const int windowLength = 180;
        const int stepLength = 120;

        if (LooksLikePreservableLongProseText(text))
            return false;

        if (LooksLikePreservableStandardsGovernanceText(text))
            return false;

        if (LooksLikePreservableTechnicalReferenceText(text))
            return false;

        if (LooksLikePreservableTechnicalTopicListText(text))
            return false;

        if (LooksLikePreservableTechnicalDecisionMatrixText(text))
            return false;

        if (LooksLikePreservableEntityRosterOrContactListText(text))
            return false;

        if (LooksLikePreservableTechnicalFigureOrChartText(text))
            return false;

        if (LooksLikeMetadataLayoutDamagedSupplement(text)
            || LooksLikeDamagedMetadataPrefix(text)
            || LooksLikeMetadataLayoutDamagedSupplement(text[..Math.Min(windowLength, text.Length)]))
        {
            return true;
        }

        if (LooksLikePreservableStructuredContentText(text))
            return false;

        var checkedWindows = 0;
        var noisyWindows = 0;
        for (var start = 0; start < text.Length; start += stepLength)
        {
            var length = Math.Min(windowLength, text.Length - start);
            if (length < 32)
                break;

            checkedWindows++;
            if (LooksLikeProbableNoiseSegment(text.Substring(start, length)))
            {
                noisyWindows++;
                if (noisyWindows >= 2)
                    return true;
            }
        }

        return checkedWindows <= 1 && noisyWindows == 1;
    }

    private static bool LooksLikePreservableLongProseText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 240)
            return false;
        if (NoisyMetadataLeadMarkerRegex().IsMatch(text))
            return false;

        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 45)
            return false;

        var normalizedTokens = tokens.Select(static token => token.ToLowerInvariant()).ToArray();
        var commonWordCount = normalizedTokens.Count(CommonWords.Contains);
        var longWordCount = normalizedTokens.Count(static token =>
            token.Count(IsLatinLetter) >= 7
            && LatinLetterVowelRatio(token) >= 0.18);
        var singleLetterCount = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Count(IsSingleLetterToken);
        var suspiciousInternalCaseTokens = tokens.Count(static token =>
            ContainsLatinLetter(token) && HasSuspiciousInternalCaseSwitch(token));
        var mixedLetterDigitTokens = normalizedTokens.Count(ContainsLatinLettersAndDigits);
        var quoteSlashPipeCount = text.Count(static ch =>
            ch is '\'' or '\u2019' or '\u2018' or '"' or '/' or '\\' or '|');
        var symbolCount = text.Count(static ch =>
            !char.IsLetterOrDigit(ch)
            && !char.IsWhiteSpace(ch)
            && ch is not '.' and not ',' and not ';' and not ':' and not '(' and not ')' and not '-' and not '\u2013' and not '\u2014' and not '+');
        var symbolRatio = (double)symbolCount / Math.Max(1, text.Length);
        var sentencePunctuationCount = text.Count(static ch => ch is '.' or '!' or '?' or '\u2026');

        if (symbolRatio >= 0.035 || quoteSlashPipeCount >= 8)
            return false;
        if (singleLetterCount >= Math.Max(5, tokens.Length / 12))
            return false;
        if (suspiciousInternalCaseTokens >= Math.Max(8, tokens.Length / 8))
            return false;
        if (mixedLetterDigitTokens >= Math.Max(10, tokens.Length / 6))
            return false;

        return sentencePunctuationCount >= 4
            && commonWordCount >= 4
            && longWordCount >= Math.Max(12, tokens.Length / 5);
    }

    private static bool LooksLikePreservableTechnicalReferenceText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 160)
            return false;
        if (LooksLikeSpacedLetterRunNoise(text) || NoisyMetadataLeadMarkerRegex().IsMatch(text))
            return false;

        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 28)
            return false;

        var substantiveWords = tokens.Count(static token =>
            token.Count(IsLatinLetter) >= 4
            && LatinLetterVowelRatio(token) >= 0.16);
        var singleLetterCount = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Count(IsSingleLetterToken);
        var quoteSlashPipeCount = text.Count(static ch =>
            ch is '\'' or '\u2019' or '\u2018' or '"' or '/' or '\\' or '|');
        var symbolCount = text.Count(static ch =>
            !char.IsLetterOrDigit(ch)
            && !char.IsWhiteSpace(ch)
            && ch is not '.' and not ',' and not ';' and not ':' and not '(' and not ')' and not '[' and not ']'
            && ch is not '-' and not '\u2013' and not '\u2014' and not '+' and not '*' and not '\u00b0');
        var symbolRatio = (double)symbolCount / Math.Max(1, text.Length);
        if (symbolRatio >= 0.075 || quoteSlashPipeCount >= Math.Max(10, tokens.Length / 4))
            return false;
        if (singleLetterCount >= Math.Max(6, tokens.Length / 10))
            return false;

        var technicalIdentifierCount = TechnicalIdentifierRegex().Matches(text).Count;
        var sectionReferenceCount = TechnicalSectionReferenceRegex().Matches(text).Count;
        var technicalCueCount = TechnicalContentCueRegex().Matches(text).Count;
        return substantiveWords >= Math.Max(14, tokens.Length / 4)
            && (technicalIdentifierCount + sectionReferenceCount >= 3
                || technicalCueCount >= 4 && sectionReferenceCount >= 1);
    }

    private static bool LooksLikePreservableTechnicalTopicListText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 120)
            return false;
        if (LooksLikeSpacedLetterRunNoise(text) || NoisyMetadataLeadMarkerRegex().IsMatch(text))
            return false;

        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 24)
            return false;

        var substantiveWords = tokens.Count(static token =>
            token.Count(IsLatinLetter) >= 4
            && LatinLetterVowelRatio(token) >= 0.16);
        var singleLetterCount = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Count(IsSingleLetterToken);
        var symbolCount = text.Count(static ch =>
            !char.IsLetterOrDigit(ch)
            && !char.IsWhiteSpace(ch)
            && ch is not '.' and not ',' and not ';' and not ':' and not '(' and not ')'
            && ch is not '[' and not ']' and not '-' and not '\u2013' and not '\u2014' and not '+');
        var symbolRatio = (double)symbolCount / Math.Max(1, text.Length);
        if (symbolRatio >= 0.075 || singleLetterCount >= Math.Max(5, tokens.Length / 8))
            return false;

        var technicalCueCount = TechnicalContentCueRegex().Matches(text).Count;
        var topicCueCount = TechnicalTopicListCueRegex().Matches(text).Count;
        return substantiveWords >= Math.Max(12, tokens.Length / 4)
            && technicalCueCount >= 4
            && topicCueCount >= 4;
    }

    private static bool LooksLikePreservableEntityRosterOrContactListText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 180)
            return false;
        if (NoisyMetadataLeadMarkerRegex().IsMatch(text))
            return false;

        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 30)
            return false;

        var substantiveWords = tokens.Count(static token =>
            token.Count(IsLatinLetter) >= 4
            && LatinLetterVowelRatio(token) >= 0.16);
        var singleLetterCount = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Count(IsSingleLetterToken);
        var suspiciousInternalCaseTokens = tokens.Count(static token =>
            ContainsLatinLetter(token) && HasSuspiciousInternalCaseSwitch(token));
        var quoteSlashPipeCount = text.Count(static ch =>
            ch is '\'' or '\u2019' or '\u2018' or '"' or '/' or '\\' or '|');
        var symbolCount = text.Count(static ch =>
            !char.IsLetterOrDigit(ch)
            && !char.IsWhiteSpace(ch)
            && ch is not '.' and not ',' and not ';' and not ':' and not '(' and not ')'
            && ch is not '[' and not ']' and not '-' and not '\u2013' and not '\u2014'
            && ch is not '+' and not '&' and not '@');
        var symbolRatio = (double)symbolCount / Math.Max(1, text.Length);
        var entityCueCount = EntityRosterCueRegex().Matches(text).Count;
        var entityAbbreviationCount = EntityAbbreviationRegex().Matches(text).Count;
        var personNamePairCount = PersonNamePairRegex().Matches(text).Count;
        var initialPersonNameCount = InitialPersonNameRegex().Matches(text).Count;
        var contactSignalCount = ContactSignalRegex().Matches(text).Count
            + PostalAddressSignalRegex().Matches(text).Count;
        var personLikeNameCount = personNamePairCount + initialPersonNameCount;
        var strongRosterEvidence = personLikeNameCount >= 6
            && entityCueCount + entityAbbreviationCount + contactSignalCount >= 4;

        if (symbolRatio >= 0.09
            || quoteSlashPipeCount >= Math.Max(12, tokens.Length / 4)
            || !strongRosterEvidence && singleLetterCount >= Math.Max(10, tokens.Length / 6)
            || suspiciousInternalCaseTokens >= Math.Max(12, tokens.Length / 5))
        {
            return false;
        }

        var rosterShape = personLikeNameCount >= 4
            && (entityCueCount >= 3
                || entityAbbreviationCount >= 3
                || contactSignalCount >= 2);
        var contactListShape = contactSignalCount >= 3
            && (entityCueCount >= 1 || personLikeNameCount >= 2)
            && substantiveWords >= Math.Max(10, tokens.Length / 4);

        return substantiveWords >= Math.Max(12, tokens.Length / 5)
            && (rosterShape || contactListShape);
    }

    private static bool LooksLikePreservableTechnicalDecisionMatrixText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 180)
            return false;
        if (LooksLikeSpacedLetterRunNoise(text) || NoisyMetadataLeadMarkerRegex().IsMatch(text))
            return false;

        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 30)
            return false;

        var normalizedTokens = tokens.Select(static token => token.ToLowerInvariant()).ToArray();
        var commonWordCount = normalizedTokens.Count(CommonWords.Contains);
        var substantiveWords = tokens.Count(static token =>
            token.Count(IsLatinLetter) >= 4
            && LatinLetterVowelRatio(token) >= 0.16);
        var singleLetterCount = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Count(IsSingleLetterToken);
        var suspiciousInternalCaseTokens = tokens.Count(static token =>
            ContainsLatinLetter(token) && HasSuspiciousInternalCaseSwitch(token));
        var quoteSlashPipeCount = text.Count(static ch =>
            ch is '\'' or '\u2019' or '\u2018' or '"' or '/' or '\\' or '|');
        var symbolCount = text.Count(static ch =>
            !char.IsLetterOrDigit(ch)
            && !char.IsWhiteSpace(ch)
            && ch is not '.' and not ',' and not ';' and not ':' and not '(' and not ')'
            && ch is not '[' and not ']' and not '-' and not '\u2013' and not '\u2014'
            && ch is not '+' and not '&');
        var symbolRatio = (double)symbolCount / Math.Max(1, text.Length);
        if (symbolRatio >= 0.11
            || quoteSlashPipeCount >= Math.Max(16, tokens.Length / 3)
            || singleLetterCount >= Math.Max(10, tokens.Length / 5)
            || suspiciousInternalCaseTokens >= Math.Max(14, tokens.Length / 4))
        {
            return false;
        }

        var technicalIdentifierCount = TechnicalIdentifierRegex().Matches(text).Count;
        var sectionReferenceCount = TechnicalSectionReferenceRegex().Matches(text).Count;
        var technicalCueCount = TechnicalContentCueRegex().Matches(text).Count;
        var matrixCueCount = TechnicalDecisionMatrixCueRegex().Matches(text).Count;
        return substantiveWords >= Math.Max(14, tokens.Length / 5)
            && commonWordCount >= 5
            && matrixCueCount >= 5
            && (technicalIdentifierCount + sectionReferenceCount >= 1
                || technicalCueCount >= 3);
    }

    private static bool LooksLikePreservableTechnicalFigureOrChartText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 140)
            return false;
        if (LooksLikeSpacedLetterRunNoise(text) || NoisyMetadataLeadMarkerRegex().IsMatch(text))
            return false;

        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 20)
            return false;

        var normalizedTokens = tokens.Select(static token => token.ToLowerInvariant()).ToArray();
        var commonWordCount = normalizedTokens.Count(CommonWords.Contains);
        var substantiveWords = tokens.Count(static token =>
            token.Count(IsLatinLetter) >= 4
            && LatinLetterVowelRatio(token) >= 0.16);
        var singleLetterCount = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Count(IsSingleLetterToken);
        var suspiciousInternalCaseTokens = tokens.Count(static token =>
            ContainsLatinLetter(token) && HasSuspiciousInternalCaseSwitch(token));
        var quoteSlashPipeCount = text.Count(static ch =>
            ch is '\'' or '\u2019' or '\u2018' or '"' or '/' or '\\' or '|');
        var symbolCount = text.Count(static ch =>
            !char.IsLetterOrDigit(ch)
            && !char.IsWhiteSpace(ch)
            && ch is not '.' and not ',' and not ';' and not ':' and not '(' and not ')'
            && ch is not '[' and not ']' and not '-' and not '\u2013' and not '\u2014'
            && ch is not '+' and not '&' and not '@' and not '=' and not '<' and not '>');
        var symbolRatio = (double)symbolCount / Math.Max(1, text.Length);
        if (symbolRatio >= 0.18
            || quoteSlashPipeCount >= Math.Max(20, tokens.Length / 2)
            || singleLetterCount >= Math.Max(18, tokens.Length / 4)
            || suspiciousInternalCaseTokens >= Math.Max(18, tokens.Length / 3))
        {
            return false;
        }

        var technicalIdentifierCount = TechnicalIdentifierRegex().Matches(text).Count;
        var technicalCueCount = TechnicalContentCueRegex().Matches(text).Count;
        var figureCueCount = TechnicalFigureOrChartCueRegex().Matches(text).Count;
        var instructionCueCount = EnumeratedInstructionCueRegex().Matches(text).Count;
        var compactAnnotatedFigure = figureCueCount >= 6
            && technicalCueCount >= 3
            && substantiveWords >= Math.Max(10, tokens.Length / 7);
        if (tokens.Length < 28 && !compactAnnotatedFigure)
            return false;

        return substantiveWords >= Math.Max(12, tokens.Length / 6)
            && (commonWordCount >= 3 || compactAnnotatedFigure)
            && figureCueCount >= 4
            && (technicalIdentifierCount >= 1
                || technicalCueCount >= 3
                || instructionCueCount >= 2);
    }

    private static bool LooksLikePreservableStandardsGovernanceText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 160)
            return false;
        if (LooksLikeSpacedLetterRunNoise(text) || NoisyMetadataLeadMarkerRegex().IsMatch(text))
            return false;

        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 28)
            return false;

        var normalizedTokens = tokens.Select(static token => token.ToLowerInvariant()).ToArray();
        var normalizedText = text.ToLowerInvariant();
        var commonWordCount = normalizedTokens.Count(CommonWords.Contains);
        var substantiveWords = tokens.Count(static token =>
            token.Count(IsLatinLetter) >= 4
            && LatinLetterVowelRatio(token) >= 0.16);
        var sentencePunctuationCount = text.Count(static ch => ch is '.' or '!' or '?' or '\u2026');
        var quoteSlashPipeCount = text.Count(static ch =>
            ch is '\'' or '\u2019' or '\u2018' or '"' or '/' or '\\' or '|');
        var symbolCount = text.Count(static ch =>
            !char.IsLetterOrDigit(ch)
            && !char.IsWhiteSpace(ch)
            && ch is not '.' and not ',' and not ';' and not ':' and not '(' and not ')'
            && ch is not '-' and not '\u2013' and not '\u2014' and not '+' and not '*');
        var symbolRatio = (double)symbolCount / Math.Max(1, text.Length);
        if (symbolRatio >= 0.08 || quoteSlashPipeCount >= Math.Max(10, tokens.Length / 4))
            return false;

        var technicalIdentifierCount = TechnicalIdentifierRegex().Matches(text).Count;
        var governanceCueCount = StandardsGovernanceCueRegex().Matches(normalizedText).Count;
        var rosterCue = StandardsRosterCueRegex().IsMatch(normalizedText);
        return governanceCueCount >= 4
            && commonWordCount >= 6
            && substantiveWords >= Math.Max(12, tokens.Length / 5)
            && sentencePunctuationCount >= 2
            && (technicalIdentifierCount >= 1
                || normalizedText.Contains("standard", StringComparison.Ordinal)
                || normalizedText.Contains("standards", StringComparison.Ordinal))
            && (rosterCue || sentencePunctuationCount >= 3);
    }

    private static bool LooksLikePreservableStructuredContentText(string text)
    {
        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 12)
            return false;

        var normalizedTokens = tokens.Select(static token => token.ToLowerInvariant()).ToArray();
        var commonWordCount = normalizedTokens.Count(CommonWords.Contains);
        var structuredQuantityCount = EmbeddedStructuredQuantityContinuationRegex().Matches(text).Count;
        var numberedStepCount = NumberedStructuredStepRegex().Matches(text).Count;
        var quoteSlashPipeCount = text.Count(static ch =>
            ch is '\'' or '\u2019' or '\u2018' or '"' or '/' or '\\' or '|');
        var suspiciousInternalCaseTokens = tokens.Count(static token =>
            ContainsLatinLetter(token) && HasSuspiciousInternalCaseSwitch(token));
        var symbolCount = text.Count(static ch =>
            !char.IsLetterOrDigit(ch)
            && !char.IsWhiteSpace(ch)
            && ch is not '.' and not ',' and not ';' and not ':' and not '(' and not ')' and not '-' and not '+');
        var symbolRatio = (double)symbolCount / Math.Max(1, text.Length);

        return LooksLikePreservableStructuredContentSegment(
            tokens.Length,
            commonWordCount,
            structuredQuantityCount,
            numberedStepCount,
            symbolRatio,
            quoteSlashPipeCount,
            suspiciousInternalCaseTokens);
    }

    private static bool LooksLikeProbableNoiseSegment(string text)
    {
        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 4)
            return false;

        var normalizedTokens = tokens
            .Select(static token => token.ToLowerInvariant())
            .ToArray();
        var commonWordCount = normalizedTokens.Count(CommonWords.Contains);
        var hasUsefulTechnicalIdentifier = ContainsUsefulTechnicalIdentifier(text);

        var mixedLetterDigitTokens = normalizedTokens.Count(ContainsLatinLettersAndDigits);
        var longLowVowelTokens = normalizedTokens.Count(static token =>
            token.Length >= 7
            && ContainsLatinLetter(token)
            && LatinLetterVowelRatio(token) < 0.18);
        var longLetterTokens = normalizedTokens.Count(static token =>
            token.Count(char.IsLetter) >= 7);
        var shortTokens = normalizedTokens.Count(static token => token.Length <= 3);
        var suspiciousInternalCaseTokens = tokens.Count(static token =>
            ContainsLatinLetter(token) && HasSuspiciousInternalCaseSwitch(token));
        var quoteSlashPipeCount = text.Count(static ch =>
            ch is '\'' or '\u2019' or '\u2018' or '"' or '/' or '\\' or '|');
        var layoutSeparatorCount = text.Count(static ch =>
            ch is '/' or '\\' or '|' or '\u00ae' or '\u00a9');
        var singleLetterTokens = tokens.Count(IsSingleLetterToken);
        var gluedInitialIOrLTokens = normalizedTokens.Count(LooksLikeGluedInitialIOrLToken);
        var compactMeasureTokens = normalizedTokens.Count(LooksLikeCompactMeasureToken);
        var structuredQuantityCount = EmbeddedStructuredQuantityContinuationRegex().Matches(text).Count;
        var numberedStepCount = NumberedStructuredStepRegex().Matches(text).Count;
        var brokenSeparatedWordPairs = CountBrokenSeparatedWordPairs(normalizedTokens);
        var shortSuspiciousTokens = normalizedTokens.Count(static token =>
            token.Length <= 3
            && ContainsLatinLetter(token)
            && !CommonWords.Contains(token));
        var hyphenatedLongTokens = normalizedTokens.Count(static token =>
            token.Length >= 10
            && token.Contains('-', StringComparison.Ordinal)
            && token.Count(char.IsLetter) >= 8);
        var symbolCount = text.Count(static ch =>
            !char.IsLetterOrDigit(ch)
            && !char.IsWhiteSpace(ch)
            && ch is not '.' and not ',' and not ';' and not ':' and not '(' and not ')' and not '-' and not '+');
        var symbolRatio = (double)symbolCount / Math.Max(1, text.Length);
        var strongSymbolNoiseShape =
            (symbolRatio >= 0.12 && tokens.Length >= 10)
            || (quoteSlashPipeCount >= 5 && symbolRatio >= 0.05)
            || (symbolRatio >= 0.08 && shortTokens >= 8);
        var rotatedTextNoiseShape =
            symbolRatio >= 0.02
            && tokens.Length >= 18
            && shortTokens >= 10
            && suspiciousInternalCaseTokens >= 4;
        var brokenSupplementalOcrShape =
            tokens.Length >= 10
            && shortSuspiciousTokens >= 4
            && commonWordCount <= Math.Max(6, tokens.Length / 2)
            && (symbolRatio >= 0.005
                || quoteSlashPipeCount >= 1
                || gluedInitialIOrLTokens >= 1
                || compactMeasureTokens >= 1);
        var layoutDamagedSupplementShape =
            tokens.Length >= 10
            && layoutSeparatorCount >= 1
            && brokenSeparatedWordPairs >= 1
            && shortSuspiciousTokens >= 2
            && commonWordCount <= Math.Max(10, tokens.Length * 3 / 4);
        var metadataLayoutDamagedSupplementShape = LooksLikeMetadataLayoutDamagedSupplement(text);

        if (hasUsefulTechnicalIdentifier && symbolRatio < 0.16 && quoteSlashPipeCount < 8)
            return false;

        if (LooksLikePreservableStructuredContentSegment(
                tokens.Length,
                commonWordCount,
                structuredQuantityCount,
                numberedStepCount,
                symbolRatio,
                quoteSlashPipeCount,
                suspiciousInternalCaseTokens))
            return false;

        if (commonWordCount >= 2
            && !strongSymbolNoiseShape
            && !rotatedTextNoiseShape
            && !brokenSupplementalOcrShape
            && !layoutDamagedSupplementShape
            && !metadataLayoutDamagedSupplementShape)
        {
            return false;
        }

        return mixedLetterDigitTokens >= 2
            || (singleLetterTokens >= 1 && gluedInitialIOrLTokens >= 1 && tokens.Length <= 6)
            || (singleLetterTokens >= 2 && compactMeasureTokens >= 1 && tokens.Length <= 8)
            || longLowVowelTokens >= 2
            || (quoteSlashPipeCount >= 3 && tokens.Length >= 6 && longLetterTokens >= 3 && shortTokens >= 1)
            || (quoteSlashPipeCount >= 3 && hyphenatedLongTokens >= 1 && longLetterTokens >= 2)
            || (symbolRatio >= 0.055 && mixedLetterDigitTokens >= 1)
            || (symbolRatio >= 0.08 && tokens.Length >= 6)
            || (strongSymbolNoiseShape && commonWordCount <= 1 && tokens.Length >= 4)
            || rotatedTextNoiseShape
            || brokenSupplementalOcrShape
            || layoutDamagedSupplementShape
            || metadataLayoutDamagedSupplementShape
            || (commonWordCount == 0 && suspiciousInternalCaseTokens >= 4 && shortTokens >= 6);
    }

    private static bool LooksLikePreservableStructuredContentSegment(
        int tokenCount,
        int commonWordCount,
        int structuredQuantityCount,
        int numberedStepCount,
        double symbolRatio,
        int quoteSlashPipeCount,
        int suspiciousInternalCaseTokens)
    {
        if (tokenCount < 12 || commonWordCount < 3)
            return false;
        if (structuredQuantityCount < 3 || numberedStepCount < 1)
            return false;
        if (symbolRatio >= 0.12 || quoteSlashPipeCount >= 8 || suspiciousInternalCaseTokens >= 4)
            return false;

        return true;
    }

    private static bool LooksLikeDamagedLayoutSupplement(string text)
    {
        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value).ToLowerInvariant())
            .Where(static token => token.Length >= 2)
            .ToArray();

        return tokens.Length >= 8
            && text.Any(static ch => ch is '|' or '/' or '\\' or '\u00ae' or '\u00a9')
            && CountBrokenSeparatedWordPairs(tokens) >= 1;
    }

    private static bool LooksLikeMetadataLayoutDamagedSupplement(string text)
    {
        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value).ToLowerInvariant())
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 10)
            return false;

        var metadataLabelCount = ShortMetadataLabelRegex().Matches(text).Count;
        if (metadataLabelCount < 3)
            return false;

        var commonWordCount = tokens.Count(CommonWords.Contains);
        var shortSuspiciousTokens = tokens.Count(static token =>
            token.Length <= 3
            && ContainsLatinLetter(token)
            && !CommonWords.Contains(token));
        var layoutSeparatorCount = text.Count(static ch =>
            ch is '/' or '\\' or '|' or '\u00ae' or '\u00a9');
        var mixedLetterDigitTokens = tokens.Count(ContainsLatinLettersAndDigits);
        var compactMeasureTokens = tokens.Count(LooksLikeCompactMeasureToken);
        var gluedInitialIOrLTokens = tokens.Count(LooksLikeGluedInitialIOrLToken);

        return shortSuspiciousTokens >= 2
            && commonWordCount <= Math.Max(18, tokens.Length * 4 / 5)
            && (layoutSeparatorCount >= 1
                || mixedLetterDigitTokens >= 1
                || compactMeasureTokens >= 1
                || gluedInitialIOrLTokens >= 1);
    }

    private static string RemoveTrailingDamagedFragmentAfterCompleteContent(string text)
    {
        for (var index = text.Length - 2; index >= 50; index--)
        {
            var ch = text[index];
            if (ch is not ('.' or '!' or '?' or '\u2026'))
                continue;
            if (ch == '.' && LooksLikeInlineSingleLetterAbbreviationPeriod(text, index))
                continue;

            if (index >= text.Length - 8)
                continue;

            var prefix = text[..(index + 1)].Trim();
            var suffix = text[(index + 1)..].Trim();
            if (prefix.Length < 80 || suffix.Length is < 8 or > 220)
                return text;

            if (LooksLikeTrailingDamagedFragment(suffix))
                return prefix;
        }

        return text;
    }

    private static bool LooksLikeInlineSingleLetterAbbreviationPeriod(string text, int periodIndex)
    {
        var previous = PreviousNonWhitespace(text, periodIndex - 1);
        if (previous < 0 || !char.IsLetter(text[previous]))
            return false;

        if (previous > 0 && char.IsLetterOrDigit(text[previous - 1]))
            return false;

        var next = NextNonWhitespace(text, periodIndex + 1);
        return next >= 0 && char.IsLetter(text[next]);
    }

    private static int PreviousNonWhitespace(string text, int start)
    {
        for (var i = Math.Min(start, text.Length - 1); i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
                return i;
        }

        return -1;
    }

    private static int NextNonWhitespace(string text, int start)
    {
        for (var i = Math.Max(0, start); i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return i;
        }

        return -1;
    }

    private static bool LooksLikeTrailingDamagedFragment(string suffix)
    {
        var trimmed = suffix.Trim();
        if (BrokenSpacedDigitRegex().IsMatch(suffix) || BrokenDigitLetterRunRegex().IsMatch(suffix))
            return true;

        if (LooksLikeStructuredQuantityContinuation(trimmed)
            || LooksLikeUsefulProseWithStructuredQuantityContinuation(trimmed))
            return false;

        if (LooksLikeTechnicalFigureLabelFragment(trimmed))
            return false;

        if (LooksLikeProbableNoiseText(suffix))
            return true;

        if (LooksLikeSymbolDamagedShortSupplement(trimmed))
            return true;

        if (EndsWithSentencePunctuation(trimmed))
            return false;

        return trimmed.Length <= 90 && StartsWithLowercaseLetter(trimmed);
    }

    private static bool LooksLikeTechnicalFigureLabelFragment(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length is < 24 or > 180)
            return false;
        if (NoisyMetadataLeadMarkerRegex().IsMatch(text)
            || TrailingSupplementStartRegex().IsMatch($" {text}")
            || LooksLikeSymbolDamagedShortSupplement(text))
        {
            return false;
        }

        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length is < 4 or > 28)
            return false;

        var substantiveWords = tokens.Count(static token =>
            token.Count(IsLatinLetter) >= 4
            && LatinLetterVowelRatio(token) >= 0.16);
        var technicalCueCount = TechnicalContentCueRegex().Matches(text).Count;
        var figureCueCount = TechnicalFigureOrChartCueRegex().Matches(text).Count;
        var symbolCount = text.Count(static ch =>
            !char.IsLetterOrDigit(ch)
            && !char.IsWhiteSpace(ch)
            && ch is not '.' and not ',' and not ';' and not ':' and not '(' and not ')'
            && ch is not '[' and not ']' and not '-' and not '\u2013' and not '\u2014'
            && ch is not '+' and not '&' and not '@' and not '=' and not '<' and not '>');

        return symbolCount <= Math.Max(2, text.Length / 24)
            && substantiveWords >= Math.Max(4, tokens.Length / 3)
            && figureCueCount >= 3
            && technicalCueCount >= 1;
    }

    private static bool LooksLikeUsefulProseWithStructuredQuantityContinuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length is < 48 or > 260)
            return false;
        if (NoisyMetadataLeadMarkerRegex().IsMatch(text)
            || TrailingSupplementStartRegex().IsMatch($" {text}")
            || LooksLikeSymbolDamagedShortSupplement(text))
        {
            return false;
        }

        var quantityMatch = EmbeddedStructuredQuantityContinuationRegex().Match(text);
        if (!quantityMatch.Success || quantityMatch.Index < 12)
            return false;

        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value).ToLowerInvariant())
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length is < 8 or > 48)
            return false;

        var letters = text.Count(char.IsLetter);
        var sentencePunctuation = text.Count(static ch => ch is '.' or '!' or '?' or '\u2026');
        return letters >= 24 && sentencePunctuation >= 1;
    }

    private static bool LooksLikeStructuredQuantityContinuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length is < 12 or > 220)
            return false;
        if (!LeadingStructuredQuantityContinuationRegex().IsMatch(text))
            return false;

        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value).ToLowerInvariant())
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length is < 5 or > 40)
            return false;

        var letterCount = text.Count(char.IsLetter);
        var digitCount = text.Count(char.IsDigit);
        return letterCount >= 8 && digitCount <= Math.Max(12, letterCount);
    }

    private static string RemoveLeadingNoisyMetadataSupplement(string text)
    {
        var match = LeadingNoisyMetadataSupplementRegex().Match(text);
        if (!match.Success)
            return text;

        var body = match.Groups["body"].Value.Trim();
        if (body.Length < 32 || LooksLikeProbableNoiseText(body))
            return string.Empty;

        return body;
    }

    private static bool LooksLikeFragmentBeforeNoisyMetadata(string prefix)
    {
        var normalized = Regex.Replace(prefix, @"\s+", " ").Trim();
        if (normalized.Length is < 12 or > 260)
            return false;
        if (EndsWithSentencePunctuation(normalized))
            return false;

        var tokens = TokenRegex().Matches(normalized)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length is < 4 or > 45)
            return false;

        return StartsWithLowercaseLetter(normalized)
            || !normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2026');
    }

    private static bool LooksLikeSymbolDamagedShortSupplement(string text)
    {
        if (text.Length is < 12 or > 160)
            return false;

        return text.Contains('#', StringComparison.Ordinal)
            || BrokenSentenceSymbolRunRegex().IsMatch(text);
    }

    private static bool LooksLikeDamagedMetadataPrefix(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length < 60 || !DamagedMetadataPrefixRegex().IsMatch(normalized))
            return false;

        var tokens = TokenRegex().Matches(normalized)
            .Select(static match => TrimToken(match.Value).ToLowerInvariant())
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 10)
            return false;

        var shortSuspiciousTokens = tokens.Count(static token =>
            token.Length <= 3
            && ContainsLatinLetter(token)
            && !CommonWords.Contains(token));

        return shortSuspiciousTokens >= 3
            || BrokenSpacedDigitRegex().IsMatch(normalized)
            || BrokenDigitLetterRunRegex().IsMatch(normalized);
    }

    private static bool LooksLikeNoisyMetadataOnlyLine(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length is < 40 or > 160)
            return false;

        var metadataLabelCount = ShortMetadataLabelRegex().Matches(normalized).Count;
        var startsWithNoisyMetadataMarker = NoisyMetadataLeadMarkerRegex().IsMatch(normalized);
        if (metadataLabelCount < 2 && !startsWithNoisyMetadataMarker)
            return false;

        if (!startsWithNoisyMetadataMarker
            && normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2022'))
        {
            return false;
        }

        var rawTokens = TokenRegex().Matches(normalized)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length > 0)
            .ToArray();
        var tokens = rawTokens
            .Select(static token => token.ToLowerInvariant())
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length is < 5 or > 60)
            return false;

        var shortSuspiciousTokens = tokens.Count(static token =>
            token.Length <= 3
            && ContainsLatinLetter(token)
            && !CommonWords.Contains(token));
        var uppercaseShortMarkers = rawTokens.Count(static token =>
            token.Length <= 4
            && token.Any(IsLatinLetter)
            && token.Where(IsLatinLetter).All(char.IsUpper)
            && !CommonWords.Contains(token.ToLowerInvariant()));
        var mixedLetterDigitTokens = tokens.Count(ContainsLatinLettersAndDigits);
        var noisySymbolCount = normalized.Count(static ch => ch is '&' or '|' or '\u00ae' or '\u00a9');

        return shortSuspiciousTokens >= 2
            || startsWithNoisyMetadataMarker && uppercaseShortMarkers >= 1
            || uppercaseShortMarkers >= 2
            || mixedLetterDigitTokens >= 1 && shortSuspiciousTokens >= 1
            || noisySymbolCount >= 1 && shortSuspiciousTokens >= 1;
    }

    private static bool LooksLikeShortMetadataScheduleOnlyLine(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length is < 32 or > 180)
            return false;
        normalized = normalized.TrimEnd('.');
        if (normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2022'))
            return false;

        var metadataLabelCount = ShortMetadataLabelRegex().Matches(normalized).Count;
        if (metadataLabelCount < 2)
            return false;

        var durationMatches = MetadataScheduleValueRegex().Matches(normalized);
        if (durationMatches.Count == 0)
            return false;

        var lastDuration = durationMatches[^1];
        var tail = normalized[(lastDuration.Index + lastDuration.Length)..].Trim();
        var tailTokens = TokenRegex().Matches(tail)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tailTokens.Length < 3)
            return false;
        if (!tailTokens.Any(static token => token.Length == 1 && token.Any(IsLatinLetter)))
            return false;

        var tokens = TokenRegex().Matches(normalized)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length is < 5 or > 32)
            return false;

        var longContentTokens = tokens.Count(static token =>
            token.Length >= 7
            && token.Any(IsLatinLetter)
            && !CommonWords.Contains(token));

        return longContentTokens <= Math.Max(4, tokens.Length / 3);
    }

    private static bool LooksLikeShortStandaloneLayoutMetadataOnlyLine(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim().TrimEnd('.');
        if (normalized.Length is < 16 or > 220)
            return false;
        if (normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2026'))
            return false;

        var durationMatches = MetadataScheduleValueRegex().Matches(normalized);
        if (durationMatches.Count == 0)
            return false;

        var hasDecorativeLayoutMarker = normalized.Any(static ch => ch is '\u2022' or '*' or '|');
        var labelCount = ShortMetadataLabelRegex().Matches(normalized).Count;
        if (!hasDecorativeLayoutMarker && (labelCount > 0 || durationMatches.Count < 2))
            return false;

        var tokens = TokenRegex().Matches(normalized)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length is < 4 or > 32)
            return false;

        var longContentTokens = tokens.Count(static token =>
            token.Count(IsLatinLetter) >= 4
            && !IsStandaloneLayoutMetadataValueToken(token));

        if (durationMatches.Count >= 2)
            return longContentTokens <= (hasDecorativeLayoutMarker ? 4 : 2);

        return hasDecorativeLayoutMarker && tokens.Length <= 14 && longContentTokens <= 2;
    }

    private static bool IsStandaloneLayoutMetadataValueToken(string token)
    {
        var normalized = token.Trim(' ', '-', ':', ';', ',', '.', '|', '/', '\\', '(', ')', '*', '\u2022')
            .ToLowerInvariant();
        if (normalized.Length == 0)
            return true;
        if (CommonWords.Contains(normalized))
            return true;
        if (normalized.All(static ch => char.IsDigit(ch) || ch is ',' or '.'))
            return true;

        return StandaloneLayoutMetadataValueTokenRegex().IsMatch(normalized);
    }

    private static bool LooksLikeShortMetadataResidue(string text)
    {
        var rawTokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length > 0)
            .ToArray();
        if (rawTokens.Length is < 2 or > 5)
            return false;

        var normalizedTokens = rawTokens.Select(static token => token.ToLowerInvariant()).ToArray();
        var commonWordCount = normalizedTokens.Count(CommonWords.Contains);
        var uppercaseShortMarkers = rawTokens.Count(static token =>
            token.Length <= 4
            && token.Any(IsLatinLetter)
            && token.Where(IsLatinLetter).All(char.IsUpper)
            && !CommonWords.Contains(token.ToLowerInvariant()));

        return uppercaseShortMarkers >= 1 && commonWordCount >= 1;
    }

    private static bool LooksLikeShortBrokenProseFragment(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length is < 45 or > 180)
            return false;
        if (ContainsUsefulTechnicalIdentifier(normalized))
            return false;
        if (!BrokenFunctionWordFragmentRegex().IsMatch(normalized))
            return false;

        var tokens = TokenRegex().Matches(normalized)
            .Select(static match => TrimToken(match.Value).ToLowerInvariant())
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length is < 8 or > 35)
            return false;

        var commonWordCount = tokens.Count(CommonWords.Contains);
        var sentencePunctuationCount = normalized.Count(static ch => ch is '.' or '!' or '?' or '\u2026');
        return sentencePunctuationCount >= 1
            && commonWordCount >= Math.Max(3, tokens.Length / 4);
    }

    private static bool EndsWithSentencePunctuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var last = text.TrimEnd()[^1];
        return last is '.' or '!' or '?' or '\u2026' or '"' or '\u201d' or '\u00bb';
    }

    private static bool StartsWithLowercaseLetter(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch))
                continue;

            return char.IsLower(ch);
        }

        return false;
    }

    private static bool EndsLikeCompleteContentBeforeSupplement(string prefix)
    {
        var trimmed = prefix.TrimEnd();
        if (trimmed.Length == 0)
            return false;

        var last = trimmed[^1];
        return last is '.' or '!' or '?' or '\u2026'
            || trimmed.EndsWith("\u00bb", StringComparison.Ordinal)
            || trimmed.EndsWith("\"", StringComparison.Ordinal)
            || trimmed.EndsWith("\u201d", StringComparison.Ordinal)
            || CompleteSentenceWithShortTrailingBulletRegex().IsMatch(trimmed);
    }

    private static string TrimToken(string token)
        => token.Trim('\'', '\u2019', '\u2018', '"', '.', ',', ';', ':', '|', '/', '\\', '-', '\u2013', '\u2014');

    private static bool LooksLikeSpacedLetterRunNoise(string text)
    {
        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length < 6)
            return false;

        var singleLetterCount = 0;
        var currentRun = 0;
        var maxRun = 0;
        foreach (var token in tokens)
        {
            if (IsSpacedLetterNoiseToken(token))
            {
                singleLetterCount++;
                currentRun++;
                maxRun = Math.Max(maxRun, currentRun);
                continue;
            }

            currentRun = 0;
        }

        var normalWordTokens = tokens.Count(static token => token.Count(char.IsLetter) >= 4);
        var singleLetterRatio = (double)singleLetterCount / tokens.Length;
        var commonLongWordTokens = tokens.Count(static token =>
            token.Count(char.IsLetter) >= 4 && CommonWords.Contains(token.ToLowerInvariant()));
        if (tokens.Length >= 40
            && singleLetterCount >= 12
            && singleLetterRatio >= 0.18
            && commonLongWordTokens <= Math.Max(8, tokens.Length / 8)
            && normalWordTokens <= tokens.Length / 2)
        {
            return true;
        }

        if (maxRun < 5)
            return false;

        if (maxRun >= 12 && singleLetterCount >= 16 && singleLetterRatio >= 0.30)
            return true;
        if (maxRun >= 12 && singleLetterCount >= 14 && singleLetterRatio >= 0.30)
            return true;

        return maxRun >= 8
            && singleLetterRatio >= 0.35
            && normalWordTokens <= 5
            || maxRun >= 5
            && singleLetterRatio >= 0.55
            && normalWordTokens <= 4;
    }

    private static bool IsSingleLetterToken(string token)
        => token.Length == 1 && token.Any(IsLatinLetter);

    private static bool IsSpacedLetterNoiseToken(string token)
        => IsSingleLetterToken(token)
           || string.Equals(token, "ff", StringComparison.OrdinalIgnoreCase)
           || string.Equals(token, "fl", StringComparison.OrdinalIgnoreCase)
           || string.Equals(token, "fi", StringComparison.OrdinalIgnoreCase)
           || string.Equals(token, "ffi", StringComparison.OrdinalIgnoreCase)
           || string.Equals(token, "ffl", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeShortGluedOcrNoiseText(string text)
    {
        var tokens = TokenRegex().Matches(text)
            .Select(static match => TrimToken(match.Value).ToLowerInvariant())
            .Where(static token => token.Length > 0)
            .ToArray();
        return tokens.Length > 0
            && tokens.Length <= 4
            && tokens[0] == "e"
            && tokens.Any(LooksLikeGluedInitialIOrLToken);
    }

    private static string RemoveLeadingShortLayoutPrefixBeforeStructuredLabels(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Trim();
        var cleaned = LeadingShortLayoutPrefixBeforeStructuredLabelsRegex()
            .Replace(normalized, string.Empty, 1)
            .TrimStart();
        return cleaned.Length == 0 ? normalized : cleaned;
    }

    private static bool LooksLikeShortPageHeaderFooterFragment(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length is < 8 or > 55)
            return false;
        if (!char.IsDigit(normalized[^1]))
            return false;
        if (normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2022'))
            return false;

        var tokens = TokenRegex().Matches(normalized)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length is < 3 or > 8)
            return false;

        var letterTokens = tokens.Where(ContainsLatinLetter).ToArray();
        if (letterTokens.Length < 3)
            return false;

        var letters = normalized.Where(IsLatinLetter).ToArray();
        if (letters.Length < 8)
            return false;

        var uppercaseRatio = letters.Count(char.IsUpper) / (double)letters.Length;
        var digitCount = normalized.Count(char.IsDigit);
        return uppercaseRatio >= 0.70
            && digitCount is >= 1 and <= 4;
    }

    private static bool LooksLikeShortBrokenHeaderFooterFragment(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length is < 3 or > 40)
            return false;
        if (!normalized.EndsWith("-", StringComparison.Ordinal))
            return false;
        if (normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2022' or ':' or ';'))
            return false;
        if (normalized.Any(char.IsDigit))
            return false;

        var tokens = TokenRegex().Matches(normalized)
            .Select(static match => TrimToken(match.Value))
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length is < 1 or > 4)
            return false;

        var letters = normalized.Where(IsLatinLetter).ToArray();
        return letters.Length >= 3 && letters.All(char.IsUpper);
    }

    private static string RemoveSpacedLetterRunNoiseFromLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var matches = TokenRegex().Matches(line)
            .Select(static match => new TokenSpan(match.Index, match.Length, TrimToken(match.Value)))
            .Where(static token => token.Text.Length > 0)
            .ToArray();
        if (matches.Length == 0)
            return line.Trim();

        if (matches.Length <= 3
            && matches.All(static token => IsSpacedLetterNoiseToken(token.Text))
            && matches.Any(static token => token.Text.Any(char.IsLower)))
        {
            return string.Empty;
        }

        var ranges = new List<(int Start, int End)>();
        var runStart = -1;
        var runEnd = -1;
        var runTokens = new List<TokenSpan>();

        void FlushRun()
        {
            if (ShouldRemoveSpacedLetterRun(runTokens, matches.Length, runStart, line))
            {
                var start = runStart;
                var end = runEnd;
                while (end < line.Length && IsTrailingNoisePunctuation(line[end]))
                    end++;
                ranges.Add((start, end));
            }

            runStart = -1;
            runEnd = -1;
            runTokens.Clear();
        }

        foreach (var token in matches)
        {
            if (IsSpacedLetterNoiseToken(token.Text))
            {
                if (runStart < 0)
                    runStart = token.Start;
                runEnd = token.End;
                runTokens.Add(token);
                continue;
            }

            FlushRun();
        }

        FlushRun();
        if (ranges.Count == 0)
            return Regex.Replace(line, @"[ \t]{2,}", " ").Trim();

        var builder = new System.Text.StringBuilder(line.Length);
        var cursor = 0;
        foreach (var range in ranges.OrderBy(static range => range.Start))
        {
            if (range.Start > cursor)
                builder.Append(line, cursor, range.Start - cursor);
            cursor = Math.Max(cursor, range.End);
        }

        if (cursor < line.Length)
            builder.Append(line, cursor, line.Length - cursor);

        var cleaned = Regex.Replace(builder.ToString(), @"[ \t]{2,}", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"^(?:\p{Ll}\s+){2,}(?=\p{Ll}{3,})", string.Empty, RegexOptions.CultureInvariant).Trim();
        cleaned = Regex.Replace(cleaned, @"^\s*[!?,;:.\u2026-]+\s*", string.Empty).Trim();
        return cleaned;
    }

    private static bool ShouldRemoveSpacedLetterRun(
        IReadOnlyList<TokenSpan> runTokens,
        int lineTokenCount,
        int runStart,
        string line)
    {
        if (runTokens.Count == 0)
            return false;
        if (runTokens.All(static token => token.Text.All(char.IsUpper)))
            return false;

        var lowercaseCount = runTokens.Count(static token => token.Text.Any(char.IsLower));
        if (runTokens.Count >= 2 && lowercaseCount == runTokens.Count && IsAtLineStart(line, runStart))
            return true;
        if (runTokens.Count >= 8 && lowercaseCount >= 1)
            return true;
        if (runTokens.Count >= 5 && lowercaseCount >= 3)
            return true;

        return lineTokenCount <= 4 && runTokens.Count >= 3 && lowercaseCount >= 1;
    }

    private static bool IsAtLineStart(string line, int index)
    {
        for (var i = 0; i < index && i < line.Length; i++)
        {
            if (!char.IsWhiteSpace(line[i]) && !IsTrailingNoisePunctuation(line[i]))
                return false;
        }

        return true;
    }

    private static bool IsTrailingNoisePunctuation(char ch)
        => char.IsWhiteSpace(ch)
           || ch is '!' or '?' or ',' or ';' or ':' or '.' or '\u2026' or '-' or '\u2013' or '\u2014';

    private static bool ContainsLatinLettersAndDigits(string token)
        => ContainsLatinLetter(token) && token.Any(char.IsDigit);

    private static bool ContainsUsefulTechnicalIdentifier(string text)
        => TechnicalIdentifierRegex().IsMatch(text);

    private static bool LooksLikeGluedInitialIOrLToken(string token)
        => token.Length >= 5
           && token[0] is 'i' or 'l' or '1'
           && !IsVowel(token[1])
           && token.Skip(1).Count(IsLatinLetter) >= 4
           && LatinLetterVowelRatio(token[1..]) >= 0.25;

    private static bool LooksLikeCompactMeasureToken(string token)
        => CompactMeasureRegex().IsMatch(token);

    private static int CountBrokenSeparatedWordPairs(IReadOnlyList<string> tokens)
    {
        var count = 0;
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var left = tokens[i];
            var right = tokens[i + 1];
            if (left.Length is < 2 or > 3 || right.Length is < 3 or > 8)
                continue;
            if (!left.All(IsLatinLetter) || !right.All(IsLatinLetter))
                continue;
            if (CommonWords.Contains(left) || CommonWords.Contains(right))
                continue;

            var combined = left + right;
            if (combined.Length < 6)
                continue;

            var ratio = LatinLetterVowelRatio(combined);
            if (ratio is >= 0.25 and <= 0.75)
                count++;
        }

        return count;
    }

    private static bool ContainsLatinLetter(string token)
        => token.Any(IsLatinLetter);

    private static bool IsLatinLetter(char ch)
        => ch is >= 'A' and <= 'Z'
           or >= 'a' and <= 'z'
           or >= '\u00c0' and <= '\u024f'
           or >= '\u1e00' and <= '\u1eff';

    private static bool HasSuspiciousInternalCaseSwitch(string token)
    {
        if (token.Length < 4 || !token.Any(char.IsLower) || !token.Any(char.IsUpper))
            return false;

        for (var i = 1; i < token.Length - 1; i++)
        {
            if (char.IsUpper(token[i]) && char.IsLower(token[i - 1]))
                return true;
        }

        return false;
    }

    private static double LatinLetterVowelRatio(string token)
    {
        var letters = token.Count(IsLatinLetter);
        if (letters == 0)
            return 1;

        var vowels = token.Count(ch => IsLatinLetter(ch) && IsVowel(ch));
        return (double)vowels / letters;
    }

    private static bool IsVowel(char ch)
    {
        ch = char.ToLowerInvariant(ch);
        return ch is 'a' or 'e' or 'i' or 'o' or 'u' or 'y'
            or '\u00e0' or '\u00e2' or '\u00e4'
            or '\u00e9' or '\u00e8' or '\u00ea' or '\u00eb'
            or '\u00ee' or '\u00ef'
            or '\u00f4' or '\u00f6'
            or '\u00f9' or '\u00fb' or '\u00fc'
            or '\u00e1' or '\u00ed' or '\u00f3' or '\u00fa' or '\u00fd'
            or '\u00e6' or '\u0153';
    }

    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "de", "der", "des", "die", "du",
        "en", "et", "for", "from", "in", "is", "la", "le", "les", "of", "on", "or", "the",
        "to", "und", "with", "au", "aux", "avec", "dans", "pour", "que", "qui", "sur",
        "ne", "pas", "nicht", "no", "not", "non", "sin", "sem", "sans", "con", "com",
        "el", "los", "las", "una", "un", "para", "por", "esta", "este", "y", "o",
        "il", "lo", "gli", "per", "nel", "nella", "sono", "come",
        "uma", "um", "voce", "nao", "se", "si", "este", "esta",
        "das", "den", "dem", "ist", "sind", "mit", "fuer", "für", "von", "zu", "im",
        "werden", "wird", "durch", "nach", "bei", "aus",
        "de", "het", "een", "van", "voor", "met", "op",
        "och", "att", "som", "den", "det",
        "og", "af", "til",
        "ja", "on", "se", "tama",
        "ve", "ile", "icin", "bu"
    };

    [GeneratedRegex(@"[\p{L}\p{N}'\u2019\u2018\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^(?<lead>.{4,140}?)\s*(?:\[\s*)?(?:index|indice)\s*:?\s*(?:\]\s*)?(?<tail>.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InlineIndexReferenceTailRegex();

    [GeneratedRegex(@"[\p{L}]{2,}\d[\p{L}\p{N}_\-]{6,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InternalReferenceCodeRegex();

    [GeneratedRegex(@"\b(?:EN|ISO|IEC|ASTM|DIN|DVS|NFPA|API|ANSI|CEN|TR|TS|PD|BS|NF|SN|UL|CSA)(?:[\s._/\-]+[A-Z]{1,6}){0,4}[\s._/\-]*\d[A-Z0-9._/\-:]*\b", RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalIdentifierRegex();

    [GeneratedRegex(@"\b(?:A\.)?\d{1,3}(?:[\.,]\d{1,3}){1,5}\b|\b(?:annex|appendix|chapter|figure|fig\.?|section|table)\s+[A-Z]?\d{1,4}(?:[\.,]\d{1,4}){0,5}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalSectionReferenceRegex();

    [GeneratedRegex(@"\b(?:abmessungen|anforderungen|authority|berechnung|bild|circuit|clamp|conductor|control|dichtung(?:en)?|disconnecting|drive|druck|electrical|enclosure|equipment|fault|fitting|flansch(?:e|es)?|gasket|gleichung|ground(?:ed|ing)?|industrial|interlock(?:ing)?|kunststoff(?:e|en)?|machine(?:ry)?|material|materials|mechanische|motor|normen?|overcurrent|pipe|pressure|probe(?:n|koerper)?|probekorper|protect(?:ion|ive)?|pruef(?:en|ung)|pruf(?:en|ung)|rating|richtlinien?|rohre?|safety|schwei(?:ss|b|\u00df)|shall|standard|standards|switch(?:ing)?|symbol|tabelle|technical|technische|thermoplast(?:e|en|ic|ics)|tube|voltage|werkstoff(?:e|en|s)?|wire(?:way|s)?|wiring)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalContentCueRegex();

    [GeneratedRegex(@"\b(?:collateral|directive|embedded|examples?|information|messages?|panel|product|section|signal|supplemental)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalTopicListCueRegex();

    [GeneratedRegex(@"\b(?:accident|alert|assigned|avoid(?:ed|ance)?|category|categories|caution|classification|classify|combination|credible|damage|decision|hazard(?:ous)?|harm|injur(?:y|ies)|matrix|matrices|moderate|minor|notice|preferred|probabilit(?:y|ies)|risk|selection|serious|severity|signal|symbol|warning|worst)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalDecisionMatrixCueRegex();

    [GeneratedRegex(@"\b(?:axis|bar|bars|bus|caption|chart|circuit|connection|control|current|diagram|distribution|electrical|enclosure|exterior|figure|horizontal|input|interior|layout|legend|line|lockout|main|module|output|panel|plot|rated|rating|remote|schematic|short-circuit|support(?:s|ed)?|supply|terminal|value|vertical|voltage|wiring)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalFigureOrChartCueRegex();

    [GeneratedRegex(@"(?:^|[\s;:.])(?:[a-z]\)|\d+\)|\([a-z]\)|\(\d+\))\s+\p{Lu}|\b(?:obtain|select|move|determine|verify|record|install|connect|disconnect)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EnumeratedInstructionCueRegex();

    [GeneratedRegex(@"\b(?:agency|agencies|association|associations|assoc|authority|authorities|center|centre|college|commission|committee|company|companies|corporation|council|department|division|engineers?|equipment|foundation|group|institute|institutes|industry|industries|laborator(?:y|ies)|lab|manufacturer(?:s)?|mfrs|office|organization|organisations?|organizations?|partners?|service|services|societ(?:y|ies)|supplier(?:s)?|supply|systems|university)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EntityRosterCueRegex();

    [GeneratedRegex(@"\b(?:AG|Alt|Assoc|Co|Corp|Div|GmbH|Inc|Inst|Lab|LLC|Ltd|Mfrs|PLC|SA|Sarl)\.?\b", RegexOptions.CultureInvariant)]
    private static partial Regex EntityAbbreviationRegex();

    [GeneratedRegex(@"\b\p{Lu}[\p{Ll}\u00df-\u024f]{2,}(?:\s+\p{Lu}\.)?\s+\p{Lu}[\p{Ll}\u00df-\u024f]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex PersonNamePairRegex();

    [GeneratedRegex(@"\*?\b\p{Lu}\.\s+\p{Lu}[\p{Ll}\u00df-\u024f]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex InitialPersonNameRegex();

    [GeneratedRegex(@"\b(?:address|adresse|contact|courriel|e-?mail|fax|mail|phone|postcode|postal|strasse|street|tel(?:ephone)?|www|zip)\b|[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}.\-]+\.[\p{L}]{2,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ContactSignalRegex();

    [GeneratedRegex(@"\b\d{1,6}\s+(?:[\p{Lu}][\p{Ll}\u00df-\u024f]{2,}|[\p{Lu}]{2,})(?:\s+(?:street|strasse|road|avenue|lane|rue|weg|platz|drive|boulevard|blvd|st\.?|ave\.?))?\b|\b[A-Z]{1,3}-?\d{4,6}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PostalAddressSignalRegex();

    [GeneratedRegex(@"\b(?:accredited|approval|approved|approves?|chair(?:person)?|committee|committees|member|members|organization|represented|representative|secretary|standard|standards|submittal|subcommittee|voted)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StandardsGovernanceCueRegex();

    [GeneratedRegex(@"\b(?:organization\s+represented|name\s+of\s+representative|chair(?:person)?|secretary|vice\s+chair|members?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StandardsRosterCueRegex();

    [GeneratedRegex(@"^\d+(?:[,.]\d+)?[a-z\u00c0-\u024f]{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactMeasureRegex();

    [GeneratedRegex(@"(^|[^\p{L}\p{N}])\p{N}\s+\p{N}([^\p{L}\p{N}]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex BrokenSpacedDigitRegex();

    [GeneratedRegex(@"(^|[^\p{L}\p{N}])\p{N}\s+\p{N}\s+\p{L}{1,2}\s+\p{L}{1,2}\s+\p{N}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BrokenDigitLetterRunRegex();

    [GeneratedRegex(@"^[^\p{L}\p{N}]*\p{L}{1,4}\s+\p{L}{6,20}\s+\d{1,4}\s+\p{L}{2,16}\s+\p{L}{1,4}\s+\p{L}{4,20}\s*[:;]\s*\d", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DamagedMetadataPrefixRegex();

    [GeneratedRegex(@"\b(?:de|of|von|di|del)\s+(?:le|la|les|the|der|die|das|el|il|lo)\s+\p{L}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BrokenFunctionWordFragmentRegex();

    [GeneratedRegex(@"[.!?\u2026]\s+[A-Z]{1,3}\s+\p{L}", RegexOptions.CultureInvariant)]
    private static partial Regex BrokenSentenceSymbolRunRegex();

    [GeneratedRegex(@"(?:^|\s)[\p{L}][\p{L}\p{N}'\u2019\u2018\-]{0,18}\s*[:;]", RegexOptions.CultureInvariant)]
    private static partial Regex ShortMetadataLabelRegex();

    [GeneratedRegex(@"\b\d+(?:[,.]\d+)?\s*(?:s|sec|secs|secondes?|seconds?|min|mins?|minutes?|h|hr|hrs?|heures?|hours?|j|jr|jours?|d|days?|dias?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MetadataScheduleValueRegex();

    [GeneratedRegex(@"^(?:s|sec|secs|secondes?|seconds?|min|mins?|minutes?|h|hr|hrs?|heures?|hours?|j|jr|jours?|d|days?|dias?|time|temps|total|duration|dur[e\u00e9]e|prep|preparation|pr[e\u00e9]paration|rest|pause|easy|facile|simple|medium|moyen|hard|difficile|cheap|cher|cost)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneLayoutMetadataValueTokenRegex();

    [GeneratedRegex(@"\s+(?:[\u00ae\u00a9]\s*,?\s*)?(?:['\u2018\u2019]\s*)?(?:[A-Z]{1,4}[\)\]\}]?\s+)?(?:preparation|pr[e\u00e9]paration|duration|dur[e\u00e9]e|time|temps)\s*[:;]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSupplementStartRegex();

    [GeneratedRegex(@"^[^\p{L}\p{N}]*(?:['\u2018\u2019]\s*)?(?:[A-Z]{1,4}[\)\]\}]?\s+)?(?:preparation|pr[e\u00e9]paration|duration|dur[e\u00e9]e|time|temps)\s*[:;].{8,180}?\b(?:ESA|QI|ER|Q|NE|N|O|D)\b.{0,120}?(?<body>\s+(?:Pour|For|Quant|Why|Pourquoi|[A-Z\u00c0-\u00de][\p{Ll}\u00df-\u00ff]{2,})\b.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadingNoisyMetadataSupplementRegex();

    [GeneratedRegex(@"^[^\p{L}\p{N}]*(?:['\u2018\u2019]\s*)?[A-Z]{1,4}[\)\]\}]?\s+(?:preparation|pr[e\u00e9]paration|duration|dur[e\u00e9]e|time|temps)\s*[:;]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NoisyMetadataLeadMarkerRegex();

    [GeneratedRegex(@"^[^\p{L}\p{N}]*\p{Lu}{2,4}\s+(?=\p{Lu}[\p{Lu}\p{Mn}'\u2019\-]{4,24}\s+\p{Lu}[\p{Lu}\p{Mn}'\u2019\-]{4,24}\s*(?:[:;\u2022\-]|\p{Lu}\p{Ll}|\p{N}))", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingShortLayoutPrefixBeforeStructuredLabelsRegex();

    [GeneratedRegex(@"^\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|km|nm|bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|pct|percent|pourcent|deg|degrees?|degres?|c|f|units?|unites?|items?|elements?|entries?|parts?|pieces?|pages?|s|sec|secs|secondes?|seconds?|min|mins?|minutes?|h|hr|hrs?|hours?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadingStructuredQuantityContinuationRegex();

    [GeneratedRegex(@"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|km|nm|bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|pct|percent|pourcent|deg|degrees?|degres?|c|f|units?|unites?|items?|elements?|entries?|parts?|pieces?|pages?|s|sec|secs|secondes?|seconds?|min|mins?|minutes?|h|hr|hrs?|hours?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedStructuredQuantityContinuationRegex();

    [GeneratedRegex(@"(?:^|[\s\.;:!\?])\d{1,3}\s+(?=\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedStructuredStepRegex();

    [GeneratedRegex(@"[\.!?\u2026]\s+(?:[\u2022\-]\s*)?[\p{L}'\u2019\s]{2,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CompleteSentenceWithShortTrailingBulletRegex();

    private readonly record struct TokenSpan(int Start, int Length, string Text)
    {
        public int End => Start + Length;
    }
}
