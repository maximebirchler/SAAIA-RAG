using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static DoclingRegionalTextRepairJson;

/// <summary>Independent verification of the sidecar's finite source-retention contract, not language correction.</summary>
internal static class DoclingRegionalTextRetentionPolicy
{
    private static readonly Regex Words = new(@"\p{L}+", RegexOptions.CultureInvariant);
    private static readonly Regex Tokens = new(@"[\p{L}\p{M}\p{N}]+(?:['’][\p{L}\p{M}\p{N}]+)*|[^\s]", RegexOptions.CultureInvariant);
    private static readonly Regex Hole = new(@"[^\S\r\n]{3,}", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Negations = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "not", "nor", "never", "without", "cannot", "can't", "don't", "ne", "non", "ni", "pas", "sans",
        "aucun", "aucune", "jamais", "nicht", "nie", "nein", "kein", "keine", "keinen", "ohne"
    };

    internal static void Validate(JsonElement proof, string original, string replacement)
    {
        var preservation = Field(proof, "source_preservation");
        if (!Bool(preservation, "reliable_suffix_tokens_in_order") || !Bool(preservation, "critical_prefix_tokens_in_order")
            || !Bool(preservation, "lowercase_two_letter_prefix_words_preserved")
            || Bool(preservation, "universal_multilingual_validation")) throw Invalid();
        EqualString(preservation, "linguistic_prefix_negation_guard", "fr_en_de_finite_v1");
        var damage = Field(proof, "damage");
        if (!Bool(damage, "candidate")) throw Invalid();
        var prefixEnd = DeterminePrefixEnd(original);
        var scalarPrefixEnd = original[..prefixEnd].EnumerateRunes().Count();
        if (Int(damage, "fragmented_prefix_end") != scalarPrefixEnd || Int(damage, "reliable_suffix_start") != scalarPrefixEnd)
            throw Invalid();
        var expected = Tokenize(original[prefixEnd..]);
        var actual = Tokenize(replacement);
        if (expected.Length == 0 || actual.Length < expected.Length
            || !actual.AsSpan(actual.Length - expected.Length).SequenceEqual(expected)) throw Invalid();
        var prefixTokens = Tokenize(original[..prefixEnd]);
        var protectedTokens = prefixTokens.Where((token, index) => IsProtected(token)
            || (token is "n" or "N") && index + 1 < prefixTokens.Length && (prefixTokens[index + 1] is "'" or "’")).ToArray();
        var cursor = 0;
        foreach (var token in actual.Take(actual.Length - expected.Length))
            if (cursor < protectedTokens.Length && token == protectedTokens[cursor]) cursor++;
        if (cursor != protectedTokens.Length) throw Invalid();
    }

    private static int DeterminePrefixEnd(string text)
    {
        if (text.EnumerateRunes().Count() is < 20 or > 8_000) throw Invalid();
        var words = Words.Matches(text).Cast<Match>().ToArray();
        var prefix = new List<Match>();
        foreach (var word in words)
        {
            if (prefix.Count > 0 && prefix.Count(IsLowerSingleWord) >= 3
                && Hole.IsMatch(text[(prefix[^1].Index + prefix[^1].Length)..word.Index])) break;
            if (word.Value.EnumerateRunes().Count() > 2) break;
            prefix.Add(word);
        }
        if (prefix.Count < 4 || prefix.Count(IsLowerSingleWord) < 3 || prefix.Count == words.Length
            || words.Skip(prefix.Count).Count(word => word.Value.EnumerateRunes().Count() >= 3) < 3
            || !Hole.IsMatch(text[..words[prefix.Count].Index])) throw Invalid();
        return prefix[^1].Index + prefix[^1].Length;
    }
    private static bool IsLowerSingleWord(Match match)
        => match.Value.EnumerateRunes().Count() == 1 && match.Value.EnumerateRunes().All(Rune.IsLower);
    private static string[] Tokenize(string text)
        => Tokens.Matches(text.Normalize(NormalizationForm.FormC)).Select(match => match.Value).ToArray();
    private static bool IsProtected(string token)
    {
        var runes = token.EnumerateRunes().ToArray();
        return Negations.Contains(token) || runes.Any(rune => Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber)
            || runes.Length == 2 && runes.All(Rune.IsLetter) && runes.All(Rune.IsLower)
            || runes.Any(rune => !Rune.IsLetter(rune))
            || runes.Any(Rune.IsUpper) && !runes.Any(Rune.IsLower);
    }
}
