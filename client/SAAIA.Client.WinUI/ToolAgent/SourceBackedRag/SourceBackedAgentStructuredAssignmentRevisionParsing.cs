using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildStructuredAssignmentRevisionTool(
            IReadOnlyList<int> rejectedIndexes,
            IReadOnlyList<string> availableEvidenceIds,
            IReadOnlyDictionary<int, HashSet<string>> rejectedEvidenceIdsByCell)
        => new[]
        {
            new SourceBackedAgentToolDefinition(
                SubmitStructuredAssignmentRevisionToolName,
                "Soumet exactement une nouvelle affectation par cellule refusee. "
                + "Chaque enum exclut mecaniquement les preuves deja refusees pour sa cellule.",
                BuildStructuredAssignmentRevisionSchema(
                    rejectedIndexes,
                    availableEvidenceIds,
                    rejectedEvidenceIdsByCell))
        };

    private static LlmStructuredOutputContract
        BuildStructuredAssignmentRevisionContract(
            IReadOnlyList<int> rejectedIndexes,
            IReadOnlyList<string> availableEvidenceIds,
            IReadOnlyDictionary<int, HashSet<string>> rejectedEvidenceIdsByCell)
        => new(
            "source_backed_structured_assignment_revision_v2",
            BuildStructuredAssignmentRevisionSchema(
                rejectedIndexes,
                availableEvidenceIds,
                rejectedEvidenceIdsByCell));

    private static JsonElement BuildStructuredAssignmentRevisionSchema(
        IReadOnlyList<int> rejectedIndexes,
        IReadOnlyList<string> availableEvidenceIds,
        IReadOnlyDictionary<int, HashSet<string>> rejectedEvidenceIdsByCell)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var index in rejectedIndexes)
        {
            var cellKey = StructuredCellPropertyKey(index);
            var allowedEvidenceIds = availableEvidenceIds
                .Where(evidenceId =>
                    !rejectedEvidenceIdsByCell.TryGetValue(
                        index,
                        out var rejectedForCell)
                    || !rejectedForCell.Contains(evidenceId))
                .ToArray();
            properties[cellKey] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] =
                    "EvidenceId exact du candidat choisi semantiquement pour "
                    + cellKey + ".",
                ["enum"] = allowedEvidenceIds
            };
        }

        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = rejectedIndexes
                    .Select(StructuredCellPropertyKey)
                    .ToArray(),
                ["additionalProperties"] = false
            },
            ClientJson.CamelCase);
    }

    private static bool TryReadStructuredAssignmentRevision(
        SourceBackedAgentCompletion completion,
        IReadOnlyList<int> rejectedIndexes,
        IReadOnlyList<string> availableEvidenceIds,
        IReadOnlyDictionary<int, HashSet<string>> rejectedEvidenceIdsByCell,
        out string rawOutput,
        out string? failureReason)
    {
        rawOutput = string.Empty;
        failureReason = null;
        JsonElement arguments;
        JsonDocument? document = null;
        if (completion.ToolCalls.Count == 1
            && string.Equals(
                completion.ToolCalls[0].Name,
                SubmitStructuredAssignmentRevisionToolName,
                StringComparison.OrdinalIgnoreCase)
            && completion.ToolCalls[0].Arguments.ValueKind == JsonValueKind.Object)
        {
            arguments = completion.ToolCalls[0].Arguments;
        }
        else if (completion.ToolCalls.Count == 0
                 && TryParseParentReviewContent(completion.Content, out document)
                 && document!.RootElement.ValueKind == JsonValueKind.Object)
        {
            arguments = document.RootElement;
        }
        else
        {
            failureReason =
                "La revision doit retourner exactement l'objet de soumission structuree.";
            document?.Dispose();
            return false;
        }

        try
        {
            if (arguments.EnumerateObject().Count() != rejectedIndexes.Count)
            {
                failureReason =
                    "La revision structuree ne couvre pas exactement les cellules refusees.";
                return false;
            }

            var assignments = new List<string>(rejectedIndexes.Count);
            foreach (var index in rejectedIndexes)
            {
                var cellKey = StructuredCellPropertyKey(index);
                if (!TryGetPropertyIgnoreCase(arguments, cellKey, out var value)
                    || value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(value.GetString()))
                {
                    failureReason =
                        $"La revision structuree ne fournit pas un EvidenceId valide pour {cellKey}.";
                    return false;
                }

                var evidenceId = value.GetString()!.Trim();
                if (!availableEvidenceIds.Contains(
                        evidenceId,
                        StringComparer.OrdinalIgnoreCase))
                {
                    failureReason =
                        $"La revision structuree fournit un EvidenceId inconnu pour {cellKey}.";
                    return false;
                }
                if (rejectedEvidenceIdsByCell.TryGetValue(
                           index,
                           out var rejectedForCell)
                    && rejectedForCell.Contains(evidenceId))
                {
                    failureReason =
                        $"La revision structuree fournit un EvidenceId interdit pour {cellKey}.";
                    return false;
                }
                assignments.Add(StructuredCellKey(index) + "=" + evidenceId);
            }

            rawOutput = string.Join(Environment.NewLine, assignments);
            return true;
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static string StructuredCellKey(int zeroBasedIndex)
        => "C" + (zeroBasedIndex + 1).ToString("D2", CultureInfo.InvariantCulture);

    private static string StructuredCellPropertyKey(int zeroBasedIndex)
        => "c" + (zeroBasedIndex + 1).ToString("D2", CultureInfo.InvariantCulture);

    private static bool StructuredRevisionUsesRejectedEvidenceForSameCell(
        string? rawOutput,
        IReadOnlyDictionary<int, HashSet<string>> rejectedEvidenceIdsByCell)
    {
        return Regex.Matches(
                rawOutput ?? string.Empty,
                StructuredAssignmentLinePattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Any(match =>
            {
                var cellIndex = int.Parse(match.Groups[1].Value) - 1;
                var evidenceId = match.Groups[2].Value;
                return rejectedEvidenceIdsByCell.TryGetValue(
                           cellIndex,
                           out var rejectedEvidenceIds)
                       && rejectedEvidenceIds.Contains(evidenceId);
            });
    }

    private static StructuredAssignmentRevisionExecution
        FailedStructuredAssignmentRevision(string failureReason)
        => new(
            new SourceBackedAgentCompletion(
                string.Empty,
                Array.Empty<SourceBackedAgentToolCall>(),
                "protocol_error"),
            false,
            0,
            0,
            string.Empty,
            failureReason,
            0);

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildStructuredAssignmentRevisionMessages(
            SourceBackedIntake intake,
            EvidenceBundle bundle,
            IReadOnlyList<SourceBackedStructuredCellClaim> claims,
            IReadOnlyList<int> rejectedIndexes,
            IReadOnlyList<string> availableEvidenceIds,
            IReadOnlyDictionary<int, HashSet<string>>
                rejectedEvidenceIdsByCell,
            bool repairProtocol,
            string? previousInvalidOutput,
            string? previousFailureReason,
            bool compactContext,
            bool usesStructuredContract)
    {
        var questionLimit = compactContext ? 360 : 600;
        var roleLimit = compactContext ? 180 : 320;
        var displayLimit = compactContext ? 100 : 160;
        var excerptLimit = compactContext ? 110 : 220;
        var prompt = new StringBuilder()
            .AppendLine("DEMANDE UTILISATEUR:")
            .AppendLine(TrimPromptValue(intake.UserQuestion, questionLimit))
            .AppendLine("CELLULES A REAFFECTER:");
        foreach (var index in rejectedIndexes)
        {
            var claim = claims[index];
            var semanticRole = intake.CanonicalColumnSemanticRoles is not null
                               && intake.CanonicalColumnSemanticRoles.TryGetValue(
                                   claim.ColumnHeader,
                                   out var role)
                ? role
                : claim.ColumnHeader;
            prompt.Append("- ").Append(claim.ClaimRef)
                .Append(" | ligne: ").Append(TrimPromptValue(claim.RowLabel, 80))
                .Append(" | colonne: ").Append(TrimPromptValue(claim.ColumnHeader, 80))
                .Append(" | role: ").Append(TrimPromptValue(semanticRole, roleLimit))
                .Append(" | INTERDITS POUR CETTE CELLULE: ")
                .AppendLine(rejectedEvidenceIdsByCell.TryGetValue(
                    index,
                    out var rejectedForCell)
                    ? string.Join(", ", rejectedForCell.OrderBy(
                        static id => id,
                        StringComparer.OrdinalIgnoreCase))
                    : claim.EvidenceIds.Single());
        }
        prompt.AppendLine("CANDIDATS DISPONIBLES:");
        foreach (var evidenceId in availableEvidenceIds)
        {
            var evidence = bundle.ById[evidenceId];
            prompt.Append("- [").Append(evidenceId).Append("] ")
                .Append(TrimPromptValue(GetEvidenceDisplayValue(evidence), displayLimit))
                .Append(" | ").AppendLine(TrimPromptValue(evidence.Excerpt, excerptLimit));
        }
        if (usesStructuredContract)
        {
            prompt.AppendLine(
                "FORMAT STRUCTURE OBLIGATOIRE: pour chaque propriete cXX, fournis "
                + "uniquement l'EvidenceId exact du candidat choisi.");
        }
        else
        {
            prompt.AppendLine(
                "APPEL OBLIGATOIRE: submit_structured_assignment_revision. Pour chaque propriete "
                + "cXX, fournis uniquement l'EvidenceId exact du candidat choisi.");
        }
        if (repairProtocol)
        {
            prompt.AppendLine("PATCH PRECEDENT INVALIDE:")
                .AppendLine(TrimPromptBlock(
                    previousInvalidOutput,
                    compactContext ? 300 : 600))
                .Append("ERREUR MECANIQUE: ")
                .AppendLine(TrimPromptValue(
                    previousFailureReason,
                    compactContext ? 180 : 300))
                .AppendLine(
                    "REPARATION: remplace les choix invalides. Couvre chaque propriete cXX "
                    + "exactement une fois, utilise chaque EvidenceId au plus une fois et "
                    + "choisis uniquement un EvidenceId enumere par le schema de cette propriete.");
        }
        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es l'orchestrateur semantique d'une revision de grille sourcee. "
                + "Affecte a chaque cellule refusee le candidat disponible qui convient le mieux "
                + "a sa ligne, sa colonne et son role. Tu ne revises aucune autre cellule. "
                + "Examine toute la liste avant de choisir et ne suis pas l'ordre des candidats. "
                + "Choisis une adequation claire et conventionnelle; n'utilise jamais une "
                + "valeur ambigue, limite ou incompatible pour simplement remplir la cellule. "
                + "La valeur affichee choisie doit elle-meme nommer une instance concrete: "
                + "une categorie, rubrique, collection, liste, role ou axe reste invalide "
                + "meme si son extrait contient de bons exemples. "
                + "N'affecte jamais a une cellule l'EvidenceId explicitement refuse dans cette "
                + "meme cellule. Chaque candidat ne peut etre utilise qu'une fois. "
                + (usesStructuredContract
                    ? "Retourne uniquement l'objet JSON conforme, avec un EvidenceId exact par propriete cXX."
                    : "Appelle uniquement l'outil de soumission avec un EvidenceId exact par propriete cXX.")),
            SourceBackedAgentMessage.User(prompt.ToString().Trim())
        };
    }

}
