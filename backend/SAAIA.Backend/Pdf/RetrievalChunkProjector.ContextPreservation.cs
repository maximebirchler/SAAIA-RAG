using System.Text.RegularExpressions;

internal static partial class RetrievalChunkProjector
{
    private static List<ExtractedDocumentUnit> KeepSubstantiveWindowContext(
        IReadOnlyList<ExtractedDocumentUnit> reliableUnits, int minWords)
    {
        var bodyPages = reliableUnits.Where(HasSubstantiveWindowBody)
            .Select(unit => (unit.SectionOrdinal, unit.PageStart, unit.PageEnd)).ToHashSet();
        return reliableUnits.Where((unit, index) => !LooksLikeLowSubstanceStandaloneUnit(unit, minWords)
            || LooksLikeSingleWordPageHeading(reliableUnits, index)
            || bodyPages.Contains((unit.SectionOrdinal, unit.PageStart, unit.PageEnd))
                && HasMeaningfulShortSourceText(unit)).ToList();
    }

    // Window membership and standalone retrieval usefulness are different.
    // Short restrictions, instructions and named values remain verbatim beside
    // their body; they do not become standalone candidates or invented headings.
    private static bool HasMeaningfulShortSourceText(ExtractedDocumentUnit unit)
    {
        if (CountStructuredQuantityUnitEvidence(unit.Text) > 0)
            return HasNamedMeasurementSubject(unit.Text);
        return ContainsSourceContentWord(unit.Text);
    }

    private static bool HasNamedMeasurementSubject(string text)
        => ContainsSourceContentWord(StructuredQuantityUnitEvidenceRegex().Replace(text, " "));

    private static bool ContainsSourceContentWord(string text)
        => SourceContextWordRegex().Matches(text).Any(word =>
            !ShortTitleConnectorTokens.Contains(FoldDiacritics(word.Value).ToLowerInvariant()));

    private static bool HasSubstantiveWindowBody(ExtractedDocumentUnit unit)
        => unit.TokenCount >= 8 && SubstantiveWordRegex().Matches(unit.Text).Count >= 3
            && !LooksLikeShortStandaloneTitleBoundaryUnit(unit);

    private static bool WindowHasMeaningfulShortContext(IReadOnlyList<ExtractedDocumentUnit> window)
        => window.Any(HasSubstantiveWindowBody)
            && window.Any(unit => unit.TokenCount < 8 && HasMeaningfulShortSourceText(unit));

    private static bool CanAttachShortContextLead(IReadOnlyList<ExtractedDocumentUnit> lead,
        IReadOnlyList<ExtractedDocumentUnit> units, int index, int combinedTokens, int maximumTokens)
    {
        var body = units[index];
        var hasBody = HasSubstantiveWindowBody(body)
            || body.TokenCount < 8 && HasMeaningfulShortSourceText(body)
                && !LooksLikeWindowTitleBoundary(units, index)
                && HasFollowingSubstantiveWindowBody(units, index);
        return combinedTokens <= maximumTokens && hasBody
            && lead.All(unit => unit.SectionOrdinal == body.SectionOrdinal
                && unit.PageStart == body.PageStart && unit.PageEnd == body.PageEnd
                && unit.TokenCount < 8
                && (HasMeaningfulShortSourceText(unit)
                    || LooksLikeShortStandaloneTitleBoundaryUnit(unit)
                    || HasCleanSingleWordTitleShape(unit)));
    }

    private static bool HasFollowingSubstantiveWindowBody(IReadOnlyList<ExtractedDocumentUnit> units, int index)
    {
        var source = units[index];
        for (var next = index + 1; next < units.Count; next++)
        {
            var unit = units[next];
            if (unit.SectionOrdinal != source.SectionOrdinal || unit.PageStart != source.PageStart
                || unit.PageEnd != source.PageEnd)
                return false;
            if (HasSubstantiveWindowBody(unit))
                return true;
            if (!HasMeaningfulShortSourceText(unit))
                return false;
        }
        return false;
    }

    private static bool LooksLikeWindowTitleBoundary(
        IReadOnlyList<ExtractedDocumentUnit> units, int index)
        => LooksLikeShortStandaloneTitleBoundaryUnit(units[index])
            || LooksLikeSingleWordPageHeading(units, index);

    private static bool LooksLikeSingleWordPageHeading(
        IReadOnlyList<ExtractedDocumentUnit> units, int index)
    {
        var unit = units[index];
        var title = unit.Text.Trim();
        if (!HasCleanSingleWordTitleShape(unit))
            return false;

        var declaredHeading = unit.HeadingLevel is > 0
            && (string.Equals(unit.SectionTitle, title, StringComparison.Ordinal)
                || unit.HeadingPath?.EndsWith(title, StringComparison.Ordinal) == true);
        var startsPhysicalPage = index == 0 || units[index - 1].PageEnd < unit.PageStart;
        if (!declaredHeading && !startsPhysicalPage)
            return false;

        // Position and a real following body establish a boundary, not semantic
        // approval of a named candidate. No heading metadata is manufactured.
        for (var next = index + 1; next < units.Count; next++)
        {
            var body = units[next];
            if (body.SectionOrdinal != unit.SectionOrdinal || body.PageStart != unit.PageStart
                || body.PageEnd != unit.PageEnd)
                return false;
            if (HasSubstantiveWindowBody(body))
                return true;
            if (!HasMeaningfulShortSourceText(body))
                return false;
        }
        return false;
    }

    private static bool HasCleanSingleWordTitleShape(ExtractedDocumentUnit unit)
    {
        var title = unit.Text.Trim();
        return unit.TokenCount == 1 && title.Length is >= 4 and <= 120 && char.IsUpper(title[0])
            && title.All(ch => char.IsLetter(ch) || ch is '-' or '\u2019' or '\'')
            && !ShortTitleConnectorTokens.Contains(FoldDiacritics(title).ToLowerInvariant());
    }

    [GeneratedRegex(@"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|km|nm|bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|pct|percent|pourcent|deg|degrees?|degres?|c|f|units?|unites?|items?|elements?|entries?|parts?|pieces?|pages?|s|sec|secs|secondes?|seconds?|min|mins?|minutes?|h|hr|hrs?|hours?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StructuredQuantityUnitEvidenceRegex();

    [GeneratedRegex(@"\p{L}[\p{L}\p{M}'\u2019\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex SourceContextWordRegex();
}
