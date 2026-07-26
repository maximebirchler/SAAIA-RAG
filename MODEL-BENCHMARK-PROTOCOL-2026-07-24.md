# Protocole de comparaison équitable des petits LLM locaux

Date : 2026-07-24
Statut : clos, décision appliquée
Machine de référence : Windows, 32 Go de RAM, NVIDIA Quadro P520 4 Go, Intel UHD à mémoire partagée
Runtime de référence : `llama.cpp` CUDA, build `b10098`

## Décision finale

- Modèle retenu : `Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf`.
- Variante de repli gouvernée : Qwen3 4B Instruct 2507 Q4_K_M.
- Runtime serveur qualifié : llama.cpp CUDA `b10098`, révision
  `0278d8362d78c5de291bc03b76016f7f74b2ab77`.
- Les modèles non retenus et Qwen2.5 ont été supprimés du disque, du catalogue,
  des profils, des exemples, des politiques de compatibilité et des
  configurations actives.
- Les sorties brutes de campagnes ont été purgées après conservation de la
  méthode et des conclusions utiles dans les documents de travail.

## Méthode de décision

Le meilleur modèle ne sera pas choisi à partir du pipeline historique optimisé
pour compenser Qwen2.5-3B. Ce pipeline reste un témoin de rétrocompatibilité,
séparé du classement principal.

Le classement principal doit mesurer :

1. les capacités propres du modèle dans ses conditions recommandées ;
2. sa robustesse dans des conditions communes ;
3. sa capacité à orchestrer un RAG canonique minimal ;
4. la qualité factuelle et mécanique du résultat final ;
5. le coût réel sur la machine cible.

Un score agrégé unique ne doit pas masquer les compromis. Les résultats sont
publiés par axe, puis le choix final est expliqué à partir des exigences du
produit.

## Modèles candidats

- Qwen2.5-3B-Instruct Q4_K_M : témoin historique, pas favori présumé.
- Qwen3-4B-Instruct-2507 Q4_K_M et Q5_K_M.
- Ministral-3-3B-Instruct-2512 Q4_K_M et Q5_K_M.
- IBM Granite-4.1-3B Q4_K_M et Q5_K_M.
- Phi-4-mini-instruct Q4_K_M et Q5_K_M.

Les variantes Q5 ne sont conservées que si un gain de qualité reproductible
justifie leur surcoût en VRAM et en latence.

## Deux régimes obligatoires

### A. Régime natif

Le corpus et la grille de notation restent identiques, mais chaque modèle reçoit :

- son chat template GGUF natif ;
- le rôle système réellement supporté par ce template ;
- les paramètres de génération recommandés par l'éditeur quand ils existent ;
- un budget de sortie suffisant pour terminer le contrat ;
- le profil matériel le plus rapide déjà qualifié pour ce modèle.

Réglages initiaux à vérifier :

| Modèle | Température | top_p | top_k | min_p | Motif |
|---|---:|---:|---:|---:|---|
| Qwen2.5-3B | 0,7 | 0,8 | 20 | 0 | configuration officielle de génération |
| Qwen3-4B-2507 | 0,7 | 0,8 | 20 | 0 | recommandation officielle Qwen |
| Ministral-3-3B | 0,15 | à confirmer | natif | natif | exemple officiel Mistral |
| Granite-4.1-3B | 0,0 | 1,0 | natif | natif | génération déterministe tant qu'IBM ne recommande pas autre chose |
| Phi-4-mini | 0,0 | 1,0 | natif | natif | exemple officiel Microsoft |

Les valeurs « natif » doivent être capturées depuis `/props` ou les métadonnées
du modèle, jamais supposées silencieusement.

### B. Régime commun

Tous les modèles reçoivent exactement :

- le même prompt ;
- le même schéma ;
- la même température déterministe ;
- le même budget de sortie suffisant ;
- les mêmes graines ;
- le même corpus et la même notation.

Ce régime mesure la robustesse d'intégration, pas le potentiel maximal.

## Trois surfaces de sortie

Chaque capacité importante est testée sous trois formes quand cela a du sens :

1. JSON seulement demandé dans le prompt, sans grammaire ;
2. JSON contraint par schéma, comme dans le produit ;
3. appel d'outil natif avec le parser recommandé par le modèle/runtime.

La grammaire JSON ne doit pas être comptée comme une preuve d'instruction
following. Elle sert à mesurer la qualité sémantique dans une sortie exploitable.

## Corpus de capacités intrinsèques

Le corpus fixe doit couvrir au minimum :

- question RAG simple avec une seule preuve exacte ;
- conflit de révisions documentaires ;
- sélection de preuve et conservation exacte fichier/page ;
- distracteurs sémantiques et sources proches mais fausses ;
- décision d'orchestration simple sans outils inutiles ;
- décision d'orchestration complexe avec mémoire, recherche, planification et writer ;
- mémoire pertinente, mémoire obsolète et mémoire sans rapport ;
- génération de requêtes de recherche par facette ;
- plan 5 jours × 4 repas à partir d'un pool de preuves contrôlé ;
- insuffisance réelle de preuves et clarification légitime ;
- cas français, anglais et au moins une reformulation naturelle.

## Répétitions et notation

- Minimum : trois graines par scénario stochastique.
- Les prompts et réponses brutes sont conservés.
- Toute sortie coupée par `finish_reason=length` est classée « budget invalide »
  avant d'être classée comme erreur du modèle.
- La notation déterministe vérifie les valeurs fermées, identifiants, fichiers,
  pages, couverture, doublons et violations explicites.
- La qualité rédactionnelle ouverte est évaluée en aveugle, sans nom de modèle.
- La moyenne, la médiane, le pire cas et le taux de réussite complet sont publiés.

## Mesures séparées

### Qualité

- compréhension de la demande ;
- décision sémantique ;
- sélection de preuves ;
- fidélité des citations ;
- respect des contraintes ;
- complétude ;
- refus ou clarification appropriés ;
- qualité du français ;
- stabilité entre répétitions.

### Performance

- temps de chargement ;
- prompt tokens/s ;
- génération tokens/s ;
- TTFT ;
- durée totale par scénario ;
- VRAM dédiée, RAM partagée et RAM système ;
- température maximale observée ;
- erreurs, timeouts et sorties tronquées.

### Intégration

- chat template correctement détecté ;
- rôle système ;
- JSON demandé ;
- JSON contraint ;
- tool calling ;
- longueur de contexte réellement tenable ;
- compatibilité `llama.cpp` ;
- sensibilité Q4/Q5.

## Pipeline RAG de classement

Le pipeline commun minimal suit :

`question -> intake/planner LLM -> outils -> EvidenceBundle -> reranker -> Evidence Judge LLM -> writer LLM -> vérificateur mécanique -> réparation LLM`

Le code peut vérifier :

- schéma et outils valides ;
- identifiants de preuve existants ;
- fichier/page issus des résultats ;
- citations absentes ou inventées ;
- doublons exacts ;
- budgets et absence de progrès mécanique.

Le code ne doit pas réécrire sémantiquement les requêtes approuvées, choisir les
recettes, éliminer une preuve jugée pertinente ou reconstruire la réponse.

## Ancien pipeline

Le pipeline compensatoire Qwen2.5 est exécuté après le classement principal.
Son résultat répond seulement à la question : « ce modèle reste-t-il compatible
avec les contraintes historiques ? »

Un échec dans ce pipeline ne peut pas faire baisser le classement intrinsèque.
Le live Qwen3 du 2026-07-24 en est le témoin : le modèle a produit de bonnes
requêtes par repas, ensuite remplacées par les anciens audits par des recherches
génériques, avant une clarification erronée.

## Critère de promotion

Le modèle promu doit :

- être au moins aussi fiable que Qwen2.5 sur les réponses simples ;
- être clairement meilleur sur l'orchestration et les demandes complexes ;
- préserver les preuves mécaniques jusqu'à l'UI ;
- tenir sur 4 Go de VRAM dédiée avec un profil stable ;
- avoir une latence acceptable ou permettre une réduction nette du nombre
  d'appels LLM ;
- réussir plusieurs répétitions sans dépendre d'une graine chanceuse.

Le choix final peut retenir deux profils si les données le justifient :

- un modèle rapide pour les tâches simples ;
- un modèle orchestrateur pour les demandes complexes.
