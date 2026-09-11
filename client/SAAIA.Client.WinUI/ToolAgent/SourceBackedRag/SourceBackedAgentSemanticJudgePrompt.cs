using System.Text.Json;

// LLM-owned semantic review protocol; domain examples belong to its prompt.
namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    internal sealed record SemanticReview(
        string Decision,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<string> RejectedEvidenceIds,
        IReadOnlyList<string> PreferredAlternativeEvidenceIds,
        string RawOutput,
        int? PromptTokens = null,
        int? CompletionTokens = null,
        string? FinishReason = null,
        int Attempts = 1,
        bool TruncationRetryExhausted = false,
        bool ContractAdjusted = false,
        bool ContextRecoveryUsed = false,
        string EvidenceContextMode = "unknown",
        int? ExactInputTokens = null,
        int CitedEvidenceCharacters = 0)
    {
        public bool IsAccepted =>
            string.Equals(Decision, "accept", StringComparison.OrdinalIgnoreCase);
    }

    internal async Task<SemanticReview> ReviewSemanticsAsync(
        SourceBackedIntake intake,
        string semanticPlan,
        WriterDraft draft,
        EvidenceBundle bundle,
        IReadOnlyList<string> observedEvidenceIds,
        CancellationToken ct)
    {
        var request = await PrepareSemanticReviewRequestAsync(
                intake,
                semanticPlan,
                draft,
                bundle,
                observedEvidenceIds,
                ct)
            .ConfigureAwait(false);
        var messages = request.Messages;
        var reviewTools = request.Tools;
        var tokenBudget = request.MaximumOutputTokens;
        var contextRecoveryUsed = request.ContextRecoveryUsed;
        var evidenceContextMode = request.EvidenceContextMode;
        var exactInputTokens = request.ExactInputTokens;
        var citedEvidenceCharacters = request.CitedEvidenceCharacters;
        SourceBackedAgentCompletion? completion = null;
        SemanticReview? review = null;
        var attempts = 0;
        var promptTokens = 0;
        var completionTokens = 0;
        var hasPromptTokenCount = false;
        var hasCompletionTokenCount = false;
        var truncationRetryExhausted = false;
        while (attempts < 2)
        {
            attempts++;
            try
            {
                completion = await _llm.CompleteAsync(
                        messages,
                        reviewTools,
                        tokenBudget,
                        ct,
                        _options.SemanticReviewTemperature,
                        requireToolCall: true)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (
                attempts < 2
                && IsContextWindowExceeded(ex))
            {
                contextRecoveryUsed = true;
                tokenBudget = Math.Min(tokenBudget, 512);
                messages = BuildSemanticJudgeMessages(
                    intake,
                    semanticPlan,
                    draft,
                    bundle,
                    observedEvidenceIds,
                    constrainedContext: true,
                    emergencyContextRecovery: true,
                    includeCompleteCitedEvidence: true);
                evidenceContextMode = "http_context_emergency_fallback";
                continue;
            }
            if (completion.PromptTokens is { } currentPromptTokens)
            {
                promptTokens += currentPromptTokens;
                hasPromptTokenCount = true;
            }
            if (completion.CompletionTokens is { } currentCompletionTokens)
            {
                completionTokens += currentCompletionTokens;
                hasCompletionTokenCount = true;
            }

            var submission = completion.ToolCalls.FirstOrDefault(static call =>
                string.Equals(
                    call.Name,
                    "submit_semantic_review",
                    StringComparison.OrdinalIgnoreCase));
            if (submission is not null)
            {
                review = ParseSemanticReview(submission.Arguments.GetRawText());
                break;
            }

            var truncated = string.Equals(
                completion.FinishReason,
                "length",
                StringComparison.OrdinalIgnoreCase);
            if (!truncated || attempts >= 2)
            {
                review = ParseSemanticReview(completion.Content);
                truncationRetryExhausted = truncated;
                break;
            }

            tokenBudget = Math.Min(1200, Math.Max(tokenBudget * 2, 768));
            messages = BuildSemanticJudgeRetryMessages(messages);
        }

        completion ??= new SourceBackedAgentCompletion(
            string.Empty,
            Array.Empty<SourceBackedAgentToolCall>(),
            "protocol_error");
        review ??= ParseSemanticReview(completion.Content);
        var citedIds = SourceContractVerifier
            .ExtractEvidenceIds(draft.Answer)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observedIds = observedEvidenceIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalized = review with
        {
            RejectedEvidenceIds = review.RejectedEvidenceIds
                .Where(citedIds.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray(),
            PreferredAlternativeEvidenceIds = review.PreferredAlternativeEvidenceIds
                .Where(id => observedIds.Contains(id) && !citedIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray(),
            PromptTokens = hasPromptTokenCount ? promptTokens : null,
            CompletionTokens = hasCompletionTokenCount ? completionTokens : null,
            FinishReason = completion.FinishReason,
            Attempts = attempts,
            TruncationRetryExhausted = truncationRetryExhausted,
            ContextRecoveryUsed = contextRecoveryUsed,
            EvidenceContextMode = evidenceContextMode,
            ExactInputTokens = exactInputTokens,
            CitedEvidenceCharacters = citedEvidenceCharacters
        };
        return EnforceSemanticReviewContract(normalized);
    }

    private static IReadOnlyList<SourceBackedAgentMessage> BuildSemanticJudgeRetryMessages(
        IReadOnlyList<SourceBackedAgentMessage> messages)
    {
        if (messages.Count == 0)
            return messages;

        var repaired = messages.ToArray();
        var last = repaired[^1];
        repaired[^1] = SourceBackedAgentMessage.User(
            last.Content
            + """


              REPARATION DE PROTOCOLE:
              La tentative precedente a ete tronquee avant l'appel d'outil.
              Appelle maintenant submit_semantic_review. Garde reasons concis et
              regroupe les EvidenceId invalides dans les listes prevues.
              """);
        return repaired;
    }

    private static IReadOnlyList<SourceBackedAgentToolDefinition> BuildSemanticReviewSubmissionTool()
        => new[]
        {
            new SourceBackedAgentToolDefinition(
                "submit_semantic_review",
                "Soumet la decision du juge semantique et ses justifications precises. Cet outil ne recherche rien et ne modifie aucune preuve.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        decision = new
                        {
                            type = "string",
                            @enum = new[] { "accept", "revise", "need_more_evidence" }
                        },
                        reasons = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "string",
                                maxLength = 240
                            },
                            minItems = 1,
                            maxItems = 4
                        },
                        rejectedEvidenceIds = new
                        {
                            type = "array",
                            description = "EvidenceId cites dont le contenu lui-meme est invalide pour la demande. Vide si accept ou si seule la formulation ou l'affectation est a revoir.",
                            items = new
                            {
                                type = "string",
                                pattern = "^E[0-9]{1,4}$"
                            },
                            uniqueItems = true,
                            maxItems = 20
                        },
                        preferredAlternativeEvidenceIds = new
                        {
                            type = "array",
                            description = "EvidenceId alternatifs deja affiches que le juge recommande pour remplacer les valeurs rejetees.",
                            items = new
                            {
                                type = "string",
                                pattern = "^E[0-9]{1,4}$"
                            },
                            uniqueItems = true,
                            maxItems = 20
                        }
                    },
                    required = new[]
                    {
                        "decision",
                        "reasons",
                        "rejectedEvidenceIds",
                        "preferredAlternativeEvidenceIds"
                    },
                    additionalProperties = false
                }, ClientJson.CamelCase))
        };

    private static IReadOnlyList<SourceBackedAgentMessage> BuildSemanticJudgeMessages(
        SourceBackedIntake intake,
        string semanticPlan,
        WriterDraft draft,
        EvidenceBundle bundle,
        IReadOnlyList<string> observedEvidenceIds,
        bool constrainedContext,
        bool emergencyContextRecovery,
        bool includeCompleteCitedEvidence)
        => new[]
        {
            SourceBackedAgentMessage.System(
                BuildSemanticJudgeSystemPrompt(
                    constrainedContext,
                    ReadAtomicEvidenceMode(semanticPlan))),
            SourceBackedAgentMessage.User(BuildSemanticJudgeContext(
                intake,
                semanticPlan,
                draft,
                bundle,
                observedEvidenceIds,
                constrainedContext,
                emergencyContextRecovery,
                includeCompleteCitedEvidence))
        };

    private static string BuildSemanticJudgeSystemPrompt(
        bool constrainedContext,
        string atomicEvidenceMode)
    {
        if (string.Equals(
                atomicEvidenceMode,
                "content_claim",
                StringComparison.OrdinalIgnoreCase))
        {
            return BuildContentClaimSemanticJudgeSystemPrompt(
                constrainedContext);
        }

        if (constrainedContext)
        {
            return """
                   Tu es le juge semantique independant d'un agent RAG. Le controle
                   mecanique garantit seulement l'existence des EvidenceId. Audite chaque
                   valeur citee puis son affectation, selon la demande explicite uniquement.

                   Une qualite explicite est un critere semantique: evalue-la dans toute la
                   preuve, sans exiger le meme mot ni inventer un seuil quantitatif comme proxy.
                   Une valeur doit etre une instance nommee, complete et utilisable de la
                   classe demandee. Refuse composant, instruction, rubrique, categorie,
                   conseil vague, libelle generique, OCR incoherent et doublon. Un titre
                   explicite suffit si la demande n'exige aucun attribut supplementaire.
                   Deux cartes d'une page restent distinctes par titre et ContentCardId.

                   Pour une grille, axes, jours, colonnes et affectations ne sont pas des
                   faits documentaires. Chaque cellule cite seulement son contenu; la source
                   ne doit pas nommer sa case. Verifie que le placement reste plausible.
                   Une mauvaise place exige revise sans rejeter une valeur utilisable ailleurs.
                   Si aucun arrangement ne couvre un axe, choisis need_more_evidence.

                   accept: toutes les valeurs sont distinctes, plausibles et soutenues; les
                   deux listes sont vides.
                   revise: tout est corrigeable avec les preuves affichees; place les valeurs
                   invalides dans rejectedEvidenceIds et autant de remplacements visibles
                   dans preferredAlternativeEvidenceIds. Ne rejette pas pour une simple place.
                   need_more_evidence: il manque des valeurs ou remplacements precis.

                   reasons contient 1 a 4 motifs concis et le nombre exact d'instances valides.
                   Pour N instances, revise seulement si au moins N valeurs valides restent
                   affichees; sinon demande plus de preuves. Appelle exactement
                   submit_semantic_review, sans texte ni autre outil.
                   """;
        }

        return """
               Tu es le juge semantique independant d'un agent RAG. La verification
               mecanique confirme seulement que les EvidenceId existent. Juge la demande
               explicite sans inventer de contrainte.

               Les qualites demandees comme simple, facile, accessible, technique ou
               economique sont des criteres semantiques. Evalue-les d'apres l'ensemble de la
               preuve sans exiger le mot exact et sans les remplacer par un seuil absent
               (nombre d'ingredients, duree, prix, calories ou autre proxy quantitatif).

               Pour chaque valeur concrete: exige une instance complete et utilisable,
               directement soutenue par son extrait. Refuse composant isole, instruction,
               rubrique, categorie, conseil vague, libelle generique ou OCR incoherent
               employe comme valeur. Refuse les doublons qui simulent la couverture.
               Une quantite, une mesure ou une preuve structuree confirme l'ancrage, pas
               la nature semantique du titre. Commence par auditer une par une toutes les
               valeurs citees; ne deduis jamais leur validite du seul total ou de la
               presence d'une source. Evalue ensuite chaque affectation par rapport a son
               libelle de ligne et de colonne.
               Un titre explicitement nomme reste une instance valide de la classe
               demandee meme s'il est court ou accompagne d'un extrait bref. N'exige
               aucun attribut absent de la demande. Deux cartes nommees peuvent partager
               une page ou une partie d'extrait: leur ChunkId et leur titre les
               distinguent; n'en deduis pas qu'elles sont identiques sans contradiction.

               Pour une grille, axes, jours, colonnes et affectations ne sont pas des faits
               documentaires. Chaque cellule doit seulement citer la preuve de son contenu.
               Une grille R x C est complete avec R x C valeurs distinctes lorsque la
               demande exige la distinction, clairement nommees, plausiblement affectees
               et citees. La source n'a pas a mentionner les libelles de placement.
               L'identite explicite d'une instance suffit si la demande n'exige pas
               d'attributs supplementaires.
               La source n'a pas a nommer l'affectation, mais ton jugement professionnel
               doit verifier qu'elle reste plausible. Une instance valide mais mal placee
               exige une revision d'ordre, sans rejet de son EvidenceId si elle peut
               servir ailleurs. Si aucun arrangement plausible ne couvre un axe explicite,
               demande davantage de preuves au lieu d'accepter un remplissage force.

               accept: livrable professionnel, complet, utilisable et soutenu.
               revise: toutes les corrections sont possibles avec les preuves affichees;
               place dans rejectedEvidenceIds chaque preuve citee dont la valeur elle-meme
               est invalide, et dans preferredAlternativeEvidenceIds les remplacements deja
               affiches. Ne rejette pas une preuve valide pour une simple affectation de case.
               Si un motif dit qu'une valeur n'est pas une instance valide ou qu'elle duplique
               une autre valeur, rejectedEvidenceIds ne peut pas etre vide: choisis et inscris
               tous les EvidenceId a remplacer. Pour un doublon, conserve semantiquement au
               plus une occurrence valide et rejette les autres.
               need_more_evidence: il manque des composants ou des alternatives precises.
               Avec accept, les deux listes d'EvidenceId sont vides.

               reasons est obligatoire et concis: quatre motifs maximum, 25 mots chacun;
               regroupe les anomalies semblables. Pour N instances, indique le nombre exact
               d'instances completes et distinctes verifiees et nomme les valeurs refusees.
               Choisis revise seulement si, apres exclusion des valeurs invalides et des
               doublons, les preuves affichees contiennent encore au moins N instances
               completes et distinctes. Sinon choisis need_more_evidence afin que
               l'orchestrateur LLM puisse rechercher de meilleures preuves.
               Appelle exactement submit_semantic_review, sans texte ni autre outil.
               """;
    }

}
