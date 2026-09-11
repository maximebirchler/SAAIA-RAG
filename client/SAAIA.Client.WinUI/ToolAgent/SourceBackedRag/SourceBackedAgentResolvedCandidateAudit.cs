using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SubmitResolvedCandidateAuditToolName =
        "submit_resolved_candidate_audit";

    private const string ResolvedAuditRejectOther = "reject_other";
    private const string ResolvedAuditAccept = "accept";
    private const string ResolvedAuditRejectAxisOrRole = "reject_axis";
    private const string ResolvedAuditRejectCategoryOrCollection =
        "reject_category";
    private const string ResolvedAuditRejectInstructionOrFragment =
        "reject_fragment";

    private static readonly string[] ResolvedCandidateAuditDecisions =
    {
        ResolvedAuditAccept,
        ResolvedAuditRejectAxisOrRole,
        ResolvedAuditRejectCategoryOrCollection,
        ResolvedAuditRejectInstructionOrFragment,
        ResolvedAuditRejectOther
    };

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildResolvedCandidateAuditMessages(
            SourceBackedIntake intake,
            string semanticPlan,
            string candidateObjectType,
            string candidateEligibilityRule,
            string semanticRowHeader,
            IReadOnlyList<string> semanticRowLabels,
            IReadOnlyList<string> semanticColumnLabels,
            IReadOnlyList<EvidenceItem> candidates)
    {
        var expectedType = string.IsNullOrWhiteSpace(candidateObjectType)
            ? ReadSemanticCandidateObjectType(semanticPlan)
            : candidateObjectType;
        var prompt = new StringBuilder()
            .Append("DEMANDE ORIGINALE: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 500))
            .Append("MISSION STRUCTUREE: ")
            .AppendLine(TrimPromptValue(semanticPlan, 520))
            .Append("TYPE ATOMIQUE DECIDE PAR LE LLM: ")
            .AppendLine(TrimPromptValue(expectedType, 160));
        if (!string.IsNullOrWhiteSpace(candidateEligibilityRule))
        {
            prompt.Append("REGLE D'ELIGIBILITE DECIDEE PAR LE LLM: ")
                .AppendLine(TrimPromptValue(candidateEligibilityRule, 300));
        }
        var axes = new[] { semanticRowHeader }
            .Concat(semanticRowLabels)
            .Concat(semanticColumnLabels)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (axes.Length > 0)
        {
            prompt.Append("AXES DE PLACEMENT, JAMAIS DES CANDIDATS: ")
                .AppendLine(string.Join(" | ", axes));
        }
        prompt.AppendLine("CANDIDATS DONT LE LIBELLE SOURCE EST DEJA RESOLU:");
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            prompt.Append(CandidateAuditKey(index))
                .Append(" | libelle=\"")
                .Append(TrimPromptValue(GetEvidenceDisplayValue(candidate), 120))
                .AppendLine("\"");
        }
        prompt.Append(
            "RAPPEL AVANT DECISION: une phrase d'action, une methode ou une procedure "
            + "generale ne nomme pas une instance particuliere et doit etre rejetee. "
            + "DECISION: retourne decisions=[...] dans l'ordre c1,c2,... avec une "
            + "valeur parmi accept, reject_axis, reject_category, reject_fragment, "
            + "reject_other. reject_axis seulement si le TEXTE DU "
            + "LIBELLE nomme lui-meme un axe/role/periode ou sa variante annotee; "
            + "reject_category pour categorie/collection/rubrique/classe generale; "
            + "reject_fragment pour instruction/fragment ou libelle pollue par des "
            + "identifiants, index ou residus OCR; reject_other pour "
            + "un autre mauvais type. Aucun quota d'acceptation.");
        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es le juge semantique final des candidats documentaires. Leur libelle "
                + "a deja ete resolu par une couche d'identite distincte: ne le remets pas en cause et "
                + "ne choisis plus de parent. Pour chaque candidat, decide uniquement si "
                + "l'element nomme peut constituer UNE preuve atomique autonome qui contribue "
                + "a UNE cellule ou unite du livrable demande. Ne lui demande jamais de "
                + "satisfaire seul tout le livrable compose ni de reproduire les exemples "
                + "mot pour mot. Accepte lorsque le libelle nomme une instance particuliere "
                + "du type attendu. Juge uniquement la nature du LIBELLE RESOLU. L'appel "
                + "de resolution a deja utilise la hierarchie et l'extrait pour rattacher le "
                + "fragment documentaire a ce libelle; ne refais pas ce travail. Les rejets "
                + "qualifient le libelle resolu lui-meme. "
                + "Une phrase verbale qui ordonne ou decrit une action a executer reste une "
                + "instruction ou un fragment, meme si elle mentionne un objet complet. Un "
                + "intitule de methode, technique, procedure ou traitement applicable a une "
                + "classe d'objets n'est pas une instance particuliere. Exige que le libelle "
                + "identifie lui-meme une instance autonome du type attendu. "
                + "Pour reject_axis, demande uniquement si le TEXTE DU LIBELLE nomme "
                + "lui-meme un axe fourni, une ligne, une colonne, un role, une periode "
                + "ou leur variante annotee. Ne demande jamais si l'objet pourrait remplir "
                + "une case. Un libelle reste reject_axis s'il reprend principalement un "
                + "axe en ajoutant une heure, un numero, une parenthese, une frequence ou "
                + "un autre qualificatif. Exemple structurel avec les axes Phase et Equipe: "
                + "'Phase 2' et 'Equipe (matin)' sont reject_axis; 'Compresseur AX-17' et "
                + "'Catalogue general' ne le sont pas. "
                + "Rejette les axes, roles, periodes, "
                 + "categories, collections, rubriques generales, consignes, fragments ou "
                 + "autres mauvais types. Un pluriel peut etre le nom exact d'une instance "
                 + "ou une classe generale: ne decide jamais sur la grammaire seule; demande "
                 + "si le libelle identifie un element particulier utilisable, plutot qu'un "
                 + "ensemble d'elements possibles. Rejette aussi comme fragment un libelle "
                 + "dont le nom precis est suivi ou fusionne avec un index brut, des codes "
                 + "internes, des identifiants techniques ou un long residu OCR: le livrable "
                 + "exige un nom source autonome et propre. Juge chaque candidat "
                 + "independamment et sans quota. "
                 + "Retourne uniquement l'objet JSON."),
            SourceBackedAgentMessage.User(prompt.ToString().Trim())
        };
    }

    private static LlmStructuredOutputContract
        BuildResolvedCandidateAuditContract(
            IReadOnlyList<EvidenceItem> candidates)
        => new(
            "source_backed_resolved_candidate_audit_v5",
            BuildResolvedCandidateAuditSchema(candidates));

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildResolvedCandidateAuditTool(
            IReadOnlyList<EvidenceItem> candidates)
        => new[]
        {
            new SourceBackedAgentToolDefinition(
                SubmitResolvedCandidateAuditToolName,
                "Accepte ou classe le rejet de chaque candidat au libelle resolu.",
                BuildResolvedCandidateAuditSchema(candidates))
        };

    private static JsonElement BuildResolvedCandidateAuditSchema(
        IReadOnlyList<EvidenceItem> candidates)
    {
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["decisions"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["enum"] = ResolvedCandidateAuditDecisions
                        },
                        ["minItems"] = candidates.Count,
                        ["maxItems"] = candidates.Count
                    }
                },
                ["required"] = new[] { "decisions" },
                ["additionalProperties"] = false
            },
            ClientJson.CamelCase);
    }

    private static CandidateCollectionAuditDecision
        ReadResolvedCandidateAuditDecision(
            SourceBackedAgentCompletion completion,
            IReadOnlyList<EvidenceItem> candidates)
    {
        JsonElement arguments;
        JsonDocument? document = null;
        if (completion.ToolCalls.Count == 1
            && string.Equals(
                completion.ToolCalls[0].Name,
                SubmitResolvedCandidateAuditToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            arguments = completion.ToolCalls[0].Arguments;
        }
        else if (completion.ToolCalls.Count == 0
                 && TryParseParentReviewContent(completion.Content, out document))
        {
            arguments = document!.RootElement;
        }
        else
        {
            return InvalidCandidateBatchDecision(
                "resolved_candidate_audit_structured_object_required");
        }

        try
        {
            var usesOrderedDecisions = TryGetPropertyIgnoreCase(
                    arguments,
                    "decisions",
                    out var decisions)
                && decisions.ValueKind == JsonValueKind.Array;
            var usesLegacyCandidateKeys = !usesOrderedDecisions
                && TryGetPropertyIgnoreCase(
                    arguments,
                    "decisionsByCandidateKey",
                    out decisions)
                && decisions.ValueKind == JsonValueKind.Object;
            if (!usesOrderedDecisions && !usesLegacyCandidateKeys)
            {
                return InvalidCandidateBatchDecision(
                    "resolved_candidate_audit_decisions_missing");
            }
            if (usesOrderedDecisions
                && decisions.GetArrayLength() != candidates.Count)
            {
                return InvalidCandidateBatchDecision(
                    "resolved_candidate_audit_decision_count_mismatch");
            }
            if (usesLegacyCandidateKeys)
            {
                var expectedKeys = Enumerable.Range(0, candidates.Count)
                    .Select(CandidateAuditKey)
                    .ToHashSet(StringComparer.Ordinal);
                var returnedKeys = decisions.EnumerateObject()
                    .Select(static property => property.Name)
                    .ToArray();
                if (returnedKeys.Length != expectedKeys.Count
                    || returnedKeys.Any(key => !expectedKeys.Contains(key)))
                {
                    return InvalidCandidateBatchDecision(
                        "resolved_candidate_audit_key_set_mismatch");
                }
            }

            var approvals = new List<CandidateCollectionApproval>();
            var rejected = new List<string>();
            var classifications = new List<CandidateCollectionClassification>();
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                JsonElement decisionElement;
                if (usesOrderedDecisions)
                {
                    decisionElement = decisions[index];
                }
                else if (!TryGetPropertyIgnoreCase(
                             decisions,
                             CandidateAuditKey(index),
                             out decisionElement))
                {
                    return InvalidCandidateBatchDecision(
                        "resolved_candidate_audit_decision_invalid:"
                        + candidate.EvidenceId);
                }
                if (decisionElement.ValueKind != JsonValueKind.String)
                {
                    return InvalidCandidateBatchDecision(
                        "resolved_candidate_audit_decision_invalid:"
                        + candidate.EvidenceId);
                }
                var value = decisionElement.GetString() ?? string.Empty;
                if (!ResolvedCandidateAuditDecisions.Contains(
                        value,
                        StringComparer.Ordinal))
                {
                    return InvalidCandidateBatchDecision(
                        "resolved_candidate_audit_decision_invalid:"
                        + candidate.EvidenceId);
                }
                var label = GetEvidenceDisplayValue(candidate);
                if (string.Equals(
                        value,
                        ResolvedAuditAccept,
                        StringComparison.Ordinal))
                {
                    approvals.Add(new CandidateCollectionApproval(
                        candidate.EvidenceId,
                        label));
                    classifications.Add(new CandidateCollectionClassification(
                        candidate.EvidenceId,
                        NamedSourceItemClassification,
                        label));
                }
                else
                {
                    rejected.Add(candidate.EvidenceId);
                    var classification = value switch
                    {
                        ResolvedAuditRejectAxisOrRole =>
                            AxisOrRoleClassification,
                        ResolvedAuditRejectCategoryOrCollection =>
                            CategoryOrCollectionClassification,
                        ResolvedAuditRejectInstructionOrFragment =>
                            InstructionOrFragmentClassification,
                        _ => OtherWrongTypeClassification
                    };
                    classifications.Add(new CandidateCollectionClassification(
                        candidate.EvidenceId,
                        classification,
                        label));
                }
            }
            return new CandidateCollectionAuditDecision(
                true,
                approvals,
                rejected,
                classifications,
                null);
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static CandidateCollectionAuditDecision
        ApplyExactLayoutAxisIdentityContract(
            CandidateCollectionAuditDecision decision,
            string semanticRowHeader,
            IReadOnlyList<string> semanticRowLabels,
            IReadOnlyList<string> semanticColumnLabels)
    {
        if (!decision.ProtocolValid)
            return decision;

        var normalizedAxes = new[] { semanticRowHeader }
            .Concat(semanticRowLabels)
            .Concat(semanticColumnLabels)
            .Select(SourceBackedStructuredTableShapeBuilder.NormalizeLabel)
            .Where(static value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (normalizedAxes.Count == 0)
            return decision;

        var conflicts = decision.ApprovedCandidates
            .Where(approval => normalizedAxes.Contains(
                SourceBackedStructuredTableShapeBuilder.NormalizeLabel(
                    approval.DisplayValue)))
            .ToArray();
        if (conflicts.Length == 0)
            return decision;

        var conflictIds = conflicts
            .Select(static approval => approval.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new CandidateCollectionAuditDecision(
            true,
            decision.ApprovedCandidates
                .Where(approval => !conflictIds.Contains(approval.EvidenceId))
                .ToArray(),
            decision.RejectedEvidenceIds
                .Concat(conflictIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            decision.Classifications
                .Where(classification =>
                    !conflictIds.Contains(classification.EvidenceId))
                .Concat(conflicts.Select(approval =>
                    new CandidateCollectionClassification(
                        approval.EvidenceId,
                        AxisOrRoleClassification,
                        approval.DisplayValue)))
                .ToArray(),
            null);
    }
}
