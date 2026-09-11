# Reprise — writer structure directement depuis l'EvidenceBundle

Date du checkpoint : 2026-08-02 23:50 Europe/Zurich

## Motif de l'arrêt

L'utilisateur a demandé un arrêt propre. Aucun nouveau chantier ne doit être lancé depuis ce checkpoint. Le Goal global reste actif et n'est pas déclaré terminé.

## Résultat principal validé

Le principal blocage du planning de repas n'était pas l'absence de bonnes sources, mais la sélection structurée imposée à Qwen3. Les sorties de tool call fondées sur de longues listes ou des tableaux parallèles provoquent un biais positionnel important avec Qwen3-4B : préfixes sélectionnés, motifs cycliques ou zones artificielles.

Une nouvelle variante laisse le LLM composer directement la grille finale à partir d'un EvidenceBundle candidat, sans étape préalable de sélection d'EvidenceId par enum. Le writer reçoit les axes, les frontières sémantiques et les preuves canoniques, puis produit le tableau final avec une citation par cellule. Le code contrôle uniquement cardinalité, identifiants, unicité des sources et forme structurée.

Cette variante a réussi trois runs live consécutifs sur le corpus contrôlé de 28 recettes :

- 5 lignes x 4 colonnes ;
- 20 recettes nommées ;
- 20 EvidenceId distincts et existants ;
- aucune recette dupliquée ;
- aucune recette principale dans les colonnes petit-déjeuner ou collation selon l'oracle du test ;
- environ 47 à 57 secondes par génération ;
- environ 1 229 tokens de prompt et 303 à 307 tokens de sortie.

Artefacts :

- `artifacts/live-source-backed-semantic-granularity-20260802-233814/report.txt`
- `artifacts/live-source-backed-semantic-granularity-20260802-233916/report.txt`
- `artifacts/live-source-backed-semantic-granularity-20260802-234010/report.txt`

## Variantes explicitement rejetées

### Affectation globale multi-classe

Contrat : un entier par candidat, -1 ou index de colonne.

Résultat : Qwen a renvoyé le motif `0,1,2,3` répété sans jugement sémantique. Échec en environ 25 secondes avec `structured_role_allocation_capacity_invalid`.

Artefact : `artifacts/live-source-backed-semantic-granularity-20260802-231924/report.txt`.

Le fichier de production expérimental a été supprimé.

### Classification binaire par rôle

Contrat : un booléen par candidat pour une seule frontière sémantique.

Résultat aller : 26 décisions plausibles sur 28, mais faux positifs graves `Paella mixte` et `Risotto aux champignons` pour le petit-déjeuner.

Artefact : `artifacts/live-source-backed-semantic-granularity-20260802-232610/report.txt`.

### Consensus ordre normal / ordre inversé

Résultat : la Paella a été éliminée, mais le croisement a créé une zone de position artificielle, conservé le Risotto et rejeté le Porridge. Cette approche ne neutralise pas le biais ; elle croise deux biais.

Artefact : `artifacts/live-source-backed-semantic-granularity-20260802-233245/report.txt`.

Le fichier de production expérimental a été supprimé.

### Plan de découverte par rôle

Qwen a parfois choisi `role_oriented`, parfois `shared_inventory`, et a pu revenir vers un inventaire partagé après lecture des résultats. Le test a montré que ce choix méta ne suffit pas à garantir la couverture sémantique finale.

Artefact principal : `artifacts/live-source-backed-semantic-granularity-20260802-231023/report.txt`.

Le fichier de production expérimental a été supprimé.

## Intégration de production effectuée

### Nouveau writer structure

Fichier :

- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentStructuredCandidateWriter.cs`

Responsabilités :

- transmettre au LLM la demande, les lignes, les colonnes, les frontières sémantiques et le vivier candidat ;
- laisser le LLM choisir, affecter et rédiger directement le tableau ;
- accepter une instruction de réparation mécanique ;
- valider mécaniquement le nombre de citations, leur unicité, leur appartenance au vivier et l'unicité de source visible ;
- activer ce chemin uniquement pour une grille large dépassant `MaximumFlatStructuredSelectionItems`.

### Runner

Fichier :

- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/SourceBackedAgentV2Runner.cs`

Modifications :

- remplacement du sélecteur par colonne par le writer structuré direct lors du tour de décision final ;
- extraction et validation des EvidenceId cités par le writer ;
- validation de la forme demandée via `SourceContractVerifier` ;
- boucle de réparation LLM en cas d'erreur mécanique ;
- trace `source_backed_agent_v2.structured_candidate_writer.completed` ;
- conservation du LLM comme seul décideur sémantique ;
- marge mécanique de candidats pour les grilles multi-colonnes : cible de vivier égale au besoin final plus au maximum huit candidats, bornée par `MaximumWorkingEvidenceItems`. Pour un planning 20 cases / 4 colonnes, la cible devient 28 candidats avant rédaction.

### Ancien sélecteur

Le fichier `SourceBackedAgentStructuredColumnSelection.cs` a été supprimé. Il effectuait quatre tool calls successifs, souffrait du biais d'ordre et produisait environ 18 affectations correctes sur 20 malgré des rôles renforcés.

## Validation au moment de l'arrêt

Après l'intégration :

- build WinUI : réussi, 0 avertissement, 0 erreur ;
- build `SAAIA.Client.ToolAgent.Tests` : réussi, 0 avertissement, 0 erreur ;
- `git diff --check` : aucune erreur, uniquement les avertissements de conversion CRLF/LF déjà présents ;
- aucune exécution live longue du pipeline de production intégré n'a été lancée après la demande d'arrêt.

Le microtest writer lui-même a été validé 3/3 avant son branchement dans le Runner. L'intégration compilée reste donc à valider end-to-end.

## Nettoyage et contrat déterministe terminés à la reprise suivante

Les trois méthodes live qui appelaient par réflexion les expériences rejetées ont été supprimées, ainsi que leur fixture d'observation devenue inutile. Une recherche ciblée ne trouve plus aucune référence à `PlanRoleDiscovery`, `ClassifyCandidatesForColumnRole`, `AllocateCandidatesAcrossColumnRoles` ou `CompleteStructuredColumnSelection`.

Le validateur mécanique du writer est maintenant couvert par cinq tests déterministes dans :

- `client/SAAIA.Client.ToolAgent.Tests/SourceBackedStructuredCandidateWriterTests.cs`

Couverture : succès avec vingt citations distinctes, cardinalité insuffisante, citation répétée, EvidenceId inconnu/non proposé et deux EvidenceId résolus vers la même source visible.

Validation après ce nettoyage :

- build WinUI : 0 avertissement, 0 erreur ;
- build des tests : 0 avertissement, 0 erreur ;
- tests ciblés : 5 réussis sur 5 en 25 ms ;
- résultat TRX : `client/SAAIA.Client.ToolAgent.Tests/TestResults/structured-candidate-writer-deterministic.trx`.

## Ordre exact de reprise recommandé

1. Relancer une fois le microtest `Live_qwen3_writes_the_structured_plan_directly_from_candidates_when_enabled` avec sa nouvelle signature pour confirmer que le nettoyage n'a rien changé.
2. Ajouter le dernier cas déterministe de structure visible manquante au niveau du `SourceContractVerifier`, si ce cas n'est pas déjà couvert par sa suite dédiée.
3. Lancer le pipeline source-backed v2 complet sur la question réelle du planning, sans gérer le processus LLM local.
4. Inspecter les traces : taille du vivier approuvé, nombre de tours/outils, audit candidat, événement du writer structuré, vérification source et source cards finales.
5. Si le run complet échoue avant le writer, corriger la collecte/navigation. S'il atteint le writer avec 28 candidats mais échoue, utiliser les erreurs mécaniques et les candidats exacts pour un microtest reproductible avant toute modification.
6. Exiger trois runs live complets consécutifs puis un parcours WinUI réel avant de déclarer le planning validé.

## Environnement live à conserver

- URL : `http://127.0.0.1:1234/v1`
- modèle : `Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf`
- `SAAIA_VALIDATION_MANAGE_LOCAL_LLM_PROCESS=0`
- serveur `llama-server` laissé actif au checkpoint.

## Git

- branche : `SAAIA_V3.1`
- commit de base observé au début de la reprise : `5f35881c`
- worktree volontairement très sale, contenant l'ensemble du chantier source-backed/ingestion antérieur ;
- aucun commit créé pendant cette tranche ;
- ne pas reset ni supprimer globalement les fichiers non suivis.

## Telegram

Rapport horaire envoyé à 23 h 30 en deux parties, 3 867 caractères, depuis :

- `artifacts/telegram-report-20260802-2330.txt`

Les accents ont été écrits en UTF-8 ; aucun encodage par substitution `@` n'a été utilisé.
