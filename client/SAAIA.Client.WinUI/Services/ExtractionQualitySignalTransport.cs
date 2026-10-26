using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services;

internal sealed record QualitySignalSnapshot(IReadOnlyList<string> Signals, RagQualitySignalsMetadata Metadata);

/// <summary>Bounds prompt transport while retaining uncertainty about upstream omissions.</summary>
internal static class ExtractionQualitySignalTransport
{
    internal const int MaximumSignals = 64;
    private static readonly string[] SignalNames = ["signals", "qualitySignals", "quality_signals"];
    private static readonly string[] ChunkSignalNames = ["chunkQualitySignals", "chunk_quality_signals"];
    private static readonly string[] MetadataNames = ["signalsMetadata", "signals_metadata", "qualitySignalsMetadata"];
    private static readonly string[] ChunkMetadataNames = ["chunkQualitySignalsMetadata", "chunk_quality_signals_metadata"];

    internal static JsonElement QualityObject(JsonElement item)
    {
        var nested = ReadProperty(item, "extractionQuality", "extraction_quality");
        return nested is { ValueKind: JsonValueKind.Object } ? nested.Value : item;
    }

    internal static QualitySignalSnapshot Read(JsonElement item, bool chunk = false)
    {
        var quality = QualityObject(item);
        var values = ReadProperty(quality, chunk ? ChunkSignalNames : SignalNames);
        var metadata = ReadProperty(quality, chunk ? ChunkMetadataNames : MetadataNames);
        var malformed = values.HasValue && values.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null);
        var strings = new List<string>();
        if (values is { ValueKind: JsonValueKind.Array } array)
        {
            if (array.GetRawText().Length > 100_000)
                malformed = true;
            else
                foreach (var value in array.EnumerateArray())
                    if (value.ValueKind == JsonValueKind.String)
                        strings.Add(value.GetString()!);
                    else
                        malformed = true;
        }
        RagQualitySignalsMetadata? typed = null;
        if (metadata.HasValue && metadata.Value.ValueKind != JsonValueKind.Null)
        {
            try
            {
                if (metadata.Value.ValueKind != JsonValueKind.Object || metadata.Value.GetRawText().Length > 2_048)
                    malformed = true;
                else
                    typed = JsonSerializer.Deserialize<RagQualitySignalsMetadata>(metadata.Value.GetRawText(), ClientJson.CamelCase);
            }
            catch (JsonException) { malformed = true; }
        }
        if (typed is not null && values is not { ValueKind: JsonValueKind.Array } && typed.Status != "unavailable")
            malformed = true;
        return Bound(strings, malformed ? new("malformed", Malformed: true) : typed);
    }

    internal static QualitySignalSnapshot Bound(IEnumerable<string>? signals, RagQualitySignalsMetadata? metadata)
    {
        var raw = signals?.ToArray() ?? [];
        var invalidValues = raw.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl));
        var valid = raw.Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        var omitted = Math.Max(0, valid.Length - MaximumSignals);
        var state = metadata ?? new("unavailable");
        var invalidMetadata = state.Status is not ("complete" or "partial" or "malformed" or "unavailable")
            || signals is null && state.Status is "complete" or "partial"
            || state.OmittedCount is < 0
            || state.Status == "complete" && (state.OmittedCount != 0 || state.Truncated != false || state.Malformed != false)
            || state.Status == "partial" && (state.Truncated != true || state.OmittedCount == 0)
            || state.Status == "unavailable" && (state.OmittedCount is not null || state.Truncated is not null || state.Malformed is not null);
        if (invalidValues || invalidMetadata || state.Status == "malformed" || state.Malformed == true)
            state = new("malformed", Truncated: omitted > 0 || state.Truncated == true ? true : null, Malformed: true);
        else if (omitted > 0)
            state = new("partial", state.Status == "complete" ? omitted : null, true, state.Malformed == false ? false : null);
        return new(valid.Take(MaximumSignals).ToArray(), state);
    }

    internal static QualitySignalSnapshot Merge(IEnumerable<QualitySignalSnapshot> snapshots)
    {
        var all = snapshots.Select(value => Bound(value.Signals, value.Metadata)).ToArray();
        if (all.Length == 0) return Bound(null, null);
        if (all.Length == 1) return all[0];
        var distinct = all.SelectMany(value => value.Signals).Distinct(StringComparer.Ordinal).ToArray();
        var localOmitted = Math.Max(0, distinct.Length - MaximumSignals);
        var malformed = all.Any(value => value.Metadata.Status == "malformed");
        var truncated = localOmitted > 0 || all.Any(value => value.Metadata.Truncated == true);
        var unavailable = all.Any(value => value.Metadata.Status == "unavailable");
        // Omitted upstream values cannot be deduplicated. Never sum them to
        // claim the exact missing count of a union of different observations.
        int? omitted = !malformed && !unavailable && all.All(value => value.Metadata.OmittedCount == 0) ? localOmitted : null;
        return new(distinct.Take(MaximumSignals).ToArray(), new(
            malformed ? "malformed" : truncated ? "partial" : unavailable ? "unavailable" : "complete",
            omitted, truncated ? true : all.All(value => value.Metadata.Truncated == false) ? false : null,
            malformed ? true : all.All(value => value.Metadata.Malformed == false) ? false : null));
    }

    internal static RagItemExtractionQuality? ReadQuality(JsonElement item)
    {
        var quality = QualityObject(item);
        if (quality.ValueKind != JsonValueKind.Object) return null;
        if (!ReadProperty(item, "extractionQuality", "extraction_quality").HasValue
            && !ReadProperty(item, SignalNames).HasValue
            && !ReadProperty(item, ChunkSignalNames).HasValue
            && !ReadProperty(item, MetadataNames).HasValue
            && !ReadProperty(item, ChunkMetadataNames).HasValue
            && !ReadProperty(item, "textStatus").HasValue)
            return null;
        var signals = Read(item);
        var chunks = Read(item, chunk: true);
        var copy = JsonNode.Parse(quality.GetRawText())!.AsObject();
        foreach (var name in copy.Select(pair => pair.Key).Where(name =>
                     name.Equals("signals", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("chunkQualitySignals", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("signalsMetadata", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("chunkQualitySignalsMetadata", StringComparison.OrdinalIgnoreCase)).ToArray())
            copy.Remove(name);
        RagItemExtractionQuality result;
        try { result = copy.Deserialize<RagItemExtractionQuality>(ClientJson.CamelCase) ?? new(); }
        catch (JsonException) { result = new(); }
        result.Signals = signals.Signals.ToList();
        result.SignalsMetadata = signals.Metadata;
        result.ChunkQualitySignals = chunks.Signals.ToList();
        result.ChunkQualitySignalsMetadata = chunks.Metadata;
        return result;
    }

    internal static RagItemExtractionQuality? MergeQualities(
        IEnumerable<RagItemExtractionQuality?> qualities, RagItemExtractionQuality? selected)
    {
        var all = qualities.ToArray();
        if (all.All(value => value is null)) return null;
        var result = selected is null ? new RagItemExtractionQuality()
            : ReadQuality(JsonSerializer.SerializeToElement(selected))!;
        var signals = Merge(all.Select(value => Bound(value?.Signals, value?.SignalsMetadata)));
        var chunks = Merge(all.Select(value => Bound(value?.ChunkQualitySignals, value?.ChunkQualitySignalsMetadata)));
        result.Signals = signals.Signals.ToList();
        result.SignalsMetadata = signals.Metadata;
        result.ChunkQualitySignals = chunks.Signals.ToList();
        result.ChunkQualitySignalsMetadata = chunks.Metadata;
        return result;
    }

    private static JsonElement? ReadProperty(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in item.EnumerateObject())
            if (names.Any(name => name.Equals(property.Name, StringComparison.OrdinalIgnoreCase)))
                return property.Value;
        return null;
    }
}
