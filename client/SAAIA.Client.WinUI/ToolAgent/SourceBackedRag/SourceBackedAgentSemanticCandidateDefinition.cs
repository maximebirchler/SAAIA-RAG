using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SemanticCandidateDefinitionToolName =
        "submit_atomic_candidate_definition";

    private sealed record SemanticCandidateDefinitionOutcome(
        string HypotheticalSinglePositionValue,
        string CandidateObjectType,
        string CandidateEligibilityRule,
        bool Attempted,
        bool ProtocolValid,
        int LlmCallCount,
        int PromptTokens,
        int CompletionTokens,
        string FailureReason);

    private async Task<SemanticCandidateDefinitionOutcome>
        ReviewSemanticCandidateDefinitionAsync(
            SourceBackedIntake intake,
            string semanticPlan,
            string rowHeader,
            IReadOnlyList<string> rowLabels,
            IReadOnlyDictionary<string, string> columnRoles,
            CancellationToken ct)
    {
        var promptTokens = 0;
        var completionTokens = 0;
        var llmCallCount = 0;
        var failureReason = string.Empty;
        SourceBackedAgentCompletion? previousCompletion = null;
        var hypotheticalSinglePositionValue = string.Empty;
        var candidateObjectType = string.Empty;
        var candidateEligibilityRule = string.Empty;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var messages = BuildSemanticCandidateDefinitionMessages(
                intake,
                semanticPlan,
                rowHeader,
                rowLabels,
                columnRoles,
                attempt,
                failureReason,
                previousCompletion);
            var maximumTokens = Math.Clamp(
                _options.MaximumSemanticColumnRoleTokens,
                128,
                320);
            var completion = _llm is ISourceBackedAgentStructuredLlmClient
                structuredLlm
                ? await structuredLlm.CompleteStructuredAsync(
                        messages,
                        BuildSemanticCandidateDefinitionContract(),
                        maximumTokens,
                        ct,
                        temperatureOverride: 0)
                    .ConfigureAwait(false)
                : await _llm.CompleteAsync(
                        messages,
                        new[]
                        {
                            BuildSemanticCandidateDefinitionTool()
                        },
                        maximumTokens,
                        ct,
                        temperatureOverride: 0,
                        requireToolCall: true)
                    .ConfigureAwait(false);
            llmCallCount++;
            promptTokens += completion.PromptTokens.GetValueOrDefault();
            completionTokens += completion.CompletionTokens.GetValueOrDefault();
            if (TryReadSemanticCandidateDefinition(
                    completion,
                    out hypotheticalSinglePositionValue,
                    out candidateObjectType,
                    out candidateEligibilityRule,
                    out failureReason))
            {
                break;
            }

            previousCompletion = completion;
        }

        if (string.IsNullOrWhiteSpace(candidateObjectType)
            || string.IsNullOrWhiteSpace(candidateEligibilityRule))
        {
            return new SemanticCandidateDefinitionOutcome(
                string.Empty,
                string.Empty,
                string.Empty,
                Attempted: true,
                ProtocolValid: false,
                LlmCallCount: llmCallCount,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                FailureReason: failureReason);
        }

        return new SemanticCandidateDefinitionOutcome(
            hypotheticalSinglePositionValue,
            candidateObjectType,
            candidateEligibilityRule,
            Attempted: true,
            ProtocolValid: true,
            LlmCallCount: llmCallCount,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            FailureReason: string.Empty);
    }

    private static SemanticCandidateDefinitionOutcome
        CreateUnusedSemanticCandidateDefinitionOutcome()
        => new(
            string.Empty,
            string.Empty,
            string.Empty,
            Attempted: false,
            ProtocolValid: true,
            LlmCallCount: 0,
            PromptTokens: 0,
            CompletionTokens: 0,
            FailureReason: string.Empty);

    private static string BuildCandidateDefinitionAuditRule(
        SemanticCandidateDefinitionOutcome outcome)
        => outcome.CandidateEligibilityRule;

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildSemanticCandidateDefinitionMessages(
            SourceBackedIntake intake,
        string semanticPlan,
        string rowHeader,
        IReadOnlyList<string> rowLabels,
        IReadOnlyDictionary<string, string> columnRoles,
            int attempt,
            string failureReason,
            SourceBackedAgentCompletion? previousCompletion)
    {
        var user = BuildSemanticCandidateDefinitionContext(
            intake,
            semanticPlan,
            rowHeader,
            rowLabels,
            columnRoles);
        if (attempt > 1)
        {
            user.Append("REPARATION_MECANIQUE: ")
                .AppendLine(TrimPromptValue(failureReason, 160));
            if (previousCompletion is not null)
            {
                user.Append("SORTIE_PRECEDENTE: ")
                    .AppendLine(TrimPromptValue(
                        RenderSemanticPlanCompletion(previousCompletion),
                        360));
            }
        }
        user.Append("Appelle uniquement ")
            .Append(SemanticCandidateDefinitionToolName)
            .Append('.');

        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu definis le type d'objet source atomique d'un livrable avant toute recherche. "
                + "Tu ne choisis aucun outil documentaire, aucune requete, aucun "
                + "perimetre et aucun quota. Determine UNE description concise du type "
                + "d'element nomme qu'un passage documentaire peut fournir pour contribuer "
                + "a une cellule. Ce type doit couvrir les valeurs legitimes des differentes "
                + "colonnes sans les confondre avec leurs roles. Definis l'objet tel qu'il "
                + "existe et est titre dans la source AVANT son placement dans le livrable, "
                + "jamais comme une cellule, un creneau, une position ou un role final. "
                + "Prefere l'objet source concret, reutilisable et susceptible d'avoir sa "
                + "propre fiche, rubrique ou section dans des documents heterogenes, plutot "
                + "qu'une reformulation abstraite de l'usage final. Si le plan prealable "
                + "decrit encore un role du livrable, affine-le vers le type documentaire "
                + "nomme que la recherche peut reellement retrouver. "
                + "Distingue explicitement l'unite de reponse de l'unite documentaire: "
                + "dans un plan, une liste, un tableau ou un calendrier, le membre, le "
                + "creneau ou l'occurrence demandee est une place a remplir, pas le type "
                + "de preuve. Cherche le type d'entite autonome dont un nom precis pourrait "
                + "etre copie comme valeur de cette place et dont la source pourrait porter "
                + "le titre. Commence par proposer dans hypotheticalSinglePositionValue UNE "
                + "valeur precise, autonome et nommee qui pourrait remplir exactement UNE "
                + "seule position du livrable ET etre recopiee comme le titre exact d'UNE "
                + "fiche ou section provenant d'un seul EvidenceId. Ce brouillon ne doit "
                + "assembler ni additionner plusieurs composants, suggestions ou titres "
                + "independants. Il ne doit jamais etre le titre du "
                + "livrable entier, une periode, une ligne, une colonne, un role, une categorie "
                + "ou une combinaison de plusieurs positions. Il n'est ni une preuve, ni une "
                + "requete, ni une valeur finale et sera jete immediatement apres cette decision. "
                + "Il ne doit contenir aucun libelle d'axe. Calibration generique du brouillon: "
                + "pour un calendrier de cours, une position pourrait contenir 'Algebre lineaire', "
                + "pas 'calendrier hebdomadaire'; pour un tableau d'equipements, une position "
                + "pourrait contenir 'Modele AX-17', pas 'tableau comparatif'. Deduis ensuite "
                + "candidateObjectType du genre editorial ou catalogue de cet objet source "
                + "concret, en tenant compte des perimetres de catalogue affiches, sans "
                + "reprendre le livrable ni ses roles. "
                + "COUVERTURE DE TOUS LES ROLES: candidateObjectType et la regle "
                + "d'eligibilite doivent pouvoir reconnaitre une instance source legitime "
                + "pour chacun des roles de colonne affiches, et pas seulement pour le "
                + "premier ou le plus saillant. Teste silencieusement une valeur source "
                + "exacte possible par role avant de choisir le type commun concret; retourne "
                + "seulement un brouillon representatif dans hypotheticalSinglePositionValue. "
                + "Si le type choisi exclut un role legitime, remonte juste assez vers leur "
                + "genre documentaire commun sans devenir 'element', 'contenu' ou 'information'. "
                + "Applique aussi ce test avant de repondre: 'un document peut-il avoir "
                + "une fiche ou une section consacree a UNE instance precise de ce type, "
                + "independamment du livrable demande ?'. Si non, descends vers l'objet "
                + "documentaire concret sous-jacent. "
                + "Calibration structurelle generique: pour un calendrier de cours, "
                + "l'objet est le cours documente, pas le creneau quotidien; pour un "
                + "tableau comparatif, l'objet est l'element compare, pas la case du "
                + "tableau. Ces contrastes expliquent seulement la distinction de niveau; "
                + "ils ne fournissent aucune valeur a recopier. "
                + "Le champ candidateObjectType doit etre un groupe nominal concret et "
                + "singulier. Il ne peut reprendre, composer ni deriver aucun mot d'un "
                + "en-tete, d'une ligne ou d'une colonne du livrable. Avant de repondre, "
                + "verifie cette contrainte; si ton premier type ressemble encore a un axe "
                + "ou a son usage, remplace-le par l'objet source sous-jacent. N'utilise "
                + "aucun libelle de ligne ou de colonne comme exemple d'instance. Une preuve atomique n'a pas "
                + "a constituer seule toute la cellule finale et peut etre plus simple qu'une "
                + "proposition assemblee. Formule aussi une regle d'eligibilite autonome: le "
                + "libelle exact doit nommer une instance du type cible, jamais le livrable, "
                + "un axe, une periode, un role, une categorie, une collection, une rubrique, "
                + "une consigne, une sous-partie ou une etape. Le brouillon hypothetique sert "
                + "uniquement a raisonner et ne doit jamais etre presente comme une valeur "
                + "trouvee. Ne reponds pas a l'utilisateur."),
            SourceBackedAgentMessage.User(user.ToString().Trim())
        };
    }

    private static StringBuilder BuildSemanticCandidateDefinitionContext(
        SourceBackedIntake intake,
        string semanticPlan,
        string rowHeader,
        IReadOnlyList<string> rowLabels,
        IReadOnlyDictionary<string, string> columnRoles)
    {
        var firstRow = rowLabels.FirstOrDefault() ?? rowHeader;
        var deliverableDescription = semanticPlan
            .Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(
                "LIVRABLE:",
                StringComparison.OrdinalIgnoreCase));
        return new StringBuilder()
            .AppendLine("DEMANDE_ORIGINALE:")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 700))
            .AppendLine("DESCRIPTION_DU_LIVRABLE (orientation, pas type de preuve):")
            .AppendLine(TrimPromptValue(
                deliverableDescription ?? semanticPlan,
                320))
            .AppendLine("PERIMETRES_DU_CATALOGUE (contexte documentaire, pas valeurs):")
            .AppendLine(string.Join(
                " | ",
                SourceBackedRetrievalScope
                    .GetAvailableCategoryPaths(intake)
                    .Select(static path => TrimPromptValue(path, 100))))
            .Append("AXE_LIGNES (coordonnees seulement): ")
            .Append(rowHeader)
            .Append(" = ")
            .AppendLine(string.Join(" | ", rowLabels))
            .Append("AXE_COLONNES (coordonnees seulement): ")
            .AppendLine(string.Join(" | ", columnRoles.Keys))
            .AppendLine("ROLES_SEMANTIQUES_DES_COLONNES (frontieres a couvrir):")
            .AppendLine(string.Join(
                Environment.NewLine,
                columnRoles.Select(static pair =>
                    "- " + TrimPromptValue(pair.Key, 60)
                    + ": " + TrimPromptValue(pair.Value, 220))))
            .Append("LIGNE_DE_REFERENCE: ")
            .AppendLine(firstRow);
    }

    private static SourceBackedAgentToolDefinition
        BuildSemanticCandidateDefinitionTool()
        => new(
            SemanticCandidateDefinitionToolName,
            "Definit le type d'objet source atomique et sa regle d'eligibilite; ne choisit aucun outil documentaire.",
            BuildSemanticCandidateDefinitionSchema());

    private static LlmStructuredOutputContract
        BuildSemanticCandidateDefinitionContract()
        => new(
            "source_backed_atomic_candidate_definition_v7",
            BuildSemanticCandidateDefinitionSchema());

    private static JsonElement BuildSemanticCandidateDefinitionSchema()
        => JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    hypotheticalSinglePositionValue = new
                    {
                        type = "string",
                        description =
                            "Titre exact hypothetique d'UNE fiche ou section source pouvant remplir UNE seule position et etre prouve par un EvidenceId; aucune combinaison de composants; jamais livrable entier, periode, role ou axe; brouillon ephemere.",
                        minLength = 2,
                        maxLength = 160
                    },
                    candidateObjectType = new
                    {
                        type = "string",
                        description =
                            "Genre editorial ou catalogue concret de l'objet source hypothetique dont une instance precise a sa propre fiche ou section; jamais classe d'usage, place, role ou occurrence demandee.",
                        minLength = 2,
                        maxLength = 160
                    },
                    candidateEligibilityRule = new
                    {
                        type = "string",
                        description =
                            "Regle pour reconnaitre une instance source autonome du type cible sans utiliser les libelles d'axes comme exemples.",
                        minLength = 8,
                        maxLength = 320
                    }
                },
                required = new[]
                {
                    "hypotheticalSinglePositionValue",
                    "candidateObjectType",
                    "candidateEligibilityRule"
                },
                additionalProperties = false
            }, ClientJson.CamelCase);

    private static bool TryReadSemanticCandidateDefinition(
        SourceBackedAgentCompletion completion,
        out string hypotheticalSinglePositionValue,
        out string candidateObjectType,
        out string candidateEligibilityRule,
        out string failureReason)
    {
        hypotheticalSinglePositionValue = string.Empty;
        candidateObjectType = string.Empty;
        candidateEligibilityRule = string.Empty;
        failureReason = string.Empty;
        if (!TryReadSemanticCandidateArguments(
                completion,
                SemanticCandidateDefinitionToolName,
                out var arguments,
                out var structuredDocument))
        {
            failureReason = "candidate_examples_single_tool_call_required";
            return false;
        }

        using (structuredDocument)
        {
        hypotheticalSinglePositionValue = NormalizeSemanticPlanLine(
            GetString(arguments, "hypotheticalSinglePositionValue") ?? string.Empty);
        candidateObjectType = NormalizeSemanticPlanLine(
            GetString(arguments, "candidateObjectType") ?? string.Empty);
        candidateEligibilityRule = NormalizeSemanticPlanLine(
            GetString(arguments, "candidateEligibilityRule") ?? string.Empty);
        if (hypotheticalSinglePositionValue.Length is < 2 or > 160
            || candidateObjectType.Length is < 2 or > 160
            || candidateEligibilityRule.Length is < 8 or > 320)
        {
            failureReason = "candidate_definition_values_invalid";
            return false;
        }
        return true;
        }
    }

    private static bool TryReadSemanticCandidateArguments(
        SourceBackedAgentCompletion completion,
        string expectedToolName,
        out JsonElement arguments,
        out JsonDocument? structuredDocument)
    {
        arguments = default;
        structuredDocument = null;
        if (completion.ToolCalls.Count == 1
            && string.Equals(
                completion.ToolCalls[0].Name,
                expectedToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            arguments = completion.ToolCalls[0].Arguments;
            return true;
        }
        if (completion.ToolCalls.Count != 0
            || string.IsNullOrWhiteSpace(completion.Content))
        {
            return false;
        }

        try
        {
            structuredDocument = JsonDocument.Parse(completion.Content);
            arguments = structuredDocument.RootElement;
            return arguments.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            structuredDocument?.Dispose();
            structuredDocument = null;
            arguments = default;
            return false;
        }
    }

    private void AddSemanticCandidateDefinitionTrace(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        SemanticCandidateDefinitionOutcome outcome)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Planner,
            "source_backed_agent_v2.semantic_candidate_definition.completed",
            ("attempted", outcome.Attempted),
            ("protocol_valid", outcome.ProtocolValid),
            ("hypothetical_single_position_value", outcome.HypotheticalSinglePositionValue),
            ("hypothetical_value_usage", "ephemeral_reasoning_only"),
            ("candidate_object_type", outcome.CandidateObjectType),
            ("candidate_eligibility_rule", outcome.CandidateEligibilityRule),
            ("llm_calls", outcome.LlmCallCount),
            ("prompt_tokens", outcome.PromptTokens),
            ("completion_tokens", outcome.CompletionTokens),
            ("failure_reason", outcome.FailureReason)));
}
