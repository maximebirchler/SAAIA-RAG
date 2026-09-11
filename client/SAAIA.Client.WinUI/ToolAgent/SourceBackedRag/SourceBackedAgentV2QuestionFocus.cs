using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static void AppendQuestionFocusContext(
        StringBuilder builder,
        SourceBackedIntake intake)
    {
        if (!string.IsNullOrWhiteSpace(intake.RequestedDocumentName))
        {
            builder.Append("DOCUMENT_EXPLICITEMENT_DEMANDE: ")
                .AppendLine(TrimPromptValue(
                    intake.RequestedDocumentName,
                    180));
        }

        if (!SourceBackedQuestionFocus.IsDocumentFamilyOrTypeQuestion(intake))
            return;

        builder.AppendLine("QUESTION_FOCUS: document_family_or_type");
        builder.AppendLine(
            "REGLE_SEMANTIQUE: l'utilisateur demande la famille, le type ou la categorie DU DOCUMENT. "
            + "Decide la reponse depuis les metadonnees source visibles (nom, chemin, categorie ou titre), "
            + "en preferant le segment significatif le plus proche du fichier. "
            + "Donne comme classification principale le niveau documentaire le plus specifique visible; "
            + "un dossier ancetre plus general peut seulement etre ajoute comme contexte. "
            + "Ne le confonds jamais avec la famille du produit, du materiau, du sujet ou d'une norme.");
    }

    private static string BuildEvidenceDocumentMetadata(EvidenceItem item)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(item.DocName))
        {
            builder.Append("fichier=")
                .Append(TrimPromptValue(item.DocName, 120));
        }
        if (!string.IsNullOrWhiteSpace(item.DocPath))
        {
            if (builder.Length > 0)
                builder.Append(" | ");
            builder.Append("chemin=")
                .Append(TrimPromptValue(item.DocPath, 220));
        }
        if (!string.IsNullOrWhiteSpace(item.CategoryPath))
        {
            if (builder.Length > 0)
                builder.Append(" | ");
            builder.Append("categorie=")
                .Append(TrimPromptValue(item.CategoryPath, 140));
        }
        if (item.PageStart is > 0)
        {
            if (builder.Length > 0)
                builder.Append(" | ");
            builder.Append("page=")
                .Append(item.PageStart.Value);
            if (item.PageEnd is > 0 && item.PageEnd != item.PageStart)
            {
                builder.Append('-')
                    .Append(item.PageEnd.Value);
            }
        }

        return builder.Length > 0
            ? builder.ToString()
            : "metadonnees_documentaires_indisponibles";
    }
}
