using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string DeclareNamedDocumentInsufficiencyToolName =
        "declare_named_document_insufficiency";
    private const string RequestNamedDocumentReferenceCorrectionToolName =
        "request_named_document_reference_correction";
    private const string ResearchNamedDocumentAlternativesToolName =
        "research_named_document_alternatives";
    private const string RequestNamedDocumentCandidateSelectionToolName =
        "request_named_document_candidate_selection";
    private const string RetryNamedDocumentCatalogToolName =
        "retry_named_document_catalog";
    private const string UseResolvedNamedDocumentToolName =
        "use_resolved_named_document";
    private const string ReinterpretNamedReferenceAsSubjectToolName =
        "reinterpret_named_reference_as_subject";

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildNamedDocumentTransitionPortfolio(
            SourceBackedIntake intake,
            NamedDocumentInitialActionPreparation preparation,
            bool catalogRetryUsed,
            IReadOnlyList<SourceBackedAgentToolDefinition> availableTools)
    {
        var observation = intake.RequestedDocumentResolution
            ?? throw new InvalidOperationException(
                "A named-document transition requires a catalog observation.");
        if (HasDefinitiveMissingExplicitDocumentIdentity(intake))
        {
            return new[] { BuildNamedDocumentInsufficiencyTool() };
        }
        var tools = new List<SourceBackedAgentToolDefinition>
        {
            BuildNamedDocumentInsufficiencyTool(),
            BuildNamedDocumentReferenceCorrectionTool(),
            BuildNamedDocumentAlternativeResearchTool()
        };
        if (observation.Status == SourceBackedDocumentResolutionStatus.Ambiguous)
            tools.Add(BuildNamedDocumentCandidateSelectionTool());
        if (RequiresNamedReferenceReconsideration(
                intake,
                preparation,
                availableTools))
        {
            tools.Add(BuildReinterpretNamedReferenceAsSubjectTool());
        }
        if (observation.Status == SourceBackedDocumentResolutionStatus.Inconclusive
            && !catalogRetryUsed)
        {
            tools.Add(BuildNamedDocumentCatalogRetryTool());
        }
        if (observation.Status == SourceBackedDocumentResolutionStatus.Resolved
            && string.Equals(
                preparation.ReasonCode,
                "resolved_identity_conflict",
                StringComparison.Ordinal))
        {
            tools.Add(BuildUseResolvedNamedDocumentTool());
        }
        return tools;
    }

    private static SourceBackedAgentToolDefinition
        BuildNamedDocumentInsufficiencyTool()
        => new(
            DeclareNamedDocumentInsufficiencyToolName,
            "Declare l'insuffisance de la source demandee.",
            ReasonOnlySchema());

    private static SourceBackedAgentToolDefinition
        BuildNamedDocumentReferenceCorrectionTool()
        => new(
            RequestNamedDocumentReferenceCorrectionToolName,
            "Demande une correction de l'identite documentaire exacte.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    identityQuestion = CompactFollowUpString(
                        4, 300, "Question directe terminee par ? sur l'identite exacte."),
                    identityCorrectionOptions = new
                    {
                        type = "array",
                        items = CompactFollowUpString(
                            1, 180, "Moyen concret de corriger l'identite."),
                        minItems = 2,
                        maxItems = 4
                    },
                    executionImpact = CompactFollowUpString(
                        2, 240, "Effet de l'identite corrigee sur la recherche.")
                },
                required = new[]
                {
                    "identityQuestion",
                    "identityCorrectionOptions", "executionImpact"
                },
                additionalProperties = false
            }, ClientJson.CamelCase));

    private static SourceBackedAgentToolDefinition
        BuildNamedDocumentAlternativeResearchTool()
        => new(
            ResearchNamedDocumentAlternativesToolName,
            "Recherche d'autres sources avec divulgation explicite.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    capability = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "rag_search", "documents_navigation",
                            "documents_content_cards"
                        }
                    },
                    query = CompactFollowUpString(
                        1, 600, "Requete pour les sources alternatives."),
                    limit = new { type = "integer", minimum = 1, maximum = 100 },
                    disclosure = CompactFollowUpString(
                        10, 300, "Signalement que les sources ne sont pas le document demande.")
                },
                required = new[]
                {
                    "capability", "query", "disclosure"
                },
                additionalProperties = false
            }, ClientJson.CamelCase));

    private static SourceBackedAgentToolDefinition
        BuildNamedDocumentCandidateSelectionTool()
        => new(
            RequestNamedDocumentCandidateSelectionToolName,
            "Demande de choisir entre les identites exactes observees.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    question = CompactFollowUpString(
                        4, 300, "Question directe terminee par ?"),
                    executionImpact = CompactFollowUpString(
                        2, 240, "Effet du candidat choisi sur la recherche.")
                },
                required = new[] { "question", "executionImpact" },
                additionalProperties = false
            }, ClientJson.CamelCase));

    private static SourceBackedAgentToolDefinition
        BuildNamedDocumentCatalogRetryTool()
        => new(
            RetryNamedDocumentCatalogToolName,
            "Retente une fois une observation inconclusive.",
            EmptyTransitionSchema());

    private static SourceBackedAgentToolDefinition
        BuildUseResolvedNamedDocumentTool()
        => new(
            UseResolvedNamedDocumentToolName,
            "Utilise l'identite resolue pour borner l'action en conflit.",
            EmptyTransitionSchema());

    private static SourceBackedAgentToolDefinition
        BuildReinterpretNamedReferenceAsSubjectTool()
        => new(
            ReinterpretNamedReferenceAsSubjectToolName,
            "Reclasse la reference comme sujet ou entite et restaure l'action documentaire suspendue sans la modifier.",
            EmptyTransitionSchema());

    private static object ReasonProperty()
        => CompactFollowUpString(10, 400, "Motif precis de la transition.");

    private static JsonElement ReasonOnlySchema()
        => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { reason = ReasonProperty() },
            required = new[] { "reason" },
            additionalProperties = false
        }, ClientJson.CamelCase);

    private static JsonElement EmptyTransitionSchema()
        => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            additionalProperties = false
        }, ClientJson.CamelCase);
}
