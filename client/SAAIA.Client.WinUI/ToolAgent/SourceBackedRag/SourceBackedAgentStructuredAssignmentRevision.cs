using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SubmitStructuredAssignmentRevisionToolName =
        "submit_structured_assignment_revision";
    private const string StructuredAssignmentLinePattern =
        @"(?m)^[ \t]*C(\d{2,})[ \t]*=[ \t]*(E\d+)[ \t]*\r?$";

    private static string BuildStructuredAssignmentRevisionFeedback(
        SourceBackedIntake intake,
        SemanticReview review,
        WriterDraft rejectedDraft,
        IReadOnlyList<string> candidateEvidenceIds)
    {
        var claims = SourceContractVerifier
            .ExtractRequestedStructuredCellClaims(rejectedDraft.Answer, intake)
            .ToArray();
        var decisions = Regex.Matches(
                review.RawOutput ?? string.Empty,
                @"(?m)^C(\d{2,})=(ACCEPT|REJECT):",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => new
            {
                ClaimIndex = int.Parse(match.Groups[1].Value) - 1,
                ClaimRef = "C" + match.Groups[1].Value,
                Accepted = string.Equals(
                    match.Groups[2].Value,
                    "ACCEPT",
                    StringComparison.OrdinalIgnoreCase)
            })
            .Where(decision => decision.ClaimIndex >= 0
                               && decision.ClaimIndex < claims.Length)
            .ToArray();
        var rejectedClaimIndexes = decisions
            .Where(static decision => !decision.Accepted)
            .Select(static decision => decision.ClaimIndex)
            .ToHashSet();
        var lockedEvidenceIds = claims
            .Where((_, index) => !rejectedClaimIndexes.Contains(index))
            .SelectMany(static claim => claim.EvidenceIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableEvidenceIds = candidateEvidenceIds
            .Where(id => !lockedEvidenceIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rejectedClaimRefs = decisions
            .Where(static decision => !decision.Accepted)
            .Select(static decision => decision.ClaimRef)
            .ToArray();
        return $"""
            REVUE SEMANTIQUE INDEPENDANTE DU TABLEAU: {review.Decision}

            TABLEAU PRECEDENT A REVISER:
            {TrimPromptBlock(rejectedDraft.Answer, 1800)}

            DECISION COMPLETE POUR CHAQUE CELLULE, DANS L'ORDRE DU TABLEAU:
            {TrimPromptBlock(review.RawOutput, 1800)}

            CELLULES A MODIFIER: {string.Join(", ", rejectedClaimRefs)}
            EVIDENCEIDS VERROUILLES PAR LES CELLULES ACCEPT: {string.Join(", ", lockedEvidenceIds.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase))}
            EVIDENCEIDS DISPONIBLES POUR LES CELLULES REJECT: {string.Join(", ", availableEvidenceIds)}

            CONTRAT DE REVISION:
            - conserve exactement la valeur et l'EvidenceId de chaque cellule ACCEPT;
            - modifie uniquement les cellules REJECT;
            - pour une cellule REJECT, utilise exclusivement un EvidenceId de la liste DISPONIBLES
              ci-dessus et choisis sa position plausible selon la ligne, la colonne et le role
              semantique de cette colonne;
            - ne deplace pas une valeur acceptee et ne cree aucun doublon;
            - la revision suivante doit produire uniquement un patch CXX=E# pour les cellules REJECT.
            """;
    }

    private static string BuildStructuredAssignmentEvidenceGapFeedback(
        SourceBackedIntake intake,
        WriterDraft rejectedDraft,
        SemanticReview review,
        int rejectedCellCount,
        int unselectedCandidateCount,
        int repeatedRejectedCellCount,
        int candidatePoolTargetCount,
        string? orchestratorResearchNeed)
    {
        var claims = SourceContractVerifier
            .ExtractRequestedStructuredCellClaims(rejectedDraft.Answer, intake)
            .ToArray();
        var rejectedIndexes = GetStructuredAssignmentRejectedCellIndexes(review)
            ?? new HashSet<int>();
        var remainingNeed = rejectedIndexes
            .Where(index => index >= 0 && index < claims.Length)
            .OrderBy(static index => index)
            .Select(index =>
            {
                var claim = claims[index];
                var role = intake.CanonicalColumnSemanticRoles is not null
                           && intake.CanonicalColumnSemanticRoles.TryGetValue(
                               claim.ColumnHeader,
                               out var semanticRole)
                    ? semanticRole
                    : claim.ColumnHeader;
                return "- " + claim.ClaimRef
                       + " | ligne=" + TrimPromptValue(claim.RowLabel, 60)
                       + " | colonne=" + TrimPromptValue(claim.ColumnHeader, 60)
                       + " | role=" + TrimPromptValue(role, 180)
                       + " | valeur_refusee=" + TrimPromptValue(claim.ClaimText, 120)
                       + " " + string.Join(' ', claim.EvidenceIds.Select(static id => "[" + id + "]"));
            })
            .ToArray();
        var remainingNeedBlock = remainingNeed.Length == 0
            ? "- details indisponibles; utilise les motifs du juge ci-dessous"
            : string.Join(Environment.NewLine, remainingNeed);

        return $"""
            REVUE SEMANTIQUE INDEPENDANTE DU TABLEAU: {review.Decision}

            BESOIN SEMANTIQUE RESTANT, DECIDE PAR LE JUGE LLM:
            {remainingNeedBlock}

            CELLULES REFUSEES: {rejectedCellCount}
            CANDIDATS NON ENCORE UTILISES: {unselectedCandidateCount}
            CELLULES ENCORE REFUSEES APRES UNE REVISION LOCALE: {repeatedRejectedCellCount}
            CIBLE MECANIQUE DE COLLECTE: {candidatePoolTargetCount} candidats citables distincts

            MOTIFS DU JUGE LLM:
            {TrimPromptBlock(string.Join(" | ", review.Reasons), 1200)}

            BESOIN DE RECHERCHE FORMULE PAR L'ORCHESTRATEUR:
            {TrimPromptBlock(orchestratorResearchNeed, 900)}

            Le juge LLM a refuse les affectations ci-dessus. La phase documentaire est rouverte
            afin que l'orchestrateur choisisse lui-meme comment obtenir de meilleurs candidats.
            Reprends la collecte avec les outils exposes selon la demande, les preuves deja
            observees, les refus ci-dessus et les actions deja consommees. Ne redige pas et ne
            reaffecte pas encore la grille.
            """;
    }

    private static int AccumulateStructuredAssignmentRejections(
        SourceBackedIntake intake,
        WriterDraft draft,
        SemanticReview review,
        IDictionary<int, HashSet<string>> rejectedEvidenceIdsByCell)
    {
        if (!string.Equals(
                review.FinishReason,
                StructuredAssignmentBatchedReviewFinishReason,
                StringComparison.OrdinalIgnoreCase))
        {
            return rejectedEvidenceIdsByCell.Sum(static pair => pair.Value.Count);
        }
        var claims = SourceContractVerifier
            .ExtractRequestedStructuredCellClaims(draft.Answer, intake)
            .ToArray();
        var rejectedIndexes = Regex.Matches(
                review.RawOutput ?? string.Empty,
                @"(?m)^C(\d{2,})=REJECT:",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => int.Parse(match.Groups[1].Value) - 1)
            .Where(index => index >= 0 && index < claims.Length)
            .Distinct()
            .ToArray();
        foreach (var rejectedIndex in rejectedIndexes)
        {
            if (!rejectedEvidenceIdsByCell.TryGetValue(
                    rejectedIndex,
                    out var rejectedEvidenceIds))
            {
                rejectedEvidenceIds = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                rejectedEvidenceIdsByCell[rejectedIndex] = rejectedEvidenceIds;
            }
            foreach (var evidenceId in claims[rejectedIndex].EvidenceIds)
                rejectedEvidenceIds.Add(evidenceId);
        }
        return rejectedEvidenceIdsByCell.Sum(static pair => pair.Value.Count);
    }

    private async Task<StructuredAssignmentRevisionExecution>
        CompleteStructuredAssignmentRevisionAsync(
            SourceBackedIntake intake,
            EvidenceBundle bundle,
            SemanticReview review,
            WriterDraft rejectedDraft,
            IReadOnlyList<string> candidateEvidenceIds,
            IReadOnlyDictionary<int, HashSet<string>>
                rejectedEvidenceIdsByCell,
            CancellationToken ct)
    {
        var shape = SourceBackedStructuredTableShapeBuilder.TryCreateFromIntake(intake);
        var claims = SourceContractVerifier
            .ExtractRequestedStructuredCellClaims(rejectedDraft.Answer, intake)
            .ToArray();
        var decisions = Regex.Matches(
                review.RawOutput ?? string.Empty,
                @"(?m)^C(\d{2,})=(ACCEPT|REJECT):",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => new
            {
                Index = int.Parse(match.Groups[1].Value) - 1,
                Accepted = string.Equals(
                    match.Groups[2].Value,
                    "ACCEPT",
                    StringComparison.OrdinalIgnoreCase)
            })
            .GroupBy(static decision => decision.Index)
            .ToDictionary(static group => group.Key, static group => group.Last().Accepted);
        if (shape is null
            || claims.Length == 0
            || decisions.Count == 0
            || decisions.Keys.Any(index => index < 0 || index >= claims.Length))
        {
            return FailedStructuredAssignmentRevision(
                "La matrice ACCEPT/REJECT de la revision structuree est incomplete.");
        }

        var rejectedIndexes = decisions
            .Where(static pair => !pair.Value)
            .Select(static pair => pair.Key)
            .OrderBy(static index => index)
            .ToArray();
        var rejectedIndexSet = rejectedIndexes.ToHashSet();
        var lockedEvidenceIds = claims
            .Where((_, index) => !rejectedIndexSet.Contains(index))
            .SelectMany(static claim => claim.EvidenceIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableEvidenceIds = candidateEvidenceIds
            .Where(id => !lockedEvidenceIds.Contains(id) && bundle.ById.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (rejectedIndexes.Length == 0
            || availableEvidenceIds.Length < rejectedIndexes.Length)
        {
            return FailedStructuredAssignmentRevision(
                "Le nombre de candidats disponibles ne couvre pas les cellules refusees.");
        }

        var completions = new List<SourceBackedAgentCompletion>(2);
        var attemptFailureReasons = new List<string>(2);
        string? lastFailureReason = null;
        string? previousInvalidOutput = null;
        var contextRecoveryUsed = false;
        int? exactInputTokens = null;
        var forbiddenEvidenceByCell = rejectedIndexes.ToDictionary(
            static index => index,
            index => claims[index].EvidenceIds.Single(),
            EqualityComparer<int>.Default);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var usesStructuredContract =
                _llm is ISourceBackedAgentStructuredLlmClient;
            var revisionTools = usesStructuredContract
                ? Array.Empty<SourceBackedAgentToolDefinition>()
                : BuildStructuredAssignmentRevisionTool(
                    rejectedIndexes,
                    availableEvidenceIds,
                    rejectedEvidenceIdsByCell);
            var maximumOutputTokens = Math.Min(
                _options.MaximumSemanticReviewTokens,
                Math.Clamp(rejectedIndexes.Length * 10 + 24, 64, 192));
            var revisionMessages = BuildStructuredAssignmentRevisionMessages(
                intake,
                bundle,
                claims,
                rejectedIndexes,
                availableEvidenceIds,
                rejectedEvidenceIdsByCell,
                repairProtocol: attempt > 1,
                previousInvalidOutput,
                lastFailureReason,
                compactContext: false,
                usesStructuredContract);
            exactInputTokens = await CountInputTokensAsync(
                    revisionMessages,
                    revisionTools,
                    requireToolCall: !usesStructuredContract,
                    ct)
                .ConfigureAwait(false);
            if (ExceedsContextBudget(exactInputTokens, maximumOutputTokens))
            {
                contextRecoveryUsed = true;
                revisionMessages = BuildStructuredAssignmentRevisionMessages(
                    intake,
                    bundle,
                    claims,
                    rejectedIndexes,
                    availableEvidenceIds,
                    rejectedEvidenceIdsByCell,
                    repairProtocol: attempt > 1,
                    previousInvalidOutput,
                    lastFailureReason,
                    compactContext: true,
                    usesStructuredContract);
                exactInputTokens = await CountInputTokensAsync(
                        revisionMessages,
                        revisionTools,
                        requireToolCall: !usesStructuredContract,
                        ct)
                    .ConfigureAwait(false);
            }
            if (exactInputTokens is { } measuredInputTokens
                && ExceedsContextBudget(exactInputTokens, maximumOutputTokens))
            {
                var availableOutputTokens =
                    _options.MaximumContextTokens
                    - measuredInputTokens
                    - ContextSafetyReserveTokens;
                if (availableOutputTokens < 64)
                {
                    lastFailureReason =
                        "La revision structuree depasse encore le budget de contexte "
                        + $"apres compaction: input={measuredInputTokens}, "
                        + $"context={_options.MaximumContextTokens}.";
                    break;
                }
                maximumOutputTokens = Math.Min(
                    maximumOutputTokens,
                    availableOutputTokens);
            }
            var completion = usesStructuredContract
                ? await ((ISourceBackedAgentStructuredLlmClient)_llm)
                    .CompleteStructuredAsync(
                        revisionMessages,
                        BuildStructuredAssignmentRevisionContract(
                            rejectedIndexes,
                            availableEvidenceIds,
                            rejectedEvidenceIdsByCell),
                        maximumOutputTokens,
                        ct,
                        temperatureOverride: 0)
                    .ConfigureAwait(false)
                : await _llm.CompleteAsync(
                        revisionMessages,
                        revisionTools,
                        maximumOutputTokens,
                        ct,
                        temperatureOverride: 0,
                        requireToolCall: true)
                    .ConfigureAwait(false);
            completions.Add(completion);
            if (!TryReadStructuredAssignmentRevision(
                    completion,
                    rejectedIndexes,
                    availableEvidenceIds,
                    rejectedEvidenceIdsByCell,
                    out var rawOutput,
                    out lastFailureReason))
            {
                previousInvalidOutput = completion.ToolCalls.Count == 1
                    ? completion.ToolCalls[0].Arguments.GetRawText()
                    : completion.Content ?? string.Empty;
                attemptFailureReasons.Add(
                    $"tentative {attempt}: {lastFailureReason}");
                continue;
            }
            if (StructuredRevisionUsesRejectedEvidenceForSameCell(
                    rawOutput,
                    rejectedEvidenceIdsByCell))
            {
                lastFailureReason =
                    "Le patch reutilise une preuve deja refusee pour la meme cellule.";
                previousInvalidOutput = rawOutput;
                attemptFailureReasons.Add(
                    $"tentative {attempt}: {lastFailureReason}");
                continue;
            }
            if (TryApplyStructuredAssignmentRevision(
                rejectedDraft,
                shape,
                rejectedIndexes,
                availableEvidenceIds,
                forbiddenEvidenceByCell,
                bundle,
                rawOutput,
                out var revisedContent,
                out lastFailureReason))
            {
                return new StructuredAssignmentRevisionExecution(
                    AggregateStructuredCandidateWriterCompletions(
                        completions,
                        revisedContent,
                        "structured_assignment_batch_revision"),
                    true,
                    rejectedIndexes.Length,
                    availableEvidenceIds.Length,
                    rawOutput,
                    null,
                    completions.Count,
                    contextRecoveryUsed,
                    exactInputTokens);
            }
            previousInvalidOutput = rawOutput;
            attemptFailureReasons.Add(
                $"tentative {attempt}: {lastFailureReason}");
        }

        var finalRawOutput = previousInvalidOutput
                             ?? completions.LastOrDefault()?.Content
                             ?? string.Empty;
        return new StructuredAssignmentRevisionExecution(
            AggregateStructuredCandidateWriterCompletions(
                completions,
                finalRawOutput,
                "protocol_error"),
            false,
            0,
            availableEvidenceIds.Length,
            finalRawOutput,
            attemptFailureReasons.Count > 0
                ? string.Join(" | ", attemptFailureReasons)
                : lastFailureReason
                  ?? "La revision structuree n'a produit aucune decision exploitable.",
            completions.Count,
            contextRecoveryUsed,
            exactInputTokens);
    }

}
