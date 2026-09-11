using System.Diagnostics;
using System.Text;
using System.Text.Json;

// Independent LLM-owned scope challenge for a single choice among observed candidates.
namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record SingleSelectionScopeReview(
        string Decision,
        string Basis,
        string SelectedEvidenceId,
        string Question,
        string Reason,
        IReadOnlyDictionary<string, string> CandidateMatches,
        bool MechanicallyNormalized,
        bool ProtocolValid,
        SourceBackedAgentCompletion Completion,
        long ElapsedMilliseconds);

    private async Task<SingleSelectionScopeReview>
        ReviewSingleSelectionScopeAsync(
            SourceBackedIntake intake,
            IReadOnlyList<FastEvidenceCandidate> candidates,
            FastEvidenceCandidate selectedCandidate,
            CancellationToken ct)
    {
        var selectedEvidenceId =
            selectedCandidate.Representative.EvidenceId;
        var context = new StringBuilder()
            .Append("DEMANDE: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 500))
            .Append("CANDIDAT_SELECTIONNE: [")
            .Append(selectedEvidenceId)
            .Append("] ")
            .AppendLine(TrimPromptValue(
                GetEvidenceDisplayValue(selectedCandidate.Representative),
                120))
            .AppendLine("CANDIDATS_OBSERVES:");
        foreach (var candidate in candidates)
        {
            var representative = candidate.Representative;
            context.Append("- [")
                .Append(representative.EvidenceId)
                .Append("] ")
                .Append(TrimPromptValue(
                    GetEvidenceDisplayValue(representative),
                    100))
                .Append(" | ")
                .Append(BuildFastEvidenceReviewMetadata(
                    representative,
                    includeDocumentPath: false))
                .Append(" | ")
                .AppendLine(CompactReviewExcerpt(
                    representative.Excerpt,
                    180));
        }

        var messages = new[]
        {
            SourceBackedAgentMessage.System(
                """
                Tu es le controleur independant de la portee du livrable. Tu ne
                juges ni le style ni l'ordre du moteur de recherche. Determine si
                choisir exactement un candidat respecte la DEMANDE sans inventer
                une preference utilisateur.

                Decide d'abord si livrer exactement un candidat suffit pour satisfaire
                la DEMANDE. Si l'utilisateur demande un exemple, un item ou une option
                qui respecte ses contraintes, tu peux accepter le candidat selectionne
                meme si plusieurs candidats sont des matches. Leur simple pluralite
                n'est jamais, a elle seule, une ambiguite bloquante.

                Demande une clarification seulement lorsqu'une information que seul
                l'utilisateur peut fournir est materiellement necessaire pour eviter
                un resultat faux, inadapte ou hors portee. Une preference facultative
                n'est pas une information manquante. Une demande exhaustive,
                comparative ou superlative ne doit pas etre arbitrairement reduite a
                un seul candidat; n'invente toutefois pas de question utilisateur si
                aucune information utilisateur ne manque.

                Classe d'abord CHAQUE candidat, independamment, comme match s'il
                satisfait materiellement la DEMANDE, sinon nonmatch. Ne compare pas
                encore leur rang. Retourne uniquement un objet JSON avec exactement
                decision, candidateMatches, selectedEvidenceId, question, reason.
                candidateMatches contient chaque identifiant affiche exactement une
                fois avec match ou nonmatch. decision=accept si le candidat deja
                selectionne est match et qu'un seul candidat suffit a la DEMANDE;
                selectedEvidenceId le designe et question est vide. decision=clarify
                seulement si au moins deux candidats sont match ET qu'une information
                utilisateur manquante empeche un choix correct; selectedEvidenceId=NONE
                et question est une question utilisateur concise dans la langue de la
                DEMANDE. reason justifie brievement la portee depuis la DEMANDE et les
                candidats visibles. Aucun autre texte.
                """),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
        var stopwatch = Stopwatch.StartNew();
        var completion = _llm is ISourceBackedAgentStructuredLlmClient structuredLlm
            ? await structuredLlm.CompleteStructuredAsync(
                    messages,
                    BuildSingleSelectionScopeReviewContract(candidates),
                    _options.MaximumActionTokens,
                    ct,
                    temperatureOverride: 0)
                .ConfigureAwait(false)
            : await _llm.CompleteAsync(
                    messages,
                    Array.Empty<SourceBackedAgentToolDefinition>(),
                    _options.MaximumActionTokens,
                    ct,
                    temperatureOverride: 0,
                    requireToolCall: false)
                .ConfigureAwait(false);
        stopwatch.Stop();

        var parsed = ReadSingleSelectionScopeReview(completion);
        var expectedCandidateIds = candidates
            .Select(static candidate => candidate.Representative.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchesComplete =
            parsed.CandidateMatches.Count == expectedCandidateIds.Count
            && parsed.CandidateMatches.Keys.All(expectedCandidateIds.Contains)
            && parsed.CandidateMatches.Values.All(static value =>
                value is "match" or "nonmatch");
        var matchingCandidateIds = parsed.CandidateMatches
            .Where(static pair => pair.Value == "match")
            .Select(static pair => pair.Key)
            .ToArray();
        var selectedCandidateMatches =
            matchingCandidateIds.Contains(
                selectedEvidenceId,
                StringComparer.OrdinalIgnoreCase);
        var multipleMatchesObserved = matchingCandidateIds.Length >= 2;
        var acceptedSelectionValid =
            string.Equals(parsed.Decision, "accept", StringComparison.Ordinal)
            && selectedCandidateMatches
            && string.Equals(
                parsed.SelectedEvidenceId,
                selectedEvidenceId,
                StringComparison.OrdinalIgnoreCase)
            && parsed.Question.Length == 0;
        var clarificationValid =
            string.Equals(parsed.Decision, "clarify", StringComparison.Ordinal)
            && multipleMatchesObserved
            && string.Equals(
                parsed.SelectedEvidenceId,
                "NONE",
                StringComparison.OrdinalIgnoreCase)
            && parsed.Question.Length >= 8;
        var protocolValid =
            parsed.ProtocolValid
            && matchesComplete
            && (acceptedSelectionValid || clarificationValid);
        var basis = !protocolValid
            ? "invalid_scope_review"
            : acceptedSelectionValid
                ? matchingCandidateIds.Length == 1
                    ? "unique_matching_candidate"
                    : "multiple_matching_candidates_non_blocking"
                : "multiple_matching_candidates_blocking";

        return parsed with
        {
            Basis = basis,
            MechanicallyNormalized = false,
            ProtocolValid = protocolValid,
            Completion = completion,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
        };
    }

    private static SingleSelectionScopeReview ReadSingleSelectionScopeReview(
        SourceBackedAgentCompletion completion)
    {
        if (completion.ToolCalls.Count > 0)
            return InvalidSingleSelectionScopeReview(completion);

        var content = (completion.Content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        if (content.StartsWith("```", StringComparison.Ordinal)
            && content.EndsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = content.IndexOf('\n');
            content = firstBreak >= 0
                ? content[(firstBreak + 1)..^3].Trim()
                : string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 5)
            {
                return InvalidSingleSelectionScopeReview(completion);
            }

            var decision = ReadFastEvidenceString(root, "decision")
                .ToLowerInvariant();
            var candidateMatches = ReadSingleSelectionCandidateMatches(root);
            var selectedEvidenceId = ReadFastEvidenceString(
                root,
                "selectedEvidenceId");
            var question = ReadFastEvidenceString(root, "question");
            var reason = ReadFastEvidenceString(root, "reason");
            return new SingleSelectionScopeReview(
                decision,
                string.Empty,
                selectedEvidenceId,
                question,
                reason,
                candidateMatches,
                false,
                (decision is "accept" or "clarify")
                && selectedEvidenceId.Length > 0
                && question.Length <= 300
                && reason.Length is >= 8 and <= 500,
                completion,
                0);
        }
        catch (JsonException)
        {
            return InvalidSingleSelectionScopeReview(completion);
        }
    }

    private static IReadOnlyDictionary<string, string>
        ReadSingleSelectionCandidateMatches(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "candidateMatches", out var matches)
            || matches.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
        }

        var parsed = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var property in matches.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                return new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            parsed[property.Name] = property.Value.GetString()?
                .Trim()
                .ToLowerInvariant() ?? string.Empty;
        }

        return parsed;
    }

    private static SingleSelectionScopeReview InvalidSingleSelectionScopeReview(
        SourceBackedAgentCompletion completion)
        => new(
            "",
            "",
            "",
            "",
            "",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            false,
            false,
            completion,
            0);

    private static LlmStructuredOutputContract
        BuildSingleSelectionScopeReviewContract(
            IReadOnlyList<FastEvidenceCandidate> candidates)
    {
        var candidateIds = candidates
            .Select(static candidate =>
                candidate.Representative.EvidenceId)
            .Append("NONE")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var schema = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["decision"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "accept", "clarify" }
                    },
                    ["candidateMatches"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = candidates.ToDictionary(
                            static candidate =>
                                candidate.Representative.EvidenceId,
                            static _ => (object)new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["enum"] = new[] { "match", "nonmatch" }
                            },
                            StringComparer.OrdinalIgnoreCase),
                        ["required"] = candidates
                            .Select(static candidate =>
                                candidate.Representative.EvidenceId)
                            .ToArray(),
                        ["additionalProperties"] = false
                    },
                    ["selectedEvidenceId"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = candidateIds
                    },
                    ["question"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["maxLength"] = 300
                    },
                    ["reason"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["minLength"] = 8,
                        ["maxLength"] = 500
                    }
                },
                ["required"] = new[]
                {
                    "decision",
                    "candidateMatches",
                    "selectedEvidenceId",
                    "question",
                    "reason"
                },
                ["additionalProperties"] = false
            });
        return new LlmStructuredOutputContract(
            "source_backed_single_selection_scope_review_v2",
            schema);
    }
}
