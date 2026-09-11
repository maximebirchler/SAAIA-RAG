# Rapport des douze dernières heures de travail

Période de référence : du 19 juillet 2026 à 23 h 27 au 20 juillet 2026 à 11 h 27, heure de Zurich.

## Résumé exécutif

Le pipeline RAG a fortement progressé sur son chemin le plus difficile : produire un planning de repas du lundi au vendredi, avec petit-déjeuner, dîner, collation et souper, à partir de preuves courantes et avec des cartes de sources visibles.

Le chemin technique complet a été atteint à deux reprises. Le live 47 a produit vingt cellules et deux cartes; le live 50 a passé le test automatisé avec vingt cellules et trois cartes. Je ne considère pourtant aucun de ces deux lives comme une réussite fonctionnelle. Les réponses contenaient encore des fragments ou des instructions de recette à la place de vrais repas. Le constat central est donc le suivant : le transport, les contrats structurels et les sources progressent nettement, mais la qualité sémantique finale n'est pas encore au niveau professionnel demandé.

Le principal correctif ajouté après le live 50 est un micro-audit LLM focalisé sur la nature de chaque valeur distincte. Le LLM reste le décideur sémantique; le code ne reconnaît aucun repas et ne contient aucune heuristique métier. Le code se limite à présenter au LLM le champ, la valeur, les citations et les extraits, puis à vérifier mécaniquement que tous les candidats ont reçu une décision `complete_instance` ou `wrong_type`.

État des validations à la clôture du rapport : compilation réussie sans avertissement, 291/291 tests SourceBacked réussis, 371/371 tests combinés SourceBacked/OpenAI/ApiClient réussis et `git diff --check` propre. La prochaine preuve décisive reste le live 51.

## 1. Réparation du contrat d'adéquation et live 45

Le travail a d'abord porté sur un cas où le juge LLM choisissait `revise` sans fournir de `revisedAnswer`. Le pipeline reconnaît maintenant les contrats d'action incomplets pour les décisions `accept`, `revise` et `need_more_evidence`. Il redemande une opinion complète au LLM au lieu de convertir la décision par code.

Le live 45 a duré environ 19 min 26 s. Il a reconstruit une intake de 5 lignes par 4 colonnes, exécuté quatre recherches RAG, constitué un premier bundle de seize preuves, lu quatre contextes documentaires, puis constitué un second bundle de trente-cinq preuves. Le writer structuré a produit vingt cellules et cinq EvidenceIds visibles. Le vérificateur mécanique a déclaré la structure valide.

Le juge d'adéquation a cependant choisi `need_more_evidence` sans fournir de suivi exécutable. Le nouveau repair a bien été déclenché, mais sa première sortie est restée incomplète. Le pipeline a terminé avec un arrêt sûr, sans exposer le tableau intermédiaire et sans cartes de sources. Le live 45 a donc validé l'emplacement du repair, mais pas sa capacité à conclure.

## 2. Retry focalisé du contrat d'adéquation

Une seconde relance LLM a été ajoutée lorsque le premier repair reste mécaniquement incomplet. Cette relance reçoit les codes de rejet, le claim map, l'intake et les preuves autorisées, mais pas la sortie brute précédente. Le LLM peut encore choisir librement entre trois issues : accepter avec un audit complet, réviser avec une réponse complète et citée, ou demander de nouvelles preuves par des appels exécutables.

Le code ne synthétise aucune décision et n'invente aucun suivi. Les traces distinguent désormais la première réparation et son retry. Les tests négatifs prouvent qu'une seconde sortie incomplète reste refusée.

## 3. Live 46 et calibration du Planner

Le live 46 a échoué en amont après environ 5 min 15 s. Le routeur a terminé, mais le Planner local n'a produit qu'environ 1,49 token par seconde. Le client l'a annulé à l'ancienne limite de 210 secondes après environ 255 tokens, avant de recevoir un JSON exploitable.

La borne exacte du Planner a été portée à 360 secondes, tout en conservant son budget de sortie à 480 tokens. Cette modification ne donne aucun pouvoir sémantique au code : elle évite seulement d'annuler une décision LLM encore en cours de génération sur le runtime local lent.

## 4. Live 47 : premier chemin complet, mais contenu insuffisant

Le live 47 a duré environ 21 min 55 s. Le Planner a terminé en 264 secondes, ce qui a confirmé la nécessité de sa nouvelle borne. L'intake 5 x 4 a été récupérée, les recherches et une recherche supplémentaire décidée par le LLM ont constitué un bundle final de vingt-neuf preuves, puis le writer a produit vingt cellules avec les citations E1 et E23.

Le pipeline a exposé une réponse source-backed et deux cartes de sources. Le test a d'abord rencontré une assertion trop stricte sur `Diner` alors que la réponse utilisait correctement `Dîner`; cette assertion a été corrigée.

Le contenu restait néanmoins impropre : une instruction fragmentaire comme « Remplir chacun des moules avec » était utilisée comme collation et « Courgette déjeuner » comme souper. Plus grave, le retry d'adéquation a accepté vingt claims tout en conservant une requête de suivi. Le contrat autorisait donc une combinaison contradictoire `accept + followUpRequests`.

Cette incohérence a été supprimée. Une décision terminale `accept` ne peut plus conserver de suivi ou de révision; `revise` ne peut pas conserver de suivi; `need_more_evidence` ne peut pas contenir une révision. Il s'agit d'invariants de contrat génériques, pas de règles sur les repas.

## 5. Lives 48 et 49 : revue d'intake trop volumineuse

Le live 48 a atteint le Planner, mais `PlannerIntakeReview` a été annulé à 240 secondes après environ 347 tokens. Sa borne a été portée à 360 secondes sans augmenter son budget de sortie.

Le live 49 a montré que cette hausse ne suffisait pas : la revue est restée active jusqu'à 360,2 secondes et n'avait produit qu'environ 300 tokens sans objet JSON complet. Les réparations suivantes ont reproduit d'anciennes formes de dictionnaires et demandé une clarification injustifiée, que le pipeline a correctement refusées.

La conclusion a été de ne pas augmenter encore aveuglément les délais. Le contrat monolithique demandait trop de responsabilités simultanées au modèle local. Il a été remplacé en priorité par deux micro-décisions LLM : une adjudication de l'axe des lignes, puis une adjudication de l'axe des colonnes. Le code assemble leurs contrats typés et applique uniquement des vérifications mécaniques.

## 6. Live 50 : progrès d'orchestration, échec sémantique explicite

Le live 50 est le résultat le plus instructif de la période. Le test automatisé a passé ses assertions après 28 min 49 s. Le Planner a identifié la grille à deux axes. Le nouveau chemin direct a évité la revue monolithique initiale. L'adjudication des lignes a accepté Lundi à Vendredi en 82,5 secondes.

La première adjudication des colonnes a été coupée à 150,1 secondes après environ 166 tokens. Les réparations ont dérivé vers des contraintes de présentation, puis le repli a finalement récupéré les quatre colonnes Petit-déjeuner, Dîner, Souper et Collation. Cette mesure a conduit à accorder 240 secondes à la seule première adjudication des colonnes; les repairs restent limités à 150 secondes.

Quatre recherches RAG ont produit 1, 10, 2 et 4 résultats. Le premier bundle contenait seize preuves. Le LLM a demandé quatre lectures documentaires, exécutées mécaniquement sur trois PDF de cuisine. Le second bundle contenait trente-six preuves.

Le writer a produit un premier brouillon auquel manquaient deux cellules du vendredi. Le vérificateur structurel l'a refusé. Une réparation de cellules a produit un tableau complet de vingt cellules. Le juge d'adéquation a d'abord demandé davantage de preuves, puis son retry a accepté les vingt claims sans suivi résiduel. Le pipeline a donc exposé trois cartes de sources.

Malgré ce passage technique, la réponse répétait chaque jour :

- petit-déjeuner : « Céréalière 1 portion de légumes 1 boisson chaude »;
- dîner : « Crème de pois et avocat »;
- souper : « Mélanger tous les ingrédients à l'aide d'un fouet »;
- collation : « Mélanger tous les ingrédients ».

Les deux dernières valeurs sont des instructions, pas des soupers ou des collations. La première ressemble à une consigne nutritionnelle fragmentaire. Le live 50 est donc classé comme échec fonctionnel, même si le test automatisé était vert. Cette distinction est essentielle : vingt cellules, des EvidenceIds et des cartes ne suffisent pas à produire une réponse professionnelle.

## 7. Nouveau micro-audit LLM des valeurs

Un palier dédié `StructuredValueTypeFitJudge` a été ajouté avant le juge d'adéquation global pour les tableaux d'au moins vingt cellules.

Les principaux fichiers concernés sont :

- `SourceBackedPipelineContracts.cs` pour les contrats typés;
- `SourceBackedStructuredValueTypeFitPrompt.cs` pour le prompt focalisé;
- `SourceBackedRagJson.StructuredValueTypeFit.cs` pour le parseur;
- `SourceBackedRagPipeline.StructuredValueTypeFit.cs` pour l'orchestration;
- `SourceBackedRagPipeline.AnswerAdequacy.cs` pour le branchement avant l'adéquation globale;
- `SourceBackedLlmOutputBudget.cs` et `SourceBackedLlmStepTimeoutPolicy.cs` pour les bornes de transport;
- `SourceBackedRagPipelineTests.cs`, `SourceBackedRagArchitectureTests.cs` et `SourceBackedRagTestDoubles.cs` pour les régressions.

Les vingt cellules du live 50 se réduisent à quatre candidats distincts après déduplication par colonne, valeur et preuves. Le LLM juge donc quatre éléments focalisés au lieu de refaire un audit global de vingt claims avec une décision d'action terminale.

Une régression reproduit exactement le mauvais tableau. Le LLM de test accepte « Crème de pois et avocat » et classe les trois autres candidats comme `wrong_type`. Le pipeline refuse alors la réponse avec `llm_answer_adequacy_failed`. Le code ne transforme jamais `wrong_type` en acceptation.

## 8. Mémoire et frontière des preuves

La mémoire a conservé son rôle prévu : contexte d'orchestration uniquement. Les traces des lives confirment `context_only_never_final_proof`. Aucun souvenir n'est promu en EvidenceId et aucune carte n'est créée depuis la mémoire. Les citations visibles proviennent exclusivement de l'EvidenceBundle courant.

Ce point est important pour les réponses simples comme pour les plans complexes : la mémoire peut aider le LLM à comprendre la continuité de la conversation, mais elle ne remplace jamais une preuve documentaire courante.

## 9. Validation technique finale

- compilation du projet de tests : 0 erreur, 0 avertissement;
- suite SourceBacked : 291 réussites sur 291;
- suite combinée SourceBacked/OpenAI/ApiClient : 371 réussites sur 371;
- contrôle `git diff --check` : aucune erreur;
- nouveaux tests ciblés : refus du tableau exact du live 50, passage positif d'un tableau de vingt cellules, politique de timeout et garde de modularité.

Une première relance globale avait affiché 281/291, car les anciennes fixtures consommaient leur réponse suivante lors du nouveau micro-audit. Le double de test a été corrigé pour reconnaître une réponse de type-fit explicite sans neutraliser les tests dédiés. Après recompilation, les 291 tests puis les 371 tests combinés sont tous passés.

## 10. État Git et dépôt

- branche : `SAAIA_V3.1`;
- commit courant : `bba84e8d3d38d541f8f432f39f3795f84eea6bb8`;
- aucun commit ni staging supplémentaire n'a été réalisé pendant cette période;
- le dépôt reste très chargé en modifications et fichiers non suivis hérités des travaux précédents;
- les changements de cette période sont documentés dans `TODO-2026-07-16-RAG-LLM-ORCHESTRATION.md`;
- aucun nettoyage destructif n'a été effectué et aucune modification existante de l'utilisateur n'a été écrasée.

Un commit global maintenant serait risqué, car il mélangerait les changements SourceBacked cohérents avec un très grand ensemble de fichiers historiques non suivis ou modifiés. Le nettoyage et les commits doivent être préparés par lots vérifiés et intentionnels.

## 11. Prochaine étape recommandée

La prochaine étape est le live 51 canonique, sans annoncer de réussite avant son analyse sémantique manuelle.

Le live 51 doit démontrer :

1. que l'adjudication initiale des colonnes dispose effectivement de sa borne de 240 secondes;
2. que la grille 5 x 4 est reconstruite sans dérive vers une clarification;
3. que les recherches et lectures produisent des preuves adaptées aux quatre types de repas;
4. que `StructuredValueTypeFitJudge` est atteint;
5. que des instructions comme « Mélanger tous les ingrédients » sont refusées comme `wrong_type`;
6. que la récupération de preuves ou la réécriture remplace uniquement les valeurs rejetées;
7. que le tableau final contient vingt valeurs complètes, des citations exactes et des cartes utiles;
8. que la réponse est réellement lisible et professionnelle, pas seulement mécaniquement conforme.

Après un live 51 concluant, il faudra encore exécuter un scénario RAG simple fondé sur la base de connaissances, vérifier le rendu WinUI des cartes, puis préparer un nettoyage et des commits séparés par responsabilité.

## Verdict final de la période

Le socle automatisé est vert et le pipeline est beaucoup plus observable, contractuel et fidèle au principe « LLM orchestrateur principal ». Le chemin vers vingt cellules et des sources visibles est désormais reproductible. En revanche, la réponse parfaite demandée n'est pas encore obtenue en conditions réelles. Le dernier live a échoué sur la qualité sémantique, et le nouveau micro-audit n'a pas encore été éprouvé contre le modèle local dans Live51.
