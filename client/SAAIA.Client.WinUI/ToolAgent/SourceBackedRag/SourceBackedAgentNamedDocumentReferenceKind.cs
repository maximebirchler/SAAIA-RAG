using System.IO;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string NamedReferenceNone = "none";
    private const string NamedReferenceSubject = "subject";
    private const string NamedReferenceDocument = "document";

    private static string ReadExplicitNamedReferenceKind(
        SourceBackedIntake intake)
    {
        var kind = NormalizeNamedReferenceKind(intake.NamedReferenceKind);
        if (kind.Length > 0)
            return kind;

        var mission = intake.InitialSemanticMission?.Arguments;
        if (mission is not { ValueKind: JsonValueKind.Object }
            || !TryGetPropertyIgnoreCase(
                mission.Value,
                "namedReferenceKind",
                out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }
        return NormalizeNamedReferenceKind(kindElement.GetString());
    }

    private static string NormalizeNamedReferenceKind(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is NamedReferenceNone
            or NamedReferenceSubject
            or NamedReferenceDocument
            ? normalized
            : string.Empty;
    }

    private static bool RequiresNamedReferenceReconsideration(
        SourceBackedIntake intake,
        NamedDocumentInitialActionPreparation preparation,
        IReadOnlyList<SourceBackedAgentToolDefinition> availableTools)
    {
        if (HasDefinitiveMissingExplicitDocumentIdentity(intake))
            return false;

        var status = intake.RequestedDocumentResolution?.Status;
        return string.Equals(
                   ReadExplicitNamedReferenceKind(intake),
                   NamedReferenceDocument,
                   StringComparison.Ordinal)
               && status is SourceBackedDocumentResolutionStatus.NotFound
                   or SourceBackedDocumentResolutionStatus.Ambiguous
               && SelectAvailableQuarantinedActions(
                       preparation,
                       availableTools)
                   .Count > 0;
    }

    private static bool HasDefinitiveMissingExplicitDocumentIdentity(
        SourceBackedIntake intake)
    {
        var observation = intake.RequestedDocumentResolution;
        if (observation is null
            || observation.Status != SourceBackedDocumentResolutionStatus.NotFound
            || !observation.CatalogObservationComplete
            || observation.Candidates.Count != 0
            || observation.ExactMatchCount is > 0
            || !string.Equals(
                ReadExplicitNamedReferenceKind(intake),
                NamedReferenceDocument,
                StringComparison.Ordinal))
        {
            return false;
        }

        var requested = string.IsNullOrWhiteSpace(intake.RequestedDocumentName)
            ? observation.RequestedReference
            : intake.RequestedDocumentName;
        return string.Equals(
            Path.GetExtension(requested?.Trim()),
            ".pdf",
            StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<SourceBackedInitialToolCall>
        SelectAvailableQuarantinedActions(
            NamedDocumentInitialActionPreparation preparation,
            IReadOnlyList<SourceBackedAgentToolDefinition> availableTools)
        => preparation.QuarantinedActions
            .Where(action => availableTools.Any(tool => string.Equals(
                tool.Name,
                action.ToolName,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
}
