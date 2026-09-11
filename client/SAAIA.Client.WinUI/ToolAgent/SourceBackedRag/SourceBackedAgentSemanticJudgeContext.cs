using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string BuildSemanticJudgeContext(
        SourceBackedIntake intake,
        string semanticPlan,
        WriterDraft draft,
        EvidenceBundle bundle,
        IReadOnlyList<string> observedEvidenceIds,
        bool constrainedContext,
        bool emergencyContextRecovery,
        bool includeCompleteCitedEvidence)
    {
        var citedIds = SourceContractVerifier
            .ExtractEvidenceIds(draft.Answer)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var citedIdSet = citedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredAtomicEvidenceCount =
            ReadRequiredAtomicEvidenceCount(semanticPlan) ?? citedIds.Length;
        var maximumAlternativeCount = constrainedContext
            ? emergencyContextRecovery ? 4 : 8
            : Math.Clamp(
                Math.Max(requiredAtomicEvidenceCount * 2, 12),
                12,
                24);
        var alternativeIds = observedEvidenceIds
            .Reverse()
            .Where(id => !citedIdSet.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maximumAlternativeCount)
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("DEMANDE ORIGINALE:");
        builder.AppendLine(intake.UserQuestion);
        if (intake.ExplicitConstraints.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("CONTRAINTES EXPLICITES:");
            foreach (var constraint in intake.ExplicitConstraints)
                builder.AppendLine(constraint);
        }
        builder.AppendLine();
        builder.AppendLine("PLAN SEMANTIQUE DU LLM:");
        builder.AppendLine(semanticPlan);
        builder.AppendLine();
        AppendSemanticColumnRoles(
            builder,
            intake.CanonicalColumnSemanticRoles);
        if (intake.CanonicalColumnSemanticRoles is { Count: > 0 })
            builder.AppendLine();
        builder.AppendLine("BROUILLON A JUGER:");
        builder.AppendLine(draft.Answer);
        builder.AppendLine();
        builder.AppendLine("PREUVES CITEES A AUDITER EXHAUSTIVEMENT:");
        foreach (var id in citedIds)
        {
            if (!bundle.ById.TryGetValue(id, out var item))
                continue;

            AppendSemanticReviewEvidence(
                builder,
                item,
                includeCompleteCitedEvidence
                    ? int.MaxValue
                    : constrainedContext
                    ? emergencyContextRecovery ? 44 : 72
                    : 180,
                constrainedContext);
        }
        if (alternativeIds.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine(
                "ALTERNATIVES DEJA OBSERVEES (seulement pour une revision eventuelle):");
            foreach (var id in alternativeIds)
            {
                if (!bundle.ById.TryGetValue(id, out var item))
                    continue;

                AppendSemanticReviewEvidence(
                    builder,
                    item,
                    constrainedContext
                        ? emergencyContextRecovery ? 32 : 48
                        : 100,
                    constrainedContext);
            }
        }

        if (emergencyContextRecovery)
        {
            builder.AppendLine();
            builder.AppendLine(
                "REPARATION DE CONTEXTE: appelle maintenant submit_semantic_review avec un JSON bref et complet.");
        }

        return builder.ToString().Trim();
    }

    private static void AppendSemanticReviewEvidence(
        StringBuilder builder,
        EvidenceItem item,
        int maximumExcerptCharacters,
        bool constrainedContext)
    {
        builder
            .Append("- ")
            .Append(item.EvidenceId)
            .Append(" | valeur=")
            .Append(TrimPromptValue(
                GetEvidenceDisplayValue(item),
                constrainedContext ? 112 : 140))
            .Append(" | fichier=")
            .Append(TrimPromptValue(
                item.DocName ?? item.DocPath ?? item.DocId,
                constrainedContext ? 72 : 100))
            .Append(" | p.")
            .Append(item.PageStart?.ToString() ?? "?");
        if (!constrainedContext)
        {
            builder
                .Append(" | type_source=")
                .Append(item.SelectionHints.TryGetValue("kind", out var kind)
                    ? TrimPromptValue(kind, 40)
                    : item.SourceKind)
                .Append(" | preuve_structuree=")
                .Append(item.SelectionHints.TryGetValue("hasGroundedEvidence", out var grounded)
                    ? TrimPromptValue(grounded, 10)
                    : "inconnu");
        }

        builder
            .Append(" | preuve=")
            .AppendLine(TrimPromptValue(item.Excerpt, maximumExcerptCharacters));
    }
}
