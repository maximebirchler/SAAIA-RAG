// Shared LLM prompt text, independent of the writer's output transport.
namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string BuildSelectedWriterFidelityInstructions(bool contentClaimMode)
        => contentClaimMode
            ? """
                Respecte le nombre, la structure, la portee et l'utilite demandes. Lis chaque
                EXTRAIT dans l'ordre. N'ajoute aucun detail suppose et ne parle ni des outils
                ni des controles. Distingue strictement une liste de composants ou
                d'ingredients des actions ordonnees: la presence d'un composant ne prouve
                jamais qu'il faut l'utiliser dans l'etape courante. Respecte l'ordre explicite
                des actions, ajouts, temperatures et attentes. Si les preuves du pool ne
                permettent pas une formulation complete et honnete, n'invente rien.
                """
            : """
                Lis les EXTRAITS dans l'ordre documentaire. Le titre, l'identite et les
                composants visibles definissent l'element: ne le renomme pas, ne le reclasse
                pas et n'en omets aucun composant qui changerait sa nature. N'ajoute aucun
                detail suppose et ne parle ni des outils ni des controles. Distingue
                strictement une liste de composants ou d'ingredients des actions ordonnees:
                la presence d'un composant ne prouve jamais qu'il faut l'utiliser dans l'etape
                courante. Respecte l'ordre explicite des actions, ajouts, temperatures et
                attentes. Si la source ajoute un composant apres une action, ne l'introduis
                pas aussi avant cette action.

                Adapte le detail a la demande. Pour une simple option selectionnee, produis
                une phrase de 30 mots maximum avec son nom puis seulement les faits visibles
                utiles. Ne donne une procedure complete que si elle est demandee; si tu en
                annonces une, fournis toutes les etapes essentielles visibles.
                """;
}
