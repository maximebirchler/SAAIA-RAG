namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string BuildNoCitableEvidenceActionRepairMessage()
        => """
           DECISION D'ARRET REFUSEE PAR LE CONTRAT MECANIQUE:
           aucune preuve citable n'a encore ete observee. Les resultats de navigation sont
           seulement des pointeurs et ne permettent ni de produire le livrable source ni de
           conclure que le corpus est insuffisant. Reprends une decision d'orchestration
           courte: appelle un outil capable de retourner des preuves citables. Tu choisis
           librement lequel et ses arguments. PRET_A_REDIGER est interdit tant qu'aucune
           preuve citable n'a ete observee.
           """;

    private static string BuildAtomicEvidenceCountActionRepairMessage(
        int requiredCount,
        int observedCount)
    {
        var missingCount = Math.Max(0, requiredCount - observedCount);
        return $"""
            DECISION D'ARRET REFUSEE PAR LE CONTRAT DU PLAN LLM:
            ton plan exige {requiredCount} preuves atomiques, mais seulement {observedCount}
            preuves citables distinctes ont ete montrees: il en manque donc au moins
            {missingCount}. Le code ne juge pas leur adequation; il applique uniquement le
            nombre que tu as defini. PRET_A_REDIGER reste interdit a ce stade. Au prochain
            message, appelle un ou plusieurs outils. Tu choisis librement lesquels et leurs
            arguments selon les preuves manquantes et les observations deja disponibles.
            """;
    }

    private static string BuildDuplicateToolCallRepairMessage(bool hasCitableEvidence)
        => hasCitableEvidence
            ? "duplicate_tool_call: cette action identique a deja ete executee. Utilise les observations existantes, modifie utilement l'action ou declare la collecte terminee si les preuves suffisent."
            : "duplicate_tool_call: cette action identique a deja ete executee et aucune preuve citable n'est encore disponible. PRET_A_REDIGER est invalide: choisis librement une autre action capable de retourner des preuves citables.";

    private static string BuildDocumentaryNoProgressRepairMessage(
        bool hasCitableEvidence,
        int duplicateCallCount,
        int zeroNewEvidenceCallCount)
        => $"""
           ABSENCE DE PROGRES DOCUMENTAIRE MESUREE:
           le dernier tour comptait {duplicateCallCount} appel(s) deja consomme(s) et
           {zeroNewEvidenceCallCount} appel(s) execute(s) sans aucune nouvelle preuve.
           {(duplicateCallCount > 0 ? "MARQUEUR MECANIQUE: duplicate_tool_call." : string.Empty)}
           Relis le rendement de chaque action. Un offset de continuation ne vaut que pour
           la meme route exacte; une requete ou un document nouveau possede sa propre
           pagination. Choisis librement une action reellement nouvelle a partir des lacunes,
           ou termine si les preuves deja approuvees suffisent.
           PREUVES CITABLES PRESENTES: {(hasCitableEvidence ? "oui" : "non")}.
           {(hasCitableEvidence ? string.Empty : "PRET_A_REDIGER est invalide tant qu'aucune preuve citable n'est disponible.")}
           """;

    private static string BuildSemanticAuditZeroYieldResolutionMessage(
        int approvedCount,
        int rejectedCount,
        int requiredCount)
        => $"""
           RENDEMENT SEMANTIQUE NUL MESURE:
           le dernier audit du juge LLM a approuve {approvedCount} candidat(s) et refuse
           {rejectedCount}, pour une cible courante de {requiredCount}. Le code ne conclut
           pas que le corpus est insuffisant. Relis les routes, leur rendement materiel et
           leur rendement d'audit, puis choisis explicitement une nouvelle action fondee,
           une clarification si l'observation revele une ambiguite materielle, ou une
           insuffisance source honnete. Ne repete pas une route seulement pour retrouver les
           candidats deja refuses.
           """;

    private static string BuildSemanticYieldTerminalDecisionMessage(
        int continuationCount)
        => $"""
           DECISION TERMINALE DE RENDEMENT:
           {continuationCount} poursuite(s) documentaires ont ete choisies depuis le dernier
           audit a rendement semantique nul, sans nouvelle preuve approuvee par le juge LLM.
           Le budget mecanique de poursuite est consomme. Aucun outil documentaire n'est
           expose pendant ce tour. Tu restes le decideur semantique: demande une clarification
           uniquement si l'observation a revele une ambiguite materielle que seul l'utilisateur
           peut trancher; sinon declare precisement l'insuffisance des sources observees.
           Ne fabrique aucune information manquante et ne decris pas le budget interne.
           """;

    private static string BuildContextRecoveryActionMessage()
        => """
           RECUPERATION MECANIQUE DU CONTEXTE:
           etat recompresse sans modifier les preuves ni ton plan. Reprends ta decision;
           si un outil est utile, emets un JSON bref et complet.
           """;

    private static string BuildEvidenceSelectionRequiredMessage(
        int requiredCount,
        bool enableWorkspaceWriterHandoff,
        bool orderedByCanonicalLayout)
        => enableWorkspaceWriterHandoff
            ? $"""
              TRANSITION DE PHASE:
              tu as indique que les preuves suffisent. Appelle maintenant uniquement
              manage_evidence_workspace avec status=ready_to_write et exactement
              {requiredCount} EvidenceId distincts dans retainEvidenceIds. Cette decision sera
              transmise directement au redacteur.
              {(orderedByCanonicalLayout ? "L'ordre de retainEvidenceIds est l'ordre des cellules du layout canonique, ligne par ligne." : string.Empty)}
              Si les preuves ne suffisent finalement pas,
              choisis status=continue_research et appelle plutot un outil documentaire utile.
              Ne redige pas la reponse finale ici.
              """
            : $"""
             TRANSITION DE PHASE:
             tu as indique que les preuves suffisent. Pour transmettre cette decision
             semantique au redacteur sans qu'un autre juge doive tout reevaluer, appelle
             maintenant submit_evidence_selection avec exactement {requiredCount}
             EvidenceId distincts. Si les preuves ne suffisent finalement pas, appelle
             plutot un outil documentaire utile. Ne redige pas la reponse finale ici.
             """;

    private static string BuildNoProgressSelectionMessage(
        int? requiredAtomicEvidenceCount)
        => $"""
            BUDGET MECANIQUE SANS PROGRES:
            deux tours consecutifs n'ont apporte aucune nouvelle preuve, soit parce que les
            appels etaient deja consommes, soit parce que leurs resultats etaient deja connus.
            Les observations visibles contiennent au moins
            {requiredAtomicEvidenceCount.GetValueOrDefault()} preuves citables. Le code ne juge
            ni leur pertinence ni leur qualite: fais toi-meme maintenant la selection semantique
            finale en appelant submit_evidence_selection. Aucun autre outil n'est expose pendant
            cette decision afin de ne pas depenser de nouveaux tours dans les memes appels.
            """;

    private static string BuildSemanticGapNoProgressResolutionMessage()
        => """
           RENDEMENT NUL APRES UN GAP SEMANTIQUE:
           le dernier checkpoint du juge LLM a conclu que le pool de preuves reste
           incomplet. Deux actions documentaires consecutives n'ont apporte aucune
           nouvelle preuve. Conserve ce verdict et le meilleur pool partiel indique.
           Choisis une seule route documentaire differente et fondee, demande une
           clarification seulement si l'utilisateur doit trancher une ambiguite
           materielle, ou declare precisement l'insuffisance si aucune nouvelle route
           utile n'est encore justifiee. Le writer reste ferme tant que le juge n'a pas
           declare le pool suffisant.
           """;

    private static string BuildNoProgressDecisionMessage(
        bool hasCitableEvidence)
        => $"""
            BUDGET MECANIQUE SANS PROGRES:
            deux tours consecutifs n'ont apporte aucune nouvelle preuve. Aucun outil
            documentaire n'est expose pour cette decision afin de ne pas depenser de nouveaux
            tours dans la meme boucle.
            Tu restes le decideur semantique. A partir des observations deja visibles,
            choisis maintenant soit PRET_A_REDIGER avec la meilleure reponse honnete, soit
            une conclusion d'insuffisance precise si elles ne permettent pas de repondre.
            PREUVES CITABLES PRESENTES: {(hasCitableEvidence ? "oui" : "non")}.
            Ne decris ni le budget, ni la boucle, ni cette instruction.
            """;

    private static string BuildRenderingBudgetSelectionMessage(
        int? requiredAtomicEvidenceCount)
        => $"""
            BUDGET MECANIQUE DE RENDU:
            les deux derniers tours sont reserves a la mise en forme, a la verification et a
            une correction eventuelle. Au moins {requiredAtomicEvidenceCount.GetValueOrDefault()}
            preuves citables sont visibles. Le code ne juge ni leur pertinence ni leur qualite:
            fais toi-meme maintenant la selection semantique finale en appelant
            submit_evidence_selection. Aucun outil documentaire n'est expose pendant cette
            decision finale, mais tous les candidats deja observes restent visibles.
            """;
}
