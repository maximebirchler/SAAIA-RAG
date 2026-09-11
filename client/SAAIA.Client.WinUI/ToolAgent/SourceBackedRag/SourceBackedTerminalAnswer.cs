using System.IO;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static class SourceBackedTerminalAnswer
{
    public static string Build(SourceBackedPipelineResult result)
    {
        var language = NormalizeLanguage(result.Intake.Language);

        var documentResolution = result.Intake.RequestedDocumentResolution;
        if (documentResolution is
            {
                Status: SourceBackedDocumentResolutionStatus.NotFound,
                CatalogObservationComplete: true
            }
            && documentResolution.Candidates.Count == 0
            && documentResolution.ExactMatchCount is not > 0
            && string.Equals(
                result.Intake.NamedReferenceKind,
                "document",
                StringComparison.OrdinalIgnoreCase))
        {
            var requested = string.IsNullOrWhiteSpace(
                result.Intake.RequestedDocumentName)
                ? documentResolution.RequestedReference
                : result.Intake.RequestedDocumentName;
            var fileName = (Path.GetFileName(requested?.Trim()) ?? string.Empty)
                .Replace("\"", string.Empty, StringComparison.Ordinal);
            if (fileName.EndsWith(
                    ".pdf",
                    StringComparison.OrdinalIgnoreCase))
            {
                return language == "fr"
                    ? $"Je n'ai pas trouvé le document demandé \"{fileName}\" dans le corpus indexé. Je ne peux donc pas fournir ses conclusions ni une page source."
                    : $"I did not find the requested document \"{fileName}\" in the indexed corpus, so I cannot provide its conclusions or a source page.";
            }
        }

        if (string.Equals(result.JudgeDecision.Decision, "clarify", StringComparison.OrdinalIgnoreCase))
        {
            if (result.Clarification is { } clarification)
            {
                var lines = new List<string>
                {
                    clarification.Message.Trim()
                };
                lines.AddRange(clarification.Options
                    .Where(static option => !string.IsNullOrWhiteSpace(option))
                    .Select(static option => "- " + option.Trim()));
                return string.Join(Environment.NewLine, lines).Trim();
            }

            return language == "fr"
                ? "J'ai besoin d'une precision avant de chercher dans les sources."
                : "I need one clarification before searching the sources.";
        }

        if (result.Verification is { IsValid: false })
            return language == "fr"
                ? "Je n'ai pas pu produire une reponse avec des sources verifiables. Je prefere m'arreter plutot que de te donner une reponse mal sourcee."
                : "I could not produce an answer with verifiable sources. I will stop here rather than give you a poorly sourced answer.";

        if (result.EvidenceBundle.Items.Count == 0
            && result.EvidenceBundle.RetrievalAttempts.Any(static attempt => attempt.TimedOut || attempt.Busy))
        {
            return language == "fr"
                ? "La recherche documentaire n'a pas abouti dans le delai prevu. Je ne peux pas en conclure que les documents pertinents sont absents ; il faut relancer la recherche."
                : "The document search did not complete within the available time. I cannot conclude that relevant documents are absent; the search must be retried.";
        }

        return language == "fr"
            ? "Les sources consultees ne suffisent pas pour produire une reponse fiable et sourcee."
            : "The consulted sources are not sufficient to produce a reliable source-backed answer.";
    }

    public static string BuildFailure(string language)
        => NormalizeLanguage(language) == "fr"
            ? "Le pipeline source-backed n'a pas pu terminer sa verification. Je prefere m'arreter plutot que de laisser un ancien chemin produire une reponse non verifiee."
            : "The source-backed pipeline could not complete its verification. I will stop here rather than let a legacy path produce an unverified answer.";

    private static string NormalizeLanguage(string? language)
        => string.Equals(language, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
}
