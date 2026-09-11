# Plan gelé de validation Terra — capacité avancée A755

Date de gel : 11 septembre 2026
Statut produit : **TESTE_NON_APPROUVE**

Ce plan est écrit avant le premier appel OpenAI payant. Terra sert de baseline
de développement pour isoler la qualité maximale du pipeline Router → Tools/RAG
SAAIA → Writer/Critic. Il ne constitue ni la cible de production, ni une vérité
factuelle : le corpus et les preuves canoniques restent l'oracle.

## État de départ

- aucun coût Terra n'est encore inscrit dans le journal local ;
- le fournisseur Local a passé sa sonde réelle structured/tool/streaming ;
- la régression A755 locale a reproduit exactement les 42 sorties qualifiées ;
- le fournisseur avancé traverse désormais le pipeline au lieu de produire le
  terminal local `advanced_analysis_required` ;
- le corpus et le backend SAAIA restent inchangés et en lecture seule pendant
  les campagnes LLM.

## Budget et ordre d'exécution

Le budget autorisé est 25 USD. Le coupe-circuit local refuse les nouvelles
réservations à 24 USD, alerte à 20 USD, limite un tour à 0,50 USD et à 32 appels.
Le journal global n'est pas recréé entre les campagnes.

1. Exécuter la sonde provider Terra de trois appels : JSON Schema, tool call
   natif, streaming avec usage.
2. Vérifier l'identité `openai / gpt-5.6-terra`, les trois métriques, le coût et
   l'absence de secret dans l'artefact.
3. Exécuter une seule fois `A755-ADV-01-meal-grid-5x4`.
4. Examiner Router, requêtes, EvidenceBundle, sélection, Writer, citations,
   latence, tokens et coût. Corriger seulement une cause générale prouvée.
5. Une fois le code gelé, répéter le planning trois fois consécutives.
6. Exécuter les trois autres cas avancés sur le même gel : synthèse de cinq
   repas, comparaison documentaire, extraction NIST à sept points.
7. Ouvrir ensuite un nouveau holdout aveugle, distinct des banques déjà vues.

Le lancement s'arrête immédiatement en cas d'erreur d'authentification, de
rate-limit persistant, de sortie structurée invalide, de mauvais fournisseur,
de dépassement budgétaire ou d'exposition d'un secret. Aucun fallback vers un
autre fournisseur n'est autorisé.

## Critères du planning 5 × 4

Une répétition est acceptée seulement si :

- elle contient exactement les cinq jours, du lundi au vendredi ;
- elle contient exactement les quatre moments demandés par jour ;
- elle propose vingt préparations concrètes et distinctes ;
- chaque cellule est reliée à une preuve ouvrable qui soutient réellement la
  préparation, sans citation décorative ;
- aucune recette, quantité, propriété ou adéquation n'est inventée ;
- aucune préférence optionnelle n'est demandée comme clarification bloquante ;
- les sources sont présentées sans duplication visuelle inutile ;
- une insuffisance, si elle se produit, nomme précisément les cellules ou les
  preuves manquantes au lieu de publier une grille partielle comme complète.

Trois répétitions consécutives doivent passer sur les mêmes hashes de code,
banque, configuration et corpus. Un test xUnit vert ne vaut que terminaison du
harnais. Le verdict sémantique est enregistré séparément après inspection de
chaque réponse et de chaque preuve.

## Critères transversaux

- le Router, le Writer et le Critic utilisent tous le même provider et modèle ;
- le fournisseur reçoit seulement les prompts nécessaires, les résultats des
  tools et les EvidenceBundles sélectionnés ;
- les métriques incluent rôle, latence, TTFT si disponible, tokens, cache,
  raisonnement, retries, coût, provider et modelId ;
- la banque connue sert à la calibration et à la régression ; le holdout reste
  fermé jusqu'au gel ;
- le statut produit reste `TESTE_NON_APPROUVE` jusqu'aux preuves avancées,
  RunPod, on-prem et aux autres gates du Goal.

## Résultats attendus après Terra

Si Terra réussit alors que le petit modèle local transfère la demande, la
frontière à deux capacités est confirmée et le pipeline devient la référence à
reproduire avec un modèle open-source. Si Terra échoue, la cause est cherchée
d'abord dans le retrieval, la construction des preuves, les prompts, les tools,
les contrats structurés ou les budgets. RunPod ne commence qu'après stabilisation
de cette baseline.
