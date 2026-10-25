# Résolution des constats client C01–C08 — clôture des contrôles hors réseau

Statut : **CORRECTIFS VALIDÉS DANS LES PÉRIMÈTRES DÉCLARÉS ; CAPACITÉ DE RÉEMPLOI NON AJOUTÉE**. Ce document complète l’audit gelé `client-agent-audit.md` sans le réécrire. Seuls les lancements orchestrés par root font foi ; cet agent n’a exécuté ni .NET, ni Git, ni réseau, ni accès à un secret.

## Éléments causaux déjà acquis

Le lot rouge client comprend9 cas exécutés :8 échecs attendus et1 témoin réussi. Il teste les décisions de scope, la contrainte située tard dans la demande, le handoff contextualisé, le log ordinaire, les axes complets et la propriété du budget. L’empreinte globale a varié durant ce passage, mais la comparaison des deux inventaires montre seulement une modification d’un test backend de lease et l’ajout d’un script de campagne ; aucun source client ou contrat de ces témoins n’a changé pendant ce passage.

Le premier lancement vert a trouvé une erreur de compilation CS8999 due à l’indentation de fermeture d’une raw string dans le prompt système. L’indentation seule a été corrigée puis le code a été regelé. Les12 contrôles Core de ce lancement ont réussi. La suite Unit globale `unit-final-v1` est terminée : backend2450/2450, contrats20/20, Core82/82, client2409 réussis et5 échecs sur2414. Les cinq échecs ont révélé une attente de scope devenue obsolète, une redaction appliquée par erreur à un formatter aussi utilisé dans un prompt, deux assertions d’ancien transport du contexte et une lecture de journal avant disposal du tour. La deuxième révision est gelée pour validation ; ces échecs ne sont pas masqués comme des réussites.

## Résolution par constat

| Constat | Changement effectif | Limite et validation à retenir |
|---|---|---|
| C01 : hypothèse de scope réimposée | Les arguments ultérieurs du modèle sont conservés ; le menu n’impose plus les catégories hypothétiques comme enum. `SourceBackedResearchScopePolicy` admet séparément des scopes obligatoires typés, refuse leur omission ou une sortie de portée. | Les témoins couvrent B, absence de scope, A obligatoire, descendant A et rejet de B. La révision2 ajoute scopePolicy et une ancre copiée de la demande dans les schémas source/grille, puis la mission et le mapper d’intake. Une restriction sans ancre valide est refusée. Les lectures ciblées/batch contrôlent aussi la catégorie connue des preuves ; une preuve B ou non résolue ne peut pas être lue sous restriction A. Les outils serveur read_source et les ACL ne relèvent pas de cette garde client. Les ACL backend et gardes de document ne sont pas supprimées. |
| C02 : contexte de suivi perdu | Nouveau contexte partagé séparé de RequestText, historique user/assistant chronologique, jamais probant, max12 tours/16000 caractères ; aucune instruction system transportée. Les sept constructions de prompts serveur le reçoivent séparément et doivent demander clarification si un contexte essentiel tronqué manque. | Le backend valide rôle/bornes/non-preuve. Le témoin couvre une révocation chronologique. Un clone de handoff dans ApplyPlannerSelectionMode avait omis ce champ ; la revue indépendante l’a détecté après le premier gel. L’agent backend possède le témoin causal grille/raffinement et son correctif après capture rouge root ; les résultats verts restent à confirmer séparément. |
| C03 : demande tronquée/flags étiquetés contraintes | `SourceBackedUserRequestPrompt` est l’unique propriétaire du texte de la demande dans24 propriétaires de prompts ; il conserve l’intégralité de la demande. Les extraits documentaires restent bornés. RiskFlags est séparé de ExplicitConstraints dans l’intake. | Un dépassement de fenêtre ne justifie plus la perte silencieuse d’une consigne ; la préadmission mesurée existante reste active et sa sonde de blocage inclut la longueur de la demande. Les deux témoins long contexte/urgence passent dans le client complet ; pas de promesse qu’une demande arbitrairement longue rentre dans une petite fenêtre. |
| C04 : logs hors politique | Le log de demande et d’arguments, les recherches et diagnostics de planning concernés suivent `ClientLog.Content`. Les exceptions n’écrivent plus leur détail par défaut. Le mode contenu explicite reste disponible. | Témoin avec marqueur privé synthétique dans un vrai tour d’orchestrateur. Ce n’est pas un scan de toutes les données Windows ni une preuve d’absence universelle de tout contenu dans chaque ancien appel de log. |
| C05 : sondes incomplètes | Les activations utilisent source_step ; une politique à valeurs fermées expose les codes capacité/famille/fournisseur dans les deux journaux, sans ouvrir les arguments ou plans. Le budget affiché vient de l’instantané effectif s’il existe ; les options du runner suivent le fournisseur actif. | Témoin sur un événement réel de frontière locale et absence du marqueur utilisateur. Pas d’élargissement global des champs texte admis. |
| C06 : bornes incompatibles | Le schéma et le lecteur natif s’accordent sur12 colonnes ; le lecteur refuse13 sans tronquer les axes. Le handoff conserve80 lignes et12 colonnes, et le backend accepte ces mêmes collections. | Aucun plafond financier ou nombre d’unités serveur n’a été augmenté. Témoins40 labels et12/13colonnes. |
| C07 : réemploi/diversité | `SourceBackedSelectionMultiplicity` centralise la capacité actuelle : affectations nommées distinctes. Le validateur de grille réutilise ce contrat ; le prompt annonce cette limite. | **Le réemploi d’un même élément dans plusieurs cellules n’est pas implémenté.** Deux témoins vérifient la grille distincte acceptée et le réemploi refusé ; aucun desserrage silencieux de la provenance. Il faudra une version de contrat séparant inventaire des preuves et affectations réutilisables. |
| C08 : budget du runner | `SourceBackedRunBudgetScope` crée le budget d’un run autonome et réutilise un budget parent sans le remettre à zéro. Le scope est libéré à la fin ou à l’exception. | Témoins run autonome et budget parent, sans fournisseur. La comptabilisation des appels reste celle du transport et du contexte partagé, pas un deuxième compteur indépendant. |

## Responsabilités extraites

Les nouveaux propriétaires couvrent la construction du contexte avancé, sa politique partagée de validation, le texte de référence de la demande, la distinction scope obligatoire/hypothèse, les valeurs autorisées des sondes de routage, la propriété du budget du run et la capacité d’affectation distincte. Ce sont des contrats réutilisés par plusieurs chemins ; les autres changements de prompts utilisent ces propriétaires au lieu de créer davantage de fichiers partiels indépendants.

L’ancienne fonction IncludeUserInstanceContext a été retirée après recherche de toutes ses références : sa seule utilisation a été remplacée par le contexte explicite commun. Aucun nettoyage général de code supposé mort n’a été entrepris.

## Périmètre des fichiers client/contrats et adaptateurs possédés

Inventaire de contenu comparé au passage avant les témoins rouges, sans Git. Les changements coordonnées d’AdvancedAnalysisEndpoints.cs (validation et bornes) et du clone Planning.cs sont portés par l’agent backend ; leurs propres tests/provenances sont distincts.

- `client/SAAIA.Client.Core.Tests/NativeRouterResponseReaderTests.cs`
- `client/SAAIA.Client.Core/NativeRouting/NativeRouterResponseReader.cs`
- `client/SAAIA.Client.ToolAgent.Tests/ClassifiedSourceWorkContractTests.cs`
- `client/SAAIA.Client.ToolAgent.Tests/GlobalClientAuditRegressionTests.cs`
- `client/SAAIA.Client.ToolAgent.Tests/NativeRouterPromptBudgetTests.cs`
- `client/SAAIA.Client.ToolAgent.Tests/SourceBackedAgentV2Tests.Planning.cs`
- `client/SAAIA.Client.WinUI/Services/ClientLog.cs`
- `client/SAAIA.Client.WinUI/Services/ClientRoutingTracePolicy.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/AdvancedConversationContextBuilder.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/NativeRouting/NativeRouterToolCatalog.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/RouterPlan.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedIntakeFactory.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentCandidateAuditReview.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentCandidateCollection.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentCompactFollowUp.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentDirectInitialResearchPrompt.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentDocumentaryToolBatch.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentFastEvidenceReviewPrompt.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentFlatEvidenceAdequacyContext.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentInitialResearchAction.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentNavigationContextBatch.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentNavigationContextExpansion.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentResearchTransition.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentResearchTransitionContextRecovery.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentResolvedCandidateAudit.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentSemanticAnswerTransaction.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentSemanticCandidateDefinition.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentSemanticCandidateStrategyPrompt.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentSemanticColumnRoles.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentSemanticPlan.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentSemanticResolutionWriterReview.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentSingleSelectionScopeReview.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentStructuredAssignmentBatchReview.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentStructuredAssignmentContinuation.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentStructuredAssignmentRevisionParsing.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentStructuredCandidateContract.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentStructuredCandidateRepair.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentStructuredCandidateWriter.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentV2Prompts.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentV2Runner.AcquireIterationCompletion.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentV2Runner.ProcessIterationSubmissionsAndTools.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentV2Runner.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentV2SystemPrompt.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentV2WriterPromptContext.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedPipelineContracts.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedResearchScopePolicy.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedRunBudgetScope.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedSelectionMultiplicity.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedStructuredValueTypeDecisionBatchPrompt.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedUserRequestPrompt.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.AdvancedAnalysisHandoff.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.ClassifiedWorkContract.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.NativeRouterPromptAndProtocol.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.RagMultiSearchExecution.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.RagSearchExecution.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.RagTrace.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.RunPipeline.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedAgentV2.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedEvidenceExplorationPasses.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedOptionCandidateExtraction.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedOptionCandidateSelection.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningCandidateDiagnostics.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningCandidateSelection.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedRag.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedRagToolExecutor.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.ToolExecutionPipeline.cs`
- `contracts/SAAIA.Contracts/AdvancedAnalysisClientLayoutLimits.cs`
- `contracts/SAAIA.Contracts/AdvancedAnalysisContracts.cs`
- `contracts/SAAIA.Contracts/AdvancedAnalysisConversationContext.cs`
- `backend/SAAIA.Backend/AdvancedAnalysis/OpenAiCompatibleAdvancedAnalysisProvider.PromptsAndEvidence.cs`
- `backend/SAAIA.Backend.Tests/OpenAiCompatibleAdvancedAnalysisProviderTests.ConversationContext.cs`

Suppression vérifiée après retrait des derniers appels/tests :

- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentV2ToolArgumentAdjustments.cs`

## Conditions avant clôture

Ne pas déclarer la révision2 validée avant ses témoins ciblés et la nouvelle suite. Le clone serveur possède son propre test causal et sa propre validation. Ne pas déclarer C07 comme une prise en charge du réemploi. Conserver les échecs et compilations rouges comme historique ; ne pas additionner les tests ciblés aux mêmes tests de la suite complète. Aucun essai de planning Terra n’a été effectué par cet agent ; le statut produit reste TESTE_NON_APPROUVE.


## Révision2 et extraction des propriétaires

La révision2 retire les trois helpers d’ajustement devenus sans effet, leur seul appel et le paramètre qui n’avait plus d’autre usage. La recherche des références restantes est vide. Les témoins de scope n’appellent plus un helper privé par réflexion : trois parcours complets de runner enregistrent réellement les outils/arguments exécutés après la transition de navigation. Le scope des nouvelles routes est maintenant une décision explicite du menu, y compris la chaîne vide pour ouvrir la recherche.

Le nouveau `SourceBackedIntakeFactory` est appelé par le vrai parcours de production. Un témoin traverse le vrai parser natif, cette factory et la garde d’outil afin de distinguer hypothèse de recherche et restriction utilisateur sans relabeller les flags internes. Les règles de capacité des axes sont sorties vers `AdvancedAnalysisClientLayoutLimits`, séparées de la politique de conversation.

La correction de log ne doit pas masquer le contenu transmis au modèle : les appels de log utilisent maintenant `FormatPlanningLogValue`, tandis que le formatter partagé par les prompts garde ses données de stratégie. Les contraintes de l’utilisateur restent présentes dans la demande entière transmise au rédacteur ; aucune limite de corpus ni ACL serveur n’est supprimée.


## État de la validation après révision2

`unit-final-v2` : backend2456/2456, contrats20/20, Core82/82, client2418 réussis sur2419. Le seul échec restant était la liste exacte des clés attendues dans le test de contrat du routeur : elle ne contenait pas encore les deux champs nouveaux de provenance. Ce contrat attendu a été actualisé, avec assertions de contenu supplémentaires et sortie des tailles sérialisées réelles. Les limites de taille restent4000 caractères pour le système et3500 par schéma : aucune augmentation pour rendre le test vert. Le code produit est inchangé depuis cette révision ; le client complet a été relancé séparément par root et réussit2419/2419.


## Résultat final relu indépendamment

Le manifeste `client-final-v3/manifest.json` est réussi, `sourceChangedDuringRun=false`. Le TRX a été reparsé : **2419 exécutés,2419 réussis,0 échec,0 non exécuté,0 avertissement**. Ce total inclut21 cas GlobalClientAuditRegressionTests et les3 parcours de scope exécutés par le vrai runner. Ces sous-totaux ne sont pas à additionner aux2419.

L’ancrage de production `winui-production-build.json` indique Release/x64/win-x64, testHooks=false, exitCode0, DLL SHA256 `134F92E49B7B24EEB05EF41541696EE28E691C7DDBC7C8E864F8F34F25FB5ECC`. Il précise `nativeUiStarted=false` : ce build ne constitue pas une nouvelle campagne d’interface native. Les anciennes observations WinUI restent attachées à leurs builds réels.

Les mesures sérialisées effectives du test de contrat sont : système3998 caractères sous la limite inchangée4000 ; schémas overview490, source2871, grille3253, missing-input780, clarification1174 et opérationnel579, tous sous3500. Ce sont des caractères réellement produits par les builders testés, pas une mesure de tokens du fournisseur. La marge système est faible et doit rester surveillée ; aucun seuil n’a été augmenté.

Les autres périmètres ont leurs propres résultats orchestrés par root : backend2456/2456, contrats20/20 et Core82/82. Ils ne constituent pas avec le client final un unique lancement identique ; les empreintes/dépendances doivent être rapprochées par l’ancrage de campagne. La campagne PostgreSQL d’intégration271 réussis/2 skips historiques est séparée.

Les rouges initiaux, l’erreur de compilation et les deux suites intermédiaires restent disponibles. C07 reste explicitement une limite de capacité ; le contrôle des catégories client n’est pas un mécanisme ACL ni un contrôle des outils serveur read_source. Les exigences utilisateur sont conservées dans la demande entière du rédacteur, et le contexte conversationnel est non probant. Aucun jugement « logiciel parfait » ni validation sémantique du planning Terra n’est déduit de ces tests. Statut produit : **TESTE_NON_APPROUVE**.
