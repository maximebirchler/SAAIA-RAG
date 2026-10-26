using System.Globalization;

/// <summary>Partitions literal source without dropping whitespace or splitting a Unicode text element.</summary>
internal static class TeiLiteralInputPartitioner
{
    internal static (string Left, string Right)? Split(string source)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(source);
        if (boundaries.Length < 2) return null;
        var middle = boundaries.Length / 2;
        var selected = middle;
        // Prefer a word boundary near the middle. The measured halves are checked again;
        // tokenization is never assumed to be monotone in the source character count.
        for (var distance = 0; distance <= boundaries.Length / 4; distance++)
        {
            var before = middle - distance;
            var after = middle + distance;
            if (before > 0 && char.IsWhiteSpace(source[boundaries[before] - 1]))
            { selected = before; break; }
            if (after < boundaries.Length && char.IsWhiteSpace(source[boundaries[after] - 1]))
            { selected = after; break; }
        }
        var split = boundaries[selected];
        return (source[..split], source[split..]);
    }
}
