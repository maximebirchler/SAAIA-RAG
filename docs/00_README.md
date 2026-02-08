# SAAIA — Documentation (CDC v2.7)

Ce dossier est la **source de vérité** pour le produit et l’implémentation.
Il est conçu pour être **lisible par un humain** et **exploitable par un agent IA (GitHub Copilot / VS Agent)**.

## À lire dans cet ordre (agent IA)
1) **CDC** : [`docs/CDC_v2.7.md`](./CDC_v2.7.md)
2) **Plan d’action** (référence) : [`docs/Plan_action_v2.7.md`](./Plan_action_v2.7.md)
3) **État d’avancement** (à mettre à jour à chaque commit) : [`docs/Plan_action_v2.7_status.md`](./Plan_action_v2.7_status.md)
4) **Architecture** : [`docs/ARCHITECTURE.md`](./ARCHITECTURE.md)
5) **Contrats API** : dossier [`docs/04_CONTRACTS/`](./04_CONTRACTS/)
6) **Modèle de données** : dossier [`docs/05_DATA/`](./05_DATA/)
7) **Runbooks** : dossier [`docs/06_RUNBOOKS/`](./06_RUNBOOKS/)
8) **Tests** : dossier [`docs/07_TESTS/`](./07_TESTS/)
9) **Décisions** (ADR) : dossier [`docs/08_DECISIONS/ADR/`](./08_DECISIONS/ADR/)

## Règle de travail (commit par commit)
Chaque commit DOIT :
- viser **un seul jalon** (ou une sous-partie clairement identifiée),
- inclure **les commandes de test**,
- mettre à jour **au minimum** :
  - `docs/Plan_action_v2.7_status.md` (fait/partiel/à faire),
  - `docs/CHANGELOG.md` (résumé de ce qui a changé).

## Glossaire rapide
- **RAG** : Retrieval-Augmented Generation (recherche + synthèse)
- **TEI** : Text Embeddings Inference (embeddings, ex: HuggingFace)
- **Qdrant** : base vectorielle (stockage des chunks)
- **Chat store** : historique conversationnel (serveur en v2.7)
- **user_id** : GUID stable généré côté client (séparation des historiques)
