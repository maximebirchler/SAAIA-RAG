using System.IO;
using SAAIA.Client.WinUI.Localization;

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
                return Pick(language,
                    $"Je n'ai pas trouvé le document demandé \"{fileName}\" dans le corpus indexé. Je ne peux donc pas fournir ses conclusions ni une page source.",
                    $"I did not find the requested document \"{fileName}\" in the indexed corpus, so I cannot provide its conclusions or a source page.",
                    $"No encontré el documento solicitado \"{fileName}\" en el corpus indexado. No puedo proporcionar sus conclusiones ni una página fuente.",
                    $"Não encontrei o documento solicitado \"{fileName}\" no corpus indexado. Não posso fornecer as suas conclusões nem uma página fonte.",
                    $"Ich habe das angeforderte Dokument \"{fileName}\" im indexierten Korpus nicht gefunden. Daher kann ich weder seine Schlussfolgerungen noch eine Quellenseite angeben.",
                    $"Non ho trovato il documento richiesto \"{fileName}\" nel corpus indicizzato. Non posso quindi fornire le sue conclusioni né una pagina fonte.");
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

            return Pick(language,
                "J'ai besoin d'une precision avant de chercher dans les sources.",
                "I need one clarification before searching the sources.",
                "Necesito una aclaración antes de buscar en las fuentes.",
                "Preciso de um esclarecimento antes de pesquisar nas fontes.",
                "Ich benötige eine Klarstellung, bevor ich in den Quellen suche.",
                "Ho bisogno di un chiarimento prima di cercare nelle fonti.");
        }

        if (result.Verification is { IsValid: false })
            return Pick(language,
                "Je n'ai pas pu produire une reponse avec des sources verifiables. Je prefere m'arreter plutot que de te donner une reponse mal sourcee.",
                "I could not produce an answer with verifiable sources. I will stop here rather than give you a poorly sourced answer.",
                "No pude producir una respuesta con fuentes verificables. Prefiero detenerme antes que darte una respuesta mal documentada.",
                "Não consegui produzir uma resposta com fontes verificáveis. Prefiro parar a dar uma resposta mal fundamentada.",
                "Ich konnte keine Antwort mit überprüfbaren Quellen erstellen. Ich halte hier an, statt eine unzureichend belegte Antwort zu geben.",
                "Non sono riuscito a produrre una risposta con fonti verificabili. Preferisco fermarmi piuttosto che darti una risposta non adeguatamente documentata.");

        if (result.EvidenceBundle.Items.Count == 0
            && result.EvidenceBundle.RetrievalAttempts.Any(static attempt => attempt.TimedOut || attempt.Busy))
        {
            return Pick(language,
                "La recherche documentaire n'a pas abouti dans le delai prevu. Je ne peux pas en conclure que les documents pertinents sont absents ; il faut relancer la recherche.",
                "The document search did not complete within the available time. I cannot conclude that relevant documents are absent; the search must be retried.",
                "La búsqueda documental no terminó en el tiempo disponible. No puedo concluir que falten documentos pertinentes; hay que repetir la búsqueda.",
                "A pesquisa documental não terminou no tempo disponível. Não posso concluir que faltam documentos relevantes; é necessário repetir a pesquisa.",
                "Die Dokumentensuche wurde nicht innerhalb der verfügbaren Zeit abgeschlossen. Daraus folgt nicht, dass relevante Dokumente fehlen; die Suche muss wiederholt werden.",
                "La ricerca documentale non si è conclusa nel tempo disponibile. Non posso dedurne che manchino documenti pertinenti; occorre ripetere la ricerca.");
        }

        return Pick(language,
            "Les sources consultees ne suffisent pas pour produire une reponse fiable et sourcee.",
            "The consulted sources are not sufficient to produce a reliable source-backed answer.",
            "Las fuentes consultadas no bastan para producir una respuesta fiable y documentada.",
            "As fontes consultadas não são suficientes para produzir uma resposta fiável e fundamentada.",
            "Die konsultierten Quellen reichen nicht für eine zuverlässige, belegte Antwort aus.",
            "Le fonti consultate non bastano per produrre una risposta affidabile e documentata.");
    }

    public static string BuildFailure(string language)
        => Pick(language,
            "Le pipeline source-backed n'a pas pu terminer sa verification. Je prefere m'arreter plutot que de laisser un ancien chemin produire une reponse non verifiee.",
            "The source-backed pipeline could not complete its verification. I will stop here rather than let a legacy path produce an unverified answer.",
            "No se pudo completar la verificación de las fuentes. Prefiero detenerme antes que producir una respuesta no verificada.",
            "Não foi possível concluir a verificação das fontes. Prefiro parar a produzir uma resposta não verificada.",
            "Die Quellenprüfung konnte nicht abgeschlossen werden. Ich halte hier an, statt eine ungeprüfte Antwort zu erzeugen.",
            "Non è stato possibile completare la verifica delle fonti. Preferisco fermarmi piuttosto che produrre una risposta non verificata.");

    private static string NormalizeLanguage(string? language)
        => LocalizedStrings.NormalizeLanguage(language);

    private static string Pick(string? language, string fr, string en, string es, string pt, string de, string it)
        => NormalizeLanguage(language) switch
        {
            "en" => en,
            "es" => es,
            "pt" => pt,
            "de" => de,
            "it" => it,
            _ => fr
        };
}
