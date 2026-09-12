# ADR A763 — Affinité durable fournisseur/modèle des analyses avancées

Date : 2026-09-12  
Statut : retenu, testé, produit toujours `TESTE_NON_APPROUVE`

## Problème

Un job d'analyse avancée peut survivre à l'arrêt du client, du worker ou du
backend. Avant le commit `3658d9cf`, chaque nouvelle prise de bail réécrivait
`provider_key` avec la configuration courante. Après expiration d'un bail et
redémarrage, un job commencé avec OpenAI pouvait donc reprendre avec RunPod ou
un serveur client. Les événements de recherche du premier essai auraient alors
été réutilisés dans une exécution conduite par un autre modèle.

Ce comportement rendait les comparaisons causales invalides et constituait un
fallback implicite. Il contredisait également la règle produit : un job avancé
emploie un seul fournisseur et un seul modèle, même s'il nécessite plusieurs
appels Planner, Writer et réparation bornée.

## Décision

La première prise de bail lie durablement le job au couple :

```text
provider_key + provider_model
```

La migration `067_advanced_analysis_provider_affinity.sql` ajoute
`provider_model`. Les reprises ne peuvent sélectionner que les jobs dont le
couple est vide ou identique à la configuration active. Après récupération d'un
bail expiré, un couple différent termine le job avec :

```text
provider_configuration_changed
```

Le nouveau fournisseur n'est pas appelé, l'identité d'origine n'est pas
écrasée et aucun résultat partiel n'est publié. WinUI explique que la demande
doit être relancée avec la configuration actuelle et masque tout payload non
validé. Le couple lié est conservé dans les métadonnées du message, y compris
en cas d'échec.

Le commit `4cbca2d5` ferme aussi une ambiguïté d'identité : l'identifiant du
modèle utilisé pour l'appel HTTP et celui persisté pour l'affinité sont
désormais strictement identiques. Une valeur dépassant la capacité SQL de 256
caractères est rejetée avant tout appel externe avec
`advanced_llm_model_invalid`; elle n'est jamais tronquée pour la comparaison.

Le commit `d960cdb8` applique la même règle au fournisseur. Une `ProviderKey`
personnalisée dépassant 100 caractères reste visible telle quelle pour
l'affinité puis est rejetée avec `advanced_llm_provider_key_invalid` avant tout
appel HTTP. Deux clés distinctes ne peuvent donc plus devenir identiques par
troncature silencieuse.

Cette règle porte sur le job avancé. Dans l'architecture hybride demandée, le
petit modèle local peut décider que la capacité avancée est nécessaire et
créer le handoff. Une fois ce handoff pris en charge par le backend, Planner,
Writer et éventuelle réparation utilisent le même fournisseur et le même
modèle. Le passage local vers avancé est une décision de capacité explicite ;
il ne constitue pas un fallback après échec du fournisseur.

## Compatibilité et limites

Un ancien job déjà lié à un `provider_key` différent échoue proprement à sa
prochaine récupération. Un ancien job dont `provider_model` est encore nul est
lié au modèle actif lors de sa première reprise post-migration. Les jobs jamais
commencés restent liés lors de leur première prise de bail.

Le changement ne choisit aucun fournisseur, ne modifie aucun tarif et
n'autorise aucun transfert externe. Les profils `openai-dev`, `runpod-bench` et
`customer-server` continuent à employer le même contrat OpenAI-compatible.

## Preuves

- commits produit : `3658d9cf74839e8b226a841bf734645dbb82ee8c`,
  `4cbca2d5fb28f1550d927699cbcd118b44c040b8` puis
  `d960cdb84376f5c32348911a0574251506827375` ;
- test PostgreSQL réel : trois tests réussis, zéro échec, base temporaire
  supprimée ;
- scénario causal : `openai-dev/terra-v1` puis
  `customer-server/qwen-v2`, statut `failed`, code
  `provider_configuration_changed`, zéro appel du fournisseur de remplacement ;
- contrôle positif : reprise avec le même fournisseur et le même modèle
  conservée ;
- validation Release complète : 4 391 réussites, zéro échec, deux sondes live
  opt-in non exécutées ;
- validation backend après durcissement de l'identité : 2 147 réussites, zéro
  échec, une sonde live opt-in non exécutée ;
- validation backend après suppression de la troncature fournisseur : 2 151
  réussites, zéro échec, une sonde live opt-in non exécutée ;
- protocole HTTP loopback : trois appels attendus, vingt claims, vingt preuves,
  verdict `PASS_BOUNDED_PROTOCOL_REPAIR_LIVE_LOOPBACK` ;
- ports 1234, 5123 et 18081 libres après exécution ;
- aucun secret dans les fichiers modifiés.

Artefacts :

- `artifacts/reprise-pc-20260908/a763-provider-affinity-3658d9cf-20260912` ;
- `artifacts/reprise-pc-20260908/a763-local-validation-provider-affinity-20260912` ;
- `artifacts/reprise-pc-20260908/a763-backend-validation-model-identity-20260912` ;
- `artifacts/reprise-pc-20260908/a763-exact-provider-identity-d960cdb-20260912` ;
- `artifacts/reprise-pc-20260908/a763-local-protocol-repair-4cbca2d5-20260912`.

## Matrice d'acceptation de l'architecture hybride

| Porte | État | Preuve ou limite |
|---|---|---|
| Petit modèle exécuté sur le poste client | Validé mécaniquement | Provider local et runtime qualifié déjà exercés sur cette machine |
| Décision locale simple / clarification / insuffisant / avancé | Validée mécaniquement, qualité bornée par A755 | Frontière et handoff typés ; le verdict sémantique global reste non approuvé |
| Fournisseur avancé configurable | Validé | `openai-dev`, `runpod-bench`, `customer-server`, ou `disabled` |
| Même logique RAG pour OpenAI, RunPod et serveur client | Validé mécaniquement | Même provider OpenAI-compatible, mêmes outils, mêmes claims et validations |
| Un fournisseur et un modèle par job avancé | Validé causalement | Commits `3658d9cf`, `4cbca2d5` et `d960cdb8`, identités exactes, migration 067, test PostgreSQL réel |
| Aucun fallback implicite après échec ou changement de config | Validé | Échec fermé, code explicite, zéro appel au fournisseur de remplacement |
| Politique de transfert externe | Validée mécaniquement | Contenu et métadonnées externes exigent les deux autorisations serveur |
| Secrets hors configuration et artefacts | Validé sur l'état courant | Résolution par référence protégée et scans sans fuite |
| Structured output et citations | Validé mécaniquement | Désérialisation, validation, refus et réparation unique bornée |
| Streaming du chemin local | Validé | `ILlmProvider.StreamAsync` normalise les chunks jusqu'à l'UI |
| Progression du chemin avancé durable | Validée | Révisions, événements d'outils, polling, persistance et reprise WinUI |
| Streaming token par token du Writer avancé | Ouvert comme choix UX | Le JSON Writer n'est publié qu'après validation complète ; l'UI reçoit la progression puis la réponse atomique validée |
| Qualité Terra après correction de l'ancrage des cellules | À éprouver | Banque gelée 3/3 bloquée par le palier Free 50 RPD |
| Qualité RunPod Qwen 32B | Non exécutée | Endpoint et runner prêts ; crédits, clé et autorisation RunPod absents |
| Modèle final sur serveur client | Non exécutable actuellement | Matériel et modèle final non disponibles |
| Configuration par licence dans les installateurs | Hors mission actuelle, architecture compatible | Les capacités et paramètres devront être matérialisés lors du chantier installateur |

Le streaming token par token de la réponse avancée ne doit pas exposer un JSON
incomplet ou un texte avant validation de ses citations. Une évolution pourra
diffuser des sections déjà validées ou un flux distinct du contrat final. Elle
ne doit pas affaiblir la publication atomique actuellement sûre.
