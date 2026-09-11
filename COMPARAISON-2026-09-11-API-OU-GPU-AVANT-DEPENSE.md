# A763 — Comparaison API ou GPU avant dépense et transmission

Date d'observation : 2026-09-11  
Statut : `PRESENTED_NO_SPEND_NO_EXTERNAL_CORPUS_TRANSFER`

## Décision recommandée

La première validation fonctionnelle de la capacité avancée devrait utiliser
deux API sur un corpus public ou synthétique : GPT-6 Astra pour mesurer le
plafond de qualité disponible et Mistral Medium 3.5 sur l'endpoint régional EU
pour mesurer un candidat européen moins coûteux. La location d'un H100 OVHcloud
avec un modèle ouvert doit venir ensuite, si l'API valide le code et si le besoin
de confidentialité, de volume ou de maîtrise opérationnelle justifie ce coût.

Ce séquencement sépare trois questions qui ne doivent pas être confondues :

1. le code d'orchestration et les contrats SAAIA savent-ils réussir une demande
   complexe ?
2. quel modèle atteint réellement la qualité requise ?
3. quelle infrastructure donne le bon coût, la bonne latence et la bonne
   politique de données en production ?

## Comparaison concrète au 11 septembre 2026

| Option | Modèle de départ | Contexte publié | Prix publié | Données et exploitation | Usage proposé |
|---|---|---:|---:|---|---|
| API OpenAI, plafond qualité | GPT-6 Astra | 1 050 000 tokens, sortie 128 000 | 10 $/M en entrée, 50 $/M en sortie | API non utilisée pour entraîner les modèles sauf opt-in; journaux d'abus jusqu'à 30 jours par défaut; ZDR et résidence EU soumis aux conditions d'éligibilité | Référence haute de l'évaluation fonctionnelle |
| API OpenAI, compromis | GPT-5.6 Sol | 1 050 000 tokens, sortie 128 000 | 4 $/M en entrée, 20 $/M en sortie | mêmes contrôles de plateforme; endpoint EU possible pour les modèles éligibles, avec approbations requises | Repli si Astra apporte trop peu de gain pour son coût |
| API Mistral EU | Mistral Medium 3.5 | 256 000 tokens | 1,50 $/M en entrée, 7,50 $/M en sortie; régional EU à 1,1 fois le tarif | calcul régional EU/EFTA; ZDR disponible sur plans payants et appels stateless éligibles; Agents, Files et Conversations sont exclus du ZDR | Candidat européen coût/confidentialité |
| GPU européen loué | 1 H100 80 Go OVHcloud + vLLM, candidat Qwen3-32B quantifié | Qwen3-32B : 32 768 natifs, 131 072 avec YaRN | 2,80 € HT/h ou 1 940 € HT/mois pour l'instance H100 publiée | corpus, modèle, traces et stockage restent dans l'infrastructure louée que SAAIA administre; mises à jour, durcissement, disponibilité et extinction sont à notre charge | Validation d'auto-hébergement puis offre dédiée à volume élevé |

Sources officielles : [comparateur des modèles OpenAI](https://developers.openai.com/api/docs/models/compare),
[contrôles de données OpenAI](https://developers.openai.com/api/docs/guides/your-data),
[tarifs Mistral](https://docs.mistral.ai/inference/pricing),
[limites de contexte Mistral](https://docs.mistral.ai/resources/known-limitations),
[inférence régionale Mistral](https://docs.mistral.ai/inference/regional-inference),
[ZDR Mistral](https://docs.mistral.ai/admin/monitor-comply/zero-data-retention),
[tarifs GPU OVHcloud](https://www.ovhcloud.com/en-ie/public-cloud/prices/),
[fiche H100 OVHcloud](https://www.ovhcloud.com/en/public-cloud/gpu/h100/) et
[fiche officielle Qwen3-32B](https://huggingface.co/Qwen/Qwen3-32B).

Les désignations « plafond qualité » et « compromis » sont des rôles dans notre
protocole, pas des résultats SAAIA déjà mesurés. Aucun fournisseur ne publie une
garantie prouvant la réussite de notre planning de repas.

## Ordre de grandeur des coûts

Hypothèse de comparaison, à remplacer par la télémétrie réelle : un job avancé
consomme 100 000 tokens d'entrée cumulés et 8 000 tokens de sortie. Elle inclut
le prompt, les extraits de preuves et les reprises d'outils, mais pas un éventuel
outil payant du fournisseur.

| Option | Coût estimé par job avec cette hypothèse |
|---|---:|
| GPT-6 Astra | 1,40 $ |
| GPT-5.6 Sol | 0,56 $ |
| Mistral Medium 3.5 global | 0,21 $ |
| Mistral Medium 3.5 régional EU | 0,231 $ |
| H100 OVHcloud utilisé 10 minutes | 0,47 € HT, hors stockage et exploitation |
| H100 OVHcloud utilisé 30 minutes | 1,40 € HT, hors stockage et exploitation |

Le GPU devient économiquement intéressant uniquement lorsqu'il est bien occupé
ou lorsqu'il achète une exigence de maîtrise des données que l'API ne satisfait
pas. À faible volume, son temps d'administration et ses périodes d'inactivité
dominent le prix par réponse. À volume élevé, la comparaison doit intégrer le
nombre de jobs concurrents, les tokens par seconde réellement obtenus, les
redémarrages et la disponibilité des GPU.

## Données que le trajet SAAIA transmettrait

Le fournisseur avancé reçoit actuellement, par contrat interne :

- le texte de la demande utilisateur et la langue;
- la description structurée du livrable et la raison du basculement;
- les références de recherche et les résumés d'outils précédents;
- le contenu résolu des preuves nécessaires, avec leurs identités canoniques;
- les identifiants techniques du job, du tenant et de l'utilisateur dans
  l'objet interne remis au fournisseur.

Un adaptateur externe ne devra pas transmettre les identifiants tenant,
utilisateur ou chemins locaux lorsqu'ils ne servent pas l'inférence. Il devra
remplacer ces valeurs par des identifiants opaques et envoyer seulement les
extraits de preuves utiles. La sortie devra rester le texte, les claims et les
`evidenceId`; le backend SAAIA continuera de valider et de reconstruire les
sources canoniques.

Pour l'évaluation initiale, aucune donnée du corpus privé ne doit sortir. Le jeu
de test doit contenir des documents synthétiques reproduisant la structure et
la charge du planning 5 × 4, sans recettes, noms, chemins ni métadonnées issus du
corpus réel.

## Protocole proposé après autorisation

1. Figer un holdout synthétique complexe avec réponses et preuves attendues.
2. Implémenter un adaptateur stateless commun et deux configurations de modèle,
   sans utiliser les services Files, Agents ou mémoire du fournisseur.
3. Exécuter d'abord GPT-6 Astra et Mistral Medium 3.5 EU sur les mêmes jobs, avec
   budgets identiques et trois répétitions.
4. Mesurer exactitude des 20 éléments, couverture, diversité, citations,
   latence, tokens, erreurs et coût réel; conserver les réponses brutes dans les
   artefacts d'évaluation autorisés.
5. Ne louer le H100 que si la chaîne est fonctionnellement validée; y déployer
   vLLM et un modèle ouvert candidat, puis rejouer exactement le même holdout.

## Point d'arrêt

La comparaison exigée avant dépense est maintenant présentée. Aucun compte
fournisseur n'a été créé, aucune clé n'a été lue ou configurée, aucun GPU n'a
été loué, aucun appel payant n'a été effectué et aucun contenu du corpus n'a été
transmis. L'étape suivante exige le choix du budget d'évaluation et l'autorisation
explicite d'utiliser un fournisseur sur le seul corpus synthétique figé.
