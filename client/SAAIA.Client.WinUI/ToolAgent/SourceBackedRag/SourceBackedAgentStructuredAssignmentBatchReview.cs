using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private async Task<StructuredAssignmentBatchReview>
        CompleteStructuredAssignmentBatchReviewAsync(
            SourceBackedIntake intake,
            IReadOnlyList<SourceBackedStructuredCellClaim> claims,
            EvidenceBundle bundle,
            CancellationToken ct)
    {
        var completions = new List<SourceBackedAgentCompletion>(2);
        var usesStructuredContract =
            _llm is ISourceBackedAgentStructuredLlmClient;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var messages = BuildStructuredAssignmentBatchReviewMessages(
                intake,
                claims,
                bundle,
                repairProtocol: attempt > 1,
                usesStructuredContract);
            var maximumOutputTokens =
                ResolveStructuredAssignmentBatchReviewOutputTokens(
                    claims.Count);
            var completion = usesStructuredContract
                ? await ((ISourceBackedAgentStructuredLlmClient)_llm)
                    .CompleteStructuredAsync(
                        messages,
                        BuildStructuredAssignmentBatchReviewContract(claims),
                        maximumOutputTokens,
                        ct,
                        temperatureOverride: 0)
                    .ConfigureAwait(false)
                : await _llm.CompleteAsync(
                        messages,
                        Array.Empty<SourceBackedAgentToolDefinition>(),
                        maximumOutputTokens,
                        ct,
                        temperatureOverride: 0,
                        requireToolCall: false)
                    .ConfigureAwait(false);
            completions.Add(completion);
            if (TryParseStructuredAssignmentBatchReview(
                    completion,
                    claims,
                    out var decisions))
            {
                return new StructuredAssignmentBatchReview(
                    true,
                    decisions,
                    completions,
                    attempt);
            }
        }

        return new StructuredAssignmentBatchReview(
            false,
            Array.Empty<StructuredAssignmentDecision>(),
            completions,
            completions.Count);
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildStructuredAssignmentBatchReviewMessages(
            SourceBackedIntake intake,
            IReadOnlyList<SourceBackedStructuredCellClaim> claims,
            EvidenceBundle bundle,
            bool repairProtocol,
            bool usesStructuredContract)
    {
        var prompt = new StringBuilder()
            .AppendLine("DEMANDE UTILISATEUR:")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 600))
            .AppendLine("ROLES SEMANTIQUES DES COLONNES DU LOT:");
        foreach (var column in claims
                     .Select(static claim => claim.ColumnHeader)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var role = intake.CanonicalColumnSemanticRoles is not null
                       && intake.CanonicalColumnSemanticRoles.TryGetValue(
                           column,
                           out var configuredRole)
                ? configuredRole
                : column;
            prompt.Append("- ").Append(TrimPromptValue(column, 80))
                .Append(": ").AppendLine(TrimPromptValue(role, 420));
        }

        prompt.AppendLine("AFFECTATIONS DU LOT A JUGER:");
        foreach (var claim in claims)
        {
            bundle.ById.TryGetValue(claim.EvidenceIds[0], out var evidence);
            prompt.Append("- ").Append(claim.ClaimRef)
                .Append(" | ligne: ").Append(TrimPromptValue(claim.RowLabel, 80))
                .Append(" | colonne: ").Append(TrimPromptValue(claim.ColumnHeader, 80))
                .Append(" | valeur: ").Append(TrimPromptValue(claim.ClaimText, 160))
                .Append(" [").Append(claim.EvidenceIds[0]).Append(']');
            if (evidence is not null)
            {
                prompt.Append(" | preuve: ")
                    .Append(TrimPromptValue(evidence.Excerpt, 220));
            }
            prompt.AppendLine();
        }

        prompt.AppendLine(
                "JUGE CHAQUE CELLULE SUR SON ADEQUATION REELLE A LA DEMANDE ET AU ROLE "
                + "DE SA COLONNE. ACCEPT si la valeur est une instance concrete, prouvee "
                + "et legitimement utilisable dans une reponse professionnelle. REJECT "
                + "en cas d'incompatibilite semantique nette, de valeur non concrete ou "
                + "de preuve qui ne confirme pas la valeur. Ne rejette pas une affectation "
                + "correcte pour une simple preference de style. Le libelle explicite et "
                + "le passage prouve priment sur une association ambigue d'un mot isole. "
                + "Juge d'abord la VALEUR AFFICHEE elle-meme: le passage sert seulement "
                + "a la confirmer et ne peut jamais la renommer. Une valeur qui nomme une "
                + "categorie, rubrique, collection, liste, role ou axe reste non concrete "
                + "et doit etre REJECT, meme si son passage contient des exemples concrets.")
            .AppendLine(usesStructuredContract
                ? "FORMAT JSON IMPOSE: pour chaque propriete CXX, choisis exactement "
                  + "ACCEPT ou REJECT. Couvre toutes les proprietes et rien d'autre."
                : "FORMAT EXACT: CXX=ACCEPT sans motif pour une cellule acceptee; "
                  + "CXX=REJECT:motif de huit mots maximum pour une cellule rejetee. "
                  + "Une ligne par cellule affichee, dans le meme ordre, rien d'autre.");
        if (repairProtocol)
        {
            prompt.AppendLine(usesStructuredContract
                ? "REPARATION DE PROTOCOLE: couvre chaque propriete CXX exactement "
                  + "une fois avec la seule valeur ACCEPT ou REJECT."
                : "REPARATION DE PROTOCOLE: couvre chaque CXX exactement une fois, "
                  + "avec ACCEPT seul ou REJECT suivi d'un motif bref.");
        }

        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es le critique semantique d'un lot coherent de cellules d'une "
                + "grille professionnelle sourcee. Tu verifies la correction, pas "
                + "l'optimisation stylistique. Prends une decision stable pour chaque "
                + "cellule a partir de son libelle, de sa preuve et du role complet de "
                + "sa colonne. Une valeur legitimement adaptee doit etre acceptee meme "
                + "si d'autres choix seraient possibles. Ne justifie jamais ACCEPT. "
                + "La valeur affichee doit elle-meme nommer l'instance concrete attendue; "
                + "une rubrique ou categorie ne devient pas une instance parce que sa "
                + "preuve contient des instances. "
                + "Pour REJECT, huit mots maximum. Reponds uniquement avec une decision "
                + "par CXX. "
                + (usesStructuredContract
                    ? "Retourne uniquement l'objet JSON conforme."
                    : string.Empty)),
            SourceBackedAgentMessage.User(prompt.ToString().Trim())
        };
    }

    private int ResolveStructuredAssignmentBatchReviewOutputTokens(
        int claimCount)
        => Math.Min(
            _options.MaximumSemanticReviewTokens,
            Math.Clamp(64 + Math.Max(1, claimCount) * 12, 128, 384));

    private static LlmStructuredOutputContract
        BuildStructuredAssignmentBatchReviewContract(
            IReadOnlyList<SourceBackedStructuredCellClaim> claims)
    {
        var properties = claims.ToDictionary(
            static claim => claim.ClaimRef,
            static _ => (object?)new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["enum"] = new[] { "ACCEPT", "REJECT" }
            },
            StringComparer.Ordinal);
        return new LlmStructuredOutputContract(
            "source_backed_structured_assignment_batch_review_v1",
            JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = claims
                        .Select(static claim => claim.ClaimRef)
                        .ToArray(),
                    ["additionalProperties"] = false
                },
                ClientJson.CamelCase));
    }

    private static bool TryParseStructuredAssignmentBatchReview(
        SourceBackedAgentCompletion completion,
        IReadOnlyList<SourceBackedStructuredCellClaim> claims,
        out IReadOnlyList<StructuredAssignmentDecision> decisions)
    {
        decisions = Array.Empty<StructuredAssignmentDecision>();
        if (completion.ToolCalls.Count > 0)
            return false;

        if (TryParseStructuredAssignmentBatchReviewJson(
                completion.Content,
                claims,
                out decisions))
        {
            return true;
        }

        var matches = Regex.Matches(
                (completion.Content ?? string.Empty).Trim().Trim('`', ' ', '\r', '\n'),
                @"(?m)^\s*(?:[-*]\s*)?(C\d{2,})\s*[:=]\s*(ACCEPT|REJECT)(?:\s*[:|-]\s*([^\r\n]+))?\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .ToArray();
        var claimsByRef = claims.ToDictionary(
            static claim => claim.ClaimRef,
            StringComparer.OrdinalIgnoreCase);
        if (matches.Length != claims.Count
            || matches.Select(static match => match.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != claims.Count
            || matches.Any(match => !claimsByRef.ContainsKey(match.Groups[1].Value)))
        {
            return false;
        }

        decisions = matches
            .Select(match =>
            {
                var accepted = string.Equals(
                    match.Groups[2].Value,
                    "ACCEPT",
                    StringComparison.OrdinalIgnoreCase);
                var reason = Regex.Replace(
                        match.Groups[3].Value,
                        @"\s+",
                        " ",
                        RegexOptions.CultureInvariant)
                    .Trim();
                if (reason.Length == 0)
                {
                    reason = accepted
                        ? "Affectation acceptee par la revue en lot du LLM."
                        : "Affectation rejetee par la revue en lot du LLM.";
                }
                return new StructuredAssignmentDecision(
                    claimsByRef[match.Groups[1].Value],
                    accepted,
                    reason);
            })
            .ToArray();
        return true;
    }

    private static bool TryParseStructuredAssignmentBatchReviewJson(
        string? content,
        IReadOnlyList<SourceBackedStructuredCellClaim> claims,
        out IReadOnlyList<StructuredAssignmentDecision> decisions)
    {
        decisions = Array.Empty<StructuredAssignmentDecision>();
        if (string.IsNullOrWhiteSpace(content))
            return false;

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != claims.Count)
            {
                return false;
            }

            var parsed = new List<StructuredAssignmentDecision>(claims.Count);
            foreach (var claim in claims)
            {
                if (!TryGetPropertyIgnoreCase(root, claim.ClaimRef, out var value)
                    || value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var verdict = value.GetString();
                var accepted = string.Equals(
                    verdict,
                    "ACCEPT",
                    StringComparison.OrdinalIgnoreCase);
                if (!accepted
                    && !string.Equals(
                        verdict,
                        "REJECT",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                parsed.Add(new StructuredAssignmentDecision(
                    claim,
                    accepted,
                    accepted
                        ? "Affectation acceptee par la revue structuree du LLM."
                        : "Affectation rejetee par la revue structuree du LLM."));
            }

            decisions = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
