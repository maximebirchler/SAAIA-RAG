# Extraction du pipeline interactif — 24 octobre 2026

## Structure obtenue

`RetrievalEngine.SearchInteractiveAsync` coordonne désormais un plan immuable, les recherches initiales, la sélection initiale, trente étapes ordonnées et la projection de la réponse. Les étapes sont des classes de responsabilité sous `Retrieval/Interactive/`, avec des paramètres typés ; il ne s'agit pas d'un découpage du même corps en fichiers `partial`.

Le contexte de ressources, les décisions de routage, les règles de sélection, les termes de récupération et les chronométrages ont des types distincts. L'état mutable de la sélection contient les résultats, leur index de déduplication et les trois ensembles de candidats nécessaires aux étapes suivantes. Les calculs et décisions restent dans les étapes qui les possèdent.

## Préservation du comportement

- Copie originale conservée dans `RetrievalEngine.before.cs.txt`.
- Extraction guidée par les symboles Roslyn, les entrées/sorties des blocs et des bornes de responsabilités explicites dans `config.json`.
- `statement-coverage.json` attribue les **211 instructions racines et les 64 instructions de la branche broad**, soit **275 instructions originales**, sans instruction non attribuée (`missing=[]`).
- Ce contrôle d'appartenance ne prouve pas, à lui seul, l'équivalence sémantique. Les suites Unit et IntegrationPg doivent valider la régression du produit reconstruit.
- Le contrôle a détecté une instruction omise à la frontière entre deux étapes : `PrioritizeExactTitleSelections`. Elle a été rétablie dans `InteractiveInitialSelectionCleanup`, immédiatement avant `PrioritizeQuotedTitleSelections`, puis le contrôle a été relancé avec succès.
- La branche `shortCircuitAfterQuotedTitle` a été retirée après preuve que sa variable locale était initialisée à `false` et jamais affectée ensuite. Les deux autres raccourcis restent représentés par une route initiale explicite.
- Les listes de titres datés, codes et opérandes sont calculées plus tôt à partir de la même requête immuable. Les règles, paramètres et ordre des sélections et récupérations sont préservés.
- La portée du chronomètre TotalMs historique est conservée ; il ne mesure pas toute la latence de l'interface cliente.

## Changements intentionnels d'observabilité et d'annulation

- Un contrôle d'annulation intervient à chaque frontière d'étape.
- Les diagnostics de métadonnées sont créés même sans demande de retour HTTP des diagnostics ; le contenu reste réservé à la demande explicite hors mode `advanced_analysis`.
- Les recherches désactivées passent par `MeasureOptionalAsync` et sont déclarées `skipped` avec une raison, plutôt que présentées comme une recherche réussie ayant retourné zéro résultat.
- Chaque étape expose les tailles de sélection avant/après, un résultat terminal, et distingue une sélection inchangée d'une sélection modifiée. La comparaison comprend l'ordre et les valeurs des résultats, pas seulement leurs identifiants.
- Une erreur conserve les compteurs partiels et provoque la fin de sonde dans `finally` ; elle reste propagée au traitement appelant.
- La projection journalise des identifiants et compteurs bornés, sans corps de source, chemin ou titre. Les budgets du collecteur commun déclarent les omissions.
- Les ajouts de candidats passent par le collecteur commun `AddRankedMatchesDiagnosed` pour tracer les motifs de rejet.

## Validation intermédiaire réellement effectuée

- Compilation du backend et des tests après extraction : réussie.
- Suite Unit intermédiaire avant les derniers ajustements de sondes et la restauration ci-dessus : **2 403 réussites et une erreur**, aucune ignorée. L'erreur concernait un ancien contrat d'architecture canonique relu séparément par l'agent validation ; cette exécution n'est pas présentée comme une validation finale verte.
- Trois tests comportementaux ciblés du nouveau pipeline : **3/3 réussis**, aucune omission (`pipeline-unit/interactive-pipeline-unit.trx`). Ils vérifient le plan d'une requête bornée en pages/diversité, la détection d'un réordonnancement, et les compteurs terminaux avec sélection partielle lors d'une exception.
- La compilation finale intermédiaire `build-final.log` a réussi ; ses trois avertissements xUnit ont ensuite été corrigés avec les assertions dédiées appropriées.
- Les imports inutilisés de Retrieval ont été supprimés après identification sémantique CS8019, avec vérification du contenu concurrent avant écriture (`unused-usings.json`). Le formatage d'indentation a été limité à `Retrieval/`.
- Après la dernière restauration, les sources de ce lot sont gelées pour la reconstruction Release puis les suites Unit et PostgreSQL isolées complètes pilotées par l'agent principal. Leurs résultats finaux doivent être lus dans les preuves de cet agent ; ils ne sont pas anticipés ici.

## Limites et outillage

Aucun appel fournisseur payant, aucune connexion au serveur de production, aucune modification du corpus installé. Cette extraction et les tests synthétiques ne constituent pas une approbation fonctionnelle du planning de repas, ni du produit installé. Le statut `TESTE_NON_APPROUVE` doit rester explicite tant que les preuves d'acceptation ne sont pas réunies.

`generated/` contient les candidats intermédiaires de l'extraction, antérieurs à des corrections et au formatage : **ne pas les recopier dans le produit**. `Coverage.cs.txt` permet de reproduire le contrôle de propriété AST à partir de l'original et de `config.json`. `Extractor.cs.txt` documente le procédé initial ; la version produit est celle de `backend/SAAIA.Backend/Retrieval/Interactive/`.
