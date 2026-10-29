using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const string SecondaryTableReadingContract = """
        alternativeTableReadings are secondary OCR observations, never repaired
        canonical cells, verified PDF coverage or proved row/column membership.
        Raw order and candidate physical bands are different views. A band does
        not establish a header, cell, parent or semantic relationship. Its line
        references point only to whole literal text in current user.evidence.
        Never reconstruct missing lines or use a historical excerpt absent here.
        Use the exact sourceKey, physical page, observationId and nextLineOffset
        to read another bounded view. If physical reading is unavailable, raw
        remains the fallback; do not invent bands. A line is indivisible: an
        explicit budget refusal supplies no part of its text. Empty observed
        lists are known empty; unavailable inventories and counts are unknown.
        imageInspectionAvailable=false or imageToolAvailable=false supplies no
        image tool or pixels; do not suggest or claim an unavailable inspection.
        """;

    private static bool HasSecondaryTableReadings(JsonElement root)
        => root.TryGetProperty("alternativeTableReadings", out var value)
           && value.ValueKind == JsonValueKind.Object;

    private static object? BuildAlternativeTableReadingsForPrompt(IReadOnlyList<PromptEvidenceItem> evidence)
    {
        var eligible = evidence.Where(item => item.SourceObservation is not null
            || item.SourceOverview?.TableRegionObservations is not null).ToArray();
        if (eligible.Length == 0) return null;
        var inventories = eligible.Where(item => item.SourceOverview?.TableRegionObservations is not null)
            .GroupBy(item => item.SourceKey, StringComparer.Ordinal).Select(group =>
            {
                var inventory = group.First().SourceOverview!.TableRegionObservations!;
                var projected = JsonSerializer.SerializeToNode(new
                {
                    sourceKey = group.Key, inventory.Availability, inventory.ObservationCount, inventory.AttemptCount,
                    // Counts describe the stored catalogue. Page entries with no
                    // admitted observation need not be repeated on every excerpt.
                    pages = inventory.Pages?.Where(page => page.ObservationCount is > 0).ToArray(),
                    zeroOrUnavailablePagesNotListed = inventory.Pages?.Count(page => page.ObservationCount is not > 0),
                    observations = inventory.Observations,
                    rawFallbackAvailableWhenPhysicalReadingUnavailable = true
                }, JsonOptions)!.AsObject();
                if (group.Any(item => item.SecondaryCatalogueDescriptorsOmitted))
                {
                    // Absent descriptors are not a known-empty observation
                    // list. The listed pages retain their stored provenance
                    // and counts, and the tool can recover a page's inventory.
                    projected.Remove("observations");
                    projected["observationDescriptorsOmittedCount"] = inventory.Observations?.Count ?? 0;
                    projected["nextObservationDescriptorOffset"] = 0;
                    projected["observationDescriptorRecovery"] = "read_source tableObservationView=inventory on one listed physical page; line offsets must be null";
                }
                return (object)projected;
            }).ToArray();
        var views = evidence.Where(item => item.SourceObservation is not null)
            .GroupBy(item => new
            {
                item.SourceKey, item.SourceObservation!.ObservationId, item.SourceObservation.View,
                item.SourceObservation.BandOrdinal, item.SourceObservation.LineOffset, item.SourceObservation.LineLimit
            }).Select(group =>
            {
                var source = group.First().SourceObservation!;
                var lines = new List<object>();
                var next = source.LineOffset;
                foreach (var item in group.OrderBy(item => item.SourceObservation!.ViewOrdinal))
                {
                    var line = item.SourceObservation!;
                    if (line.ViewOrdinal != next || item.ContentTruncated || item.Content.Length != item.OriginalContentLength)
                        break;
                    lines.Add(new { item.EvidenceId, line.LineId, line.RawOrdinal, line.ViewOrdinal, line.TextSha256 });
                    next++;
                }
                return (object)new
                {
                    group.Key.SourceKey, source.ObservationId, source.TableId,
                    physicalPage = group.First().PhysicalPageStart, source.RawObservationSha256,
                    source.View, source.BandOrdinal, source.LineOffset, source.LineLimit, source.ViewLineCount,
                    returnedLineCount = lines.Count,
                    nextLineOffset = next < source.ViewLineCount ? (int?)next : null,
                    entireViewReturned = source.LineOffset == 0 && next == source.ViewLineCount,
                    windowComplete = next == Math.Min(source.ViewLineCount, source.LineOffset + source.LineLimit),
                    status = lines.Count == 0 && source.ViewLineCount > source.LineOffset
                        ? "line_does_not_fit_model_budget" : next < Math.Min(source.ViewLineCount, source.LineOffset + source.LineLimit)
                            ? "partial_model_budget" : "observed",
                    lines, source.QualitySignals, source.SemanticAccuracy,
                    source.CellMembershipProven, source.SemanticParentLinksAsserted,
                    pdfCoverageVerified = false, canonicalGridRepaired = false
                };
            }).ToArray();
        return new
        {
            inventories, views,
            imageInspectionAvailable = eligible.Any(item => item.SourceOverview?.VisualAssets?.ImageToolAvailable == true),
            instruction = "Secondary observations only. Literal text occurs once in current evidence. Copy its evidenceId; bands are candidate physical order, never canonical cells or proven semantic associations. Use raw when the physical view is unavailable."
        };
    }

    // All prompt roles pass through this seam. Null properties remain absent for
    // legacy sources; no global serializer setting or old prompt changes.
    private static string AddSecondaryTableReadingsToPrompt(string prompt)
    {
        var node = JsonNode.Parse(prompt) as JsonObject;
        var evidence = node?["evidence"] as JsonArray ?? node?["observations"] as JsonArray;
        if (evidence is null) return prompt;
        var typed = evidence.Deserialize<PromptEvidenceItem[]>(JsonOptions);
        if (typed is null) return prompt;
        // SourceReference is intentionally not serialized. The projection below
        // depends only on the current evidence and its typed observation fields.
        var alternative = BuildAlternativeTableReadingsForPrompt(typed);
        if (alternative is null) return prompt;
        node!["alternativeTableReadings"] = JsonSerializer.SerializeToNode(alternative, JsonOptions);
        foreach (var item in evidence.OfType<JsonObject>())
        {
            item.Remove("secondaryCatalogueDescriptorsOmitted");
            if (item["sourceOverview"] is JsonObject overview) overview.Remove("tableRegionObservations");
        }
        var declarations = (node["researchTools"] as JsonObject)?["tools"] as JsonArray ?? node["tools"] as JsonArray;
        if (declarations is not null)
            declarations.Add(JsonSerializer.SerializeToNode(new
            {
                name = "read_source_secondary_table_observation", operation = "read_source",
                sourceKey = "exact currently observed sourceKey", pageStart = "one physical page", pageEnd = "same page",
                tableObservationView = "inventory|raw|physical_band|unassigned",
                tableObservationId = "exact observed observationId, null for inventory",
                tableObservationBandOrdinal = "observed band ordinal for physical_band only; null otherwise",
                tableObservationLineOffset = "0 initially, then current nextLineOffset; null for inventory",
                tableObservationLineLimit = "integer 1 to 64; null for inventory",
                instruction = "Do not mix canonical tableOrdinal/row parameters. Lines remain whole; raw is available when candidate physical bands are unavailable."
            }, JsonOptions));
        return node.ToJsonString(JsonOptions);
    }

    // Measure the actual final wire, including the escaping of nested tool JSON.
    // Removing documentary fields from an otherwise identical serialized request
    // isolates their exact contribution without estimating tokens or characters.
    internal static int MeasureSecondaryDocumentaryWireCharacters(string serializedPayload)
    {
        var original = JsonNode.Parse(serializedPayload)!.AsObject();
        var baseline = (JsonObject)original.DeepClone();
        var messages = baseline[baseline.ContainsKey("input") ? "input" : "messages"]!.AsArray();
        foreach (var item in messages.OfType<JsonObject>().ToArray())
        {
            if (item["role"]?.GetValue<string>() == "tool" || item["type"]?.GetValue<string>() == "function_call_output"
                || item.ContainsKey("tool_calls") || item["type"]?.GetValue<string>() == "function_call")
            {
                messages.Remove(item);
                continue;
            }
            if (item["role"]?.GetValue<string>() != "user" || item["content"] is not JsonValue content
                || !content.TryGetValue<string>(out var text)) continue;
            var user = JsonNode.Parse(text) as JsonObject;
            if (user is null) continue;
            user.Remove("evidence"); user.Remove("observations"); user.Remove("structuredTables"); user.Remove("alternativeTableReadings");
            user.Remove("visualSources");
            user.Remove("visualSourceCapabilities");
            user.Remove("alternativeNativeReadings");
            user.Remove("nativeObservationCapabilities");
            item["content"] = user.ToJsonString(JsonOptions);
        }
        return serializedPayload.Length - baseline.ToJsonString(JsonOptions).Length;
    }
}
