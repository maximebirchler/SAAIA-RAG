using System.Text.Json;
using SAAIA.Backend.Models;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalResultProjector
{
    private const int MaximumApiQualitySignals = 64;
    private const int MaximumQualitySignalsJsonCharacters = 100_000;

    internal static RagQualitySignalsObservation ObserveExtractionQualitySignalsJson(string? json)
    {
        if (json is null)
            return new([], new("unavailable"));
        if (json.Length > MaximumQualitySignalsJsonCharacters)
            return new([], new("malformed", Malformed: true));
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Null)
                return new([], new("unavailable"));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new([], new("malformed", Malformed: true));
            var values = new List<string>();
            var malformed = false;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String
                    || NormalizeExtractionQualityToken(item.GetString()) is not { } signal)
                    malformed = true;
                else
                    values.Add(signal);
            }
            var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
            var omitted = Math.Max(0, distinct.Length - MaximumApiQualitySignals);
            return new(distinct.Take(MaximumApiQualitySignals).ToArray(), malformed
                ? new("malformed", Truncated: omitted > 0 ? true : null, Malformed: true)
                : new(omitted > 0 ? "partial" : "complete", omitted, omitted > 0, false));
        }
        catch (JsonException)
        {
            return new([], new("malformed", Malformed: true));
        }
    }

    private static RagQualitySignalsObservation BoundQualitySignals(
        IReadOnlyList<string>? values, RagQualitySignalsMetadataDto? metadata)
    {
        var observed = ObserveExtractionQualitySignalsJson(JsonSerializer.Serialize(values ?? []));
        var valid = metadata is null ? new RagQualitySignalsMetadataDto("unavailable") : metadata;
        if (valid.Status is not ("complete" or "partial" or "malformed" or "unavailable")
            || valid.OmittedCount is < 0
            || valid.Status == "complete" && (valid.OmittedCount != 0 || valid.Truncated != false || valid.Malformed != false)
            || valid.Status == "partial" && valid.Truncated != true
            || valid.Status == "unavailable" && (valid.OmittedCount is not null || valid.Truncated is not null || valid.Malformed is not null))
            valid = new("malformed", Malformed: true);
        if (observed.Metadata.Malformed == true || valid.Malformed == true || valid.Status == "malformed")
            return observed with { Metadata = new("malformed",
                Truncated: observed.Metadata.Truncated == true || valid.Truncated == true ? true : null, Malformed: true) };
        if (observed.Metadata.Truncated == true)
            return observed with { Metadata = new("partial",
                valid.Status == "complete" ? observed.Metadata.OmittedCount : null, true,
                valid.Malformed == false ? false : null) };
        return observed with { Metadata = valid };
    }

    private static RagQualitySignalsObservation MatchQualitySignals(RagMatch match)
    {
        if (match.ExtractionQualityObservation is not { } observation || observation.Metadata.Status == "unavailable")
            return BoundQualitySignals(match.ExtractionQualitySignals, null);
        var legacy = BoundQualitySignals(match.ExtractionQualitySignals, null);
        if (!legacy.Signals.SequenceEqual(observation.Signals, StringComparer.Ordinal))
            return legacy with { Metadata = new("malformed", Malformed: true) };
        return BoundQualitySignals(observation.Signals, observation.Metadata);
    }

    private static RagQualitySignalsObservation MergeQualitySignals(params RagQualitySignalsObservation[] observations)
    {
        if (observations.Length == 0)
            return new([], new("unavailable"));
        var all = observations.Select(item => BoundQualitySignals(item.Signals, item.Metadata)).ToArray();
        var values = all.SelectMany(item => item.Signals).Distinct(StringComparer.Ordinal).ToArray();
        var locallyOmitted = Math.Max(0, values.Length - MaximumApiQualitySignals);
        var malformed = all.Any(item => item.Metadata.Malformed == true);
        var truncated = locallyOmitted > 0 || all.Any(item => item.Metadata.Truncated == true);
        var unavailable = all.Any(item => item.Metadata.Status == "unavailable");
        var complete = !malformed && !truncated && !unavailable;
        // Contents omitted upstream are unavailable for deduplication across
        // groups. Do not add their counts and present a fabricated exact union.
        int? omitted = complete ? 0 : null;
        if (!malformed && !unavailable)
        {
            if (all.Length == 1 && all[0].Metadata.OmittedCount is { } count)
                omitted = count;
            else if (all.All(item => item.Metadata.OmittedCount == 0))
                omitted = locallyOmitted;
        }
        return new(values.Take(MaximumApiQualitySignals).ToArray(), new(
            malformed ? "malformed" : truncated ? "partial" : unavailable ? "unavailable" : "complete",
            omitted, truncated ? true : all.All(item => item.Metadata.Truncated == false) ? false : null,
            malformed ? true : all.All(item => item.Metadata.Malformed == false) ? false : null));
    }
}
