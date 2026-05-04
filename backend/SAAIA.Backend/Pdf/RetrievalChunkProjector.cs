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
                chunkType: "legacy_word_window_v1"));
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

        var chunks = new List<ProjectedRetrievalChunk>();
        var chunkIndex = 0;

        foreach (var sectionGroup in orderedUnits.GroupBy(unit => unit.SectionOrdinal))
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

                    if (window.Count > 0 && LooksLikeHighSignalUnit(candidate))
                    {
                        stoppedBeforeStructuredBoundary = true;
                        break;
                    }

                    if (window.Count > 0 && tokenTotal + candidateTokens > maxWords)
                        break;

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
                    int? representativeUnit = window.Count == 1
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
                        chunkType: window.Count == 1 ? "unit_exact_v1" : "section_window_v1"));
                }

                if (cursor >= sectionUnits.Count)
                    break;

                start = ComputeNextStart(sectionUnits, start, cursor, overlapWords);
            }
        }

        AddHighSignalUnitChunks(chunks, orderedUnits, ref chunkIndex);

        if (chunks.Count == 0)
        {
            var fallback = orderedUnits;
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
                chunkType: "document_window_v1"));
        }

        return chunks;
    }

    private static void AddHighSignalUnitChunks(
        List<ProjectedRetrievalChunk> chunks,
        IReadOnlyList<ExtractedDocumentUnit> orderedUnits,
        ref int chunkIndex)
    {
        var existingExactUnitOrdinals = chunks
            .Where(static chunk => string.Equals(chunk.ChunkType, "unit_exact_v1", StringComparison.Ordinal))
            .Select(static chunk => chunk.UnitOrdinal)
            .Where(static ordinal => ordinal.HasValue)
            .Select(static ordinal => ordinal!.Value)
            .ToHashSet();

        foreach (var unit in orderedUnits)
        {
            if (existingExactUnitOrdinals.Contains(unit.Ordinal))
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
                chunkType: "unit_exact_v1"));

            existingExactUnitOrdinals.Add(unit.Ordinal);
        }
    }

    private static bool WindowContainsHighSignalUnit(IReadOnlyList<ExtractedDocumentUnit> window)
        => window.Any(LooksLikeHighSignalUnit);

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

        var signalCount = 0;
        if (ContainsAny(normalized,
            "ingredient", "ingredients", "ingredienti", "zutaten", "materials", "materiaux"))
            signalCount++;
        if (ContainsAny(normalized,
            "preparation", "realisation", "method", "procedure", "procedures", "steps", "etapes"))
            signalCount++;
        if (ContainsAny(normalized,
            "requirements", "requirement", "warning", "caution", "attention", "consigne", "instructions"))
            signalCount++;

        return signalCount >= 2
            || (signalCount >= 1 && (ServingOrStepMarkerRegex().IsMatch(normalized) || CountNumberedSteps(normalized) >= 2));
    }

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
        => StructuralBoundaryRegex().Replace(text, " ");

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
        string chunkType)
    {
        var normalizedText = NormalizeRetrievalText(text);
        var prefixedText = PrefixDetectedEmbeddedTitle(normalizedText);

        return new(
            ChunkIndex: chunkIndex,
            SectionOrdinal: sectionOrdinal,
            UnitOrdinal: unitOrdinal,
            PageStart: pageStart,
            PageEnd: pageEnd,
            Text: prefixedText,
            TokenCount: CountTokens(prefixedText),
            Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(prefixedText)),
            ChunkType: chunkType,
            OffsetStart: offsetStart,
            OffsetEnd: offsetEnd);
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

        normalized = UppercaseRunToTitleCaseBoundaryRegex().Replace(normalized, " ");
        normalized = LowerToKnownLabelBoundaryRegex().Replace(normalized, " ");
        normalized = AdjacentKnownLabelsBoundaryRegex().Replace(normalized, "${first} ${second}");
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

        return !normalized.Contains("table des matieres", StringComparison.Ordinal)
            && !normalized.Contains("table of contents", StringComparison.Ordinal);
    }

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
        "ingredients",
        "ingredient",
        "preparation",
        "preparations",
        "etapes",
        "steps",
        "method",
        "methods",
        "temps total",
        "total time",
        "sauces",
        "document",
        "page",
        "pages"
    };

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])(?:pour|for|para|per)\s+\d+|(?:^|[^\p{L}\p{N}])\d+\s*[\.)]\s+\p{L}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ServingOrStepMarkerRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])\d+\s*[\.)]\s+\p{L}", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedStepRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}\p{Nd}])(?=(?:Pour|For|Para|Per|Ingredients?|Ingrédients?|Zutaten|Preparation|Préparation|Realisation|Réalisation|Etapes?|Étapes?)\b)", RegexOptions.CultureInvariant)]
    private static partial Regex StructuralBoundaryRegex();

    [GeneratedRegex(@"^\s*\d{1,6}(?=(?:Temps|Total|Ingredients?|Ingr[eÃ©]dients?|Preparation|Pr[eÃ©]paration|\p{Lu}(?:[\p{Ll}]{2,}|['\u2019]\p{Lu}{2,}|\p{Lu}{2,})))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadingCompactPageNumberRegex();

    [GeneratedRegex(@"(?<=\d)(?=(?:Temps|Total|Ingredients?|Ingr[eÃ©]dients?|Preparation|Pr[eÃ©]paration|\p{Lu}(?:[\p{Ll}]{2,}|['\u2019]\p{Lu}{2,}|\p{Lu}{2,})))", RegexOptions.CultureInvariant)]
    private static partial Regex DigitToStructuralLeadRegex();

    [GeneratedRegex(@"(?<=[\p{L}])(?=\d+(?:[,.]\d+)?(?:\s*(?:g|kg|mg|ml|cl|l|oz|lb|c\.|cuill|personnes?|people|servings?|portions?|brins?|cubes?|gousses?|tranches?|morceaux?|feuilles?|sachets?|pinc[e\u00e9]es?|carottes?|oignons?|\u00e9chalotes?|echalotes?|branches?|lamelles?|escalopes?|capsules?|gla[c\u00e7]ons?|bouquets?|piments?|fruits?|l[e\u00e9]gumes?|jaunes?|blancs?|oeufs?|\u0153ufs?|eggs?|cloves?|slices?|pieces?|leaves?|cups?|tbsp|tsp|s|sec|secs|secondes?|seconds?|min|h)(?:\b|\s)|[\.)]\s*\p{L}))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LetterToNumericMeasureRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}])(?=\d{1,4}\s+(?:brins?|cubes?|gousses?|tranches?|morceaux?|feuilles?|sachets?|pinc[e\u00e9]es?|carottes?|oignons?|\u00e9chalotes?|echalotes?|branches?|lamelles?|escalopes?|capsules?|gla[c\u00e7]ons?|bouquets?|piments?|fruits?|l[e\u00e9]gumes?|jaunes?|blancs?|oeufs?|\u0153ufs?|eggs?|cloves?|slices?|pieces?|leaves?)(?:\s|[,\.;:\)\]]|$))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LowerToCountNounBoundaryRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}])(?=\d{1,4}(?:[,.]\d+)?\s+\p{Ll})", RegexOptions.CultureInvariant)]
    private static partial Regex LowerToLooseQuantityBoundaryRegex();

    [GeneratedRegex(@"(?<=[\.!?:;\)\]\u00ae])(?=\d{1,4}(?:[,.]\d+)?\s+\p{Ll})", RegexOptions.CultureInvariant)]
    private static partial Regex PunctuationToLooseQuantityBoundaryRegex();

    [GeneratedRegex(@"\b(?<word>[\p{L}]+['\u2019][\p{L}]{2,20})(?=\d{1,4}\s+\p{Ll})", RegexOptions.CultureInvariant)]
    private static partial Regex ApostropheWordToNumericWordBoundaryRegex();

    [GeneratedRegex(@"(?<=[\p{L}])(?=\d+\s+\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex LetterToBareNumberedStepRegex();

    [GeneratedRegex(@"(?<=[\.!?])(?=\d+\s+\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex PunctuationToBareNumberedStepRegex();

    [GeneratedRegex(@"(?<=[\.!?:;\)\]\u00ae])(?=\d+(?:[,.]\d+)?(?:/\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|c\.|cuill|cups?|tbsp|tsp|personnes?|people|servings?|portions?|s|sec|secs|secondes?|seconds?|min|h)(?:\b|(?=\d)))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PunctuationToNumericMeasureRegex();

    [GeneratedRegex(@"\b(?<unit>personnes?|people|servings?|portions?|s|sec|secs|secondes?|seconds?|min|h)(?=\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UnitToDigitBoundaryRegex();

    [GeneratedRegex(@"\b(?:ingredients?|ingr[e\u00e9]dients?|zutaten|preparation|pr[e\u00e9]paration|\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|c\.|cuill|cups?|tbsp|tsp|personnes?|people|servings?|portions?|s|sec|secs|secondes?|seconds?|min|h))\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QuantityDenseMarkerRegex();

    [GeneratedRegex(@"(?<=[\p{Lu}])(?=\p{Lu}\p{Ll}{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex UppercaseRunToTitleCaseBoundaryRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}])(?=(?:Sel|Poivre|Salt|Pepper))", RegexOptions.CultureInvariant)]
    private static partial Regex LowerToKnownLabelBoundaryRegex();

    [GeneratedRegex(@"\b(?<first>Sel|Salt)(?<second>Poivre|Pepper)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AdjacentKnownLabelsBoundaryRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}\p{Nd}])(?=(?:Temps|Total|Pour|For|Para|Per|With|Avec|Ingredients?|Ingr[eÃ©]dients?|Zutaten|Preparation|Pr[eÃ©]paration|Method|Steps?|Etapes?|[A-Z]{2,}\b))", RegexOptions.CultureInvariant)]
    private static partial Regex LowerOrDigitToStructuralLeadRegex();

    [GeneratedRegex(@"[ \t\f\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"(?:\s*\r?\n\s*){2,}", RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphWhitespaceRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<title>[\p{Lu}][\p{Lu}\p{Nd}'\u2019\-\s]{4,90}?)(?=(?:\s+\d{1,4}\s*(?:g|kg|mg|ml|cl|l|oz|lb|c\.|cuill|personnes?|people|servings?|portions?|min|h)\b|\s+[A-Z][\p{Ll}]{2,}|\s*$|[\.:\-\u2013\u2014]))", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedUppercaseTitleRegex();

    [GeneratedRegex(@"(?<=[\p{Lu}])\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex MostlyUppercaseTrailingMeasureNumberRegex();

    [GeneratedRegex(@"\b(?:ingredients?|ingr[eÃ©]dients?|zutaten|preparation|pr[eÃ©]paration|method|steps?|etapes?|temps total|total time|\d+\s*(?:personnes?|people|servings?|portions?|min|h))\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredContextBeforeTitleRegex();

    [GeneratedRegex(@"\b(?:ingredients?|ingr[eÃ©]dients?|zutaten|preparation|pr[eÃ©]paration|method|steps?|\d+\s*(?:g|kg|mg|ml|cl|l|oz|lb|c\.|cuill))\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredContextAfterTitleRegex();
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
    int? OffsetEnd = null);
