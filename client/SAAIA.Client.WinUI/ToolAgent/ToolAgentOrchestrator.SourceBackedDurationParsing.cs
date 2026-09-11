using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static int? TryExtractRequestedMaxMinutes(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var match = Regex.Match(
            normalized,
            @"\b(?:moins\s+de|en\s+moins\s+de|sous|maximum|max|under|less\s+than|within)\s+(?<n>\d{1,3})\s*(?:min|minutes?)\b",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(
                normalized,
                @"\b(?:moins|maximum|max|sous|under|less|within)\b.{0,32}\b(?<n>\d{1,3})\s*(?:min|minutes?)\b",
                RegexOptions.CultureInvariant);
        }
        if (match.Success)
        {
            return int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                ? minutes
                : null;
        }

        var hourMatch = Regex.Match(
            normalized,
            @"\b(?:moins\s+de|en\s+moins\s+de|sous|maximum|max|under|less\s+than|within)\s+(?:(?<h>\d{1,2})|(?<one>un|une|one|a|an))\s*(?:h\b|hr\b|hrs\b|heure|heures|hour|hours)\b",
            RegexOptions.CultureInvariant);
        if (!hourMatch.Success)
        {
            hourMatch = Regex.Match(
                normalized,
                @"\b(?:moins|maximum|max|sous|under|less|within)\b.{0,32}\b(?:(?<h>\d{1,2})|(?<one>un|une|one|a|an))\s*(?:h\b|hr\b|hrs\b|heure|heures|hour|hours)\b",
                RegexOptions.CultureInvariant);
        }
        if (!hourMatch.Success)
            return null;

        var hours = hourMatch.Groups["one"].Success
            ? 1
            : int.TryParse(hourMatch.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHours)
                ? parsedHours
                : 0;

        return hours is > 0 and <= 24
            ? hours * 60
            : null;
    }

    private static int? ExtractBestVisibleDurationMinutes(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        var primaryDuration = ExtractBestVisibleDurationMinutesFromText(text);
        if (primaryDuration.HasValue)
            return primaryDuration;

        var lookupText = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        return string.Equals(lookupText, text, StringComparison.Ordinal)
            ? null
            : ExtractBestVisibleDurationMinutesFromText(lookupText);
    }

    private static int? ExtractBestVisibleDurationMinutesFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var labeledTotal = ExtractLabeledVisibleDurationTotalMinutes(text);
        if (labeledTotal.HasValue)
            return labeledTotal;

        var values = new List<int>();
        foreach (Match match in Regex.Matches(NormalizeDurationScanText(text), @"\b(?<h>\d{1,2})\s*(?:h\b|heures?\b)(?:\s*(?<m>\d{1,2})\s*min(?:utes?)?)?", RegexOptions.CultureInvariant))
        {
            var hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            values.Add(hours * 60 + minutes);
        }

        foreach (Match match in Regex.Matches(NormalizeDurationScanText(text), @"\b(?<m>\d{1,3})\s*min(?:utes?)?(?=\b|(?:preparation|prep|procedure|process|execution|repos|pause|wait|rest|total|temps|duree)\b)", RegexOptions.CultureInvariant))
            values.Add(int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture));

        return values
            .Where(value => value is > 0 and <= 1440)
            .OrderByDescending(static value => value)
            .Cast<int?>()
            .FirstOrDefault();
    }

    private static int? ExtractLabeledVisibleDurationTotalMinutes(string normalizedText)
    {
        var text = NormalizeDurationScanText(normalizedText);
        var durations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            text,
            @"\b(?<label>temps\s+total|duree\s+totale|total|preparation|prep|procedure|process|execution|operation|operations|repos|pause|wait|rest)\s*[:=\-]?\s*(?<duration>\d{1,2}\s*(?:h\b|heures?\b)(?:\s*\d{1,2}\s*min(?:utes?)?)?|\d{1,3}\s*min(?:utes?)?)",
            RegexOptions.CultureInvariant))
        {
            var label = NormalizeDurationLabel(match.Groups["label"].Value);
            var minutes = ParseVisibleDurationMinutes(match.Groups["duration"].Value);
            if (!minutes.HasValue)
                continue;

            if (label == "total")
                return minutes.Value;

            durations.TryAdd(label, minutes.Value);
        }

        foreach (Match match in Regex.Matches(
            text,
            @"\b(?<label>[\p{L}][\p{L}\-]{2,30})\s*[:=\-]\s*(?<duration>\d{1,2}\s*(?:h\b|heures?\b)(?:\s*\d{1,2}\s*min(?:utes?)?)?|\d{1,3}\s*min(?:utes?)?)",
            RegexOptions.CultureInvariant))
        {
            var label = NormalizeDurationLabel(match.Groups["label"].Value);
            if (LooksLikeDurationLabelNoise(label))
                continue;

            var minutes = ParseVisibleDurationMinutes(match.Groups["duration"].Value);
            if (minutes.HasValue)
                durations.TryAdd(label, minutes.Value);
        }

        return durations.Count >= 2
            ? durations.Values.Sum()
            : durations.Values.Cast<int?>().FirstOrDefault();
    }

    private static string NormalizeDurationScanText(string text)
    {
        return NormalizeStructuredScanText(text);
    }

    private static string NormalizeStructuredScanText(string? text)
    {
        var normalized = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        normalized = Regex.Replace(normalized, @"(?<=\d)(?=\p{L})", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?<=\p{L})(?=\d)", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?<=\p{Ll})(?=\p{Lu})", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"(?<=[\p{L}])(?=(?:preparation|prep|procedure|process|execution|repos|pause|wait|rest|total|temps|duree)\b)",
            " ",
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(normalized);
    }

    private static string NormalizeDurationLabel(string label)
    {
        var normalized = NormalizeLexicalLookup(label);
        if (Regex.IsMatch(normalized, @"\b(?:temps\s+total|duree\s+totale|total)\b", RegexOptions.CultureInvariant))
            return "total";
        if (Regex.IsMatch(normalized, @"\b(?:preparation|prep)\b", RegexOptions.CultureInvariant))
            return "prep";
        if (Regex.IsMatch(normalized, @"\b(?:procedure|process|execution|operation|operations)\b", RegexOptions.CultureInvariant))
            return "process";
        if (Regex.IsMatch(normalized, @"\b(?:repos|pause|wait|rest)\b", RegexOptions.CultureInvariant))
            return "rest";
        return normalized;
    }

    private static bool LooksLikeDurationLabelNoise(string label)
        => string.IsNullOrWhiteSpace(label)
            || label.Length < 3
            || Regex.IsMatch(label, @"\b(?:min|mins|minute|minutes|heure|heures|hour|hours|page|pages|source|sources|document|documents|pdf)\b", RegexOptions.CultureInvariant);

    private static int? ParseVisibleDurationMinutes(string value)
    {
        var text = NormalizeLexicalLookup(value);
        var hourMatch = Regex.Match(
            text,
            @"\b(?<h>\d{1,2})\s*(?:h\b|heures?\b)(?:\s*(?<m>\d{1,2})\s*min(?:utes?)?)?",
            RegexOptions.CultureInvariant);
        if (hourMatch.Success)
        {
            var hours = int.Parse(hourMatch.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = hourMatch.Groups["m"].Success
                ? int.Parse(hourMatch.Groups["m"].Value, CultureInfo.InvariantCulture)
                : 0;
            var total = hours * 60 + minutes;
            return total is > 0 and <= 1440 ? total : null;
        }

        var minuteMatch = Regex.Match(
            text,
            @"\b(?<m>\d{1,3})\s*min(?:utes?)?",
            RegexOptions.CultureInvariant);
        if (!minuteMatch.Success)
            return null;

        var minuteTotal = int.Parse(minuteMatch.Groups["m"].Value, CultureInfo.InvariantCulture);
        return minuteTotal is > 0 and <= 1440 ? minuteTotal : null;
    }
}
