# Reprise du 2 août 2026 - Source-backed v2 et relations sémantiques des axes

## Point d'arrêt

- Date du constat : 2026-08-02 00:05, Europe/Zurich.
- Branche : `SAAIA_V3.1`.
- Commit de base : `5f35881c`.
- Aucun test, processus `dotnet`, `vstest`, `testhost` ou script live lié au chantier n'est encore actif.
- Le serveur local Qwen3 sur le port 1234 reste volontairement actif. Il n'exécute pas de test et n'a pas été arrêté comme s'il s'agissait d'un processus bloqué.
- Aucun commit n'a été créé à ce point : la dernière modification de stratégie sémantique n'a pas encore passé la suite complète et le run live métier reste insuffisant.

## Résultats acquis et vérifiés

### Navigation vers une preuve mécanique

La navigation documentaire renvoie maintenant les pointeurs mécaniques nécessaires pour rejoindre une preuve : révision, empreinte de source, entrée de navigation, chunk cible et ancre cible. Ces éléments traversent l'`EvidenceBundle`, tout en restant marqués `navigation_map` et `orientation_only`; ils ne peuvent donc pas être présentés comme preuve finale avant résolution par `documents.context`.

Preuves live :

- `artifacts/live-document-navigation-20260801-v231-target-pointers.txt` : 200 entrées sur 200 possèdent les pointeurs attendus.
- `artifacts/live-document-navigation-20260801-v231-exact-recipe-context.txt` : l'entrée « Boeuf bourguignon » rejoint le chunk cible `88433710-97f0-3fed-94d4-3518bdb1a789`, puis `documents.context` restitue la préparation et les informations de cuisson à la page 5.

### Tests déterministes

- `client/SAAIA.Client.ToolAgent.Tests/TestResults/source-backed-navigation-target-pointer-v2.trx` : 1137 tests exécutés, 1137 réussis, le 2026-08-01 à 23:18.
- `client/SAAIA.Client.ToolAgent.Tests/TestResults/source-discovery-query-targeted.trx` : 115 tests ciblés exécutés, 115 réussis, le 2026-08-01 à 23:45.
- Après les toutes dernières modifications de stratégie, le lot combiné exécute encore 115 tests déterministes avec succès; son seul échec est le microtest live décrit ci-dessous. La suite complète de 1137 tests n'a pas encore été relancée après ces dernières modifications.

### Stratégie de candidats améliorée

Le contrat de stratégie ne demande plus de valeur hypothétique d'une cellule. Cette valeur contaminait la recherche et transformait un exemple inventé, tel qu'une omelette, en exigence documentaire. Le LLM reçoit maintenant le plan sémantique déjà interprété et doit fournir :

- le type d'objet source d'une position;
- une règle d'éligibilité observable dans la source;
- une requête générique de découverte de ces objets;
- le périmètre documentaire et la relation du vivier de candidats.

Le dernier microtest live produit désormais :

- objet candidat : `recette`;
- règle : titre correspondant à un nom complet de plat et observable comme titre ou section;
- requête de découverte : `recette de plat nomme`;
- vivier : `shared_pool` sur la catégorie Cuisine.

Cette partie est nettement meilleure que le run v231, mais pas encore suffisante pour valider le pipeline.

## Échec live restant

Artefact : `artifacts/live-source-backed-semantic-granularity-20260802-000044/report.txt`.

Le modèle renvoie :

- axe des jours : `placement_only`, attendu et correct;
- axe des moments de repas : `placement_only`, incorrect;
- résultat attendu pour ce second axe : `source_discriminator`, car l'adéquation sémantique d'une recette change selon le rôle demandé à la colonne, même si la source ne contient pas mot pour mot le libellé de la colonne.

Le prompt contient déjà une définition générique détaillée du test d'échange. Continuer à lui ajouter des phrases serait probablement un nouveau pansement. La prochaine reprise doit d'abord remettre en cause la forme du contrat et la charge cognitive de cet appel, par exemple en évaluant chaque axe séparément ou en fournissant à l'arbitrage d'axe l'intégralité structurée des intentions du plan plutôt qu'une seule ligne de preuves atomiques. La décision doit rester celle du LLM; le code ne doit pas coder en dur le sens des jours, repas, colonnes ou domaines.

## Run complet v231 : diagnostic à conserver

Le run `artifacts/live-v2-weekly-meal-20260801-v231-target-navigation-run1b` a échoué honnêtement après environ 10 minutes 28 secondes, sans produire le lundi complet. Le plan 5 x 4 était compris, mais la recherche initiale a demandé une navigation large sans requête, puis des cartes abstraites de type « repas quotidien » ou « planning hebdomadaire ». Elle n'a appelé ni `documents.context` ni `rag.search`, n'a validé que 5 candidats sur 20 et a accepté plusieurs titres de planification au lieu de vraies recettes. La cause observée est la sémantique de découverte et de qualification des candidats, pas la capacité mécanique à rejoindre un fichier, une page ou un chunk.

## État Git vérifié au point d'arrêt

- 422 entrées dans le worktree : 64 fichiers suivis modifiés, 8 supprimés et 350 non suivis.
- Diff suivi : 8358 lignes ajoutées et 86440 supprimées, les suppressions provenant notamment de la décomposition des anciens monolithes et tests remplacés.
- `git diff --check` retourne 0. Des avertissements de conversion CRLF/LF existent, sans erreur d'espace blanc.
- Cet état contient un chantier accumulé important. Ne pas réinitialiser, supprimer ou committer aveuglément le worktree.

## Ordre exact de reprise

1. Repenser le petit contrat LLM de relation des axes au lieu d'empiler une nouvelle heuristique ou davantage de prose.
2. Faire passer le microtest live avec une distinction générique : ligne de placement et colonne discriminante, sans vocabulaire métier codé en dur.
3. Relancer les tests ciblés, puis la suite déterministe complète.
4. Recompiler le client x64 et lancer une nouvelle version live v232.
5. Vérifier que la première recherche découvre de vraies instances, que navigation et cartes conduisent à `documents.context`, et que seules des preuves résolues alimentent le rédacteur.
6. Une fois le premier planning correct et suffisamment rapide, exécuter trois runs consécutifs et poursuivre les validations mémoire, WinUI, latence, nettoyage et commits prévues par le Goal.

Le Goal global reste actif et non atteint.

## Mise à jour de reprise - arrêt demandé à 00:19

Le contrat d'axe a été simplifié après le premier point d'arrêt. L'appel ne redemande plus la granularité, le type d'objet et la règle d'éligibilité déjà décidés par la stratégie de candidats. Il demande désormais uniquement deux décisions sémantiques booléennes et deux justifications courtes. Le code convertit mécaniquement ces décisions en `placement_only` ou `source_discriminator`, et les justifications sont ajoutées aux balises de trace.

Validation déterministe obtenue après cette refonte :

- `client/SAAIA.Client.ToolAgent.Tests/TestResults/axis-contract-targeted.trx` : 115 tests exécutés, 115 réussis.

Premier microtest live du nouveau contrat :

- `client/SAAIA.Client.ToolAgent.Tests/TestResults/axis-semantic-boolean-contract-live.trx` : 1 test exécuté, 1 échec.
- `artifacts/live-source-backed-semantic-granularity-20260802-001535/report.txt` : Qwen3 classe maintenant correctement les moments de repas comme `source_discriminator`, mais classe aussi les jours comme `source_discriminator`.
- La justification brute révèle une invention : le modèle suppose qu'un plat doit être adapté à un jour précis en fonction d'habitudes ou d'un contexte alimentaire, alors que ni la demande ni le plan n'imposent cette distinction.
- La stratégie de candidats a aussi ajouté à tort la présence d'une préparation ou d'ingrédients à sa règle d'éligibilité, alors que la preuve atomique demandée est seulement un nom de plat.

Deux ajustements génériques ont ensuite été appliqués :

- la règle d'éligibilité doit désormais exiger uniquement l'identité et les champs explicitement demandés par `PREUVES_ATOMIQUES` et `ACCEPTER_SI`, sans confondre une identité complète avec tous les détails internes de l'objet;
- la décision d'axe doit distinguer les coordonnées de date ou d'occurrence des libellés qui nomment directement des rôles ou types de contenu, et ne peut pas inventer une habitude ou une contrainte absente de la demande.

Le second microtest live `axis-semantic-explicit-request-live` a été interrompu à la demande de l'utilisateur pendant sa compilation. Son arbre de processus `dotnet/testhost` a été arrêté explicitement. Il n'a produit ni TRX ni artefact et ne constitue donc aucune preuve. Les deux derniers ajustements de prompt n'ont pas encore été validés par une compilation terminée.

Au point d'arrêt de 00:19 :

- aucun processus de test RAG n'est actif;
- le serveur Qwen3 reste disponible et n'exécute aucun run;
- `git diff --check` retourne toujours 0;
- aucun commit n'a été créé;
- la première action de reprise est de compiler puis de relancer uniquement le microtest live des axes, avant toute suite complète ou nouveau planning.
