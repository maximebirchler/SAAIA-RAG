using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class ExactMatchEntryExtractor
{
    public static IReadOnlyList<ExtractedExactMatchEntry> Extract(IReadOnlyList<ExtractedDocumentUnit> units)
    {
        if (units.Count == 0)
            return Array.Empty<ExtractedExactMatchEntry>();

        var entries = new List<ExtractedExactMatchEntry>();
        var entryIndex = 0;

        foreach (var unit in units.OrderBy(u => u.Ordinal))
        {
            var normalizedUnit = NormalizeWhitespace(unit.Text);
            if (string.IsNullOrWhiteSpace(normalizedUnit))
                continue;

            var candidates = SplitCandidates(normalizedUnit)
                .Concat(ExtractTargetedReferences(normalizedUnit))
                .Select(NormalizeWhitespace)
                .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
                candidates.Add(normalizedUnit);

            foreach (var candidate in candidates)
            {
                var tokenCount = CountTokens(candidate);
                if (tokenCount <= 0)
                    continue;

                var normalizedText = NormalizeForLookup(candidate);
                if (string.IsNullOrWhiteSpace(normalizedText))
                    continue;

                var kind = InferEntryKind(candidate);

                entries.Add(new ExtractedExactMatchEntry(
                    EntryIndex: entryIndex++,
                    SectionOrdinal: unit.SectionOrdinal,
                    UnitOrdinal: unit.Ordinal,
                    PageStart: unit.PageStart,
                    PageEnd: unit.PageEnd,
                    Text: candidate,
                    NormalizedText: normalizedText,
                    CharCount: candidate.Length,
                    TokenCount: tokenCount,
                    Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText)),
                    Kind: kind));
            }
        }

        return entries;
    }

    internal static IReadOnlyList<string> ExtractLookupTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var terms = new List<string>();
        var normalizedWhole = NormalizeForLookup(text);
        if (!string.IsNullOrWhiteSpace(normalizedWhole))
            terms.Add(normalizedWhole);

        foreach (var reference in ExtractTargetedReferences(text))
        {
            var normalizedReference = NormalizeForLookup(reference);
            if (!string.IsNullOrWhiteSpace(normalizedReference))
                terms.Add(normalizedReference);
        }

        return terms
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(term => term.Length)
            .ToArray();
    }

    private static IEnumerable<string> SplitCandidates(string text)
    {
        var sentences = SentenceSplitRegex()
            .Split(text)
            .Select(NormalizeWhitespace)
            .Where(static sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();

        if (sentences.Count <= 1)
            return [text];

        var goodSentences = sentences
            .Where(static sentence => sentence.Length is >= 40 and <= 400)
            .Where(static sentence => CountTokens(sentence) >= 5)
            .ToList();

        return goodSentences.Count > 0 ? goodSentences : [text];
    }

    private static string NormalizeWhitespace(string text)
        => Regex.Replace(text, @"\s+", " ").Trim();

    internal static string NormalizeForLookup(string text)
    {
        var compact = NormalizeWhitespace(text).ToLowerInvariant();
        compact = ExactPunctuationRegex().Replace(compact, " ");
        return Regex.Replace(compact, @"\s+", " ").Trim();
    }

    internal static IEnumerable<string> ExtractTargetedReferences(string text)
    {
        foreach (Match match in StandardReferenceRegex().Matches(text))
        {
            var value = NormalizeWhitespace(match.Value);
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }

        foreach (Match match in CodeReferenceRegex().Matches(text))
        {
            var value = NormalizeWhitespace(match.Value);
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private static string InferEntryKind(string candidate)
    {
        if (StandardReferenceRegex().IsMatch(candidate))
            return "standard_ref";

        if (CodeReferenceRegex().IsMatch(candidate))
            return "code_ref";

        return "verbatim_excerpt";
    }

    private static int CountTokens(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [GeneratedRegex(@"(?<=[\.\!\?\;\:])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceSplitRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}\s]", RegexOptions.CultureInvariant)]
    private static partial Regex ExactPunctuationRegex();

    [GeneratedRegex(@"\b(?:EN|ISO|IEC|ASTM|DIN|NFPA|API|ANSI|CEN|TR)\s*(?:[A-Z]{1,4}\s*)?\d[\w\-\/\.:]*\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StandardReferenceRegex();

    [GeneratedRegex(@"\b(?=[A-Z0-9._/\-]{4,40}\b)(?=[A-Z0-9._/\-]*[A-Z])(?=[A-Z0-9._/\-]*\d)[A-Z0-9][A-Z0-9._/\-]{2,39}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CodeReferenceRegex();
}

internal sealed record ExtractedExactMatchEntry(
    int EntryIndex,
    int? SectionOrdinal,
    int UnitOrdinal,
    int PageStart,
    int PageEnd,
    string Text,
    string NormalizedText,
    int CharCount,
    int TokenCount,
    byte[] Checksum,
    string Kind);
