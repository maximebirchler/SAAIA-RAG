using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static JsonNode? NativeViewQualityReferences(object alternative, IReadOnlyList<PromptEvidenceItem> evidence,
        string collection)
    {
        var node = JsonSerializer.SerializeToNode(alternative, JsonOptions);
        if (collection is not ("evidence" or "observations") || node?["views"] is not JsonArray views)
            return node;

        foreach (var view in views.OfType<JsonObject>())
        {
            if (view["lines"] is not JsonArray { Count: > 0 } lines
                || view["qualitySignals"] is not JsonArray { Count: > 0 }
                || !TryNativeQualityValue(view, "sourceKey", out string sourceKey)
                || !TryNativeQualityValue(view, "physicalPage", out int page)
                || !TryNativeQualityValue(view, "blockId", out string block)
                || !TryNativeQualityValue(view, "rawTextSha256", out string rawSha)
                || !TryNativeQualityValue(view, "offset", out int offset)
                || !TryNativeQualityValue(view, "limit", out int limit)
                || !TryNativeQualityValue(view, "rawLineCount", out int rawLineCount)
                || string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(block)
                || !NativeQualityHash(rawSha) || page < 1 || offset < 0 || limit is < 1 or > 64 || rawLineCount < 0)
                continue;

            PromptEvidenceItem? first = null;
            var exact = true;
            var lineOrdinal = offset;
            foreach (var line in lines)
            {
                if (line is not JsonObject metadata
                    || !TryNativeQualityValue(metadata, "evidenceId", out string evidenceId))
                {
                    exact = false;
                    break;
                }
                var matches = evidence.Where(item => item.EvidenceId == evidenceId).ToArray();
                if (matches.Length != 1 || matches[0] is not { NativeObservation: { } source, SourceQuality: { } quality } item
                    || source.Reference is not { } reference
                    || string.IsNullOrWhiteSpace(evidenceId) || string.IsNullOrWhiteSpace(reference.LineId)
                    || !NativeQualityHash(reference.LineTextSha256) || reference.LineOrdinal != lineOrdinal
                    || reference.UnicodeScalarStart < 0 || reference.UnicodeScalarEndExclusive < reference.UnicodeScalarStart
                    || item.SourceKey != sourceKey || item.ContentTruncated || item.Content.Length != item.OriginalContentLength
                    || reference.PhysicalPage != page || reference.BlockId != block || reference.RawTextSha256 != rawSha
                    || source.LineOffset != offset || source.LineLimit != limit || source.ViewLineCount != rawLineCount
                    || quality.MetadataMalformed || quality.MetadataTruncated || quality.SignalsOmittedCount != 0
                    || !KnownNativeQualitySignals(source.QualitySignals)
                    || !KnownNativeQualitySignals(quality.Signals)
                    || !source.QualitySignals.SequenceEqual(quality.Signals, StringComparer.Ordinal)
                    || !JsonNode.DeepEquals(view["qualitySignals"], JsonSerializer.SerializeToNode(source.QualitySignals, JsonOptions)))
                {
                    exact = false;
                    break;
                }

                // Compare every admitted line, not the group's first item.
                // These are the same complete references emitted in the view.
                var expectedLine = JsonSerializer.SerializeToNode(new
                {
                    item.EvidenceId, reference.LineId, reference.LineOrdinal, reference.LineTextSha256,
                    reference.UnicodeScalarStart, reference.UnicodeScalarEndExclusive
                }, JsonOptions);
                if (!JsonNode.DeepEquals(metadata, expectedLine))
                {
                    exact = false;
                    break;
                }
                first ??= item;
                lineOrdinal++;
            }
            if (!exact || first is null)
                continue;

            // Both full per-line arrays remain in this current-user payload.
            // The shared view references their exact native evidence ID rather
            // than repeating an identical third array. Divergence is never
            // normalized or replaced by a reference to another observation.
            view.Remove("qualitySignals");
            view["qualitySignalsSourceEvidenceId"] = first.EvidenceId;
            view["qualitySignalsReference"] = "current_user." + collection + "[evidenceId].nativeObservation.qualitySignals";
        }
        return node;
    }

    private static bool KnownNativeQualitySignals(IReadOnlyList<string>? signals)
        => signals is { Count: > 0 and <= 64 } && signals.All(code => code is { Length: > 0 and <= 160 }
            && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':'));

    private static bool NativeQualityHash(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool TryNativeQualityValue<T>(JsonObject value, string key, out T result)
    {
        if (value[key] is JsonValue scalar && scalar.TryGetValue<T>(out var found) && found is not null)
        {
            result = found;
            return true;
        }
        result = default!;
        return false;
    }
}
