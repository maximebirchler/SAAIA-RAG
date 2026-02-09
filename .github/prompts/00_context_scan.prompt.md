# Prompt — 00 Context scan (OBLIGATOIRE avant de coder) — STRICT

Tu es un agent IA dans Visual Studio sur le repo **SAAIA RAG On-Prem** (CDC v2.7).

## Objectif
Avant tout code, produire une compréhension *opérationnelle* du jalon, des non-négociables et des fichiers à toucher.

## Étape 1 — Lire obligatoirement (dans cet ordre)
1) `docs/00_README.md`
2) `docs/CDC_v2.7.md`
3) `docs/Plan_action_v2.7.md`
4) `docs/Plan_action_v2.7_status.md`
5) `docs/ARCHITECTURE.md`
6) `docs/04_CONTRACTS/README.md`
7) `docs/04_CONTRACTS/ERROR_MODEL.md`
8) `docs/07_TESTS/SMOKE_TESTS.md`

## Étape 2 — Non négociables à rappeler (liste courte)
- Serveur = **NO LLM**
- Erreurs = JSON `{ error, requestId }` + header `X-Request-Id`
- **Ne jamais supprimer/renommer** un doc de référence sans instruction explicite (ex: `docs/CHANGELOG.md`, `docs/04_CONTRACTS/*`)
- Un commit = un jalon (ou sous-jalon explicitement nommé)

## Étape 3 — Résultat attendu (format obligatoire)
Répondre en 4 sections :

### (1) Résumé en 10–15 lignes
- non négociables
- jalon ciblé + DoD
- risques de divergence vs CDC

### (2) Fichiers probables à modifier
Liste précise (paths) + justification.

### (3) Tests attendus
Commande(s) exactes + endpoints à vérifier.

### (4) “STOP conditions”
Liste les cas où tu dois t’arrêter et demander : ex
- besoin de supprimer/renommer un fichier doc existant
- besoin de modifier le schéma DB sans migration
- besoin de créer des fichiers au root
