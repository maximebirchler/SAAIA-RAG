using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    internal sealed record StructuredCandidateAssignment(
        int CellIndex,
        string EvidenceId);

    internal sealed record StructuredCandidateConflictRepair(
        IReadOnlyList<StructuredCandidateAssignment> Assignments,
        IReadOnlyList<int> ConflictCellIndexes,
        IReadOnlyList<string> AvailableEvidenceIds,
        IReadOnlyDictionary<int, string> OriginalCellValues);

    internal static bool TryPrepareStructuredCandidateTableRepair(
        string? table,
        EvidenceBundle bundle,
        IReadOnlyList<string> candidateEvidenceIds,
        IReadOnlyList<string> rowLabels,
        IReadOnlyList<string> columnLabels,
        out StructuredCandidateConflictRepair repair)
    {
        repair = new StructuredCandidateConflictRepair(
            Array.Empty<StructuredCandidateAssignment>(),
            Array.Empty<int>(),
            Array.Empty<string>(),
            new Dictionary<int, string>());
        if (string.IsNullOrWhiteSpace(table)
            || rowLabels.Count == 0
            || columnLabels.Count == 0)
        {
            return false;
        }

        var rows = table
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith('|') && line.Contains('|'))
            .Select(SplitStructuredCandidateMarkdownRow)
            .Where(static row => row.Count > 0)
            .ToArray();
        if (rows.Length < 2)
            return false;

        var normalizedColumns = columnLabels
            .Select(SourceBackedStructuredTableShapeBuilder.NormalizeLabel)
            .ToArray();
        var headerIndex = Array.FindIndex(
            rows,
            row =>
            {
                var normalizedCells = row
                    .Select(SourceBackedStructuredTableShapeBuilder.NormalizeLabel)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return normalizedColumns.All(normalizedCells.Contains);
            });
        if (headerIndex < 0)
            return false;

        var header = rows[headerIndex];
        var columnIndexes = header
            .Select((value, index) => new
            {
                Label = SourceBackedStructuredTableShapeBuilder.NormalizeLabel(value),
                Index = index
            })
            .Where(static item => item.Label.Length > 0)
            .GroupBy(static item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Index,
                StringComparer.OrdinalIgnoreCase);
        if (normalizedColumns.Any(column => !columnIndexes.ContainsKey(column)))
            return false;

        var separatorPattern = new Regex(
            @"^\s*:?-{3,}:?\s*$",
            RegexOptions.CultureInvariant);
        var expectedRows = rowLabels
            .Select(SourceBackedStructuredTableShapeBuilder.NormalizeLabel)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rowByLabel = rows
            .Skip(headerIndex + 1)
            .Where(row => row.Count > 0
                          && !row.All(cell => separatorPattern.IsMatch(cell)))
            .Where(row => expectedRows.Contains(
                SourceBackedStructuredTableShapeBuilder.NormalizeLabel(row[0])))
            .GroupBy(
                row => SourceBackedStructuredTableShapeBuilder.NormalizeLabel(row[0]),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        var allowed = candidateEvidenceIds
            .Where(bundle.ById.ContainsKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignments = new List<StructuredCandidateAssignment>();
        var conflicts = new HashSet<int>();
        var cellValues = new Dictionary<int, string>();
        for (var rowIndex = 0; rowIndex < rowLabels.Count; rowIndex++)
        {
            var normalizedRow =
                SourceBackedStructuredTableShapeBuilder.NormalizeLabel(
                    rowLabels[rowIndex]);
            rowByLabel.TryGetValue(normalizedRow, out var row);
            for (var columnIndex = 0; columnIndex < columnLabels.Count; columnIndex++)
            {
                var cellIndex = rowIndex * columnLabels.Count + columnIndex;
                var sourceColumnIndex = columnIndexes[normalizedColumns[columnIndex]];
                var cell = row is not null && sourceColumnIndex < row.Count
                    ? row[sourceColumnIndex].Trim()
                    : string.Empty;
                cellValues[cellIndex] = cell;
                var evidenceIds = SourceContractVerifier
                    .ExtractEvidenceIds(cell)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (evidenceIds.Length != 1
                    || !allowed.Contains(evidenceIds[0]))
                {
                    conflicts.Add(cellIndex);
                    continue;
                }

                assignments.Add(new StructuredCandidateAssignment(
                    cellIndex,
                    evidenceIds[0].ToUpperInvariant()));
            }
        }

        static void AddDuplicateConflicts(
            IEnumerable<IGrouping<string, StructuredCandidateAssignment>> groups,
            ISet<int> conflictIndexes)
        {
            foreach (var group in groups.Where(static group => group.Count() > 1))
            foreach (var assignment in group)
                conflictIndexes.Add(assignment.CellIndex);
        }

        AddDuplicateConflicts(
            assignments.GroupBy(
                static item => item.EvidenceId,
                StringComparer.OrdinalIgnoreCase),
            conflicts);
        AddDuplicateConflicts(
            assignments.GroupBy(
                item => bundle.ById[item.EvidenceId].VisibleSourceKey,
                StringComparer.OrdinalIgnoreCase),
            conflicts);
        AddDuplicateConflicts(
            assignments.GroupBy(
                item => GetEvidenceDisplayValue(bundle.ById[item.EvidenceId]),
                StringComparer.OrdinalIgnoreCase),
            conflicts);

        var lockedEvidence = assignments
            .Where(item => !conflicts.Contains(item.CellIndex))
            .Select(static item => item.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lockedVisibleSources = lockedEvidence
            .Select(id => bundle.ById[id].VisibleSourceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lockedDisplayValues = lockedEvidence
            .Select(id => GetEvidenceDisplayValue(bundle.ById[id]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = candidateEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(bundle.ById.ContainsKey)
            .Where(id => !lockedEvidence.Contains(id))
            .Where(id => !lockedVisibleSources.Contains(
                bundle.ById[id].VisibleSourceKey))
            .Where(id => !lockedDisplayValues.Contains(
                GetEvidenceDisplayValue(bundle.ById[id])))
            .ToArray();
        if (available.Length < conflicts.Count)
            return false;

        repair = new StructuredCandidateConflictRepair(
            assignments,
            conflicts.OrderBy(static index => index).ToArray(),
            available,
            cellValues);
        return assignments.Count + conflicts.Count
               >= rowLabels.Count * columnLabels.Count;
    }

    private static IReadOnlyList<string> SplitStructuredCandidateMarkdownRow(
        string line)
        => line.Trim().Trim('|')
            .Split('|')
            .Select(static cell => cell.Trim())
            .ToArray();

    private static string RenderStructuredCandidateAssignments(
        IEnumerable<StructuredCandidateAssignment> assignments)
        => string.Join(
            Environment.NewLine,
            assignments
                .OrderBy(static item => item.CellIndex)
                .Select(static item =>
                    "C" + (item.CellIndex + 1).ToString("D2")
                    + "=" + item.EvidenceId));

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildStructuredCandidateConflictRepairMessages(
            SourceBackedIntake intake,
            EvidenceBundle bundle,
        StructuredCandidateConflictRepair repair,
        IReadOnlyList<string> rowLabels,
        IReadOnlyList<string> columnLabels,
        IReadOnlyDictionary<string, string> columnRoles,
        string? previousInvalidOutput = null,
        string? previousFailureReason = null)
    {
        var prompt = new StringBuilder()
            .AppendLine("DEMANDE UTILISATEUR:")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 600))
            .AppendLine("CELLULES EN CONFLIT A REAFFECTER:");
        foreach (var cellIndex in repair.ConflictCellIndexes)
        {
            var rowIndex = cellIndex / columnLabels.Count;
            var columnIndex = cellIndex % columnLabels.Count;
            var columnLabel = columnLabels[columnIndex];
            prompt.Append("- C").Append((cellIndex + 1).ToString("D2"))
                .Append(" | ligne: ").Append(TrimPromptValue(rowLabels[rowIndex], 80))
                .Append(" | colonne: ").Append(TrimPromptValue(columnLabel, 80))
                .Append(" | role: ").Append(TrimPromptValue(
                    columnRoles.TryGetValue(columnLabel, out var role)
                        ? role
                        : columnLabel,
                    280))
                .Append(" | valeur refusee: ")
                .AppendLine(TrimPromptValue(
                    repair.OriginalCellValues.TryGetValue(cellIndex, out var original)
                        && !string.IsNullOrWhiteSpace(original)
                            ? original
                            : "cellule absente ou sans citation exploitable",
                    180));
        }
        prompt.AppendLine("CANDIDATS DISPONIBLES POUR CES CELLULES:");
        foreach (var evidenceId in repair.AvailableEvidenceIds)
        {
            var evidence = bundle.ById[evidenceId];
            prompt.Append('[').Append(evidenceId).Append("] ")
                .Append(TrimPromptValue(GetEvidenceDisplayValue(evidence), 140))
                .Append(" | ").AppendLine(TrimPromptValue(evidence.Excerpt, 180));
        }
        prompt.AppendLine(
            "FORMAT EXACT: une ligne CXX=E# pour chaque cellule affichee, rien d'autre. "
            + "Chaque E# choisi, source visible et libelle doivent etre distincts entre "
            + "les cellules reparees. Les candidats deja verrouilles ont ete retires.")
            .AppendLine(
                "SI ET SEULEMENT SI aucune affectation distincte et semantiquement "
                + "valide n'existe avec les candidats affiches, ne reutilise pas une "
                + "preuve et n'invente rien. Reponds alors exactement sur deux lignes:")
            .AppendLine("BESOIN_PREUVES=CXX,CYY")
            .AppendLine("RAISON=besoin documentaire concis et actionnable");
        if (!string.IsNullOrWhiteSpace(previousInvalidOutput))
        {
            prompt.AppendLine("PATCH PRECEDENT INVALIDE:")
                .AppendLine(TrimPromptBlock(previousInvalidOutput, 600))
                .Append("ERREUR MECANIQUE: ")
                .AppendLine(TrimPromptValue(previousFailureReason, 300))
                .AppendLine(
                    "Corrige le patch avec des EvidenceId tous distincts, ou demande "
                    + "honnetement de nouvelles preuves si la liste ne permet pas "
                    + "l'affectation semantique requise.");
        }
        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es l'orchestrateur semantique qui repare des cellules invalides dans "
                + "une grille sourcee. Reaffecte uniquement les cellules affichees avec "
                + "les candidats disponibles qui conviennent le mieux a leur ligne, leur "
                + "colonne et leur role. Les autres cellules sont verrouillees. Chaque "
                + "candidat ne peut etre utilise qu'une fois. Reponds uniquement au "
                + "format CXX=E#, ou au protocole BESOIN_PREUVES lorsqu'une vraie "
                + "alternative documentaire manque."),
            SourceBackedAgentMessage.User(prompt.ToString().Trim())
        };
    }

    internal static bool LooksLikeStructuredCandidateEvidenceRequest(
        string? rawOutput)
        => (rawOutput ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Any(static line => line.StartsWith(
                "BESOIN_PREUVES=",
                StringComparison.OrdinalIgnoreCase));

    internal static bool TryReadStructuredCandidateEvidenceRequest(
        string? rawOutput,
        IReadOnlyCollection<int> allowedCellIndexes,
        out string researchNeed,
        out int requestedAdditionalCandidateCount,
        out string failureReason)
    {
        researchNeed = string.Empty;
        requestedAdditionalCandidateCount = 0;
        failureReason = string.Empty;
        var lines = (rawOutput ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0
                                  && !line.StartsWith("```", StringComparison.Ordinal))
            .ToArray();
        if (lines.Length != 2
            || !lines[0].StartsWith(
                "BESOIN_PREUVES=",
                StringComparison.OrdinalIgnoreCase)
            || !lines[1].StartsWith("RAISON=", StringComparison.OrdinalIgnoreCase))
        {
            failureReason =
                "structured_candidate_writer_evidence_request_protocol_invalid";
            return false;
        }

        var allowed = allowedCellIndexes.ToHashSet();
        var requested = lines[0]["BESOIN_PREUVES=".Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Regex.Match(
                value,
                @"^C(\d{1,3})$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
        if (requested.Length == 0
            || requested.Any(static match => !match.Success))
        {
            failureReason =
                "structured_candidate_writer_evidence_request_cells_invalid";
            return false;
        }
        var requestedIndexes = requested
            .Select(static match => int.Parse(match.Groups[1].Value) - 1)
            .ToArray();
        if (requestedIndexes.Distinct().Count() != requestedIndexes.Length
            || requestedIndexes.Any(index => !allowed.Contains(index)))
        {
            failureReason =
                "structured_candidate_writer_evidence_request_cells_invalid";
            return false;
        }

        researchNeed = lines[1]["RAISON=".Length..].Trim();
        if (string.IsNullOrWhiteSpace(researchNeed))
        {
            failureReason =
                "structured_candidate_writer_evidence_request_reason_missing";
            return false;
        }

        requestedAdditionalCandidateCount = requestedIndexes.Length;
        return true;
    }

    internal static bool TryApplyStructuredCandidateConflictRepair(
        StructuredCandidateConflictRepair repair,
        string? rawPatch,
        out string repairedAssignments,
        out string failureReason)
    {
        repairedAssignments = string.Empty;
        failureReason = string.Empty;
        var lines = (rawPatch ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0
                                  && !line.StartsWith("```", StringComparison.Ordinal))
            .ToArray();
        var matches = lines.Select(line => Regex.Match(
                line,
                @"^C(\d{1,3})\s*=\s*(E\d+)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
        var expectedCells = repair.ConflictCellIndexes.ToHashSet();
        var allowedEvidence = repair.AvailableEvidenceIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if (lines.Length != expectedCells.Count
            || matches.Any(static match => !match.Success))
        {
            failureReason =
                "structured_candidate_writer_conflict_patch_count_invalid";
            return false;
        }
        var patch = matches.Select(static match => new StructuredCandidateAssignment(
                int.Parse(match.Groups[1].Value) - 1,
                match.Groups[2].Value.ToUpperInvariant()))
            .ToArray();
        if (patch.Select(static item => item.CellIndex).Distinct().Count()
                != expectedCells.Count
            || patch.Any(item => !expectedCells.Contains(item.CellIndex)
                                 || !allowedEvidence.Contains(item.EvidenceId))
            || patch.Select(static item => item.EvidenceId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != expectedCells.Count)
        {
            failureReason =
                "structured_candidate_writer_conflict_patch_invalid";
            return false;
        }
        var assignmentByCell = repair.Assignments.ToDictionary(
            static item => item.CellIndex,
            static item => item.EvidenceId);
        foreach (var item in patch)
            assignmentByCell[item.CellIndex] = item.EvidenceId;
        repairedAssignments = RenderStructuredCandidateAssignments(
            assignmentByCell.Select(static item =>
                new StructuredCandidateAssignment(item.Key, item.Value)));
        return true;
    }

}
