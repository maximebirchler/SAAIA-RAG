using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static readonly JsonSerializerOptions ExtractionQualityPromptJsonOptions = new(ClientJson.CamelCase)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static void AppendEvidenceExtractionQuality(StringBuilder context, EvidenceItem item)
    {
        if (item.ExtractionQualityDetails is not { } quality) return;
        // These bounded diagnostics remain separate from source text and never
        // imply that a mechanically indexed excerpt is semantically verified.
        var bounded = ExtractionQualitySignalTransport.ReadQuality(JsonSerializer.SerializeToElement(quality));
        context.Append("DIAGNOSTICS_EXTRACTION (statuts de l'observation principale, signaux reunis; exactitude non verifiee): ")
            .AppendLine(JsonSerializer.Serialize(bounded, ExtractionQualityPromptJsonOptions));
    }
}
