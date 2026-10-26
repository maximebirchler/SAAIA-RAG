using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

// These are observed extraction diagnostics, not a judgment that the source or
// its interpretation is correct. Keep them separate from documentary content.
internal sealed record AdvancedAnalysisSourceQuality(
    string? TextStatus,
    bool? OcrCandidate,
    IReadOnlyList<string> Signals)
{
    private const int MaximumSignals = 64;
    public string SemanticAccuracy => "not_verified";
    public bool MetadataMalformed { get; init; }
    public bool MetadataTruncated { get; init; }
    public int SignalsOmittedCount { get; init; }

    internal static AdvancedAnalysisSourceQuality? FromMetadata(
        string? textStatus, string? ocrCandidate, string? signalsJson)
    {
        if (textStatus is null && ocrCandidate is null && signalsJson is null)
            return null;
        var malformed = false;
        bool? ocr = null;
        if (ocrCandidate is not null)
        {
            if (bool.TryParse(ocrCandidate, out var parsed)) ocr = parsed;
            else malformed = true;
        }
        var signals = new List<string>();
        var omitted = 0;
        if (signalsJson is { Length: > 100_000 })
        {
            malformed = true;
        }
        else if (signalsJson is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(signalsJson);
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                    malformed = true;
                else
                    foreach (var item in json.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || !IsCode(item.GetString(), 160))
                            malformed = true;
                        else if (!signals.Contains(item.GetString()!, StringComparer.Ordinal))
                        {
                            if (signals.Count < MaximumSignals) signals.Add(item.GetString()!);
                            else omitted++;
                        }
                    }
            }
            catch (JsonException) { malformed = true; }
        }
        return new AdvancedAnalysisSourceQuality(textStatus, ocr, signals)
        {
            MetadataMalformed = malformed,
            MetadataTruncated = omitted > 0 || signalsJson?.Length > 100_000,
            SignalsOmittedCount = omitted
        }.Bound();
    }

    internal AdvancedAnalysisSourceQuality Bound()
    {
        var malformed = MetadataMalformed || TextStatus is not null && !IsCode(TextStatus, 64);
        var signals = new List<string>();
        var omitted = Math.Max(0, SignalsOmittedCount);
        foreach (var signal in Signals ?? [])
        {
            if (!IsCode(signal, 160)) malformed = true;
            else if (!signals.Contains(signal, StringComparer.Ordinal))
            {
                if (signals.Count < MaximumSignals) signals.Add(signal);
                else omitted++;
            }
        }
        return this with
        {
            TextStatus = TextStatus is null || IsCode(TextStatus, 64) ? TextStatus : "unknown",
            Signals = signals,
            MetadataMalformed = malformed,
            MetadataTruncated = MetadataTruncated || omitted > 0,
            SignalsOmittedCount = omitted
        };
    }

    internal static AdvancedAnalysisSourceQuality? Merge(IEnumerable<AdvancedAnalysisSourceQuality?> qualities)
    {
        var all = qualities.ToArray();
        var observed = all.Where(item => item is not null).Select(item => item!.Bound()).ToArray();
        if (observed.Length == 0) return null;
        var statuses = observed.Select(item => item.TextStatus).Distinct(StringComparer.Ordinal).ToArray();
        var allObserved = observed.Length == all.Length;
        return new AdvancedAnalysisSourceQuality(
            allObserved && statuses.Length == 1 ? statuses[0] : "mixed",
            observed.Any(item => item.OcrCandidate == true) ? true
                : allObserved && observed.All(item => item.OcrCandidate == false) ? false : null,
            observed.SelectMany(item => item.Signals).Distinct(StringComparer.Ordinal).ToArray())
        {
            MetadataMalformed = observed.Any(item => item.MetadataMalformed),
            MetadataTruncated = observed.Any(item => item.MetadataTruncated),
            SignalsOmittedCount = observed.Sum(item => item.SignalsOmittedCount)
        }.Bound();
    }

    private static bool IsCode(string? value, int maximum)
        => value is { Length: > 0 } && value.Length <= maximum
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':');
}
