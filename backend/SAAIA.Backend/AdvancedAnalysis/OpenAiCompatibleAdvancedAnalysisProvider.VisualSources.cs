using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const string VisualSourceCitationContract = """
        visualSources are immutable authenticated resource locators, not model
        pixels or an interpretation. Cite their exact document, revision, source,
        page, assetId and content hash. A page_with_observed_region is a whole
        page with a source anchor locator, never a newly recognized formula crop.
        A figure asset is not linked to another block merely by bbox overlap.
        imageToolAvailable=false and modelPixelsProvided=false mean no image
        inspection occurred or can be requested through these metadata tools.
        humanViewerAvailable=null is unknown. Do not claim that a client viewer
        works or ask an unavailable model image tool to inspect these resources.
        Once an exact sourceKey is observed and read_source is permitted,
        request includeVisualSources=true with visualSourceOffset=0 and optional
        visualSourceLimit from 1 to 64 to discover these locators.
        Use nextAssetOffset for the next whole-descriptor window. Missing or
        budget-refused metadata does not prove absence of images in the PDF.
        """;

    private static bool HasVisualSourceCitations(JsonElement root)
        => root.TryGetProperty("visualSourceCapabilities", out var capabilities)
            && capabilities.TryGetProperty("visualSourceCitationAvailable", out var value)
            && value.ValueKind == JsonValueKind.True;

    private string AddVisualSourcesToPrompt(string prompt)
    {
        // Return the original bytes before parsing when the feature is off.
        if (!_options.VisualSourceCitationsEnabled)
            return prompt;

        var node = JsonNode.Parse(prompt) as JsonObject;
        if (node is null)
            return prompt;

        node["visualSourceCapabilities"] = JsonSerializer.SerializeToNode(new
        {
            visualSourceCitationAvailable = true,
            authenticatedPixelReadAvailable = (bool?)null,
            humanViewerAvailable = (bool?)null,
            imageToolAvailable = false,
            modelPixelsProvided = false,
            interpretationStatus = "not_performed",
            semanticAccuracy = "not_verified"
        }, JsonOptions);
        node["visualSourceCitationContract"] = VisualSourceCitationContract;

        var evidence = node["evidence"] as JsonArray ?? node["observations"] as JsonArray;
        var sources = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (evidence is not null)
        {
            foreach (var item in evidence.OfType<JsonObject>())
            {
                if (item["sourceOverview"] is not JsonObject overview
                    || overview["visualSources"] is not JsonObject metadata)
                    continue;

                var raw = metadata.ToJsonString(JsonOptions);
                if (seen.Add(raw))
                    sources.Add(metadata.DeepClone());

                overview.Remove("visualSources");
            }
        }

        if (sources.Count > 0)
            node["visualSources"] = sources;

        var declarations = (node["researchTools"] as JsonObject)?["tools"] as JsonArray ?? node["tools"] as JsonArray;
        declarations?.Add(JsonSerializer.SerializeToNode(new
        {
            name = "read_source_visual_source_citations",
            operation = "read_source",
            includeVisualSources = true,
            sourceKey = "exact currently observed sourceKey",
            pageStart = "physical page",
            pageEnd = "inclusive, at most four pages",
            visualSourceOffset = "0 initially, then nextAssetOffset",
            visualSourceLimit = "integer 1 to 64; default 4",
            instruction = VisualSourceCitationContract
        }, JsonOptions));
        return node.ToJsonString(JsonOptions);
    }
}
