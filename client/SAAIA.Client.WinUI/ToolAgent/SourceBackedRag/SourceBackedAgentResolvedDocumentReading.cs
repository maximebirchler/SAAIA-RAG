using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static readonly HashSet<string> ResolvedDocumentReadingArguments = new(StringComparer.Ordinal)
    {
        "docId", "pageStart", "pageEnd", "before", "after", "limit", "offset"
    };

    private static IReadOnlyList<SourceBackedAgentToolDefinition> AddResolvedDocumentReadingTool(
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        SourceBackedIntake? intake)
    {
        if (intake is null
            || !SourceBackedNamedDocumentIdentity.TryGetResolvedRequestedIdentity(intake, out var identity)
            || !Guid.TryParse(identity.DocId, out _)
            || tools.Any(static tool => tool.Name == "documents_context"))
            return tools;

        // This catalog identity is a locator, never an EvidenceItem. The model
        // chooses whether to read, which pages, and how much indexed text to request.
        var context = SourceBackedAgentToolCatalog.Build(useConstrainedContextDescriptions: true)
            .Single(static tool => tool.Name == "documents_context");
        var properties = context.Parameters.GetProperty("properties").EnumerateObject()
            .Where(property => ResolvedDocumentReadingArguments.Contains(property.Name))
            .ToDictionary(static property => property.Name, static property => property.Value.Clone());
        properties["docId"] = JsonSerializer.SerializeToElement(new
        {
            type = "string", @enum = new[] { identity.DocId }
        });
        return tools.Append(context with
        {
            Description = "Read indexed text from the requested document resolved in the current catalog: "
                + JsonSerializer.Serialize(identity.DocPath) + ". The catalog establishes identity only, not content evidence. "
                + "Choose pages or an offset if useful; omit them to read from the beginning. Returned text is the evidence to assess.",
            Parameters = JsonSerializer.SerializeToElement(new
            {
                type = "object", properties, required = new[] { "docId" }, additionalProperties = false
            })
        }).ToArray();
    }

    private static string? ValidateResolvedDocumentReadingCall(
        SourceBackedAgentToolCall call, SourceBackedIntake intake)
    {
        if (call.Name != "documents_context"
            || !SourceBackedNamedDocumentIdentity.TryGetResolvedRequestedIdentity(intake, out var identity)
            || !Guid.TryParse(identity.DocId, out _))
            return null;
        if (call.Arguments.ValueKind != JsonValueKind.Object)
            return "tool_arguments_must_be_an_object";
        if (!string.Equals(GetString(call.Arguments, "docId"), identity.DocId, StringComparison.OrdinalIgnoreCase))
            return "resolved_document_reading_identity_mismatch";
        if (call.Arguments.EnumerateObject().Any(property => !ResolvedDocumentReadingArguments.Contains(property.Name)))
            return "resolved_document_reading_unexposed_argument";
        return null;
    }
}
