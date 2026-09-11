# Reprise — audit sémantique groupé des candidats

Date du checkpoint : 2026-08-03 23:10 (Europe/Zurich)

## État exact

Le dernier test complet du planning de repas avait isolé le principal goulet d’étranglement avant le writer : l’ancien « audit par lots » exécutait en réalité plusieurs appels LLM séquentiels par candidat (choix du libellé, vérification, puis parfois arbitrage). Avec 40 candidats, cela provoquait des dizaines d’appels, une forte latence et une collection approuvée encore polluée.

La voie de production appelle maintenant `CompleteSemanticCandidateAuditBatchAsync` une seule fois par lot, avec une seule réparation de protocole autorisée. Les candidats sont découpés en vrais lots contigus et les lots peuvent être évalués en parallèle. Le contrat `submit_candidate_audit` demande un tableau strict `selectedLabelIndexes` : `0` rejette le candidat, `1..n` choisit l’un des libellés réellement proposés. Le code ne décide pas si un libellé est pertinent ; il vérifie seulement la longueur, les bornes, l’identité exacte et les doublons mécaniques.

Un audit qui reste invalide après sa réparation bornée ne rouvre plus le même lot aux tours suivants. Le pipeline termine ce chemin proprement afin d’éviter les boucles coûteuses.

Les preuves portant exactement le même libellé final sont dédupliquées mécaniquement avant le writer, en complément de la déduplication par source visible. Le LLM conserve le choix sémantique du libellé et de la source.

## Validation déterministe acquise

- Compilation de `RAG.sln` : succès, 0 erreur, 0 avertissement.
- Compilation du projet de tests ToolAgent : succès, 0 erreur, 0 avertissement.
- Tests ciblés de migration : 7/7 réussis.
- Classe complète `SourceBackedAgentV2Tests` : 123/123 réussis.
- Test de concurrence : 48 candidats deviennent deux appels LLM parallèles de 24, au lieu de 96 appels dans l’ancien chemin.
- Le contrat sait choisir un libellé parent sémantique : les fixtures valident `Tartiflette` à la place de `Pour 4 personnes` et `Gratin dauphinois` à la place de `Preparation`.
- `git diff --check` ciblé : aucune erreur.

Artefact déterministe principal :
`artifacts/test-results/source-backed-v2-batched-audit-v3/source-backed-v2-batched-audit-v3.trx`

## Mesures Qwen3 réelles

Modèle actif : `Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf`, contexte serveur confirmé à 8192 tokens.

### Un lot de 24, concurrence 1

- durée : 31,230 s ;
- appels LLM : 1 ;
- protocole : valide ;
- les 8 vraies recettes ont été repérées, mais Qwen3 a aussi accepté `Correction` et `La conservation` ;
- surtout, il a choisi les rubriques parentes `Desserts`, `Soupes` et `Plats` au lieu des titres précis des recettes.

Artefact : `artifacts/live-source-backed-semantic-granularity-20260803-230746/report.txt`

### Deux lots de 12, concurrence 2

- durée : 25,239 s ;
- appels LLM : 2 en parallèle ;
- protocole : valide ;
- les 8 vraies recettes ont cette fois reçu leur titre précis ;
- quatre faux positifs structurels subsistent : `PETITS DEJEUNERS`, `LES RECETTES` et deux ancres ramenées à `Exemple de menu`.

Artefact : `artifacts/live-source-backed-semantic-granularity-20260803-230909/report.txt`

## Conclusion honnête

Le gain de performance est réel et important : un audit synthétique de 24 candidats tient désormais autour de 25 à 31 secondes avec 1 ou 2 appels, au lieu de dizaines d’appels. En revanche, le contrat combiné « décider du type + choisir le bon libellé » n’est pas encore assez fiable pour alimenter le planning complet. Le test live est donc volontairement rouge sur la qualité, malgré un protocole valide.

La prochaine direction recommandée est un contre-audit LLM au niveau du lot : première sélection contextuelle, puis un seul contrôle indépendant des libellés provisoirement acceptés, lus comme des noms isolés. Cela reprend le principe robuste de l’ancien contrôle sans réintroduire deux ou trois appels par candidat. Après validation synthétique sur plusieurs rotations, il faudra supprimer l’ancien `SourceBackedAgentDirectCandidateAudit.cs` et ses tests devenus morts, puis relancer le planning complet.

## Commande de reprise immédiate

1. Concevoir le contrat du contre-audit groupé, générique et sans logique Cuisine.
2. Prouver sur les 24 candidats que seuls E17 à E24 sont retenus avec leurs titres exacts.
3. Tester plusieurs ordres et comparer lot 12/concurrence 2 avec lot 24/concurrence 1.
4. Supprimer le chemin direct par candidat et migrer les microprobes.
5. Relancer le parcours complet du planning de repas seulement après ces preuves.

