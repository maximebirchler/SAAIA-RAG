# Correction du contrôle de recherche après le premier cas Luna

Date : 2026-10-25. Cette résolution complète `prompt-and-tool-review.md`, sans modifier les traces ni les preuves historiques. Le premier résultat payant reste une insuffisance observée ; aucune réussite réelle n'est déduite des changements décrits ici.

## Changements produit

- `backend/SAAIA.Backend/AdvancedAnalysis/OpenAiCompatibleAdvancedAnalysisProvider.cs` : suppression de l'arrêt systématique après un premier suivi des demandes non structurées. La boucle conserve le même plafond cumulé d'appels configuré et les mêmes réserves de synthèse/critique. Elle s'arrête si le batch entier a déjà été exécuté ou si le budget documentaire ne permet plus de travail. Aucun plafond financier ou d'appels n'est augmenté. Les observations de recherche conservent maintenant les diagnostics de récupération, résolution documentaire et dégradation, en plus des diagnostics de lecture/recherche littérale.
- Le libellé sans preuve décrit désormais une limite de récupération : « Je n’ai pas pu retrouver de passage documentaire vérifié… », avec équivalents anglais et allemand, plutôt qu'une affirmation sur l'absence du contenu dans la documentation. Le résultat reste `insufficient_documentation`, sans claim inventée.
- `OpenAiCompatibleAdvancedAnalysisProvider.Planning.cs` : une requête ciblée du modèle satisfait l'obligation de recherche dans un document nommé ; le code ne la précède plus systématiquement d'une copie longue de la question. L'identité est transmise par `documentHint`, séparément du texte de contenu. Les identifiants connus accolés à une requête de contenu sont retirés du texte recherché, y compris lors d'un suivi. Les requêtes portant seulement une identité de document restent possibles. Une requête de secours est encore créée si un document requis n'est pas couvert dans le plan, dans les limites existantes. Les requêtes attachées à une source déjà observée conservent leur identité canonique et ne sont pas réécrites de cette façon.
- `OpenAiCompatibleAdvancedAnalysisProvider.PromptsAndEvidence.cs` : contrat et exemple Planner montrent `documentHint`. Les consignes génériques demandent quelques termes distinctifs, puis une formulation plus courte ou une autre graphie en cas de résultat vide ; elles rappellent que la récupération sparse peut conjoindre les termes. La demande complète demeure intacte et séparée du texte de recherche. Le Review reçoit son nombre de tours encore disponibles après le présent appel, la réserve de synthèse et le budget documentaire. Les retours opérationnels ne sont jamais présentés comme preuve ni comme autorisation de lecture.
- `OpenAiCompatibleAdvancedAnalysisProvider.NativeResearchTools.cs` : les résultats d'outils natifs réutilisent le même résumé compact de diagnostic et les mêmes observations de résolution/dégradation que les retours JSON du contrôleur.
- Nouveau `OpenAiCompatibleAdvancedAnalysisProvider.ResearchDiagnostics.cs` : responsabilité de projection des diagnostics du contrôleur. Seuls les comptes opérationnels sont exposés, avec au plus 16 phases, 16 motifs de rejet et 16 retrievers dégradés ; les codes sont bornés. Les listes détaillées de candidats, snippets et copies brutes de la requête du DTO de récupération ne sont pas recopiées dans les prompts. Un chemin de résolution de plus de 2 000 caractères est omis entièrement, avec `resolvedDocPathOmitted=true` : aucune identité tronquée n'est fabriquée et le filtre de recherche exécuté n'est pas modifié. Les contrats rappellent que le nombre de candidats est un échantillon borné, sans valeur d'inventaire exhaustif.

Root possède séparément les ajouts au contrat et l'alimentation des diagnostics dans `AdvancedAnalysisToolGateway.cs`. Ce rapport n'attribue pas ces éditions à l'auteur de la correction du provider.

## Garanties conservées et portée

Une résolution de document, même exacte, reste une métadonnée. Elle ne produit ni `sourceKey` admissible ni preuve. `read_source` et `find_source_text` exigent toujours une source déjà observée et validée. Les contrôles de tenant, document, révision, hash et citation ne sont pas supprimés. Sans preuve, le retour sûr d'insuffisance précédant le Writer reste en place.

Aucune opération initiale de découverte documentaire navigable n'est ajoutée dans ce lot. Cette possibilité serait un chantier distinct avec ses propres contrôles d'identité. Aucun enforcement global supplémentaire de catégorie utilisateur ou d'ACL n'est revendiqué. Le budget n'est pas élargi et le choix des requêtes reste générique : aucune recette, réponse attendue ou page cible n'est inscrite dans le produit.

La cause du troisième zéro brut a été établie séparément par les expériences parentes gratuites : la locale PostgreSQL `C` de la fixture conservait l'ellipse Unicode dans le lexème du mot concerné, rendant fausse la conjonction de recherche. Le changement contrôlé de locale retrouvait le passage dans les trois scopes, sans modification du moteur de recherche ni élargissement en OR. Voir `artifacts/luna-live-20261025/postgres-unicode-root-cause.md`, relu après sa rédaction. Ces expériences n'ont pas été exécutées par l'auteur du présent correctif.

Le présent lot ne corrige pas cette locale : il traite les contraintes de requête, la continuité et la qualité du retour d'observation. Le raccourcissement et la séparation de l'identifiant ne doivent donc pas être présentés comme la résolution unique du déficit réel. Un zéro dense ne certifie ni disponibilité de l'index vectoriel ni couverture sémantique exhaustive.

## Tests préparés et état de validation

Nouveau fichier `backend/SAAIA.Backend.Tests/OpenAiCompatibleAdvancedAnalysisProviderTests.ResearchFeedback.cs` : 16 cas.

| Cas | Vérification causale ou témoin |
| --- | --- |
| 2 variantes sans/avec critique | Première reformulation vide, deuxième utile, réponse sourcée ; réserve de synthèse/critique et plafond existant respectés. |
| 2 plafonds réduits | Aucun emprunt de la réserve pour prolonger indéfiniment une recherche vide, aucune augmentation de configuration. |
| Batch identique | Pas de seconde exécution ni de boucle de Review consommant le solde. |
| Budget documentaire épuisé | Pas d'appel Review payant inutilisable après le dernier appel outil. |
| Document nommé, plan initial | Le gateway déterministe ne trouve la preuve que si le texte compact et `documentHint` sont réellement séparés ; aucune recherche mécanique redondante. |
| Document nommé, suivi | Même séparation dans une reformulation, identité requise conservée. |
| 3 états de résolution | `resolved`, `ambiguous` et `no_match` sont distingués dans le vrai prompt suivant, avec dégradation et récupération, sans fabriquer de handle. |
| Retour natif | Même projection compacte après une véritable séquence de fonctions simulée, sans candidat/snippet ni fausse evidence. |
| Résolution sans preuve | Un handle inventé pour une lecture canonique est rejeté avant tout appel outil. |
| Métadonnées nombreuses | Bornes de projection et absence de snippets/copies de requêtes brutes vérifiées. |
| 2 chemins de résolution surdimensionnés | Omission explicite dans les retours Review et natif, sans fragment de chemin ni modification de scope. |

Les tests existants du fichier principal sont adaptés au contrat documentaire réel : le faux gateway documentaire utilise désormais `DocumentHint` plutôt que des chiffres obligatoires dans la query ; les assertions exigent les deux contenus/documents et les requêtes ciblées, tout en conservant le filtrage des documents voisins et les preuves de réponse. Le cas sans preuve couvre FR/EN × document nommé/non nommé : le texte doit parler d'une recherche infructueuse, sans affirmer l'absence documentaire. Le test ancien d'une reformulation reçoit un second Review `ready`, attend quatre appels et vérifie que ce tour voit les résultats de la reformulation.

Root a exécuté une première capture RED de 11 cas avant correction du provider : compilation réussie, 8 échecs et 3 témoins verts. Un de ces huit échecs provenait d'une erreur de fixture du nouveau test natif : absence du paramètre obligatoire `sourceKey` vide. Cette fixture est corrigée ensuite ; son premier échec ne constitue pas une preuve causale de défaut produit. Les trois autres cas ont été ajoutés après cette capture initiale.

Une première suite Unit complète communiquée par root après les corrections a exécuté 2 479 tests : 2 478 PASS, un échec, zéro skip. L'échec était l'ancien scénario simulé de reformulation qui envoyait directement une réponse de Writer au lieu du second Review désormais légitime ; sa fixture et ses assertions sont corrigées ensuite, sans rétablir l'arrêt prématuré du produit.

À l'écriture de cette mise à jour, le provider et ses tests sont de nouveau gelés pour la suite finale orchestrée par root. L'auteur de cette correction n'a lancé aucun `.NET`, service, réseau ni modèle. La confirmation GREEN finale et son périmètre doivent être ajoutés uniquement à partir des résultats réels de root. Aucun nouvel essai payant ni validation sémantique réelle n'est revendiqué ici.
