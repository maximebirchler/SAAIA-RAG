using System.Text.RegularExpressions;

namespace SAAIA.Client.ToolAgent.Tests;

internal enum SemanticResolutionV5PromptPolicyArm
{
    Current,
    Candidate
}

internal static class SemanticResolutionV5PromptPolicyExperiment
{
    internal const string CurrentSupportClause =
        "PREUVES_DE_SUPPORT: evidenceIds contient toutes et seulement les preuves "
        + "necessaires au claim, meme si le livrable comporte une seule unite; la "
        + "cardinalite du livrable ne limite pas la cardinalite des preuves.";

    internal const string CurrentContextClause =
        "PRIORITE_CONTEXTUELLE: context prime sur research lorsqu'une preuve visible "
        + "est un localisateur concret non encore ouvert vers le contenu demande; "
        + "research s'applique s'il n'existe aucun localisateur exploitable ou si son "
        + "contenu ouvert reste insuffisant.";

    internal const string CurrentReasonClause =
        "RAISON_CONCISE: reason doit etre complete, concise et rester sous la borne du contrat.";

    internal const string CandidateSupportClause =
        "ORDRE_PREUVES: pour answer, place d'abord la preuve qui supporte directement "
        + "le resultat choisi ou le claim principal, puis les autres preuves necessaires; "
        + "si aucune preuve principale unique n'existe, conserve l'ordre documentaire.";

    internal const string CandidateContextClause =
        "LOCALISATEUR_EXECUTABLE: context prime sur research lorsqu'une preuve visible "
        + "indique ou ouvrir le contenu demande, notamment par document, sommaire, index, "
        + "section, page, tableau, annexe ou ancre; choisis context avec cet unique "
        + "evidenceId meme si le contenu n'est pas encore dans le pool. Research s'applique "
        + "seulement s'il n'existe aucun localisateur exploitable ou si le contenu deja "
        + "ouvert reste insuffisant.";

    internal const string CandidateReasonClause =
        "RAISON_COURTE: reason contient au maximum deux phrases completes et 240 "
        + "caracteres, terminees par une ponctuation.";

    internal static IReadOnlyList<(string role, string content)> Apply(
        IReadOnlyList<(string role, string content)> source,
        SemanticResolutionV5PromptPolicyArm arm)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = source.Select(static message => (message.role, message.content)).ToArray();
        if (arm == SemanticResolutionV5PromptPolicyArm.Current)
            return clone;

        var systemIndexes = clone
            .Select((message, index) => (message, index))
            .Where(static item => string.Equals(
                item.message.role,
                "system",
                StringComparison.Ordinal))
            .Select(static item => item.index)
            .ToArray();
        if (systemIndexes.Length != 1)
        {
            throw new InvalidOperationException(
                $"A539 requires exactly one system message; observed {systemIndexes.Length}.");
        }

        var systemIndex = systemIndexes[0];
        var currentSystem = clone[systemIndex].content;
        var candidateSystem = ReplaceExactlyOnce(
            currentSystem,
            CurrentSupportClause,
            CandidateSupportClause,
            "support");
        candidateSystem = ReplaceExactlyOnce(
            candidateSystem,
            CurrentContextClause,
            CandidateContextClause,
            "context");
        candidateSystem = ReplaceExactlyOnce(
            candidateSystem,
            CurrentReasonClause,
            CandidateReasonClause,
            "reason");

        foreach (var removed in new[]
                 {
                     CurrentSupportClause,
                     CurrentContextClause,
                     CurrentReasonClause
                 })
        {
            if (candidateSystem.Contains(removed, StringComparison.Ordinal))
                throw new InvalidOperationException("A539 source clause survived transformation.");
        }
        foreach (var added in new[]
                 {
                     CandidateSupportClause,
                     CandidateContextClause,
                     CandidateReasonClause
                 })
        {
            if (CountOrdinal(candidateSystem, added) != 1)
                throw new InvalidOperationException("A539 candidate clause multiplicity is invalid.");
        }

        clone[systemIndex] = (clone[systemIndex].role, candidateSystem);
        return clone;
    }

    internal static string RecoverCurrentSystem(string candidateSystem)
    {
        var current = ReplaceExactlyOnce(
            candidateSystem,
            CandidateSupportClause,
            CurrentSupportClause,
            "support-reverse");
        current = ReplaceExactlyOnce(
            current,
            CandidateContextClause,
            CurrentContextClause,
            "context-reverse");
        return ReplaceExactlyOnce(
            current,
            CandidateReasonClause,
            CurrentReasonClause,
            "reason-reverse");
    }

    internal static bool IsCandidateReasonValid(string? reason)
    {
        var value = reason?.Trim() ?? string.Empty;
        if (value.Length is < 8 or > 240)
            return false;
        if (value[^1] is not ('.' or '!' or '?'))
            return false;
        var sentenceTerminators = Regex.Matches(value, "[.!?]").Count;
        return sentenceTerminators is >= 1 and <= 2;
    }

    internal static string Classify(
        bool campaignIntegrityValid,
        int currentExact,
        int currentReasonValid,
        int candidateExact,
        int candidateReasonValid,
        int candidateSafetyValid)
    {
        if (!campaignIntegrityValid)
            return "campaign_invalid";
        if (candidateSafetyValid < 2)
            return "candidate_rejected_safety";
        if (candidateExact < 4)
            return "candidate_rejected_accuracy";
        if (candidateReasonValid < 4)
            return "candidate_rejected_reason";
        if (currentExact == 4 && currentReasonValid == 4)
            return "paired_equal_perfect_inconclusive";
        return candidateExact > currentExact || candidateReasonValid > currentReasonValid
            ? "candidate_supported"
            : "campaign_invalid";
    }

    private static string ReplaceExactlyOnce(
        string source,
        string oldValue,
        string newValue,
        string label)
    {
        var count = CountOrdinal(source, oldValue);
        if (count != 1)
        {
            throw new InvalidOperationException(
                $"A539 {label} source clause count must be 1; observed {count}.");
        }
        return source.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static int CountOrdinal(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while (true)
        {
            var index = source.IndexOf(value, offset, StringComparison.Ordinal);
            if (index < 0)
                return count;
            count++;
            offset = index + value.Length;
        }
    }
}
