# Rapprochement des contrats hérités — canonique/Docling

Analyse après `unit-all-v1`, sans nouveau service, modèle, réseau ou test exécuté par cet agent. Le TRX Backend `Backend-7309394c267c4a76825e9d0c6f4c7be1.trx` a été relu ; les quatre échecs ci-dessous sont bien ceux rapportés, distincts des deux échecs Noise gérés par l’agent OCR. Les sources/tests sont de nouveau gelés pour l’exécution root.

| Ancien test | Contrat corrigé | Raison et assertions conservées |
| --- | --- | --- |
| `Project_InfersRepeatedMarginFurnitureAndOmitsItFromRetrieval` | `Project_AnnotatesRepeatedMarginsWithoutDeletingUnknownSourceText` | Le fixture prouvait une répétition en marge, pas l’inutilité de tous ces mots. On exige l’annotation et son nombre de pages observées, sans modifier `IsRepeatedFurniture` des blocs body ni tronquer la dernière instruction. Le texte de marque est accessible ; l’avertissement substantiel reste accessible. Le témoin séparé des vrais compteurs de page reste actif. |
| `NativeTextCoverageReconciliation_RecoversOnlyProvenMissingNavigationText` | `NativeTextCoverageReconciliation_RetainsUnrepresentedOrderWithoutPageWideNavigationInference` | Le natif contenait une ligne dans un autre ordre et une ligne de sommaire ; la présence des mêmes lettres dans la table n’établissait pas l’égalité de cette observation. Les deux lignes sont conservées avec association non affirmée ; la table d’index connue conserve son rôle navigation, sans imposer ce rôle à toute la page. Identité, moteur, blocs, anchors et bundle restent vérifiés. |
| `NativeTextCoverageReconciliation_DoesNotReinjectCoveredHyphenFragmentOrNativeNoise` | `NativeTextCoverageReconciliation_RetainsUnverifiedHyphenAndGlyphObservationsWithoutAssertingASection` | Le fixture n’est pas une césure complète strictement égale : une ligne finit par un fragment de mot et l’autre mêle glyphes douteux et une instruction. Le score/sac de mots ne prouve pas que tout cela est déjà conservé. Les observations restent littérales, le corps canonique reste inchangé, et aucun titre/section n’est attribué. Ce test ne déclare pas ces glyphes fiables ni ce texte sémantiquement compris. |
| `OcrDiagnostics_DoNotInventPerPageAttribution` | `OcrDiagnostics_SeparatesEngineTimingFromUnobservedPageAttribution` | L’ancien cas attendait deux pages tentées pour un timing moteur de count2 alors que le fixture n’avait qu’une page et aucun ID de page observé. On conserve count2/scope dans les champs moteur, déclare les pages inconnues et garde les durées et listes non inventées. |

Le nombre de tests n’est pas réduit par ces renommages. Les preuves historiques des noms et échecs précédents restent inchangées. L’ancienne constante de texte tronqué, devenue sans référence après ce changement d’intention, est supprimée ; aucune suppression de code appelé n’est déduite d’une simple absence de couverture de test.

## Défaut supplémentaire réellement corrigé

La première présence littérale acceptait un fragment entouré de ponctuation : `20` paraissait présent dans `-20`, `20.5`, `20,5`, et `Approved` dans une phrase niant ce statut. Ce n’était pas un mauvais contrat ancien, mais un nouveau défaut. Le lot `literal-boundaries-red` a capturé **4 FAIL et 3 PASS** avant la correction, selon root. La correction compare désormais des observations canoniques complètes avec des fenêtres de lignes natives complètes ; la simple proximité de mots, une sous-chaîne de valeur ou une négation ajoutée ne vaut pas preuve d’égalité.

Un bloc strictement égal malgré un retour à la ligne natif reste sans duplication. Le rapprochement consomme une occurrence canonique par occurrence source, et les anciennes récupérations munies d’un ancrage exact de lignes assurent l’idempotence. Le témoin additionnel de multiplicité/idempotence n’est pas présenté comme un RED exécuté.

## Limites maintenues

Ce rapprochement privilégie la conservation lorsqu’il ne peut pas démontrer l’égalité. Il peut donc rendre accessibles des observations alternatives imparfaites et accroître le volume de contexte. Le statut non attribué ne les transforme pas en texte validé ; la fidélité sémantique, le lien à un sujet, la qualité OCR et le coût/temps doivent rester évalués séparément.

Aucune correction par recette, liste de mots métier, nombre attendu de portions, réponse attendue ou relaxation du protocole de citation n’a été ajoutée. Le prochain résultat à attendre est la suite ciblée/full réellement exécutée, puis le parcours complet du worker avec la génération publiée et les endpoints.
