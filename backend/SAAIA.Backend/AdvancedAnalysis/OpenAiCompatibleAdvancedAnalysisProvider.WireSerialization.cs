using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    // This JSON is an HTTP application/json body, never an HTML fragment.
    // Keep prompt construction unchanged: compact only the outer encoding so
    // the provider receives the same decoded message and tool strings.
    private static readonly Lazy<JsonSerializerOptions> WireJsonOptions = new(() =>
        new JsonSerializerOptions(JsonOptions)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    private static string SerializeProviderPayload(Dictionary<string, object?> payload)
        => JsonSerializer.Serialize(payload, WireJsonOptions.Value);

    private static int MeasureEscapedProviderWireTextCharacters(string text)
        => JsonSerializer.Serialize(text, WireJsonOptions.Value).Length - 2;

    private static string CompactNativeResearchUserPrompt(string userPrompt, out bool liftedDiagnosticInstruction)
    {
        liftedDiagnosticInstruction = false;
        var prompt = JsonNode.Parse(userPrompt)!.AsObject();
        if (prompt["researchTools"] is not JsonObject research)
            return userPrompt;

        // The actual function declarations already carry the native tool schemas.
        var removedLegacySchemas = research["tools"] is JsonArray && research.Remove("tools");
        if (research["priorSearches"] is JsonArray priorSearches)
        {
            foreach (var search in priorSearches.OfType<JsonObject>())
            {
                if (search["retrievalDiagnostic"] is JsonObject diagnostic
                    && diagnostic["instruction"] is JsonValue instruction
                    && instruction.TryGetValue<string>(out var text)
                    && text == ResearchDiagnosticPromptInstruction)
                {
                    diagnostic.Remove("instruction");
                    liftedDiagnosticInstruction = true;
                }
            }
        }

        return removedLegacySchemas || liftedDiagnosticInstruction ? prompt.ToJsonString(JsonOptions) : userPrompt;
    }
}
