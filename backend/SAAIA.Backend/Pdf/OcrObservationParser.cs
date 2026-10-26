using System.Globalization;

internal static class OcrObservationParser
{
    internal static PdfOcrPageObservation Parse(string rawText, string? tsv, string engine, string? language)
    {
        var words = new List<PdfOcrObservedWord>();
        int? imageWidth = null;
        int? imageHeight = null;
        foreach (var line in (tsv ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length < 12)
                continue;
            if (parts[0] == "1" && int.TryParse(parts[8], out var width) && int.TryParse(parts[9], out var height)
                && width > 0 && height > 0)
            {
                imageWidth = width;
                imageHeight = height;
            }
            if (parts[0] != "5")
                continue;
            var parsedConfidence = double.TryParse(parts[10], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var confidence) && double.IsFinite(confidence);
            var text = string.Join('\t', parts.Skip(11));
            if (string.IsNullOrWhiteSpace(text))
                continue;
            int.TryParse(parts[6], out var left);
            int.TryParse(parts[7], out var top);
            int.TryParse(parts[8], out var wordWidth);
            int.TryParse(parts[9], out var wordHeight);
            words.Add(new(text, parsedConfidence && confidence is >= 0 and <= 100 ? confidence / 100 : null,
                left, top, wordWidth, wordHeight, parts[2], parts[3], parts[4]));
        }
        return new(rawText, engine, language, words, imageWidth, imageHeight, tsv);
    }
}
