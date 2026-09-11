# Reprise — Source-backed V2, observabilité et sélection de contexte

Date du point d’arrêt : 2026-07-28 22:45 Europe/Zurich  
Dépôt : `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG`  
Branche : `SAAIA_V3.1`  
HEAD : `5f35881c`  
Écart vérifié avec `origin/SAAIA_V3.1` : 15 commits locaux d’avance, 0 commit distant d’avance.

## Pourquoi le travail s’arrête ici

L’utilisateur a demandé un arrêt propre. Aucun nouveau live long n’a été lancé
après cette demande. Aucun processus `dotnet test` ou
`tools/run-live-model-e2e.ps1` n’est laissé actif.

Le prochain acte utile est un run live comparatif. Le binaire x64 contenant la
dernière correction est déjà reconstruit avec zéro avertissement et zéro erreur.

## État réellement validé

### Régression déterministe

Dernière suite ciblée :

- résultat : 222/222 tests réussis;
- artefact :
  `artifacts/test-results/source-backed-v2-recent-evidence-full-20260728/source-backed-v2-recent-evidence-full-20260728.trx`;
- périmètre : V2, client OpenAI natif, architecture source-backed, mémoire de
  conversation et contexte mémoire.

Tests spécifiques ajoutés et réussis :

- une boucle simple de recherches strictement identiques est bornée;
- le retrait des outils ne dure qu’un seul tour;
- après une revue `need_more_evidence`, le LLM retrouve le catalogue complet;
- une recherche récente n’est plus masquée par un ancien inventaire de cartes.

### Build x64

Dernier build :

```text
dotnet build client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj
  -p:Platform=x64 -p:Configuration=Debug --no-restore
```

Résultat : zéro avertissement, zéro erreur.

### Live réel v4

Artefacts :

- `artifacts/live-v2-default-simple-20260728-v4/`;
- trace concentrée :
  `artifacts/live-v2-default-simple-20260728-v4/trace-focused.log`;
- réponse brute de la recherche backend :
  `artifacts/live-v2-default-simple-20260728-v4/backend-search-response.json`.

Question :

> Je veux un dessert au chocolat facile, tu proposes quoi ?

Le test technique est passé en 4 min 43 s, mais la réponse fonctionnelle reste
inacceptable : le pipeline a terminé par une insuffisance prudente au lieu de
proposer une recette.

Il ne faut donc pas présenter ce live comme une réussite produit.

## Diagnostic démontré par les nouvelles balises

Les outils backend sont rapides :

- inventaire de 40 cartes : environ 165 à 207 ms;
- recherches RAG : environ 0,8 à 1,0 s.

La latence vient surtout des appels LLM successifs. Les balises
`[LLM_NATIVE ...]` enregistrent désormais :

- `trace_id` et `call_id`;
- type d’appel;
- messages et caractères;
- outils exposés;
- jetons exacts;
- motif de fin;
- nombre d’appels outils;
- durée;
- annulation ou erreur.

Le run v4 a aussi démontré une erreur de sélection du contexte :

1. `documents.content_cards` a fourni 40 cartes générales Cuisine;
2. `rag.search` a ensuite trouvé six passages réellement utiles;
3. l’ancienne compaction priorisait toujours les
   `canonical_content_card`, même anciennes;
4. les résultats récents pouvaient donc être chassés du contexte du rédacteur.

La réponse backend vérifiée contient pourtant de bons résultats :

- Éclairs au chocolat, p. 34;
- Muffins banane-chocolat, p. 60;
- mousse avocat-chocolat, p. 123;
- Gâteau au chocolat, p. 67;
- Gâteau chocolat-courgette, p. 23.

## Dernière correction appliquée

La préférence sémantique codée en dur pour les cartes canoniques a été retirée.
La compaction applique maintenant une règle mécanique :

- pour une même source, conserver la preuve observée le plus récemment;
- parmi les sources, conserver en premier les preuves les plus récemment
  observées;
- ne pas décider dans le code qu’un type de preuve est sémantiquement meilleur.

Le LLM récupère ainsi les résultats de la dernière action et reste responsable
de leur pertinence.

Les balises ont aussi été enrichies avec :

- `new_evidence_count`;
- `new_evidence_ids`;
- `new_evidence_sources`;
- `cited_evidence_ids` à la sortie du rédacteur;
- `cited_evidence_ids` après vérification mécanique.

Ces champs ne contiennent ni prompt complet, ni clé, ni secret.

## Prochain run exact

Ne pas reconstruire avant ce run : le dernier binaire x64 est frais.

Relancer le même scénario dans un nouveau répertoire, par exemple :

```text
artifacts/live-v2-default-simple-20260728-v5
```

Paramètres :

- Qwen3-4B-Instruct-2507 Q5_K_M;
- llama.cpp local `http://127.0.0.1:1234/v1`;
- backend réel du serveur;
- profil `canonical-direct`;
- activation V2 par défaut;
- filtre de question `dessert au chocolat facile`;
- timeout réel 8 minutes;
- `-SkipBuild -Platform x64 -Configuration Debug`.

La clé API doit être lue depuis `infra/.env.server-linux` sans jamais être
affichée ou copiée dans un artefact.

## Critères pour analyser le prochain live

Le run v5 n’est une réussite que si :

1. une vraie proposition de dessert est rendue;
2. les citations visibles correspondent à un ou plusieurs résultats chocolat;
3. les cartes source contiennent fichier, page et identité stable;
4. le juge sémantique accepte la réponse;
5. le terminal n’est pas `insufficient_evidence`;
6. les nouvelles balises montrent les EvidenceId récents dans le contexte et
   dans la réponse;
7. la latence et le nombre d’appels LLM sont comptés honnêtement.

Un simple test xUnit vert ne suffit pas.

## Défauts encore ouverts

### Priorité 1 — qualité simple

- Vérifier en live que la correction de récence produit enfin une proposition
  chocolatée correctement sourcée.
- Si Qwen répète encore une recherche après `need_more_evidence`, remplacer le
  tour libre actuel par un petit contrat de décision LLM structuré :
  continuer avec une action différente, rédiger, ou conclure à l’insuffisance.
  Le choix doit rester au LLM.

### Priorité 2 — latence

- Éliminer les appels redondants entre routeur, plan sémantique, décision
  d’outil, rédacteur et juge.
- Réutiliser une première action explicitement produite par le LLM au lieu de
  lui redemander immédiatement une décision équivalente.
- Mesurer chaque variante par qualité et temps, sans optimiser uniquement pour
  le petit modèle.

### Priorité 3 — contexte matériel

- llama.cpp expose une fenêtre réelle de 8 192 jetons;
- le client annonce encore 4 096 jetons dans le run V2;
- synchroniser la capacité active avec le runtime réellement lancé;
- comparer 4K/8K avant de retenir la valeur par profil matériel.

### Priorité 4 — observabilité complète

Étendre les mêmes balises corrélées aux blocs de contexte :

- politique;
- demande;
- mémoire de session;
- mémoire projet;
- mémoire corpus;
- outils;
- preuves;
- retours de vérification.

Pour chaque bloc, enregistrer taille, jetons, éléments retenus/écartés et motif
mécanique, sans journaliser de contenu sensible.

### Après stabilisation du cœur

- implémenter `M2 Project Memory`;
- permettre le déplacement d’une discussion existante dans ou hors d’un projet;
- auditer les six mémoires;
- découper et supprimer le pipeline V1 et les cadavres;
- réduire `RagEndpoints.cs`;
- exécuter les validations simples, documents nommés, mémoire, plan de repas,
  autres catégories et WinUI réel;
- nettoyer puis produire des commits cohérents.

## État Git et prudence

Le worktree contient un très grand chantier intentionnel, avec de nombreux
fichiers modifiés et non suivis. Il n’a pas été réinitialisé et aucun fichier
utilisateur n’a été écrasé.

`git diff --check` ne signale pas d’erreur de patch; il affiche uniquement des
avertissements de normalisation CRLF/LF déjà présents dans le chantier.

Aucun commit n’a été créé à ce point d’arrêt : la correction la plus récente est
testée automatiquement mais son live comparatif v5 n’a pas encore été exécuté.
