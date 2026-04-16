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

            var references = ExtractTargetedReferences(normalizedUnit).ToList();
            var referenceVariants = references
                .SelectMany(ExpandReferenceVariants)
                .Select(NormalizeWhitespace)
                .Where(static v => !string.IsNullOrWhiteSpace(v));

            var candidates = SplitCandidates(normalizedUnit)
                .Concat(references)
                .Concat(referenceVariants)
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

            // Generate sub-variants for composite references:
            // "EN 15281" → also produce "15281"
            // "CEN TR 15281" → also produce "tr 15281", "15281"
            foreach (var variant in ExpandReferenceVariants(reference))
            {
                var normalizedVariant = NormalizeForLookup(variant);
                if (!string.IsNullOrWhiteSpace(normalizedVariant))
                    terms.Add(normalizedVariant);
            }
        }

        return terms
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(term => term.Length)
            .ToArray();
    }

    internal static IReadOnlyList<string> ExtractReferenceKeys(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in ExtractTargetedReferences(text))
        {
            foreach (Match match in ReferenceKeyRegex().Matches(reference))
            {
                var key = NormalizeWhitespace(match.Value).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(key))
                    keys.Add(key);
            }
        }

        return keys
            .OrderByDescending(static key => key.Length)
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

    /// <summary>
    /// For a composite reference like "CEN TR 15281" or "EN 15281", produce sub-variants:
    /// - Strip known prefixes progressively (EN/ISO/IEC/CEN/TR/etc.)
    /// - Extract bare numeric core (e.g., "15281")
    /// This allows cross-matching between "EN 15281" (query) and "CEN TR 15281" (doc).
    /// </summary>
    internal static IEnumerable<string> ExpandReferenceVariants(string reference)
    {
        var normalized = NormalizeWhitespace(reference);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
            yield break;

        // Progressively strip leading prefix tokens
        // "CEN TR 15281" → "TR 15281" → "15281"
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "EN", "ISO", "IEC", "ASTM", "DIN", "NFPA", "API", "ANSI", "CEN", "TR", "TS", "PD", "BS" };

        for (var i = 1; i < parts.Length; i++)
        {
            if (!prefixes.Contains(parts[i - 1]))
                break;
            var sub = string.Join(" ", parts[i..]);
            if (!string.IsNullOrWhiteSpace(sub))
                yield return sub;
        }

        // Also extract bare numeric core if present
        foreach (var part in parts)
        {
            if (part.Length >= 4 && part.All(char.IsDigit))
            {
                yield return part;
                break; // Only first numeric core
            }
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

    [GeneratedRegex(@"\b\d{4,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceKeyRegex();
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
