using System.Text.Encodings.Web;
using System.Text.Json;

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
}
